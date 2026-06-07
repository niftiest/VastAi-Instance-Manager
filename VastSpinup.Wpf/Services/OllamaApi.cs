using System.Net.Http.Json;
using System.Text.Json;

namespace VastSpinup.Services;

public class OllamaApi
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public OllamaApi(string baseUrl, HttpClient? http = null)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{_baseUrl}/api/version", ct);
            if (!resp.IsSuccessStatusCode) return null;
            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch { return null; }
    }

    public async Task<List<string>> ListModelsAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{_baseUrl}/api/tags", ct);
        resp.EnsureSuccessStatusCode();
        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        var names = new List<string>();
        if (doc.TryGetProperty("models", out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var m in arr.EnumerateArray())
                if (m.TryGetProperty("name", out var n)) names.Add(n.GetString() ?? "");
        return names;
    }

    /// <summary>
    /// Forces an unload of the model from VRAM via keep_alive=0. This also releases the file
    /// handles so a subsequent DeleteModelAsync can actually reclaim the disk blobs (otherwise
    /// they linger and "free disk" only happens after Ollama's 5-min idle timeout).
    /// </summary>
    public async Task UnloadModelAsync(string tag, CancellationToken ct = default)
    {
        try
        {
            var body = JsonContent.Create(new { model = tag, keep_alive = 0, prompt = "" });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate") { Content = body };
            using var resp = await _http.SendAsync(req, ct);
            // Best-effort; we don't fail change-model if unload errors. The model might not be loaded.
        }
        catch { /* swallow; unload is best-effort */ }
    }

    /// <summary>
    /// Static: checks whether a model tag exists on the public Ollama registry
    /// (https://registry.ollama.ai). Useful for verifying a suggested tag without actually pulling.
    /// Returns (exists, error, manifest). Existence is determined by whether the manifest GET succeeds.
    /// </summary>
    public static async Task<(bool exists, string? error, JsonElement? manifest)> CheckRegistryTagAsync(string tag, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tag)) return (false, "empty tag", null);
        var (repo, version) = NormalizeTag(tag);
        var url = $"https://registry.ollama.ai/v2/{repo}/manifests/{version}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/vnd.docker.distribution.manifest.v2+json");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                if ((int)resp.StatusCode == 404) return (false, "not found on Ollama registry", null);
                return (false, $"HTTP {(int)resp.StatusCode}", null);
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            var doc = JsonSerializer.Deserialize<JsonElement>(body);
            return (true, null, doc);
        }
        catch (Exception ex) { return (false, ex.Message, null); }
    }

    /// <summary>
    /// Fetches the per-tag "params" blob from the Ollama registry. Returns the parsed JSON
    /// (e.g. {"temperature":0.6,"top_p":0.95,"top_k":40,"repeat_penalty":1,"stop":["..."]}) or null
    /// if the tag has no params layer (model uses Ollama defaults).
    /// </summary>
    public static async Task<JsonElement?> GetRegistryParamsAsync(string tag, CancellationToken ct = default)
    {
        var (exists, _, manifest) = await CheckRegistryTagAsync(tag, ct);
        if (!exists || manifest == null) return null;

        var (repo, _) = NormalizeTag(tag);
        // Find the layer with mediaType "application/vnd.ollama.image.params"
        if (!manifest.Value.TryGetProperty("layers", out var layers) || layers.ValueKind != JsonValueKind.Array)
            return null;
        string? digest = null;
        foreach (var layer in layers.EnumerateArray())
        {
            if (layer.TryGetProperty("mediaType", out var mt)
                && mt.GetString() == "application/vnd.ollama.image.params"
                && layer.TryGetProperty("digest", out var d))
            {
                digest = d.GetString();
                break;
            }
        }
        if (string.IsNullOrEmpty(digest)) return null;

        var url = $"https://registry.ollama.ai/v2/{repo}/blobs/{digest}";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch { return null; }
    }

    /// <summary>
    /// Searches Ollama's curated library via their public search page (HTMX endpoint).
    /// Returns the set of model names matching the query — optionally filtered by capability
    /// (e.g. "tools", "vision", "thinking"). Names returned are bare repo names like "qwen3-coder".
    /// </summary>
    public static async Task<HashSet<string>> SearchLibraryAsync(string query, string? capabilityFilter = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new(StringComparer.OrdinalIgnoreCase);
        try
        {
            var q = Uri.EscapeDataString(query);
            var url = $"https://ollama.com/search?q={q}";
            if (!string.IsNullOrEmpty(capabilityFilter))
                url += $"&c={Uri.EscapeDataString(capabilityFilter)}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("HX-Request", "true");
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"OllamaApi.SearchLibraryAsync: HTTP {(int)resp.StatusCode} for '{url}'.");
                return new(StringComparer.OrdinalIgnoreCase);
            }
            var html = await resp.Content.ReadAsStringAsync(ct);
            return OllamaLibraryParser.ParseSearchNames(html);
        }
        catch (OperationCanceledException) { return new(StringComparer.OrdinalIgnoreCase); }
        catch (Exception ex)
        {
            AppLog.Warn($"OllamaApi.SearchLibraryAsync failed for query='{query}'", ex);
            return new(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Richer Ollama-library search: returns per-result name, description, capabilities, and
    /// available sizes by scraping the search page's HTML. Each result's name IS the exact tag
    /// you'd pass to `ollama pull` — no heuristic mapping needed (unlike the HF→Ollama flow).
    /// </summary>
    public static async Task<List<OllamaSearchResult>> SearchModelsAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new();
        try
        {
            var q = Uri.EscapeDataString(query);
            var url = $"https://ollama.com/search?q={q}";

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Add("HX-Request", "true");
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                AppLog.Warn($"OllamaApi.SearchModelsAsync: HTTP {(int)resp.StatusCode} for '{url}'.");
                return new();
            }
            var html = await resp.Content.ReadAsStringAsync(ct);
            return OllamaLibraryParser.ParseSearchResults(html);
        }
        catch (OperationCanceledException) { return new(); }
        catch (Exception ex)
        {
            AppLog.Warn($"OllamaApi.SearchModelsAsync failed for query='{query}'", ex);
            return new();
        }
    }

    private static (string repo, string version) NormalizeTag(string tag)
    {
        // "huihui_ai/qwen:32b" → ("huihui_ai/qwen", "32b")
        // "llama3.3:70b"      → ("library/llama3.3", "70b")
        // "qwq"               → ("library/qwq", "latest")
        var colon = tag.IndexOf(':');
        string repo = colon > 0 ? tag[..colon] : tag;
        string version = colon > 0 ? tag[(colon + 1)..] : "latest";
        if (!repo.Contains('/')) repo = "library/" + repo;
        return (repo, version);
    }

    /// <summary>
    /// Calls /api/show to get the model's capabilities (e.g. ["completion", "tools", "vision"]).
    /// Returns an empty list on failure. OpenCode refuses tool-calling tasks if "tools" isn't in
    /// this list — so we use this to warn the user post-pull, before they discover it mid-chat.
    /// </summary>
    public async Task<List<string>> GetModelCapabilitiesAsync(string tag, CancellationToken ct = default)
    {
        try
        {
            var body = JsonContent.Create(new { model = tag });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/show") { Content = body };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return new();
            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var caps = new List<string>();
            if (doc.TryGetProperty("capabilities", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in arr.EnumerateArray())
                {
                    var s = c.GetString();
                    if (!string.IsNullOrEmpty(s)) caps.Add(s);
                }
            }
            return caps;
        }
        catch { return new(); }
    }

    /// <summary>Deletes a model from the Ollama daemon's disk. Frees space for a different model.</summary>
    public async Task DeleteModelAsync(string tag, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/delete")
        {
            Content = JsonContent.Create(new { model = tag })
        };
        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException($"Delete failed (HTTP {(int)resp.StatusCode}): {body}");
        }
    }

    /// <summary>
    /// Runs a single-token generation to force llama-server to load the model into VRAM.
    /// Returns null on success; an error string on failure. Catches the common
    /// "CUDA error: device kernel image is invalid" (broken host driver/CUDA combo).
    /// </summary>
    public async Task<string?> SmokeTestAsync(string tag, CancellationToken ct = default)
    {
        try
        {
            var body = JsonContent.Create(new
            {
                model = tag,
                prompt = "hi",
                stream = false,
                options = new { num_predict = 1 }
            });
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/generate") { Content = body };
            // Generous timeout — first inference includes model load into VRAM.
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromMinutes(3));
            using var resp = await _http.SendAsync(req, cts.Token);
            var content = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode) return $"HTTP {(int)resp.StatusCode}: {content}";
            // Ollama returns 200 even on llama-server crashes — error is in body.
            try
            {
                var doc = JsonSerializer.Deserialize<JsonElement>(content);
                if (doc.TryGetProperty("error", out var err))
                    return err.GetString() ?? "unknown error";
            }
            catch { /* not JSON — treat as raw error if no 'response' */ }
            return null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "Timed out waiting for first inference (model load > 3 min).";
        }
        catch (Exception ex) { return ex.Message; }
    }

    /// <summary>Streams pull progress. Each update has (status, totalBytes, completedBytes).</summary>
    public async Task PullModelAsync(
        string tag,
        IProgress<(string status, long? total, long? completed)>? progress,
        CancellationToken ct = default)
    {
        var body = JsonContent.Create(new { model = tag, stream = true });
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/pull") { Content = body };
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement obj;
            try { obj = JsonSerializer.Deserialize<JsonElement>(line); } catch { continue; }
            if (obj.TryGetProperty("error", out var err))
                throw new InvalidOperationException($"Pull error: {err.GetString()}");
            string status = obj.TryGetProperty("status", out var s) ? s.GetString() ?? "" : "";
            long? total = obj.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt64() : null;
            long? done = obj.TryGetProperty("completed", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt64() : null;
            progress?.Report((status, total, done));
            if (status.Equals("success", StringComparison.OrdinalIgnoreCase)) break;
        }
    }
}

/// <summary>One row from Ollama's library search. Name IS the pullable tag (no quant suffix needed
/// for the base; user can append :Nb if they want a specific size).</summary>
public class OllamaSearchResult
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Capabilities { get; set; } = new();
    public List<string> Sizes { get; set; } = new();
    public string PullsDisplay { get; set; } = "";
    public bool HasTools => Capabilities.Any(c => c.Equals("tools", StringComparison.OrdinalIgnoreCase));
    public bool HasVision => Capabilities.Any(c => c.Equals("vision", StringComparison.OrdinalIgnoreCase));
    public bool HasThinking => Capabilities.Any(c => c.Equals("thinking", StringComparison.OrdinalIgnoreCase));
}
