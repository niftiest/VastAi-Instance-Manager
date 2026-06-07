using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
