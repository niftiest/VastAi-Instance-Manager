namespace VastSpinup.Services;

/// <summary>
/// Derives a compact modality label for a model row in the HF search dialog. Helps users avoid
/// picking multimodal models when they want text-only (e.g. for OpenCode), and vice versa.
/// </summary>
public static class ModalityHelper
{
    public const string Text = "text";
    public const string Vision = "+vis";
    public const string Audio = "+aud";
    public const string VisionAndAudio = "+v/a";
    public const string Multi = "multi";
    public const string Unknown = "";

    /// <summary>HF API returns pipeline_tag + a tags list. Both are used because GGUF quant repos
    /// often inherit pipeline_tag="text-generation" from a non-multimodal template even when the
    /// base model is multimodal — the tags list catches what pipeline_tag misses.</summary>
    public static string FromHfModel(HuggingFaceModel m)
    {
        var pt = (m.PipelineTag ?? "").ToLowerInvariant();
        if (pt == "any-to-any") return Multi;
        if (pt == "image-text-to-text" || pt == "image-to-text" || pt == "visual-question-answering")
            return Vision;
        if (pt == "automatic-speech-recognition" || pt == "audio-text-to-text" || pt == "audio-classification")
            return Audio;

        bool hasVision = false, hasAudio = false;
        if (m.Tags != null)
        {
            foreach (var t in m.Tags)
            {
                var tt = t.ToLowerInvariant();
                if (!hasVision && (tt.Contains("vision") || tt.Contains("vlm")
                    || tt.Contains("mllm") || tt.Contains("multimodal")
                    || tt.Contains("image-text") || tt.Contains("clip")))
                    hasVision = true;
                if (!hasAudio && (tt.Contains("audio") || tt.Contains("whisper")
                    || tt.Contains("speech") || tt.Contains("asr")))
                    hasAudio = true;
            }
        }
        if (hasVision && hasAudio) return VisionAndAudio;
        if (hasVision) return Vision;
        if (hasAudio) return Audio;
        // pipeline_tag explicitly says text → confident text-only. Otherwise unknown.
        if (pt == "text-generation" || pt == "text2text-generation" || pt == "conversational") return Text;
        return Unknown;
    }

    /// <summary>Ollama-library results carry an authoritative Capabilities list — no guessing.</summary>
    public static string FromOllamaResult(OllamaSearchResult r)
    {
        bool hasVision = r.Capabilities.Any(c => c.Equals("vision", StringComparison.OrdinalIgnoreCase));
        bool hasAudio  = r.Capabilities.Any(c => c.Equals("audio", StringComparison.OrdinalIgnoreCase));
        if (hasVision && hasAudio) return VisionAndAudio;
        if (hasVision) return Vision;
        if (hasAudio) return Audio;
        return Text;
    }
}
