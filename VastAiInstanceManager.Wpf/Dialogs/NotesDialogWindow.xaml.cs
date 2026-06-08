using System.Windows;

namespace VastAiInstanceManager.Wpf.Dialogs;

public partial class NotesDialogWindow : Window
{
    public string? Result { get; private set; }

    public NotesDialogWindow(long machineId, string? gpuDisplay, string? existingNote)
    {
        InitializeComponent();
        var gpu = string.IsNullOrWhiteSpace(gpuDisplay) ? "(unknown GPU)" : gpuDisplay;
        InfoText.Text = $"Machine {machineId}  •  {gpu}\nNote follows the host across rentals.";
        NotesBox.Text = existingNote ?? string.Empty;
        NotesBox.CaretIndex = NotesBox.Text.Length;
        NotesBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        var txt = NotesBox.Text;
        Result = string.IsNullOrWhiteSpace(txt) ? null : txt.Trim();
        DialogResult = true;
        Close();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        NotesBox.Text = string.Empty;
        Result = null;
        DialogResult = true;
        Close();
    }
}
