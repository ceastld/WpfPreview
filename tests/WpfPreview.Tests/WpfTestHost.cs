using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace WpfPreview.Tests;

internal static class WpfTestHost
{
    private static readonly Lazy<Dispatcher> Ui = new(Start);
    private static readonly object Gate = new();

    internal static void Run(Action action) => Run(() => { action(); return Task.CompletedTask; });

    internal static void Run(Func<Task> action)
    {
        lock (Gate)
        {
            Ui.Value.InvokeAsync(async () =>
            {
                var before = Application.Current.Windows.Cast<Window>().ToArray();
                try { await action(); }
                finally
                {
                    foreach (var window in Application.Current.Windows.Cast<Window>().Except(before).ToArray())
                        window.Close();
                }
            }).Task.Unwrap().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        }
    }

    private static Dispatcher Start()
    {
        var ready = new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                ready.SetResult(app.Dispatcher);
                app.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
}
