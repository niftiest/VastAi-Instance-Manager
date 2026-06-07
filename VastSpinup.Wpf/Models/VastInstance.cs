using System.Text.Json.Serialization;

namespace VastSpinup.Models;

public class VastInstance
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("actual_status")] public string? ActualStatus { get; set; }
    [JsonPropertyName("intended_status")] public string? IntendedStatus { get; set; }
    [JsonPropertyName("status_msg")] public string? StatusMsg { get; set; }
    [JsonPropertyName("public_ipaddr")] public string? PublicIpAddr { get; set; }
    [JsonPropertyName("ssh_host")] public string? SshHost { get; set; }
    [JsonPropertyName("ssh_port")] public int SshPort { get; set; }
    [JsonPropertyName("dph_total")] public double DphTotal { get; set; }
    [JsonPropertyName("gpu_name")] public string? GpuName { get; set; }
    [JsonPropertyName("num_gpus")] public int NumGpus { get; set; }
    [JsonPropertyName("gpu_ram")] public int GpuRamMb { get; set; }              // per-GPU VRAM (MB)
    [JsonPropertyName("gpu_total_ram")] public int GpuTotalRamMb { get; set; }   // total across all GPUs (MB)
    [JsonPropertyName("disk_space")] public double DiskSpaceGb { get; set; }     // provisioned disk (GB)
    [JsonPropertyName("machine_id")] public long MachineId { get; set; }         // host's physical box ID (shared with offers)
    [JsonPropertyName("template_hash_id")] public string? TemplateHashId { get; set; }   // template_hash for offer filtering ("warm hosts")
    [JsonPropertyName("start_date")] public double? StartDate { get; set; }

    public int TotalVramGb
    {
        get
        {
            // vast.ai's instance endpoint sometimes omits gpu_total_ram; fall back to per-card × count.
            if (GpuTotalRamMb > 0) return GpuTotalRamMb / 1024;
            if (GpuRamMb > 0 && NumGpus > 0) return (GpuRamMb * NumGpus) / 1024;
            return 0;
        }
    }
    public int DiskGb => (int)DiskSpaceGb;

    public string GpuDisplay => string.IsNullOrEmpty(GpuName) ? "—" : (NumGpus > 0 ? $"{NumGpus}x {GpuName}" : GpuName);

    public TimeSpan? Uptime
    {
        get
        {
            if (!StartDate.HasValue || StartDate.Value <= 0) return null;
            var start = DateTimeOffset.FromUnixTimeSeconds((long)StartDate.Value).UtcDateTime;
            var delta = DateTime.UtcNow - start;
            return delta.TotalSeconds < 0 ? TimeSpan.Zero : delta;
        }
    }

    public double? SpentDollars
    {
        get
        {
            var up = Uptime;
            if (up == null) return null;
            return up.Value.TotalHours * DphTotal;
        }
    }

    public string UptimeDisplay
    {
        get
        {
            var up = Uptime;
            if (up == null) return "—";
            if (up.Value.TotalDays >= 1) return $"{(int)up.Value.TotalDays}d {up.Value.Hours}h {up.Value.Minutes}m";
            if (up.Value.TotalHours >= 1) return $"{(int)up.Value.TotalHours}h {up.Value.Minutes}m {up.Value.Seconds}s";
            if (up.Value.TotalMinutes >= 1) return $"{(int)up.Value.TotalMinutes}m {up.Value.Seconds}s";
            return $"{(int)up.Value.TotalSeconds}s";
        }
    }

    public string SpentDisplay => SpentDollars.HasValue ? $"${SpentDollars.Value:F3}" : "—";

    // 'ports' is Docker-style mapping dict like {"11434/tcp": [{"HostIp": "0.0.0.0", "HostPort": "17626"}]}
    [JsonPropertyName("ports")] public System.Text.Json.JsonElement? PortsRaw { get; set; }

    public string? GetExternalPortFor(int containerPort)
    {
        if (PortsRaw is null) return null;
        var pe = PortsRaw.Value;
        if (pe.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        var key = $"{containerPort}/tcp";
        if (!pe.TryGetProperty(key, out var arr) || arr.ValueKind != System.Text.Json.JsonValueKind.Array || arr.GetArrayLength() == 0)
            return null;
        var first = arr[0];
        if (first.TryGetProperty("HostPort", out var hp)) return hp.GetString();
        return null;
    }
}
