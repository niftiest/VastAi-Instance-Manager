using VastAiInstanceManager.Services;
using Xunit;

namespace VastAiInstanceManager.Tests;

public class SmokeClassifierTests
{
    [Fact]
    public void NullOrEmpty_isOk()
    {
        Assert.Equal(SmokeClassifier.Category.Ok, SmokeClassifier.Classify(null));
        Assert.Equal(SmokeClassifier.Category.Ok, SmokeClassifier.Classify(""));
    }

    [Theory]
    [InlineData("CUDA error: device kernel image is invalid")]
    [InlineData("no CUDA-capable device detected")]
    [InlineData("CUDA driver version is insufficient for CUDA runtime version")]
    [InlineData("device kernel image is invalid")]
    public void BrokenHost_recognized(string stderr)
        => Assert.Equal(SmokeClassifier.Category.BrokenHost, SmokeClassifier.Classify(stderr));

    [Theory]
    [InlineData("HTTP 500: {\"error\":\"llama-server process has terminated: exit status 1: error: Failed to load CLIP model from /root/.ollama/...\"}")]
    [InlineData("Failed to load vision model")]
    [InlineData("Failed to load CLIP model from /root/.ollama/models/blobs/sha256-91f086971e56...")]
    [InlineData("mtmd: projector failed to load")]
    public void Multimodal_failures_recognized(string stderr)
        => Assert.Equal(SmokeClassifier.Category.MultimodalIncompatible, SmokeClassifier.Classify(stderr));

    [Theory]
    [InlineData("error: check_tensor_dims failed for token_embd.weight")]
    [InlineData("error: tensor token_embd.weight has wrong shape; expected [4096, 32000], got [4096, 32064]")]
    [InlineData("llama: unknown model architecture 'gpt-oss-experimental'")]
    [InlineData("unsupported file format version 4")]
    [InlineData("unknown pre-tokenizer 'tekken-2'")]
    [InlineData("llama-server process has terminated: exit status 1: error: missing tensor")]
    public void BrokenModel_recognized(string stderr)
        => Assert.Equal(SmokeClassifier.Category.BrokenModel, SmokeClassifier.Classify(stderr));

    [Fact]
    public void Multimodal_takes_precedence_over_BrokenModel_pattern()
    {
        // The CLIP-load failure typically comes packaged INSIDE a "llama-server process has terminated"
        // wrapper. Ensure we classify as Multimodal, not generic BrokenModel.
        var both = "HTTP 500: {\"error\":\"llama-server process has terminated: Failed to load CLIP model\"}";
        Assert.Equal(SmokeClassifier.Category.MultimodalIncompatible, SmokeClassifier.Classify(both));
    }

    [Theory]
    [InlineData("totally unknown error gibberish")]
    [InlineData("HTTP 500: {}")]
    [InlineData("connection refused")]
    public void Unknown_falls_through(string stderr)
        => Assert.Equal(SmokeClassifier.Category.Unknown, SmokeClassifier.Classify(stderr));

    [Fact]
    public void Case_insensitive()
    {
        Assert.Equal(SmokeClassifier.Category.BrokenHost, SmokeClassifier.Classify("CUDA ERROR: something"));
        Assert.Equal(SmokeClassifier.Category.MultimodalIncompatible, SmokeClassifier.Classify("failed to load clip model"));
    }
}
