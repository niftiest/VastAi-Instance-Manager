using VastSpinup.Models;
using VastSpinup.Services;

namespace VastSpinup.Wpf.Orchestration;

/// <summary>
/// Drives a single rent → boot → SSH → Ollama → pull → smoke → ready flow.
/// Pure orchestration logic - no WPF types, no observable collections, no commands.
/// All UI updates flow through the IOrchestratorHost.
/// </summary>
public class RentOrchestrator
{
    private readonly IOrchestratorHost _host;

    public RentOrchestrator(IOrchestratorHost host) => _host = host;

    public async Task RunAsync(VastOffer offer, ModelEntry model, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        _host.AppendLog($"=== Spinup: offer {offer.Id} ({offer.NumGpus}x {offer.GpuName}, ${offer.DphTotal:F3}/hr) ===");
        _host.SetStatus("Creating instance...");
        const string onStart = "nohup ollama serve > /var/log/ollama.log 2>&1 &";
        var spinupStart = DateTime.UtcNow;
        long instanceId = await _host.Api.CreateInstanceAsync(offer.Id, model.DiskGb, VastApi.DefaultImage, onStart, ct);
        _host.RegisterSpinupCts(instanceId, cts);
        try
        {
            _host.SetProgress(10);
            _host.AppendLog($"Instance {instanceId} created.");
            _host.SetInstanceExtraStatus(instanceId, "SETUP: Instance created, waiting for boot...");
            await _host.RefreshInstancesAsync(silent: true);

            var keyTask = Task.Run(() => EphemeralKeyService.Generate(instanceId), ct);

            _host.SetStatus("Waiting for instance to come up...");
            var inst = await WaitForRunningAsync(instanceId, ct);
            var bootSec = (DateTime.UtcNow - spinupStart).TotalSeconds;
            _host.SetProgress(30);
            _host.AppendLog($"Instance running. IP: {inst.PublicIpAddr}");

            if (!_host.TemplateHash.HasHash && !string.IsNullOrEmpty(inst.TemplateHashId))
                _host.TemplateHash.Save(inst.TemplateHashId);

            await AttachSshKeyAsync(instanceId, keyTask, ct);
            _host.SetProgress(40);

            var ollamaPort = inst.GetExternalPortFor(11434);
            if (string.IsNullOrEmpty(ollamaPort)) throw new InvalidOperationException("No external port for 11434.");
            var ollamaUrl = $"http://{inst.PublicIpAddr}:{ollamaPort}";
            _host.AppendLog($"Ollama endpoint: {ollamaUrl}");

            var ollama = new OllamaApi(ollamaUrl);
            await WaitForOllamaAsync(instanceId, ollama, ollamaUrl, bootSec, offer.MachineId, ct);

            _host.SetStatus($"Pulling {model.OllamaTag}...");
            _host.AppendLog($"Pulling model {model.OllamaTag}...");
            await PullModelAsync(instanceId, ollama, model, ct);
            _host.AppendLog($"Pull complete: {model.OllamaTag}");
            _host.SetProgress(92);

            // Smoke + capability check
            var smokeErr = await ollama.SmokeTestAsync(model.OllamaTag, ct);
            var category = SmokeClassifier.Classify(smokeErr);
            if (HandleSmokeFailure(offer.Id, instanceId, model, smokeErr, category)) return;

            await ReportCapabilitiesAsync(ollama, model, ct);

            _host.SetStatus("READY. Click 'Use' on the instance to wire up opencode.");
            _host.SetInstanceExtraStatus(instanceId, "READY - click 'Use' to wire up opencode.");
            _host.AppendLog($"=== READY ===  Endpoint: {ollamaUrl}");
            _host.SetProgress(100);
            _host.NotifyInstanceReady(instanceId,
                "Instance ready",
                $"Instance {instanceId} is ready.\n\n{model.OllamaTag} is loaded at {ollamaUrl}.\n\nClick 'Use' on the instance row to wire up opencode.");
        }
        finally
        {
            _host.UnregisterSpinupCts(instanceId);
        }
    }

