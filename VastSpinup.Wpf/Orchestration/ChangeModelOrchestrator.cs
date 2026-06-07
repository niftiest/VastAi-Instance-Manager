using VastSpinup.Models;
using VastSpinup.Services;

namespace VastSpinup.Wpf.Orchestration;

/// <summary>
/// Drives the unload → delete → pull → smoke-test pipeline that swaps the loaded model on
/// a live instance. Pure orchestration - UI updates flow through IOrchestratorHost.
/// </summary>
public class ChangeModelOrchestrator
{
    private readonly IOrchestratorHost _host;

    public ChangeModelOrchestrator(IOrchestratorHost host) => _host = host;

    public async Task RunAsync(VastInstance inst, OllamaApi ollama, string ollamaUrl,
        List<string> installed, ModelEntry newModel, CancellationToken ct)
    {
        long id = inst.Id;
        _host.AppendLog($"=== Change model on {id}: → {newModel.OllamaTag} ===");
        _host.SetProgress(0);

        await UnloadAndDeleteAsync(id, ollama, installed, ct);

        // Phase 2: Pull new
        _host.SetStatus($"Pulling {newModel.OllamaTag}...");
        _host.SetInstanceExtraStatus(id, $"CHANGE: Pulling {newModel.OllamaTag}...");
        _host.AppendLog($"Pulling {newModel.OllamaTag}...");
        await PullNewAsync(id, ollama, newModel, ct);
        _host.AppendLog($"Pull complete: {newModel.OllamaTag}");
        _host.SetProgress(95);

        // Phase 3: Smoke test + capability check
        _host.SetStatus("Smoke-testing new model...");
        _host.SetInstanceExtraStatus(id, "CHANGE: Smoke-testing new model...");
        var smokeErr = await ollama.SmokeTestAsync(newModel.OllamaTag, ct);
        var category = SmokeClassifier.Classify(smokeErr);
        if (category != SmokeClassifier.Category.Ok)
        {
            _host.AppendLog($"  ⚠ Smoke test FAILED: {smokeErr}");
            bool multimodal = category == SmokeClassifier.Category.MultimodalIncompatible;
            if (multimodal) _host.AddMultimodalIncompatible(id);
            _host.SetInstanceExtraStatus(id, multimodal
                ? "MULTIMODAL INCOMPATIBLE - pick a text-only model via Change Model."
                : "SMOKE TEST FAILED - try a different model.");
            _host.SetStatus("Switch finished but smoke test failed.");
            _host.ShowIncompatibleModelPopup(newModel, multimodal);
            _host.SetProgress(100);
            return;
        }
        _host.AppendLog("Smoke test OK.");

        await ReportCapabilitiesAsync(ollama, newModel, ct);

        _host.SetStatus("Model switched. Click 'Use' to wire opencode to this instance.");
        _host.SetInstanceExtraStatus(id, "READY - click 'Use' to wire up opencode.");
        _host.SetProgress(100);
        _host.NotifyInstanceReady(id,
            "Model switched",
            $"Instance {id} now has {newModel.OllamaTag} loaded.\n\nClick 'Use' to wire opencode to this instance.");
    }

    private async Task UnloadAndDeleteAsync(long id, OllamaApi ollama, List<string> installed, CancellationToken ct)
    {
        if (installed.Count > 0)
        {
            _host.SetInstanceExtraStatus(id, $"CHANGE: Unloading {installed.Count} model(s) from VRAM...");
            _host.SetStatus("Unloading old model(s) from VRAM...");
            foreach (var tag in installed)
            {
                ct.ThrowIfCancellationRequested();
                _host.AppendLog($"  Unloading {tag} (keep_alive=0)...");
                await ollama.UnloadModelAsync(tag, ct);
            }
            await Task.Delay(500, ct);
        }
        _host.SetInstanceExtraStatus(id, $"CHANGE: Deleting {installed.Count} model(s) to free disk...");
        _host.SetStatus("Deleting old model(s)...");
        for (int i = 0; i < installed.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tag = installed[i];
            try { _host.AppendLog($"  Deleting {tag}..."); await ollama.DeleteModelAsync(tag, ct); }
            catch (Exception ex) { _host.AppendLog($"  delete warn ({tag}): {ex.Message}"); }
            _host.SetProgress(((double)(i + 1) / installed.Count) * 20.0);
        }
        _host.SetProgress(20);
    }

    private async Task PullNewAsync(long id, OllamaApi ollama, ModelEntry newModel, CancellationToken ct)
    {
        string lastPhase = "";
        var progress = new Progress<(string status, long? total, long? completed)>(p =>
        {
            string label = p.status;
            if (p.total.HasValue && p.completed.HasValue && p.total > 0)
            {
                int pct = (int)((p.completed.Value * 100.0) / p.total.Value);
                _host.SetProgress(20 + (95 - 20) * (pct / 100.0));
                label = $"{p.status} {pct}% ({p.completed.Value / 1_073_741_824.0:F2}/{p.total.Value / 1_073_741_824.0:F2} GB)";
            }
            _host.SetStatus(label);
            _host.SetInstanceExtraStatus(id, $"PULL: {label}");
            if (p.status != lastPhase) { lastPhase = p.status; _host.AppendLog($"  [pull] {p.status}"); }
        });

        try { await ollama.PullModelAsync(newModel.OllamaTag, progress, ct); }
        catch (Exception ex) when (ex.Message.Contains("no space left", StringComparison.OrdinalIgnoreCase))
        {
            _host.AppendLog("  ⚠ DISK FULL - pull aborted.");
            _host.AppendLog($"  Recover: SSH in and run  rm -rf /root/.ollama/models/blobs/*-partial*");
            _host.AppendLog($"  Or: Destroy {id} and rent a new instance with more disk space.");
            _host.SetInstanceExtraStatus(id, "CHANGE FAILED: disk full.");
            throw;
        }
        catch (Exception ex) when (ex.Message.Contains("sharded GGUF", StringComparison.OrdinalIgnoreCase))
        {
            _host.AppendLog("  ⚠ SHARDED GGUF - Ollama 0.30 can't pull these via hf.co/ (ollama/ollama#5245).");
            _host.AppendLog($"  Edit '{newModel.Key}' to a non-sharded quant, or pick a curated ollama.com tag.");
            _host.SetInstanceExtraStatus(id, "PULL FAILED: sharded GGUF.");
            throw;
        }
    }

    private async Task ReportCapabilitiesAsync(OllamaApi ollama, ModelEntry newModel, CancellationToken ct)
    {
        var caps = await ollama.GetModelCapabilitiesAsync(newModel.OllamaTag, ct);
        if (caps.Count == 0) return;
        _host.AppendLog($"Model capabilities: {string.Join(", ", caps)}");
        if (!caps.Contains("tools", StringComparer.OrdinalIgnoreCase))
            _host.AppendLog("  ⚠ No 'tools' capability - OpenCode tool-calling won't work.");
    }
}
