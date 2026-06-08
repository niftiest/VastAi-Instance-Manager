using System.Collections.ObjectModel;
using System.Windows;
using VastAiInstanceManager.Models;
using VastAiInstanceManager.Services;

namespace VastAiInstanceManager.Wpf.Dialogs;

public partial class ManageModelsDialogWindow : Window
{
    private readonly ModelRegistry _registry = new();
    private Dictionary<string, ModelEntry> _models = new();
    private readonly ObservableCollection<ModelEntry> _bound = new();

    public bool Modified { get; private set; }

    public ManageModelsDialogWindow()
    {
        InitializeComponent();
        ModelsGrid.DataContext = _bound;
        ReloadList();
    }

    private void ReloadList()
    {
        _models = _registry.Load();
        _bound.Clear();
        foreach (var m in _models.Values) _bound.Add(m);
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        var hf = new HuggingFaceSearchDialogWindow { Owner = this };
        var hfOk = hf.ShowDialog() == true && hf.Selected != null;

        var edit = new ModelEditDialogWindow { Owner = this };
        if (hfOk) edit.ApplyHfResult(hf);
        if (edit.ShowDialog() != true) return;

        if (_models.ContainsKey(edit.Key)) { MessageBox.Show("Key already exists."); return; }
        _models[edit.Key] = edit.Result;
        _registry.Save(_models);
        Modified = true;
        ReloadList();
    }

    private void EditButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsGrid.SelectedItem is not ModelEntry m) return;
        var dlg = new ModelEditDialogWindow(m, m.Key) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _models[m.Key] = dlg.Result;
        _registry.Save(_models);
        Modified = true;
        ReloadList();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (ModelsGrid.SelectedItem is not ModelEntry m) return;
        if (MessageBox.Show($"Delete model '{m.Key}'?", "Confirm", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _models.Remove(m.Key);
        _registry.Save(_models);
        Modified = true;
        ReloadList();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ModelsGrid_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        => EditButton_Click(sender, new RoutedEventArgs());
}
