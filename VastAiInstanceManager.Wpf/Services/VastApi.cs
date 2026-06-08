using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using VastAiInstanceManager.Models;

namespace VastAiInstanceManager.Services;

/// <summary>Thin wrapper around the vast.ai REST API. Bearer auth.</summary>
public class VastApi
{
    public const string BaseUrl = "https://console.vast.ai/api/v0";
    public const string BaseUrlV1 = "https://console.vast.ai/api/v1";
    public const string DefaultImage = "vastai/ollama:0.30.0";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public VastApi(string apiKey, HttpClient? http = null)
    {
        _apiKey = apiKey ?? throw new ArgumentNullException(nameof(apiKey));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public static string? TryLoadDefaultApiKey()
    {
        var candidates = new[]
        {
            AppPaths.VastApiKeyFile,
            Path.Combine(AppPaths.UserProfile, ".vast_api_key"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return File.ReadAllText(c).Trim();
        return null;
    }

    public static void SaveApiKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.VastApiKeyFile)!);
        File.WriteAllText(AppPaths.VastApiKeyFile, key.Trim());
    }

    public async Task<JsonElement> GetCurrentUserAsync(CancellationToken ct = default)
    {
        var resp = await _http.GetAsync($"{BaseUrl}/users/current/", ct);
        await ThrowIfFail(resp);
        return await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), JsonOpts, ct);
    }

    public async Task<List<VastOffer>> SearchOffersAsync(
        int vramGb, int diskGb, double minReliability, double? maxPrice,
        bool requireDirectPorts = true,
        int limit = 50,
        CancellationToken ct = default)
    {
        // NOTE: vast.ai's /bundles/ endpoint does NOT support filtering by template_hash_id -
        // that field is only for instance CREATION, not offer search. Per their docs:
        //   https://docs.vast.ai/api-reference/creating-and-using-templates-with-api
        // For "is this host warm for our image?" we rely on local HostStats (machines we've
        // successfully spun up on) - see MainForm.searchButton_Click's fallback path.
        var body = new Dictionary<string, object?>
        {
            ["verified"]   = new { eq = true },
            ["external"]   = new { eq = false },
            ["rentable"]   = new { eq = true },
            ["gpu_ram"]    = new { gte = vramGb * 1000 },
            ["disk_space"] = new { gte = diskGb },
            ["reliability"] = new { gt = minReliability },
            ["order"]      = new[] { new[] { "dph_total", "asc" } },
            ["type"]       = "ondemand",
            ["limit"]      = limit,
            ["allocated_storage"] = diskGb,
        };
        if (requireDirectPorts) body["direct_port_count"] = new { gt = 0 };
        if (maxPrice.HasValue) body["dph_total"] = new { lte = maxPrice.Value };

        var resp = await _http.PostAsJsonAsync($"{BaseUrl}/bundles/", body, JsonOpts, ct);
        await ThrowIfFail(resp);
        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), JsonOpts, ct);
        if (!doc.TryGetProperty("offers", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return new();
        var list = new List<VastOffer>();
        foreach (var item in arr.EnumerateArray())
        {
            try { var o = item.Deserialize<VastOffer>(JsonOpts); if (o != null) list.Add(o); } catch { }
        }
        return list;
    }

    public async Task<long> CreateInstanceAsync(long offerId, int diskGb, string image, string onStart, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["image"]   = image,
            ["disk"]    = diskGb,
            ["runtype"] = "ssh_direct",
            ["env"] = new Dictionary<string, object>
            {
                ["OLLAMA_HOST"] = "0.0.0.0:11434",
                ["OLLAMA_FLASH_ATTENTION"] = "1",
                ["OLLAMA_KV_CACHE_TYPE"] = "q8_0",
                ["-p 11434:11434"] = true,
            },
            ["onstart"] = onStart,
            ["label"]   = "ollama-spinup",
        };
        var resp = await _http.PutAsJsonAsync($"{BaseUrl}/asks/{offerId}/", body, JsonOpts, ct);
        var content = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            if (content.Contains("no_such_ask", StringComparison.OrdinalIgnoreCase))
                throw new OfferNoLongerAvailableException(offerId);
            throw new VastApiException($"Create failed: HTTP {(int)resp.StatusCode}: {content}");
        }
        var doc = JsonSerializer.Deserialize<JsonElement>(content, JsonOpts);
        if (!doc.TryGetProperty("success", out var s) || !s.GetBoolean())
        {
            if (content.Contains("no_such_ask", StringComparison.OrdinalIgnoreCase))
                throw new OfferNoLongerAvailableException(offerId);
            throw new VastApiException($"Create returned non-success: {content}");
        }
        return doc.GetProperty("new_contract").GetInt64();
    }

    public async Task<VastInstance?> GetInstanceAsync(long instanceId, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"{BaseUrl}/instances/{instanceId}/", ct);
            if ((int)resp.StatusCode == 404) return null;
            await ThrowIfFail(resp);
            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), JsonOpts, ct);
            if (!doc.TryGetProperty("instances", out var inst) || inst.ValueKind != JsonValueKind.Object) return null;
            return inst.Deserialize<VastInstance>(JsonOpts);
        }
        catch (HttpRequestException) { return null; }
    }

    public async Task<List<VastInstance>> ListInstancesAsync(CancellationToken ct = default)
    {
        // Only the LIST endpoint was deprecated to v1; all mutators (start/stop/destroy/ssh-keys) remain on v0.
        var resp = await _http.GetAsync($"{BaseUrlV1}/instances/", ct);
        await ThrowIfFail(resp);
        var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), JsonOpts, ct);
        var list = new List<VastInstance>();
        if (doc.TryGetProperty("instances", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in arr.EnumerateArray())
            {
                try { var v = item.Deserialize<VastInstance>(JsonOpts); if (v != null) list.Add(v); } catch { }
            }
        }
        return list;
    }

    public async Task DestroyInstanceAsync(long instanceId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync($"{BaseUrl}/instances/{instanceId}/", ct);
        if ((int)resp.StatusCode == 404)
            throw new VastApiException($"Destroy failed: instance {instanceId} not found (404). Possibly already destroyed, or wrong endpoint.");
        await ThrowIfFail(resp);
    }

    public async Task StopInstanceAsync(long instanceId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["state"] = "stopped" };
        var resp = await _http.PutAsJsonAsync($"{BaseUrl}/instances/{instanceId}/", body, JsonOpts, ct);
        await ThrowIfFail(resp);
    }

    public async Task StartInstanceAsync(long instanceId, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["state"] = "running" };
        var resp = await _http.PutAsJsonAsync($"{BaseUrl}/instances/{instanceId}/", body, JsonOpts, ct);
        await ThrowIfFail(resp);
    }

    /// <summary>
    /// Fetches public/recommended templates from /api/v0/template/, returning those whose image
    /// matches the given substring. Used to discover the template_hash before our first spinup so
    /// the "Cached" column populates on the very first search.
    /// </summary>
    public async Task<List<JsonElement>> SearchTemplatesAsync(string imageContains, CancellationToken ct = default)
    {
        var matches = new List<JsonElement>();
        try
        {
            var body = new
            {
                order_by = new[] { new { col = "sort_order", dir = "asc" } },
                select_filters = new
                {
                    recommended = new { eq = true },
                    @private = new { eq = false },
                },
            };
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/template/")
            {
                Content = JsonContent.Create(body, options: JsonOpts),
            };
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return matches;
            var doc = await JsonSerializer.DeserializeAsync<JsonElement>(await resp.Content.ReadAsStreamAsync(ct), JsonOpts, ct);
            if (!doc.TryGetProperty("templates", out var arr) || arr.ValueKind != JsonValueKind.Array) return matches;

            foreach (var t in arr.EnumerateArray())
            {
                var img = t.TryGetProperty("image", out var im) ? im.GetString() ?? "" : "";
                if (img.Contains(imageContains, StringComparison.OrdinalIgnoreCase))
                    matches.Add(t.Clone());
            }
        }
        catch { /* best-effort */ }
        return matches;
    }

    /// <summary>Extracts a template_hash value from a template JSON object, trying known field names.</summary>
    public static string? ExtractTemplateHash(JsonElement template)
    {
        foreach (var fieldName in new[] { "hash_id", "template_hash_id", "template_hash", "hash" })
        {
            if (template.TryGetProperty(fieldName, out var v))
            {
                if (v.ValueKind == JsonValueKind.String) return v.GetString();
                if (v.ValueKind == JsonValueKind.Number) return v.GetInt64().ToString();
            }
        }
        return null;
    }

    public async Task<int> AttachSshKeyAsync(long instanceId, string publicKey, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object> { ["ssh_key"] = publicKey };
        // Tight exponential backoff: 500ms, 1s, 2s, 4s, 8s = ~15.5s worst-case (vs 30s+ before).
        int[] backoffMs = { 500, 1000, 2000, 4000, 8000 };
        HttpResponseMessage? resp = null;
        for (int attempt = 0; attempt <= backoffMs.Length; attempt++)
        {
            resp = await _http.PostAsJsonAsync($"{BaseUrl}/instances/{instanceId}/ssh-keys/", body, JsonOpts, ct);
            if (resp.IsSuccessStatusCode) return attempt + 1;
            if ((int)resp.StatusCode != 404 || attempt == backoffMs.Length) break;
            await Task.Delay(backoffMs[attempt], ct);
        }
        await ThrowIfFail(resp!);
        return -1;
    }

    private static async Task ThrowIfFail(HttpResponseMessage resp)
    {
        if (resp.IsSuccessStatusCode) return;
        var body = await resp.Content.ReadAsStringAsync();
        throw new VastApiException($"HTTP {(int)resp.StatusCode}: {body}");
    }
}

public class VastApiException : Exception
{
    public VastApiException(string message) : base(message) { }
}

public class OfferNoLongerAvailableException : VastApiException
{
    public long OfferId { get; }
    public OfferNoLongerAvailableException(long offerId)
        : base($"Offer {offerId} is no longer available (likely rented by someone else).")
    {
        OfferId = offerId;
    }
}
