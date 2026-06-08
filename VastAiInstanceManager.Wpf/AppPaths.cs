namespace VastAiInstanceManager;

/// <summary>Centralized file/directory paths. Mirrors the PowerShell script layout
/// so both can coexist and read each other's config.</summary>
public static class AppPaths
{
    public static string UserProfile =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string VastConfigDir => Path.Combine(UserProfile, ".config", "vast-ai-instance-manager");
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
        Directory.CreateDirectory(VastConfigDir);
        Directory.CreateDirectory(EphemeralKeysDir);
        Directory.CreateDirectory(LogsDir);
    }
}
