using VastSpinup.Models;
using VastSpinup.Services;

namespace VastSpinup.Wpf.ViewModels;

/// <summary>One row in the offers DataGrid. Plain display strings; rebuilt on each search/refresh.</summary>
public class OfferRowVM
{
    public VastOffer Offer { get; }
    public bool IsFavorite { get; }
    public string FavoriteCell { get; }
    public string Gpu { get; }
    public string Vram { get; }
    public string Location { get; }
    public string Price { get; }
    public string Boot { get; }
    public string Reliability { get; }
    public string Bw { get; }
    public string Tps { get; }
    public string Disk { get; }
    public string Net { get; }
    /// <summary>Three-state: "✓" warm, "✗" known-slow, "" unknown.</summary>
    public string Cached { get; }
    public string? Note { get; }

    public OfferRowVM(VastOffer offer, ModelEntry model, FavoritesService favorites, HostStatsService hostStats, NotesService notes, int slowBootThresholdSec)
    {
        Offer = offer;
        IsFavorite = favorites.IsFavorite(offer.MachineId);
        FavoriteCell = IsFavorite ? "★" : "";
        Gpu = $"{offer.NumGpus}x {offer.GpuName}";
        Vram = $"{offer.TotalVramGb} GB";
        Location = offer.GeolocationDisplay;
        Price = $"${offer.DphTotal:F3}";
        Boot = FormatBootStat(offer.MachineId, hostStats);
        Reliability = $"{offer.Reliability * 100:F1}%";
        Bw = $"{offer.GpuMemBwGbs:F0}";
        Tps = $"~{offer.EstimateTokensPerSec(model.ModelSizeGb)} t/s";
        Disk = $"{offer.DiskSpaceGb:F0} GB";
        Net = $"{offer.InetDownMbps:F0}/{offer.InetUpMbps:F0}";

        var stats = hostStats.Get(offer.MachineId);
        Cached = stats == null ? "" : (stats.AvgBootSec > slowBootThresholdSec ? "✗" : "✓");
        Note = notes.Get(offer.MachineId);
    }

    private static string FormatBootStat(long machineId, HostStatsService hostStats)
    {
        var s = hostStats.Get(machineId);
        if (s == null) return "?";
        int total = (int)s.TotalReadySec;
        return total >= 60 ? $"~{total / 60}m {total % 60}s" : $"~{total}s";
    }
}
