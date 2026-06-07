using System.Text.Json;
using VastSpinup.Models;

namespace VastSpinup.Services;

public class ModelRegistry
{
    public string FilePath { get; }
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public ModelRegistry(string? path = null)
    {
        FilePath = path ?? AppPaths.ModelsFile;
    }

    public Dictionary<string, ModelEntry> Load()
    {
        if (!File.Exists(FilePath))
        {
            SeedDefault();
        }
        var raw = AtomicJson.Read<Dictionary<string, ModelEntry>>(
            FilePath,
            ReadOpts,
            (p, ex) => AppLog.Warn($"Failed to read models file '{p}' — returning empty registry.", ex));
        if (raw == null) return new();
        foreach (var kv in raw) kv.Value.Key = kv.Key;
        return raw;
    }

    public void Save(Dictionary<string, ModelEntry> models)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        AtomicJson.Write(
            FilePath,
            models,
            WriteOpts,
            (p, ex) => AppLog.Error($"Failed to write models file '{p}' — model edits not persisted.", ex));
    }

    /// <summary>Writes the same default model set the PowerShell script seeds.</summary>
    public void SeedDefault()
    {
        AppPaths.EnsureAll();
        var defaults = new Dictionary<string, ModelEntry>
        {
            ["llama-70b"] = new ModelEntry
            {
                Name = "Llama 3.3 70B Abliterated",
                VramGb = 48, DiskGb = 80,
                OllamaTag = "huihui_ai/llama3.3-abliterated",
                NumCtx = 16384, Temperature = 0.7, TopP = 0.9, TopK = 40,
                ModelSizeGb = 42,
                Notes = "Smart 70B with tool calling. Needs ~46GB with Q8 KV cache.",
            },
            ["qwen3-30b"] = new ModelEntry
            {
                Name = "Qwen3 30B-A3B Abliterated (Instruct)",
                VramGb = 24, DiskGb = 35,
                OllamaTag = "huihui_ai/qwen3-abliterated:30b-a3b-instruct-2507-q4_K_M",
                NumCtx = 32768, Temperature = 0.7, TopP = 0.8, TopK = 20,
                ModelSizeGb = 19,
                Notes = "MoE (3B active). Fast, tool-capable, abliterated.",
            },
            ["gemma-27b"] = new ModelEntry
            {
                Name = "Gemma 3 27B Abliterated (Q8)",
                VramGb = 32, DiskGb = 50,
                OllamaTag = "huihui_ai/gemma3-abliterated:27b-q8_0",
                NumCtx = 16384, Temperature = 0.7, TopP = 0.95, TopK = 64,
                ModelSizeGb = 30,
                Notes = "Vision-capable abliterated. Q8 only on Ollama (no Q4 variant).",
            },
            ["qwen-coder-32b"] = new ModelEntry
            {
                Name = "Qwen 2.5 Coder 32B Abliterated",
                VramGb = 24, DiskGb = 35,
                OllamaTag = "huihui_ai/qwen2.5-coder-abliterate:32b",
                NumCtx = 32768, Temperature = 0.3, TopP = 0.8, TopK = 20,
                ModelSizeGb = 19,
                Notes = "Coder-focused. Note repo has typo 'abliterate' (no 'd'). Weak post-tool narration.",
            },
            ["qwq-32b"] = new ModelEntry
            {
                Name = "QwQ 32B Abliterated (reasoning)",
                VramGb = 24, DiskGb = 35,
                OllamaTag = "huihui_ai/qwq-abliterated:32b",
                NumCtx = 32768, Temperature = 0.7, TopP = 0.95, TopK = 40,
                ModelSizeGb = 19,
                Notes = "Reasoning model with thinking mode.",
            },
            ["gpt-oss-20b"] = new ModelEntry
            {
                Name = "GPT-OSS 20B (OpenAI open release)",
                VramGb = 16, DiskGb = 25,
                OllamaTag = "gpt-oss:20b",
                NumCtx = 24576, Temperature = 0.5, TopP = 1.0, TopK = 0,
                ModelSizeGb = 17,
                Notes = "Strong tool use + adaptive reasoning. Cheapest viable remote pick. Not abliterated.",
            },
        };
        Save(defaults);
    }
}
