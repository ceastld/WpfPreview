using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json.Linq;

namespace WpfPreview.Tests;

[TestClass]
public class AttachTests
{
    [TestMethod]
    public void Attach_ServesSnapshotAndContext_WithoutProductDependencies_AndReleasesPort()
    {
        WpfTestHost.Run(async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "wpf-preview-attach-" + Guid.NewGuid().ToString("N"));
            var port = FreePort();
            var app = Application.Current;
            var options = new WpfPreviewOptions { Port = port, ProjectRoot = root, ApplicationName = "独立测试" };
            using var session = app.AttachWpfPreview(options);
            using var client = new HttpClient { BaseAddress = session.PreviewUri, Timeout = TimeSpan.FromSeconds(10) };
            Assert.AreSame(session, app.AttachWpfPreview(options));
            var config = JObject.Parse(await client.GetStringAsync("config"));
            Assert.AreEqual("独立测试", (string?)config["applicationName"]);
            Assert.IsFalse((bool)config["canOpenWindow"]!);
            var html = await client.GetStringAsync("");
            StringAssert.Contains(html, "id=\"annotation\"");
            Assert.IsFalse(html.Contains("/api/rpc"));
            var window = new Window
            {
                Title = "独立测试窗口", Width = 300, Height = 200, ShowInTaskbar = false,
                Content = new Grid { Background = Brushes.White, Children = { new Button { Name = "Target", Content = "测试" } } },
            };
            window.Show();
            window.UpdateLayout();
            try
            {
                var windows = JArray.Parse(await client.GetStringAsync("windows"));
                var id = windows.Single(w => (string?)w["title"] == window.Title)["id"];
                var frame = JObject.Parse(await client.GetStringAsync("frame?id=" + id));
                var element = ((JArray)frame["elements"]!).Single(e => (string?)e["name"] == "Target");
                var body = new JObject { ["selections"] = new JArray(new JObject
                {
                    ["frameId"] = frame["frameId"]!.DeepClone(), ["kind"] = "element",
                    ["elementId"] = element["id"]!.DeepClone(), ["note"] = "调整按钮间距",
                }) };
                using var post = Request("context", body.ToString());
                using var response = await client.SendAsync(post);
                response.EnsureSuccessStatusCode();
                var exported = JObject.Parse(await response.Content.ReadAsStringAsync());
                var folder = Path.GetFullPath((string)exported["folder"]!, root);
                Assert.IsTrue(File.Exists(Path.Combine(folder, "selection-1.png")));
                var context = JObject.Parse(File.ReadAllText(Path.Combine(folder, "context.json")));
                Assert.AreEqual(root, Path.GetFullPath((string)context["repositoryRootFromContext"]!, folder));
                StringAssert.Contains((string)exported["text"]!, "调整按钮间距");
                Assert.IsFalse(((string)exported["text"]!).Contains("Quicker"));
                Assert.IsFalse(((string)exported["text"]!).Contains(root));
                window.Close();
                using var stale = await client.GetAsync("frame?id=" + id);
                Assert.AreEqual(HttpStatusCode.BadRequest, stale.StatusCode);
            }
            finally
            {
                session.Dispose();
                session.Dispose();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            using var replacement = app.AttachWpfPreview(options);
            Assert.AreNotSame(session, replacement);
            StringAssert.Contains(await client.GetStringAsync("config"), "独立测试");
        });
    }

    [TestMethod]
    public void Attach_RejectsForeignOriginAndMissingHeader_InvokesHostOnUiThread()
    {
        WpfTestHost.Run(async () =>
        {
            var app = Application.Current;
            var calls = 0;
            using var session = app.AttachWpfPreview(new WpfPreviewOptions
            {
                Port = FreePort(), OpenWindowAsync = () =>
                {
                    app.Dispatcher.VerifyAccess(); calls++; return Task.CompletedTask;
                },
            });
            using var client = new HttpClient { BaseAddress = session.PreviewUri, Timeout = TimeSpan.FromSeconds(10) };
            using var foreign = Request("open-window", "{}");
            foreign.Headers.Add("Origin", "https://foreign.example");
            using var rejected = await client.SendAsync(foreign);
            Assert.AreEqual(HttpStatusCode.Forbidden, rejected.StatusCode);
            using var missing = await client.PostAsync("open-window", new StringContent("{}", Encoding.UTF8, "application/json"));
            Assert.AreEqual(HttpStatusCode.Forbidden, missing.StatusCode);
            Assert.AreEqual(0, calls);
            using var valid = Request("open-window", "{}");
            using var accepted = await client.SendAsync(valid);
            accepted.EnsureSuccessStatusCode();
            Assert.AreEqual(1, calls);
        });
    }

    private static HttpRequestMessage Request(string path, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Wpf-Preview", "context");
        return request;
    }

    internal static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
