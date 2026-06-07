using System.Net;
using System.Text.RegularExpressions;

namespace VastSpinup.Services;

/// <summary>
/// Pure HTML→object parsing for ollama.com search results. Lifted out of OllamaApi so the
/// fragile regex selectors can be exhaustively tested against fixtures. When ollama.com
/// changes their markup, these tests should fail before users do.
///
/// Each method takes raw HTML and is allocation-light: regexes are pre-compiled statics.
/// </summary>
public static class OllamaLibraryParser
{
    // Each result has href="/library/{name}" - used by the lightweight name-only search
    // (e.g. for cross-referencing HF rows with the "tools" capability filter).
    private static readonly Regex LibraryHref = new(
        @"href=""/library/([a-z0-9._\-]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Each full result is wrapped in <li ... x-test-model ...>; split on it to get per-card blocks.
    private static readonly Regex CardSplit = new(
        @"<li[^>]*x-test-model[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Within one card:
    private static readonly Regex NameInCard = new(
        @"href=""/library/([a-z0-9._\-/]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DescInCard = new(
        @"<p class=""max-w-lg[^""]*"">([^<]+)</p>",
        RegexOptions.Compiled);
    private static readonly Regex CapInCard = new(
        @"<span x-test-capability[^>]*>([^<]+)</span>",
        RegexOptions.Compiled);
    private static readonly Regex SizeInCard = new(
        @"<span x-test-size[^>]*>([^<]+)</span>",
        RegexOptions.Compiled);
    private static readonly Regex PullsInCard = new(
        @"<span x-test-pull-count[^>]*>([^<]+)</span>",
        RegexOptions.Compiled);

    /// <summary>Extracts unique library names from search HTML.
    /// Used to cross-reference HF results with Ollama's "tools" capability filter.</summary>
    public static HashSet<string> ParseSearchNames(string? html)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(html)) return names;
        foreach (Match m in LibraryHref.Matches(html))
            names.Add(m.Groups[1].Value);
        return names;
    }

    /// <summary>Extracts full per-result records from search HTML.
    /// Each result's Name is the exact pullable tag - no heuristic mapping needed.</summary>
    public static List<OllamaSearchResult> ParseSearchResults(string? html)
    {
        var results = new List<OllamaSearchResult>();
        if (string.IsNullOrEmpty(html)) return results;

        // Split on each result's <li x-test-model ...> marker, then mine the blocks.
        // The first chunk is page chrome before any result, so drop it.
        var blocks = CardSplit.Split(html).Skip(1);
        foreach (var block in blocks)
        {
            var nameMatch = NameInCard.Match(block);
            if (!nameMatch.Success) continue;
            var name = nameMatch.Groups[1].Value;

            var descMatch = DescInCard.Match(block);
            var desc = descMatch.Success ? WebUtility.HtmlDecode(descMatch.Groups[1].Value).Trim() : "";

            var caps = CapInCard.Matches(block)
                .Select(m => m.Groups[1].Value.Trim()).Distinct().ToList();
            var sizes = SizeInCard.Matches(block)
                .Select(m => m.Groups[1].Value.Trim()).ToList();
            var pullsMatch = PullsInCard.Match(block);
            var pulls = pullsMatch.Success ? pullsMatch.Groups[1].Value.Trim() : "";

            results.Add(new OllamaSearchResult
            {
                Name = name,
                Description = desc,
                Capabilities = caps,
                Sizes = sizes,
                PullsDisplay = pulls,
            });
        }
        return results;
    }
}