    private async Task AttachSshKeyAsync(long instanceId, Task<(string PrivateKeyPath, string PublicKey)?> keyTask, CancellationToken ct)
    {
        _host.SetInstanceExtraStatus(instanceId, "SETUP: Attaching SSH key...");
        _host.SetStatus("Attaching ephemeral SSH key...");
        var key = await keyTask;
        if (!key.HasValue) return;
        try
        {
            int attempts = await _host.Api.AttachSshKeyAsync(instanceId, key.Value.PublicKey, ct);
            string attemptStr = attempts > 1 ? $" ({attempts} attempts)" : "";
            _host.AppendLog($"Ephemeral SSH key attached{attemptStr}.");
        }
        catch (Exception ex) { _host.AppendLog("SSH key attach skipped: " + ex.Message + " (Ollama HTTP still works.)"); }
    }

    private async Task WaitForOllamaAsync(long instanceId, OllamaApi ollama, string ollamaUrl, double bootSec, long machineId, CancellationToken ct)
    {
        _host.SetStatus("Waiting for Ollama daemon...");
        _host.SetInstanceExtraStatus(instanceId, "SETUP: Waiting for Ollama daemon...");
        var ollamaWaitStart = DateTime.UtcNow;
        const int ollamaMaxSec = 300;
        const double expectedOllamaSec = 25.0;
        string? ollamaVersion = null;
        while ((DateTime.UtcNow - ollamaWaitStart).TotalSeconds < ollamaMaxSec)
        {
            ct.ThrowIfCancellationRequested();
            ollamaVersion = await ollama.GetVersionAsync(ct);
            if (ollamaVersion != null) break;
            // Ease Progress from 40 (entry) toward 48 (just below PROG_OLLAMA=50)
            double elapsedSec = (DateTime.UtcNow - ollamaWaitStart).TotalSeconds;
            _host.SetProgress(40 + 8 * Math.Min(0.95, elapsedSec / expectedOllamaSec));
            _host.SetInstanceExtraStatus(instanceId, $"SETUP: Waiting for Ollama daemon... ({(int)elapsedSec}s of {ollamaMaxSec}s max)");
            await Task.Delay(5000, ct);
        }
        if (ollamaVersion == null)
            throw new TimeoutException($"Ollama daemon never responded at {ollamaUrl} within {ollamaMaxSec}s.");
        var ollamaReadySec = (DateTime.UtcNow - ollamaWaitStart).TotalSeconds;
        _host.SetProgress(50);
        _host.AppendLog($"Ollama ready (v{ollamaVersion}). Boot {bootSec:F0}s + Ollama wait {ollamaReadySec:F0}s = {bootSec + ollamaReadySec:F0}s.");
        _host.HostStats.Record(machineId, bootSec, ollamaReadySec);
    }

    private async Task PullModelAsync(long instanceId, OllamaApi ollama, ModelEntry model, CancellationToken ct)
    {
        string lastPhase = "";
        var progress = new Progress<(string status, long? total, long? completed)>(p =>
        {
            string label = p.status;
            if (p.total.HasValue && p.completed.HasValue && p.total > 0)
            {
                int pct = (int)((p.completed.Value * 100.0) / p.total.Value);
                _host.SetProgress(50 + (40 * pct / 100.0));
                label = $"{p.status} {pct}% ({p.completed.Value / 1_073_741_824.0:F2}/{p.total.Value / 1_073_741_824.0:F2} GB)";
            }
            _host.SetStatus(label);
            _host.SetInstanceExtraStatus(instanceId, $"PULL: {label}");
            if (p.status != lastPhase) { lastPhase = p.status; _host.AppendLog($"  [pull] {p.status}"); }
        });
        try { await ollama.PullModelAsync(model.OllamaTag, progress, ct); }
        catch (Exception ex) when (ex.Message.Contains("sharded GGUF", StringComparison.OrdinalIgnoreCase))
        {
            _host.AppendLog("  ⚠ SHARDED GGUF - Ollama 0.30 can't pull these via hf.co/ (ollama/ollama#5245).");
            _host.AppendLog($"  Edit the '{model.Key}' entry to a single-file quant, or pick a curated ollama.com tag.");
            _host.SetInstanceExtraStatus(instanceId, "PULL FAILED: sharded GGUF.");
            throw;
        }
    }

