using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using VastAiInstanceManager.Services;

namespace VastAiInstanceManager.Wpf.Dialogs;

public partial class HuggingFaceSearchDialogWindow : Window
{
    private readonly HuggingFaceApi _hf = new();
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _searchCts;
    private readonly Dictionary<string, List<(string filename, long sizeBytes)>> _sizesByModelId = new();
    private readonly ObservableCollection<ResultRow> _rows = new();
    private List<HuggingFaceModel>? _popularCache;

    public HuggingFaceModel? Selected { get; private set; }
    public string SuggestedOllamaTag { get; private set; } = "";
    public double SelectedSizeGb { get; private set; }
    public string? SelectedReadme { get; private set; }
    public string? SelectedBaseReadme { get; private set; }
    public System.Text.Json.JsonElement? SelectedRegistryParams { get; private set; }
    public List<string> SelectedWarnings { get; private set; } = new();

    // Shows an "Enter manually" button that closes with DialogResult=true and Selected=null.
    public bool AllowManualEntry
    {
        get => ManualButton.Visibility == Visibility.Visible;
        set => ManualButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    public HuggingFaceSearchDialogWindow()
    {
        InitializeComponent();
        ResultsGrid.ItemsSource = _rows;
        _debounce.Tick += async (_, _) =>
        {
            _debounce.Stop();
            try { await RunSearch(); }
            catch (Exception ex) { VastAiInstanceManager.Services.AppLog.Warn("HF search tick crashed", ex); }
        };
        Closed += (_, _) => { _searchCts?.Cancel(); _debounce.Stop(); };
        QueryBox.Focus();
        Loaded += async (_, _) =>
        {
            try { await LoadPopularAsync(); }
            catch (Exception ex) { VastAiInstanceManager.Services.AppLog.Warn("HF popular load crashed", ex); }
        };
    }

    /// <summary>Pre-populates the grid with HF's top-liked GGUF models so the dialog opens on
    /// something useful. Cached so re-emptying the search query is instant.</summary>
    private async Task LoadPopularAsync()
    {
        StatusText.Text = "Loading popular models...";
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;
        try { _popularCache = await _hf.GetPopularAsync(GgufOnlyBox.IsChecked == true, 100, ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;
        ShowPopular(ct);
    }

    private void ShowPopular(CancellationToken ct)
    {
        if (_popularCache == null) return;
        _rows.Clear();
        foreach (var m in _popularCache)
        {
            _rows.Add(new ResultRow
            {
                Id = m.Id,
                Author = m.Author ?? "",
                Downloads = m.Downloads.ToString("N0"),
                Likes = m.Likes.ToString("N0"),
                Size = "…",
                Tools = "",
                Modal = ModalityHelper.FromHfModel(m),
                Added = m.CreatedAt?.ToString("MM/dd/yy") ?? "",
                Source = "HF",
                Tag = m,
            });
        }
        StatusText.Text = $"Top {_popularCache.Count} by likes - type to search.";
        _ = FetchSizesAsync(_popularCache, ct);
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private async Task RunSearch()
    {
        var q = QueryBox.Text.Trim();
        if (q.Length < 2)
        {
            // Restore the popular pre-load. If we don't have it cached yet, fetch it now.
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            if (_popularCache == null) await LoadPopularAsync();
            else ShowPopular(_searchCts.Token);
            return;
        }

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        StatusText.Text = "Searching Hugging Face...";
        _rows.Clear();

        var ggufOnly = GgufOnlyBox.IsChecked == true;
        List<HuggingFaceModel> results;
        try { results = await _hf.SearchAsync(q, ggufOnly, 25, ct); }
        catch (OperationCanceledException) { return; }

        if (ct.IsCancellationRequested) return;

        var ollamaTask = OllamaApi.SearchModelsAsync(q, ct);

        foreach (var m in results)
        {
            _rows.Add(new ResultRow
            {
                Id = m.Id,
                Author = m.Author ?? "",
                Downloads = m.Downloads.ToString("N0"),
                Likes = m.Likes.ToString("N0"),
                Size = "…",
                Tools = "…",
                Modal = ModalityHelper.FromHfModel(m),
                Added = m.CreatedAt?.ToString("MM/dd/yy") ?? "",
                Source = "HF",
                Tag = m,
            });
        }
        StatusText.Text = $"{results.Count} HF result{(results.Count == 1 ? "" : "s")} for \"{q}\"" + (ggufOnly ? " (GGUF only)" : "");

        _ = FetchSizesAsync(results, ct);
        _ = FetchToolsAsync(results, q, ct);
        _ = MergeOllamaResultsAsync(ollamaTask, q, ct);
    }

    private async Task MergeOllamaResultsAsync(Task<List<OllamaSearchResult>> ollamaTask, string query, CancellationToken ct)
    {
        List<OllamaSearchResult> ollama;
        try { ollama = await ollamaTask; }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested || ollama.Count == 0) return;

        Dispatcher.Invoke(() =>
        {
            if (ct.IsCancellationRequested) return;
            int insertAt = 0;
            foreach (var r in ollama)
            {
                var sizesLabel = r.Sizes.Count > 0 ? string.Join("/", r.Sizes) : "";
                _rows.Insert(insertAt++, new ResultRow
                {
                    Id = r.Name,
                    Author = "",
                    Downloads = r.PullsDisplay,
                    Likes = "",
                    Size = sizesLabel,
                    Tools = r.HasTools ? "✓" : "",
                    Modal = ModalityHelper.FromOllamaResult(r),
                    Added = "",
                    Source = "Ollama",
                    Tag = r,
                });
            }
            StatusText.Text += $"  •  +{ollama.Count} from Ollama library";
        });
    }

    private async Task FetchToolsAsync(List<HuggingFaceModel> models, string query, CancellationToken ct)
    {
        HashSet<string> toolNames;
        try { toolNames = await OllamaApi.SearchLibraryAsync(query, "tools", ct); }
        catch (OperationCanceledException) { return; }
        if (ct.IsCancellationRequested) return;

        Dispatcher.Invoke(() =>
        {
            foreach (var row in _rows)
            {
                if (row.Tag is not HuggingFaceModel m) continue;
                var hfLower = m.Id.ToLowerInvariant();
                row.Tools = toolNames.Any(n => n.Length >= 4 && hfLower.Contains(n)) ? "✓" : "";
            }
        });
    }

    private async Task FetchSizesAsync(List<HuggingFaceModel> models, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(6);
        var tasks = models.Select(async m =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var files = await _hf.GetGgufFilesAsync(m.Id, ct);
                if (ct.IsCancellationRequested) return;
                Dispatcher.Invoke(() => UpdateSizeCell(m.Id, files));
            }
            catch (OperationCanceledException) { }
            finally { gate.Release(); }
        });
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
    }

    private void UpdateSizeCell(string modelId, List<(string filename, long sizeBytes)> files)
    {
        _sizesByModelId[modelId] = files;
        foreach (var row in _rows)
        {
            if (row.Tag is HuggingFaceModel m && m.Id == modelId)
            {
                row.Size = GgufHelpers.FormatRecommendedSize(files);
                return;
            }
        }
    }

    private void Manual_Click(object sender, RoutedEventArgs e)
    {
        Selected = null;
        DialogResult = true;
    }

    private void Results_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => Select_Click(sender, new RoutedEventArgs());

    private void OpenHf_Click(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ResultRow r) return;
        if (r.Tag is HuggingFaceModel m)
            Process.Start(new ProcessStartInfo($"https://huggingface.co/{m.Id}") { UseShellExecute = true });
    }

