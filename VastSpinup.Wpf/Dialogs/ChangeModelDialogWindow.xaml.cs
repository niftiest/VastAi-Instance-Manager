using System.Windows;
using VastSpinup.Models;

namespace VastSpinup.Wpf.Dialogs;

public partial class ChangeModelDialogWindow : Window
{
    public ModelEntry? Selected { get; private set; }

    public ChangeModelDialogWindow(
        IEnumerable<ModelEntry> fittingModels,
        IEnumerable<string> currentlyInstalled,
        long instanceId,
        int vramGb,
        int diskGb,
        IEnumerable<ModelEntry>? skippedModels = null)
    {
        InitializeComponent();
        var installedText = currentlyInstalled.Any() ? string.Join(", ", currentlyInstalled) : "(none)";
        InfoText.Text = $"Instance {instanceId}  •  VRAM: {vramGb} GB  •  Disk: {diskGb} GB" +
                        Environment.NewLine + "Installed: " + installedText;

        foreach (var m in fittingModels) ModelCombo.Items.Add(m);
        if (ModelCombo.Items.Count > 0) ModelCombo.SelectedIndex = 0;

        if (skippedModels != null && skippedModels.Any())
        {
            var names = string.Join(", ", skippedModels.Select(m => m.Key));
            SkippedText.Text = $"Hidden (don't fit this instance's VRAM or disk): {names}";
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelCombo.SelectedItem is not ModelEntry m) { MessageBox.Show("Pick a model."); return; }
        Selected = m;
        DialogResult = true;
        Close();
    }
}
