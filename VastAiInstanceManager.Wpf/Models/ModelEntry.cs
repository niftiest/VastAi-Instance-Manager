using System.Text.Json.Serialization;

namespace VastAiInstanceManager.Models;

public class ModelEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("vram_gb")] public int VramGb { get; set; }
    [JsonPropertyName("disk_gb")] public int DiskGb { get; set; }
    [JsonPropertyName("ollama_tag")] public string OllamaTag { get; set; } = "";
    [JsonPropertyName("num_ctx")] public int NumCtx { get; set; } = 16384;
    [JsonPropertyName("temperature")] public double Temperature { get; set; } = 0.7;
    [JsonPropertyName("top_p")] public double TopP { get; set; } = 0.9;
    [JsonPropertyName("top_k")] public int TopK { get; set; } = 40;
    [JsonPropertyName("min_p")] public double MinP { get; set; } = 0.0;
    [JsonPropertyName("repeat_penalty")] public double RepeatPenalty { get; set; } = 1.1;
    [JsonPropertyName("model_size_gb")] public double ModelSizeGb { get; set; }
    [JsonPropertyName("notes")] public string Notes { get; set; } = "";

    /// <summary>Modality flag used to filter the Change Model dialog when an instance has shown
    /// it can't load multimodal models (e.g. CLIP load failure on vastai/ollama:0.30.0).
    /// Values: "text", "+vis", "+aud", "+v/a", "multi", "" (unknown - treated as safe).
    /// Populated automatically when adding a model via HF search; can be edited later.</summary>
    [JsonPropertyName("modality")] public string Modality { get; set; } = "";

    /// <summary>True when this model loads more than text (vision, audio, or any-to-any).</summary>
    [JsonIgnore] public bool IsMultimodal =>
        Modality == "+vis" || Modality == "+aud" || Modality == "+v/a" || Modality == "multi";

    /// <summary>Optional override of the default GPU blocklist patterns. Null = use GpuBlocklist.Default.</summary>
    [JsonPropertyName("excluded_gpu_patterns")] public List<string>? ExcludedGpuPatterns { get; set; }

    // UI-only filter settings; not in the JSON registry
    [JsonIgnore] public double? MaxPricePerHour { get; set; }
    [JsonIgnore] public double MinReliability { get; set; } = 0.95;

    [JsonIgnore] public string Key { get; set; } = "";   // populated when loaded from dict

    public override string ToString() => $"{Key}  -  {Name}";
}
