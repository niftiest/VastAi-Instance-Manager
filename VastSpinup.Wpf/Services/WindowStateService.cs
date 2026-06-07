using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;

namespace VastSpinup.Services;

/// <summary>Persisted main-window placement so the app reopens where the user left it.</summary>
public class SavedWindowState
{
    [JsonPropertyName("left")] public double Left { get; set; } = double.NaN;
    [JsonPropertyName("top")] public double Top { get; set; } = double.NaN;
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    [JsonPropertyName("is_maximized")] public bool IsMaximized { get; set; }
}

public static class WindowStateService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static SavedWindowState? Load()
    {
        return AtomicJson.Read<SavedWindowState>(
            AppPaths.WindowStateFile,
            onError: (p, ex) => AppLog.Warn($"Failed to read window state '{p}'.", ex));
    }

    public static void Save(SavedWindowState state)
    {
        AppPaths.EnsureAll();
        AtomicJson.Write(
            AppPaths.WindowStateFile,
            state,
            JsonOpts,
            (p, ex) => AppLog.Warn($"Failed to write window state '{p}'.", ex));
    }

    /// <summary>True if (Left, Top, Width, Height) overlaps the virtual screen by at least
    /// 100x100 px. Used to discard saved coords after a monitor disconnect, so the window
    /// doesn't open off-screen and look lost.</summary>
    public static bool IsOnScreen(double left, double top, double width, double height)
    {
        if (double.IsNaN(left) || double.IsNaN(top) || width <= 0 || height <= 0) return false;
        // SystemParameters.VirtualScreen* is the union bounding box of every monitor.
        var virt = new Rect(
            SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenHeight);
        var win = new Rect(left, top, width, height);
        win.Intersect(virt);
        return win.Width >= 100 && win.Height >= 100;
    }
}
