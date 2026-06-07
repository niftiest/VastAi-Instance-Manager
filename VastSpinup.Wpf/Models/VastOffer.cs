using System.Text.Json.Serialization;

namespace VastSpinup.Models;

/// <summary>Raw shape of an offer from POST /bundles/. Many fields omitted.</summary>
public class VastOffer
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("ask_contract_id")] public long AskContractId { get; set; }
    [JsonPropertyName("machine_id")] public long MachineId { get; set; }
    [JsonPropertyName("gpu_name")] public string GpuName { get; set; } = "";
    [JsonPropertyName("num_gpus")] public int NumGpus { get; set; }
    [JsonPropertyName("gpu_ram")] public int GpuRamMb { get; set; }
    [JsonPropertyName("gpu_total_ram")] public int GpuTotalRamMb { get; set; }
    [JsonPropertyName("gpu_mem_bw")] public double GpuMemBwGbs { get; set; }
    [JsonPropertyName("dph_total")] public double DphTotal { get; set; }
    [JsonPropertyName("reliability")] public double Reliability { get; set; }
    [JsonPropertyName("disk_space")] public double DiskSpaceGb { get; set; }
    [JsonPropertyName("inet_up")] public double InetUpMbps { get; set; }
    [JsonPropertyName("inet_down")] public double InetDownMbps { get; set; }
    [JsonPropertyName("geolocation")] public string Geolocation { get; set; } = "";
    [JsonPropertyName("cpu_name")] public string CpuName { get; set; } = "";
    [JsonPropertyName("cpu_ram")] public int CpuRamMb { get; set; }
    [JsonPropertyName("direct_port_count")] public int DirectPortCount { get; set; }
    [JsonPropertyName("verification")] public string Verification { get; set; } = "";
    [JsonPropertyName("compute_cap")] public int ComputeCap { get; set; }

    public int TotalVramGb => GpuTotalRamMb / 1024;

    /// <summary>vast.ai sometimes returns "city, country" with city blank (e.g., ", CN").
    /// Strip the empty parts so we render "CN" instead of ", CN".</summary>
    public string GeolocationDisplay
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Geolocation)) return "—";
            var parts = Geolocation.Split(',')
                .Select(p => p.Trim())
                .Where(p => p.Length > 0);
            var joined = string.Join(", ", parts);
            return joined.Length == 0 ? "—" : joined;
        }
    }

    /// <summary>Estimate tokens/sec for a given model size, given memory bandwidth + multi-GPU penalty.</summary>
    public int EstimateTokensPerSec(double modelSizeGb)
    {
        if (modelSizeGb <= 0) return 0;
        double eff = NumGpus switch { 1 => 1.0, 2 => 0.7, 3 => 0.55, _ => 0.45 };
        double realFactor = 0.7; // observed real-world vs theoretical
        bool fits = TotalVramGb >= modelSizeGb * 1.15;
        if (!fits) return (int)(GpuMemBwGbs / modelSizeGb * eff * 0.15);
        return (int)(GpuMemBwGbs / modelSizeGb * eff * realFactor);
    }

    public bool FitsModel(double modelSizeGb) => TotalVramGb >= modelSizeGb * 1.15;
}
