namespace VastSpinup.Services;

/// <summary>
/// Simple file-based diagnostic logger. Replaces the previous "silent catch + UI log only"
/// pattern so post-mortem triage is possible. Writes to ~/.config/vast-spinup/logs/app.log,
/// auto-rotated when it crosses ~2 MB so the file never grows unbounded.
/// </summary>
public static class AppLog
{
    private static readonly object _gate = new();
    private const long RotateBytes = 2 * 1024 * 1024;

    public enum Level { Info, Warn, Error }

    public static void Info(string message) => Write(Level.Info, message, null);
    public static void Warn(string message, Exception? ex = null) => Write(Level.Warn, message, ex);
    public static void Error(string message, Exception? ex = null) => Write(Level.Error, message, ex);

    private static void Write(Level level, string message, Exception? ex)
    {
        try
        {
            var path = AppPaths.AppLogFile;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            lock (_gate)
            {
                Rotate(path);
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                if (ex != null)
                {
                    line += $"  {ex.GetType().Name}: {ex.Message}";
                    // Trim long stack traces to one line of context - full trace is in ex.ToString().
                    var st = ex.StackTrace;
                    if (!string.IsNullOrEmpty(st))
                    {
                        var firstFrame = st.Split('\n', 2)[0].Trim();
                        line += $"  at {firstFrame}";
                    }
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never throw. If we can't write, there's nothing useful to do -
            // surfacing a dialog or popup here would just confuse the user.
        }
    }

    private static void Rotate(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var info = new FileInfo(path);
            if (info.Length < RotateBytes) return;
            var rotated = path + ".1";
            if (File.Exists(rotated)) File.Delete(rotated);
            File.Move(path, rotated);
        }
        catch { /* rotation failure is non-fatal */ }
    }
}
