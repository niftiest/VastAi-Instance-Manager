using System.Text.Json;

namespace VastSpinup.Services;

/// <summary>
/// Atomic JSON read/write helpers. Writes go to a sibling .tmp file then are renamed over the
/// target so a crash or kill mid-write can't truncate the real file. A per-path lock prevents
/// two threads in the same process from racing each other; cross-process safety relies on the
/// OS rename semantics (atomic on NTFS).
/// </summary>
public static class AtomicJson
{
    private static readonly Dictionary<string, object> _locks = new(StringComparer.OrdinalIgnoreCase);

    private static object LockFor(string path)
    {
        lock (_locks)
        {
            if (!_locks.TryGetValue(path, out var l)) { l = new object(); _locks[path] = l; }
            return l;
        }
    }

    /// <summary>Reads + deserializes the JSON file. Returns default(T) on missing/corrupt file
    /// and surfaces the failure through the optional onError callback so callers can log it
    /// instead of silently swallowing.</summary>
    public static T? Read<T>(string path, JsonSerializerOptions? opts = null, Action<string, Exception>? onError = null)
    {
        lock (LockFor(path))
        {
            try
            {
                if (!File.Exists(path)) return default;
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<T>(json, opts);
            }
            catch (Exception ex)
            {
                onError?.Invoke(path, ex);
                return default;
            }
        }
    }

    /// <summary>Atomic write: serialize → tmp file → File.Move with overwrite. Returns false
    /// and reports via onError if the write fails for any reason (disk full, perms, etc.).</summary>
    public static bool Write<T>(string path, T value, JsonSerializerOptions? opts = null, Action<string, Exception>? onError = null)
    {
        lock (LockFor(path))
        {
            var tmp = path + ".tmp";
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(value, opts);
                File.WriteAllText(tmp, json);
                // Move with overwrite: atomic on NTFS, replaces the target in one syscall.
                File.Move(tmp, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                onError?.Invoke(path, ex);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leftover tmp file is harmless */ }
                return false;
            }
        }
    }
}
