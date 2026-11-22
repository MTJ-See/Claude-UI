using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUI
{
    internal sealed class MarketplaceView : IDisposable
    {
        private readonly Window owner;
        private readonly Grid host;
        private readonly MarketplaceHttp http;
        private readonly MarketplaceCredentials credentials;
        private readonly Func<MarketplaceInstallContext> installContext;
        private readonly Dictionary<string, IMarketplaceProvider> providers;
        private readonly StackPanel content = new StackPanel();
        private readonly TextBlock title = new TextBlock { FontSize = 26, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) };
        private readonly TextBox search = new TextBox { Width = 240, Margin = new Thickness(0, 0, 8, 0), ToolTip = "按名称或简介搜索，按 Enter 查询" };
        private readonly Button previous, next, retry;
        private readonly WrapPanel toolbar;
        private CancellationTokenSource loading;
        private MarketplaceKind kind;
        private int page = 1;
        private string query = "";
        private MarketplacePage currentPage;
        private MarketplaceItem selected;
        private bool showingInstalled;
        private Button browseMarket;
        private readonly Dictionary<string, BitmapSource> icons = new Dictionary<string, BitmapSource>();
        private static readonly SemaphoreSlim iconGate = new SemaphoreSlim(4);

        public MarketplaceView(Window owner, Grid host, AppData data, Func<bool> save, IMarketplaceProvider provider = null, Func<MarketplaceInstallContext> installContext = null)
        {
            this.owner = owner; this.host = host; this.installContext = installContext ?? (() => new MarketplaceInstallContext { Workspace = data.Workspace, Cli = CliInstallation.Find(data.CliPath, AppDomain.CurrentDomain.BaseDirectory) });
            credentials = new MarketplaceCredentials(data);
            http = new MarketplaceHttp(() => credentials.ModelScope);
            providers = new[] { provider ?? new ModelScopeMarketplaceProvider(http) }.ToDictionary(p => p.Id);
            host.Margin = new Thickness(28, 24, 28, 20);
            host.SetResourceReference(Panel.BackgroundProperty, "Canvas");
            host.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), owner.FindResource("QuietScrollBar"));
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            host.RowDefinitions.Add(new RowDefinition());
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = new StackPanel(); heading.Children.Add(title);
            var subtitle = Text("发现与连接扩展能力，为 Claude 提供工具、数据源和可复用技能。", 13);
            subtitle.Margin = new Thickness(0, 8, 0, 0); subtitle.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            heading.Children.Add(subtitle); host.Children.Add(heading);
            status.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            toolbar = new WrapPanel { Margin = new Thickness(0, 15, 0, 0) };
            var searchGroup = new Grid { Width = 280, Margin = new Thickness(0, 0, 8, 0) };
            search.Width = Double.NaN; search.Height = 38; search.Margin = new Thickness(0); search.Padding = new Thickness(36, 8, 8, 8);
            search.SetResourceReference(Control.BackgroundProperty, "InputSurface");
            searchGroup.SetBinding(UIElement.VisibilityProperty, new Binding("Visibility") { Source = search });
            searchGroup.Children.Add(search);
            var searchIcon = DeveloperVisuals.Icon("search"); searchIcon.HorizontalAlignment = HorizontalAlignment.Left; searchIcon.VerticalAlignment = VerticalAlignment.Center; searchIcon.Margin = new Thickness(12, 0, 0, 0); searchGroup.Children.Add(searchIcon);
            var hint = Text("搜索名称或描述…", 13); hint.IsHitTestVisible = false; hint.VerticalAlignment = VerticalAlignment.Center; hint.Margin = new Thickness(36, 0, 0, 0);
            hint.SetResourceReference(TextBlock.ForegroundProperty, "SubtleInk");
            var hintStyle = new Style(typeof(TextBlock)); hintStyle.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Collapsed));
            var emptySearch = new DataTrigger { Binding = new Binding("Text") { Source = search }, Value = "" }; emptySearch.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible)); hintStyle.Triggers.Add(emptySearch); hint.Style = hintStyle;
            searchGroup.Children.Add(hint); toolbar.Children.Add(searchGroup);
            var searchButton = Action("搜索", async () => { query = search.Text.Trim(); page = 1; await LoadList(true); });
            searchButton.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
            toolbar.Children.Add(searchButton);
            toolbar.Children.Add(Action("刷新", async () => { if (showingInstalled) await LoadInstalled(); else if (selected == null) await LoadList(true); else await LoadDetail(selected, true); }));

            toolbar.Children.Add(Action("已安装", LoadInstalled));
            browseMarket = Action("浏览市场", async () => { await LoadList(true); });
            browseMarket.Visibility = Visibility.Collapsed; toolbar.Children.Add(browseMarket);
            Grid.SetRow(toolbar, 1); host.Children.Add(toolbar);
            Grid.SetRow(status, 2); host.Children.Add(status);
            var scroll = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 3); host.Children.Add(scroll);
            var footer = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
            previous = Action("上一页", async () => { page--; await LoadList(false); });
            next = Action("下一页", async () => { page++; await LoadList(false); });
            retry = Action("重试", async () => { if (showingInstalled) await LoadInstalled(); else if (selected == null) await LoadList(true); else await LoadDetail(selected, true); });
            footer.Children.Add(previous); footer.Children.Add(next); footer.Children.Add(retry);
            Grid.SetRow(footer, 4); host.Children.Add(footer);
            search.KeyDown += async (s, e) => { if (e.Key == System.Windows.Input.Key.Enter) { query = search.Text.Trim(); page = 1; await LoadList(true); } };
            host.IsVisibleChanged += delegate { if (!host.IsVisible) Cancel(); };
        }
        private Button Action(string text, Func<Task> action)
        {
            var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(10, 6, 10, 6) };
            button.SetResourceReference(FrameworkElement.StyleProperty, "Ghost");
            button.Click += async delegate { try { await action(); } catch (Exception) { status.Text = "操作未完成，请重试。"; } };
            return button;
        }
        public async Task Open(MarketplaceKind value)
        {
            Cancel(); kind = value; page = 1; query = ""; search.Text = ""; currentPage = null; selected = null;
            title.Text = kind == MarketplaceKind.Mcp ? "MCP 市场" : "Skill 市场";
            await LoadList(true);
        }
        private void Cancel() { if (loading != null) { loading.Cancel(); loading.Dispose(); loading = null; } }
        private CancellationToken Begin()
        {
            Cancel(); loading = new CancellationTokenSource();
            status.Text = "正在从魔搭实时加载…"; retry.Visibility = Visibility.Collapsed;
            previous.IsEnabled = next.IsEnabled = false; content.Children.Clear();
            return loading.Token;
        }
        private async Task LoadList(bool refresh)
        {
            selected = null;
            showingInstalled = false; browseMarket.Visibility = Visibility.Collapsed; title.Text = kind == MarketplaceKind.Mcp ? "MCP 市场" : "Skill 市场";
            previous.Visibility = next.Visibility = search.Visibility = toolbar.Children[1].Visibility = Visibility.Visible;
            var token = Begin();
            try
            {
                var result = await providers["modelscope"].ListAsync(kind, page, query, refresh, token);
                if (token.IsCancellationRequested) return;
                currentPage = result; RenderList();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) Failed(ex); }
        }
        private async Task LoadInstalled()
        {
            showingInstalled = true; selected = null;
            previous.Visibility = next.Visibility = search.Visibility = toolbar.Children[1].Visibility = Visibility.Collapsed;
            browseMarket.Visibility = Visibility.Visible;
            var token = Begin(); status.Text = "正在扫描本地 Claude 配置与 skills 目录…";
            var manager = new InstalledExtensions(installContext());
            try
            {
                var snapshot = await Task.Run(() => manager.Scan(), token);
                if (token.IsCancellationRequested) return;
                title.Text = kind == MarketplaceKind.Mcp ? "已安装 · MCP" : "已安装 · Skill";
                var rows=snapshot.Items.Where(item=>item.Kind==kind).OrderBy(item=>item.Name,StringComparer.OrdinalIgnoreCase).ToList();
                status.Text="本地已安装 "+rows.Count+" 项 · 开关作用于当前项目，同名条目共同生效；请新建或重启 Claude 会话。";
                if(snapshot.Warnings.Count>0) status.Text+="\n"+String.Join("\n",snapshot.Warnings.Distinct());
                content.Children.Add(Text("当前项目："+(installContext().Workspace??"未选择"),12));
                if(rows.Count==0) content.Children.Add(MarketplaceDetailView.Card(Text("未发现已安装的 "+(kind==MarketplaceKind.Mcp?"MCP 服务":"Skill")+"。可点击「浏览市场」安装后刷新。",14)));
                foreach(var item in rows)
                    content.Children.Add(InstalledExtensionView.Card(item,async enabled=>
                    {
                        await Task.Run(()=>manager.SetEnabled(item,enabled));
                        // All installations sharing a name follow Claude's same per-name override.
                        if(showingInstalled && host.IsVisible) await LoadInstalled();
                    }));
            }
            catch(OperationCanceledException) { }
            catch(Exception ex) { if(!token.IsCancellationRequested) Failed(ex); }
        }
        private void Failed(Exception ex)
        {
            status.Text = ex is InvalidOperationException ? ex.Message : "市场数据加载失败，请检查网络后重试。";
            retry.Visibility = Visibility.Visible;
            // A failed page can always navigate back to the previous page.
            previous.IsEnabled = selected == null && page > 1;
        }
        private void RenderList()
        {
            content.Children.Clear();
            previous.Visibility = next.Visibility = search.Visibility = toolbar.Children[1].Visibility = Visibility.Visible;
            previous.IsEnabled = currentPage.Number > 1;
            next.IsEnabled = currentPage.HasNext;
            retry.Visibility = Visibility.Collapsed;
            page = currentPage.Number;
            status.Text = "魔搭社区 · 第 " + page + " 页 · 共 " + currentPage.Total + " 项" +
                (String.IsNullOrEmpty(currentPage.Notice) ? "" : "\n" + currentPage.Notice);
            if (currentPage.Items.Count == 0) content.Children.Add(Text("没有匹配条目，请调整搜索词。", 14));
            var cards = new ResponsiveCardPanel();
            foreach (var item in currentPage.Items) cards.Children.Add(MarketCard(item));
            content.Children.Add(cards);
        }

        // 列表卡片保留真实接口数据和详情/安装入口，仅重构展示层。
        private Border MarketCard(MarketplaceItem item)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var logo = new Grid { Width = 48, Height = 48 };
            var fallback = DeveloperVisuals.Icon(item.Kind == MarketplaceKind.Mcp ? "blocks" : "sparkles", 24);
            fallback.HorizontalAlignment = HorizontalAlignment.Center; fallback.VerticalAlignment = VerticalAlignment.Center;
            logo.Children.Add(fallback);
            var icon = new Image { Width = 32, Height = 32, Stretch = Stretch.Uniform }; logo.Children.Add(icon);
            var tile = new Border { Child = logo, Width = 48, Height = 48, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top };
            tile.SetResourceReference(Border.BackgroundProperty, "IconTile"); row.Children.Add(tile);
            if (loading != null) LoadIcon(item.IconUrl, icon, loading.Token);
            var body = new StackPanel { Margin = new Thickness(16, 0, 0, 0) }; Grid.SetColumn(body, 1); row.Children.Add(body);
            var name = Text(item.Name, 16); name.FontWeight = FontWeights.SemiBold; name.TextWrapping = TextWrapping.NoWrap; name.TextTrimming = TextTrimming.CharacterEllipsis; name.ToolTip = item.Name; name.Margin = new Thickness(0, 0, 0, 6); body.Children.Add(name);
            var description = Text(item.Description, 13); description.LineHeight = 20; description.Height = 40; description.TextTrimming = TextTrimming.CharacterEllipsis; description.ToolTip = item.Description; description.Margin = new Thickness(0);
            description.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); body.Children.Add(description);
            var tags = new WrapPanel { Margin = new Thickness(0, 12, 0, 0), MinHeight = 24 };
            foreach (string tag in (item.Tags ?? new string[0]).Take(3))
            {
                var text = Text(tag, 11); text.Margin = new Thickness(0); text.TextWrapping = TextWrapping.NoWrap; text.MaxWidth = 150; text.TextTrimming = TextTrimming.CharacterEllipsis; text.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
                var pill = new Border { Child = text, Padding = new Thickness(8, 3, 8, 3), CornerRadius = new CornerRadius(11), Margin = new Thickness(0, 0, 6, 4), ToolTip = tag };
                pill.SetResourceReference(Border.BackgroundProperty, "IconTile"); tags.Children.Add(pill);
            }
            body.Children.Add(tags);
            var footer = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
            var open = Action("", () => LoadDetail(item, false)); open.Content = DeveloperVisuals.Icon("arrow-right", 16, null); open.Width = open.Height = 32; open.MinHeight = 32; open.Padding = new Thickness(0); open.Margin = new Thickness(8, 0, -4, -3); open.ToolTip = "查看 " + item.Name + " 详情"; open.SetResourceReference(FrameworkElement.StyleProperty, "MarketArrow");
            System.Windows.Automation.AutomationProperties.SetName(open, (string)open.ToolTip);
            var shift = new TranslateTransform(); open.RenderTransform = shift;
            Action slide = delegate { double to = open.IsMouseOver || open.IsKeyboardFocused ? 2 : 0; if (SystemParameters.ClientAreaAnimation) shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(shift.X, to, TimeSpan.FromMilliseconds(200))); else shift.X = to; };
            open.MouseEnter += delegate { slide(); }; open.MouseLeave += delegate { slide(); }; open.IsKeyboardFocusedChanged += delegate { slide(); };
            DockPanel.SetDock(open, Dock.Right); footer.Children.Add(open);
            var meta = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            var author = Text(item.Author, 11); author.TextWrapping = TextWrapping.NoWrap; author.TextTrimming = TextTrimming.CharacterEllipsis; author.MaxWidth = 105; author.Margin = new Thickness(0, 0, 12, 0); author.ToolTip = item.Author; author.SetResourceReference(TextBlock.ForegroundProperty, "SubtleInk"); meta.Children.Add(author);
            var popularity = new StackPanel { Orientation = Orientation.Horizontal };
            var eye = DeveloperVisuals.Icon("eye", 13, "SubtleInk"); eye.Margin = new Thickness(0, 0, 5, 0); eye.VerticalAlignment = VerticalAlignment.Center; popularity.Children.Add(eye);
            var views = Text(item.Popularity, 11); views.Margin = new Thickness(0); views.SetResourceReference(TextBlock.ForegroundProperty, "SubtleInk"); popularity.Children.Add(views); meta.Children.Add(popularity);
            footer.Children.Add(meta); body.Children.Add(footer);
            var card = new Border { Child = row, Padding = new Thickness(20), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12) };
            card.SetResourceReference(Border.BorderBrushProperty, "Line"); card.SetResourceReference(Border.BackgroundProperty, "Panel"); DeveloperVisuals.CardInteraction(card);
            return card;
        }

        private async Task LoadDetail(MarketplaceItem item, bool refresh)
        {
            selected = item;
            showingInstalled = false; browseMarket.Visibility = Visibility.Collapsed; title.Text = kind == MarketplaceKind.Mcp ? "MCP 市场" : "Skill 市场";
            previous.Visibility = next.Visibility = search.Visibility = toolbar.Children[1].Visibility = Visibility.Collapsed;
            var token = Begin();
            try
            {
                var detail = await providers[item.Provider].DetailAsync(item.Kind, item.Id, refresh, token);
                if (token.IsCancellationRequested) return;
                selected = detail;
                status.Text = "魔搭社区 · 详情按需获取";
                var install = Action("安装到 Claude", () =>
                {
                    var dialog = new MarketplaceInstallDialog(owner, detail, installContext(), credentials);
                    if (dialog.Show()) status.Text = "安装成功 · " + detail.Name;
                    return Task.FromResult(0);
                });
                install.SetResourceReference(FrameworkElement.StyleProperty, "Primary");
                Button source = null; Uri url;
                if (SafeLink(detail.SourceUrl, out url))
                    source = Action("源码仓库 ↗", () => { Process.Start(new ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }); return Task.FromResult(0); });
                content.Children.Add(MarketplaceDetailView.Create(detail, install, source));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!token.IsCancellationRequested) Failed(ex); }
        }
        private static TextBlock Text(string value, double size)
        {
            var text = new TextBlock { Text = value ?? "", FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); return text;
        }
        internal static bool SafeLink(string value, out Uri uri)
        {
            return Uri.TryCreate(value, UriKind.Absolute, out uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && !uri.IsLoopback;
        }
        private static void HideFallback(Image icon)
        {
            var host = icon.Parent as Grid;
            if (host != null && host.Children.Count > 1) host.Children[0].Visibility = Visibility.Collapsed;
        }

        private async void LoadIcon(string url, Image image, CancellationToken token)
        {
            // Icons use an independent, unauthenticated request and never use WPF's URI disk cache.
            Uri uri;
            if (!SafeLink(url, out uri)) return;
            BitmapSource cached;
            if (icons.TryGetValue(url, out cached)) { image.Source = cached; HideFallback(icon: image); return; }
            try
            {
                await iconGate.WaitAsync(token);
                try
                {
                    var bitmap = await Task.Run(() =>
                    {
                        var request = (HttpWebRequest)WebRequest.Create(uri);
                        request.Timeout = request.ReadWriteTimeout = 7000; request.AllowAutoRedirect = false;
                        using (token.Register(request.Abort))
                        using (var response = (HttpWebResponse)request.GetResponse())
                        using (var stream = response.GetResponseStream())
                        using (var memory = new MemoryStream())
                        {
                            if ((int)response.StatusCode != 200) return null;
                            byte[] buffer = new byte[8192]; int count;
                            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested(); memory.Write(buffer, 0, count);
                                if (memory.Length > 1024 * 1024) return null;
                            }
                            memory.Position = 0;
                            var result = new BitmapImage(); result.BeginInit(); result.CacheOption = BitmapCacheOption.OnLoad;
                            result.DecodePixelWidth = 64; result.StreamSource = memory; result.EndInit(); result.Freeze(); return result;
                        }
                    }, token);
                    if (bitmap != null && !token.IsCancellationRequested)
                    {
                        if (icons.Count >= 64) icons.Clear(); icons[url] = bitmap; image.Source = bitmap; HideFallback(icon: image);
                    }
                }
                finally { iconGate.Release(); }
            }
            catch (Exception) { /* Keep the local fallback icon on failed or cancelled loads. */ }
        }
        public void Dispose() { Cancel(); http.Clear(); icons.Clear(); }
    }
}
