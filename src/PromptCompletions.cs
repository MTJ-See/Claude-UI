using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClaudeUI
{
    internal sealed class PromptCompletionItem
    {
        public string Title { get; set; }
        public string Description { get; set; }
        public string Insert { get; set; }
        public bool File;
    }

    internal sealed class PromptCompletionToken
    {
        public int Start, End;
        public string Query;
        public bool File;
    }

    internal static class PromptCompletionCatalog
    {
        private static readonly HashSet<string> Excluded = new HashSet<string>(new[] {
            ".git", "node_modules", ".vs", ".idea", "bin", "obj", "dist", "build", "artifacts", ".checks", ".venv", "venv", "__pycache__", ".next", "coverage"
        }, StringComparer.OrdinalIgnoreCase);

        // 只对当前光标所在的命令或引用 token 补全，不触发邮箱和普通路径。
        internal static PromptCompletionToken Token(string text, int caret)
        {
            if (text == null || caret < 0 || caret > text.Length) return null;
            string before = text.Substring(0, caret);
            var command = Regex.Match(before, @"^\s*/([^\s/]*)$");
            if (command.Success)
            {
                int start = before.IndexOf('/'); int end = caret;
                while (end < text.Length && !Char.IsWhiteSpace(text[end])) end++;
                return new PromptCompletionToken { Start = start, End = end, Query = command.Groups[1].Value };
            }
            var file = Regex.Match(before, "(?:^|[\\s(])@(?:\"([^\"\\r\\n]*)|([^\\s@\"\\r\\n]*))$");
            if (!file.Success) return null;
            int marker = file.Index; if (before[marker] != '@') marker++; int finish = caret;
            bool quoted = marker + 1 < text.Length && text[marker + 1] == '"';
            if (quoted)
            {
                while (finish < text.Length && text[finish] != '"' && text[finish] != '\r' && text[finish] != '\n') finish++;
                if (finish < text.Length && text[finish] == '"') finish++;
            }
            else while (finish < text.Length && !Char.IsWhiteSpace(text[finish])) finish++;
            return new PromptCompletionToken { Start = marker, End = finish, Query = file.Groups[1].Success ? file.Groups[1].Value : file.Groups[2].Value, File = true };
        }

        internal static IList<PromptCompletionItem> Commands(string workspace, string config, CancellationToken token)
        {
            var items = new List<PromptCompletionItem>();
            string[,] local = {
                {"help", "查看本机 CLI 帮助"}, {"new", "新建对话，保留历史"}, {"clear", "开始新的上下文，保留历史"},
                {"compact", "通过 CLI 压缩当前会话上下文"}, {"model", "打开模型配置，或 /model 模型名称"},
                {"effort", "调整推理力度，支持 low / medium / high / xhigh / max"}, {"permissions", "打开工具授权与参数设置"},
                {"plan", "切换到只读规划模式"}, {"mcp", "打开 MCP 市场与已安装服务"}, {"skills", "打开 Skill 市场"},
                {"plugin", "查看本机已安装插件"}, {"doctor", "运行 CLI 健康诊断"}, {"status", "打开请求统计"}
            };
            for (int i = 0; i < local.GetLength(0); i++) items.Add(Command(local[i, 0], local[i, 1]));
            if (!Directory.Exists(workspace)) return items;
            token.ThrowIfCancellationRequested();
            var snapshot = new InstalledExtensions(new MarketplaceInstallContext { Workspace = workspace, ConfigDirectory = config }).Scan();
            foreach (var skill in snapshot.Items.Where(item => item.Kind == MarketplaceKind.Skill && item.Enabled))
            {
                token.ThrowIfCancellationRequested();
                if (!Regex.IsMatch(skill.Name ?? "", @"^[a-zA-Z0-9_:-]+$") || !UserInvocable(skill.Location)) continue;
                items.Add(Command(skill.Name, skill.Description + " · " + skill.Scope));
            }
            string userRoot = String.IsNullOrWhiteSpace(config) ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") : config;
            if (String.IsNullOrWhiteSpace(userRoot)) userRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            var roots = new List<string> { Path.Combine(userRoot, "commands") };
            for (var directory = new DirectoryInfo(workspace); directory != null; directory = directory.Parent)
            {
                roots.Add(Path.Combine(directory.FullName, ".claude", "commands"));
                if (Directory.Exists(Path.Combine(directory.FullName, ".git")) || System.IO.File.Exists(Path.Combine(directory.FullName, ".git"))) break;
            }
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                foreach (string path in EnumerateFiles(root, token, 2000))
                {
                    if (!path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) continue;
                    string relative = path.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length + 1);
                    string name = relative.Substring(0, relative.Length - 3).Replace(Path.DirectorySeparatorChar, ':');
                    if (Regex.IsMatch(name, @"^[a-zA-Z0-9_:-]+$")) items.Add(Command(name, "自定义 CLI 命令"));
                }
            }
            return items.GroupBy(item => item.Title, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        }

        private static PromptCompletionItem Command(string name, string description)
        {
            return new PromptCompletionItem { Title = "/" + name, Insert = "/" + name + " ", Description = description };
        }

        private static bool UserInvocable(string manifest)
        {
            try
            {
                using (var reader = new StreamReader(manifest))
                {
                    if ((reader.ReadLine() ?? "").Trim() != "---") return true;
                    for (int i = 0; i < 100; i++)
                    {
                        string line = reader.ReadLine(); if (line == null || line.Trim() == "---") break;
                        if (Regex.IsMatch(line, @"^\s*user-invocable:\s*['"" ]?false\b", RegexOptions.IgnoreCase)) return false;
                    }
                }
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            return true;
        }

        // 只索引文件名，限制遍历规模并跳过链接和依赖目录，不读取文件内容。
        private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token, int limit)
        {
            if (!Directory.Exists(root)) yield break;
            var queue = new Queue<string>(); queue.Enqueue(root); int visited = 0;
            while (queue.Count > 0 && visited < limit)
            {
                token.ThrowIfCancellationRequested(); string directory = queue.Dequeue();
                string[] entries;
                try { entries = Directory.GetFileSystemEntries(directory); }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }
                foreach (string entry in entries)
                {
                    if (++visited > limit) yield break;
                    token.ThrowIfCancellationRequested(); FileAttributes attributes;
                    try { attributes = System.IO.File.GetAttributes(entry); }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                    if ((attributes & FileAttributes.Directory) != 0)
                    { if (!Excluded.Contains(Path.GetFileName(entry))) queue.Enqueue(entry); }
                    else yield return entry;
                }
            }
        }

        internal static IList<PromptCompletionItem> Files(string workspace, string query, CancellationToken token)
        {
            if (!Directory.Exists(workspace)) return new List<PromptCompletionItem>();
            string root = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string match = (query ?? "").Replace('\\', '/');
            return EnumerateFiles(root, token, 20000).Select(file => file.Substring(root.Length).Replace('\\', '/'))
                .Where(relative => relative.IndexOf(match, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(relative => Path.GetFileName(relative).StartsWith(match, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(relative => relative.Length).ThenBy(relative => relative, StringComparer.OrdinalIgnoreCase).Take(60)
                .Select(relative => new PromptCompletionItem { File = true, Title = Path.GetFileName(relative), Description = relative,
                    Insert = "@" + (relative.Any(Char.IsWhiteSpace) ? "\"" + relative + "\"" : relative) + " " }).ToList();
        }
    }

    internal sealed class PromptCompletions : IDisposable
    {
        private readonly TextBox prompt;
        private readonly FrameworkElement anchor;
        private readonly Func<string> workspace, config;
        private readonly Func<bool> enabled;
        private readonly Popup popup;
        private readonly ListBox list;
        private readonly TextBlock heading;
        private readonly DispatcherTimer debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        private CancellationTokenSource search;
        private PromptCompletionToken active;
        private bool applying, loading, disposed;
        private string dismissedText, commandKey;
        private int dismissedCaret, revision;
        private DateTime commandsLoaded;
        private IList<PromptCompletionItem> commands;

        internal PromptCompletions(TextBox prompt, FrameworkElement anchor, Func<string> workspace, Func<string> config, Func<bool> enabled, ResourceDictionary resources)
        {
            this.prompt = prompt; this.anchor = anchor; this.workspace = workspace; this.config = config; this.enabled = enabled;
            list = new ListBox { BorderThickness = new Thickness(0), Background = Brushes.Transparent, MaxHeight = 228, Focusable = false, IsTabStop = false };
            list.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "NavItem");
            list.Resources.Add(typeof(ScrollBar), resources["QuietScrollBar"]);
            var template = new DataTemplate();
            var row = new FrameworkElementFactory(typeof(StackPanel));
            var title = new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty, new Binding("Title")); title.SetValue(TextBlock.FontSizeProperty, 13d); row.AppendChild(title);
            var description = new FrameworkElementFactory(typeof(TextBlock)); description.SetBinding(TextBlock.TextProperty, new Binding("Description")); description.SetValue(TextBlock.FontSizeProperty, 11d); description.SetValue(TextBlock.MarginProperty, new Thickness(0, 3, 0, 0)); description.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); description.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); row.AppendChild(description);
            template.VisualTree = row; list.ItemTemplate = template;
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            heading = new TextBlock { FontSize = 11, Margin = new Thickness(10, 5, 10, 8), TextWrapping = TextWrapping.Wrap };
            heading.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            var content = new StackPanel(); content.Children.Add(heading); content.Children.Add(list);
            var surface = new Border { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(5), Child = content, Margin = new Thickness(0, 0, 0, 8) };
            surface.Resources.MergedDictionaries.Add(resources);
            surface.SetResourceReference(Border.BackgroundProperty, "Panel"); surface.SetResourceReference(Border.BorderBrushProperty, "CardHoverLine");
            popup = new Popup { PlacementTarget = anchor, Placement = PlacementMode.Top, AllowsTransparency = true, StaysOpen = true, Child = surface };
            debounce.Tick += async delegate { debounce.Stop(); await Refresh(); };
            prompt.TextChanged += delegate { Schedule(); }; prompt.SelectionChanged += delegate { Schedule(); };
            prompt.IsKeyboardFocusWithinChanged += delegate { if (!prompt.IsKeyboardFocusWithin) Close(); };
            anchor.IsVisibleChanged += delegate { if (!anchor.IsVisible) Close(); };
            list.PreviewMouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e) {
                DependencyObject source = e.OriginalSource as DependencyObject;
                while (source != null && !(source is ListBoxItem)) source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
                var item = source as ListBoxItem;
                if (item == null) return;
                list.SelectedItem = item.DataContext; e.Handled = true; Accept();
            };
        }

        private void Schedule()
        {
            if (applying || disposed) return;
            debounce.Stop(); revision++;
            if (search != null) search.Cancel();
            if (!enabled() || prompt.IsReadOnly || (dismissedText == prompt.Text && dismissedCaret == prompt.CaretIndex)) { Close(); return; }
            active = PromptCompletionCatalog.Token(prompt.Text, prompt.CaretIndex);
            if (active == null) { Close(); return; }
            list.ItemsSource = null; loading = true;
            if (popup.IsOpen) heading.Text = active.File ? "正在查找当前项目文件…" : "正在筛选命令…";
            debounce.Start();
        }

        private async Task Refresh()
        {
            if (disposed || !enabled() || prompt.IsReadOnly || !prompt.IsKeyboardFocusWithin || !anchor.IsVisible) return;
            var token = PromptCompletionCatalog.Token(prompt.Text, prompt.CaretIndex); if (token == null) { Close(); return; }
            int current = ++revision; var cancellation = new CancellationTokenSource();
            if (search != null) { search.Cancel(); search.Dispose(); } search = cancellation;
            CancellationToken cancellationToken = cancellation.Token;
            active = token; loading = true; list.ItemsSource = null;
            heading.Text = token.File ? "正在查找当前项目文件…" : "正在加载命令与已安装 Skill…";
            ((FrameworkElement)popup.Child).Width = Math.Max(250, Math.Min(640, anchor.ActualWidth)); popup.IsOpen = true;
            try
            {
                string folder = workspace(), configuration = config();
                IList<PromptCompletionItem> items;
                if (token.File) items = await Task.Run(() => PromptCompletionCatalog.Files(folder, token.Query, cancellationToken));
                else
                {
                    string key = folder + "|" + configuration;
                    if (commands == null || commandKey != key || DateTime.UtcNow - commandsLoaded > TimeSpan.FromSeconds(15))
                    {
                        var found = await Task.Run(() => PromptCompletionCatalog.Commands(folder, configuration, cancellationToken));
                        if (current != revision || disposed) return;
                        commands = found; commandKey = key; commandsLoaded = DateTime.UtcNow;
                    }
                    items = commands.Where(item => item.Title.Substring(1).IndexOf(token.Query, StringComparison.OrdinalIgnoreCase) >= 0).Take(60).ToList();
                }
                if (current != revision || disposed || cancellation.IsCancellationRequested) return;
                list.ItemsSource = items; list.SelectedIndex = items.Count > 0 ? 0 : -1;
                heading.Text = items.Count == 0 ? (token.File ? "未找到匹配文件；可继续输入路径或使用 + 添加文件" : "未找到匹配命令；可继续输入 CLI 命令") :
                    (token.File ? "项目文件" : "客户端命令与已安装 Skill") + " · ↑↓ 选择 · Tab / Enter 补全 · Esc 关闭";
            }
            catch (OperationCanceledException) { }
            catch (IOException) { if (current == revision) heading.Text = "暂时无法读取当前项目。"; }
            catch (UnauthorizedAccessException) { if (current == revision) heading.Text = "没有权限读取当前项目。"; }
            catch (ArgumentException) { if (current == revision) heading.Text = "项目路径无效。"; }
            catch (NotSupportedException) { if (current == revision) heading.Text = "项目路径暂不支持。"; }
            finally { if (current == revision) loading = false; }
        }

        internal bool HandleKey(KeyEventArgs e)
        {
            if (e.Key == Key.Escape && (popup.IsOpen || debounce.IsEnabled)) { Dismiss(); return true; }
            // 及时打开待显示的补全，避免输入 / 后立刻按 Enter 时误发送。
            if (debounce.IsEnabled && e.KeyboardDevice.Modifiers == ModifierKeys.None && (e.Key == Key.Enter || e.Key == Key.Tab)) { debounce.Stop(); var pending = Refresh(); return true; }
            if (!popup.IsOpen || e.KeyboardDevice.Modifiers != ModifierKeys.None) return false;
            if (e.Key == Key.Down || e.Key == Key.Up)
            {
                if (list.Items.Count > 0) { list.SelectedIndex = (list.SelectedIndex + (e.Key == Key.Down ? 1 : list.Items.Count - 1)) % list.Items.Count; list.ScrollIntoView(list.SelectedItem); }
                return true;
            }
            if (e.Key == Key.Tab || e.Key == Key.Enter) { if (!loading) Accept(); return true; }
            return false;
        }

        internal void Accept()
        {
            var item = list.SelectedItem as PromptCompletionItem;
            var token = PromptCompletionCatalog.Token(prompt.Text, prompt.CaretIndex);
            if (item == null || token == null || item.File != token.File) return;
            applying = true;
            try { prompt.Select(token.Start, token.End - token.Start); prompt.SelectedText = item.Insert; prompt.CaretIndex = token.Start + item.Insert.Length; }
            finally { applying = false; }
            Dismiss(); prompt.Focus();
        }
        private void Dismiss() { dismissedText = prompt.Text; dismissedCaret = prompt.CaretIndex; Close(); }
        internal void Close() { debounce.Stop(); revision++; if (search != null) search.Cancel(); popup.IsOpen = false; loading = false; }
        public void Dispose() { disposed = true; Close(); if (search != null) search.Dispose(); }
    }
}
