using System.Text.Json;
using System.Text.RegularExpressions;
using VastSpinup.Models;

namespace VastSpinup.Services;

public class ReadmeOverrides
{
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? MinP { get; set; }
    public int? NumCtx { get; set; }
    public double? RepeatPenalty { get; set; }
    public string? RecommendedSection { get; set; }   // snippet for the Notes field

    public bool HasAny => Temperature.HasValue || TopP.HasValue || TopK.HasValue
        || MinP.HasValue || NumCtx.HasValue || RepeatPenalty.HasValue
        || !string.IsNullOrEmpty(RecommendedSection);

    public List<string> AppliedFields()
    {
        var l = new List<string>();
        if (Temperature.HasValue) l.Add($"temperature={Temperature:F2}");
        if (TopP.HasValue)        l.Add($"top_p={TopP:F2}");
        if (TopK.HasValue)        l.Add($"top_k={TopK}");
        if (MinP.HasValue)        l.Add($"min_p={MinP:F2}");
        if (NumCtx.HasValue)      l.Add($"num_ctx={NumCtx}");
        if (RepeatPenalty.HasValue) l.Add($"repeat_penalty={RepeatPenalty:F2}");
        return l;
    }
}

/// <summary>
/// Heuristic defaults for new model entries. Given the HF id, suggested Ollama tag, and the
/// representative GGUF file size, this produces a fully-populated ModelEntry with sensible
/// VRAM/disk requirements + context/temperature settings inferred from the model's name.
/// The user can still edit anything before saving.
/// </summary>
public static class ModelDefaults
{
    // Standard GPU VRAM sizes that vast.ai offers list — round VramGb up to the next one.
    private static readonly int[] StandardVramGb = { 8, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128 };

    public static ModelEntry Suggest(string hfId, string ollamaTag, double ggufSizeGb)
    {
        var entry = new ModelEntry
        {
            Name = DeriveName(hfId),
            OllamaTag = ollamaTag,
        };

        // Convention used by the FitsModel check: ModelSizeGb is multiplied by 1.15 at fit-check time,
        // so we set it to the raw GGUF size + 1 GB for KV/activation headroom.
        entry.ModelSizeGb = Math.Round(ggufSizeGb + 1.0, 1);

        // Per-card VRAM minimum for the API search filter — round model size up to the next
        // standard GPU size so a single-GPU offer is viable. Multi-GPU still works via total-VRAM fit check.
        entry.VramGb = RoundUpToStandardVram(entry.ModelSizeGb * 1.15);

        // Disk provisioning. Need: container (~5 GB) + system + model file + room for one extra model
        // (for Change Model swap). Formula: ceil(size * 1.8) + 5 — fits the pattern of the curated defaults.
        entry.DiskGb = (int)Math.Ceiling(ggufSizeGb * 1.8) + 5;

        // Behavioral defaults inferred from model class.
        var (ctx, temp, topP, topK) = ClassifyModel(hfId);
        entry.NumCtx = ctx;
        entry.Temperature = temp;
        entry.TopP = topP;
        entry.TopK = topK;

        entry.Notes = $"From HF: {hfId}  •  Defaults derived for ~{ggufSizeGb:F1} GB GGUF " +
                      $"({GetModelClass(hfId)}). Verify Ollama tag with 'ollama pull <tag>' before renting.";
        return entry;
    }

    private static int RoundUpToStandardVram(double gbNeeded)
    {
        foreach (var s in StandardVramGb)
            if (s >= gbNeeded) return s;
        return StandardVramGb[^1]; // 128 GB max — anything above gets capped
    }

    private static string DeriveName(string hfId)
    {
        var slash = hfId.IndexOf('/');
        return slash >= 0 ? hfId[(slash + 1)..].Replace('-', ' ') : hfId;
    }

