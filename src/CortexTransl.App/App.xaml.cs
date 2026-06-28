using CortexTransl.App.Utils;
using CortexTransl.App.Views;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App;

public partial class App : System.Windows.Application
{
    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.Activate();
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogStartupException("dispatcher", e.Exception);
        MessageBox.Show(e.Exception.ToString(), "Cortex Transl startup error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Current.Shutdown(-1);
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogStartupException("app-domain", exception);
        }
        else
        {
            LogStartupException("app-domain", new InvalidOperationException(e.ExceptionObject?.ToString() ?? "Unknown fatal error."));
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogStartupException("task", e.Exception);
        e.SetObserved();
    }

    private static void LogStartupException(string source, Exception exception)
    {
        try
        {
            var paths = AppDataPaths.CreateDefault();
            var message = $"{DateTimeOffset.Now:O} {source}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(paths.DataDirectory, "logs", "startup-errors.log"), message);
        }
        catch
        {
        }
    }

}
