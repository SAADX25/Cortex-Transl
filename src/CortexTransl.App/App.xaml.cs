using CortexTransl.App.Utils;
using CortexTransl.App.Views;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App;

public partial class App : System.Windows.Application
{
    private Mutex? _instanceMutex;
    private bool _ownsInstanceMutex;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (!TryTakeSingleInstance())
        {
            MessageBox.Show(
                "Cortex Transl is already running.",
                "Cortex Transl",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);

        var mainWindow = new MainWindow();
        MainWindow = mainWindow;
        mainWindow.Show();
        mainWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsInstanceMutex)
        {
            try
            {
                _instanceMutex?.ReleaseMutex();
            }
            catch
            {
            }
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }

    private bool TryTakeSingleInstance()
    {
        _instanceMutex = new Mutex(true, @"Local\CortexTransl.SingleInstance", out var created);
        _ownsInstanceMutex = created;
        return created;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write("dispatcher", e.Exception);
        e.Handled = true;
        try
        {
            MessageBox.Show(
                "Something went wrong, but Cortex Transl is still running. Try F8 again or select the region with F9.",
                "Cortex Transl",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
        }
    }

    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            AppLog.Write("app-domain", exception);
        }
        else
        {
            AppLog.Write("app-domain", e.ExceptionObject?.ToString() ?? "Unknown fatal error.");
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        AppLog.Write("task", e.Exception);
        e.SetObserved();
    }
}