    private static string GetModelClass(string hfId)
    {
        var l = hfId.ToLowerInvariant();
        if (l.Contains("coder") || l.Contains("code") || l.Contains("starcoder") || l.Contains("deepseek-coder")) return "coder";
        if (l.Contains("vision") || l.Contains("-vl") || l.Contains("vlm") || l.Contains("llava")) return "vision";
        if (l.Contains("qwq") || l.Contains("reasoning") || l.Contains("thinking")
            || l.Contains("o1-") || l.Contains("r1-") || l.Contains("-r1") || l.Contains("deepseek-r1")) return "reasoning";
        if (l.Contains("instruct") || l.Contains("chat") || l.Contains("assistant")) return "instruct";
        return "generic";
    }

    /// <summary>Returns (numCtx, temperature, topP, topK) tuned to the model's role.</summary>
    private static (int ctx, double temp, double topP, int topK) ClassifyModel(string hfId)
    {
        switch (GetModelClass(hfId))
        {
            case "coder":     return (32768, 0.3, 0.8, 20);   // deterministic, longer ctx for whole files
            case "reasoning": return (32768, 0.7, 0.95, 40);  // higher topP for chain-of-thought diversity
            case "vision":    return (16384, 0.7, 0.95, 64);  // shorter ctx (images eat tokens), broader top_k
            case "instruct":  return (16384, 0.7, 0.9, 40);   // standard chat
            default:          return (16384, 0.7, 0.9, 40);   // safe defaults
        }
    }

    /// <summary>
    /// Scans a HuggingFace README for developer-recommended sampling parameters.
    /// Handles common patterns seen in the wild:
    ///   "Temperature: 0.6"   "temp = 0.6"   "Recommended temperature is 0.6"
    ///   "top_p: 0.95"        "Top-P 0.95"
    ///   "top_k = 20"
    ///   "min_p: 0.0"
    ///   "Context length: 32K"   "ctx: 32768"
    ///   "repeat_penalty: 1.1"
    /// Also pulls a snippet from the "Recommended" / "Usage" / "Sampling" / "Parameters" section
    /// for display in the model's Notes field.
    /// </summary>
    public static ReadmeOverrides ParseReadme(string? readme)
    {
        var o = new ReadmeOverrides();
        if (string.IsNullOrWhiteSpace(readme)) return o;

        double? FindDouble(string keyPattern)
        {
            var m = Regex.Match(readme, $@"(?i)(?:^|[\s|`*•\-])(?:{keyPattern})\s*[:=]?\s*([0-9]*\.?[0-9]+)\b", RegexOptions.Multiline);
            return m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;
        }
        int? FindInt(string keyPattern)
        {
            var m = Regex.Match(readme, $@"(?i)(?:^|[\s|`*•\-])(?:{keyPattern})\s*[:=]?\s*([0-9]+)\b", RegexOptions.Multiline);
            return m.Success && int.TryParse(m.Groups[1].Value, out var v) ? v : null;
        }

        o.Temperature   = FindDouble(@"temp(?:erature)?");
        o.TopP          = FindDouble(@"top[-_ ]?p");
        o.TopK          = FindInt   (@"top[-_ ]?k");
        o.MinP          = FindDouble(@"min[-_ ]?p");
        o.RepeatPenalty = FindDouble(@"repeat[-_ ]?penalty|repetition[-_ ]?penalty");

        // Context can be "32K", "32k", or a plain number.
        var ctxMatch = Regex.Match(readme,
            @"(?i)(?:^|[\s|`*•\-])(?:context[-_ ]?(?:length|window|size)?|ctx|num[-_ ]ctx|max[-_ ]?(?:tokens|context))\s*[:=]?\s*([0-9]+)\s*([kK])?\b",
            RegexOptions.Multiline);
        if (ctxMatch.Success && int.TryParse(ctxMatch.Groups[1].Value, out var ctxVal))
        {
            if (ctxMatch.Groups[2].Value.Length > 0) ctxVal *= 1024;
            // Sanity: skip absurd values like "context up to 1M tokens" → 1024 ctx would be wrong, but
            // a misparsed 1 with no K suffix would also be wrong. Accept 512..1_048_576.
            if (ctxVal >= 512 && ctxVal <= 1_048_576) o.NumCtx = ctxVal;
        }

        o.RecommendedSection = ExtractSection(readme);
        return o;
    }

