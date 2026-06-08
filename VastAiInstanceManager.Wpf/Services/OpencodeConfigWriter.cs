using System.Text;
using System.Text.Json;
using VastAiInstanceManager.Models;

namespace VastAiInstanceManager.Services;

/// <summary>Writes opencode-remote.json + ensure-remote.ps1 so the `opencoderemote`
/// launcher (from the PowerShell setup) immediately works against a newly provisioned instance.</summary>
public static class OpencodeConfigWriter
{
    public static void Write(string endpointUrl, ModelEntry model, long instanceId)
    {
        Directory.CreateDirectory(AppPaths.OpencodeDir);

        // Only append :latest when tag has no explicit version.
        var modelRef = model.OllamaTag.Contains(':') ? model.OllamaTag : $"{model.OllamaTag}:latest";

        var config = new Dictionary<string, object?>
        {
            ["$schema"] = "https://opencode.ai/config.json",
            ["model"]   = $"ollama-remote/{modelRef}",
            ["permission"] = new Dictionary<string, object> { ["*"] = "allow" },
            ["provider"] = new Dictionary<string, object?>
            {
                ["ollama-remote"] = new Dictionary<string, object?>
                {
                    ["npm"]     = "@ai-sdk/openai-compatible",
                    ["name"]    = "Ollama Remote (vast.ai)",
                    ["options"] = new Dictionary<string, object> { ["baseURL"] = $"{endpointUrl}/v1" },
                    ["models"]  = new Dictionary<string, object>
                    {
                        [modelRef] = new Dictionary<string, object>
                        {
                            ["name"] = $"{model.Name} (remote vast.ai)",
                            ["options"] = new Dictionary<string, object>
                            {
                                ["num_ctx"]        = model.NumCtx,
                                ["temperature"]    = model.Temperature,
                                ["top_p"]          = model.TopP,
                                ["top_k"]          = model.TopK,
                                ["min_p"]          = 0.0,
                                ["repeat_penalty"] = 1.05,
                            }
                        }
                    }
                }
            },
            ["plugin"] = new[] { "opencode-mem" }
        };

        File.WriteAllText(
            AppPaths.OpencodeRemoteJson,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })
        );

        var ps1 = $@"$ErrorActionPreference = 'SilentlyContinue'

$RemoteUrl = ""{endpointUrl}""
$VastConsoleUrl = ""https://cloud.vast.ai/instances/""

try {{
    $resp = Invoke-RestMethod -Uri ""$RemoteUrl/api/version"" -TimeoutSec 8
    Write-Host ""Remote Ollama ready (vast.ai, version $($resp.version))"" -ForegroundColor Green
    exit 0
}} catch {{
    Write-Host """"
    Write-Host ""=========================================================="" -ForegroundColor Red
    Write-Host ""  Remote Ollama (vast.ai) is NOT reachable at:"" -ForegroundColor Red
    Write-Host ""  $RemoteUrl"" -ForegroundColor Red
    Write-Host ""=========================================================="" -ForegroundColor Red
    Write-Host """"
    Write-Host ""Likely cause: the vast.ai instance is stopped."" -ForegroundColor Yellow
    Write-Host ""Open VastAiInstanceManager.exe to rent a fresh one, OR start instance {instanceId} at:"" -ForegroundColor Yellow
    Write-Host ""  $VastConsoleUrl"" -ForegroundColor Yellow
    Write-Host """"
    exit 1
}}
";
        File.WriteAllText(AppPaths.EnsureRemotePs1, ps1);
    }
}
