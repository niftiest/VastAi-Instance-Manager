using System.Text.Json;
using System.Text.Json.Serialization;

namespace VastSpinup.Services;

/// <summary>Per-host (machine_id) performance stats, persisted across sessions.</summary>
public class HostStats
{
    [JsonPropertyName("machine_id")] public long MachineId { get; set; }
    [JsonPropertyName("avg_boot_sec")] public double AvgBootSec { get; set; }     // Create → instance running
    [JsonPropertyName("avg_ollama_sec")] public double AvgOllamaSec { get; set; } // running → Ollama responds
    [JsonPropertyName("sample_count")] public int SampleCount { get; set; }
    [JsonPropertyName("last_seen")] public DateTime LastSeen { get; set; }

    /// <summary>Total seconds from rent-click to model-pull-ready.</summary>
    public double TotalReadySec => AvgBootSec + AvgOllamaSec;
}

/// <summary>
/// Tracks how long each vast.ai host (keyed by machine_id) historically takes to go from
/// "rent" → "ready for model pull". Surfaces in the offers list so a cheap-but-slow host
/// can be weighed against a pricier-but-faster one.
/// </summary>
public class HostStatsService
{
    private Dictionary<long, HostStats> _stats = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Load()
    {
        var list = AtomicJson.Read<List<HostStats>>(
            AppPaths.HostStatsFile,
            onError: (p, ex) => AppLog.Warn($"Failed to read host-stats file '{p}'.", ex));
        if (list != null) _stats = list.ToDictionary(s => s.MachineId);
    }

    public void Save()
    {
        AppPaths.EnsureAll();
        AtomicJson.Write(
            AppPaths.HostStatsFile,
            _stats.Values.OrderBy(s => s.MachineId).ToList(),
            JsonOpts,
            (p, ex) => AppLog.Warn($"Failed to write host-stats file '{p}'.", ex));
    }

    public HostStats? Get(long machineId)
        => machineId != 0 && _stats.TryGetValue(machineId, out var s) ? s : null;

    /// <summary>Records one boot observation. Updates the running average incrementally.</summary>
    public void Record(long machineId, double bootSec, double ollamaSec)
    {
        if (machineId == 0) return;
        if (!_stats.TryGetValue(machineId, out var s))
        {
            s = new HostStats { MachineId = machineId };
            _stats[machineId] = s;
        }
        int n = s.SampleCount + 1;
        s.AvgBootSec   = (s.AvgBootSec   * s.SampleCount + bootSec)   / n;
        s.AvgOllamaSec = (s.AvgOllamaSec * s.SampleCount + ollamaSec) / n;
        s.SampleCount  = n;
        s.LastSeen     = DateTime.UtcNow;
        Save();
    }
}
