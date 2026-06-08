namespace VastAiInstanceManager.Models;

/// <summary>
/// GPU families known to fail with current Ollama / llama.cpp CUDA builds.
/// The vastai/ollama image is typically compiled for compute capability sm_75+ (Turing and newer).
/// Older Pascal / Maxwell / Kepler / Volta cards either won't run at all or crash with
/// "CUDA error: device kernel image is invalid".
///
/// Patterns are case-insensitive substring matches against the offer's GPU name.
/// Per-model overrides live in ModelEntry.ExcludedGpuPatterns; if null, these defaults apply.
/// </summary>
public static class GpuBlocklist
{
    public static readonly IReadOnlyList<string> Default = new[]
    {
        // Volta datacenter (sm_70) - confirmed broken with vastai/ollama:0.30.0
        "Tesla V100",
        "Titan V",

        // Pascal datacenter (sm_60 / sm_61)
        "Tesla P100",
        "Tesla P40",
        "Tesla P4",

        // Maxwell datacenter (sm_52)
        "Tesla M40",
        "Tesla M60",

        // Kepler datacenter (sm_35 / sm_37) - ancient
        "Tesla K80",
        "Tesla K40",
        "Tesla K20",

        // Pascal consumer (sm_61)
        "GTX 1080",
        "GTX 1070",
        "GTX 1060",
        "GTX 1050",
        "Titan Xp",

        // Maxwell consumer (sm_52)
        "GTX 980",
        "GTX 970",
        "GTX 960",
        "GTX 950",

        // Pascal Quadro (sm_61) - note: matches "Quadro P4000" etc. (Pascal)
        // RTX A4000/A5000/A6000 are Ampere (sm_86) and are NOT matched.
        "Quadro P",

        // Maxwell / Kepler Quadro
        "Quadro M",
        "Quadro K",
    };

    public static bool IsExcluded(string gpuName, IReadOnlyList<string>? patterns = null)
    {
        if (string.IsNullOrEmpty(gpuName)) return false;
        var list = patterns ?? Default;
        foreach (var p in list)
        {
            if (gpuName.Contains(p, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
