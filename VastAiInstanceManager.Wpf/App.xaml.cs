using System.Windows;
using System.Windows.Threading;
using VastAiInstanceManager.Services;

namespace VastAiInstanceManager.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Info($"Vast.ai Instance Manager starting (v{GetType().Assembly.GetName().Version}).");

        // UI-thread crash: log + show, then keep running so the user can decide what to do.
        DispatcherUnhandledException += (s, args) =>
        {
            AppLog.Error("DispatcherUnhandledException", args.Exception);
            MessageBox.Show(
                $"An unexpected error occurred:\n\n{args.Exception.Message}\n\nThe app will keep running. See {AppPaths.AppLogFile} for details.",
                "Vast.ai Instance Manager - unhandled error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // Background-thread crash: log only (no UI on a non-UI thread).
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            AppLog.Error("AppDomain.UnhandledException", args.ExceptionObject as Exception);
        };

        // Awaited Task that threw and was never observed.
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            AppLog.Warn("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLog.Info($"Vast.ai Instance Manager exiting (code {e.ApplicationExitCode}).");
        base.OnExit(e);
    }
}
