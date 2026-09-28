using System;
using System.Windows;

namespace WpfPreview.Sample;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
#if DEBUG
        this.AttachWpfPreview(new WpfPreviewOptions
        {
            ApplicationName = "独立 WPF 示例",
            Port = int.TryParse(Environment.GetEnvironmentVariable("WPF_PREVIEW_PORT"), out var port) ? port : 5198,
        });
#endif
        base.OnStartup(e);
    }
}
