using VastAiInstanceManager.Models;
using VastAiInstanceManager.Wpf.Mvvm;

namespace VastAiInstanceManager.Wpf.ViewModels;

/// <summary>One row in the instances DataGrid. Uptime/Spent are recomputed on the polling tick
/// by rebuilding the displayed values via property setters. Spent shows vast.ai's billed charges
/// when available, else falls back to a "~" estimate from uptime × $/hr.</summary>
public class InstanceRowVM : ObservableObject
{
    public VastInstance Instance { get; }

    public InstanceRowVM(VastInstance instance, string? extraStatus = null, InstanceCharges? charges = null)
    {
        Instance = instance;
        _extraStatus = extraStatus;
        _charges = charges;
        Refresh();
    }

    public long Id => Instance.Id;
    public string Gpu => Instance.GpuDisplay;
    public string Status => Instance.ActualStatus ?? "-";
    public string Price => $"${Instance.DphTotal:F3}";
    public string Endpoint
    {
        get
        {
            var port = Instance.GetExternalPortFor(11434);
            return Instance.PublicIpAddr != null && port != null
                ? $"http://{Instance.PublicIpAddr}:{port}"
                : "-";
        }
    }
    public string Ip => Instance.PublicIpAddr ?? "-";
    public string Ssh => string.IsNullOrEmpty(Instance.SshHost) ? "-" : $"{Instance.SshHost}:{Instance.SshPort}";

    private string _uptime = "-";
    public string Uptime { get => _uptime; set => SetField(ref _uptime, value); }

    private string _spent = "-";
    public string Spent { get => _spent; set => SetField(ref _spent, value); }

    private string _spentDetail = "";
    public string SpentDetail { get => _spentDetail; set => SetField(ref _spentDetail, value); }

    private InstanceCharges? _charges;
    public InstanceCharges? Charges
    {
        get => _charges;
        set { _charges = value; Refresh(); }
    }

    private string? _extraStatus;
    public string Message
    {
        get => _extraStatus ?? Instance.StatusMsg ?? "";
        set { _extraStatus = value; OnPropertyChanged(); }
    }

    /// <summary>Called by the polling tick to recompute uptime/spent in place without rebuilding the row.</summary>
    public void Refresh()
    {
        Uptime = Instance.UptimeDisplay;
        if (_charges != null)
        {
            Spent = $"${_charges.Total:F3}";
            SpentDetail = _charges.Breakdown;
        }
        else
        {
            Spent = Instance.SpentDollars.HasValue ? $"~${Instance.SpentDollars.Value:F3}" : "-";
            SpentDetail = "Estimate (uptime × $/hr) - billed charges not loaded yet.\nExcludes bandwidth; counts image-loading time at the full rate.";
        }
    }
}
