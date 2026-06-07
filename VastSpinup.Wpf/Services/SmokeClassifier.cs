namespace VastSpinup.Services;

/// <summary>
/// Classifies post-pull smoke-test failures returned by Ollama's llama-server stderr.
/// Pure logic — no I/O, no UI — so it can be exhaustively tested as patterns drift
/// across Ollama and llama.cpp versions. Add new patterns here, not in the orchestrator.
/// </summary>
public static class SmokeClassifier
{
    public enum Category
    {
        /// <summary>Smoke succeeded — no error string.</summary>
        Ok,
        /// <summary>Host GPU/driver/CUDA stack is broken. Different host needed.</summary>
        BrokenHost,
        /// <summary>Multimodal projector (vision/audio CLIP) couldn't load on this Ollama version.
        /// Host is fine; text-only models will work.</summary>
        MultimodalIncompatible,
        /// <summary>Generic model/runtime mismatch (tensor shape, unknown architecture, etc.).
        /// Host is fine; a different model may work.</summary>
        BrokenModel,
        /// <summary>Failure didn't match any known pattern.</summary>
        Unknown,
    }

    public static Category Classify(string? smokeErr)
    {
        if (string.IsNullOrEmpty(smokeErr)) return Category.Ok;

        if (Contains(smokeErr,
                "device kernel image is invalid",
                "CUDA error",
                "no CUDA-capable device",
                "CUDA driver version is insufficient"))
            return Category.BrokenHost;

        // Multimodal-specific failures. Checked before generic broken-model so we can
        // surface a distinct "this provider can't do multimodal" message and filter the
        // Change Model dialog accordingly.
        if (Contains(smokeErr,
                "Failed to load CLIP model",
                "Failed to load vision model",
                "mtmd"))
            return Category.MultimodalIncompatible;

        if (Contains(smokeErr,
                "check_tensor_dims",
                "wrong shape",
                "token_embd",
                "unknown model architecture",
                "unsupported file format",
                "unknown pre-tokenizer",
                // Broad backstop: if llama-server itself crashed at load and host CUDA is fine,
                // it's almost always a model/runtime mismatch.
                "llama-server process has terminated"))
            return Category.BrokenModel;

        return Category.Unknown;
    }

    private static bool Contains(string s, params string[] needles)
    {
        foreach (var n in needles)
            if (s.Contains(n, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