    /// <summary>Returns true if the flow should stop (broken host / broken model / multimodal-incompat).</summary>
    private bool HandleSmokeFailure(long offerId, long instanceId, ModelEntry model, string? smokeErr, SmokeClassifier.Category category)
    {
        if (category == SmokeClassifier.Category.Ok)
        {
            _host.AppendLog("Smoke test OK - model loaded into VRAM successfully.");
            return false;
        }
        _host.AppendLog($"  ⚠ Smoke test FAILED: {smokeErr}");
        if (category == SmokeClassifier.Category.BrokenHost)
        {
            _host.AddDeadOffer(offerId);
            _host.AppendLog($"  This host's CUDA/driver setup is broken. Offer {offerId} blacklisted for this session.");
            _host.AppendLog($"  Destroy {instanceId} and pick a different offer.");
            _host.SetInstanceExtraStatus(instanceId, "BROKEN HOST - destroy and retry on a different offer.");
            _host.SetStatus("Host has broken CUDA - destroy and try another.");
            _host.SetProgress(100);
            return true;
        }
        if (category is SmokeClassifier.Category.MultimodalIncompatible or SmokeClassifier.Category.BrokenModel)
        {
            bool multimodal = category == SmokeClassifier.Category.MultimodalIncompatible;
            if (multimodal) _host.AddMultimodalIncompatible(instanceId);
            _host.AppendLog(multimodal
                ? "  MULTIMODAL incompatible on this provider - the vision/audio projector can't load on its Ollama version."
                : "  MODEL/runtime mismatch - host is fine, this model+ollama combo isn't.");
            _host.AppendLog("  Use Change Model on this instance to pick a different one.");
            _host.SetInstanceExtraStatus(instanceId, multimodal
                ? "MULTIMODAL INCOMPATIBLE - use Change Model (text-only models will load)."
                : "MODEL INCOMPATIBLE - use Change Model.");
            _host.SetStatus("Model incompatible. Host is fine.");
            _host.SetProgress(100);
            _host.ShowIncompatibleModelPopup(model, multimodal);
            return true;
        }
        _host.AppendLog("  (Unknown error category - continuing setup.)");
        return false;
    }

    private async Task ReportCapabilitiesAsync(OllamaApi ollama, ModelEntry model, CancellationToken ct)
    {
        var capabilities = await ollama.GetModelCapabilitiesAsync(model.OllamaTag, ct);
        if (capabilities.Count == 0) return;
        _host.AppendLog($"Model capabilities: {string.Join(", ", capabilities)}");
        if (!capabilities.Contains("tools", StringComparer.OrdinalIgnoreCase))
        {
            _host.AppendLog("  ⚠ No 'tools' capability - OpenCode CAN'T use this model for file edits / tool calls.");
            _host.AppendLog("  Works for plain chat, but agent-style workflows will fail with 'does not support tools'.");
        }
    }

    private async Task<VastInstance> WaitForRunningAsync(long instanceId, CancellationToken ct)
    {
        var start = DateTime.UtcNow;
        string lastMsg = "";
        var lastChange = start;
        const int hardTimeoutSec = 900;
        const int sameMsgCapSec = 300;
        const double expectedBootSec = 90.0;  // typical vast.ai boot - eases progress smoothly toward 28 while waiting
        while ((DateTime.UtcNow - start).TotalSeconds < hardTimeoutSec)
        {
            ct.ThrowIfCancellationRequested();
            var inst = await _host.Api.GetInstanceAsync(instanceId, ct);
            double elapsedSec = (DateTime.UtcNow - start).TotalSeconds;
            // Ease Progress from 10 (entry) toward 28 (just below PROG_BOOTED=30), capped at 0.95
            // of the range so it never hits 30 until the instance is actually running.
            _host.SetProgress(10 + 18 * Math.Min(0.95, elapsedSec / expectedBootSec));
            if (inst != null)
            {
                if (inst.ActualStatus == "running") return inst;
                if (inst.ActualStatus is "error" or "failed" or "destroyed" or "offline" or "exited")
                    throw new InvalidOperationException($"Instance entered {inst.ActualStatus}: {inst.StatusMsg}");
                var msg = inst.StatusMsg ?? "";
                if (msg != lastMsg)
                {
                    lastMsg = msg;
                    lastChange = DateTime.UtcNow;
                    _host.AppendLog($"  > {msg}");
                }
                _host.SetInstanceExtraStatus(instanceId, $"SETUP [{inst.ActualStatus}]: {msg} ({(int)elapsedSec}s)");
                if ((DateTime.UtcNow - lastChange).TotalSeconds > sameMsgCapSec)
                    throw new TimeoutException($"Stuck on '{msg}' for >{sameMsgCapSec}s.");
            }
            await Task.Delay(3000, ct);
        }
        throw new TimeoutException($"Instance {instanceId} not running after {hardTimeoutSec}s.");
    }
}