    private async void Select_Click(object sender, RoutedEventArgs e)
    {
        try { await SelectAsync(); }
        catch (Exception ex)
        {
            VastAiInstanceManager.Services.AppLog.Error("HF Select_Click crashed", ex);
            MessageBox.Show($"Select failed:\n\n{ex.Message}", "Vast.ai Instance Manager", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SelectAsync()
    {
        if (ResultsGrid.SelectedItem is not ResultRow row) { MessageBox.Show("Pick a model first."); return; }
        if (row.Tag is OllamaSearchResult ollamaResult) { await OnSelectOllamaAsync(ollamaResult); return; }
        if (row.Tag is not HuggingFaceModel m) return;

        var candidate = HuggingFaceApi.SuggestOllamaTag(m.Id);
        StatusText.Text = $"Verifying '{candidate}' on Ollama registry...";
        IsEnabled = false;
        var (exists, _, _) = await OllamaApi.CheckRegistryTagAsync(candidate);

        if (!exists)
        {
            if (_sizesByModelId.TryGetValue(m.Id, out var cachedFiles))
            {
                var pick = GgufHelpers.PickRepresentativeFile(cachedFiles);
                if (pick.HasValue)
                {
                    if (pick.Value.isSharded)
                    {
                        SelectedWarnings.Add(
                            $"⚠ Best-fit quant '{pick.Value.quantLabel}' is sharded across multiple GGUF files. " +
                            $"Ollama 0.30 can't pull sharded GGUFs via hf.co/ (ollama/ollama#5245). " +
                            $"Options: (a) edit the Ollama tag to a non-sharded smaller quant from this repo, " +
                            $"(b) find a curated Ollama tag at ollama.com/library, or " +
                            $"(c) pick a different model.");
                        StatusText.Text = "⚠ Sharded GGUF - hf.co/ pull won't work. See Notes in next dialog.";
                    }
                    else
                    {
                        candidate = HuggingFaceApi.BuildHfDirectTag(m.Id, pick.Value.quantLabel);
                        StatusText.Text = $"Curated tag not on Ollama registry - using HF direct: {candidate}";
                    }
                }
            }
        }

        Selected = m;
        SuggestedOllamaTag = candidate;
        if (_sizesByModelId.TryGetValue(m.Id, out var files))
        {
            const double gib = 1_073_741_824.0;
            SelectedSizeGb = GgufHelpers.PickRepresentativeSizeBytes(files) / gib;
        }

        StatusText.Text = $"Fetching {m.Id} README + Ollama params...";
        try
        {
            SelectedReadme = await _hf.GetReadmeAsync(m.Id);
            var baseTask = FetchBaseReadmeAsync(SelectedReadme);
            var paramsTask = OllamaApi.GetRegistryParamsAsync(candidate);
            SelectedBaseReadme = await baseTask;
            SelectedRegistryParams = await paramsTask;
        }
        catch (Exception ex) { VastAiInstanceManager.Services.AppLog.Warn("HF README/params fetch failed (proceeding)", ex); }
        IsEnabled = true;
        DialogResult = true;
        Close();
    }

    private async Task OnSelectOllamaAsync(OllamaSearchResult or)
    {
        var tag = or.Name;
        if (or.Sizes.Count > 0 && !tag.Contains(':')) tag = $"{or.Name}:{or.Sizes[^1]}";

        StatusText.Text = $"Fetching {tag} manifest from Ollama registry...";
        IsEnabled = false;
        long modelSizeBytes = 0;
        try
        {
            var (exists, _, manifest) = await OllamaApi.CheckRegistryTagAsync(tag);
            if (exists && manifest != null
                && manifest.Value.TryGetProperty("layers", out var layers)
                && layers.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var l in layers.EnumerateArray())
                {
                    if (l.TryGetProperty("mediaType", out var mt)
                        && mt.GetString() == "application/vnd.ollama.image.model"
                        && l.TryGetProperty("size", out var sz)
                        && sz.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        modelSizeBytes = sz.GetInt64();
                        break;
                    }
                }
            }
            SelectedRegistryParams = await OllamaApi.GetRegistryParamsAsync(tag);
        }
        catch (Exception ex) { VastAiInstanceManager.Services.AppLog.Warn("Ollama manifest/params fetch failed (proceeding)", ex); }
        IsEnabled = true;

        Selected = new HuggingFaceModel { Id = or.Name };
        SuggestedOllamaTag = tag;
        SelectedSizeGb = modelSizeBytes / 1_073_741_824.0;
        SelectedReadme = string.IsNullOrEmpty(or.Description) ? null : "## Ollama Library Description\n\n" + or.Description;
        SelectedBaseReadme = null;
        SelectedWarnings.Clear();
        SelectedWarnings.Add(or.Capabilities.Count > 0
            ? "Ollama capabilities: " + string.Join(", ", or.Capabilities)
            : "Ollama capabilities: (none listed)");
        if (!or.HasTools)
            SelectedWarnings.Add("⚠ This Ollama model has no 'tools' capability - OpenCode tool-calling won't work.");

        DialogResult = true;
        Close();
    }

    private async Task<string?> FetchBaseReadmeAsync(string? primaryReadme)
    {
        var baseId = ModelDefaults.ExtractBaseModelFromReadme(primaryReadme);
        if (string.IsNullOrEmpty(baseId)) return null;
        if (baseId == Selected?.Id) return null;
        return await _hf.GetReadmeAsync(baseId);
    }

    /// <summary>One row in the results DataGrid. Implements INPC so async size/tools updates re-render.</summary>
    public class ResultRow : Mvvm.ObservableObject
    {
        private string _id = ""; public string Id { get => _id; set => SetField(ref _id, value); }
        private string _author = ""; public string Author { get => _author; set => SetField(ref _author, value); }
        private string _downloads = ""; public string Downloads { get => _downloads; set => SetField(ref _downloads, value); }
        private string _likes = ""; public string Likes { get => _likes; set => SetField(ref _likes, value); }
        private string _size = ""; public string Size { get => _size; set => SetField(ref _size, value); }
        private string _tools = ""; public string Tools { get => _tools; set => SetField(ref _tools, value); }
        private string _added = ""; public string Added { get => _added; set => SetField(ref _added, value); }
        private string _source = ""; public string Source { get => _source; set => SetField(ref _source, value); }
        private string _modal = ""; public string Modal { get => _modal; set => SetField(ref _modal, value); }
        public object? Tag { get; set; }
    }
}
