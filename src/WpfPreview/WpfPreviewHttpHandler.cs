using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WpfPreview;

/// <summary>开发用真实 WPF 预览。只读快照，不向被预览窗口派发输入。</summary>
internal sealed class WpfPreviewHttpHandler
{
    private readonly ConditionalWeakTable<Window, WindowId> _ids = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Application _application;
    private readonly WpfPreviewOptions _options;
    private readonly CancellationToken _stopping;
    private readonly Lazy<WpfPreviewSources> _sources;

    internal WpfPreviewHttpHandler(Application application, WpfPreviewOptions options, CancellationToken stopping)
    {
        _application = application;
        _options = options;
        _stopping = stopping;
        _sources = new(() => WpfPreviewSources.FromDirectory(options.SourceRoot!));
    }
    private readonly WpfPreviewContext _contexts = new();
    private readonly SemaphoreSlim _exportGate = new(1, 1);
    private sealed class WindowId { public string Value { get; } = Guid.NewGuid().ToString("N"); }

    public async Task<bool> TryHandleRequestAsync(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "";
        if (path != "/wpf-preview" && !path.StartsWith("/wpf-preview/", StringComparison.Ordinal)) return false;
        var response = context.Response;
        response.Headers["Cache-Control"] = "no-store";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers["X-Wpf-Preview-Pid"] = Environment.ProcessId.ToString();
        response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'";
        try
        {
            if (context.Request.HttpMethod == "POST" && path is "/wpf-preview/context" or "/wpf-preview/open-window")
            {
                var request = context.Request;
                var origin = request.Headers["Origin"];
                if (request.Headers["X-Wpf-Preview"] != "context"
                    || request.ContentType?.Split(';')[0].Trim() != "application/json"
                    || (origin != null && origin != request.Url!.GetLeftPart(UriPartial.Authority)))
                {
                    response.StatusCode = 403;
                    await WriteJsonAsync(response, new JObject { ["error"] = "请从本地预览页面复制上下文。" });
                    return true;
                }
                if (path == "/wpf-preview/open-window")
                {
                    if (_options.OpenWindowAsync is not { } openWindow)
                    {
                        response.StatusCode = 404;
                        await WriteJsonAsync(response, new JObject { ["error"] = "宿主没有配置示例窗口。" });
                        return true;
                    }
                    await _application.Dispatcher.InvokeAsync(openWindow, DispatcherPriority.Normal, _stopping).Task.Unwrap();
                    await WriteJsonAsync(response, new JObject { ["ok"] = true });
                    return true;
                }
                if (!await _exportGate.WaitAsync(0))
                {
                    response.StatusCode = 429;
                    await WriteJsonAsync(response, new JObject { ["error"] = "正在准备上下文，请稍候。" });
                    return true;
                }
                try
                {
                    using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
                    var buffer = new char[65537];
                    var length = 0;
                    while (length < buffer.Length)
                    {
                        var count = await reader.ReadAsync(buffer, length, buffer.Length - length);
                        if (count == 0) break;
                        length += count;
                    }
                    if (length > 65536) throw new InvalidOperationException("上下文请求过大，请减少说明文字。");
                    var payload = JObject.Parse(new string(buffer, 0, length));
                    await WriteJsonAsync(response, _contexts.Export(payload, _options.ContextDirectory!, _options.ProjectRoot!));
                }
                finally { _exportGate.Release(); }
            }
            else if (context.Request.HttpMethod != "GET")
            {
                response.StatusCode = 405;
                response.Headers["Allow"] = "GET";
                await WriteJsonAsync(response, new JObject { ["error"] = "仅支持 GET。" });
            }
            else if (path is "/wpf-preview" or "/wpf-preview/")
            {
                using var stream = typeof(WpfPreviewHttpHandler).Assembly.GetManifestResourceStream("WpfPreview.index.html")!;
                using var reader = new StreamReader(stream, Encoding.UTF8);
                await WriteAsync(response, await reader.ReadToEndAsync(), "text/html; charset=utf-8");
            }
            else if (path == "/wpf-preview/config")
            {
                await WriteJsonAsync(response, new JObject
                {
                    ["applicationName"] = _options.ApplicationName,
                    ["canOpenWindow"] = _options.OpenWindowAsync != null,
                    ["openWindowLabel"] = _options.OpenWindowLabel,
                });
            }
            else if (path == "/wpf-preview/windows")
            {
                var app = _application;
                var windows = await app.Dispatcher.InvokeAsync(() => new JArray(app.Windows.Cast<Window>()
                    .Where(w => w.IsVisible && w.IsLoaded && w.WindowState != WindowState.Minimized)
                    .Select(w => new JObject
                    {
                        ["id"] = _ids.GetValue(w, static _ => new WindowId()).Value,
                        ["title"] = w.Title,
                        ["type"] = w.GetType().FullName,
                    })), DispatcherPriority.Normal, _stopping);
                await WriteJsonAsync(response, windows);
            }
            else if (path == "/wpf-preview/frame")
            {
                // 不排队积压 UI 渲染；多个浏览器同时刷新时让后来的请求稍后重试。
                if (!await _captureGate.WaitAsync(0))
                {
                    response.StatusCode = 429;
                    await WriteJsonAsync(response, new JObject { ["error"] = "正在生成快照，请稍后刷新。" });
                    return true;
                }
                try
                {
                    var sources = _sources.Value; // 文件索引在 HTTP 工作线程上构造。
                    var id = context.Request.QueryString["id"];
                    var app = _application;
                    var frame = await app.Dispatcher.InvokeAsync(() =>
                    {
                        var window = app.Windows.Cast<Window>().FirstOrDefault(w => _ids.GetValue(w, static _ => new WindowId()).Value == id);
                        if (window is null || !window.IsVisible || window.WindowState == WindowState.Minimized)
                            throw new InvalidOperationException("窗口已关闭、隐藏或最小化，请刷新窗口列表。");
                        if (window.Content is not FrameworkElement root)
                            throw new InvalidOperationException("此窗口没有可预览的 WPF 内容。");
                        var result = WpfPreviewSnapshot.Capture(root, sources);
                        result["title"] = window.Title;
                        result["windowType"] = window.GetType().FullName;
                        return result;
                    }, DispatcherPriority.Normal, _stopping);
                    _contexts.Remember(frame);
                    await WriteJsonAsync(response, frame);
                }
                finally { _captureGate.Release(); }
            }
            else
            {
                response.StatusCode = 404;
                await WriteJsonAsync(response, new JObject { ["error"] = "没有此预览接口。" });
            }
        }
        catch (Exception ex)
        {
            response.StatusCode = 400;
            await WriteJsonAsync(response, new JObject { ["error"] = ex.Message });
        }
        return true;
    }

    private static Task WriteJsonAsync(HttpListenerResponse response, JToken value) =>
        WriteAsync(response, value.ToString(Formatting.None), "application/json; charset=utf-8");

    private static async Task WriteAsync(HttpListenerResponse response, string text, string contentType)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.ContentType = contentType;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }
}
