using System.Windows;
using System.Windows.Threading;

namespace CortexTransl.App.Utils;

public static class UiDispatch
{
    public static void Run(Action action, Dispatcher? dispatcher = null)
    {
        try
        {
            dispatcher ??= Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
            {
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.Invoke(action, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromMilliseconds(400));
        }
        catch
        {
        }
    }
}
