using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using VastAiInstanceManager.Models;
using VastAiInstanceManager.Services;
using VastAiInstanceManager.Wpf.Mvvm;
using VastAiInstanceManager.Wpf.Orchestration;

namespace VastAiInstanceManager.Wpf.ViewModels;

public class MainViewModel : ObservableObject, IOrchestratorHost
{
    public const int SlowBootThresholdSec = 90;

    private readonly ModelRegistry _registry = new();
    private readonly FavoritesService _favorites = new();
    private readonly HostStatsService _hostStats = new();
    private readonly NotesService _notes = new();
    private readonly TemplateHashService _templateHash = new();
    private VastApi? _api;
    private Dictionary<string, ModelEntry> _modelsDict = new();

    private List<VastOffer> _currentOffers = new();
    private readonly HashSet<long> _deadOfferIds = new();
    private readonly HashSet<long> _cachedOfferIds = new();
    /// <summary>Instances whose smoke test indicated they can't load multimodal models
    /// (CLIP/vision projector failures). Used to filter Change Model picks for them.</summary>
    private readonly HashSet<long> _multimodalIncompatibleInstances = new();
    private readonly Dictionary<long, string> _instanceExtraStatus = new();
    private readonly Dictionary<long, CancellationTokenSource> _spinupByInstance = new();
    private readonly List<CancellationTokenSource> _activeSpinups = new();

    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly DispatcherTimer _uptimeTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Dictionary<long, InstanceCharges> _charges = new();
    private readonly DispatcherTimer _balanceTimer = new() { Interval = TimeSpan.FromSeconds(30) };

    public ObservableCollection<ModelEntry> Models { get; } = new();
    public ObservableCollection<OfferRowVM> Offers { get; } = new();
    public ObservableCollection<InstanceRowVM> Instances { get; } = new();

    private ModelEntry? _selectedModel;
    public ModelEntry? SelectedModel
    {
        get => _selectedModel;
        set { if (SetField(ref _selectedModel, value)) _ = OnModelChangedAsync(); }
    }

    private OfferRowVM? _selectedOffer;
    public OfferRowVM? SelectedOffer { get => _selectedOffer; set => SetField(ref _selectedOffer, value); }

