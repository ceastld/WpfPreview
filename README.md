# WPF Preview

用于开发阶段的 WPF 界面预览库。真实窗口截图、控件树、XAML 定位、框选和批注都在库内；浏览器页面作为嵌入资源随 DLL 分发，无需 WebView、Node 或前端服务。库目标为 `net8.0-windows`，Quicker 通过 Debug 项目引用接入。

项目仓库：[ceastld/WpfPreview](https://github.com/ceastld/WpfPreview)，采用 [MIT 许可证](LICENSE)。这是早期开发工具，当前版本为 `0.1.0`。

## 获取

需要 Windows 和 .NET 8 或更新版本的 SDK。可以克隆源码后使用 ProjectReference，也可以从 [GitHub Releases](https://github.com/ceastld/WpfPreview/releases) 下载 `.nupkg`，将下载目录添加为本地 NuGet 源。当前未发布到 NuGet.org。

```powershell
git clone https://github.com/ceastld/WpfPreview.git
cd WpfPreview
dotnet run --project samples/WpfPreview.Sample
```

## 接入

在应用项目中添加仅 Debug 生效的 ProjectReference，路径指向 `src/WpfPreview/WpfPreview.csproj`。然后在 `App.OnStartup` 中接入：

```csharp
#if DEBUG
using WpfPreview;
#endif

protected override void OnStartup(StartupEventArgs e)
{
#if DEBUG
    this.AttachWpfPreview();
#endif
    base.OnStartup(e);
}
```

打开 `http://127.0.0.1:5198/wpf-preview/`。默认通过调用文件向上查找 `.csproj`，将该目录作为源码扫描和相对路径基准；源码移动或多项目解决方案应显式配置：

```csharp
this.AttachWpfPreview(new WpfPreviewOptions
{
    Port = 5198,
    ApplicationName = "我的应用",
    ProjectRoot = @"D:\Projects\MyApp", // 所有导出路径共用的基准
    SourceRoot = @"D:\Projects\MyApp\src", // 可省略，默认扫描项目根目录
    ContextDirectory = ".wpf-preview/context", // 相对 ProjectRoot
});
```

Attach 须在 Application 的 UI 线程调用。返回的 `WpfPreviewSession.PreviewUri` 是浏览地址；应用退出自动停止服务，也可提前 `Dispose()`。重复 Attach 返回同一会话，采用首次配置；释放后可以重新接入。端口被占用时明确抛出异常，不连接或结束其它进程。

宿主可用 `OpenWindowAsync` 和 `OpenWindowLabel` 提供菜单入口。回调在 UI 线程调用，应完成“显示窗口”后返回，不应等待窗口关闭；窗口所有权与业务由宿主处理。不配置时页面隐藏入口。

## 使用与边界

- 拖动框选、单击控件，直接在选区旁输入批注；Enter 保存并复制上下文，Shift+Enter 换行。中文输入法的确认键不会误提交。
- 批注框内可切换父级／子级控件，保留已输入文字；向上扩大选区后，向下会回到原来的分支。
- 批注框根据四周空间避开选区，竖长选区优先显示在侧边；缩放、滚动时保持在画布可见范围内。
- 页面固定在视口内；画布支持缩放、滚动，批注可以合并后复制。
- 导出局部截图、原始窗口截图和 `context.json`，路径相对 `ProjectRoot`。上下文文件包含 `repositoryRootFromContext`，可从文件所在目录还原基准。建议忽略 `.wpf-preview/`。
- 当前交付方式是剪贴板，需要在 Codex 或其它助手中粘贴。尚未接入 MCP，也不会自动写入 Codex 桌面输入框。
- XAML 精确位置依赖 WPF 调试源信息；可在启动进程前设置 `ENABLE_XAML_DIAGNOSTICS_SOURCE_INFO=1`。否则按所属视图内唯一名称定位，最后回退到所属视图，明确标注定位依据。
- 只读检查当前 Application UI 线程的可见窗口。截图不向窗口派发鼠标或键盘输入；WebView2、原生 HWND 内容可能无法截图。源码变更需重新构建运行。
- 服务只绑定 `127.0.0.1`，拒绝跨来源请求。不要在正式产品中无条件启动开发服务。库本身可用 Release 构建打包，宿主应限制引用与启动入口。

## 示例与验证

`samples/WpfPreview.Sample` 是不引用 Quicker 的 WPF 应用，只有一处 Attach。`WPF_PREVIEW_PORT` 可配置其端口。`tests/WpfPreview.Tests` 验证截图坐标、XAML 定位、相对路径、HTTP 接入及释放。

在独立仓库根目录运行：

```powershell
dotnet build samples/WpfPreview.Sample/WpfPreview.Sample.csproj -c Debug
dotnet test tests/WpfPreview.Tests/WpfPreview.Tests.csproj -c Debug
dotnet pack src/WpfPreview/WpfPreview.csproj -c Release -o artifacts/packages
```

GitHub Actions 在 Windows 上构建示例、运行测试并生成 NuGet 包。库没有 Quicker 依赖；仓库中的 `Directory.Build.*` 和 `Directory.Packages.props` 使它也能作为子模块嵌入已有解决方案。

网页交互回归测试只需 Node.js 22 或更新版本，无需安装 npm 包，在仓库根目录运行：

```powershell
node --test tests/Web/preview.test.cjs
```

测试直接加载网页中的实际脚本，覆盖父子层级往返、草稿与导出选区、Enter／输入法防误提交，以及 729 组浮层避让位置。GitHub Actions 会独立运行这组测试。测试中的 DOM、布局与网络是替身；CSS 排版、真实 WPF 截图和浏览器剪贴板权限仍需人工或浏览器验证。Node 仅用于开发测试，使用库不需要 Node。
