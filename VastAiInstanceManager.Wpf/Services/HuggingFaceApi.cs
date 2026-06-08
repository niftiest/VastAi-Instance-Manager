using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VastAiInstanceManager.Services;

public class HuggingFaceModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("author")] public string? Author { get; set; }
    [JsonPropertyName("downloads")] public long Downloads { get; set; }
    [JsonPropertyName("likes")] public long Likes { get; set; }
    [JsonPropertyName("lastModified")] public DateTime? LastModified { get; set; }
    [JsonPropertyName("createdAt")] public DateTime? CreatedAt { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("pipeline_tag")] public string? PipelineTag { get; set; }
}

public class HuggingFaceModelDetail : HuggingFaceModel
{
    [JsonPropertyName("siblings")] public List<HuggingFaceSibling>? Siblings { get; set; }
}

public class HuggingFaceSibling
{
    [JsonPropertyName("rfilename")] public string Filename { get; set; } = "";
    [JsonPropertyName("size")] public long? Size { get; set; }   // only populated by /api/models/{id}/tree/, not /api/models/{id}
}

public class HuggingFaceApi
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://huggingface.co/api";
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public HuggingFaceApi(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("VastAiInstanceManager/1.0");
    }

    /// <summary>Search public HF models. Filters to GGUF-formatted repos by default since
    /// those are what Ollama can serve. Sorted by downloads descending.</summary>
    public async Task<List<HuggingFaceModel>> SearchAsync(string query, bool ggufOnly = true, int limit = 25, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        var q = Uri.EscapeDataString(query);
        // &full=true gives us author + lastModified (otherwise null on the default search response).
        var url = $"{BaseUrl}/models?search={q}&limit={limit}&sort=downloads&direction=-1&full=true";
        if (ggufOnly) url += "&filter=gguf";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var list = await JsonSerializer.DeserializeAsync<List<HuggingFaceModel>>(stream, JsonOpts, ct);
            return list ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Top public HF models by likes, no search query - used to pre-populate the search
    /// dialog so users land on something useful instead of a blank list.</summary>
    public async Task<List<HuggingFaceModel>> GetPopularAsync(bool ggufOnly = true, int limit = 100, CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/models?limit={limit}&sort=likes&direction=-1&full=true";
        if (ggufOnly) url += "&filter=gguf";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var list = await JsonSerializer.DeserializeAsync<List<HuggingFaceModel>>(stream, JsonOpts, ct);
            return list ?? new();
        }
        catch { return new(); }
    }

    /// <summary>Full model record including siblings list (file names; sizes are NOT in this response).</summary>
    public async Task<HuggingFaceModelDetail?> GetDetailAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/models/{id}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonSerializer.DeserializeAsync<HuggingFaceModelDetail>(stream, JsonOpts, ct);
        }
        catch { return null; }
    }

    /// <summary>
    /// Returns (filename, sizeBytes) of all .gguf files in the repo (recursive).
    /// HF's /tree/ endpoint returns file metadata including a 'size' field.
    /// Empty list = no GGUF found.
    /// </summary>
    public async Task<List<(string filename, long sizeBytes)>> GetGgufFilesAsync(string id, CancellationToken ct = default)
    {
        var files = new List<(string, long)>();
        try
        {
            using var resp = await _http.GetAsync($"{BaseUrl}/models/{id}/tree/main?recursive=true", ct);
            if (!resp.IsSuccessStatusCode) return files;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var tree = await JsonSerializer.DeserializeAsync<List<TreeEntry>>(stream, JsonOpts, ct);
            if (tree == null) return files;
            foreach (var e in tree)
            {
                if (e.Type == "file" && !string.IsNullOrEmpty(e.Path)
                    && e.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase)
                    && e.Size > 0)
                {
                    files.Add((e.Path, e.Size));
                }
            }
        }
        catch { /* silent - caller shows "-" */ }
        return files;
    }

    /// <summary>Fetches the raw README.md for a model. Returns null if missing/unreachable.
    /// Used to extract developer-recommended sampling/context settings.</summary>
    public async Task<string?> GetReadmeAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://huggingface.co/{id}/raw/main/README.md", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    /// <summary>Fetches the raw tokenizer_config.json - its chat_template field can be scanned for
    /// tool-calling support before pulling the model.</summary>
    public async Task<string?> GetTokenizerConfigAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://huggingface.co/{id}/raw/main/tokenizer_config.json", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch { return null; }
    }

    private class TreeEntry
    {
        [JsonPropertyName("type")] public string? Type { get; set; }
        [JsonPropertyName("path")] public string? Path { get; set; }
        [JsonPropertyName("size")] public long Size { get; set; }
    }

    /// <summary>Heuristic: derive a likely Ollama tag from an HF model id. Often wrong - user must verify.
    /// Pattern observed: HF "huihui-ai/Qwen2.5-Coder-32B-Instruct-abliterated" ≈ Ollama "huihui_ai/qwen2.5-coder-abliterate:32b".
    /// We can't reliably guess the size suffix, so we just convert org dashes to underscores and lowercase the repo.</summary>
    public static string SuggestOllamaTag(string hfId)
    {
        var slash = hfId.IndexOf('/');
        if (slash <= 0) return hfId.ToLowerInvariant();
        var org = hfId[..slash].Replace('-', '_');
        var repo = hfId[(slash + 1)..].ToLowerInvariant();
        return $"{org}/{repo}";
    }

    /// <summary>
    /// Constructs Ollama's direct-from-HuggingFace pull tag. Ollama 0.3+ supports pulling GGUF files
    /// straight from any HF repo via the hf.co/ prefix, bypassing the curated Ollama registry.
    /// Format: "hf.co/{user}/{repo}:{quant}" - e.g. "hf.co/bartowski/Qwen2.5-Coder-32B-Instruct-GGUF:Q4_K_M".
    /// Pass quantLabel="" to omit the quant tag (Ollama picks default, usually Q4_K_M).
    /// </summary>
    public static string BuildHfDirectTag(string hfId, string quantLabel)
    {
        return string.IsNullOrEmpty(quantLabel) ? $"hf.co/{hfId}" : $"hf.co/{hfId}:{quantLabel}";
    }
}
