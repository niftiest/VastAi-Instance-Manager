using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VastSpinup.Services;

/// <summary>
/// Polls the GitHub Releases API for a newer tagged release than what this build reports.
/// Hardcoded repo - if you fork, update <see cref="GitHubRepo"/> below.
/// Failures (no network, repo private, rate-limited, malformed response) are silent: an
/// update check is never allowed to interrupt a working app.
/// </summary>
public static class UpdateChecker
{
    /// <summary>"owner/name" of the GitHub repo to poll. UPDATE THIS if you fork.</summary>
    public const string GitHubRepo = "niftiest/VastSpinup";

    public record UpdateInfo(Version Latest, Version Current, string TagName, string HtmlUrl, string ReleaseName);

    /// <summary>Returns an UpdateInfo if a newer release is available, null otherwise.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        var current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0, 0);
        return await CheckAsync(GitHubRepo, current, ct);
    }

    public static async Task<UpdateInfo?> CheckAsync(string ownerRepo, Version current, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"VastSpinup/{current}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var url = $"https://api.github.com/repos/{ownerRepo}/releases/latest";
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: ct);
            if (release == null || string.IsNullOrEmpty(release.TagName)) return null;

            if (!TryParseTag(release.TagName, out var latest)) return null;
            if (latest <= current) return null;

            return new UpdateInfo(latest, current, release.TagName, release.HtmlUrl ?? "", release.Name ?? release.TagName);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            AppLog.Warn("UpdateChecker: failed to query GitHub Releases", ex);
            return null;
        }
    }

    /// <summary>"v0.1.0" / "0.1.0" / "v0.1.0-rc1" -> Version(0,1,0). Trailing -rc / +meta is ignored.</summary>
    public static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        if (string.IsNullOrWhiteSpace(tag)) return false;
        var t = tag.Trim().TrimStart('v', 'V');
        // Strip pre-release / build metadata suffix.
        int cut = t.IndexOfAny(new[] { '-', '+' });
        if (cut > 0) t = t[..cut];
        return Version.TryParse(t, out version!);
    }

    private class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }
}
