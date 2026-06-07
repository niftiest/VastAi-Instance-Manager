using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using VastSpinup.Services;
using VastSpinup.Wpf.ViewModels;

namespace VastSpinup.Wpf;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;
        _vm.OfferSelectionRequested += row =>
        {
            OffersGrid.SelectedItem = row;
            OffersGrid.ScrollIntoView(row);
            OffersGrid.Focus();
        };
        RestoreWindowPlacement();
        Closing += (_, _) => SaveWindowPlacement();
        Loaded += async (_, _) =>
        {
            try { await _vm.InitializeAsync(); }
            catch (Exception ex)
            {
                VastSpinup.Services.AppLog.Error("MainWindow.Loaded: VM init crashed", ex);
                MessageBox.Show(
                    $"Startup error:\n\n{ex.Message}\n\nSee {VastSpinup.AppPaths.AppLogFile} for details.",
                    "VastSpinup", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        };
    }

    private void RestoreWindowPlacement()
    {
        var saved = WindowStateService.Load();
        if (saved == null) return;
        // Reject saved coords that don't overlap any current monitor (e.g. after a monitor disconnect).
        // Fall through to the XAML CenterScreen default in that case.
        if (!WindowStateService.IsOnScreen(saved.Left, saved.Top, saved.Width, saved.Height)) return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = saved.Left;
        Top = saved.Top;
        // Honour MinWidth/MinHeight set in the XAML.
        Width  = Math.Max(saved.Width,  MinWidth);
        Height = Math.Max(saved.Height, MinHeight);
        if (saved.IsMaximized) WindowState = WindowState.Maximized;
    }

    private void SaveWindowPlacement()
    {
        // RestoreBounds is the non-maximized size; Left/Top/Width/Height in maximized mode
        // would be the monitor's full size, which we don't want to save.
        var bounds = WindowState == WindowState.Maximized ? RestoreBounds
                   : new Rect(Left, Top, Width, Height);
        WindowStateService.Save(new SavedWindowState
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            IsMaximized = WindowState == WindowState.Maximized,
        });
    }

    private void BestPrice_Click(object sender, MouseButtonEventArgs e) => _vm.SelectBestPriceCommand.Execute(null);
    private void Fastest_Click(object sender, MouseButtonEventArgs e) => _vm.SelectFastestCommand.Execute(null);
    private void BestValue_Click(object sender, MouseButtonEventArgs e) => _vm.SelectBestValueCommand.Execute(null);

    private void OffersRow_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.DataGridRow row && row.Item is ViewModels.OfferRowVM offer)
            _vm.EditNotesForOffer(offer);
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb)
        {
            tb.CaretIndex = tb.Text.Length;
            tb.ScrollToEnd();
        }
    }
}
