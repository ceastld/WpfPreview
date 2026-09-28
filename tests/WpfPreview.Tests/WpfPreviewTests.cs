using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;
using WpfPreview;

namespace WpfPreview.Tests;

[TestClass]
public class WpfPreviewTests
{
    [TestMethod]
    public void Snapshot_AlignsPixelsAndBounds_ExcludesCollapsedAndClippedElements()
    {
        WpfTestHost.Run(() =>
        {
            var root = new Canvas { Width = 160, Height = 100, Background = Brushes.White, ClipToBounds = true, Margin = new Thickness(17) };
            var visible = new Border { Name = "Visible", Width = 40, Height = 20, Background = Brushes.Red };
            Canvas.SetLeft(visible, 30); Canvas.SetTop(visible, 15); root.Children.Add(visible);
            root.Children.Add(new Border { Name = "Hidden", Width = 20, Height = 20, Visibility = Visibility.Collapsed });
            var outside = new Border { Name = "Outside", Width = 20, Height = 20 };
            Canvas.SetLeft(outside, 200); root.Children.Add(outside);
            root.Measure(new Size(300, 200)); root.Arrange(new Rect(0, 0, 194, 134));
            var result = WpfPreviewSnapshot.Capture(root, WpfPreviewSources.FromDirectory("missing-preview-sources"));
            var elements = (JArray)result["elements"]!;
            Assert.IsFalse(elements.Any(e => (string)e["name"]! is "Hidden" or "Outside"));
            var item = elements.Single(e => (string)e["name"]! == "Visible");
            Assert.AreEqual(30d, (double)item["bounds"]!["x"]!);
            Assert.AreEqual(15d, (double)item["bounds"]!["y"]!);
            using var stream = new MemoryStream(Convert.FromBase64String(((string)result["image"]!).Split(',')[1]));
            var image = new PngBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            var pixels = new byte[4];
            image.CopyPixels(new Int32Rect((int)(35 * image.PixelWidth / (double)result["width"]!),
                (int)(20 * image.PixelHeight / (double)result["height"]!), 1, 1), pixels, 4, 0);
            Assert.AreEqual((byte)255, pixels[2], "元素坐标处应是红色像素。");
            Assert.AreEqual((byte)0, pixels[1]);
            StringAssert.Contains(result.ToString(), "\"parentId\"");
            StringAssert.Contains(result.ToString(), "\"properties\"");
        });
    }

    [TestMethod]
    public void SourceMapping_UsesUniqueName_AndLabelsViewFallbackHonestly()
    {
        var folder = Path.Combine(Path.GetTempPath(), "quicker-wpf-preview-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var file = Path.Combine(folder, "Preview.xaml");
            File.WriteAllText(file, """
                <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      x:Class="WpfPreview.Tests.PreviewTestRoot">
                  <Button x:Name="Target" />
                </Grid>
                """);
            var sources = WpfPreviewSources.FromDirectory(folder);
            WpfTestHost.Run(() =>
            {
                var root = new PreviewTestRoot();
                var button = new Button { Name = "Target" }; root.Children.Add(button);
                var location = sources.Resolve(button)!;
                Assert.AreEqual(file, (string)location["path"]!);
                Assert.AreEqual(4, (int)location["line"]!);
                Assert.AreEqual("name", (string)location["kind"]!);
                var unknown = new Border(); root.Children.Add(unknown);
                Assert.AreEqual("view", (string)sources.Resolve(unknown)!["kind"]!);
                Assert.IsNull(sources.Resolve(new TextBlock()));
            });
        }
        finally { File.Delete(Path.Combine(folder, "Preview.xaml")); Directory.Delete(folder); }
    }

