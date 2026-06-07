using System.Windows;
using System.Windows.Media;
using VastSpinup.Services;

namespace VastSpinup.Wpf.Dialogs;

public partial class SettingsDialogWindow : Window
{
    public SettingsDialogWindow()
    {
        InitializeComponent();
        var current = VastApi.TryLoadDefaultApiKey();
        if (!string.IsNullOrEmpty(current)) KeyBox.Password = current;
        PathText.Text = $"Stored at: {AppPaths.VastApiKeyFile}";
    }

    /// <summary>"Show" toggle: flip visibility between PasswordBox and TextBox, mirroring the
    /// current value across so the user keeps what they typed. No Grid.Children manipulation —
    /// both controls live in the XAML at the same cell, one is Collapsed at any time.</summary>
    private void ShowKeyBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (ShowKeyBox.IsChecked == true)
        {
            PlainKeyBox.Text = KeyBox.Password;
            PlainKeyBox.Visibility = Visibility.Visible;
            KeyBox.Visibility = Visibility.Collapsed;
            PlainKeyBox.Focus();
            PlainKeyBox.SelectionStart = PlainKeyBox.Text.Length;
        }
        else
        {
            KeyBox.Password = PlainKeyBox.Text;
            KeyBox.Visibility = Visibility.Visible;
            PlainKeyBox.Visibility = Visibility.Collapsed;
            KeyBox.Focus();
        }
    }

    private string CurrentKey()
        => (ShowKeyBox.IsChecked == true ? PlainKeyBox.Text : KeyBox.Password).Trim();

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey();
        if (string.IsNullOrEmpty(key)) { MessageBox.Show("Enter an API key first."); return; }
        TestButton.IsEnabled = false;
        StatusText.Text = "Testing...";
        StatusText.Foreground = (Brush)FindResource("TextSecondary");
        try
        {
            var api = new VastApi(key);
            var u = await api.GetCurrentUserAsync();
            var email = u.TryGetProperty("email", out var em) ? em.GetString() : "?";
            var credit = u.TryGetProperty("credit", out var cr) ? cr.GetDouble() : 0;
            StatusText.Text = $"OK — {email} (credit ${credit:F2})";
            StatusText.Foreground = (Brush)FindResource("AccentSuccess");
        }
        catch (Exception ex)
        {
            AppLog.Warn("Settings: API key test failed", ex);
            StatusText.Text = "Failed: " + ex.Message;
            StatusText.Foreground = (Brush)FindResource("AccentDanger");
        }
        finally { TestButton.IsEnabled = true; }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = CurrentKey();
        if (string.IsNullOrEmpty(key)) { MessageBox.Show("Empty key."); return; }
        try
        {
            VastApi.SaveApiKey(key);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            AppLog.Error("Settings: failed to save API key", ex);
            MessageBox.Show("Save failed: " + ex.Message);
        }
    }
}
