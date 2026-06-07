using VastSpinup.Models;
using VastSpinup.Services;

namespace VastSpinup.Wpf.Orchestration;

/// <summary>
/// Small surface area that long-running orchestrators (rent, change-model) need from the
/// containing MainViewModel. Keeping this narrow makes it possible to extract these flows
/// out of the VM without a god-class fight, and gives a clear seam for future tests
/// (a fake host can record calls).
/// </summary>
public interface IOrchestratorHost
{
    // ── Services the orchestrators consume ────────────────────────────────────
    VastApi Api { get; }
    HostStatsService HostStats { get; }
    TemplateHashService TemplateHash { get; }

    // ── UI / state push-out (UI-thread safe; the host marshals if needed) ────
    void AppendLog(string line);
    void SetStatus(string status);
    void SetProgress(double progress);
    void SetInstanceExtraStatus(long instanceId, string? msg);
    void ShowIncompatibleModelPopup(ModelEntry model, bool multimodal);
    Task RefreshInstancesAsync(bool silent);

    // ── Session-scoped flags that influence subsequent picks/filters ─────────
    void AddDeadOffer(long offerId);
    void AddMultimodalIncompatible(long instanceId);

    // ── Cancellation registry so Stop/Destroy on an instance row can cancel
    //     an in-flight spinup or change-model for the same id. ──────────────
    void RegisterSpinupCts(long instanceId, CancellationTokenSource cts);
    void UnregisterSpinupCts(long instanceId);
}
