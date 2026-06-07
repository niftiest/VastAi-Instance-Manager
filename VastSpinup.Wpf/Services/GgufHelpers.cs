using System.Text.RegularExpressions;

namespace VastSpinup.Services;

/// <summary>
/// Quant-aware GGUF file selection. Used by the HuggingFace search dialog (and other callers)
/// to pick a representative file per repo for VRAM/disk sizing and Ollama-tag construction.
/// </summary>
public static class GgufHelpers
{
    /// <summary>Quant preference, best → fallback. mxfp4 is gpt-oss native; bf16/f16/fp16 last-resort.</summary>
    public static readonly string[] PreferredQuants =
        { "q4_k_m", "q4_0", "q4_k_s", "q5_k_m", "q5_0", "mxfp4", "q8_0", "bf16", "f16", "fp16" };

    /// <summary>Returns the size in bytes of the file that PickRepresentativeFile would pick. 0 if no GGUF found.</summary>
    public static long PickRepresentativeSizeBytes(List<(string filename, long sizeBytes)> files)
    {
        var p = PickRepresentativeFile(files);
        return p?.sizeBytes ?? 0;
    }

    /// <summary>
    /// Picks the single best representative file for VRAM-sizing purposes.
    /// Preference: Q4_K_M (de-facto standard) → Q4_0 → Q4_K_S → Q5_K_M → MXFP4 → median of available.
    /// Aggregates sharded GGUFs (sum of *-NNNNN-of-NNNNN.gguf parts) into a single logical file with isSharded=true.
    /// Prefers non-sharded files when available because Ollama 0.30 can't pull sharded GGUFs via hf.co/ (issue ollama/ollama#5245).
    /// </summary>
    public static (string filename, long sizeBytes, string quantLabel, bool isSharded)? PickRepresentativeFile(List<(string filename, long sizeBytes)> files)
    {
        if (files.Count == 0) return null;

        var aggregated = AggregateShards(files);
        // Exclude tiny GGUFs (< 0.5 GB) — those are imatrix calibration files / metadata, not model weights.
        var modelsOnly = aggregated.Where(f => f.sizeBytes >= 500_000_000).ToList();
        if (modelsOnly.Count == 0) modelsOnly = aggregated;   // fall back if everything is small

        // Pass 1: non-sharded preferred quants (Ollama can pull these via hf.co/)
        foreach (var quant in PreferredQuants)
        {
            var match = modelsOnly.FirstOrDefault(f => !f.isSharded && f.filename.ToLowerInvariant().Contains(quant));
            if (match.filename != null) return (match.filename, match.sizeBytes, quant.ToUpperInvariant(), false);
        }

        // Pass 2: sharded preferred quants (won't work via hf.co/ — caller must check isSharded)
        foreach (var quant in PreferredQuants)
        {
            var match = modelsOnly.FirstOrDefault(f => f.isSharded && f.filename.ToLowerInvariant().Contains(quant));
            if (match.filename != null) return (match.filename, match.sizeBytes, quant.ToUpperInvariant(), true);
        }

        var sorted = modelsOnly.OrderBy(f => f.sizeBytes).ToList();
        var median = sorted[sorted.Count / 2];
        var hint = TryParseQuantFromName(median.filename) ?? "";
        return (median.filename, median.sizeBytes, hint, median.isSharded);
    }

    /// <summary>Sharded GGUFs ("name-00001-of-00010.gguf", ...) get summed into one logical entry per
    /// group with isSharded=true. Non-sharded files pass through unchanged.</summary>
    public static List<(string filename, long sizeBytes, bool isSharded)> AggregateShards(List<(string filename, long sizeBytes)> files)
    {
        var shardRegex = new Regex(
            @"^(?<prefix>.+?)-(?<part>\d{5})-of-(?<total>\d{5})\.gguf$",
            RegexOptions.IgnoreCase);

        var groups = new Dictionary<string, long>();   // prefix → summed size
        var nonSharded = new List<(string, long, bool)>();

        foreach (var (path, size) in files)
        {
            var lastSlash = path.LastIndexOf('/');
            var name = lastSlash >= 0 ? path[(lastSlash + 1)..] : path;
            var m = shardRegex.Match(name);
            if (m.Success)
            {
                var dir = lastSlash >= 0 ? path[..(lastSlash + 1)] : "";
                var key = dir + m.Groups["prefix"].Value;
                groups[key] = (groups.TryGetValue(key, out var v) ? v : 0) + size;
            }
            else
            {
                nonSharded.Add((path, size, false));
            }
        }

        var result = new List<(string filename, long sizeBytes, bool isSharded)>();
        foreach (var (prefix, total) in groups) result.Add(($"{prefix}.gguf", total, true));
        result.AddRange(nonSharded);
        return result;
    }

    /// <summary>"{size} {quant}" e.g. "13.2 GB Q4_K_M", or "—" if no GGUF found. Flags sharded picks with ⚠.</summary>
    public static string FormatRecommendedSize(List<(string filename, long sizeBytes)> files)
    {
        var pick = PickRepresentativeFile(files);
        if (pick == null) return "—";
        const double gib = 1_073_741_824.0;
        var basePart = string.IsNullOrEmpty(pick.Value.quantLabel)
            ? $"{pick.Value.sizeBytes / gib:F1} GB"
            : $"{pick.Value.sizeBytes / gib:F1} GB {pick.Value.quantLabel}";
        return pick.Value.isSharded ? $"⚠ {basePart} sharded" : basePart;
    }

    public static string? TryParseQuantFromName(string filename)
    {
        var m = Regex.Match(filename, @"(?i)\b(iq?\d+(?:_[a-z0-9]+)*|f(?:p)?16|bf16)\b");
        return m.Success ? m.Value.ToUpperInvariant() : null;
    }
}
