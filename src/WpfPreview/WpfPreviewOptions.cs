using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace WpfPreview;

/// <summary>只在开发入口显式 Attach；不会自动打开浏览器或修改目标窗口。</summary>
public sealed class WpfPreviewOptions
{
    public int Port { get; init; } = 5198;
    /// <summary>导出路径的基准。默认查找 Attach 调用文件所属的项目目录。</summary>
    public string? ProjectRoot { get; init; }
    /// <summary>XAML 索引范围，默认等于 ProjectRoot。</summary>
    public string? SourceRoot { get; init; }
    /// <summary>截图和批注存放目录，默认是 ProjectRoot/.wpf-preview/context。</summary>
    public string? ContextDirectory { get; init; }
    public string ApplicationName { get; init; } = "WPF";
    /// <summary>可选宿主入口，在 Application 的 Dispatcher 上执行。</summary>
    public Func<Task>? OpenWindowAsync { get; init; }
    public string OpenWindowLabel { get; init; } = "打开示例窗口";

    internal WpfPreviewOptions Normalize(string callerFile)
    {
        if (Port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        var projectRoot = Path.GetFullPath(ProjectRoot ?? FindProjectRoot(callerFile));
        return new WpfPreviewOptions
        {
            Port = Port,
            ProjectRoot = projectRoot,
            SourceRoot = Path.GetFullPath(SourceRoot ?? projectRoot, projectRoot),
            ContextDirectory = Path.GetFullPath(ContextDirectory ?? ".wpf-preview/context", projectRoot),
            ApplicationName = ApplicationName,
            OpenWindowAsync = OpenWindowAsync,
            OpenWindowLabel = OpenWindowLabel,
        };
    }

    private static string FindProjectRoot(string callerFile)
    {
        var directory = Path.GetDirectoryName(callerFile);
        if (directory != null && Directory.Exists(directory))
        {
            for (var current = new DirectoryInfo(directory); current != null; current = current.Parent)
                if (Directory.EnumerateFiles(current.FullName, "*.csproj").Any()) return current.FullName;
        }
        return Environment.CurrentDirectory;
    }
}
