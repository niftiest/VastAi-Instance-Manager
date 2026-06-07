using System.Globalization;
using System.Windows;
using VastSpinup.Models;
using VastSpinup.Services;

namespace VastSpinup.Wpf.Dialogs;

public partial class ModelEditDialogWindow : Window
{
    public ModelEntry Result { get; private set; } = new();
    public string Key { get; private set; } = "";
    /// <summary>Set by HF search (and preserved across edits). Used by Change Model dialog to
    /// hide multimodal models when an instance has shown it can't load them.</summary>
    private string _modality = "";

    public ModelEditDialogWindow(ModelEntry? existing = null, string existingKey = "")
    {
        InitializeComponent();
        if (existing != null)
        {
            KeyBox.Text = existingKey;
            KeyBox.IsReadOnly = true;
            NameBox.Text = existing.Name;
            TagBox.Text = existing.OllamaTag;
            VramBox.Text = existing.VramGb.ToString();
            DiskBox.Text = existing.DiskGb.ToString();
            SizeBox.Text = existing.ModelSizeGb.ToString("F1", CultureInfo.InvariantCulture);
            CtxBox.Text = existing.NumCtx.ToString();
            TempBox.Text = existing.Temperature.ToString("F2", CultureInfo.InvariantCulture);
            TopPBox.Text = existing.TopP.ToString("F2", CultureInfo.InvariantCulture);
            TopKBox.Text = existing.TopK.ToString();
            MaxPriceBox.Text = existing.MaxPricePerHour.HasValue ? existing.MaxPricePerHour.Value.ToString("F3", CultureInfo.InvariantCulture) : "0";
            MinRelBox.Text = existing.MinReliability.ToString("F3", CultureInfo.InvariantCulture);
            NotesBox.Text = existing.Notes;
            _modality = existing.Modality;
            Title = $"Edit Model - {existingKey}";
        }
        else
        {
            VramBox.Text = "24"; DiskBox.Text = "35"; SizeBox.Text = "19";
            CtxBox.Text = "16384"; TempBox.Text = "0.7"; TopPBox.Text = "0.9"; TopKBox.Text = "40";
            MaxPriceBox.Text = "0"; MinRelBox.Text = "0.95";
            Title = "Add Model";
        }
    }

    private void HfSearch_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new HuggingFaceSearchDialogWindow { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Selected == null) return;
        ApplyHfResult(dlg);
    }

    public void ApplyHfResult(HuggingFaceSearchDialogWindow dlg)
    {
        if (dlg.Selected == null) return;
        var m = dlg.Selected;
        _modality = ModalityHelper.FromHfModel(m);

        var slash = m.Id.IndexOf('/');
        if (string.IsNullOrEmpty(KeyBox.Text) && !KeyBox.IsReadOnly)
            KeyBox.Text = (slash >= 0 ? m.Id[(slash + 1)..] : m.Id).ToLowerInvariant().Replace('-', '_');

        var template = ModelDefaults.Suggest(m.Id, dlg.SuggestedOllamaTag, dlg.SelectedSizeGb);

        var registryOverrides = ModelDefaults.ParseRegistryParams(dlg.SelectedRegistryParams);
        var primaryOverrides = ModelDefaults.ParseReadme(dlg.SelectedReadme);
        var baseOverrides = ModelDefaults.ParseReadme(dlg.SelectedBaseReadme);
        var overrides = ModelDefaults.Merge(
            ModelDefaults.Merge(registryOverrides, primaryOverrides), baseOverrides);

        if (overrides.Temperature.HasValue) template.Temperature = overrides.Temperature.Value;
        if (overrides.TopP.HasValue) template.TopP = overrides.TopP.Value;
        if (overrides.TopK.HasValue) template.TopK = overrides.TopK.Value;
        if (overrides.NumCtx.HasValue) template.NumCtx = overrides.NumCtx.Value;

        NameBox.Text = template.Name;
        TagBox.Text = template.OllamaTag;
        VramBox.Text = template.VramGb.ToString();
        DiskBox.Text = template.DiskGb.ToString();
        SizeBox.Text = template.ModelSizeGb.ToString("F1", CultureInfo.InvariantCulture);
        CtxBox.Text = template.NumCtx.ToString();
        TempBox.Text = template.Temperature.ToString("F2", CultureInfo.InvariantCulture);
        TopPBox.Text = template.TopP.ToString("F2", CultureInfo.InvariantCulture);
        TopKBox.Text = template.TopK.ToString();

        var notes = template.Notes;
        var sources = new List<string>();
        if (registryOverrides.HasAny) sources.Add($"Ollama registry: {string.Join(", ", registryOverrides.AppliedFields())}");
        if (primaryOverrides.HasAny && primaryOverrides.AppliedFields().Count > 0)
            sources.Add($"HF README ({m.Id}): {string.Join(", ", primaryOverrides.AppliedFields())}");
        if (baseOverrides.HasAny && baseOverrides.AppliedFields().Count > 0)
        {
            var baseId = ModelDefaults.ExtractBaseModelFromReadme(dlg.SelectedReadme);
            sources.Add($"HF README ({baseId ?? "base"}): {string.Join(", ", baseOverrides.AppliedFields())}");
        }
        if (sources.Count > 0)
            notes += "\n\n📖 Recommended params found:\n" + string.Join("\n", sources.Select(s => "  • " + s));
        if (!string.IsNullOrEmpty(overrides.RecommendedSection))
            notes += $"\n\n--- Developer's recommended-section ---\n{overrides.RecommendedSection}";
        if (dlg.SelectedWarnings.Count > 0)
            notes = string.Join("\n", dlg.SelectedWarnings) + "\n\n" + notes;
        NotesBox.Text = notes;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = KeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(NameBox.Text) || string.IsNullOrEmpty(TagBox.Text))
        { MessageBox.Show("Key, name and Ollama tag are required."); return; }

        if (!TryD(VramBox.Text, out var vram) || !TryD(DiskBox.Text, out var disk) ||
            !TryD(SizeBox.Text, out var sizeGb) || !TryD(CtxBox.Text, out var ctx) ||
            !TryD(TempBox.Text, out var temp) || !TryD(TopPBox.Text, out var topP) ||
            !TryD(TopKBox.Text, out var topK) || !TryD(MaxPriceBox.Text, out var maxPrice) ||
            !TryD(MinRelBox.Text, out var minRel))
        {
            MessageBox.Show("One of the numeric fields couldn't be parsed.");
            return;
        }

        Key = key;
        Result = new ModelEntry
        {
            Key = key,
            Name = NameBox.Text.Trim(),
            OllamaTag = TagBox.Text.Trim(),
            VramGb = (int)Math.Clamp(vram, 1, 256),
            DiskGb = (int)Math.Clamp(disk, 1, 1024),
            ModelSizeGb = Math.Clamp(sizeGb, 0, 500),
            NumCtx = (int)Math.Clamp(ctx, 1024, 262144),
            Temperature = Math.Clamp(temp, 0, 2),
            TopP = Math.Clamp(topP, 0, 1),
            TopK = (int)Math.Clamp(topK, 0, 1000),
            Notes = NotesBox.Text.Trim(),
            Modality = _modality,
            MaxPricePerHour = maxPrice > 0 ? Math.Clamp(maxPrice, 0, 100) : null,
            MinReliability = Math.Clamp(minRel, 0, 1),
        };
        DialogResult = true;
        Close();
    }

    private static bool TryD(string s, out double v)
        => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}
