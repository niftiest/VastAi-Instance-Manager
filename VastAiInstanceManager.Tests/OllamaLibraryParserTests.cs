using VastAiInstanceManager.Services;
using Xunit;

namespace VastAiInstanceManager.Tests;

/// <summary>
/// Regression tests for the brittle ollama.com HTML scraper. When ollama.com restyles their
/// search page the parser breaks silently in production - these tests pin the expected markup
/// so the failure surfaces in CI instead. If a test fails after an ollama.com redesign,
/// inspect the live page, update both the fixture and the regexes in OllamaLibraryParser.
/// </summary>
public class OllamaLibraryParserTests
{
    /// <summary>Hand-crafted fixture mimicking ollama.com/search HTMX-fragment markup. Real markup
    /// has more chrome and class clutter, but the x-test-* attributes are stable selectors.</summary>
    private const string TwoResultsHtml = @"
<ul>
  <li class=""group"" x-test-model data-name=""qwen2.5-coder"">
    <a href=""/library/qwen2.5-coder"" class=""block"">
      <div>
        <span class=""font-semibold"">qwen2.5-coder</span>
        <p class=""max-w-lg text-sm text-gray-600"">Code-specialized Qwen2.5 with strong tool calling.</p>
        <div class=""flex gap-2"">
          <span x-test-capability class=""cap"">tools</span>
          <span x-test-capability class=""cap"">thinking</span>
          <span x-test-capability class=""cap"">tools</span>
          <span x-test-size class=""size"">1.5b</span>
          <span x-test-size class=""size"">7b</span>
          <span x-test-size class=""size"">32b</span>
          <span x-test-pull-count class=""pulls"">2.1M Pulls</span>
        </div>
      </div>
    </a>
  </li>
  <li class=""group"" x-test-model data-name=""llava"">
    <a href=""/library/llava"" class=""block"">
      <div>
        <span class=""font-semibold"">llava</span>
        <p class=""max-w-lg text-sm text-gray-600"">Vision-language model. Description &amp; details.</p>
        <div class=""flex gap-2"">
          <span x-test-capability class=""cap"">completion</span>
          <span x-test-capability class=""cap"">vision</span>
          <span x-test-size class=""size"">7b</span>
          <span x-test-pull-count class=""pulls"">800K Pulls</span>
        </div>
      </div>
    </a>
  </li>
</ul>";

    [Fact]
    public void ParseSearchNames_extracts_unique_library_slugs()
    {
        var names = OllamaLibraryParser.ParseSearchNames(TwoResultsHtml);
        Assert.Contains("qwen2.5-coder", names);
        Assert.Contains("llava", names);
        Assert.Equal(2, names.Count);
    }

    [Fact]
    public void ParseSearchNames_handles_empty_and_null()
    {
        Assert.Empty(OllamaLibraryParser.ParseSearchNames(null));
        Assert.Empty(OllamaLibraryParser.ParseSearchNames(""));
        Assert.Empty(OllamaLibraryParser.ParseSearchNames("<html><body>no library hrefs</body></html>"));
    }

    [Fact]
    public void ParseSearchNames_is_case_insensitive_for_dedup()
    {
        // Hypothetical: same slug appearing in href and inside body, different case.
        var html = @"<a href=""/library/Qwen2.5-Coder""></a><a href=""/library/qwen2.5-coder""></a>";
        var names = OllamaLibraryParser.ParseSearchNames(html);
        Assert.Single(names);
    }

    [Fact]
    public void ParseSearchResults_extracts_two_results_with_fields()
    {
        var results = OllamaLibraryParser.ParseSearchResults(TwoResultsHtml);
        Assert.Equal(2, results.Count);

        var qwen = results[0];
        Assert.Equal("qwen2.5-coder", qwen.Name);
        Assert.Equal("Code-specialized Qwen2.5 with strong tool calling.", qwen.Description);
        Assert.Equal(new[] { "tools", "thinking" }, qwen.Capabilities);  // dedup'd, order preserved
        Assert.Equal(new[] { "1.5b", "7b", "32b" }, qwen.Sizes);
        Assert.Equal("2.1M Pulls", qwen.PullsDisplay);

        var llava = results[1];
        Assert.Equal("llava", llava.Name);
        Assert.True(llava.HasVision);
        Assert.False(llava.HasTools);
        Assert.Equal(new[] { "7b" }, llava.Sizes);
        Assert.Equal("800K Pulls", llava.PullsDisplay);
    }

    [Fact]
    public void ParseSearchResults_decodes_html_entities_in_description()
    {
        var results = OllamaLibraryParser.ParseSearchResults(TwoResultsHtml);
        Assert.Equal("Vision-language model. Description & details.", results[1].Description);
    }

    [Fact]
    public void ParseSearchResults_empty_or_chrome_only_returns_empty()
    {
        Assert.Empty(OllamaLibraryParser.ParseSearchResults(null));
        Assert.Empty(OllamaLibraryParser.ParseSearchResults(""));
        Assert.Empty(OllamaLibraryParser.ParseSearchResults("<html><body>no results</body></html>"));
    }

    [Fact]
    public void ParseSearchResults_skips_block_without_library_href()
    {
        // Defensive: if a card is missing its anchor (markup change in flight), skip it
        // rather than emitting a result with an empty Name.
        var html = @"
<li x-test-model>
  <p class=""max-w-lg foo"">Card without an /library/ href</p>
</li>
<li x-test-model>
  <a href=""/library/good""></a>
  <p class=""max-w-lg foo"">good one</p>
</li>";
        var results = OllamaLibraryParser.ParseSearchResults(html);
        Assert.Single(results);
        Assert.Equal("good", results[0].Name);
    }
}
