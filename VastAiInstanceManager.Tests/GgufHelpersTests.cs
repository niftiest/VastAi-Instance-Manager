using VastAiInstanceManager.Services;
using Xunit;

namespace VastAiInstanceManager.Tests;

public class GgufHelpersTests
{
    private static (string, long) F(string filename, double sizeGb)
        => (filename, (long)(sizeGb * 1_073_741_824));

    [Fact]
    public void Empty_list_returns_null_pick()
    {
        Assert.Null(GgufHelpers.PickRepresentativeFile(new()));
    }

    [Fact]
    public void Prefers_q4_k_m_over_q5()
    {
        var files = new List<(string, long)>
        {
            F("model-q5_0.gguf", 13),
            F("model-q4_K_M.gguf", 11),
            F("model-q8_0.gguf", 20),
        };
        var pick = GgufHelpers.PickRepresentativeFile(files);
        Assert.NotNull(pick);
        Assert.Equal("model-q4_K_M.gguf", pick!.Value.filename);
        Assert.False(pick.Value.isSharded);
    }

    [Fact]
    public void Prefers_non_sharded_q4_over_sharded_q4()
    {
        // The shipping bug we care about: Ollama 0.30 can't pull sharded GGUFs via hf.co/.
        // When both forms exist for the same quant, pick the non-sharded one.
        var files = new List<(string, long)>
        {
            F("model-q4_K_M-00001-of-00010.gguf", 1.5),
            F("model-q4_K_M-00002-of-00010.gguf", 1.5),
            F("model-q4_K_M.gguf", 12),
        };
        var pick = GgufHelpers.PickRepresentativeFile(files);
        Assert.NotNull(pick);
        Assert.False(pick!.Value.isSharded);
        Assert.Equal("model-q4_K_M.gguf", pick.Value.filename);
    }

    [Fact]
    public void Shards_aggregate_into_one_logical_file()
    {
        var files = new List<(string, long)>
        {
            F("big-q4_K_M-00001-of-00003.gguf", 14),
            F("big-q4_K_M-00002-of-00003.gguf", 14),
            F("big-q4_K_M-00003-of-00003.gguf", 14),
        };
        var pick = GgufHelpers.PickRepresentativeFile(files);
        Assert.NotNull(pick);
        Assert.True(pick!.Value.isSharded);
        // The summed size should equal ~42 GB, not just one shard.
        double sumGb = pick.Value.sizeBytes / 1_073_741_824.0;
        Assert.InRange(sumGb, 41.5, 42.5);
    }

    [Fact]
    public void Tiny_imatrix_calibration_files_filtered_out()
    {
        // Files under 0.5 GB are imatrix calibration / metadata, not model weights.
        // They should not be picked even if they match a quant pattern.
        var files = new List<(string, long)>
        {
            F("imatrix-q4_K_M.gguf", 0.05),    // 50 MB - calibration
            F("model-q5_K_M.gguf", 14),        // real weight
        };
        var pick = GgufHelpers.PickRepresentativeFile(files);
        Assert.NotNull(pick);
        Assert.Equal("model-q5_K_M.gguf", pick!.Value.filename);
    }

    [Fact]
    public void Falls_back_to_median_when_no_preferred_quant_matches()
    {
        var files = new List<(string, long)>
        {
            F("model-iq2_xxs.gguf", 5),
            F("model-iq2_xs.gguf",  6),
            F("model-iq3_xs.gguf",  8),
        };
        var pick = GgufHelpers.PickRepresentativeFile(files);
        Assert.NotNull(pick);
        // Median of three sorted by size.
        Assert.Equal("model-iq2_xs.gguf", pick!.Value.filename);
    }

    [Fact]
    public void FormatRecommendedSize_flags_sharded_picks()
    {
        var files = new List<(string, long)>
        {
            F("big-q4_K_M-00001-of-00002.gguf", 30),
            F("big-q4_K_M-00002-of-00002.gguf", 30),
        };
        var s = GgufHelpers.FormatRecommendedSize(files);
        Assert.StartsWith("⚠", s);
        Assert.Contains("sharded", s);
    }

    [Fact]
    public void FormatRecommendedSize_empty_is_dash()
    {
        Assert.Equal("-", GgufHelpers.FormatRecommendedSize(new()));
    }
}
