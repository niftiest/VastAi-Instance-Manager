using System.Diagnostics;

namespace VastAiInstanceManager.Services;

/// <summary>Generates a fresh ed25519 SSH keypair per-instance using ssh-keygen.</summary>
public static class EphemeralKeyService
{
    /// <summary>Returns (privateKeyPath, publicKey) or null on failure.</summary>
    public static (string PrivateKeyPath, string PublicKey)? Generate(long instanceId)
    {
        AppPaths.EnsureAll();
        var keyDir = Path.Combine(AppPaths.EphemeralKeysDir, instanceId.ToString());
        if (Directory.Exists(keyDir)) Directory.Delete(keyDir, true);
        Directory.CreateDirectory(keyDir);
        var privKey = Path.Combine(keyDir, "id_ed25519");

        var psi = new ProcessStartInfo
        {
            FileName = "ssh-keygen",
            Arguments = $"-t ed25519 -N \"\" -f \"{privKey}\" -C \"vast-spinup-{instanceId}\" -q",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            p.WaitForExit(15_000);
            var pubPath = privKey + ".pub";
            if (!File.Exists(pubPath)) return null;
            var pubKey = File.ReadAllText(pubPath).Trim();
            return (privKey, pubKey);
        }
        catch
        {
            return null;
        }
    }

    public static void Cleanup(long instanceId)
    {
        var keyDir = Path.Combine(AppPaths.EphemeralKeysDir, instanceId.ToString());
        if (Directory.Exists(keyDir))
        {
            try { Directory.Delete(keyDir, true); } catch { }
        }
    }
}