    /// <summary>
    /// Parses Ollama's registry params blob — authoritative, machine-readable, and curator-vetted.
    /// Schema (varies per model): {"temperature":0.6,"top_p":0.95,"top_k":40,"repeat_penalty":1,
    /// "stop":["..."],"num_ctx":32768,"num_predict":-1,"min_p":0.0}
    /// Should be preferred over README parsing when available.
    /// </summary>
    public static ReadmeOverrides ParseRegistryParams(JsonElement? paramsJson)
    {
        var o = new ReadmeOverrides();
        if (paramsJson == null || paramsJson.Value.ValueKind != JsonValueKind.Object) return o;
        var root = paramsJson.Value;
        if (root.TryGetProperty("temperature", out var t) && t.ValueKind == JsonValueKind.Number)    o.Temperature = t.GetDouble();
        if (root.TryGetProperty("top_p",       out var p) && p.ValueKind == JsonValueKind.Number)    o.TopP        = p.GetDouble();
        if (root.TryGetProperty("top_k",       out var k) && k.ValueKind == JsonValueKind.Number)    o.TopK        = k.GetInt32();
        if (root.TryGetProperty("min_p",       out var mp) && mp.ValueKind == JsonValueKind.Number)  o.MinP        = mp.GetDouble();
        if (root.TryGetProperty("num_ctx",     out var nc) && nc.ValueKind == JsonValueKind.Number)  o.NumCtx      = nc.GetInt32();
        if (root.TryGetProperty("repeat_penalty", out var rp) && rp.ValueKind == JsonValueKind.Number) o.RepeatPenalty = rp.GetDouble();
        return o;
    }

    /// <summary>Merges two ReadmeOverrides; primary wins when both have a value for the same field.</summary>
    public static ReadmeOverrides Merge(ReadmeOverrides primary, ReadmeOverrides fallback)
    {
        return new ReadmeOverrides
        {
            Temperature   = primary.Temperature   ?? fallback.Temperature,
            TopP          = primary.TopP          ?? fallback.TopP,
            TopK          = primary.TopK          ?? fallback.TopK,
            MinP          = primary.MinP          ?? fallback.MinP,
            NumCtx        = primary.NumCtx        ?? fallback.NumCtx,
            RepeatPenalty = primary.RepeatPenalty ?? fallback.RepeatPenalty,
            RecommendedSection = !string.IsNullOrEmpty(primary.RecommendedSection)
                ? primary.RecommendedSection : fallback.RecommendedSection,
        };
    }

    /// <summary>Pulls a 'base_model: foo/bar' value out of a README's YAML frontmatter.
    /// Handles both `base_model: foo/bar` and `base_model:\n  - foo/bar` (list) forms.</summary>
    public static string? ExtractBaseModelFromReadme(string? readme)
    {
        if (string.IsNullOrWhiteSpace(readme)) return null;
        if (!readme.StartsWith("---")) return null;
        var endIdx = readme.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (endIdx < 0) return null;
        var yaml = readme.Substring(4, endIdx - 4);
        var m = Regex.Match(yaml, @"(?m)^base_model:\s*(?:\r?\n\s*-\s*)?([^\s\r\n]+)");
        if (!m.Success) return null;
        var v = m.Groups[1].Value.Trim().Trim('"', '\'');
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private static string? ExtractSection(string readme)
    {
        var lines = readme.Replace("\r\n", "\n").Split('\n');
        // Find any heading that signals recommendations/usage/sampling/etc.
        int start = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^#+\s*(recommend|recommended|usage|sampling|settings|parameters|inference|generation|how to use)", RegexOptions.IgnoreCase))
            {
                start = i;
                break;
            }
        }
        if (start < 0) return null;

        // Section ends at the next markdown heading of equal or higher level (we don't dedent — just stop on next #).
        int end = lines.Length;
        for (int i = start + 1; i < lines.Length; i++)
        {
            if (Regex.IsMatch(lines[i], @"^#+\s+\S")) { end = i; break; }
        }
        var snippet = string.Join("\n", lines.Skip(start).Take(end - start)).Trim();
        if (snippet.Length > 600) snippet = snippet[..600].TrimEnd() + "...";
        return snippet;
    }
}
