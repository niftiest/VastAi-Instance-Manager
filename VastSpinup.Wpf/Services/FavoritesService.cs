using System.Text.Json;

namespace VastSpinup.Services;

/// <summary>
/// Persists a set of "favorited" vast.ai machine IDs (the host's physical box, not the offer ID).
/// Favoriting by machine_id means the star survives when the host repackages its offers.
/// </summary>
public class FavoritesService
{
    private HashSet<long> _favs = new();
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public void Load()
    {
        var ids = AtomicJson.Read<List<long>>(
            AppPaths.FavoritesFile,
            onError: (p, ex) => AppLog.Warn($"Failed to read favorites file '{p}' - starting with empty set.", ex));
        if (ids != null) _favs = new HashSet<long>(ids);
    }

    public void Save()
    {
        AppPaths.EnsureAll();
        AtomicJson.Write(
            AppPaths.FavoritesFile,
            _favs.OrderBy(x => x).ToList(),
            JsonOpts,
            (p, ex) => AppLog.Warn($"Failed to write favorites file '{p}'.", ex));
    }

    public bool IsFavorite(long machineId) => machineId != 0 && _favs.Contains(machineId);

    public bool Toggle(long machineId)
    {
        if (machineId == 0) return false;
        if (!_favs.Add(machineId)) _favs.Remove(machineId);
        Save();
        return _favs.Contains(machineId);
    }

    public int Count => _favs.Count;
}
