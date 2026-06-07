using System.Text.Json;
using System.Text.Json.Serialization;

namespace VastSpinup.Services;

/// <summary>
/// Persists the vast.ai template_hash for our Ollama spinup template across sessions.
/// Once we successfully spin up an instance, vast.ai assigns a template_hash derived from
/// (image + onstart + env + ports). Future searches filter by this hash to identify
/// hosts that have already cached our docker image — dramatically faster boots.
/// </summary>
public class TemplateHashService
{
    private string? _hash;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public string? Hash => _hash;
    public bool HasHash => !string.IsNullOrEmpty(_hash);

    public void Load()
    {
        var doc = AtomicJson.Read<HashRecord>(
            AppPaths.TemplateHashFile,
            onError: (p, ex) => AppLog.Warn($"Failed to read template-hash file '{p}'.", ex));
        _hash = doc?.Hash;
    }

    public void Save(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash) || hash == _hash) return;
        _hash = hash;
        AppPaths.EnsureAll();
        AtomicJson.Write(
            AppPaths.TemplateHashFile,
            new HashRecord { Hash = hash, CapturedAt = DateTime.UtcNow },
            JsonOpts,
            (p, ex) => AppLog.Warn($"Failed to write template-hash file '{p}'.", ex));
    }

    private class HashRecord
    {
        [JsonPropertyName("hash")] public string? Hash { get; set; }
        [JsonPropertyName("captured_at")] public DateTime CapturedAt { get; set; }
    }
}
