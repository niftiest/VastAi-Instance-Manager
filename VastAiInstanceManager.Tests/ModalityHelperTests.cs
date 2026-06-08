using VastAiInstanceManager.Services;
using Xunit;

namespace VastAiInstanceManager.Tests;

public class ModalityHelperTests
{
    [Fact]
    public void Empty_returns_unknown()
    {
        var m = new HuggingFaceModel { Id = "x" };
        Assert.Equal(ModalityHelper.Unknown, ModalityHelper.FromHfModel(m));
    }

    [Theory]
    [InlineData("text-generation", "text")]
    [InlineData("text2text-generation", "text")]
    [InlineData("conversational", "text")]
    public void Pipeline_tag_text_is_text(string pt, string expected)
    {
        var m = new HuggingFaceModel { Id = "x", PipelineTag = pt };
        Assert.Equal(expected, ModalityHelper.FromHfModel(m));
    }

    [Theory]
    [InlineData("image-text-to-text", "+vis")]
    [InlineData("image-to-text", "+vis")]
    [InlineData("visual-question-answering", "+vis")]
    public void Pipeline_tag_vision_is_vis(string pt, string expected)
    {
        var m = new HuggingFaceModel { Id = "x", PipelineTag = pt };
        Assert.Equal(expected, ModalityHelper.FromHfModel(m));
    }

    [Theory]
    [InlineData("automatic-speech-recognition", "+aud")]
    [InlineData("audio-text-to-text", "+aud")]
    [InlineData("audio-classification", "+aud")]
    public void Pipeline_tag_audio_is_aud(string pt, string expected)
    {
        var m = new HuggingFaceModel { Id = "x", PipelineTag = pt };
        Assert.Equal(expected, ModalityHelper.FromHfModel(m));
    }

    [Fact]
    public void Pipeline_tag_any_to_any_is_multi()
    {
        var m = new HuggingFaceModel { Id = "x", PipelineTag = "any-to-any" };
        Assert.Equal(ModalityHelper.Multi, ModalityHelper.FromHfModel(m));
    }

    [Fact]
    public void Tags_pick_up_vision_when_pipeline_tag_is_generic()
    {
        // The exact scenario from the conversation: unsloth quant repos inherit pipeline_tag=text-generation
        // from a non-multimodal template even though the base is multimodal. Tags catch this.
        var m = new HuggingFaceModel
        {
            Id = "unsloth/gemma-4-12b-it-GGUF",
            PipelineTag = "text-generation",
            Tags = new List<string> { "gguf", "vision", "vlm", "multimodal" },
        };
        Assert.Equal(ModalityHelper.Vision, ModalityHelper.FromHfModel(m));
    }

    [Fact]
    public void Vision_and_audio_tags_combine_to_v_slash_a()
    {
        var m = new HuggingFaceModel
        {
            Id = "x",
            Tags = new List<string> { "vision", "audio" },
        };
        Assert.Equal(ModalityHelper.VisionAndAudio, ModalityHelper.FromHfModel(m));
    }

    [Fact]
    public void Ollama_result_uses_capabilities_directly()
    {
        var r = new OllamaSearchResult
        {
            Name = "llava",
            Capabilities = new List<string> { "completion", "vision" },
        };
        Assert.Equal(ModalityHelper.Vision, ModalityHelper.FromOllamaResult(r));
    }

    [Fact]
    public void Ollama_text_only_default()
    {
        var r = new OllamaSearchResult
        {
            Name = "qwen2.5",
            Capabilities = new List<string> { "completion", "tools" },
        };
        Assert.Equal(ModalityHelper.Text, ModalityHelper.FromOllamaResult(r));
    }
}