    private InstanceRowVM? _selectedInstance;
    public InstanceRowVM? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (SetField(ref _selectedInstance, value))
            {
                OnPropertyChanged(nameof(FavoriteButtonText));
                OnPropertyChanged(nameof(NotesButtonText));
            }
        }
    }

    public string FavoriteButtonText
    {
        get
        {
            var inst = SelectedInstance?.Instance;
            if (inst == null || inst.MachineId == 0) return "★ Favorite";
            return _favorites.IsFavorite(inst.MachineId) ? "✕ Unfavorite" : "★ Favorite";
        }
    }

    public string NotesButtonText
    {
        get
        {
            var inst = SelectedInstance?.Instance;
            if (inst == null || inst.MachineId == 0) return "Add Notes";
            return _notes.Has(inst.MachineId) ? "Edit Notes" : "Add Notes";
        }
    }

    private string _status = "Idle.";
    public string Status { get => _status; set => SetField(ref _status, value); }

    private string _balance = "Balance: -";
    public string Balance { get => _balance; set => SetField(ref _balance, value); }

    private readonly StringBuilder _logBuilder = new();
    private string _log = "";
    public string Log { get => _log; set => SetField(ref _log, value); }

    private double _progress;
    public double Progress { get => _progress; set => SetField(ref _progress, value); }

    private bool _progressVisible;
    public bool ProgressVisible { get => _progressVisible; set => SetField(ref _progressVisible, value); }

    private string _bestPriceText = "BEST PRICE: -";
    public string BestPriceText { get => _bestPriceText; set => SetField(ref _bestPriceText, value); }
    private string _fastestText = "FASTEST: -";
    public string FastestText { get => _fastestText; set => SetField(ref _fastestText, value); }
    private string _bestValueText = "BEST VALUE: -";
    public string BestValueText { get => _bestValueText; set => SetField(ref _bestValueText, value); }

    public VastOffer? BestPriceOffer { get; private set; }
    public VastOffer? FastestOffer { get; private set; }
    public VastOffer? BestValueOffer { get; private set; }

    // Commands
    public ICommand SearchCommand { get; }
    public ICommand RentCommand { get; }
    public ICommand CancelSpinupCommand { get; }
    public ICommand RefreshInstancesCommand { get; }
    public ICommand UseInstanceCommand { get; }
    public ICommand FavoriteInstanceCommand { get; }
    public ICommand NotesInstanceCommand { get; }
    public ICommand ChangeModelCommand { get; }
    public ICommand StartInstanceCommand { get; }
    public ICommand StopInstanceCommand { get; }
    public ICommand DestroyInstanceCommand { get; }
    public ICommand OpenConsoleCommand { get; }
    public ICommand ManageModelsCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand SelectBestPriceCommand { get; }
    public ICommand SelectFastestCommand { get; }
    public ICommand SelectBestValueCommand { get; }

    public event Action<OfferRowVM>? OfferSelectionRequested;

    public MainViewModel()
    {
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => _api != null && SelectedModel != null);
        RentCommand = new AsyncRelayCommand(RentAsync, () => _api != null && SelectedOffer != null && SelectedModel != null);
        CancelSpinupCommand = new RelayCommand(CancelLatestSpinup, () => _activeSpinups.Count > 0);
        RefreshInstancesCommand = new AsyncRelayCommand(() => RefreshInstancesAsync(silent: false));
        UseInstanceCommand = new AsyncRelayCommand(UseInstanceAsync, () => SelectedInstance != null);
        FavoriteInstanceCommand = new RelayCommand(ToggleFavorite, () => SelectedInstance != null);
        NotesInstanceCommand = new RelayCommand(EditNotes, () => SelectedInstance != null);
        ChangeModelCommand = new AsyncRelayCommand(ChangeModelAsync, () => SelectedInstance != null);
        StartInstanceCommand = new AsyncRelayCommand(StartInstanceAsync, () => SelectedInstance != null);
        StopInstanceCommand = new AsyncRelayCommand(StopInstanceAsync, () => SelectedInstance != null);
        DestroyInstanceCommand = new AsyncRelayCommand(DestroyInstanceAsync, () => SelectedInstance != null);
        OpenConsoleCommand = new RelayCommand(() => Process.Start(new ProcessStartInfo("https://cloud.vast.ai/instances/") { UseShellExecute = true }));
        ManageModelsCommand = new RelayCommand(OpenManageModels);
        SettingsCommand = new RelayCommand(OpenSettings);
        SelectBestPriceCommand = new RelayCommand(() => SelectOffer(BestPriceOffer));
        SelectFastestCommand = new RelayCommand(() => SelectOffer(FastestOffer));
        SelectBestValueCommand = new RelayCommand(() => SelectOffer(BestValueOffer));

        _pollTimer.Tick += async (_, _) =>
        {
            try { await RefreshInstancesAsync(silent: true); }
            catch (Exception ex) { AppLog.Warn("Poll-timer RefreshInstances crashed", ex); }
        };
        _uptimeTimer.Tick += (_, _) =>
        {
            try { foreach (var i in Instances) i.Refresh(); }
            catch (Exception ex) { AppLog.Warn("Uptime-timer tick crashed", ex); }
        };
        _balanceTimer.Tick += async (_, _) =>
        {
            try { await UpdateBalanceAsync(); }
            catch (Exception ex) { AppLog.Warn("Balance-timer UpdateBalance crashed", ex); }
            try { await UpdateChargesAsync(); }
            catch (Exception ex) { AppLog.Warn("Balance-timer UpdateCharges crashed", ex); }
        };
    }

    public async Task InitializeAsync()
    {
        AppPaths.EnsureAll();
        _favorites.Load();
        _hostStats.Load();
        _notes.Load();
        _templateHash.Load();
        ReloadModels();
        _uptimeTimer.Start();
        if (!TryConnect())
        {
            // First-run / no-key flow: auto-pop Settings so the user can paste a key without
            // having to hunt for the button. If they save successfully, continue init; if they
            // cancel, the app stays in the "No API key" state and they can open Settings later.
            await PromptForApiKeyAsync();
            if (_api == null) return;
        }
        await CheckAuthAsync();
        await RefreshInstancesAsync(silent: true);
        await UpdateChargesAsync();
        _pollTimer.Start();

        // Background update check - fire and forget. If a newer version exists, prompt the user.
        // Never blocks startup; silent on any error (no network, rate-limited, etc.).
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(8));   // let the main UI settle first
            var info = await UpdateChecker.CheckAsync();
            if (info != null) ShowUpdateAvailable(info);
        });
    }

    private void ShowUpdateAvailable(UpdateChecker.UpdateInfo info)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            var result = MessageBox.Show(
                Application.Current?.MainWindow,
                $"A newer version of VastAiInstanceManager is available.\n\n" +
                $"  Current:  {info.Current}\n" +
                $"  Latest:    {info.Latest}  ({info.ReleaseName})\n\n" +
                "Open the download page?",
                "Vast.ai Instance Manager - update available",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (result == MessageBoxResult.Yes && !string.IsNullOrEmpty(info.HtmlUrl))
            {
                try { Process.Start(new ProcessStartInfo(info.HtmlUrl) { UseShellExecute = true }); }
                catch (Exception ex) { AppLog.Warn("Failed to open release URL", ex); }
            }
        }));
    }

    private Task PromptForApiKeyAsync()
    {
        var dlg = new Dialogs.SettingsDialogWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true) TryConnect();
        return Task.CompletedTask;
    }

    public void ReloadModels()
    {
        var prevKey = SelectedModel?.Key;
        _modelsDict = _registry.Load();
        Models.Clear();
        foreach (var m in _modelsDict.Values) Models.Add(m);
        SelectedModel = Models.FirstOrDefault(m => m.Key == prevKey) ?? Models.FirstOrDefault();
    }

    private bool TryConnect()
    {
        var key = VastApi.TryLoadDefaultApiKey();
        if (string.IsNullOrEmpty(key))
        {
            Status = "No vast.ai API key. Click Settings to add one.";
            _api = null;
            return false;
        }
        _api = new VastApi(key);
        return true;
    }

    private async Task CheckAuthAsync()
    {
        if (_api == null) return;
        try
        {
            var u = await _api.GetCurrentUserAsync();
            var email = u.TryGetProperty("email", out var em) ? em.GetString() : "?";
            var credit = u.TryGetProperty("credit", out var cr) ? cr.GetDouble() : 0;
            Status = $"Connected as {email}";
            Balance = $"Balance: ${credit:F2}";
            if (!_balanceTimer.IsEnabled) _balanceTimer.Start();
        }
        catch (Exception ex)
        {
            Status = "Auth check FAILED - Settings → check key";
            Balance = "Balance: ?";
            AppendLog("Auth error: " + ex.Message);
        }
    }

    private async Task UpdateBalanceAsync()
    {
        if (_api == null) return;
        try
        {
            var u = await _api.GetCurrentUserAsync();
            var credit = u.TryGetProperty("credit", out var cr) ? cr.GetDouble() : 0;
            Balance = $"Balance: ${credit:F2}";
        }
        catch { }
    }

    /// <summary>Pulls exact billed amounts (GPU + disk + bandwidth) for the listed instances so the
    /// Spent column shows what vast.ai actually charged instead of an uptime estimate.</summary>
    private async Task UpdateChargesAsync()
    {
        if (_api == null || Instances.Count == 0) return;
        var oldestStart = Instances
            .Where(r => r.Instance.StartDate is > 0)
            .Select(r => (long)r.Instance.StartDate!.Value)
            .DefaultIfEmpty(DateTimeOffset.UtcNow.ToUnixTimeSeconds())
            .Min();
        try
        {
            _charges = await _api.GetInstanceChargesAsync(DateTimeOffset.FromUnixTimeSeconds(oldestStart));
            foreach (var row in Instances)
            {
                _charges.TryGetValue(row.Id, out var c);
                row.Charges = c;
            }
        }
        catch (Exception ex) { AppLog.Warn("Charges fetch failed", ex); }
    }

    private async Task OnModelChangedAsync()
    {
        if (SelectedModel == null)
        {
            Offers.Clear();
            ResetPicks();
            Status = "Pick a model to load offers.";
            return;
        }
        if (_api != null) await SearchAsync();
    }

    private async Task SearchAsync()
    {
        if (_api == null || SelectedModel == null) return;
        var model = SelectedModel;
        Offers.Clear();
        Status = $"Searching offers (VRAM ≥ {model.VramGb} GB, disk ≥ {model.DiskGb} GB)...";
        try
        {
            _currentOffers = await _api.SearchOffersAsync(model.VramGb, model.DiskGb, model.MinReliability, model.MaxPricePerHour);
            _cachedOfferIds.Clear();
            foreach (var o in _currentOffers)
                if (_hostStats.Get(o.MachineId) != null) _cachedOfferIds.Add(o.Id);
            if (_cachedOfferIds.Count > 0)
                AppendLog($"Cached column: {_cachedOfferIds.Count} of {_currentOffers.Count} offers are on hosts you've used before.");
            int shown = PopulateOffers(_currentOffers, model);
            int dropped = _currentOffers.Count - shown;
            Status = dropped > 0
                ? $"{shown} offers shown (hid {dropped} unsuitable). Select one and Rent."
                : $"{shown} offers. Select one and Rent.";
        }
        catch (Exception ex)
        {
            Status = "Search failed.";
            AppendLog("Search error: " + ex.Message);
        }
    }

    private int PopulateOffers(List<VastOffer> offers, ModelEntry model)
    {
        var fitting = new List<VastOffer>();
        int blacklisted = 0, gpuExcluded = 0;
        foreach (var o in offers)
        {
            if (_deadOfferIds.Contains(o.Id)) { blacklisted++; continue; }
            if (GpuBlocklist.IsExcluded(o.GpuName, model.ExcludedGpuPatterns)) { gpuExcluded++; continue; }
            if (!o.FitsModel(model.ModelSizeGb)) continue;
            fitting.Add(o);
        }
        var ordered = fitting
            .OrderByDescending(o => _favorites.IsFavorite(o.MachineId))
            .ThenByDescending(o => _cachedOfferIds.Contains(o.Id))
            .ThenBy(o => o.DphTotal)
            .ToList();
        Offers.Clear();
        foreach (var o in ordered)
            Offers.Add(new OfferRowVM(o, model, _favorites, _hostStats, _notes, SlowBootThresholdSec));
        UpdatePicks(fitting, model);
        if (blacklisted > 0) AppendLog($"Filtered {blacklisted} previously-failed offer(s) from results.");
        if (gpuExcluded > 0) AppendLog($"Filtered {gpuExcluded} offer(s) with known-incompatible GPUs.");
        return fitting.Count;
    }

    private void UpdatePicks(List<VastOffer> fitting, ModelEntry model)
    {
        if (fitting.Count == 0) { ResetPicks(); return; }
        var picks = OfferRanking.Compute(fitting, model);
        BestPriceOffer = picks.BestPrice;
        FastestOffer = picks.Fastest;
        BestValueOffer = picks.BestValue;
        BestPriceText = picks.BestPrice == null ? "BEST PRICE: -" : $"BEST PRICE: {Fmt(picks.BestPrice, model)}";
        FastestText   = picks.Fastest   == null ? "FASTEST: -"    : $"FASTEST: {Fmt(picks.Fastest, model)}";
        BestValueText = picks.BestValue == null ? "BEST VALUE: -" : $"BEST VALUE: {Fmt(picks.BestValue, model)}";

        static string Fmt(VastOffer o, ModelEntry m)
        {
            var pull = o.EstimatePullCost(m.ModelSizeGb);
            var pullText = pull >= 0.005 ? $" + ${pull:F2} pull" : "";
            return $"{o.NumGpus}x {o.GpuName} - ${o.DphTotal:F3}/hr{pullText} - {o.EstimateTokensPerSec(m.ModelSizeGb)} t/s";
        }
    }

    private void ResetPicks()
    {
        BestPriceOffer = FastestOffer = BestValueOffer = null;
        BestPriceText = "BEST PRICE: -";
        FastestText = "FASTEST: -";
        BestValueText = "BEST VALUE: -";
    }

    private void SelectOffer(VastOffer? offer)
    {
        if (offer == null) return;
        var row = Offers.FirstOrDefault(r => r.Offer.Id == offer.Id);
        if (row != null)
        {
            SelectedOffer = row;
            OfferSelectionRequested?.Invoke(row);
        }
    }

    public async Task RefreshInstancesAsync(bool silent)
    {
        if (_api == null) return;
        if (!silent) Status = "Refreshing instances...";
        try
        {
            var list = await _api.ListInstancesAsync();
            // Preserve selection by id
            var selectedId = SelectedInstance?.Id;
            Instances.Clear();
            foreach (var i in list.OrderByDescending(x => x.Id))
            {
                _instanceExtraStatus.TryGetValue(i.Id, out var extra);
                _charges.TryGetValue(i.Id, out var charges);
                Instances.Add(new InstanceRowVM(i, extra, charges));
            }
            if (selectedId.HasValue)
                SelectedInstance = Instances.FirstOrDefault(r => r.Id == selectedId.Value);
            if (!silent)
            {
                Status = $"Refreshed instances: {Instances.Count} total.";
                AppendLog($"Refreshed instances: {Instances.Count} total.");
            }
        }
        catch (Exception ex)
        {
            Status = "Instance refresh failed.";
            AppendLog("Refresh error: " + ex.Message);
        }
    }

    private void CancelLatestSpinup()
    {
        if (_activeSpinups.Count == 0) return;
        var latest = _activeSpinups[^1];
        latest.Cancel();
        AppendLog($"Cancellation requested for most recent spinup ({_activeSpinups.Count} active total).");
    }

    private void ToggleFavorite()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null || inst.MachineId == 0) return;
        bool nowFav = _favorites.Toggle(inst.MachineId);
        AppendLog(nowFav ? $"★ Favorited machine {inst.MachineId}." : $"Removed favorite for machine {inst.MachineId}.");
        OnPropertyChanged(nameof(FavoriteButtonText));
        if (SelectedModel != null) PopulateOffers(_currentOffers, SelectedModel);
    }

    private void EditNotes()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null) return;
        EditNotesForMachine(inst.MachineId, inst.GpuDisplay);
    }

    /// <summary>Opens the notes dialog for a given offer row (used by double-click on the offers DataGrid).</summary>
    public void EditNotesForOffer(OfferRowVM row)
    {
        if (row == null) return;
        var o = row.Offer;
        EditNotesForMachine(o.MachineId, $"{o.NumGpus}x {o.GpuName}");
    }

    private void EditNotesForMachine(long machineId, string gpuDisplay)
    {
        if (machineId == 0)
        {
            MessageBox.Show("This row has no machine_id from vast.ai - can't attach notes.");
            return;
        }
        var existing = _notes.Get(machineId);
        var dlg = new Dialogs.NotesDialogWindow(machineId, gpuDisplay, existing) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        _notes.Set(machineId, dlg.Result);
        AppendLog(dlg.Result == null
            ? $"Cleared notes for machine {machineId}."
            : $"Saved notes for machine {machineId} ({gpuDisplay}).");
        OnPropertyChanged(nameof(NotesButtonText));
        if (SelectedModel != null) PopulateOffers(_currentOffers, SelectedModel);
    }

    private async Task UseInstanceAsync()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null) return;
        if (SelectedModel == null) { MessageBox.Show("Pick the model loaded on this instance first."); return; }
        var port = inst.GetExternalPortFor(11434);
        if (inst.PublicIpAddr == null || port == null) { MessageBox.Show("Instance has no Ollama port mapping."); return; }
        var url = $"http://{inst.PublicIpAddr}:{port}";
        var ollama = new OllamaApi(url);

        // Pre-flight: confirm the chosen model is actually loaded on this instance. Without this
        // check, a failed pull during the original rent leaves the instance reachable but empty,
        // and opencode hits "invalid model name" on every request.
        Status = "Verifying model is loaded on the instance...";
        List<string> installed;
        try { installed = await ollama.ListModelsAsync(); }
        catch (Exception ex)
        {
            AppLog.Warn("UseInstance: ListModels failed", ex);
            MessageBox.Show(
                $"Could not reach Ollama on this instance:\n\n{ex.Message}\n\nNo config was written.",
                "Vast.ai Instance Manager", MessageBoxButton.OK, MessageBoxImage.Error);
            Status = "Use failed: Ollama unreachable.";
            return;
        }

        var wanted = SelectedModel.OllamaTag.Contains(':') ? SelectedModel.OllamaTag : $"{SelectedModel.OllamaTag}:latest";
        bool present = installed.Any(t =>
            string.Equals(t, wanted, StringComparison.OrdinalIgnoreCase) ||
            // Tolerate ':latest' drift in either direction.
            string.Equals(t.TrimEnd(':', 'l', 'a', 't', 'e', 's', 't'), wanted.TrimEnd(':', 'l', 'a', 't', 'e', 's', 't'), StringComparison.OrdinalIgnoreCase));

        if (!present)
        {
            var installedList = installed.Count == 0 ? "  (nothing)" : "  " + string.Join("\n  ", installed);
            var choice = MessageBox.Show(
                $"'{wanted}' isn't loaded on this instance.\n\nInstalled models:\n{installedList}\n\nPull it now? (will take several minutes)",
                "Model not loaded", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
            if (choice != MessageBoxResult.Yes)
            {
                AppendLog($"Use cancelled: '{wanted}' not on instance.");
                Status = choice == MessageBoxResult.No ? "Use cancelled - pick a different model or pull manually." : "Use cancelled.";
                return;
            }
            // Pull inline. Reuse the rent flow's progress-callback shape so the log + status look familiar.
            try { await PullModelInlineAsync(ollama, SelectedModel.OllamaTag); }
            catch (Exception ex)
            {
                AppLog.Warn("UseInstance: inline pull failed", ex);
                MessageBox.Show(
                    $"Pull failed:\n\n{ex.Message}\n\nNo config was written. Try a different model.",
                    "Vast.ai Instance Manager", MessageBoxButton.OK, MessageBoxImage.Error);
                Status = "Pull failed - no config written.";
                return;
            }
        }

        OpencodeConfigWriter.Write(url, SelectedModel, inst.Id);
        AppendLog($"Wired opencoderemote -> {url} (model: {SelectedModel.OllamaTag})");
        Status = "opencode wired up.";
        MessageBox.Show($"opencode-remote.json now points at {url}");
    }

    private async Task PullModelInlineAsync(OllamaApi ollama, string tag)
    {
        Status = $"Pulling {tag}...";
        AppendLog($"Pulling {tag} (on-demand from Use)...");
        string lastPhase = "";
        var progress = new Progress<(string status, long? total, long? completed)>(p =>
        {
            string label = p.status;
            if (p.total.HasValue && p.completed.HasValue && p.total > 0)
            {
                int pct = (int)((p.completed.Value * 100.0) / p.total.Value);
                label = $"{p.status} {pct}% ({p.completed.Value / 1_073_741_824.0:F2}/{p.total.Value / 1_073_741_824.0:F2} GB)";
            }
            Status = label;
            if (p.status != lastPhase) { lastPhase = p.status; AppendLog($"  [pull] {p.status}"); }
        });
        await ollama.PullModelAsync(tag, progress, CancellationToken.None);
        AppendLog($"Pull complete: {tag}");
    }

    private async Task StartInstanceAsync()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null || _api == null) return;
        try
        {
            await _api.StartInstanceAsync(inst.Id);
            AppendLog($"Start signal sent to {inst.Id}.");
            await RefreshInstancesAsync(silent: true);
        }
        catch (Exception ex) { AppendLog("Start failed: " + ex.Message); }
    }

    private async Task StopInstanceAsync()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null || _api == null) return;
        if (_spinupByInstance.TryGetValue(inst.Id, out var cts))
        {
            AppendLog($"Cancelling in-flight spinup/change for {inst.Id}...");
            cts.Cancel();
        }
        try
        {
            await _api.StopInstanceAsync(inst.Id);
            AppendLog($"Stop signal sent to {inst.Id}.");
            await RefreshInstancesAsync(silent: true);
        }
        catch (Exception ex) { AppendLog("Stop failed: " + ex.Message); }
    }

    private async Task DestroyInstanceAsync()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null || _api == null) return;
        var ans = MessageBox.Show($"Destroy instance {inst.Id}? This is permanent.", "Confirm destroy",
            MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ans != MessageBoxResult.Yes) return;
        if (_spinupByInstance.TryGetValue(inst.Id, out var cts)) cts.Cancel();
        try
        {
            await _api.DestroyInstanceAsync(inst.Id);
            AppendLog($"Destroyed {inst.Id}.");
            await RefreshInstancesAsync(silent: true);
        }
        catch (Exception ex) { AppendLog("Destroy failed: " + ex.Message); }
    }

    private void OpenSettings()
    {
        var dlg = new Dialogs.SettingsDialogWindow { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        TryConnect();
        _ = CheckAuthAsync();
    }

    // --- Rent / spinup flow ---

    private async Task RentAsync()
    {
        if (_api == null || SelectedOffer == null || SelectedModel == null) return;
        var offer = SelectedOffer.Offer;
        var model = SelectedModel;
        var ans = MessageBox.Show(
            $"Rent {offer.NumGpus}x {offer.GpuName} at ${offer.DphTotal:F3}/hr?\n\nWill create instance, attach ephemeral SSH key, pull {model.OllamaTag}, and wire up opencoderemote.",
            "Confirm rental", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (ans != MessageBoxResult.Yes) return;

        var cts = new CancellationTokenSource();
        _activeSpinups.Add(cts);
        ProgressVisible = true;
        Progress = 0;
        try
        {
            await new RentOrchestrator(this).RunAsync(offer, model, cts);
            await RefreshInstancesAsync(silent: true);
        }
        catch (OperationCanceledException) { AppendLog($"Cancelled spinup for offer {offer.Id}."); }
        catch (OfferNoLongerAvailableException ex)
        {
            _deadOfferIds.Add(ex.OfferId);
            AppendLog(ex.Message);
            Status = "Offer unavailable. Removed from list.";
            for (int i = Offers.Count - 1; i >= 0; i--) if (Offers[i].Offer.Id == ex.OfferId) Offers.RemoveAt(i);
        }
        catch (Exception ex)
        {
            AppendLog($"FAILED (offer {offer.Id}): " + ex.Message);
            Status = "Spinup failed.";
        }
        finally
        {
            _activeSpinups.Remove(cts);
            cts.Dispose();
            ProgressVisible = false;
        }
    }

    private void SetInstanceExtraStatus(long instanceId, string? msg)
    {
        if (msg == null) _instanceExtraStatus.Remove(instanceId);
        else _instanceExtraStatus[instanceId] = msg;
        var row = Instances.FirstOrDefault(r => r.Id == instanceId);
        if (row != null) row.Message = msg ?? row.Instance.StatusMsg ?? "";
    }

    // --- Change Model ---

    private async Task ChangeModelAsync()
    {
        var inst = SelectedInstance?.Instance;
        if (inst == null || _api == null) return;
        if (inst.ActualStatus != "running") { MessageBox.Show("Instance must be running to change its model."); return; }
        var port = inst.GetExternalPortFor(11434);
        if (inst.PublicIpAddr == null || port == null) { MessageBox.Show("Instance has no Ollama port mapping."); return; }
        var ollamaUrl = $"http://{inst.PublicIpAddr}:{port}";
        var ollama = new OllamaApi(ollamaUrl);

        List<string> installed;
        try { installed = await ollama.ListModelsAsync(); }
        catch (Exception ex) { MessageBox.Show("Couldn't reach Ollama on that instance: " + ex.Message); return; }

        int vramAvail = inst.TotalVramGb;
        int diskAvail = inst.DiskGb;
        bool multimodalBanned = _multimodalIncompatibleInstances.Contains(inst.Id);
        var allModels = _modelsDict.Values.ToList();
        var fits = allModels.Where(m =>
            (vramAvail <= 0 || vramAvail >= m.ModelSizeGb * 1.15) &&
            (diskAvail <= 0 || diskAvail >= m.DiskGb)).ToList();
        var skipped = allModels.Except(fits).ToList();

        // If this instance has already failed a multimodal smoke test, hide multimodal models -
        // those just waste time and disk pulling them only to fail at load again.
        if (multimodalBanned)
        {
            var hiddenForModality = fits.Where(m => m.IsMultimodal).ToList();
            if (hiddenForModality.Count > 0)
            {
                fits = fits.Except(hiddenForModality).ToList();
                skipped.AddRange(hiddenForModality);
                AppendLog($"Change Model: hid {hiddenForModality.Count} multimodal model(s) - this instance has shown it can't load them.");
            }
        }

        if (fits.Count == 0)
        {
            var extra = multimodalBanned ? "\n\n(Multimodal models are hidden because this instance can't load them.)" : "";
            MessageBox.Show(
                $"No models in the registry fit this instance:\n  VRAM: {vramAvail} GB\n  Disk: {diskAvail} GB{extra}",
                "No fits", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new Dialogs.ChangeModelDialogWindow(fits, installed, inst.Id, vramAvail, diskAvail, skipped)
        { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true || dlg.Selected == null) return;

        var newModel = dlg.Selected;
        var cts = new CancellationTokenSource();
        _activeSpinups.Add(cts);
        _spinupByInstance[inst.Id] = cts;
        ProgressVisible = true;
        Progress = 0;
        try
        {
            await new ChangeModelOrchestrator(this).RunAsync(inst, ollama, ollamaUrl, installed, newModel, cts.Token);
            await RefreshInstancesAsync(silent: true);
        }
        catch (OperationCanceledException) { AppendLog($"Cancelled model change on {inst.Id}."); }
        catch (Exception ex)
        {
            AppendLog($"Change-model FAILED ({inst.Id}): " + ex.Message);
            Status = "Change model failed.";
        }
        finally
        {
            _activeSpinups.Remove(cts);
            _spinupByInstance.Remove(inst.Id);
            cts.Dispose();
            ProgressVisible = false;
        }
    }

    // --- Manage Models ---

    private void OpenManageModels()
    {
        var dlg = new Dialogs.ManageModelsDialogWindow { Owner = Application.Current.MainWindow };
        dlg.ShowDialog();
        if (dlg.Modified) ReloadModels();
    }

    // --- IOrchestratorHost implementation (orchestrators read services + push UI updates here) ---

    VastApi IOrchestratorHost.Api => _api ?? throw new InvalidOperationException("No API client - not connected.");
    HostStatsService IOrchestratorHost.HostStats => _hostStats;
    TemplateHashService IOrchestratorHost.TemplateHash => _templateHash;
    void IOrchestratorHost.SetStatus(string status) => Status = status;
    void IOrchestratorHost.SetProgress(double progress) => Progress = progress;
    void IOrchestratorHost.SetInstanceExtraStatus(long instanceId, string? msg) => SetInstanceExtraStatus(instanceId, msg);
    void IOrchestratorHost.ShowIncompatibleModelPopup(ModelEntry model, bool multimodal) => ShowIncompatibleModelPopup(model, multimodal);
    void IOrchestratorHost.NotifyInstanceReady(long instanceId, string title, string body)
    {
        // Fire-and-forget on the dispatcher so the orchestrator's await chain completes (including
        // the `finally` that unregisters the spinup CTS) before the MessageBox blocks the UI.
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
            MessageBox.Show(
                Application.Current?.MainWindow,
                body,
                $"Vast.ai Instance Manager - {title}",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }));
    }
    Task IOrchestratorHost.RefreshInstancesAsync(bool silent) => RefreshInstancesAsync(silent);
    void IOrchestratorHost.AddDeadOffer(long offerId) => _deadOfferIds.Add(offerId);
    void IOrchestratorHost.AddMultimodalIncompatible(long instanceId) => _multimodalIncompatibleInstances.Add(instanceId);
    void IOrchestratorHost.RegisterSpinupCts(long instanceId, CancellationTokenSource cts) => _spinupByInstance[instanceId] = cts;
    void IOrchestratorHost.UnregisterSpinupCts(long instanceId) => _spinupByInstance.Remove(instanceId);

    // --- Smoke-test helpers ---

    private void ShowIncompatibleModelPopup(ModelEntry model, bool multimodalFailure)
    {
        string title = multimodalFailure ? "Multimodal model can't load here" : "Model incompatible with this provider";
        string body = multimodalFailure
            ? $"The smoke test for '{model.Key}' failed because this provider's Ollama runtime can't load the model's vision/audio projector.\n\n" +
              "The HOST itself is fine - text-only models will load.\n\n" +
              "Use Change Model on this instance to pick a text-only model. Multimodal models will be hidden from the picker for this instance."
            : $"The smoke test for '{model.Key}' failed - this model isn't compatible with the Ollama runtime on this provider.\n\n" +
              "The HOST itself is fine. Use Change Model to pick a different model.";
        MessageBox.Show(body, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // --- Log ---

    public void AppendLog(string line)
    {
        var stamp = DateTime.Now.ToString("h:mm:ss tt");
        _logBuilder.AppendLine($"[{stamp}] {line}");
        Log = _logBuilder.ToString();
    }
}
