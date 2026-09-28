using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Media.Imaging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WpfPreview;

/// <summary>保存用户看到的那一帧及其选区，不重新截图，避免上下文与点选位置错位。</summary>
internal sealed class WpfPreviewContext
{
    private readonly object _gate = new();
    private readonly Dictionary<string, JObject> _frames = new();
    private readonly Queue<string> _order = new();

    internal void Remember(JObject frame)
    {
        lock (_gate)
        {
            var id = Guid.NewGuid().ToString("N");
            frame["frameId"] = id;
            frame["capturedAt"] = DateTimeOffset.Now.ToString("O");
            _frames.Add(id, frame);
            _order.Enqueue(id);
            while (_order.Count > 16) _frames.Remove(_order.Dequeue());
        }
    }

    internal JObject Export(JObject request, string outputRoot, string repositoryRoot)
    {
        repositoryRoot = Path.GetFullPath(repositoryRoot);
        // 仅导出副本使用相对路径；缓存帧仍保留原始位置，避免重复导出时再次转换。
        string RelativePath(string path) => Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');

        if (request["selections"] is not JArray selections || selections.Count is < 1 or > 8)
            throw new InvalidOperationException("请选取 1 至 8 个区域。");
        // 全部校验通过才创建文件；快照 ID 只查内存，不作为用户提供的磁盘路径使用。
        var prepared = new List<(JObject Frame, Rect Bounds, JArray Elements, string Note, string Kind)>();
        lock (_gate)
        {
            foreach (var item in selections.OfType<JObject>())
            {
                if (!_frames.TryGetValue((string?)item["frameId"] ?? "", out var frame))
                    throw new InvalidOperationException("选区的截图已过期，请移除该项并在刷新截图后重新选择。");
                var elements = (JArray)frame["elements"]!;
                Rect bounds;
                JArray matches;
                var kind = (string?)item["kind"];
                if (kind == "element")
                {
                    if (item["elementId"]?.Type != JTokenType.Integer)
                        throw new InvalidOperationException("控件编号无效。");
                    var element = elements.FirstOrDefault(e => (int?)e["id"] == (int)item["elementId"]!);
                    if (element is null) throw new InvalidOperationException("控件已不存在，请重新选择。");
                    bounds = ReadBounds(element["bounds"]!);
                    matches = new JArray(element.DeepClone());
                }
                else if (kind == "region")
                {
                    bounds = ReadBounds(item["bounds"]!);
                    matches = new JArray(elements.Where(e => IsRegionTarget(e, bounds)).Take(80).Select(e => e.DeepClone()));
                }
                else throw new InvalidOperationException("选区类型无效。");
                var width = (double)frame["width"]!;
                var height = (double)frame["height"]!;
                if (bounds.X < 0 || bounds.Y < 0 || bounds.Right > width + .01 || bounds.Bottom > height + .01)
                    throw new InvalidOperationException("选区超出截图范围。");
                bounds.Intersect(new Rect(0, 0, width, height));
                var note = (string?)item["note"] ?? "";
                if (note.Length > 4000) throw new InvalidOperationException("每项说明请控制在 4000 字以内。");
                prepared.Add((frame, bounds, matches, note, kind));
            }
        }
        if (prepared.Count != selections.Count) throw new InvalidOperationException("选区格式无效。");

        var folder = Path.Combine(outputRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        var exported = new JArray();
        var savedFrames = new Dictionary<string, string>();
        var prompt = new StringBuilder("请根据以下 WPF 界面上下文协助调整样式。先读取 context.json，并用图像查看工具查看各项局部截图；需要整体布局时再看窗口截图。源码定位为 view 时只是所属视图，不能当作控件的精确行号。\n");
        prompt.AppendLine("路径基准：接入预览时配置的项目根目录；context.json 中的路径使用同一基准，可由 repositoryRootFromContext 反向定位。");
        prompt.AppendLine($"上下文文件：{RelativePath(Path.Combine(folder, "context.json"))}");
        foreach (var entry in prepared)
        {
            var frame = entry.Frame;
            var frameId = (string)frame["frameId"]!;
            using var stream = new MemoryStream(Convert.FromBase64String(((string)frame["image"]!).Split(',')[1]));
            var image = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (!savedFrames.TryGetValue(frameId, out var fullPath))
            {
                fullPath = Path.Combine(folder, $"window-{savedFrames.Count + 1}.png");
                File.WriteAllBytes(fullPath, stream.ToArray());
                savedFrames.Add(frameId, fullPath);
            }
            var cropPath = Path.Combine(folder, $"selection-{exported.Count + 1}.png");
            var scaleX = image.PixelWidth / (double)frame["width"]!;
            var scaleY = image.PixelHeight / (double)frame["height"]!;
            var left = Math.Clamp((int)Math.Floor(entry.Bounds.X * scaleX), 0, image.PixelWidth - 1);
            var top = Math.Clamp((int)Math.Floor(entry.Bounds.Y * scaleY), 0, image.PixelHeight - 1);
            var right = Math.Clamp((int)Math.Ceiling(entry.Bounds.Right * scaleX), left + 1, image.PixelWidth);
            var bottom = Math.Clamp((int)Math.Ceiling(entry.Bounds.Bottom * scaleY), top + 1, image.PixelHeight);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(new CroppedBitmap(image, new Int32Rect(left, top, right - left, bottom - top))));
            using (var output = File.Create(cropPath)) encoder.Save(output);

            var all = (JArray)frame["elements"]!;
            // 将祖先单独保存，帮助识别模板 / 布局归属；不把整个窗口的数千节点塞进提示词。
            var ancestors = new Dictionary<int, JToken>();
            foreach (var element in entry.Elements)
            {
                var parent = (int?)element["parentId"];
                while (parent is { } id && id >= 0 && id < all.Count)
                {
                    if (!ancestors.TryAdd(id, all[id].DeepClone())) break;
                    parent = (int?)all[id]["parentId"];
                }
            }
            foreach (var element in entry.Elements.Concat(ancestors.Values))
            {
                if (element["source"] is JObject source && (string?)source["path"] is { Length: > 0 } sourcePath)
                    source["path"] = RelativePath(sourcePath);
            }
            var context = new JObject
            {
                ["number"] = exported.Count + 1, ["kind"] = entry.Kind, ["note"] = entry.Note,
                ["frameId"] = frameId, ["capturedAt"] = frame["capturedAt"]!.DeepClone(),
                ["windowTitle"] = frame["title"]?.DeepClone(), ["windowType"] = frame["windowType"]?.DeepClone(),
                ["windowWidth"] = frame["width"]!.DeepClone(), ["windowHeight"] = frame["height"]!.DeepClone(),
                ["bounds"] = new JObject { ["x"] = entry.Bounds.X, ["y"] = entry.Bounds.Y, ["width"] = entry.Bounds.Width, ["height"] = entry.Bounds.Height },
                ["screenshot"] = RelativePath(cropPath), ["windowScreenshot"] = RelativePath(fullPath),
                ["elements"] = entry.Elements, ["ancestors"] = new JArray(ancestors.Values),
                ["warning"] = frame["warning"]?.DeepClone(), ["frameTruncated"] = frame["truncated"]?.DeepClone(),
                ["elementsTruncated"] = entry.Kind == "region" && all.Count(e => IsRegionTarget(e, entry.Bounds)) > 80,
            };
            exported.Add(context);
            prompt.AppendLine($"\n{exported.Count}. 窗口：{(string?)frame["title"]}");
            prompt.AppendLine($"   我的要求：{(string.IsNullOrWhiteSpace(entry.Note) ? "（待补充，请结合我在消息中的描述）" : entry.Note)}");
            prompt.AppendLine($"   局部截图：{RelativePath(cropPath)}");
            var sources = entry.Elements.Select(e => e["source"]).OfType<JObject>()
                .Select(s => $"{s["path"]}:{s["line"]}（{s["kind"]}）").Distinct().Take(6);
            foreach (var source in sources) prompt.AppendLine("   XAML：" + source);
        }
        File.WriteAllText(Path.Combine(folder, "context.json"), new JObject
        {
            ["schemaVersion"] = 2, ["createdAt"] = DateTimeOffset.Now.ToString("O"),
            ["pathBase"] = "repositoryRoot",
            ["repositoryRootFromContext"] = Path.GetRelativePath(folder, repositoryRoot).Replace('\\', '/'),
            ["coordinateSpace"] = "WPF DIP relative to window content", ["selections"] = exported,
        }.ToString(Formatting.Indented), new UTF8Encoding(false));
        var text = prompt.ToString();
        File.WriteAllText(Path.Combine(folder, "prompt.txt"), text, new UTF8Encoding(false));
        return new JObject { ["text"] = text, ["folder"] = RelativePath(folder), ["count"] = exported.Count };
    }

    private static Rect ReadBounds(JToken token)
    {
        double Read(string key)
        {
            var value = token?[key];
            if (value?.Type is not (JTokenType.Float or JTokenType.Integer) || !double.IsFinite((double)value))
                throw new InvalidOperationException("选区坐标无效。");
            return (double)value;
        }
        var x = Read("x"); var y = Read("y"); var width = Read("width"); var height = Read("height");
        if (width <= 0 || height <= 0) throw new InvalidOperationException("选区不能为空。");
        return new Rect(x, y, width, height);
    }

    private static bool IsRegionTarget(JToken element, Rect region)
    {
        if ((string?)element["type"] == "System.Windows.Documents.AdornerLayer") return false;
        var bounds = ReadBounds(element["bounds"]!);
        var overlap = Rect.Intersect(bounds, region);
        return !overlap.IsEmpty && overlap.Width * overlap.Height >= bounds.Width * bounds.Height * .6;
    }
}
