using System.Text.Json;

namespace VastSpinup.Services;

/// <summary>
/// Free-form per-host notes keyed by machine_id. Persists across spinups so a note left on
/// an instance ("this one took 3 min to boot - bad cache") shows up in the offers list next
/// time that host appears.
/// </summary>
public class NotesService
{
    private Dictionary<long, string> _notes = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Load()
    {
        var dict = AtomicJson.Read<Dictionary<long, string>>(
            AppPaths.NotesFile,
            onError: (p, ex) => AppLog.Warn($"Failed to read notes file '{p}'.", ex));
        if (dict != null) _notes = dict;
    }

    public void Save()
    {
        AppPaths.EnsureAll();
        var ordered = _notes.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
        AtomicJson.Write(
            AppPaths.NotesFile,
            ordered,
            JsonOpts,
            (p, ex) => AppLog.Warn($"Failed to write notes file '{p}'.", ex));
    }

    public string? Get(long machineId)
        => machineId != 0 && _notes.TryGetValue(machineId, out var s) ? s : null;

    public bool Has(long machineId) => Get(machineId) is not null;

    /// <summary>Sets or replaces the note. Empty/null deletes the entry.</summary>
    public void Set(long machineId, string? note)
    {
        if (machineId == 0) return;
        if (string.IsNullOrWhiteSpace(note))
        {
            _notes.Remove(machineId);
        }
        else
        {
            _notes[machineId] = note.Trim();
        }
        Save();
    }

    public int Count => _notes.Count;
}
