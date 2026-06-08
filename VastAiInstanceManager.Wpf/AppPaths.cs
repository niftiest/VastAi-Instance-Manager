namespace VastAiInstanceManager;

/// <summary>Centralized file/directory paths. Mirrors the PowerShell script layout
/// so both can coexist and read each other's config.</summary>
public static class AppPaths
{
    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>App data dir at ~/.config/vast-ai-instance-manager/. EnsureAll() one-time-migrates
    /// from the legacy ~/.config/vast-spinup/ path so users carry their favorites, host stats,
    /// notes, and model registry across the rename.</summary>
    public static string VastConfigDir => Path.Combine(UserProfile, ".config", "vast-ai-instance-manager");

    /// <summary>Legacy path. Migrated into VastConfigDir on first launch, then ignored.</summary>
    public static string LegacyVastConfigDir => Path.Combine(UserProfile, ".config", "vast-spinup");
    public static string ModelsFile    => Path.Combine(VastConfigDir, "models.json");
    public static string FavoritesFile => Path.Combine(VastConfigDir, "favorites.json");
    public static string NotesFile     => Path.Combine(VastConfigDir, "host-notes.json");
    public static string HostStatsFile => Path.Combine(VastConfigDir, "host-stats.json");
    public static string TemplateHashFile => Path.Combine(VastConfigDir, "ollama-template-hash.json");
    public static string LastInstanceFile => Path.Combine(VastConfigDir, "last-instance.json");
    public static string WindowStateFile => Path.Combine(VastConfigDir, "window.json");
    public static string EphemeralKeysDir => Path.Combine(VastConfigDir, "ephemeral");
    public static string LogsDir       => Path.Combine(VastConfigDir, "logs");
    public static string AppLogFile    => Path.Combine(LogsDir, "app.log");

    public static string OpencodeDir   => Path.Combine(UserProfile, ".config", "opencode");
    public static string OpencodeRemoteJson => Path.Combine(OpencodeDir, "opencode-remote.json");
    public static string EnsureRemotePs1    => Path.Combine(OpencodeDir, "ensure-remote.ps1");

    public static string VastApiKeyFile => Path.Combine(UserProfile, ".config", "vastai", "vast_api_key");

    public static void EnsureAll()
    {
        // One-time migration: if the new dir doesn't exist but the legacy one does, move it across
        // so users keep their favorites / host stats / notes / models / window placement.
        if (!Directory.Exists(VastConfigDir) && Directory.Exists(LegacyVastConfigDir))
        {
            try { Directory.Move(LegacyVastConfigDir, VastConfigDir); }
            catch
            {
                // Move can fail across volumes or if files are locked. Fall back to a recursive copy
                // (and the legacy dir stays in place for the user to clean up manually).
                Directory.CreateDirectory(VastConfigDir);
                foreach (var src in Directory.EnumerateFileSystemEntries(LegacyVastConfigDir, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(LegacyVastConfigDir, src);
                    var dst = Path.Combine(VastConfigDir, rel);
                    try
                    {
                        if (Directory.Exists(src)) Directory.CreateDirectory(dst);
                        else
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                            if (!File.Exists(dst)) File.Copy(src, dst);
                        }
                    }
                    catch { /* best effort per-file; logged elsewhere if it matters */ }
                }
            }
        }

        Directory.CreateDirectory(VastConfigDir);
        Directory.CreateDirectory(EphemeralKeysDir);
        Directory.CreateDirectory(LogsDir);
    }
}
