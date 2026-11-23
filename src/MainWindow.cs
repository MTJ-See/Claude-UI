using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Shape = System.Windows.Shapes.Shape;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ClaudeUI
{
    internal sealed class MainWindow
    {
        private readonly HashSet<string> deletedSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Window window;
        private readonly SidebarResizer sidebarResizer;
        private readonly RequestDetailResizer requestDetailResizer;
        private readonly CliConversationCatalog catalog = new CliConversationCatalog();
        private readonly SidebarNavigation navigation;
        private readonly DispatcherTimer catalogTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        private bool refreshingCatalog, loadingConversation;
        private int conversationRevision;
        private DateTime lastCatalogRefresh;
        private readonly ImageSource userAvatar;
        private readonly DataStore store;
        private readonly AppData data;
        private readonly CliRunner runner = new CliRunner();
        private readonly CliSessionLocation sessionLocations;
        private readonly RequestStatisticsView statistics;
        private readonly MarketplaceView marketplace;
        private readonly PromptCompletions completions;
        private readonly ComposerOptionMenus optionMenus;
        private readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(65) };
        private readonly List<Button> actions = new List<Button>();
        private readonly TextBox prompt, log;
        private readonly CliOutputView toolOutput;
        private readonly ListBox history;
        private readonly ComboBox effort, permission, sessionModel;
        private readonly StackPanel messages;
        private readonly ScrollViewer scroll;
        private readonly ConcurrentQueue<Tuple<string, bool, DateTime>> pending = new ConcurrentQueue<Tuple<string, bool, DateTime>>();
        private readonly StringBuilder output = new StringBuilder();
        private Conversation current;
        private CliInstallation installation;
        private CancellationTokenSource cancellation;
        private TaskCompletionSource<bool> permissionDecision;
        private bool permissionRejected;
        private ClaudeStream stream;
        private MarkdownReply reply;
        private ReplyProgressView replyProgress;
        private bool updatingHistory, updatingModels, closing;
        private int probeRevision;
        private Stopwatch clock;
        private string operation, lastError;
        private GatewayRoute requestGateway;
        private decimal? costBaseline;
        private DateTime requestStart;
        private IList<RequestRate> requestRates;
        private string requestedModel;
        private string requestedModelName;
        private bool statisticsRecorded;
        private const int LogLimit = 220000;
        private const int SessionTitleLimit = 100;
        private bool Busy { get { return cancellation != null; } }

        public MainWindow(DataStore store)
        {
            this.store = store;
            sessionLocations = new CliSessionLocation(runner, AppDomain.CurrentDomain.BaseDirectory);
            string warning = null;
            try { data = store.Read(); }
            catch (Exception ex) { data = new AppData(); warning = "无法读取本地记录 · " + ex.Message; }
            using (var markup = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeUI.MainWindow.xaml"))
                window = (Window)XamlReader.Load(markup);
            sidebarResizer = new SidebarResizer(window, data, () => Save());
            window.Icon = (ImageSource)window.Resources["BrandIcon"];
            using (var avatarStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeUI.UserAvatar.jpg"))
                userAvatar = System.Windows.Media.Imaging.BitmapFrame.Create(avatarStream,
                    System.Windows.Media.Imaging.BitmapCreateOptions.PreservePixelFormat,
                    System.Windows.Media.Imaging.BitmapCacheOption.OnLoad);
            userAvatar.Freeze();
            prompt = Find<TextBox>("PromptBox");
            log = Find<TextBox>("LogBox");
            toolOutput = new CliOutputView(Find<RichTextBox>("ToolResultBox"));
            effort = Find<ComboBox>("EffortBox");
            permission = Find<ComboBox>("PermissionBox");
            sessionModel = Find<ComboBox>("SessionModelBox");
            history = Find<ListBox>("HistoryList");
            messages = Find<StackPanel>("Messages");
            scroll = Find<ScrollViewer>("ConversationScroll");
            history.ItemsSource = data.Conversations;
            navigation = new SidebarNavigation(window, data,
                async conversation => await OpenConversationAsync(conversation),
                folder => { if (!Busy) { OpenWorkspace(folder); Activate(NewConversation()); Save(); } }, () => Save(), CreateProjectMenu);
            statistics = new RequestStatisticsView(window, store.Statistics, data.RequestRates, Save,
                configuredModels: () => data.ModelProfiles.Select(profile => profile.ModelId), modelProfiles: () => data.ModelProfiles);
            requestDetailResizer = new RequestDetailResizer(window, data, () => Save());
            marketplace = new MarketplaceView(window, Find<Grid>("MarketplacePage"), data, Save, installContext: () => new MarketplaceInstallContext {
                Cli = installation, Workspace = current == null ? data.Workspace : current.Workspace, ConfigDirectory = current == null ? null : current.CliConfigDirectory });
            window.Closed += delegate { marketplace.Dispose(); };
            InitializeOptions();
            optionMenus = new ComposerOptionMenus(window, () => ((Option)permission.SelectedItem).Value,
                value => permission.SelectedItem = Select(permission, value), () => ((Option)effort.SelectedItem).Value,
                value => effort.SelectedItem = Select(effort, value), () => !Busy && !loadingConversation);
            BindButtons();
            Button("ArchivedProjectsButton").Click += delegate {
                if (Busy) return;
                navigation.ShowArchived = !navigation.ShowArchived;
                data.ProjectsCollapsed = false;
                Find<FrameworkElement>("ProjectsBody").Visibility = Visibility.Visible;
                Label("ProjectsChevron").Text = "\uE70D";
                navigation.Render(current);
                Save();
            };
            Theme.Apply(window.Resources, data.Dark);
            completions = new PromptCompletions(prompt, Find<Border>("ComposerSurface"),
                () => current == null ? data.Workspace : current.Workspace,
                () => current == null ? null : current.CliConfigDirectory,
                () => !Busy && !loadingConversation && !closing, window.Resources);
            window.Closed += delegate { completions.Dispose(); };
            window.Deactivated += delegate { completions.Close(); };
            UpdateThemeButton();
            OpenWorkspace(data.Workspace);
            UpdateButtons();
            if (warning != null) ChatStatus(warning, "Failure");
            timer.Tick += delegate { Pump(); };
            window.Closing += Close;
            window.Loaded += async delegate {
                await Task.WhenAll(Probe(), RefreshCatalog());
                // Reload the active CLI history too, so previously cached command records
                // are corrected when upgrading from a version that displayed them.
                if (!closing && !Busy && current != null && !String.IsNullOrWhiteSpace(current.CliTranscriptPath))
                    await OpenConversationAsync(current);
                if (!closing) catalogTimer.Start();
            };
            catalogTimer.Tick += async delegate { if (!Busy && !loadingConversation) await RefreshCatalog(); };
            window.Activated += async delegate
            {
                if (!Busy && !loadingConversation && DateTime.UtcNow - lastCatalogRefresh > TimeSpan.FromSeconds(10)) await RefreshCatalog();
            };
        }

        public Window View { get { return window; } }
        private T Find<T>(string name) where T : class { return (T)window.FindName(name); }
        private Button Button(string name) { return Find<Button>(name); }
        private TextBlock Label(string name) { return Find<TextBlock>(name); }
        private static Visibility Visible(bool value) { return value ? Visibility.Visible : Visibility.Collapsed; }

        private void InitializeOptions()
        {
            effort.ItemsSource = new[] {
                new Option("默认", ""), new Option("低", "low"), new Option("中", "medium"),
                new Option("高", "high"), new Option("很高", "xhigh"), new Option("最高", "max")
            };
            permission.ItemsSource = ComposerOptionMenus.Permissions.Select(item => new Option(item.Title, item.Value)).ToArray();
            permission.ToolTip = "需要确认的工具操作会弹出授权提示。接受编辑会按 Claude CLI 规则自动接受文件编辑。";
            effort.SelectedItem = Select(effort, data.Effort);
            permission.SelectedItem = Select(permission, data.Permission == "manual" ? "default" : data.Permission);
            effort.SelectionChanged += delegate { UpdateSummary(); CaptureOptions(); Save(); };
            permission.SelectionChanged += delegate { UpdateSummary(); CaptureOptions(); Save(); };
            UpdateSummary();
        }

        private static Option Select(ComboBox box, string value)
        {
            return box.Items.Cast<Option>().FirstOrDefault(item => item.Value == value) ?? (Option)box.Items[0];
        }

        private void BindButtons()
        {
            Button("NewButton").Click += delegate { Activate(NewConversation()); prompt.Focus(); };
            Button("ToolsButton").Click += delegate { ShowTools(true); };
            Button("StatsButton").Click += delegate { ShowStatistics(); };
            Button("McpMarketButton").Click += async delegate { await ShowMarketplace(MarketplaceKind.Mcp); };
            Button("SkillMarketButton").Click += async delegate { await ShowMarketplace(MarketplaceKind.Skill); };
            Button("CliButton").Click += delegate { ShowTools(true); };
            Button("BackButton").Click += delegate { ShowTools(false); };
            Button("LogButton").Click += delegate { ShowTools(true); };
            Button("WorkspaceButton").Click += ChooseWorkspace;
            Button("AddProjectButton").Click += ChooseWorkspace;
            Button("PickCliButton").Click += ChooseCli;
            Button("AutoCliButton").Click += async delegate { data.CliPath = null; Save(); await Probe(); };
            Button("ToggleRawLogButton").Click += delegate
            {
                bool raw = log.Visibility != Visibility.Visible;
                log.Visibility = Visible(raw);
                Find<RichTextBox>("ToolResultBox").Visibility = Visible(!raw);
                Button("ToggleRawLogButton").Content = raw ? "格式化结果" : "原始日志";
            };
            Button("CopyLogButton").Click += delegate
            {
                try { if (log.Text.Length > 0) Clipboard.SetText(log.Text); }
                catch (System.Runtime.InteropServices.ExternalException) { Label("ToolStatus").Text = "剪贴板正被占用，请重试。"; }
            };
            Button("ConfigButton").Click += delegate { optionMenus.ShowPermissions(Button("ConfigButton")); };
            Button("EffortButton").Click += delegate { optionMenus.ShowEfforts(Button("EffortButton")); };
            BindPromptSuggestion("ExplorePromptButton", "请先梳理当前项目的目录结构，说明主要模块、入口和运行方式。");
            BindPromptSuggestion("ReviewPromptButton", "请检查当前项目的代码，优先找出可能导致错误的问题，并说明原因和修复建议。");
            BindPromptSuggestion("PlanPromptButton", "我想在当前项目中完成一个新任务，请先和我确认目标，再把实现过程拆成可执行的步骤。");
            Button("SendButton").Click += async delegate { if (Busy) Stop(); else await Send(); };
            Button("AttachButton").Click += delegate {
                if (Busy || loadingConversation) return;
                var picker = new OpenFileDialog { Multiselect = true, Title = "添加附件", Filter = "所有文件|*.*" };
                if (picker.ShowDialog(window) == true) AddAttachments(picker.FileNames);
            };
            DataObject.AddPastingHandler(prompt, PasteAttachment);
            AttachmentPaste.Attach(prompt, Clipboard.GetDataObject, () => Busy || loadingConversation || prompt.IsReadOnly,
                PasteAttachmentData, error => ChatStatus(error, "Failure"));
            prompt.AllowDrop = true;
            prompt.PreviewDragOver += delegate(object sender, DragEventArgs e) {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effects = Busy || loadingConversation ? DragDropEffects.None : DragDropEffects.Copy;
                e.Handled = true;
            };
            prompt.PreviewDrop += delegate(object sender, DragEventArgs e) {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Handled = true;
                if (!Busy && !loadingConversation) AddAttachments((string[])e.Data.GetData(DataFormats.FileDrop));
            };
            Button("ModelsButton").Click += delegate { ConfigureModels(); };
            sessionModel.SelectionChanged += FollowModelSelection;
            var modelMenu = new ContextMenu(); var configureModel = new MenuItem { Header = "模型配置…" };
            configureModel.Click += delegate { ConfigureModels(); }; modelMenu.Items.Add(configureModel); sessionModel.ContextMenu = modelMenu;
            Button("StopToolButton").Click += delegate { Stop(); };
            Button("AllowToolPermission").Click += delegate { if (permissionDecision != null) permissionDecision.TrySetResult(true); };
            Button("DenyToolPermission").Click += delegate { if (permissionDecision != null) permissionDecision.TrySetResult(false); };
            Button("ThemeButton").Click += delegate
            {
                data.Dark = !data.Dark;
                Theme.Animate(window.Resources, data.Dark);
                UpdateThemeButton(true);
                Save();
            };
            Button("MinButton").Click += delegate { SystemCommands.MinimizeWindow(window); };
            Button("MaxButton").Click += delegate
            {
                if (window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(window);
                else SystemCommands.MaximizeWindow(window);
            };
            Button("CloseButton").Click += delegate { window.Close(); };
            window.StateChanged += delegate { Button("MaxButton").Content = window.WindowState == WindowState.Maximized ? "\uE923" : "\uE922"; };
            var conversationStyle = new Style(typeof(ListBoxItem), history.ItemContainerStyle);
            conversationStyle.Setters.Add(new Setter(UIElement.IsEnabledProperty, new System.Windows.Data.Binding("CanNavigate")));
            history.ItemContainerStyle = conversationStyle;
            history.SelectionChanged += async delegate
            {
                var selected = history.SelectedItem as Conversation;
                if (!Busy && !updatingHistory && selected != null) await OpenConversationAsync(selected);
            };
            window.AddHandler(System.Windows.Controls.Button.ClickEvent, new RoutedEventHandler(OpenSessionMenu));
            window.PreviewMouseRightButtonUp += OpenSessionMenuAtPointer;
            window.PreviewMouseDown += ReturnToSelectedConversation;
            prompt.TextChanged += delegate
            {
                Label("PromptPlaceholder").Visibility = Visible(prompt.Text.Length == 0);
                UpdateButtons();
            };
            PromptShortcuts.Attach(prompt, async () => await Send(), e => completions != null && completions.HandleKey(e));
            Find<ContentControl>("CliPathIcon").Content = DeveloperVisuals.Icon("folder-open");
            Find<ContentControl>("ToolEmptyIcon").Content = DeveloperVisuals.Icon("terminal", 34, "SubtleInk");
            Button("PickCliButton").Content = DeveloperVisuals.ButtonContent("folder-open", "选择 CLI");
            Button("AutoCliButton").Content = DeveloperVisuals.ButtonContent("search", "自动查找");
            Button("CopyLogButton").Content = DeveloperVisuals.ButtonContent("copy", "复制输出");
            Button("ModelsButton").Content = DeveloperVisuals.Icon("blocks", 16);
            foreach (var command in CliAction.All)
            {
                string icon = command.Name == "版本" ? "badge-info" : command.Name == "健康诊断" ? "stethoscope" :
                    command.Name == "登录状态" ? "shield-check" : command.Name == "MCP 服务" ? "blocks" :
                    command.Name == "插件列表" ? "plug" : command.Name == "后台任务" ? "list-todo" : "circle-help";
                var button = new Button { Content = DeveloperVisuals.ButtonContent(icon, command.Name), ToolTip = command.Description,
                    Style = (Style)window.Resources["ToolPill"], Margin = new Thickness(0, 0, 8, 8) };
                System.Windows.Automation.AutomationProperties.SetName(button, command.Name);
                button.Click += async delegate { await RunAction(command); };
                Find<WrapPanel>("ActionButtons").Children.Add(button);
                actions.Add(button);
            }
        }

        private Conversation NewConversation()
        {
            string directory = data.Workspace;
            if (!Directory.Exists(directory)) directory = AppDomain.CurrentDomain.BaseDirectory;
            return new Conversation { Workspace = directory, Title = "新建对话" };
        }

        internal void OpenWorkspace(string directory)
        {
            data.Workspace = Directory.Exists(directory) ? directory :
                data.Conversations.Select(item => item.Workspace).FirstOrDefault(Directory.Exists) ?? AppDomain.CurrentDomain.BaseDirectory;
            var recent = data.Conversations.Where(item => SameWorkspace(item.Workspace, data.Workspace)).ToList();
            var selected = recent.FirstOrDefault(item => item.Id == data.ActiveConversationId) ?? recent.FirstOrDefault();
            Activate(selected ?? NewConversation());
        }

        private static bool SameWorkspace(string left, string right)
        {
            if (left == null || right == null) return false;
            return String.Equals(left.Replace('/', '\\').TrimEnd('\\'), right.Replace('/', '\\').TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }

        private void Activate(Conversation conversation)
        {
            conversationRevision++;
            loadingConversation = false;
            if (current != null) current.Draft = prompt.Text;
            current = conversation;
            if (current.DraftAttachments == null) current.DraftAttachments = new List<ChatAttachment>();
            RenderAttachments();
            if (Directory.Exists(current.Workspace)) data.Workspace = current.Workspace;
            string project = CliConversationCatalog.Normalize(current.Workspace);
            if (project != null && !data.ProjectFolders.Contains(project, StringComparer.OrdinalIgnoreCase)) data.ProjectFolders.Add(project);
            data.ActiveConversationId = data.Conversations.Contains(current) ? current.Id : null;
            UpdateHistory();
            UpdateWorkspace();
            RefreshModelChoices();
            RenderMessages();
            prompt.Text = current.Draft ?? "";
            UpdateWelcome();
            if (MissingModelProfile) ChatStatus("原模型配置已移除，请重新选择模型。", "Failure");
            else ChatStatus(Directory.Exists(current.Workspace) ? "就绪" : "会话目录不存在，请更换目录后新建对话。", "MutedInk");
            ShowTools(false);
            UpdateButtons();
            ScrollToEnd();
        }

        private void RenderMessages()
        {
            messages.Children.Clear();
            foreach (var message in current.Messages)
            {
                var body = AddAttachedMessage(message.Role, message.Text, message.Attachments);
                var progress = body.Tag as ReplyProgressView;
                if (progress != null) progress.Restore(message.RunSummary);
            }
        }

        private ModelProfile ActiveModel
        {
            get { return current == null ? null : data.ModelProfiles.FirstOrDefault(item => item.Id == current.ModelProfileId); }
        }

        private bool MissingModelProfile
        {
            get { return current != null && !String.IsNullOrEmpty(current.ModelProfileId) && ActiveModel == null; }
        }

        private void RefreshModelChoices()
        {
            var choices = new List<Option> { new Option("本机默认", "") };
            choices.AddRange(data.ModelProfiles.Select(item => new Option(item.Name, item.Id)));
            if (MissingModelProfile) choices.Insert(0, new Option("请选择模型", current.ModelProfileId));
            updatingModels = true;
            sessionModel.ItemsSource = choices;
            sessionModel.SelectedItem = Select(sessionModel, current.ModelProfileId ?? "");
            updatingModels = false;
            UpdateModelHint();
        }

        private void UpdateModelHint()
        {
            sessionModel.ToolTip = ActiveModel == null ? "为当前对话选择模型" : ActiveModel.ModelId +
                (ActiveModel.UsesEndpoint ? " · " + ActiveModel.BaseUrl : " · 使用本机 CLI 的服务");
        }

        private void FollowModelSelection(object sender, SelectionChangedEventArgs e)
        {
            var selected = sessionModel.SelectedItem as Option;
            if (updatingModels || current == null || selected == null) return;
            string previous = current.ModelProfileId;
            bool removedProfile = MissingModelProfile;
            current.ModelProfileId = selected.Value.Length == 0 ? null : selected.Value;
            if (current.ModelProfileId == previous) return;
            if (data.Conversations.Contains(current) && !Save())
            {
                current.ModelProfileId = previous;
                RefreshModelChoices();
                return;
            }
            if (removedProfile) RefreshModelChoices();
            else UpdateModelHint();
            UpdateButtons();
            ChatStatus("下次发送使用 " + selected + (String.IsNullOrEmpty(current.SessionId) ? "。" : "，并接续本会话上下文。"), "MutedInk");
        }

        private void ConfigureModels()
        {
            if (Busy) return;
            var dialog = new ModelProfilesDialog(window, data.ModelProfiles, current.ModelProfileId, TestModelProfile,
                (owner, profile) => statistics.EditRate(owner, profile));
            if (!dialog.ShowDialog()) return;
            var previous = data.ModelProfiles;
            var updated = new List<ModelProfile>(previous);
            string id = dialog.Saved == null ? dialog.RemovedId : dialog.Saved.Id;
            int index = updated.FindIndex(item => item.Id == id);
            if (dialog.Saved == null)
            {
                if (index >= 0) updated.RemoveAt(index);
            }
            else if (index < 0) updated.Add(dialog.Saved);
            else updated[index] = dialog.Saved;
            data.ModelProfiles = updated;
            if (!Save())
            {
                data.ModelProfiles = previous;
                return;
            }
            RefreshModelChoices();
            UpdateButtons();
            if (MissingModelProfile) ChatStatus("原模型配置已移除，请重新选择模型。", "Failure");
            else ChatStatus(dialog.Saved == null ? "已移除模型配置。" : "模型配置已保存，可在发送按钮旁选择。", "Success");
        }

        private async Task<string> TestModelProfile(ModelProfile profile, CancellationToken token)
        {
            var cli = installation;
            var session = current;
            if (cli == null) return "尚未检测到 Claude CLI，请在「CLI 工具」中检查安装。";
            if (!Directory.Exists(session.Workspace)) return "工作目录不存在，请先更换工作目录。";
            string configuration = await sessionLocations.ResolveAsync(cli, session, token);
            var probeRates = data.RequestRates.ToList();
            return await ModelProfileProbe.RunAsync(runner, cli, session.Workspace, configuration, profile, token,
                rows => window.Dispatcher.Invoke(() => statistics.Record(rows, probeRates)));
        }

        private async Task RefreshCatalog()
        {
            if (refreshingCatalog || closing || Busy || loadingConversation) return;
            refreshingCatalog = true;
            try
            {
                var roots = CatalogRoots();
                var snapshot = await Task.Run(() => catalog.Read(roots));
                if (closing || Busy || loadingConversation) return;
                MergeCatalog(snapshot);
                lastCatalogRefresh = DateTime.UtcNow;
                UpdateHistory();
                Find<TextBlock>("NoHistory").Text = snapshot.UnreadableFiles > 0 ? "部分 CLI 记录暂时无法读取" : "暂无 CLI 对话";
                Save();
                if (current != null && current.Messages.Count == 0 && !String.IsNullOrWhiteSpace(current.CliTranscriptPath))
                    await OpenConversationAsync(current);
            }
            catch (IOException ex) { ChatStatus("读取 CLI 对话失败 · " + ex.Message, "Failure"); }
            catch (UnauthorizedAccessException ex) { ChatStatus("无法读取 CLI 对话 · " + ex.Message, "Failure"); }
            finally { refreshingCatalog = false; }
        }

        private string[] CatalogRoots()
        {
            return data.Conversations.Select(item => item.CliConfigDirectory).Concat(new[] {
                Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
                Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "CLI_DefaultWorkSpace"))
            }).Where(path => !String.IsNullOrWhiteSpace(path)).ToArray();
        }

        private void MergeCatalog(CliCatalogSnapshot snapshot)
        {
            foreach (string folder in snapshot.Projects)
                if (!data.ProjectFolders.Contains(folder, StringComparer.OrdinalIgnoreCase)) data.ProjectFolders.Add(folder);
            foreach (var imported in snapshot.Conversations)
            {
                string key = CliConversationCatalog.Key(imported);
                if (deletedSessions.Contains(key) || data.HiddenCliSessions.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                var existing = data.Conversations.FirstOrDefault(item => item.SessionId == imported.SessionId && SameWorkspace(item.Workspace, imported.Workspace));
                if (existing == null || existing.Id.StartsWith("cli:", StringComparison.Ordinal))
                    existing = RecoverFailedSession(snapshot, imported, existing);
                if (existing == null) data.Conversations.Add(imported);
                else
                {
                    if (String.CompareOrdinal(imported.UpdatedUtc, existing.UpdatedUtc) >= 0 || String.IsNullOrWhiteSpace(existing.CliTranscriptPath))
                    {
                        existing.CliTranscriptPath = imported.CliTranscriptPath;
                        existing.CliConfigDirectory = imported.CliConfigDirectory;
                    }
                    if (String.CompareOrdinal(imported.UpdatedUtc, existing.UpdatedUtc) > 0) existing.UpdatedUtc = imported.UpdatedUtc;
                    if (existing != current) existing.Title = imported.Title;
                }
            }
        }

        private Conversation RecoverFailedSession(CliCatalogSnapshot snapshot, Conversation imported, Conversation existing)
        {
            if (String.IsNullOrEmpty(imported.FirstApiError)) return existing;
            var matches = data.Conversations.Where(item => CliHistoryMerge.MatchesFailedSession(item, imported)).Take(2).ToArray();
            if (matches.Length != 1 || snapshot.Conversations.Count(item => CliHistoryMerge.MatchesFailedSession(matches[0], item)) != 1)
                return existing;
            var local = matches[0];
            var keep = existing == null || current == local ? local : existing;
            var duplicate = keep == local ? existing : local;
            if (duplicate != null && (!String.IsNullOrEmpty(duplicate.Draft) ||
                (duplicate.DraftAttachments != null && duplicate.DraftAttachments.Count != 0))) return existing;
            var transcript = CliConversationCatalog.ReadTranscript(imported.CliTranscriptPath);
            if (existing != null)
            {
                var cached = new CliTranscript();
                cached.Messages.AddRange(CliHistoryMerge.Merge(existing.Messages, transcript));
                transcript.Messages.Clear();
                transcript.Messages.AddRange(cached.Messages);
            }
            keep.Messages = CliHistoryMerge.Merge(local.Messages, transcript);
            keep.SessionId = imported.SessionId;
            if (duplicate != null) data.Conversations.Remove(duplicate);
            if (current == keep)
            {
                RenderMessages();
                UpdateWelcome();
            }
            return keep;
        }


        private async Task OpenConversationAsync(Conversation conversation)
        {
            if (conversation == null) return;
            if (Busy)
            {
                if (conversation == current) ShowTools(false);
                return;
            }
            int revision = ++conversationRevision;
            if (!String.IsNullOrWhiteSpace(conversation.CliTranscriptPath) && File.Exists(conversation.CliTranscriptPath))
            {
                loadingConversation = true;
                UpdateButtons();
                ChatStatus("正在读取 CLI 对话…", "MutedInk");
                try
                {
                    var transcript = await Task.Run(() => CliConversationCatalog.ReadTranscript(conversation.CliTranscriptPath));
                    if (closing || revision != conversationRevision || Busy) return;
                    conversation.Messages = CliHistoryMerge.Merge(conversation.Messages, transcript);
                }
                catch (IOException) { ChatStatus("CLI 记录暂时无法读取，显示客户端已保存的内容。", "Failure"); }
                catch (UnauthorizedAccessException) { ChatStatus("无权读取 CLI 记录，显示客户端已保存的内容。", "Failure"); }
                finally { if (revision == conversationRevision) { loadingConversation = false; UpdateButtons(); } }
            }
            if (closing || revision != conversationRevision || Busy) return;
            Activate(conversation);
            Save();
        }

        private void UpdateHistory()
        {
            updatingHistory = true;
            history.ItemsSource = data.Conversations.OrderByDescending(item => item.UpdatedUtc ?? "").ToList();
            navigation.Render(current);
            history.SelectedItem = data.Conversations.Contains(current) ? current : null;
            Find<TextBlock>("NoHistory").Visibility = Visible(data.Conversations.Count == 0);
            updatingHistory = false;
        }

        private void OpenSessionMenu(object sender, RoutedEventArgs e)
        {
            var button = e.OriginalSource as Button;
            if (button == null || button.Name != "SessionMenuButton") return;
            var conversation = button.DataContext as Conversation;
            if (conversation == null || Busy) return;
            e.Handled = true;
            ShowSessionMenu(conversation, button, System.Windows.Controls.Primitives.PlacementMode.Bottom);
        }

        private void ReturnToSelectedConversation(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            if (Find<Grid>("ChatPage").Visibility == Visibility.Visible) return;
            var source = e.OriginalSource as DependencyObject;
            while (source != null)
            {
                // The row's menu remains a separate action, even for the active conversation.
                if (source is System.Windows.Controls.Primitives.ButtonBase) return;
                var row = source as ListBoxItem;
                if (row != null)
                {
                    var list = ItemsControl.ItemsControlFromItemContainer(row);
                    bool sidebar = list == history || (list != null && Find<StackPanel>("ProjectList").IsAncestorOf(list));
                    if (sidebar && row.DataContext == current)
                    {
                        ShowTools(false);
                        e.Handled = true;
                    }
                    return;
                }
                var content = source as FrameworkContentElement;
                source = content != null ? content.Parent : source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
        }

        private void OpenSessionMenuAtPointer(object sender, MouseButtonEventArgs e)
        {
            var source = e.OriginalSource as DependencyObject;
            ListBoxItem row = null;
            while (source != null)
            {
                row = source as ListBoxItem;
                if (row != null) break;
                var content = source as FrameworkContentElement;
                source = content != null ? content.Parent : source is Visual || source is System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);
            }
            var conversation = row != null ? row.DataContext as Conversation : null;
            if (conversation == null || Busy) return;
            e.Handled = true;
            ShowSessionMenu(conversation, row, System.Windows.Controls.Primitives.PlacementMode.MousePoint);
        }

        private void ShowSessionMenu(Conversation conversation, UIElement anchor, System.Windows.Controls.Primitives.PlacementMode placement)
        {
            var menu = CreateSessionMenu(conversation);
            menu.PlacementTarget = anchor;
            menu.Placement = placement;
            menu.IsOpen = true;
        }

        internal ContextMenu CreateSessionMenu(Conversation conversation)
        {
            var menu = new ContextMenu {
                Style = (Style)window.Resources["ProjectActionMenu"],
                FontFamily = window.FontFamily,
                FontSize = window.FontSize
            };
            foreach (string key in new[] { "Panel", "Line", "Ink", "MutedInk", "Selected", "Sidebar", "Failure", "FailureSurface" }) menu.Resources[key] = BrushFor(key);
            Guid sessionId;
            bool hasSession = Guid.TryParse(conversation.SessionId, out sessionId);
            bool canEdit = hasSession && installation != null && Directory.Exists(conversation.Workspace);
            string editHint = !hasSession ? "此对话还没有可用的 Claude CLI 会话。" :
                installation == null ? "尚未检测到 Claude CLI。" :
                !Directory.Exists(conversation.Workspace) ? "会话的工作目录已不存在。" : null;

            AddSessionItem(menu, "压缩上下文", async () =>
            {
                await OpenConversationAsync(conversation);
                await Send("/compact");
            }, canEdit, editHint ?? "将较早的对话和工具结果归纳成摘要，减少后续请求的输入量。", vectorIcon: "compact");
            AddSessionItem(menu, "重命名…", async () => await EditSession(conversation, false), canEdit, editHint, vectorIcon: "edit");
            AddSessionItem(menu, "创建分支…", async () => await EditSession(conversation, true), canEdit, editHint, vectorIcon: "branch");
            AddSessionItem(menu, "复制会话 ID", () => CopySessionId(sessionId.ToString()), hasSession, vectorIcon: "copy");
            menu.Items.Add(new Separator { Style = (Style)window.Resources["SessionMenuSeparator"] });
            AddSessionItem(menu, "从对话列表移除…", () => RemoveRecent(conversation), true, "移除本应用保存的记录，Claude CLI 会话仍可接续。", vectorIcon: "remove");
            AddSessionItem(menu, "彻底删除…", () => DeleteConversation(conversation), true,
                "永久删除这条对话及其在本机的会话记录。", vectorIcon: "trash");
            return menu;
        }

        internal ContextMenu CreateProjectMenu(string folder)
        {
            var state = ProjectState(folder);
            var menu = new ContextMenu { Style = (Style)window.Resources["ProjectActionMenu"], FontFamily = window.FontFamily, FontSize = window.FontSize };
            foreach (string key in new[] { "Panel", "Line", "Ink", "MutedInk", "Selected", "Sidebar", "Failure", "FailureSurface" }) menu.Resources[key] = BrushFor(key);
            menu.Tag = "ProjectActions";
            AddSessionItem(menu, state.IsPinned ? "取消置顶" : "置顶", () => ChangeProjectNavigation(folder, "pin"), vectorIcon: state.IsPinned ? "unpin" : "pin");
            AddSessionItem(menu, "编辑", () => {
                string name = AskSessionName(ProjectDisplayName(folder), false, true);
                if (name != null) RenameProject(folder, name);
            }, hint: "编辑项目显示名称，保留原文件夹路径。", vectorIcon: "edit");
            menu.Items.Add(new Separator { Style = (Style)window.Resources["SessionMenuSeparator"] });
            AddSessionItem(menu, state.IsArchived ? "取消归档" : "归档聊天", () => ChangeProjectNavigation(folder, "archive"),
                hint: "归档整个项目分组，原会话仍可在对话列表中接续。", vectorIcon: state.IsArchived ? "restore" : "archive");
            menu.Items.Add(new Separator { Style = (Style)window.Resources["SessionMenuSeparator"] });
            AddSessionItem(menu, "移除项目", () => ChangeProjectNavigation(folder, "remove"),
                hint: "从侧栏移除项目分组，保留文件夹及全部会话；通过添加项目文件夹可重新添加。", vectorIcon: "remove");
            return menu;
        }

        private ProjectNavigationState ProjectState(string folder)
        {
            var state = data.ProjectStates.FirstOrDefault(item => SameWorkspace(item.Folder, folder));
            if (state == null) { state = new ProjectNavigationState { Folder = folder }; data.ProjectStates.Add(state); }
            return state;
        }

        private string ProjectDisplayName(string folder)
        {
            var state = ProjectState(folder);
            return String.IsNullOrWhiteSpace(state.Name) ? Path.GetFileName(folder.TrimEnd('\\', '/')) : state.Name;
        }

        internal void RenameProject(string folder, string name)
        {
            if (Busy || !ValidSessionTitle(name)) return;
            var state = ProjectState(folder);
            string previous = state.Name;
            state.Name = name.Trim();
            if (!Save()) state.Name = previous;
            UpdateHistory();
        }

        internal void ChangeProjectNavigation(string folder, string action)
        {
            if (Busy) return;
            var state = ProjectState(folder);
            bool pinned = state.IsPinned, archived = state.IsArchived, removed = state.IsRemoved;
            if (action == "pin") state.IsPinned = !pinned;
            else if (action == "archive") state.IsArchived = !archived;
            else if (action == "remove") state.IsRemoved = true;
            else return;
            if (!Save())
            {
                state.IsPinned = pinned; state.IsArchived = archived; state.IsRemoved = removed;
                return;
            }
            UpdateHistory();
            ChatStatus(action == "pin" ? (state.IsPinned ? "项目已置顶。" : "项目已取消置顶。") :
                action == "archive" ? (state.IsArchived ? "项目已归档，可在「已归档」中恢复。" : "项目已取消归档。") : "项目已移除，全部会话和文件均已保留。", "Success");
        }

        private void AddSessionItem(ContextMenu menu, string text, Action action, bool enabled = true, string hint = null, string glyph = null, string vectorIcon = null)
        {
            bool projectAction = vectorIcon != null;
            bool destructive = vectorIcon == "remove" || vectorIcon == "trash";
            var item = new MenuItem { Header = text, Tag = destructive ? "RemoveProject" : glyph, IsEnabled = enabled, ToolTip = hint,
                Style = (Style)window.Resources[projectAction ? "ProjectActionItem" : "SessionMenuItem"] };
            if (projectAction) item.Icon = ProjectActionVisuals.Icon(vectorIcon, destructive);
            System.Windows.Automation.AutomationProperties.SetName(item, text);
            ToolTipService.SetShowOnDisabled(item, true);
            item.Click += delegate { if (!Busy) action(); };
            menu.Items.Add(item);
        }

        private Brush BrushFor(string resource) { return (Brush)window.Resources[resource]; }

        private void CopySessionId(string id)
        {
            try { Clipboard.SetText(id); ChatStatus("已复制 Claude CLI 会话 ID。", "Success"); }
            catch (System.Runtime.InteropServices.ExternalException) { ChatStatus("剪贴板正被占用，请重试。", "Failure"); }
        }

        private void RemoveRecent(Conversation conversation)
        {
            int index = data.Conversations.IndexOf(conversation);
            if (index < 0) return;
            string details = "从对话列表中移除「" + conversation.Title + "」？\n\n此操作会清除 Claude UI 保存的这条记录。Claude CLI 保存的原始会话会保留。";
            if (DialogVisuals.Show(window, details, "从对话列表移除", MessageBoxButton.YesNo,
                MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            string hidden = CliConversationCatalog.Key(conversation);
            if (!data.HiddenCliSessions.Contains(hidden, StringComparer.OrdinalIgnoreCase)) data.HiddenCliSessions.Add(hidden);
            data.Conversations.RemoveAt(index);
            if (!Save())
            {
                data.HiddenCliSessions.Remove(hidden);
                data.Conversations.Insert(index, conversation);
                return;
            }
            if (current == conversation)
                OpenWorkspace(conversation.Workspace);
            else UpdateHistory();
            ChatStatus("已从对话列表移除。", "Success");
        }

        private void DeleteConversation(Conversation conversation)
        {
            if (loadingConversation || !data.Conversations.Contains(conversation)) return;
            string details = "彻底删除「" + conversation.Title + "」？\n\n这会永久删除该对话、Claude CLI 会话文件、关联的本地请求统计和专属附件副本。此操作无法撤销。";
            if (DialogVisuals.Show(window, details, "彻底删除对话", MessageBoxButton.YesNo,
                MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            int index = data.Conversations.IndexOf(conversation);
            if (index < 0 || Busy || loadingConversation) return;
            try
            {
                ConversationDeletion.DeleteRecords(store, conversation,
                    data.Conversations.Where(item => item != conversation), CatalogRoots());
            }
            catch (Exception ex)
            {
                if (!(ex is IOException) && !(ex is UnauthorizedAccessException) && !(ex is ArgumentException)) throw;
                ChatStatus("删除未完成，可重试 · " + ex.Message, "Failure");
                return;
            }
            data.Conversations.RemoveAt(index);
            if (!Save())
            {
                data.Conversations.Insert(index, conversation);
                ChatStatus("删除未完成，无法更新对话列表，可重试。", "Failure");
                return;
            }
            deletedSessions.Add(CliConversationCatalog.Key(conversation));
            if (current == conversation) { OpenWorkspace(conversation.Workspace); Save(); }
            else UpdateHistory();
            if (Find<Grid>("StatsPage").IsVisible) statistics.Show(current.SessionId);
            ChatStatus("已彻底删除对话。", "Success");
        }

        private async Task EditSession(Conversation conversation, bool fork)
        {
            Guid id;
            if (installation == null || !Directory.Exists(conversation.Workspace) || !Guid.TryParse(conversation.SessionId, out id)) return;
            string label = fork ? "创建分支" : "重命名";
            string name = AskSessionName(conversation.Title, fork);
            if (name == null || (!fork && name == conversation.Title)) return;
            var edit = new SessionEdit(id.ToString(), name, fork);
            var result = new ClaudeStream();
            ChatStatus(label + "中…", "Accent");
            bool success = await Run(label, edit.Arguments(), edit.Input, conversation.Workspace,
                line => result.Read(line), () => edit.Validate(result), session: conversation);
            if (closing) return;
            if (!success)
            {
                ChatStatus(Label("ToolStatus").Text + " · 详情见执行日志", "Failure");
                return;
            }
            if (fork)
            {
                var branch = new Conversation {
                    Title = name, Workspace = conversation.Workspace, SessionId = result.SessionId,
                    CliConfigDirectory = conversation.CliConfigDirectory,
                    ModelProfileId = conversation.ModelProfileId,
                    Messages = new List<StoredMessage>(conversation.Messages)
                };
                data.Conversations.Insert(0, branch);
                Activate(branch);
                prompt.Focus();
            }
            else
            {
                foreach (var matching in data.Conversations.Where(item =>
                    String.Equals(item.SessionId, edit.SourceId, StringComparison.OrdinalIgnoreCase))) matching.Title = name;
                UpdateHistory();
                UpdateWorkspace();
            }
            ChatStatus(fork ? "已创建分支，可继续发送消息。" : "已在 Claude CLI 重命名会话。", "Success");
            Save();
        }

        private string AskSessionName(string title, bool fork, bool project = false)
        {
            const string suffix = "（分支）";
            int available = SessionTitleLimit - (fork ? suffix.Length : 0);
            string initialName = (title.Length > available ? title.Substring(0, available) : title) + (fork ? suffix : "");
            var dialog = new Window {
                Owner = window, Title = project ? "编辑项目" : fork ? "创建会话分支" : "重命名会话",
                Width = 420, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
                Background = BrushFor("Canvas"), Foreground = BrushFor("Ink"), FontFamily = window.FontFamily, FontSize = window.FontSize
            };
            dialog.Resources.MergedDictionaries.Add(window.Resources);
            var panel = new StackPanel { Margin = new Thickness(18) };
            panel.Children.Add(new TextBlock {
                Text = project ? "修改侧栏中的项目名称，原文件夹路径保持不变。" : fork ? "复制当前会话上下文，并以新名称继续。" : "新名称将显示在 Claude CLI 和最近对话中。",
                Foreground = BrushFor("MutedInk"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
            });
            var field = new TextBox { Text = initialName, MaxLength = SessionTitleLimit, MinHeight = 34, VerticalContentAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(field, "会话名称");
            panel.Children.Add(field);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            var cancelButton = new Button { Content = "取消", IsCancel = true, MinWidth = 65, Margin = new Thickness(0, 0, 8, 0) };
            var confirm = new Button { Content = fork ? "创建分支" : "保存名称", IsDefault = true, Style = (Style)window.Resources["Primary"] };
            field.TextChanged += delegate { confirm.IsEnabled = ValidSessionTitle(field.Text); };
            confirm.IsEnabled = ValidSessionTitle(field.Text);
            confirm.Click += delegate { if (ValidSessionTitle(field.Text)) dialog.DialogResult = true; };
            buttons.Children.Add(cancelButton);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);
            DialogVisuals.Apply(dialog);
            dialog.Content = panel;
            dialog.Loaded += delegate { field.Focus(); field.SelectAll(); };
            return dialog.ShowDialog() == true ? field.Text.Trim() : null;
        }

        private static bool ValidSessionTitle(string text)
        {
            return !String.IsNullOrWhiteSpace(text) && text.Length <= SessionTitleLimit &&
                !text.Any(character => Char.IsControl(character) || character == '\u2028' || character == '\u2029');
        }

        private void UpdateWorkspace()
        {
            Label("ChatTitle").Text = current.Title;
            Label("WorkspaceLabel").Text = current.Workspace;
            Label("WorkspaceLabel").ToolTip = current.Workspace;
        }

        private void BindPromptSuggestion(string name, string text)
        {
            Button(name).Click += delegate { prompt.Text = text; prompt.CaretIndex = prompt.Text.Length; prompt.Focus(); };
        }

        private void UpdateWelcome()
        {
            Find<StackPanel>("Welcome").Visibility = Visible(current.Messages.Count == 0);
        }

        private void HighlightNavigation(string name)
        {
            foreach (string item in new[] { "ToolsButton", "McpMarketButton", "SkillMarketButton", "StatsButton" })
                Button(item).Tag = item == name ? "Active" : null;
        }

        private async Task ShowMarketplace(MarketplaceKind kind)
        {
            HighlightNavigation(kind == MarketplaceKind.Mcp ? "McpMarketButton" : "SkillMarketButton");
            Find<Grid>("ChatPage").Visibility = Visibility.Collapsed;
            Find<Grid>("ToolsPage").Visibility = Visibility.Collapsed;
            Find<Grid>("StatsPage").Visibility = Visibility.Collapsed;
            Find<Grid>("MarketplacePage").Visibility = Visibility.Visible;
            await marketplace.Open(kind);
        }

        private void ShowTools(bool show)
        {
            HighlightNavigation(show ? "ToolsButton" : null);
            Find<Grid>("MarketplacePage").Visibility = Visibility.Collapsed;
            Find<Grid>("StatsPage").Visibility = Visibility.Collapsed;
            Find<Grid>("ToolsPage").Visibility = Visible(show);
            Find<Grid>("ChatPage").Visibility = Visible(!show);
        }

        private void ShowStatistics()
        {
            HighlightNavigation("StatsButton");
            Find<Grid>("MarketplacePage").Visibility = Visibility.Collapsed;
            Find<Grid>("ChatPage").Visibility = Visibility.Collapsed;
            Find<Grid>("ToolsPage").Visibility = Visibility.Collapsed;
            Find<Grid>("StatsPage").Visibility = Visibility.Visible;
            statistics.Show(current.SessionId);
        }

        private void ChooseWorkspace(object sender, RoutedEventArgs e)
        {
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog {
                Description = "选择项目文件夹", SelectedPath = current.Workspace, ShowNewFolderButton = false
            })
            {
                if (dialog.ShowDialog(new DialogOwner(window)) != System.Windows.Forms.DialogResult.OK) return;
                var projectState = ProjectState(dialog.SelectedPath);
                projectState.IsRemoved = false;
                projectState.IsArchived = false;
                navigation.ShowArchived = false;
                OpenWorkspace(dialog.SelectedPath);
                Save();
            }
        }

        private async void ChooseCli(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog {
                Title = "选择本机 Claude CLI 的可执行文件", Filter = "Claude CLI (claude.exe)|claude.exe|可执行文件 (*.exe)|*.exe",
                CheckFileExists = true
            };
            if (installation != null) dialog.InitialDirectory = Path.GetDirectoryName(installation.DisplayPath);
            if (dialog.ShowDialog(window) != true) return;
            data.CliPath = dialog.FileName;
            Save();
            await Probe();
        }

        private async Task Probe()
        {
            int revision = ++probeRevision;
            installation = null;
            UpdateButtons();
            CliStatus("正在查找 Claude CLI", "MutedInk", null);
            try
            {
                var found = CliInstallation.Find(data.CliPath, AppDomain.CurrentDomain.BaseDirectory);
                if (found == null)
                {
                    CliStatus("未找到 Claude CLI", "Failure", data.CliPath);
                    return;
                }
                var version = new StringBuilder();
                using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                {
                    int exit = await runner.RunAsync(found, AppDomain.CurrentDomain.BaseDirectory, new[] { "--version" }, null,
                        (line, error) => { if (!error && version.Length < 500) version.AppendLine(line); }, timeout.Token);
                    if (closing || revision != probeRevision) return;
                    string text = version.ToString().Trim();
                    if (exit != 0 || !Regex.IsMatch(text, @"\b\d+\.\d+\.\d+\b.*Claude Code", RegexOptions.IgnoreCase))
                    {
                        CliStatus("CLI 版本检查失败", "Failure", found.DisplayPath);
                        return;
                    }
                    installation = found;
                    CliStatus("Claude Code " + text.Split(' ')[0], "Success", found.DisplayPath);
                }
            }
            catch (Exception ex)
            {
                if (!closing && revision == probeRevision) CliStatus("无法启动 Claude CLI", "Failure", ex.Message);
            }
            finally { if (!closing && revision == probeRevision) UpdateButtons(); }
        }

        private void CliStatus(string label, string resource, string detail)
        {
            Label("CliLabel").Text = label;
            Button("CliButton").ToolTip = detail ?? label;
            Label("CliPathLabel").Text = detail ?? label;
            Label("CliPathLabel").ToolTip = detail ?? label;
            Find<Ellipse>("CliDot").SetResourceReference(Shape.FillProperty, resource);
        }

        private void RenderAttachments()
        {
            var panel = Find<WrapPanel>("AttachmentList");
            panel.Children.Clear();
            if (current == null || current.DraftAttachments == null) return;
            foreach (var attachment in current.DraftAttachments.ToArray())
                panel.Children.Add(AttachmentFiles.Card(attachment, () => {
                    current.DraftAttachments.Remove(attachment);
                    RenderAttachments(); UpdateButtons(); Save();
                }));
        }

        private void QueueAttachment(Func<ChatAttachment> create)
        {
            if (Busy || loadingConversation || current == null) return;
            if (current.DraftAttachments.Count >= AttachmentFiles.MaxCount) throw new IOException("每条消息最多添加 10 个附件。");
            var attachment = create();
            if (current.DraftAttachments.Sum(a => a.Size) + attachment.Size > AttachmentFiles.MaxTotalSize)
            {
                File.Delete(attachment.LocalPath);
                throw new IOException("每条消息的附件总大小最多 30 MB。");
            }
            current.DraftAttachments.Add(attachment);
            RenderAttachments(); UpdateButtons();
            if (!data.Conversations.Contains(current)) data.Conversations.Insert(0, current);
            current.Draft = prompt.Text;
            Save();
        }

        private void AddAttachments(IEnumerable<string> paths)
        {
            var errors = new List<string>();
            foreach (string path in paths)
            {
                try { QueueAttachment(() => AttachmentFiles.Import(path, store.AttachmentDirectory)); }
                catch (Exception ex) { errors.Add(Path.GetFileName(path) + "：" + ex.Message); }
            }
            if (errors.Count > 0) ChatStatus(String.Join("；", errors), "Failure");
            else ChatStatus("附件已添加，发送前可移除。", "MutedInk");
        }

        private void PasteAttachment(object sender, DataObjectPastingEventArgs e)
        {
            if (Busy || loadingConversation) { e.CancelCommand(); return; }
            if (!AttachmentPaste.HasAttachment(e.DataObject)) return;
            e.CancelCommand();
            try { PasteAttachmentData(e.DataObject); }
            catch (Exception ex) { ChatStatus("粘贴附件失败 · " + ex.Message, "Failure"); }
        }

        private void PasteAttachmentData(IDataObject data)
        {
            if (Busy || loadingConversation) return;
            if (data.GetDataPresent(DataFormats.FileDrop))
                AddAttachments((string[])data.GetData(DataFormats.FileDrop));
            else if (data.GetDataPresent(DataFormats.Bitmap))
            {
                var image = data.GetData(DataFormats.Bitmap) as System.Windows.Media.Imaging.BitmapSource;
                if (image == null) throw new IOException("剪贴板图片暂时不可读取，请重新复制。");
                QueueAttachment(() => AttachmentFiles.SaveImage(image, store.AttachmentDirectory,
                    "截图-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png"));
                ChatStatus("截图已添加。", "MutedInk");
            }
        }

        // 客户端已有入口在本地路由；Skill、自定义命令和 /compact 原样交给 CLI。
        private async Task<bool> HandleComposerCommand(string text)
        {
            var match = Regex.Match(text, @"^/([a-zA-Z0-9_-]+)(?:\s+(.*))?$", RegexOptions.Singleline);
            if (!match.Success) return false;
            string name = match.Groups[1].Value.ToLowerInvariant(), argument = match.Groups[2].Value.Trim();
            if (!new[] { "help", "new", "clear", "model", "effort", "permissions", "plan", "mcp", "skills", "plugin", "doctor", "status" }.Contains(name)) return false;
            if (name == "model" && argument.Length > 0)
            {
                var profile = data.ModelProfiles.FirstOrDefault(item => String.Equals(item.Name, argument, StringComparison.OrdinalIgnoreCase) || String.Equals(item.ModelId, argument, StringComparison.OrdinalIgnoreCase));
                var choice = argument == "default" || argument == "本机默认" ? Select(sessionModel, "") : profile == null ? null : Select(sessionModel, profile.Id);
                if (choice == null) { ChatStatus("未找到该模型配置，可使用 /model 打开配置。", "Failure"); return true; }
                sessionModel.SelectedItem = choice;
            }
            else if (name == "effort" && argument.Length > 0)
            {
                var choice = effort.Items.Cast<Option>().FirstOrDefault(item => String.Equals(item.Value, argument, StringComparison.OrdinalIgnoreCase) || item.ToString() == argument);
                if (choice == null) { ChatStatus("推理力度：low、medium、high、xhigh、max，或使用 /effort 打开设置。", "Failure"); return true; }
                effort.SelectedItem = choice; CaptureOptions(); Save();
            }
            else if (argument.Length > 0) return false;
            else
            {
                if (name == "new" || name == "clear") { prompt.Clear(); current.Draft = ""; Activate(NewConversation()); Save(); }
                else if (name == "model") ConfigureModels();
                else if (name == "effort" || name == "permissions" || name == "plan")
                {
                    if (name == "plan") { permission.SelectedItem = Select(permission, "plan"); CaptureOptions(); Save(); }
                    if (name == "effort") optionMenus.ShowEfforts(Button("EffortButton"));
                    else optionMenus.ShowPermissions(Button("ConfigButton"));
                }
                else if (name == "status") ShowStatistics();
                else if (name == "mcp") await ShowMarketplace(MarketplaceKind.Mcp);
                else if (name == "skills") await ShowMarketplace(MarketplaceKind.Skill);
                else
                {
                    ShowTools(true);
                    string label = name == "doctor" ? "健康诊断" : name == "plugin" ? "插件列表" : "CLI 帮助";
                    if (installation != null) await RunAction(CliAction.All.First(action => action.Name == label));
                    else ChatStatus("尚未找到 Claude CLI，请先选择本机可执行文件。", "Failure");
                }
            }
            if (completions != null) completions.Close();
            prompt.Clear(); current.Draft = ""; Save();
            return true;
        }

        private RequestOptions ChatOptions(bool compacting)
        {
            var selected = ActiveModel;
            return new RequestOptions { Model = selected == null ? null : selected.ModelId,
                Effort = data.Effort, Permission = data.Permission, SessionId = current.SessionId, AskToolPermission = !compacting };
        }

        private async Task Send(string command = null)
        {
            string text = command ?? prompt.Text.Trim();
            if (Busy || loadingConversation || current == null) return;
            if (command == null && text.StartsWith("/", StringComparison.Ordinal) && current.DraftAttachments.Count == 0 && await HandleComposerCommand(text)) return;
            var attachments = command == null ? new List<ChatAttachment>(current.DraftAttachments) : new List<ChatAttachment>();
            if (Busy || loadingConversation || installation == null || (String.IsNullOrWhiteSpace(text) && attachments.Count == 0)) return;
            if (MissingModelProfile)
            {
                ChatStatus("请先为当前对话选择可用的模型。", "Failure");
                return;
            }
            if (!CheckWorkspace()) return;
            bool compacting = Regex.IsMatch(text, @"^/compact(?:\s|$)");
            if (attachments.Count > 0 && text.StartsWith("/", StringComparison.Ordinal))
            {
                ChatStatus("附件请随普通消息发送，不能附加到斜杠命令。", "Failure");
                return;
            }
            if (compacting && String.IsNullOrEmpty(current.SessionId))
            {
                ChatStatus("请先发送消息，建立可压缩的 Claude CLI 会话。", "Failure");
                return;
            }
            ModelProfile selected = ActiveModel;
            IDictionary<string, string> environment = null;
            if (selected != null)
            {
                try
                {
                    if (!ModelProfile.IsModelId(selected.ModelId)) throw new InvalidOperationException("请在「模型配置」中检查模型 ID。");
                    environment = selected.CliEnvironment();
                }
                catch (InvalidOperationException ex)
                {
                    ChatStatus(ex.Message, "Failure");
                    return;
                }
            }
            if (text.Length > 1000000)
            {
                ChatStatus("消息超过 100 万字符，请缩短后再发送。", "Failure");
                return;
            }
            CaptureOptions();
            object userContent = null;
            try
            {
                text = AttachmentFiles.Prompt(text, attachments);
                if (attachments.Count > 0) userContent = AttachmentFiles.Content(text, attachments);
            }
            catch (Exception ex)
            {
                ChatStatus("无法读取附件 · " + ex.Message, "Failure");
                return;
            }
            var options = ChatOptions(compacting);
            if (!compacting)
            {
                if (current.Messages.Count == 0) current.Title = TitleFrom(text);
                current.UpdatedUtc = DateTime.UtcNow.ToString("o");
                current.Messages.Add(new StoredMessage { Role = "user", Text = text, Attachments = attachments });
                data.Conversations.Remove(current);
                data.Conversations.Insert(0, current);
                data.ActiveConversationId = current.Id;
                UpdateHistory();
                UpdateWorkspace();
                UpdateWelcome();
                AddAttachedMessage("user", text, attachments);
                reply = (MarkdownReply)AddMessage("assistant", "");
                replyProgress = (ReplyProgressView)reply.Tag;
                replyProgress.Start(data.Effort);
            }
            stream = new ClaudeStream();
            requestGateway = null;
            costBaseline = null;
            requestedModel = options.Model;
            requestedModelName = selected == null ? "本机默认" : selected.Name;
            requestRates = data.RequestRates.ToList();
            requestStart = DateTime.UtcNow;
            statisticsRecorded = false;
            if (command == null) { prompt.Clear(); current.DraftAttachments.Clear(); RenderAttachments(); }
            ScrollToEnd();
            Save();
            await Run(compacting ? "压缩上下文" : "对话", options.Arguments(), text,
                validateResult: compacting ? (Func<string>)(() => stream.ValidateManualCompaction(options.SessionId)) : null,
                environment: environment, session: current, askToolPermission: options.AskToolPermission, userContent: userContent);
        }

        private Task<bool> RequestToolPermission(CliToolPermission request, CancellationToken token)
        {
            return window.Dispatcher.InvokeAsync(() => ShowToolPermission(request, token)).Task.Unwrap();
        }

        private async Task<bool> ShowToolPermission(CliToolPermission request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var panel = Find<Border>("ToolPermissionPanel");
            var arguments = Find<TextBox>("ToolPermissionInput");
            permissionDecision = answer;
            Label("ToolPermissionName").Text = request.Tool;
            Label("ToolPermissionWorkspace").Text = current.Workspace;
            Label("ToolPermissionReason").Text = String.IsNullOrWhiteSpace(request.Reason) ? "请确认是否允许执行此工具。" : request.Reason;
            arguments.Text = request.DescribeInput();
            arguments.ScrollToHome();
            panel.Visibility = Visibility.Visible;
            try
            {
                using (token.Register(() => answer.TrySetCanceled()))
                {
                    bool allowed = await answer.Task;
                    token.ThrowIfCancellationRequested();
                    if (!allowed)
                    {
                        permissionRejected = true;
                        cancellation.CancelAfter(TimeSpan.FromSeconds(5));
                    }
                    return allowed;
                }
            }
            finally
            {
                if (permissionDecision == answer)
                {
                    panel.Visibility = Visibility.Collapsed;
                    arguments.Clear();
                    permissionDecision = null;
                    if (replyProgress != null && !closing)
                        replyProgress.Observe(permissionRejected ? "正在停止" : "继续执行", clock.Elapsed.TotalSeconds);
                }
            }
        }

        private async Task RunAction(CliAction action)
        {
            if (action.Name == "版本")
            {
                if (Busy || loadingConversation || installation == null) return;
                await CheckCliUpdate();
                return;
            }
            if (Busy || loadingConversation || installation == null || !CheckWorkspace()) return;
            await Run(action.Name, action.Arguments, null);
        }

        private async Task CheckCliUpdate()
        {
            var selected = installation;
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            bool attempted = false;
            await Run("版本", new[] { "--version" }, null, directory: directory, execute: async token => {
                WriteLog("正在检测本机 CLI：" + selected.DisplayPath);
                Version local = await CliUpdate.LocalAsync(runner, selected, directory, token);
                CliStatus("Claude Code " + local, "Success", selected.DisplayPath);
                WriteLog("本机版本：" + local);
                WriteLog("正在查询最新版本（latest）…");
                WriteLog("版本来源：" + CliUpdate.LatestUrl);
                string latest = await CliUpdate.LatestAsync(token);
                WriteLog("最新版本：" + latest);
                if (local.CompareTo(CliUpdate.ParseVersion(latest)) >= 0)
                {
                    WriteLog(local.ToString() == latest ? "当前已是最新版本。" : "本机版本比 latest 更新，无需更新。");
                    return 0;
                }
                var plan = CliUpdate.Plan(selected, latest);
                WriteLog("发现新版本，可更新：" + local + " → " + latest);
                token.ThrowIfCancellationRequested();
                if (closing) throw new OperationCanceledException();
                var choice = DialogVisuals.Show(window, "本机版本：" + local + "\n最新版本：" + latest +
                    "\n\n安装位置：" + selected.DisplayPath + "\n更新命令：" + plan.Command +
                    "\n\n是否立即下载并更新 CLI？", "Claude Code 版本更新", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No);
                token.ThrowIfCancellationRequested();
                if (choice != MessageBoxResult.Yes)
                {
                    WriteLog("已选择暂不更新，本机版本保持 " + local + "。");
                    return 0;
                }
                attempted = true;
                operation = "更新 CLI";
                Label("ToolResultTitle").Text = "更新 CLI";
                Find<TextBox>("ToolCommand").Text = plan.Command;
                WriteLog("$ " + plan.Command);
                int exit;
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    timeout.CancelAfter(TimeSpan.FromMinutes(8));
                    exit = await runner.RunAsync(plan.Installation, directory, plan.Arguments, null,
                        (line, error) => pending.Enqueue(Tuple.Create(line, error, DateTime.UtcNow)), timeout.Token);
                }
                Pump();
                if (exit != 0) return exit;
                WriteLog("正在重新检测更新后的本机版本…");
                Version updated = await CliUpdate.LocalAsync(runner, selected, directory, token);
                WriteLog("更新后版本：" + updated);
                if (updated.CompareTo(CliUpdate.ParseVersion(latest)) < 0)
                    throw new IOException("更新命令已结束，但当前 CLI 尚未达到目标版本 " + latest +
                        "。请检查日志、更新通道、版本限制或安装权限后重试。");
                CliStatus("Claude Code " + updated, "Success", selected.DisplayPath);
                WriteLog("CLI 更新成功，下次执行命令将使用 " + updated + "。");
                return 0;
            });
            if (attempted && !closing) await Probe();
        }

        private bool CheckWorkspace()
        {
            if (Directory.Exists(current.Workspace)) return true;
            const string message = "工作目录不存在，请返回对话并更换目录。";
            Label("ToolStatus").Text = message;
            ChatStatus(message, "Failure");
            return false;
        }

        private async Task<bool> Run(string label, string[] arguments, string input, string directory = null,
            Action<string> observeLine = null, Func<string> validateResult = null, IDictionary<string, string> environment = null,
            Conversation session = null, bool askToolPermission = false, object userContent = null,
            Func<CancellationToken, Task<int>> execute = null)
        {
            var token = cancellation = new CancellationTokenSource();
            operation = label;
            permissionRejected = false;
            clock = Stopwatch.StartNew();
            lastError = null;
            Label("ToolStatus").SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            output.Clear();
            log.Clear();
            toolOutput.Clear();
            Find<StackPanel>("ToolOutputEmpty").Visibility = Visibility.Collapsed;
            Label("ToolResultTitle").Text = label;
            var action = CliAction.All.FirstOrDefault(item => item.Name == label);
            Label("ToolResultDescription").Text = action != null ? action.Description : "Claude CLI 执行日志";
            string command = "claude " + String.Join(" ", arguments.Select(CliRunner.QuoteArgument));
            Find<TextBox>("ToolCommand").Text = command;
            Find<Border>("ToolCommandSurface").Visibility = Visibility.Visible;
            Label("ToolExitCode").Text = "";
            SetToolBadge("运行中", "Accent", "Selected");
            WriteLog("$ " + command, true, false);
            UpdateButtons();
            timer.Start();
            Pump();
            int exit = -1;
            bool stopped = false;
            string failure = null;
            try
            {
                if (session != null)
                {
                    session.CliConfigDirectory = await sessionLocations.ResolveAsync(installation, session, token.Token);
                    var scoped = environment == null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
                    scoped["CLAUDE_CONFIG_DIR"] = session.CliConfigDirectory;
                    environment = scoped;
                }
                string workspace = directory ?? current.Workspace;
                if (stream != null)
                {
                    var routing = environment;
                    requestGateway = await Task.Run(() => GatewayRoute.Resolve(workspace, routing), token.Token);
                    costBaseline = session != null ? await Task.Run(() => SessionCost.ReadBaseline(session), token.Token) : null;
                    requestStart = DateTime.UtcNow;
                    requestRates = data.RequestRates.ToList();
                }
                exit = execute != null ? await execute(token.Token) : await runner.RunAsync(installation, workspace, arguments, input,
                    (line, error) =>
                    {
                        pending.Enqueue(Tuple.Create(line, error, DateTime.UtcNow));
                        if (!error && observeLine != null) observeLine(line);
                    }, token.Token, environment,
                    askToolPermission ? (Func<CliToolPermission, CancellationToken, Task<bool>>)RequestToolPermission : null, userContent);
                stopped = token.IsCancellationRequested;
            }
            catch (OperationCanceledException) { stopped = true; }
            catch (Exception ex) { failure = ex.Message; }
            finally
            {
                timer.Stop();
                cancellation = null;
                token.Dispose();
                if (!closing)
                {
                    Pump();
                    if (permissionRejected)
                    {
                        stopped = true;
                        failure = null;
                    }
                    if (!stopped && failure == null)
                    {
                        failure = GetFailure(exit);
                        if (failure == null && validateResult != null) failure = validateResult();
                    }
                    Finish(exit, stopped, failure);
                    UpdateButtons();
                }
            }
            return !closing && !stopped && failure == null;
        }

        private string GetFailure(int exit)
        {
            if (stream != null && stream.Failure != null) return stream.Failure;
            if (exit != 0) return String.IsNullOrWhiteSpace(lastError) ? "Claude CLI 退出码 " + exit + "。详情见执行日志。" : lastError;
            if (stream != null && !stream.Finished) return "Claude CLI 未返回完整的执行结果。详情见执行日志。";
            return null;
        }

        private void Pump()
        {
            Tuple<string, bool, DateTime> item;
            var batch = new StringBuilder();
            bool changed = false;
            int remaining = Busy ? 1500 : Int32.MaxValue;
            while (remaining-- > 0 && pending.TryDequeue(out item))
            {
                string line = Regex.Replace(item.Item1, @"\x1B\[[0-?]*[ -/]*[@-~]", "");
                WriteLog((item.Item2 ? "[stderr] " : "") + line, false);
                changed = true;
                if (item.Item2 && !String.IsNullOrWhiteSpace(line)) lastError = line;
                if (stream != null && !item.Item2)
                {
                    batch.Append(stream.Read(line, item.Item3));
                    foreach (string activity in stream.DrainActivities())
                        if (replyProgress != null) replyProgress.Observe(activity, clock.Elapsed.TotalSeconds);
                }
            }
            if (batch.Length > 0 && reply != null)
            {
                reply.AppendMarkdown(batch.ToString());
                ScrollToEnd();
            }
            if (changed) FlushLog();
            if (replyProgress != null && stream != null) replyProgress.SetRequestIds(stream.RequestIds);
            if (!Busy) return;
            string elapsed = ((int)clock.Elapsed.TotalSeconds) + " 秒";
            if (replyProgress != null)
            {
                if (permissionDecision != null) replyProgress.Observe("等待工具授权", clock.Elapsed.TotalSeconds);
                else if (permissionRejected || cancellation.IsCancellationRequested) replyProgress.Observe("正在停止", clock.Elapsed.TotalSeconds);
                replyProgress.Update(clock.Elapsed.TotalSeconds, stream == null ? null : stream.Model);
            }
            if (permissionDecision != null)
            {
                Label("ToolStatus").Text = operation + " · 等待工具授权";
                SetToolBadge("等待授权", "Accent", "Selected");
                ChatStatus("等待工具授权", "Accent");
                return;
            }
            string running = permissionRejected || cancellation.IsCancellationRequested ? "正在停止…" : "运行中 · " + elapsed;
            Label("ToolStatus").Text = operation + " · " + running;
            SetToolBadge(cancellation.IsCancellationRequested || permissionRejected ? "正在停止" : "运行中", "Accent", "Selected");
            if (stream != null)
            {
                if (!String.IsNullOrEmpty(stream.Model)) running += " · " + stream.Model;
                if (!String.IsNullOrEmpty(stream.Activity)) running += " · " + stream.Activity;
                ChatStatus(operation == "压缩上下文" ? operation + " · " + running : running, "Accent");
            }
        }

        private void Finish(int exit, bool stopped, string failure)
        {
            string elapsed = clock.Elapsed.TotalSeconds.ToString("0.0") + " 秒";
            string status = permissionRejected ? "已拒绝授权并终止" : stopped ? "已停止" : failure != null ? "执行失败" : "已完成";
            Label("ToolStatus").Text = operation + " · " + status + " · " + elapsed;
            Label("ToolStatus").SetResourceReference(TextBlock.ForegroundProperty, failure == null ? "MutedInk" : "Failure");
            SetToolBadge(status, failure != null ? "Failure" : stopped || permissionRejected ? "MutedInk" : "Success",
                failure != null ? "FailureSurface" : stopped || permissionRejected ? "Sidebar" : "SuccessSurface");
            Label("ToolExitCode").Text = exit >= 0 ? "退出码 " + exit : "未返回退出码";
            if (failure != null) WriteLog("错误 · " + failure);
            WriteLog("[" + status + (exit >= 0 ? " · 退出码 " + exit : "") + " · " + elapsed + "]", true, false);
            if (stream == null) return;
            LinkSession(failure == null && stream.Finished);
            bool statisticsSaved = RecordStatistics(stopped, failure != null);

            string response = null;
            if (reply != null)
            {
                response = reply.Markdown;
                if (String.IsNullOrWhiteSpace(response) && failure == null && !permissionRejected)
                {
                    response = stream.Result;
                    reply.AppendMarkdown(response);
                }
                if (replyProgress != null) replyProgress.Update(clock.Elapsed.TotalSeconds, stream.Model);
                var summary = replyProgress == null ? null : replyProgress.Complete(status, clock.Elapsed.TotalSeconds, DateTime.UtcNow);
                current.Messages.Add(new StoredMessage { Role = "assistant", Text = response ?? "", RunSummary = summary });
            }
            if (stream.Compaction == "auto" || (stream.Compaction == "manual" && failure == null && !stopped))
            {
                string notice = stream.Compaction == "auto" ? "已自动压缩上下文" : "已压缩上下文";
                if (stream.PreCompactTokens.HasValue && stream.PostCompactTokens.HasValue)
                    notice += " · CLI 估算 " + stream.PreCompactTokens.Value.ToString("N0") + " → " +
                        stream.PostCompactTokens.Value.ToString("N0") + " tokens";
                StoreMessage("notice", notice + "。");
            }
            if (failure != null && !String.Equals(response, failure, StringComparison.Ordinal)) StoreMessage("error", failure);
            if (stopped) StoreMessage("notice", "执行已停止。");
            if (permissionRejected) StoreMessage("notice", "已拒绝工具授权，本轮请求已终止。");
            else if (stream.PermissionDenials > 0) StoreMessage("notice", "Claude CLI 拒绝了 " + stream.PermissionDenials + " 次工具调用，详情见执行日志。");
            string detail = String.IsNullOrEmpty(stream.Model) ? "" : " · " + stream.Model;
            ChatStatus((reply == null ? operation + " · " : "") + status + " · " + elapsed + detail,
                failure != null ? "Failure" : stopped ? "MutedInk" : "Success");
            if (!statisticsSaved) ChatStatus(status + " · 请求统计未写入本地文件", "Failure");
            if (Find<Grid>("StatsPage").IsVisible) statistics.Show(current.SessionId);
            current.UpdatedUtc = DateTime.UtcNow.ToString("o");
            UpdateHistory();
            reply = null;
            replyProgress = null;
            stream = null;
            Save();
            ScrollToEnd();
        }

        private void LinkSession(bool completed)
        {
            if (!String.IsNullOrEmpty(current.SessionId) || String.IsNullOrEmpty(stream.SessionId)) return;
            if (!completed)
            {
                string file = CliConversationCatalog.FindPersistedTranscript(current.CliConfigDirectory, current.Workspace, stream.SessionId);
                if (file == null) return;
                current.CliTranscriptPath = file;
            }
            current.SessionId = stream.SessionId;
        }

        private bool RecordStatistics(bool stopped, bool failed)
        {
            if (stream == null || statisticsRecorded) return true;
            statisticsRecorded = true;
            var rows = stream.Statistics(requestStart, requestGateway, current.SessionId,
                requestedModel, costBaseline, stopped, failed, permissionRejected ? "拒绝工具授权" : "手动停止", operation);
            foreach (var row in rows) row.RequestedModelName = requestedModelName;
            if (replyProgress != null) replyProgress.SetRequestIds(rows.Select(row => row.RequestId));
            return statistics.Record(rows, requestRates);
        }

        private void StoreMessage(string role, string text)
        {
            current.Messages.Add(new StoredMessage { Role = role, Text = text });
            AddMessage(role, text);
        }

        private void SetToolBadge(string text, string foreground, string background)
        {
            Find<System.Windows.Shapes.Ellipse>("ToolPulseDot").SetResourceReference(System.Windows.Shapes.Shape.FillProperty, foreground);
            Label("ToolBadge").Text = text;
            Label("ToolBadge").SetResourceReference(TextBlock.ForegroundProperty, foreground);
            Find<Border>("ToolBadgeSurface").SetResourceReference(Border.BackgroundProperty, background);
        }

        private void WriteLog(string message, bool refresh = true, bool present = true)
        {
            if (present) toolOutput.Append(message);
            output.AppendLine(message);
            if (output.Length > LogLimit) output.Remove(0, output.Length - LogLimit);
            if (refresh) FlushLog();
        }

        private void FlushLog()
        {
            bool follow = log.ExtentHeight - log.VerticalOffset - log.ViewportHeight < 30;
            double offset = log.VerticalOffset;
            log.Text = (output.Length == LogLimit ? "[仅显示最近 22 万字符]" + Environment.NewLine : "") + output;
            toolOutput.Flush();
            if (follow) log.ScrollToEnd();
            else log.ScrollToVerticalOffset(offset);
        }

        private Control AddMessage(string role, string text)
        {
            return AddAttachedMessage(role, text, null);
        }

        private Control AddAttachedMessage(string role, string text, IList<ChatAttachment> attachments)
        {
            string name = role == "user" ? "我" : role == "assistant" ? "Claude" : role == "error" ? "执行错误" : "执行提示";
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };
            row.Children.Add(CreateRoleHeader(role, name));
            var card = new Border { CornerRadius = new CornerRadius(6), Padding = role == "user" ? new Thickness(16, 12, 16, 12) : new Thickness(0), BorderThickness = new Thickness(0) };
            card.SetResourceReference(Border.BackgroundProperty, role == "user" ? "Sidebar" : "Canvas");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            Control body = role == "assistant" ? (Control)new MarkdownReply(text) :
                new TextBox {
                    Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                    Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = Brushes.Transparent
                };
            body.FontSize = 15;
            body.SetResourceReference(Control.ForegroundProperty, role == "error" ? "Failure" : "Ink");
            System.Windows.Automation.AutomationProperties.SetName(body, name);
            card.Child = body;
            row.Children.Add(card);
            if (attachments != null && attachments.Count > 0)
            {
                var previews = new WrapPanel { Margin = new Thickness(0,8,0,0) };
                foreach (var attachment in attachments) previews.Children.Add(AttachmentFiles.Card(attachment));
                row.Children.Add(previews);
            }
            if (role == "assistant")
            {
                var progress = new ReplyProgressView((ImageSource)window.Resources["BrandIcon"]);
                body.Tag = progress;
                row.Children.Add(progress);
            }
            messages.Children.Add(row);
            return body;
        }

        private FrameworkElement CreateRoleHeader(string role, string name)
        {
            bool assistant = role == "assistant";
            bool user = role == "user";
            string ink = assistant ? "AssistantInk" : user ? "UserInk" : role == "error" ? "Failure" : "MutedInk";
            string surface = assistant ? "AssistantSurface" : user ? "UserSurface" : "Sidebar";
            var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
            System.Windows.Automation.AutomationProperties.SetName(header, "消息角色：" + name);

            var avatar = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 10, 0) };
            avatar.SetResourceReference(Border.BackgroundProperty, surface);
            if (assistant)
            {
                avatar.Child = new System.Windows.Controls.Image {
                    Source = (ImageSource)window.Resources["BrandIcon"], Width = 27, Height = 27,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
            }
            else if (user)
            {
                avatar.Background = new ImageBrush(userAvatar) { Stretch = Stretch.UniformToFill };
                RenderOptions.SetBitmapScalingMode(avatar, BitmapScalingMode.HighQuality);
            }
            else
            {
                var glyph = new TextBlock {
                    Text = role == "error" ? "\uE783" : "\uE946",
                    FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                glyph.SetResourceReference(TextBlock.ForegroundProperty, ink);
                avatar.Child = glyph;
            }
            header.Children.Add(avatar);

            var title = new TextBlock { Text = name, FontSize = 15, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center };
            title.SetResourceReference(TextBlock.ForegroundProperty, ink);
            header.Children.Add(title);
            if (user || assistant)
            {
                var caption = new TextBlock { Text = user ? "我的消息" : "AI 助手", FontSize = 12, FontWeight = FontWeights.Medium };
                caption.SetResourceReference(TextBlock.ForegroundProperty, ink);
                var badge = new Border {
                    Child = caption, CornerRadius = new CornerRadius(4), Padding = new Thickness(7, 3, 7, 3),
                    Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                };
                badge.SetResourceReference(Border.BackgroundProperty, surface);
                header.Children.Add(badge);
            }
            return header;
        }

        private void ScrollToEnd()
        {
            window.Dispatcher.BeginInvoke(new Action(scroll.ScrollToEnd), DispatcherPriority.Background);
        }

        private void UpdateButtons()
        {
            foreach (var name in new[] { "NewButton", "WorkspaceButton", "AddProjectButton", "PickCliButton", "AutoCliButton", "ConfigButton", "ModelsButton", "EffortButton" })
                Button(name).IsEnabled = !Busy;
            foreach (Button command in actions) command.IsEnabled = installation != null && !Busy;
            history.IsEnabled = true;
            Find<StackPanel>("ProjectList").IsEnabled = true;
            foreach (var conversation in data.Conversations)
                conversation.SetExecutionState(Busy && stream != null && conversation == current, Busy && conversation != current);
            prompt.IsReadOnly = Busy;
            if (Busy && completions != null) completions.Close();
            sessionModel.IsEnabled = !Busy;
            permission.IsEnabled = effort.IsEnabled = !Busy;
            if (Busy) optionMenus.Close();
            Button("AttachButton").IsEnabled = !Busy && !loadingConversation;
            Find<WrapPanel>("AttachmentList").IsEnabled = !Busy && !loadingConversation;
            // 发送入口在执行时原位切换成停止按钮，不再占用模型配置旁的额外位置。
            var send = Button("SendButton");
            send.IsEnabled = Busy || (installation != null && !loadingConversation && !MissingModelProfile &&
                (!String.IsNullOrWhiteSpace(prompt.Text) || (current != null && current.DraftAttachments != null && current.DraftAttachments.Count > 0)));
            Find<System.Windows.Shapes.Path>("SendArrow").Visibility = Visible(!Busy);
            Find<System.Windows.Shapes.Rectangle>("SendStopIcon").Visibility = Visible(Busy);
            send.ToolTip = Busy ? (cancellation.IsCancellationRequested ? "正在停止当前请求…" : "运行中，点击停止当前请求") : "发送消息（Enter）";
            System.Windows.Automation.AutomationProperties.SetName(send, Busy ? "停止当前请求" : "发送");
            Button("StopToolButton").Visibility = Visible(Busy);
        }

        private void ChatStatus(string message, string resource)
        {
            Label("ChatStatus").Text = message;
            Label("ChatStatus").ToolTip = message;
            Find<Ellipse>("ChatDot").SetResourceReference(Shape.FillProperty, resource);
        }

        private void UpdateSummary()
        {
            var permissionChoice = permission.SelectedItem as Option;
            var effortChoice = effort.SelectedItem as Option;
            var mode = ComposerOptionMenus.Permission(permissionChoice == null ? null : permissionChoice.Value);
            var level = ComposerOptionMenus.Effort(effortChoice == null ? null : effortChoice.Value);
            Label("PermissionSummary").Text = mode.Title;
            Label("PermissionSummary").SetResourceReference(TextBlock.ForegroundProperty, mode.Caution ? "PermissionWarning" : "MutedInk");
            Label("ConfigSummary").Text = level.Title;
            Button("ConfigButton").ToolTip = mode.Title + " · " + mode.Description;
            Button("EffortButton").ToolTip = "推理强度：" + level.Title + " · " + level.Description;
        }

        private void CaptureOptions()
        {
            data.Effort = ((Option)effort.SelectedItem).Value;
            data.Permission = ((Option)permission.SelectedItem).Value;
        }

        private void UpdateThemeButton(bool animate = false)
        {
            var button = Button("ThemeButton");
            ThemeToggleIcon.Update(button, data.Dark, animate);
            button.Tag = data.Dark ? "True" : "False";
            button.ToolTip = data.Dark ? "切换为浅色主题" : "切换为深色主题";
            System.Windows.Automation.AutomationProperties.SetName(button, (string)button.ToolTip);
        }

        private bool Save()
        {
            try { store.Save(data); return true; }
            catch (Exception ex) { ChatStatus("保存本地记录失败 · " + ex.Message, "Failure"); return false; }
        }

        private void Stop()
        {
            if (cancellation != null) { cancellation.Cancel(); UpdateButtons(); }
        }

        private void Close(object sender, CancelEventArgs e)
        {
            if (Busy && DialogVisuals.Show(window, "Claude CLI 仍在运行。要停止并关闭窗口吗？", "Claude UI",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            if (stream != null)
            {
                Pump();
                LinkSession(false);
                RecordStatistics(true, false);
                if (reply != null && replyProgress != null)
                {
                    var summary = replyProgress.Complete("已中断", clock.Elapsed.TotalSeconds, DateTime.UtcNow);
                    current.Messages.Add(new StoredMessage { Role = "assistant", Text = reply.Markdown, RunSummary = summary });
                }
            }
            closing = true;
            catalogTimer.Stop();
            conversationRevision++;
            Stop();
            CaptureOptions();
            Save();
        }

        private static string TitleFrom(string text)
        {
            string condensed = Regex.Replace(text, @"\s+", " ").Trim();
            return condensed.Length > 45 ? condensed.Substring(0, 45) + "…" : condensed;
        }

        private sealed class DialogOwner : System.Windows.Forms.IWin32Window
        {
            public IntPtr Handle { get; private set; }
            public DialogOwner(Window owner) { Handle = new System.Windows.Interop.WindowInteropHelper(owner).Handle; }
        }

        private sealed class Option
        {
            private readonly string label;
            public readonly string Value;
            public Option(string label, string value) { this.label = label; Value = value; }
            public override string ToString() { return label; }
        }
    }
}
