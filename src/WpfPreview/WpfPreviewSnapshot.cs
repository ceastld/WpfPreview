using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Newtonsoft.Json.Linq;

namespace WpfPreview;

internal static class WpfPreviewSnapshot
{
    internal static JObject Capture(FrameworkElement root, WpfPreviewSources sources)
    {
        root.Dispatcher.VerifyAccess();
        root.UpdateLayout();
        var width = root.ActualWidth;
        var height = root.ActualHeight;
        if (width <= 0 || height <= 0) throw new InvalidOperationException("窗口尚未完成布局，请稍后刷新。");
        var dpi = VisualTreeHelper.GetDpi(root);
        var scale = Math.Min(1, 2400 / Math.Max(width * dpi.DpiScaleX, height * dpi.DpiScaleY));
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * dpi.DpiScaleX * scale),
            (int)Math.Ceiling(height * dpi.DpiScaleY * scale), 96 * dpi.DpiScaleX * scale,
            96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
        // 显式 Viewbox 保证 Content 的 Margin/Offset 不会使图片和元素坐标错位。
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen())
        {
            var rectangle = new Rect(0, 0, width, height);
            dc.DrawRectangle(Window.GetWindow(root)?.Background ?? Brushes.Transparent, null, rectangle);
            dc.DrawRectangle(new VisualBrush(root)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect((Point)VisualTreeHelper.GetOffset(root), root.RenderSize),
                Stretch = Stretch.Fill,
            }, null, rectangle);
        }
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);

        var elements = new JArray();
        var nativeContent = false;
        var truncated = false;
        Visit(root, null, new Rect(0, 0, width, height), 0);
        return new JObject
        {
            ["width"] = width, ["height"] = height,
            ["image"] = "data:image/png;base64," + Convert.ToBase64String(output.ToArray()),
            ["elements"] = elements,
            ["warning"] = nativeContent ? "包含 WebView2 / 原生窗口内容，该区域可能无法由 WPF 渲染到图片。" : "",
            ["truncated"] = truncated,
        };

        void Visit(DependencyObject visual, int? parent, Rect clip, int depth)
        {
            if (elements.Count >= 3000 || depth > 100) { truncated = true; return; }
            if (visual is HwndHost) nativeContent = true;
            if (visual is not Visual) return;
            if (visual is UIElement ui && ui.Visibility != Visibility.Visible) return;
            var transform = ((Visual)visual).TransformToAncestor(root);
            if (visual is UIElement clipped)
            {
                var geometry = VisualTreeHelper.GetClip(clipped);
                if (geometry != null) clip.Intersect(transform.TransformBounds(geometry.Bounds));
                if (clipped.ClipToBounds) clip.Intersect(transform.TransformBounds(new Rect(clipped.RenderSize)));
            }
            if (clip.IsEmpty) return;
            if (visual is FrameworkElement element && element.ActualWidth > 0 && element.ActualHeight > 0)
            {
                var bounds = transform.TransformBounds(new Rect(element.RenderSize));
                bounds.Intersect(clip);
                if (!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0)
                {
                    var id = elements.Count;
                    var properties = new JObject
                    {
                        ["尺寸"] = $"{element.ActualWidth:0.##} × {element.ActualHeight:0.##}",
                        ["Margin"] = element.Margin.ToString(),
                        ["水平对齐"] = element.HorizontalAlignment.ToString(),
                        ["垂直对齐"] = element.VerticalAlignment.ToString(),
                        ["DataContext 类型"] = element.DataContext?.GetType().FullName ?? "",
                    };
                    if (element is Control control)
                    {
                        properties["Padding"] = control.Padding.ToString();
                        properties["FontSize"] = control.FontSize;
                        properties["Background"] = control.Background?.ToString();
                        properties["Foreground"] = control.Foreground?.ToString();
                    }
                    if (element is TextBlock text)
                    {
                        properties["FontSize"] = text.FontSize;
                        properties["Foreground"] = text.Foreground?.ToString();
                    }
                    elements.Add(new JObject
                    {
                        ["id"] = id, ["parentId"] = parent,
                        ["type"] = element.GetType().FullName, ["name"] = element.Name,
                        ["bounds"] = new JObject { ["x"] = bounds.X, ["y"] = bounds.Y, ["width"] = bounds.Width, ["height"] = bounds.Height },
                        ["source"] = sources.Resolve(element), ["properties"] = properties,
                    });
                    parent = id;
                }
            }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(visual); i++)
                Visit(VisualTreeHelper.GetChild(visual, i), parent, clip, depth + 1);
        }
    }
}
