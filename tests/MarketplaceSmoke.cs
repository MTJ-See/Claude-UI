using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClaudeUI
{
    internal static class MarketplaceSmoke
    {
        private static int checks;
        private static void Check(bool value, string name) { if (!value) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
        [STAThread] public static int Main(string[] args)
        {
            try
            {
                if (args.Contains("__mcp_fixture__"))
                {
                    if (args.Contains("get"))
                    {
                        if (args.Contains("existing")) return 0;
                        if (args.Contains("broken")) { Console.WriteLine("Configuration parse error"); return 2; }
                        Console.WriteLine("No MCP server found with name: demo"); return 1;
                    }
                    if (args.Contains("add-json")) { Console.WriteLine("Added demo " + String.Join(" ", args)); return 0; }
                    return 2;
                }
                Run(Core());
                Run(InstallChecks());
                InstalledExtensionsSmoke.Run();
                if (args.Contains("--live")) Run(Live());
                Ui(args[0]);
                Console.WriteLine("Marketplace checks passed: " + checks); return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static async Task Core()
        {
            int calls = 0; string credential = ""; string lastBody = null, lastPath = null;
            var http = new MarketplaceHttp(() => credential, (path, body, secret, token) =>
            {
                Interlocked.Increment(ref calls); lastBody = body; lastPath = path;
                return new MarketplaceResponse { Json = path.StartsWith("/mcp") ?
                    "{\"success\":true,\"data\":{\"mcp_server_list\":[{\"id\":\"@demo/fetch\",\"chinese_name\":\"网页抓取\",\"view_count\":12,\"categories\":[\"browser\"]}],\"total_count\":120}}" :
                    "{\"success\":true,\"data\":{\"skills\":[{\"id\":\"demo/review\",\"display_name\":\"\",\"description\":\"代码检查\",\"downloads\":5,\"category\":\"developer-tools\"}],\"total\":21}}" };
            });
            var provider = new ModelScopeMarketplaceProvider(http);
            var both = await Task.WhenAll(provider.ListAsync(MarketplaceKind.Skill, 1, "a&b", false, CancellationToken.None), provider.ListAsync(MarketplaceKind.Skill, 1, "a&b", false, CancellationToken.None));
            Check(calls == 1, "Concurrent identical requests share memory result");
            Check(lastPath.Contains("a%26b"), "Search query is escaped");
            Check(both[0].Items[0].Name == "demo/review" && both[0].Items[0].Popularity.Contains("下载 5"), "Skill fallback name and popularity");
            Check(both[0].HasNext, "Skill pagination");
            credential = "synthetic-not-real";
            await provider.ListAsync(MarketplaceKind.Skill, 1, "a&b", false, CancellationToken.None);
            Check(calls == 2, "Cache isolated by credential");
            await provider.ListAsync(MarketplaceKind.Skill, 1, "a&b", true, CancellationToken.None);
            Check(calls == 3, "Explicit refresh bypasses cache");
            http.Clear(); await provider.ListAsync(MarketplaceKind.Skill, 1, "a&b", false, CancellationToken.None);
            Check(calls == 4, "Clearing cache reissues request");
            var mcp = await provider.ListAsync(MarketplaceKind.Mcp, 5, "fetch", false, CancellationToken.None);
            Check(lastBody.Contains("\"page_number\":5") && mcp.Items[0].Name == "网页抓取", "MCP PUT body and raw list mapping");
            Check(!mcp.HasNext && mcp.Notice.Contains("100"), "MCP API pagination ceiling is explained");
            try { await provider.ListAsync(MarketplaceKind.Mcp, 6, "", false, CancellationToken.None); throw new Exception("accepted invalid page"); } catch (ArgumentOutOfRangeException) { Check(true, "MCP rejects unsupported page"); }
            var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await http.GetAsync("/skills", null, true, cancelled.Token); throw new Exception("ignored cancel"); } catch (OperationCanceledException) { Check(true, "Cancelled request does not execute"); }
            try { ModelScopeMarketplaceProvider.Data("{\"success\":false,\"data\":{}}"); throw new Exception("accepted API failure"); } catch (InvalidOperationException) { Check(true, "API failure envelope rejected"); }
            var detail = ModelScopeMarketplaceProvider.Map(MarketplaceKind.Mcp, ModelScopeMarketplaceProvider.Data("{\"success\":true,\"data\":{\"id\":\"@a/b\",\"server_config\":[{\"command\":\"uvx\"}],\"env_schema\":{\"required\":[\"API_KEY\"]}}}"));
            Check(detail.Configuration.Contains("API_KEY") && detail.Installation.Contains("uvx") && detail.Permissions.Contains("未提供"), "Detail metadata and absent permissions");
            Uri uri;
            Check(!MarketplaceView.SafeLink("file:///C:/Windows", out uri) && !MarketplaceView.SafeLink("https://localhost/a", out uri) && MarketplaceView.SafeLink("https://github.com/demo/repo", out uri), "External links allow public HTTPS only");
            var data = new AppData(); var credentials = new MarketplaceCredentials(data);
            credentials.Set("fake-ms", "fake-gh", true);
            Check(data.MarketplaceModelScopeToken != "fake-ms" && new MarketplaceCredentials(data).ModelScope == "fake-ms", "Saved credentials encrypted and recoverable");
            credentials.Set("", "", false);
            Check(data.MarketplaceModelScopeToken == null && credentials.ModelScope == "", "Blank session clears saved token and suppresses environment fallback");
        }
        private static void Run(Task task)
        {
            var frame = new DispatcherFrame();
            // Pump the STA dispatcher while waiting for async UI and network continuations.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
            timer.Tick += delegate { if (task.IsCompleted) { frame.Continue = false; timer.Stop(); } };
            timer.Start(); Dispatcher.PushFrame(frame); task.GetAwaiter().GetResult();
        }
        private static async Task Live()
        {
            var provider = new ModelScopeMarketplaceProvider(new MarketplaceHttp(() => ""));
            using (var timeout = new CancellationTokenSource(60000))
            foreach (var kind in new[] { MarketplaceKind.Mcp, MarketplaceKind.Skill })
            {
                var page = await provider.ListAsync(kind, 1, "", true, timeout.Token);
                Check(page.Items.Count > 0 && page.Total > 0, "Live anonymous " + kind + " list");
                var detail = await provider.DetailAsync(kind, page.Items[0].Id, true, timeout.Token);
                Check(detail.Id == page.Items[0].Id && detail.Installation.Length > 0, "Live anonymous " + kind + " detail");                if (kind == MarketplaceKind.Skill)
                {
                    string target = Path.Combine(Path.GetTempPath(), "claudeui-live-skill-" + Guid.NewGuid().ToString("N"));
                    try
                    {
                        await MarketplaceInstaller.DownloadSkill(detail, target, "download-check", "", line => Console.WriteLine("DOWNLOAD " + line), timeout.Token);
                        Check(File.Exists(Path.Combine(target, "download-check", "SKILL.md")), "Live official Skill download and installation in isolated directory");
                    }
                    finally { MarketplaceInstaller.CheckTree(target); if (Directory.Exists(target)) Directory.Delete(target, true); }
                }
            }
        }
        private static void Ui(string output)
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Window window;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeUI.MainWindow.xaml")) window = (Window)XamlReader.Load(stream);
            Check(window.FindName("McpMarketButton") != null && window.FindName("SkillMarketButton") != null, "Both market entry points exist");
            var host = (Grid)window.FindName("MarketplacePage");
            string uiFixture=Path.Combine(output,"ui-installed"), uiProject=Path.Combine(uiFixture,"project"), uiConfig=Path.Combine(uiFixture,"config");
            Directory.CreateDirectory(Path.Combine(uiProject,".git")); Directory.CreateDirectory(uiConfig);
            File.WriteAllText(Path.Combine(uiConfig,".claude.json"),new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(new {
                mcpServers=new { fetch=new { command="uvx" }, remote=new {type="http",url="https://example.com/mcp"} },
                projects=new Dictionary<string,object>{{Path.GetFullPath(uiProject),new {disabledMcpServers=new[]{"remote"}}}}
            }));
            var view = new MarketplaceView(window, host, new AppData(), () => true, new FakeProvider(),()=>new MarketplaceInstallContext { Workspace=uiProject,ConfigDirectory=uiConfig });
            foreach (var name in new[] { "ChatPage", "ToolsPage", "StatsPage" }) ((Grid)window.FindName(name)).Visibility = Visibility.Collapsed;
            host.Visibility = Visibility.Visible;
            Run(view.Open(MarketplaceKind.Mcp));
            foreach (bool dark in new[] { false, true })
            {
                Theme.Apply(window.Resources, dark);
                var root = (Grid)window.Content;
                root.SetResourceReference(Panel.BackgroundProperty, "Canvas");
                root.Measure(new Size(1280, 860)); root.Arrange(new Rect(0, 0, 1280, 860)); root.UpdateLayout();
                var bitmap = new RenderTargetBitmap(1280, 860, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var stream = File.Create(Path.Combine(output, dark ? "market-dark.png" : "market-light.png"))) encoder.Save(stream);
            }
            Check(!Descendants(host).OfType<Button>().Any(b=>Convert.ToString(b.Content)=="返回列表") && Descendants(host).OfType<Button>().Any(b=>Convert.ToString(b.Content)=="已安装"), "Installed replaces return-to-list action");
            Check(host.ActualWidth > 500, "Market lays out within main content area");
            var cards = Descendants(host).OfType<ResponsiveCardPanel>().Single();
            Check(cards.Children.Count == 2, "Market cards preserve every API result");
            Run(view.Open(MarketplaceKind.Skill));
            Theme.Apply(window.Resources, true);
            var skillRoot = (Grid)window.Content; skillRoot.Measure(new Size(1280, 860)); skillRoot.Arrange(new Rect(0, 0, 1280, 860)); skillRoot.UpdateLayout();
            var skillBitmap = new RenderTargetBitmap(1280, 860, 96, 96, PixelFormats.Pbgra32); skillBitmap.Render(skillRoot);
            var skillEncoder = new PngBitmapEncoder(); skillEncoder.Frames.Add(BitmapFrame.Create(skillBitmap));
            using (var file = File.Create(Path.Combine(output, "skill-dark.png"))) skillEncoder.Save(file);
            Check(Descendants(host).OfType<TextBlock>().Any(label => label.Text == "Skill 市场"), "Skill market uses the same native card layout");
            Run(view.Open(MarketplaceKind.Mcp));

            var detail = (Task)typeof(MarketplaceView).GetMethod("LoadDetail", BindingFlags.NonPublic | BindingFlags.Instance)
                .Invoke(view, new object[] { new MarketplaceItem { Provider = "modelscope", Kind = MarketplaceKind.Mcp, Id = "@demo/fetch" }, false });
            Run(detail);
            var panel = (StackPanel)typeof(MarketplaceView).GetField("content", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(view);
            Check(Descendants(panel).OfType<TextBlock>().Any(t => t.Text.Contains("DEMO_KEY")), "Detail renders structured configuration fields");
            Check(Descendants(panel).OfType<TextBlock>().Any(t => t.Text.Contains("未提供权限")), "Detail explicitly displays unknown permissions");
            Check(Descendants(panel).OfType<MarkdownReply>().Any(), "Detail uses Markdown typography");
            Check(!Descendants(host).OfType<Button>().Any(b => Convert.ToString(b.Content).Contains("访问令牌")), "Removed marketplace token button");
            foreach (bool dark in new[] { false, true }) { Theme.Apply(window.Resources, dark); var root = (Grid)window.Content; root.Measure(new Size(1280,860)); root.Arrange(new Rect(0,0,1280,860)); root.UpdateLayout(); var bitmap = new RenderTargetBitmap(1280,860,96,96,PixelFormats.Pbgra32); bitmap.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file = File.Create(Path.Combine(output, dark ? "detail-dark.png" : "detail-light.png"))) encoder.Save(file); }
            new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
            var installItem = new MarketplaceItem { Provider="modelscope", Id="demo/review", Kind=MarketplaceKind.Skill, Name="示例 Skill" };
            var installDialog = new MarketplaceInstallDialog(window, installItem, new MarketplaceInstallContext { Workspace=output }, new MarketplaceCredentials(new AppData()));
            var installWindow = (Window)typeof(MarketplaceInstallDialog).GetField("dialog", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(installDialog);
            var installRoot = (ScrollViewer)installWindow.Content;
            installRoot.SetResourceReference(Control.BackgroundProperty,"Canvas");
            installRoot.Measure(new Size(680,760)); installRoot.Arrange(new Rect(0,0,680,760)); installRoot.UpdateLayout();
            Check(Descendants(installRoot).OfType<Button>().Any(b=>Convert.ToString(b.Content)=="确认安装"), "Installation confirmation dialog constructs and lays out");
            var installBitmap = new RenderTargetBitmap(680,760,96,96,PixelFormats.Pbgra32); installBitmap.Render(installRoot);
            var installEncoder = new PngBitmapEncoder(); installEncoder.Frames.Add(BitmapFrame.Create(installBitmap));
            using(var file=File.Create(Path.Combine(output,"install-dialog.png"))) installEncoder.Save(file);
            installWindow.Close();
            Run((Task)typeof(MarketplaceView).GetMethod("LoadInstalled",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(view,new object[0]));
            Check(Descendants(panel).OfType<System.Windows.Controls.Primitives.ToggleButton>().Count()==2,"Installed page scans fixture configurations and exposes slider switches");
            foreach(bool dark in new[]{false,true})
            {
                Theme.Apply(window.Resources,dark); var root=(Grid)window.Content; root.Measure(new Size(1280,860)); root.Arrange(new Rect(0,0,1280,860)); root.UpdateLayout();
                var bitmap=new RenderTargetBitmap(1280,860,96,96,PixelFormats.Pbgra32); bitmap.Render(root);
                var encoder=new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using(var file=File.Create(Path.Combine(output,dark?"installed-dark.png":"installed-light.png"))) encoder.Save(file);
            }
            MarketplaceInstaller.CheckTree(uiFixture); Directory.Delete(uiFixture,true);
            view.Dispose(); window.Close();
        }
        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            yield return root;
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
                foreach (var descendant in Descendants(child)) yield return descendant;
        }
        private static async Task InstallChecks()
        {
            string root = Path.Combine(Path.GetTempPath(), "claudeui-install-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string good = Path.Combine(root,"good.zip");
                using(var archive = ZipFile.Open(good, ZipArchiveMode.Create))
                using(var writer = new StreamWriter(archive.CreateEntry("demo/SKILL.md").Open())) writer.Write("---\nname: demo\ndescription: test\n---\n# Example");
                MarketplaceInstaller.Extract(good, Path.Combine(root,"unpacked"), CancellationToken.None);
                Check(File.Exists(Path.Combine(root,"unpacked","demo","SKILL.md")), "Skill archive extracts into staging");
                string bad = Path.Combine(root,"bad.zip");
                using(var archive = ZipFile.Open(bad, ZipArchiveMode.Create))
                using(var writer = new StreamWriter(archive.CreateEntry("../escaped.txt").Open())) writer.Write("no");
                try { MarketplaceInstaller.Extract(bad, Path.Combine(root,"blocked"), CancellationToken.None); throw new Exception("accepted traversal"); }
                catch(InvalidOperationException) { Check(!File.Exists(Path.Combine(root,"escaped.txt")), "Zip traversal is blocked"); }
                var item = new MarketplaceItem { Provider="modelscope", Id="@demo/review", Kind=MarketplaceKind.Skill };
                Check(MarketplaceInstaller.ArchiveUrl(item)=="https://www.modelscope.cn/skills/demo/review/archive/zip/master", "Skill official archive URL");
                Directory.CreateDirectory(Path.Combine(root,"existing"));
                try { await MarketplaceInstaller.DownloadSkill(item,root,"existing","",line=>{},CancellationToken.None); throw new Exception("overwrote directory"); }
                catch(InvalidOperationException) { Check(Directory.Exists(Path.Combine(root,"existing")),"Skill collision preserves existing directory"); }
                var mapped = ModelScopeMarketplaceProvider.Map(MarketplaceKind.Mcp, ModelScopeMarketplaceProvider.Data("{\"success\":true,\"data\":{\"id\":\"@demo/test\",\"is_hosted\":true,\"server_config\":[{\"mcpServers\":{\"test\":{\"command\":\"uvx\",\"args\":[\"test\"],\"env\":{\"DEMO_KEY\":\"${DEMO_KEY}\"}}}}],\"env_schema\":{\"properties\":{\"DEMO_KEY\":{\"description\":\"测试密钥\"}},\"required\":[\"DEMO_KEY\"]}}}"));
                Check(mapped.McpOptions.Count==1 && mapped.Parameters.Count==1 && mapped.Hosted,"MCP installation metadata is structured");
                mapped.McpOptions[0].Config["command"] = Assembly.GetExecutingAssembly().Location;
                var config = MarketplaceInstaller.PrepareConfig(mapped.McpOptions[0], new Dictionary<string,string>{{"DEMO_KEY","synthetic-secret-value"}});
                Check(Convert.ToString(((Dictionary<string,object>)config["env"])["DEMO_KEY"])=="synthetic-secret-value", "Configuration substitutes fields without shell interpolation");                var multiple=new McpInstallOption { Name="multiple",Config=new Dictionary<string,object> { {"command",Assembly.GetExecutingAssembly().Location},{"env",new Dictionary<string,object>{{"A","${A}"},{"B","${B}"}}} } };
                var prepared=MarketplaceInstaller.PrepareConfig(multiple,new Dictionary<string,string>{{"A","first"},{"B","second"}});
                Check(Convert.ToString(((Dictionary<string,object>)prepared["env"])["B"])=="second","Multiple configuration placeholders are filled before validation");
                Check(!MarketplaceInstaller.Redact("Key synthetic-secret-value",config).Contains("synthetic-secret-value"),"Installation output redacts secrets");
                var context = new MarketplaceInstallContext { Cli = new CliInstallation(Assembly.GetExecutingAssembly().Location,"__mcp_fixture__"), Workspace=root, ConfigDirectory=root };
                var logs = new System.Text.StringBuilder();
                await MarketplaceInstaller.InstallMcp(context,"demo","user",config,line=>{lock(logs) logs.AppendLine(line);},CancellationToken.None);
                Check(logs.ToString().Contains("已保存") && !logs.ToString().Contains("synthetic-secret-value"), "MCP subprocess registration with redacted logs");
                try { await MarketplaceInstaller.InstallMcp(context,"existing","user",config,line=>{},CancellationToken.None); throw new Exception("overwrote MCP"); }
                catch(InvalidOperationException) { Check(true,"Existing MCP blocks installation"); }
                try { await MarketplaceInstaller.InstallMcp(context,"broken","user",config,line=>{},CancellationToken.None); throw new Exception("ignored config failure"); }
                catch(InvalidOperationException) { Check(true,"Failed conflict check blocks installation"); }
            }
            finally { MarketplaceInstaller.CheckTree(root); Directory.Delete(root,true); }
        }
        private sealed class FakeProvider : IMarketplaceProvider
        {
            public string Id { get { return "modelscope"; } }
            public Task<MarketplacePage> ListAsync(MarketplaceKind kind, int page, string search, bool refresh, CancellationToken token)
            {
                return Task.FromResult(new MarketplacePage { Number = 1, Total = 2, Items = new List<MarketplaceItem> {
                    new MarketplaceItem { Kind = kind, Name = "Fetch 网页内容抓取", Description = "检索网页内容并转换为 Markdown，便于处理和引用。", Author = "modelcontextprotocol", Popularity = "浏览 655506", Tags = new[] { "browser-automation" } },
                    new MarketplaceItem { Kind = kind, Name = "开发工具示例", Description = "支持项目代码与文档处理。此处使用模拟数据验证界面布局，产品页面始终从远程获取。", Author = "demo", Popularity = "热度暂无数据", Tags = new[] { "developer-tools" } }
                }});
            }
            public Task<MarketplaceItem> DetailAsync(MarketplaceKind kind, string id, bool refresh, CancellationToken token)
            {
                return Task.FromResult(new MarketplaceItem { Provider = Id, Id = id, Kind = kind, Name = "测试详情", Author = "demo", Description = "详情示例",
                    Configuration = "DEMO_KEY：必填", Permissions = "平台未提供权限声明", Installation = "示例安装配置", Readme = "## 网页内容抓取\n\n将网页转换为 **Markdown**，供 Claude 阅读和引用。\n\n- 支持网页文本提取\n- 支持分段读取\n\n```bash\nuvx mcp-server-fetch\n```", Parameters = new List<MarketplaceParameter> { new MarketplaceParameter { Name = "DEMO_KEY", Description = "测试参数说明", Required = true } } });
            }
        }
    }
}