    [TestMethod]
    public void Snapshot_RejectsUnarrangedContent()
    {
        WpfTestHost.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() =>
            WpfPreviewSnapshot.Capture(new Grid(), WpfPreviewSources.FromDirectory("missing-preview-sources"))));
    }

    [TestMethod]
    public void Context_ExportsOriginalPixels_SourceAncestry_AndMultipleSelections()
    {
        var folder = Path.Combine(Path.GetTempPath(), "quicker-wpf-context-" + Guid.NewGuid().ToString("N"));
        WpfTestHost.Run(() =>
        {
            var root = new Canvas { Width = 160, Height = 100, Background = Brushes.White };
            var target = new Border { Name = "Target", Width = 40, Height = 20, Background = Brushes.Red };
            Canvas.SetLeft(target, 30); Canvas.SetTop(target, 15); root.Children.Add(target);
            root.Measure(new Size(160, 100)); root.Arrange(new Rect(0, 0, 160, 100));
            var frame = WpfPreviewSnapshot.Capture(root, WpfPreviewSources.FromDirectory("missing-preview-sources"));
            frame["title"] = "测试窗口";
            var context = new WpfPreviewContext(); context.Remember(frame);
            var element = ((JArray)frame["elements"]!).Single(e => (string?)e["name"] == "Target");
            var sourcePath = Path.Combine(folder, "QuickerPc", "Preview.xaml");
            element["source"] = new JObject { ["path"] = sourcePath, ["line"] = 4, ["kind"] = "name" };
            frame["elements"]![0]!["source"] = new JObject
                { ["path"] = Path.Combine(folder, "QuickerPc", "Root.xaml"), ["line"] = 1, ["kind"] = "view" };
            // 选中之后改变真实 UI，不应改变已缓存快照导出的像素。
            target.Background = Brushes.Blue;
            try
            {
                var response = context.Export(new JObject { ["selections"] = new JArray(
                    new JObject { ["frameId"] = frame["frameId"]!.DeepClone(), ["kind"] = "element", ["elementId"] = element["id"]!.DeepClone(), ["note"] = "统一间距" },
                    new JObject { ["frameId"] = frame["frameId"]!.DeepClone(), ["kind"] = "region", ["bounds"] = element["bounds"]!.DeepClone(), ["note"] = "区域说明" }) }, Path.Combine(folder, "Tools/temp/wpf-preview-context"), folder);
                var output = Path.GetFullPath((string)response["folder"]!, folder);
                var data = JObject.Parse(File.ReadAllText(Path.Combine(output, "context.json")));
                Assert.AreEqual(2, (int)data["schemaVersion"]!);
                Assert.AreEqual("repositoryRoot", (string)data["pathBase"]!);
                Assert.AreEqual(folder, Path.GetFullPath((string)data["repositoryRootFromContext"]!, output));
                var selections = (JArray)data["selections"]!;
                Assert.AreEqual(2, selections.Count);
                Assert.AreEqual("统一间距", (string)selections[0]["note"]!);
                Assert.AreEqual("name", (string)selections[0]["elements"]![0]!["source"]!["kind"]!);
                Assert.AreEqual("QuickerPc/Preview.xaml", (string)selections[0]["elements"]![0]!["source"]!["path"]!);
                Assert.AreEqual("QuickerPc/Root.xaml", (string)selections[0]["ancestors"]![0]!["source"]!["path"]!);
                Assert.AreEqual("QuickerPc/Preview.xaml", (string)selections[1]["elements"]![0]!["source"]!["path"]!);
                Assert.AreEqual(sourcePath, (string)element["source"]!["path"]!, "导出不能改变缓存帧的绝对定位。");
                Assert.IsFalse(Path.IsPathRooted((string)response["folder"]!));
                Assert.IsTrue(File.Exists(Path.GetFullPath((string)selections[0]["windowScreenshot"]!, folder)));
                Assert.AreEqual(1, ((JArray)selections[0]["ancestors"]!).Count);
                Assert.AreEqual(1, ((JArray)selections[1]["elements"]!).Count, "不应将覆盖全窗的根容器当作区域内目标。");
                Assert.AreEqual(selections[0]["windowScreenshot"]!.ToString(), selections[1]["windowScreenshot"]!.ToString());
                using var imageFile = File.OpenRead(Path.GetFullPath((string)selections[0]["screenshot"]!, folder));
                var image = new PngBitmapDecoder(imageFile, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
                var pixel = new byte[4]; image.CopyPixels(new Int32Rect(image.PixelWidth / 2, image.PixelHeight / 2, 1, 1), pixel, 4, 0);
                Assert.AreEqual((byte)255, pixel[2]); Assert.AreEqual((byte)0, pixel[0]);
                StringAssert.Contains((string)response["text"]!, (string)response["folder"]! + "/context.json");
                StringAssert.Contains((string)response["text"]!, "QuickerPc/Preview.xaml:4");
                Assert.IsFalse(((string)response["text"]!).Contains(folder));
                Assert.IsFalse(data.ToString().Contains(folder.Replace("\\", "\\\\")));
                StringAssert.Contains((string)response["text"]!, "selection-2.png");
            }
            finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
        });
    }

    [TestMethod]
    public void Context_RejectsStaleFrames_AndOutOfBoundsBeforeWriting()
    {
        var context = new WpfPreviewContext();
        var frame = new JObject { ["width"] = 100, ["height"] = 100, ["elements"] = new JArray() };
        context.Remember(frame);
        var folder = Path.Combine(Path.GetTempPath(), "quicker-wpf-context-invalid-" + Guid.NewGuid().ToString("N"));
        var selection = new JObject { ["frameId"] = frame["frameId"]!.DeepClone(), ["kind"] = "region",
            ["bounds"] = new JObject { ["x"] = 95, ["y"] = 0, ["width"] = 20, ["height"] = 10 } };
        var request = new JObject { ["selections"] = new JArray(selection) };
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Export(request, folder, folder));
        selection["bounds"]!["x"] = 0;
        for (var i = 0; i < 16; i++) context.Remember((JObject)frame.DeepClone());
        Assert.ThrowsExactly<InvalidOperationException>(() => context.Export(request, folder, folder));
        Assert.IsFalse(Directory.Exists(folder));
    }
}

internal sealed class PreviewTestRoot : Grid { }
