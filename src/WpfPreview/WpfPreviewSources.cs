using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Diagnostics;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace WpfPreview;

internal sealed class WpfPreviewSources
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private readonly Dictionary<string, (string Path, XElement Root)> _views = new();

    internal static WpfPreviewSources FromDirectory(string directory)
    {
        var result = new WpfPreviewSources();
        if (!Directory.Exists(directory)) return result;
        Scan(directory);
        return result;

        void Scan(string folder)
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.xaml"))
            {
                try
                {
                    using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                    var root = XElement.Load(reader, LoadOptions.SetLineInfo);
                    var className = (string?)root.Attribute(Xaml + "Class");
                    if (className != null) result._views.TryAdd(className, (path, root));
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException) { }
            }
            foreach (var child in Directory.EnumerateDirectories(folder))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || new[] { "bin", "obj", "node_modules", "Web", "Libs" }.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) Scan(child);
            }
        }
    }

    internal JObject? Resolve(FrameworkElement element)
    {
        var source = VisualDiagnostics.GetXamlSourceInfo(element);
        if (source?.SourceUri is { IsAbsoluteUri: true, IsFile: true } uri && source.LineNumber > 0 && File.Exists(uri.LocalPath))
            return Location(uri.LocalPath, source.LineNumber, "exact");

        // BAML 未携带行号时，仅对所属视图中唯一的名称做定位；模板内部不猜测名称归属。
        for (DependencyObject? current = element; current != null; current = VisualTreeHelper.GetParent(current))
        {
            if (!_views.TryGetValue(current.GetType().FullName ?? "", out var view)) continue;
            if (element.TemplatedParent is null && !string.IsNullOrEmpty(element.Name))
            {
                var named = view.Root.DescendantsAndSelf().Where(e =>
                    (string?)e.Attribute(Xaml + "Name") == element.Name || (string?)e.Attribute("Name") == element.Name).Take(2).ToArray();
                if (named.Length == 1) return Location(view.Path, ((IXmlLineInfo)named[0]).LineNumber, "name");
            }
            return Location(view.Path, ((IXmlLineInfo)view.Root).LineNumber, "view");
        }
        return null;
    }

    private static JObject Location(string path, int line, string kind) =>
        new() { ["path"] = path, ["line"] = line, ["kind"] = kind };
}
