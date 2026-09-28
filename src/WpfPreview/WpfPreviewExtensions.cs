using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WpfPreview;

public static class WpfPreviewExtensions
{
    private static readonly ConditionalWeakTable<Application, WpfPreviewSession> Sessions = new();

    /// <summary>
    /// 在 UI 线程接入开发预览。同一个 Application 重复接入会返回原会话；
    /// Dispose 或应用退出后自动停止服务。宿主应将调用和项目引用放在 Debug 条件中。
    /// </summary>
    public static WpfPreviewSession AttachWpfPreview(this Application application,
        WpfPreviewOptions? options = null, [CallerFilePath] string callerFile = "")
    {
        ArgumentNullException.ThrowIfNull(application);
        application.Dispatcher.VerifyAccess();
        if (application.Dispatcher.HasShutdownStarted)
            throw new InvalidOperationException("应用正在退出，无法接入 WPF 预览。");
        lock (Sessions)
        {
            if (Sessions.TryGetValue(application, out var existing)) return existing;
            var session = new WpfPreviewSession(application, (options ?? new()).Normalize(callerFile));
            Sessions.Add(application, session);
            return session;
        }
    }

    internal static void Detach(Application application, WpfPreviewSession session)
    {
        lock (Sessions)
            if (Sessions.TryGetValue(application, out var current) && ReferenceEquals(current, session))
                Sessions.Remove(application);
    }
}
