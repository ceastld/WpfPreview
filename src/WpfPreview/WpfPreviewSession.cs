using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace WpfPreview;

/// <summary>拥有本地监听器的生命周期；不拥有宿主窗口。</summary>
public sealed class WpfPreviewSession : IDisposable
{
    private readonly Application _application;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly WpfPreviewHttpHandler _handler;
    private int _disposed;

    public Uri PreviewUri { get; }

    internal WpfPreviewSession(Application application, WpfPreviewOptions options)
    {
        _application = application;
        PreviewUri = new Uri($"http://127.0.0.1:{options.Port}/wpf-preview/");
        _handler = new WpfPreviewHttpHandler(application, options, _stopping.Token);
        _listener.Prefixes.Add($"http://127.0.0.1:{options.Port}/");
        try { _listener.Start(); }
        catch { _listener.Close(); _stopping.Dispose(); throw; }
        application.Exit += OnExit;
        _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException) { break; }
            // 大截图不会阻塞其它连接；Handler 对截图与导出分别限流。
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            var request = context.Request;
            var authority = PreviewUri.GetLeftPart(UriPartial.Authority);
            var origin = request.Headers["Origin"];
            if (request.Url?.GetLeftPart(UriPartial.Authority) != authority
                || (origin != null && origin != authority)
                || request.Headers["Sec-Fetch-Site"] == "cross-site")
            {
                context.Response.StatusCode = 403;
                return;
            }
            if (!await _handler.TryHandleRequestAsync(context).ConfigureAwait(false))
                context.Response.StatusCode = 404;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // 浏览器断开或应用退出不影响宿主。
        }
        finally { context.Response.Close(); }
    }

    private void OnExit(object sender, ExitEventArgs args) => Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _listener.Close();
        WpfPreviewExtensions.Detach(_application, this);
        if (_application.Dispatcher.CheckAccess()) _application.Exit -= OnExit;
        else if (!_application.Dispatcher.HasShutdownStarted)
            _application.Dispatcher.BeginInvoke(new Action(() => _application.Exit -= OnExit));
    }
}
