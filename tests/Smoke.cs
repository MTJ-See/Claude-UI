using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUI
{
    internal static class Smoke
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Contains("__version_fixture__"))
            {
                Console.WriteLine("2.1.318 (Claude Code)");
                return args.Contains("--version") ? 0 : 1;
            }
            // Reuse the smoke executable as a deterministic CLI child process to exercise probe callbacks.
            if (args.Contains("__usage_fixture_success__") || args.Contains("__usage_fixture_multi__") ||
                args.Contains("__usage_fixture_response_exit_error__") || args.Contains("__usage_fixture_missing_result__"))
            {
                Console.WriteLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"probe-request\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":8}}}}");
                Console.WriteLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":2}}}");
                Console.WriteLine("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_stop\"}}");
                if (args.Contains("__usage_fixture_multi__"))
                    Console.WriteLine("{\"type\":\"assistant\",\"message\":{\"id\":\"probe-second\",\"model\":\"claude-sonnet-4-5\",\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":12,\"output_tokens\":3}}}");
                if (!args.Contains("__usage_fixture_missing_result__"))
                    Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":" + (args.Contains("__usage_fixture_multi__") ? "2" : "1") + "}");
                return args.Contains("__usage_fixture_response_exit_error__") ? 1 : 0;
            }
            if (args.Contains("__usage_fixture_empty_success__"))
            {
                Console.WriteLine("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1}");
                return 0;
            }
            if (args.Contains("__usage_fixture_failure__"))
            {
                Console.WriteLine("{\"type\":\"result\",\"subtype\":\"error_during_execution\",\"is_error\":true,\"api_error_status\":404}");
                return 1;
            }
            if (args.Contains("__usage_fixture_cancel__")) { Thread.Sleep(10000); return 0; }
            string work = Path.Combine(Path.GetTempPath(), "ClaudeUI-smoke-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            try
            {
                var store = new DataStore(work);
                CheckConversationDeletion(work);
                CheckThemeTransitions();
                CheckNativeDeveloperVisuals();
                CheckPromptCompletions(work);
                CheckCliUpdate(work);
                CheckHistoryMerge(work);
                CheckReplyTurnGrouping(work);
                string historyFile = Environment.GetEnvironmentVariable("CLAUDEUI_SMOKE_HISTORY_FILE");
                if (!String.IsNullOrWhiteSpace(historyFile))
                {
                    var recovered = CliConversationCatalog.ReadTranscript(historyFile);
                    Console.WriteLine("CHECK source history: " + recovered.Messages.Count + " visible messages, " +
                        recovered.InternalMessages.Count + " internal command records excluded (read only)");
                    string cacheDirectory = Environment.GetEnvironmentVariable("CLAUDEUI_SMOKE_CACHE_DIRECTORY");
                    if (!String.IsNullOrWhiteSpace(cacheDirectory))
                    {
                        var existing = new DataStore(cacheDirectory).Read().Conversations
                            .FirstOrDefault(item => item.SessionId == Path.GetFileNameWithoutExtension(historyFile));
                        if (existing != null)
                        {
                            var combined = CliHistoryMerge.Merge(existing.Messages, recovered);
                            Check(existing.Messages.All(old => combined.Any(message => message.Role == old.Role &&
                                (message.Text ?? "").Contains(old.Text ?? ""))), "Actual cache migration lost reply content.");
                            Console.WriteLine("CHECK actual cache: " + existing.Messages.Count + " cards -> " + combined.Count +
                                " cards; all cached message text retained (read only)");
                        }
                    }
                }
                CheckPortableData(work);
                store.Save(new AppData { Dark = true, Workspace = work });
                string settingsFile = Path.Combine(work, "state.json");
                string settings = File.ReadAllText(settingsFile, Encoding.UTF8);
                File.WriteAllText(settingsFile, "{\"AutoCompactPercent\":\"25\"," + settings.Substring(1), Encoding.UTF8);
                var loaded = store.Read();
                Check(loaded.Dark && loaded.Workspace == work, "State did not round-trip.");
                loaded.Dark = false;
                store.Save(loaded);
                Check(!store.Read().Dark, "State update did not replace the previous record.");
                Check(!File.ReadAllText(settingsFile).Contains("AutoCompactPercent"), "The retired compaction preference was saved again.");

                var profile = new ModelProfile { Id = "fixture", Name = "测试模型", ModelId = "fixture-model",
                    BaseUrl = "https://example.test/anthropic", Authentication = ModelProfile.Bearer,
                    ProtectedCredential = ModelProfile.Protect("synthetic-credential") };
                loaded.Model = "previous-model";
                loaded.ModelProfiles.Add(profile);
                loaded.Conversations.Add(new Conversation { Title = "历史", Workspace = work });
                store.Save(loaded);
                loaded = store.Read();
                string previousId = loaded.Conversations[0].ModelProfileId;
                Check(loaded.Model == null && loaded.ModelProfiles.Count == 2 && loaded.ModelProfiles.Any(item => item.Id == previousId), "Legacy model did not migrate.");
                Check(!File.ReadAllText(Path.Combine(work, "state.json")).Contains("synthetic-credential"), "Plaintext credential leaked into settings.");
                Check(profile.CliEnvironment()["ANTHROPIC_AUTH_TOKEN"] == "synthetic-credential" &&
                    profile.CliEnvironment()["ANTHROPIC_API_KEY"] == null, "Bearer credentials were not isolated.");
                Check(ModelProfile.NormalizeEndpoint("http://127.0.0.1:321/api/") == "http://127.0.0.1:321/api" &&
                    ModelProfile.NormalizeEndpoint("https://example.test/v1/messages") == null &&
                    ModelProfile.NormalizeEndpoint("https://example.test/v1/chat/completions") == null, "Base URL validation failed.");
                var tokenDance = new ModelProfile { ModelId = "deepseek-v4.1-flash-1017", Authentication = ModelProfile.Bearer,
                    BaseUrl = "https://tokendance.space/gateway/v1/chat/completions", ProtectedCredential = profile.ProtectedCredential };
                var tokenDanceEnvironment = tokenDance.CliEnvironment();
                Check(tokenDanceEnvironment["ANTHROPIC_BASE_URL"] == "https://tokendance.space/gateway" &&
                    tokenDanceEnvironment["ANTHROPIC_AUTH_TOKEN"] == "synthetic-credential", "The saved TokenDance address was not corrected.");
                CheckRequestStatistics(store, work);
                CheckCancelledCosts(work);
                CheckUsageDashboard(work);
                CheckBillingRules(work);
                CheckProbeStatistics(work);
                var catalogFixture = CheckCliCatalog(work);

                var app = new Application();
                CheckFailedSessionRecovery(work);
                CheckStatisticsDeletion(work);
                CheckStatisticsPagination(work);
                var controller = new MainWindow(store);
                var window = controller.View;
                window.ApplyTemplate();
                CheckAttachments(controller, store, work);
                Check(window.FindName("SendButton") is Button && window.FindName("ActionButtons") is WrapPanel, "Window controls were not loaded.");
                var permissionPanel = (Border)window.FindName("ToolPermissionPanel");
                Check(permissionPanel.Visibility == Visibility.Collapsed && permissionPanel.HorizontalAlignment == HorizontalAlignment.Right &&
                    permissionPanel.VerticalAlignment == VerticalAlignment.Bottom &&
                    window.FindName("AllowToolPermission") is Button && window.FindName("DenyToolPermission") is Button,
                    "The permission prompt was not loaded at the bottom right.");
                var permissionOptions = new RequestOptions { Permission = "default", AskToolPermission = true };
                Check(permissionOptions.Arguments().Contains("--permission-prompt-tool") && permissionOptions.Arguments().Contains("stdio") &&
                    !permissionOptions.Arguments().Contains("text"), "Chat was not configured for CLI permission requests.");
                Check(window.FindName("ContinueButton") == null && !permissionOptions.Arguments().Contains("--continue"),
                    "The directory-wide continue action was not removed.");
                permissionOptions.SessionId = Guid.NewGuid().ToString();
                var resumeArguments = permissionOptions.Arguments();
                int resumeIndex = Array.IndexOf(resumeArguments, "--resume");
                Check(resumeIndex >= 0 && resumeIndex + 1 < resumeArguments.Length && resumeArguments[resumeIndex + 1] == permissionOptions.SessionId,
                    "Selecting a specific conversation no longer resumes that session.");
                var history = (ListBox)window.FindName("HistoryList");
                Check(window.FindName("AddProjectButton") is Button && window.FindName("ProjectList") is StackPanel,
                    "Project navigation or the folder picker button was not loaded.");
                Check(((FrameworkElement)window.FindName("ConversationsBody")).Visibility == Visibility.Visible,
                    "Conversations were not expanded by default.");
                ((Button)window.FindName("ConversationsToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((FrameworkElement)window.FindName("ConversationsBody")).Visibility == Visibility.Collapsed && store.Read().ConversationsCollapsed,
                    "Conversation collapse did not persist.");
                ((Button)window.FindName("ConversationsToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((FrameworkElement)window.FindName("ConversationsBody")).Visibility == Visibility.Visible && !store.Read().ConversationsCollapsed,
                    "Conversations did not expand again.");
                ((Button)window.FindName("ProjectsToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((FrameworkElement)window.FindName("ProjectsBody")).Visibility == Visibility.Collapsed, "Projects did not collapse.");
                ((Button)window.FindName("ProjectsToggleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var row = (DockPanel)history.ItemTemplate.LoadContent();
                Check(row.FindName("SessionMenuButton") is Button, "Recent session row is missing the operations button.");
                var picker = (ComboBox)window.FindName("SessionModelBox");
                Check(window.FindName("ModelsButton") is Button && picker.SelectedItem.ToString() == "previous-model", "Startup did not restore the saved workspace conversation.");
                new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                new ModelProfilesDialog(window, loaded.ModelProfiles, profile.Id, (draft, cancellation) => System.Threading.Tasks.Task.FromResult<string>(null));
                CheckBillingDialog(window, work);
                controller.OpenWorkspace(AppDomain.CurrentDomain.BaseDirectory);
                Check(history.SelectedItem == null && picker.SelectedItem.ToString() == "本机默认", "An empty workspace did not open a new conversation.");
                controller.OpenWorkspace(work + Path.DirectorySeparatorChar);
                Check(history.SelectedItem is Conversation && picker.SelectedItem.ToString() == "previous-model", "Reopening a workspace did not restore its chat and model.");
                picker.SelectedItem = picker.Items.Cast<object>().First(choice => choice.ToString() == profile.Name);
                Check(store.Read().Conversations[0].ModelProfileId == profile.Id, "Model choice did not persist for the session.");
                ((Button)window.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(picker.SelectedItem.ToString() == "本机默认", "Model choice unexpectedly changed the new session default.");
                Check(history.SelectedItem == null, "The new conversation button opened existing history.");
                var toolsButton = (Button)window.FindName("ToolsButton");
                toolsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((Grid)window.FindName("ToolsPage")).Visibility == Visibility.Visible, "Tools navigation failed.");
                var themeButton = (Button)window.FindName("ThemeButton");
                CheckSessionMenu(controller, loaded.Conversations[0]);
                CheckHistorySurface(history);
                ((Button)window.FindName("StatsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(((Grid)window.FindName("StatsPage")).Visibility == Visibility.Visible, "Request statistics navigation failed.");
                var stats = (ListView)window.FindName("StatsRows");
                stats.SelectedIndex = 0;
                Check(stats.Items.Count == 1 && ((RequestStat)stats.Items[0]).BilledText == "未返回" &&
                    !((TextBlock)window.FindName("StatsDetail")).Text.Contains("CLI 估算"), "CLI estimate appeared as actual billing.");
                stats.Measure(new Size(850, 300));
                stats.Arrange(new Rect(0, 0, 850, 300));
                stats.UpdateLayout();
                var fullRequestId = (TextBox)window.FindName("StatsRequestId");
                fullRequestId.SelectAll();
                Check(fullRequestId.IsReadOnly && fullRequestId.SelectedText == "请求 ID：" + ((RequestStat)stats.SelectedItem).RequestId,
                    "Statistics request ID was not fully selectable.");
                var idColumn = ((GridView)stats.View).Columns.First(column => (string)column.Header == "请求 ID");
                Check(idColumn.Width == 180 && idColumn.CellTemplate.LoadContent() is TextBox,
                    "Request ID column lost its compact width or selectable text.");
                var requestFilter = (TextBox)window.FindName("StatsRequestFilter");
                requestFilter.Text = "no-such-request";
                Check(stats.Items.Count == 0, "Request ID filtering did not update the dashboard.");
                ((Button)window.FindName("StatsResetFilters")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(stats.Items.Count == 1, "Reset did not restore dashboard records.");
                ((ComboBox)window.FindName("StatsStatusFilter")).SelectedItem = "失败";
                Check(stats.Items.Count == 0, "Status filtering did not update the dashboard.");
                ((Button)window.FindName("StatsResetFilters")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                string dashboardPreview = Environment.GetEnvironmentVariable("CLAUDE_UI_STATS_PREVIEW");
                if (!String.IsNullOrEmpty(dashboardPreview))
                {
                    var page = (Grid)window.FindName("StatsPage");
                    foreach (bool dark in new[] { false, true })
                    {
                        Theme.Apply(window.Resources, dark);
                        page.Measure(new Size(1180, 720)); page.Arrange(new Rect(0, 0, 1180, 720)); page.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(1180, 720, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(page);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(dashboardPreview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                    }
                    Theme.Apply(window.Resources, store.Read().Dark);
                }
                CheckStatisticsChatNavigation(controller, loaded);
                themeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(store.Read().Dark && themeButton.Content is DrawingImage && (string)themeButton.Tag == "True" && (string)themeButton.ToolTip == "切换为浅色主题", "Theme switch failed.");
                CheckSessionMenu(controller, loaded.Conversations[0]);
                CheckHistorySurface(history);
                Console.WriteLine("PASS session menu rendering without a system gutter in both themes");
                Console.WriteLine("PASS recent conversations keep their sidebar background during requests");
                Console.WriteLine("PASS window markup, tools navigation, theme switch, state persistence");

                Check(PromptShortcuts.GetAction(Key.Enter, ModifierKeys.None, false) == PromptAction.Send, "Enter did not send.");
                Check(PromptShortcuts.GetAction(Key.Enter, ModifierKeys.Control, false) == PromptAction.NewLine, "Ctrl+Enter did not insert a line.");
                Check(PromptShortcuts.GetAction(Key.Enter, ModifierKeys.None, true) == PromptAction.None, "Held Enter triggered another send.");
                Check(PromptShortcuts.GetAction(Key.LeftShift, ModifierKeys.Shift, false) == PromptAction.None, "The old Shift shortcut still sends.");
                Check(PromptShortcuts.GetAction(Key.Enter, ModifierKeys.Control | ModifierKeys.Shift, false) == PromptAction.None, "An unrelated chord triggered an action.");
                Console.WriteLine("PASS model configuration, secret storage, per-session selection, and Enter shortcuts");
                CheckComposerCommandRoutes(controller);
                CheckComposerOptionMenus(controller, store, work);
                CheckComposerSendState(controller);
                CheckRequestDetailLayout();
                CheckDialogVisuals(window);
                CheckRequestOverviewLayout();
                CheckStatisticsListSpace(controller);
                CheckCliOutputPresentation(controller, work);
                CheckReplyProgress(controller, store);
                CheckRunningNavigation(controller);
                CheckMarkdownReply();
                CheckMarkdownTables();
                CheckSidebarNavigation(controller, store, catalogFixture, work);

                var parser = new ClaudeStream();
                const string id = "5ac945e5-fd8c-4d64-942f-2670c376c154";
                parser.Read("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"" + id + "\",\"model\":\"test\"}");
                Check(parser.SessionId == id && parser.Model == "test", "Initialization event was not parsed.");
                parser.Read("{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compact_metadata\":{\"trigger\":\"auto\",\"pre_tokens\":80010,\"post_tokens\":521}}");
                Check(parser.Compaction == "auto" && parser.PreCompactTokens == 80010 && parser.PostCompactTokens == 521,
                    "The automatic compaction boundary was not parsed.");
                Check(parser.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"OK\"}}}") == "OK", "Text delta was not parsed.");
                Check(parser.Read("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"OK\"}]}}") == null, "Streamed text was duplicated.");
                parser.Read("{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"is_error\":true,\"permission_denials\":[{}]}");
                Check(parser.Finished && parser.Failure != null && parser.PermissionDenials == 1, "Error metadata was not parsed.");
                Console.WriteLine("PASS stream deltas, de-duplication, failure details");

                var rename = new SessionEdit(id, "测试会话", false);
                var fork = new SessionEdit(id, "测试会话", true);
                var operation = new ClaudeStream();
                operation.Read("{\"type\":\"result\",\"subtype\":\"success\",\"session_id\":\"" + id + "\"}");
                Check(operation.ValidateManualCompaction(id) != null, "An ordinary response was mistaken for a compaction.");
                operation.Read("{\"type\":\"system\",\"subtype\":\"compact_boundary\",\"compact_metadata\":{\"trigger\":\"manual\"}}");
                Check(operation.ValidateManualCompaction(id) == null && operation.ValidateManualCompaction(Guid.NewGuid().ToString()) != null,
                    "The manual compaction boundary was not validated against the resumed session.");
                operation.Read("{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":0,\"session_id\":\"" + id + "\",\"result\":\"Session renamed to: 测试会话\"}");
                Check(rename.Validate(operation) == null && rename.Input == "/rename 测试会话", "Valid local rename was rejected.");
                Check(fork.Validate(operation) != null && fork.Arguments().Contains("--fork-session"), "Fork accepted the original session ID.");
                Console.WriteLine("PASS session command, result validation, and row menu markup");

                var cli = CliInstallation.Find(null, AppDomain.CurrentDomain.BaseDirectory);
                Check(cli != null, "Local Claude CLI was not found.");
                var runner = new CliRunner();
                foreach (var action in CliAction.All.Where(item => item.Name == "版本" || item.Name == "健康诊断" ||
                    item.Name == "登录状态" || item.Name == "后台任务"))
                {
                    var output = new StringBuilder();
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    {
                        int code = runner.RunAsync(cli, work, action.Arguments, null, (line, error) => {
                            if (!error) output.AppendLine(line);
                        }, timeout.Token).GetAwaiter().GetResult();
                        string result = output.ToString();
                        Check((code == 0 || action.Name == "登录状态") && result.Length > 0, "CLI command failed: " + action.Name + " (" + code + ").");
                        if (action.Name == "后台任务")
                            Check(new System.Web.Script.Serialization.JavaScriptSerializer().DeserializeObject(result) is object[],
                                "The non-interactive background session command did not return a JSON array.");
                    }
                    Console.WriteLine("PASS claude " + String.Join(" ", action.Arguments));
                }

                if (args.Contains("--live"))
                {
                    Environment.SetEnvironmentVariable("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1");
                    var options = new RequestOptions { Permission = "default" };
                    var isolated = new[] {
                        "--safe-mode", "--strict-mcp-config", "--tools", "", "--setting-sources", "user",
                        "--system-prompt", "Follow the user instruction.", "--max-turns", "1"
                    };
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
                    {
                        var first = LiveRun(runner, cli, work, options, isolated,
                            "Keep the code word GLASS in mind. Reply with exactly OK.", "OK", timeout.Token);
                        Check(first.SessionId != null, "Claude CLI did not return a reusable session ID.");
                        EditSession(runner, cli, work, new SessionEdit(first.SessionId, "测试会话 原始", false), timeout.Token);
                        var branch = EditSession(runner, cli, work, new SessionEdit(first.SessionId, "测试会话 分支", true), timeout.Token);
                        options.SessionId = branch.SessionId;
                        LiveRun(runner, cli, work, options, isolated, "Reply with only the code word I asked you to remember.",
                            "GLASS", timeout.Token);
                    }
                    Console.WriteLine("PASS Claude CLI live reply, local rename, session fork, and resumed context");
                }
                app.Shutdown();
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.GetBaseException().Message);
                return 1;
            }
            finally
            {
                foreach (var file in Directory.EnumerateFiles(work)) File.Delete(file);
                if (!Directory.EnumerateFileSystemEntries(work).Any()) Directory.Delete(work);
            }
        }

        private static void CheckCliUpdate(string work)
        {
            Check(CliUpdate.ParseVersion("\u001b[32m2.1.318 (Claude Code)\u001b[0m\r\n").ToString() == "2.1.318",
                "CLI version output was not parsed.");
            Check(CliUpdate.ParseVersion("2.1.100").CompareTo(CliUpdate.ParseVersion("2.1.99")) > 0,
                "Versions were compared as text.");
            Check(CliUpdate.ParseLatest("{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"2.1.319\"}") == "2.1.319",
                "Registry version was not parsed.");
            foreach (string json in new[] { "{}", "{\"name\":\"other\",\"version\":\"2.1.319\"}",
                "{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"2.1.319;bad\"}",
                "{\"name\":\"@anthropic-ai/claude-code\",\"version\":\"2.1.319-beta\"}" })
            {
                bool rejected = false;
                try { CliUpdate.ParseLatest(json); } catch (InvalidDataException) { rejected = true; }
                Check(rejected, "Invalid registry data was accepted: " + json);
            }
            string prefix = Path.Combine(work, "npm prefix with spaces");
            string package = Path.Combine(prefix, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(package));
            string npm = Path.Combine(prefix, "node_modules", "npm", "bin", "npm-cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(npm));
            File.WriteAllText(npm, "fixture");
            File.WriteAllText(Path.Combine(prefix, "node.exe"), "fixture");
            var plan = CliUpdate.Plan(new CliInstallation(package), "2.1.319");
            Check(plan.Installation.Script == npm && plan.Arguments.Contains(prefix) &&
                plan.Arguments.Contains("@anthropic-ai/claude-code@2.1.319") &&
                plan.Command.Contains(CliRunner.QuoteArgument(prefix)), "npm update did not preserve the selected prefix and version.");
            string script = Path.Combine(Path.GetDirectoryName(package), "..", "cli.js");
            Check(CliUpdate.Plan(new CliInstallation(Path.Combine(prefix, "node.exe"), script), "2.1.319").Installation.Script == npm,
                "Legacy npm JavaScript installation was not recognized.");
            string native = Path.Combine(work, "native", "claude.exe");
            plan = CliUpdate.Plan(new CliInstallation(native), "2.1.319");
            Check(plan.Installation.Program == native && plan.Arguments.SequenceEqual(new[] { "update" }),
                "Native update did not use the selected CLI.");
            var fixture = new CliInstallation(System.Reflection.Assembly.GetExecutingAssembly().Location, "__version_fixture__");
            Check(CliUpdate.LocalAsync(new CliRunner(), fixture, work, CancellationToken.None).GetAwaiter().GetResult().ToString() == "2.1.318",
                "Version subprocess verification failed.");
            using (var cancel = new CancellationTokenSource())
            {
                cancel.Cancel();
                bool stopped = false;
                try { CliUpdate.LocalAsync(new CliRunner(), fixture, work, cancel.Token).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Cancelled version check continued.");
            }
            Console.WriteLine("PASS CLI updates: version comparison, registry validation, exact npm prefix, legacy npm/native routing, subprocess verification and cancellation");
        }

        private static void CheckRequestStatistics(DataStore store, string work)
        {
            var stream = new ClaudeStream();
            stream.Read("{\"type\":\"stream_event\",\"api_message_id\":\"one\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"one\",\"model\":\"fixture\",\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":80,\"cache_read_input_tokens\":18829,\"output_tokens\":1}}}}");
            stream.Read("{\"type\":\"stream_event\",\"api_message_id\":\"one\",\"event\":{\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":436}}}");
            stream.Read("{\"type\":\"assistant\",\"message\":{\"id\":\"one\",\"model\":\"fixture\",\"usage\":{\"input_tokens\":2,\"output_tokens\":1}}}");
            stream.Read("{\"type\":\"stream_event\",\"api_message_id\":\"one\",\"event\":{\"type\":\"message_stop\"}}");
            stream.Read("{\"type\":\"result\",\"subtype\":\"success\",\"num_turns\":1,\"total_cost_usd\":0.093,\"usage\":{\"input_tokens\":2,\"cache_creation_input_tokens\":80,\"cache_read_input_tokens\":18829,\"output_tokens\":436},\"stop_reason\":\"end_turn\"}");
            var route = GatewayRoute.Resolve(work, new Dictionary<string, string> {
                { "ANTHROPIC_BASE_URL", "https://name:secret@example.test/gateway?key=secret" }
            });
            var rows = stream.Statistics(DateTime.UtcNow, route, "session", "fixture", 0.08m, false, false);
            var row = rows.Single();
            Check(row.TotalInput == 18911 && row.Output == 436 && row.EndReason == "end_turn" && !row.Incomplete, "Stream usage was counted incorrectly.");
            Check(row.CliEstimate == 0.013m && row.Gateway == "https://example.test/gateway", "Cost baseline or gateway normalization failed.");
            var rate = new RequestRate { Model = row.Model, Currency = "CNY", Input = 1, CacheWrite = 2, CacheRead = 3, Output = 4 };
            Check(rate.Estimate(row) == (2 + 160 + 56487 + 1744) / 1000000m, "Configured rate calculation failed.");
            var failed = new ClaudeStream();
            var error = failed.Statistics(DateTime.UtcNow, route, "session", "fixture", null, true, false).Single();
            Check(error.ExecutionOnly && error.EndReason == "手动停止" && !error.TotalInput.HasValue, "Stop invented usage or lost its reason.");
            store.Statistics.Append(rows);
            Check(store.Statistics.Read().Single().CliEstimate == 0.013m, "Request statistics did not persist.");
            var billed = new ClaudeStream();
            AddBillingResponse(billed, "one", "{\"billing\":{\"amount\":0.00425,\"currency\":\"CNY\"}}", true);
            AddBillingResponse(billed, "two", "{\"usage\":{\"cost\":0,\"currency\":\"USD\"}}", true);
            AddBillingResponse(billed, "three", "{\"usage\":{\"cost\":0.2}}", true);
            AddBillingResponse(billed, "four", "{\"billing\":{\"amount\":0.33,\"currency\":\"CNY\"}}", false);
            AddBillingResponse(billed, "five", "{\"billing\":{\"amount\":0}}", true);
            var charges = billed.Statistics(DateTime.UtcNow, route, "session", "fixture", null, true, false);
            Check(charges.Count == 6 && charges[0].BilledText == "CNY 0.00425" && charges[0].BillingField == "billing.amount" &&
                charges[1].BilledText == "USD 0" && charges[2].BilledText == "0.2" && charges[2].BilledCurrency == null &&
                charges[3].BilledText == "CNY 0.33" && charges[3].Incomplete && charges[4].BilledText == "0" && charges[4].BilledCurrency == null &&
                charges[5].ExecutionOnly,
                "Actual billing was missing, misattributed, or incorrectly inferred.");
            store.Statistics.Append(charges);
            Check(store.Statistics.Read().Last(item => item.HasBilledAmount).BilledAmount == 0m, "Reported zero charge did not survive storage.");
            File.Delete(Path.Combine(work, "request-statistics.jsonl"));
            store.Statistics.Append(rows);
            Console.WriteLine("PASS request statistics, cache totals, resumed cost delta, gateway redaction, and persistence");
        }

        private static void CheckCancelledCosts(string work)
        {
            var stream = new ClaudeStream(); var started = DateTime.UtcNow.AddSeconds(-10);
            stream.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"completed-before-denial\",\"model\":\"cancel-model\",\"usage\":{\"input_tokens\":20}}}}", started);
            stream.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":10},\"delta\":{\"stop_reason\":\"tool_use\"}}}", started.AddSeconds(1));
            stream.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_stop\"}}", started.AddSeconds(2));
            var rows = stream.Statistics(started, null, "cancel-session", "cancel-model", null, true, false, "拒绝工具授权");
            var rate = new RequestRate { Model = "cancel-model", Currency = "CNY", Unit = 1000, Input = 2, Output = 3 };
            foreach (var row in rows) RequestPricing.Apply(row, new[] { rate });
            var completed = rows.Single(row => !row.ExecutionOnly); var cancelled = rows.Single(row => row.ExecutionOnly);
            Check(completed.EstimatedCost == .07m && completed.Status == "成功" && cancelled.Status == "已取消" && cancelled.EndReason == "拒绝工具授权",
                "Cancelling tool permission lost the completed call charge or changed statuses.");
            Check(cancelled.EstimateText.Contains("已完成部分") && cancelled.EstimateText.Contains("配置费用 CNY 0.07") && !cancelled.EstimateText.Contains("未计费") && !cancelled.EstimatedCost.HasValue,
                "The cancelled row does not display known completed fees or incorrectly duplicates a charge.");
            Check(cancelled.InputText == "20" && cancelled.OutputText == "10" && !cancelled.Input.HasValue && !cancelled.Output.HasValue && !cancelled.TotalInput.HasValue,
                "Cancelled events must show linked completed input/output without copying tokens into stored raw fields.");
            var directory = Path.Combine(work, "cancel-cost-storage"); var storage = new StatisticsStore(directory); storage.Append(rows);
            var restored = storage.Read(); var restoredCancel = restored.Single(row => row.ExecutionOnly);
            Check(restoredCancel.EstimateText == cancelled.EstimateText && !File.ReadAllText(Path.Combine(directory, "request-statistics.jsonl")).Contains("CompletedCostRows"),
                "Restart lost cancelled cost links or serialized recursive presentation references.");
            Check(restoredCancel.InputText == "20" && restoredCancel.OutputText == "10" && restoredCancel.UsageDetails.Contains("同轮已完成"),
                "Restart lost linked completed token display or provenance.");
            rate.Input = 900; RequestPricing.Apply(restored.First(), new[] { rate });
            Check(restoredCancel.EstimateText.Contains("CNY 0.07"), "Historical completed costs changed with today's rates.");
            Check(RequestCostPresentation.CostScope(restored).Sum(row => row.EstimatedCost ?? 0) == .07m &&
                RequestCostPresentation.CostScope(new[] { restoredCancel }).Sum(row => row.EstimatedCost ?? 0) == .07m,
                "Completed cancellation fees were double counted or lost when filtering to cancelled events.");
            var overview = new StackPanel(); new RequestOverviewLayout(overview).Show(new[] { restoredCancel });
            overview.Measure(new Size(850, Double.PositiveInfinity)); overview.Arrange(new Rect(0, 0, 850, overview.DesiredSize.Height)); overview.UpdateLayout();
            Check(String.Join("\n", VisualChildren<TextBlock>(overview).Select(label => label.Text)).Contains("配置费用 CNY 0.07"), "Cancelled-only overview hides the related completed fees.");
            var linkedSummary = String.Join("\n", VisualChildren<TextBlock>(overview).Select(label => label.Text));
            Check(linkedSummary.Contains("1 次") && linkedSummary.Contains("20") && linkedSummary.Contains("10"), "Cancelled-only totals do not include linked completed token usage.");
            var combinedOverview = new StackPanel(); new RequestOverviewLayout(combinedOverview).Show(restored);
            combinedOverview.Measure(new Size(850, Double.PositiveInfinity)); combinedOverview.Arrange(new Rect(0, 0, 850, combinedOverview.DesiredSize.Height)); combinedOverview.UpdateLayout();
            Check(String.Join("\n", VisualChildren<TextBlock>(combinedOverview).Select(label => label.Text)) == linkedSummary,
                "Including the source request and cancelled event double counted token or fee totals.");
            var detail = new StackPanel(); Theme.Apply(detail.Resources, true); new RequestDetailLayout(detail).Show(restoredCancel);
            Check(String.Join("\n", LogicalText(detail)).Contains("同轮已完成部分费用"), "Cancelled request details omit its completed fees.");
            Check(String.Join("\n", LogicalText(detail)).Contains("同轮已完成部分 TOKEN 与缓存"), "Cancelled details do not identify linked token usage.");
            var cached = new RequestStat { RunId = "cached-cancel", Input = 20, CacheWrite = 30, CacheRead = 40, Output = 0 };
            var cachedCancel = new RequestStat { RunId = "cached-cancel", ExecutionOnly = true, Status = "已取消" };
            RequestCostPresentation.Attach(new[] { cached, cachedCancel });
            Check(cachedCancel.InputText == "90" && cachedCancel.OutputText == "0", "Associated inputs lost caches or zero output.");
            var missing = new RequestStat { RunId = "cached-cancel", Input = null, Output = 5 };
            RequestCostPresentation.Attach(new[] { cached, missing, cachedCancel });
            Check(cachedCancel.InputText == "90*" && cachedCancel.OutputText == "5" && cachedCancel.UsageDetails.Contains("部分调用未返回"),
                "Partially reported completed usage was treated as a full total or erased known tokens.");
            var partialUsage = new RequestStat { RunId = "cached-cancel", Input = 7, Output = 2, Incomplete = true, Status = "已取消" };
            RequestCostPresentation.Attach(new[] { cached, partialUsage });
            Check(partialUsage.InputText == "7" && partialUsage.OutputText == "2" && partialUsage.CompletedInputTokens == 90,
                "Linked completed usage overwrote a partial request's own observed tokens.");
            var legacy = new RequestStat { RunId = "legacy-cancel", ExecutionOnly = true, Status = "已取消", PricingStatus = "请求不完整，未计费" };
            RequestCostPresentation.Attach(new[] { legacy });
            Check(legacy.InputText == "—" && legacy.OutputText == "—" && legacy.EstimateText == "请求未完成，费用未知" && !legacy.HasBilledAmount, "Unknown costs were described as free or billed without evidence.");
            legacy.RunEstimate = .03m;
            Check(!legacy.EstimateText.Contains("CLI 估算") && !legacy.CostDetails.Contains("CLI 估算") && legacy.RunEstimate == 0.03m && !legacy.HasBilledAmount, "CLI run estimate was hidden or promoted to a gateway bill.");
            var zero = new RequestStat { RunId = "zero-cancel", EstimatedCost = 0m, EstimateCurrency = "USD", PricingStatus = "已按配置计费" };
            var zeroCancel = new RequestStat { RunId = "zero-cancel", ExecutionOnly = true, Status = "已取消" };
            RequestCostPresentation.Attach(new[] { zero, zeroCancel });
            Check(zeroCancel.EstimateText.Contains("USD 0"), "A reported zero charge was lost.");
            var billed = new RequestStat { RunId = "mixed-cancel", BilledAmount = 1m, BilledCurrency = "CNY", EstimatedCost = .9m, EstimateCurrency = "CNY" };
            var estimate = new RequestStat { RunId = "mixed-cancel", EstimatedCost = .2m, EstimateCurrency = "USD", PricingStatus = "公开参考价" };
            var mixed = new RequestStat { RunId = "mixed-cancel", ExecutionOnly = true, Status = "已取消" };
            RequestCostPresentation.Attach(new[] { billed, estimate, mixed });
            Check(mixed.EstimateText.Contains("网关扣费 CNY 1") && mixed.EstimateText.Contains("参考估算 USD 0.2") && !mixed.EstimateText.Contains("1.2"),
                "Reported charges and reference estimates were mixed or currencies were converted.");
            RequestCostPresentation.Attach(new[] { mixed });
            Check(mixed.CompletedCostText.Length == 0, "Removing a source request retained stale linked fees.");
            var partial = new ClaudeStream();
            partial.Read("{\"type\":\"assistant\",\"message\":{\"id\":\"partial-with-bill\",\"model\":\"cancel-model\",\"billing\":{\"amount\":0.25,\"currency\":\"CNY\"},\"usage\":{\"input_tokens\":20,\"output_tokens\":2}}}");
            var interrupted = partial.Statistics(started, null, null, "cancel-model", null, true, false).Single();
            RequestPricing.Apply(interrupted, new[] { rate });
            Check(interrupted.Incomplete && interrupted.BilledAmount == .25m && interrupted.EstimateText.Contains("网关扣费 CNY 0.25") && !interrupted.EstimatedCost.HasValue,
                "Explicit gateway charges were discarded on an interrupted response, or partial local costs were invented.");
            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_CANCEL_COST_PREVIEW");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                detail.Measure(new Size(810, Double.PositiveInfinity)); detail.Arrange(new Rect(0, 0, 810, detail.DesiredSize.Height)); detail.UpdateLayout();
                var bitmap = new RenderTargetBitmap(810, (int)Math.Ceiling(detail.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(detail);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using (var file = File.Create(preview)) png.Save(file);
            }
            Console.WriteLine("PASS cancelled costs: completed fees remain visible, historical links, cancelled-only filters, no double counting, fee snapshots, zero/mixed currencies, explicit partial charges and unknown amounts");
        }

        private static void CheckStatisticsPagination(string work)
        {
            var store = new StatisticsStore(Path.Combine(work, "statistics-pagination"));
            var source = Enumerable.Range(0, 125).Select(index => new RequestStat { RequestId = "page-" + index.ToString("000"),
                Model = "page-model", Input = 1, Output = 2, EstimatedCost = 1m, EstimateCurrency = "CNY", PricingStatus = "已按配置计费",
                Status = "成功", SessionId = index % 2 == 0 ? "even-session" : "odd-session" }).ToList();
            store.Append(source);
            var window = (Window)System.Windows.Markup.XamlReader.Load(System.Xml.XmlReader.Create(typeof(Smoke).Assembly.GetManifestResourceStream("ClaudeUI.MainWindow.xaml")));
            try
            {
                var view = new RequestStatisticsView(window, store, new List<RequestRate>(), () => true, (count, all) => true);
                view.Show("even-session");
                var list = (ListView)window.FindName("StatsRows");
                var size = (ComboBox)window.FindName("StatsPageSize");
                var jump = (TextBox)window.FindName("StatsPageJump");
                var previous = (Button)window.FindName("StatsPreviousPage");
                var next = (Button)window.FindName("StatsNextPage");
                var position = (TextBlock)window.FindName("StatsPagePosition");
                Action<string> go = value => { jump.Text = value; ((Button)window.FindName("StatsJumpPage")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); };
                Check((int)size.SelectedItem == 20 && list.Items.Count == 20 && position.Text == "1 / 7" && !previous.IsEnabled && next.IsEnabled &&
                    ((RequestStat)list.Items[0]).RequestId == "page-124", "Default pagination lost page size, order or navigation boundaries.");
                string summary = OverviewText(window);
                Check(summary.Contains("125 次") && summary.Contains("配置费用 CNY 125"), "Pagination limited overall totals to one page.");
                list.SelectedIndex = 0;
                next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(position.Text == "2 / 7" && list.SelectedItems.Count == 0 && ((RequestStat)list.Items[0]).RequestId == "page-104" && OverviewText(window) == summary,
                    "Next page lost its slice, cleared totals or retained hidden selections.");
                previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(position.Text == "1 / 7", "Previous page did not return to page one.");
                go("7");
                Check(list.Items.Count == 5 && position.Text == "7 / 7" && !next.IsEnabled && previous.IsEnabled, "Last page did not show its remainder.");
                foreach (string invalid in new[] { "0", "8", "-1", "abc", "", "2147483648", "1.5" })
                {
                    go(invalid);
                    Check(position.Text == "7 / 7" && ((TextBlock)window.FindName("StatsPageError")).Visibility == Visibility.Visible,
                        "Invalid page changed the current page or had no feedback: " + invalid);
                }
                size.SelectedItem = 50;
                Check(list.Items.Count == 50 && position.Text == "1 / 3", "50-row mode did not reset to page one.");
                go("3"); Check(list.Items.Count == 25 && position.Text == "3 / 3", "50-row mode lost the last-page remainder.");
                size.SelectedItem = 100;
                Check(list.Items.Count == 100 && position.Text == "1 / 2", "100-row mode did not reset to page one.");
                var pageTestHandle = new System.Windows.Interop.WindowInteropHelper(window).EnsureHandle();
                jump.Text = "2";
                var enter = new KeyEventArgs(Keyboard.PrimaryDevice, System.Windows.Interop.HwndSource.FromHwnd(pageTestHandle), 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent };
                jump.RaiseEvent(enter);
                Check(enter.Handled && list.Items.Count == 25 && position.Text == "2 / 2", "Enter did not jump to the requested page.");
                var filter = (TextBox)window.FindName("StatsRequestFilter"); filter.Text = "page-124";
                Check(list.Items.Count == 1 && position.Text == "1 / 1" && !next.IsEnabled && !previous.IsEnabled, "Filtering did not reset and repaginate.");
                filter.Text = "no-such-request";
                Check(list.Items.Count == 0 && position.Text == "1 / 1" && !jump.IsEnabled && !((Button)window.FindName("StatsJumpPage")).IsEnabled,
                    "Empty results left invalid navigation enabled.");
                filter.Clear(); size.SelectedItem = 20;
                var current = (CheckBox)window.FindName("StatsCurrentOnly"); current.IsChecked = true; current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(position.Text == "1 / 4" && ((TextBlock)window.FindName("StatsPageSummary")).Text.Contains("共 63 条"), "Session filtering did not count all matching rows.");
                current.IsChecked = false; current.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); go("3");
                string preview = Environment.GetEnvironmentVariable("CLAUDEUI_PAGINATION_PREVIEW");
                if (!String.IsNullOrWhiteSpace(preview))
                {
                    Theme.Apply(window.Resources, true);
                    ((Grid)window.FindName("StatsPage")).Visibility = Visibility.Visible;
                    ((Grid)window.FindName("ChatPage")).Visibility = Visibility.Collapsed;
                    var root = (FrameworkElement)window.Content;
                    foreach (int width in new[] { 1280, 820 })
                    {
                        int height = width == 1280 ? 850 : 700;
                        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(preview + "-" + width + ".png")) encoder.Save(file);
                    }
                }
                go("7"); ((Button)window.FindName("StatsSelectAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.SelectedItems.Count == 5, "Select page selected records on other pages.");
                ((Button)window.FindName("StatsDeleteSelected")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(store.Read().Count == 120 && list.Items.Count == 20 && position.Text == "6 / 6", "Deleting the final page did not move back to the last valid page.");
                ((Button)window.FindName("StatsClearAll")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(store.Read().Count == 0 && list.Items.Count == 0 && position.Text == "1 / 1", "Clear-all did not clear records outside the current page.");
                view.Record(new[] { new RequestStat { Model = "new-model", RequestId = "after-clear" } });
                Check(list.Items.Count == 1 && position.Text == "1 / 1", "New records did not recover from empty pagination.");
            }
            finally { window.Close(); }
            Console.WriteLine("PASS statistics pagination: 20/50/100 rows, ordering, previous/next, jump/Enter, invalid input, empty/session/ID filters, complete totals, page-scoped selection, deletion bounds and new records");
        }

        private static void CheckStatisticsDeletion(string work)
        {
            string directory = Path.Combine(work, "statistics-delete");
            Directory.CreateDirectory(directory);
            string file = Path.Combine(directory, "request-statistics.jsonl");
            try
            {
                var store = new StatisticsStore(directory);
                var first = new RequestStat { RequestId = "same-id", RunId = "first-run", Model = "first", Input = 1, Output = 2 };
                var second = new RequestStat { RequestId = "same-id", RunId = "second-run", Model = "second", Input = 3, Output = 4 };
                var legacy = new RequestStat { Model = "legacy", Input = 5, Output = 6 };
                store.Append(new[] { first, second, legacy });
                Window window;
                using (var markup = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeUI.MainWindow.xaml"))
                    window = (Window)System.Windows.Markup.XamlReader.Load(markup);
                bool approved = false;
                int confirmedCount = 0;
                bool confirmedAll = false;
                var view = new RequestStatisticsView(window, store, new List<RequestRate>(), () => true,
                    (count, all) => { confirmedCount = count; confirmedAll = all; return approved; });
                view.Show(null);
                var list = (ListView)window.FindName("StatsRows");
                var delete = (Button)window.FindName("StatsDeleteSelected");
                var clear = (Button)window.FindName("StatsClearAll");
                var selectAll = (Button)window.FindName("StatsSelectAll");
                var deselectAll = (Button)window.FindName("StatsDeselectAll");
                Check(list.SelectedItems.Count == 0 && !delete.IsEnabled, "Rows were selected for deletion without user action.");
                ((Grid)window.FindName("StatsPage")).Visibility = Visibility.Visible;
                list.Measure(new Size(1800, 400)); list.Arrange(new Rect(0, 0, 1800, 400)); list.UpdateLayout();
                var checkboxes = VisualChildren<CheckBox>(list).Where(box => System.Windows.Automation.AutomationProperties.GetName(box) == "选择此请求").ToList();
                Check(checkboxes.Count == 3, "Request selection checkboxes were not rendered.");
                Action<CheckBox> toggle = box => typeof(System.Windows.Controls.Primitives.ToggleButton)
                    .GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(box, null);
                toggle(checkboxes[0]); toggle(checkboxes[1]);
                Check(list.SelectedItems.Count == 2 && ((TextBlock)window.FindName("StatsSelectionCount")).Text == "已选 2 条", "Checkbox selection did not accumulate multiple requests.");
                var selectedContainer = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(list.SelectedItems[0]);
                var clickSelected = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent };
                selectedContainer.RaiseEvent(clickSelected);
                Check(clickSelected.Handled && list.SelectedItems.Count == 1 && !selectedContainer.IsSelected, "Clicking a selected row did not toggle off only that row.");
                selectedContainer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Check(list.SelectedItems.Count == 2 && selectedContainer.IsSelected, "Clicking the row again did not restore selection.");
                var scroller = (ScrollViewer)list.Template.FindName("StatsScrollViewer", list);
                list.Measure(new Size(700, 200)); list.Arrange(new Rect(0, 0, 700, 200)); list.UpdateLayout();
                Check(scroller.ScrollableWidth > 0, "Statistics test did not create horizontal overflow.");
                var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                view.ScrollHorizontally(wheel, ModifierKeys.Shift); list.UpdateLayout();
                Check(wheel.Handled && scroller.HorizontalOffset > 0 && scroller.VerticalOffset == 0, "Shift + wheel did not scroll horizontally.");
                wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                view.ScrollHorizontally(wheel, ModifierKeys.Shift); list.UpdateLayout();
                Check(wheel.Handled && scroller.HorizontalOffset == 0, "Shift + wheel did not scroll back left.");
                wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
                view.ScrollHorizontally(wheel, ModifierKeys.None);
                Check(!wheel.Handled, "Normal wheel scrolling was intercepted.");
                list.Measure(new Size(1800, 400)); list.Arrange(new Rect(0, 0, 1800, 400)); list.UpdateLayout();
                var unselected = list.Items.Cast<RequestStat>().Single(row => !list.SelectedItems.Contains(row));
                var unselectedContainer = (ListViewItem)list.ItemContainerGenerator.ContainerFromItem(unselected);
                unselectedContainer.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                Check(list.SelectedItems.Count == 3, "Clicking another row replaced the existing selection instead of adding to it.");
                toggle(checkboxes[2]);
                Check(list.SelectedItems.Count == 2, "Explicit checkbox deselection affected other selected rows.");
                var idText = VisualChildren<TextBox>(selectedContainer).First();
                idText.Focus(); idText.SelectAll();
                Check(list.SelectionMode == SelectionMode.Multiple && list.SelectedItems.Count == 2, "Focusing request ID text discarded the multi-selection.");
                toggle(checkboxes[0]);
                Check(list.SelectedItems.Count == 1, "Unchecking one request cleared other selections.");
                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                view.Show(null);
                Check(list.SelectedItems.Count == 3, "Refreshing statistics discarded multiple selections.");
                deselectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.SelectedItems.Count == 0 && !delete.IsEnabled && !deselectAll.IsEnabled, "Deselect-all did not reset selection controls.");
                var selected = list.Items.Cast<RequestStat>().Single(row => row.RunId == "first-run");
                list.SelectedItem = selected;
                delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.Items.Count == 3 && store.Read().Count == 3, "Cancelling deletion changed statistics.");
                // A new disk record absent from the current view must survive selective deletion.
                store.Append(new[] { new RequestStat { RequestId = "external", Model = "external" } });
                approved = true;
                delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(confirmedCount == 1 && !confirmedAll && list.Items.Count == 2 &&
                    store.Read().Count == 3 && store.Read().Any(row => row.RunId == "second-run") && store.Read().Any(row => row.RequestId == "external"),
                    "Selective deletion removed unrelated request runs or newer persisted rows.");
                using (var locked = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    list.SelectedIndex = 0;
                    delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(list.Items.Count == 2 && ((TextBlock)window.FindName("StatsWarning")).Text.Contains("删除失败"),
                        "A failed disk deletion removed rows from the view.");
                }
                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(confirmedCount == 2 && list.Items.Count == 0 && store.Read().Single().RequestId == "external" && !delete.IsEnabled && !clear.IsEnabled,
                    "Multi-selection or legacy-row deletion did not persist.");
                view.Record(new[] { first, second });
                ((TextBox)window.FindName("StatsRequestFilter")).Text = "first-run";
                Check(list.Items.Count == 1, "Deletion fixture filtering failed.");
                selectAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(list.SelectedItems.Count == 1 && ((RequestStat)list.SelectedItem).RunId == "first-run", "Select-all included requests outside the filter.");
                clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(confirmedAll && confirmedCount == 2 && list.Items.Count == 0 && store.Read().Count == 0 &&
                    OverviewText(window).Contains("0 次"),
                    "Clear-all was restricted by filters or did not refresh persisted totals.");
                view.Record(new[] { first });
                Check(store.Read().Count == 1 && list.Items.Count == 1, "New request records failed after clearing statistics.");
                Console.WriteLine("PASS statistics deletion: checkbox multi-select, selected count, select/deselect all, refresh preservation, filter scope, confirmation, legacy records, unrelated records, disk failure, totals and new writes");
            }
            finally
            {
                foreach (string entry in Directory.GetFiles(directory)) File.Delete(entry);
                Directory.Delete(directory);
            }
        }

        private static void CheckProbeStatistics(string work)
        {
            var cli = CliInstallation.Find(System.Reflection.Assembly.GetExecutingAssembly().Location, work);
            IList<RequestStat> recorded = null;
            var runner = new CliRunner();
            var profile = new ModelProfile { ModelId = "__usage_fixture_success__" };
            string error = ModelProfileProbe.RunAsync(runner, cli, work, work, profile, CancellationToken.None,
                rows => recorded = rows).GetAwaiter().GetResult();
            Check(error == null && recorded.Count == 1 && recorded[0].Operation == "连接测试" && recorded[0].Status == "成功" && recorded[0].Input == 8 && recorded[0].Output == 2,
                "Connection probe usage did not enter the shared telemetry callback.");
            profile.ModelId = "__usage_fixture_multi__";
            error = ModelProfileProbe.RunAsync(runner, cli, work, work, profile, CancellationToken.None,
                rows => recorded = rows).GetAwaiter().GetResult();
            Check(error == null && recorded.Count == 2 && recorded.All(row => row.Status == "成功" && !row.ExecutionOnly) &&
                recorded.Sum(row => row.Output) == 5, "Successful multi-response probe was rejected or invented a CLI failure record.");
            foreach (string fixture in new[] { "__usage_fixture_response_exit_error__", "__usage_fixture_missing_result__" })
            {
                profile.ModelId = fixture;
                error = ModelProfileProbe.RunAsync(runner, cli, work, work, profile, CancellationToken.None,
                    rows => recorded = rows).GetAwaiter().GetResult();
                Check(error != null && error.Contains("网关已返回完整模型响应") && recorded.Any(row => row.ExecutionOnly),
                    "A complete response concealed CLI failure or missing completion.");
            }
            profile.ModelId = "__usage_fixture_empty_success__";
            error = ModelProfileProbe.RunAsync(runner, cli, work, work, profile, CancellationToken.None,
                rows => recorded = rows).GetAwaiter().GetResult();
            Check(error != null && recorded.Single().ExecutionOnly, "Empty CLI success was accepted without a model response.");
            profile.ModelId = "__usage_fixture_failure__";
            error = ModelProfileProbe.RunAsync(runner, cli, work, work, profile, CancellationToken.None,
                rows => recorded = rows).GetAwaiter().GetResult();
            Check(error.Contains("404") && recorded.Single().Status == "失败" && !recorded[0].Input.HasValue,
                "A failed connection probe lost its failure record or invented usage.");
            profile.ModelId = "__usage_fixture_cancel__";
            bool cancelled = false;
            using (var timeout = new CancellationTokenSource(300))
            {
                try { ModelProfileProbe.RunAsync(runner, cli, work, work, profile, timeout.Token,
                    rows => recorded = rows).GetAwaiter().GetResult(); }
                catch (OperationCanceledException) { cancelled = true; }
            }
            Check(cancelled && recorded.Single().Status == "已取消", "Cancellation bypassed connection probe telemetry.");
            Console.WriteLine("PASS connection probe child-process integration: successful token capture, HTTP failure, cancellation, shared recording callback");
        }

        private static RequestStat BillingRow(string time = "2026-11-20T01:00:00Z")
        {
            return new RequestStat { Model = "deepseek-fixture", Gateway = "https://gateway.test", StartedUtc = time,
                Input = 1000000, CacheRead = 1000000, CacheWrite = 0, Output = 1000000 };
        }

        private static void CheckBillingRules(string work)
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            const string alias = "deepseek-v4.1-flash-1017", canonical = "deepseek-v4-1-flash-261017";
            var namedProfile = new ModelProfile { Name = "我的 DeepSeek", ModelId = alias, BaseUrl = "https://gateway.test" };
            var namedRow = new RequestStat { Model = canonical, RequestedModel = alias, Gateway = namedProfile.BaseUrl };
            Check(RequestModelDisplay.Resolve(namedRow, new[] { namedProfile }) == namedProfile.Name && namedRow.Model == canonical,
                "Model list did not use the configured name or changed billing identity.");
            namedRow.RequestedModel = null;
            Check(RequestModelDisplay.Resolve(namedRow, new[] { namedProfile }) == namedProfile.Name,
                "Legacy gateway alias did not resolve to the configured name.");
            Check(RequestModelDisplay.Resolve(namedRow, new[] { namedProfile, new ModelProfile { Name = "Other", BaseUrl = namedProfile.BaseUrl } }) == canonical,
                "Ambiguous legacy model guessed a configuration name.");
            var native = new RequestStat { RequestedModelName = "本机默认", Model = "claude-sonnet-4-5" };
            Check(RequestModelDisplay.Resolve(native, new ModelProfile[0]) == "claude-sonnet-4-5",
                "Native default model did not display its actual CLI model.");
            Check(RequestModelDisplay.Resolve(native, new[] { new ModelProfile { Name = "自定义名称", ModelId = native.Model } }) == "claude-sonnet-4-5",
                "Native default request was mislabeled as a custom configuration.");
            native.Model = null;
            Check(RequestModelDisplay.Resolve(native, null) == "未记录模型", "Default request without a response lost its source label.");
            Check(RequestModelDisplay.Resolve(new RequestStat { Model = canonical }, null) == canonical,
                "Legacy default-model record did not fall back to its actual model.");
            Check(RequestModelDisplay.Resolve(new RequestStat { RequestedModel = alias }, null) == alias,
                "Legacy model fallback lost the requested ID.");
            namedRow.RequestedModelName = "发送时的模型名称";
            Check(RequestModelDisplay.Resolve(namedRow, new ModelProfile[0]) == namedRow.RequestedModelName,
                "Stored model name was lost after configuration removal.");
            namedRow.ModelDisplayName = namedRow.RequestedModelName;
            Check(new StatisticsFilter { ModelDisplayName = namedRow.RequestedModelName }.Apply(new[] { namedRow }).Count == 1 &&
                new StatisticsFilter { ModelDisplayName = canonical }.Apply(new[] { namedRow }).Count == 0,
                "Model filter used gateway model instead of displayed configuration name.");
            var namedStorage = new StatisticsStore(Path.Combine(work, "model-name-snapshot")); namedStorage.Append(new[] { namedRow });
            Check(namedStorage.Read().Single().RequestedModelName == namedRow.RequestedModelName, "Configured model name did not persist.");
            var aliasRate = new RequestRate { Model = alias, Currency = "CNY", Input = 0.8m, CacheRead = 0.016m, Output = 3.2m,
                PeakEnabled = true, PeakInput = 1.6m, PeakCacheRead = 0.032m, PeakOutput = 6.4m };
            var aliasStream = new ClaudeStream();
            aliasStream.Read("{\"type\":\"assistant\",\"message\":{\"id\":\"alias-request\",\"model\":\"" + canonical + "\",\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":14074,\"output_tokens\":44}}}", new DateTime(2026, 11, 20, 9, 42, 32, DateTimeKind.Utc));
            var aliasRow = aliasStream.Statistics(new DateTime(2026, 11, 20, 9, 42, 32, DateTimeKind.Utc), null, null, alias, null, false, false).Single();
            RequestPricing.Apply(aliasRow, new[] { aliasRate });
            Check(aliasRow.Model == canonical && aliasRow.RequestedModel == alias && aliasRow.EstimatedCost == 0.0228m &&
                aliasRow.PriceSource.Contains(alias) && aliasRow.PricingStatus == "已按配置计费", "Gateway model alias lost requested-model pricing or response identity.");
            Func<RequestStat> aliasFixture = () => new RequestStat { Model = canonical, RequestedModel = alias, Gateway = "https://gateway.test",
                StartedUtc = "2026-11-20T09:42:32Z", Input = 14074, Output = 44 };
            var responseRate = new RequestRate { Model = canonical, Currency = "USD", BillingMode = "Request", Unit = 1, PerRequest = 0.25m };
            var responseRow = aliasFixture(); RequestPricing.Apply(responseRow, new[] { aliasRate, responseRate });
            Check(responseRow.EstimatedCost == 0.25m && responseRow.EstimateCurrency == "USD", "Response model rule did not take priority over request alias.");
            responseRate.Enabled = false;
            var responseDisabled = aliasFixture(); RequestPricing.Apply(responseDisabled, new[] { aliasRate, responseRate });
            Check(responseDisabled.PricingStatus.Contains("停用") && !responseDisabled.EstimatedCost.HasValue, "Disabled response rule fell back to alias.");
            var aliasScoped = new RequestRate { Model = alias, Gateway = "https://gateway.test", Currency = "CNY", BillingMode = "Request", Unit = 1, PerRequest = 0.5m };
            var scopedAliasRow = aliasFixture(); RequestPricing.Apply(scopedAliasRow, new[] { aliasRate, aliasScoped });
            Check(scopedAliasRow.EstimatedCost == 0.5m, "Scoped request alias did not take priority.");
            aliasScoped.Gateway = "https://other.test";
            var wrongGateway = aliasFixture(); RequestPricing.Apply(wrongGateway, new[] { aliasScoped });
            Check(!wrongGateway.EstimatedCost.HasValue, "Request alias crossed gateway scope.");
            var aliasConflict = aliasFixture(); RequestPricing.Apply(aliasConflict, new[] { aliasRate, aliasRate });
            Check(aliasConflict.PricingStatus.Contains("冲突"), "Duplicate request alias rules were accepted.");
            var oldAliasRow = aliasFixture(); oldAliasRow.RequestedModel = null; RequestPricing.Apply(oldAliasRow, new[] { aliasRate });
            Check(!oldAliasRow.EstimatedCost.HasValue, "Legacy record guessed a request alias.");
            var aliasStorage = new StatisticsStore(Path.Combine(work, "alias-billing")); aliasStorage.Append(new[] { aliasRow });
            Check(aliasStorage.Read().Single().RequestedModel == alias && aliasStorage.Read().Single().EstimatedCost == 0.0228m, "Request model or alias billing snapshot did not persist.");
            var legacy = json.Deserialize<RequestRate>("{\"Model\":\"legacy\",\"Gateway\":\"old\",\"Currency\":\"CNY\",\"Input\":1,\"Output\":2}");
            Check(legacy.Enabled && legacy.Unit == 1000000 && legacy.BillingMode == "Token" && legacy.Validate() == null, "Old rate defaults were not migrated.");
            var rate = new RequestRate { Model = "deepseek-fixture", Currency = "CNY", Input = 1, CacheRead = 0.02m, Output = 4,
                PeakEnabled = true, PeakInput = 2, PeakCacheRead = 0.04m, PeakOutput = 8 };
            Check(rate.Validate() == null, "Valid rule rejected.");
            var row = BillingRow();
            RequestPricing.Apply(row, new[] { rate });
            Check(row.EstimatedCost == 10.04m && row.EstimateCurrency == "CNY" && row.PriceSource.Contains("高峰") && row.PriceSource.Contains("2"), "Peak cache/input/output pricing failed.");
            var boundaries = new Dictionary<string, bool> {
                { "2026-11-20T00:59:59Z", false }, { "2026-11-20T01:00:00Z", true },
                { "2026-11-20T03:59:59Z", true }, { "2026-11-20T04:00:00Z", false },
                { "2026-11-20T05:59:59Z", false }, { "2026-11-20T06:00:00Z", true },
                { "2026-11-20T09:59:59Z", true }, { "2026-11-20T10:00:00Z", false },
                { "2026-11-21T01:00:00Z", false }, { "2026-11-22T01:00:00Z", false },
                { "2026-11-20T09:00:00+08:00", true }
            };
            foreach (var pair in boundaries)
            {
                var boundary = BillingRow(pair.Key); RequestPricing.Apply(boundary, new[] { rate });
                Check(boundary.EstimatedCost == (pair.Value ? 10.04m : 5.02m), "Beijing peak boundary failed at " + pair.Key);
            }
            rate.Holidays = "2026-11-20,2026-10-01";
            var holiday = BillingRow(); RequestPricing.Apply(holiday, new[] { rate });
            Check(holiday.EstimatedCost == 5.02m, "Holiday did not use idle rate.");
            var noTime = BillingRow(null); RequestPricing.Apply(noTime, new[] { rate });
            Check(!noTime.EstimatedCost.HasValue && noTime.PricingStatus.Contains("时间"), "Missing time invented a peak tier.");
            rate.PeakEnabled = false; rate.Input = 100; rate.Currency = "USD";
            RequestPricing.Apply(row, new[] { rate });
            Check(row.EstimatedCost == 10.04m && row.EstimateCurrency == "CNY", "Rule edits changed a recorded snapshot.");
            rate.Input = 1; rate.Unit = 1000;
            var thousand = BillingRow(); RequestPricing.Apply(thousand, new[] { rate });
            Check(thousand.EstimatedCost == 5020m && thousand.EstimateCurrency == "USD", "Unit or raw USD amount was converted.");
            rate.Unit = 1;
            var single = BillingRow(); RequestPricing.Apply(single, new[] { rate });
            Check(single.EstimatedCost == 5020000m, "Per-token unit failed.");
            rate.BillingMode = "Request"; rate.PerRequest = 0.123m;
            var call = BillingRow(); call.Input = null; call.Output = null;
            RequestPricing.Apply(call, new[] { rate });
            Check(call.EstimatedCost == 0.123m && call.EstimatedOutputCost == 0 && call.EstimateText == "USD 0.123", "Per-request pricing required tokens or changed currency.");
            rate.PerRequest = 0;
            var free = BillingRow(); RequestPricing.Apply(free, new[] { rate });
            Check(free.EstimatedCost == 0, "Free rule was treated as missing.");
            rate.Enabled = false; rate.Model = "claude-sonnet-4-5";
            var disabled = BillingRow(); disabled.Model = rate.Model;
            RequestPricing.Apply(disabled, new[] { rate });
            Check(!disabled.EstimatedCost.HasValue && disabled.PricingStatus.Contains("停用"), "Disabled rule fell back to USD public price.");
            rate.Model = "deepseek-fixture"; rate.Enabled = true;
            var specific = new RequestRate { Model = rate.Model, Gateway = "https://gateway.test", Currency = "CNY", Input = 2, Output = 8 };
            var scoped = BillingRow(); RequestPricing.Apply(scoped, new[] { rate, specific });
            Check(scoped.EstimatedCost == 10m && scoped.EstimateCurrency == "CNY", "Gateway rule did not take priority over model-wide rule.");
            var otherGateway = BillingRow(); otherGateway.Gateway = "other"; RequestPricing.Apply(otherGateway, new[] { rate, specific });
            Check(otherGateway.EstimatedCost == 0 && otherGateway.EstimateCurrency == "USD", "Model-wide rule did not extend to a new gateway.");
            specific.Enabled = false;
            var blocked = BillingRow(); RequestPricing.Apply(blocked, new[] { rate, specific });
            Check(!blocked.EstimatedCost.HasValue, "Disabled scoped rule used a global rule.");
            var conflict = BillingRow(); RequestPricing.Apply(conflict, new[] { rate, rate });
            Check(!conflict.EstimatedCost.HasValue && conflict.PricingStatus.Contains("冲突"), "Duplicate rules silently picked a price.");
            rate.Currency = "EUR";
            var invalid = BillingRow(); RequestPricing.Apply(invalid, new[] { rate });
            Check(!invalid.EstimatedCost.HasValue && invalid.PricingStatus.Contains("无效"), "Invalid stored currency was accepted.");
            rate.Currency = "USD"; rate.Holidays = "2026-02-30";
            Check(rate.Validate() != null, "Invalid calendar date accepted."); rate.Holidays = "";
            rate.Input = -1; Check(rate.Validate() != null, "Negative price accepted."); rate.Input = 0;
            rate.Unit = 0; Check(rate.Validate() != null, "Zero divisor accepted."); rate.Unit = 1;
            var partial = BillingRow(); partial.Incomplete = true; RequestPricing.Apply(partial, new[] { rate });
            Check(!partial.EstimatedCost.HasValue, "Partial request was priced per call.");
            var rates = new List<RequestRate>();
            Check(RateConfiguration.Save(rates, null, rate, () => false) != null && rates.Count == 0, "Failed create changed active rules.");
            Check(RateConfiguration.Save(rates, null, rate, () => true) == null, "Create failed.");
            var changed = new RequestRate { Model = rate.Model, Currency = "CNY" };
            Check(RateConfiguration.Save(rates, rate, changed, () => { throw new IOException(); }) != null && rates.Single() == rate, "Failed edit did not roll back.");
            Check(RateConfiguration.Save(rates, null, changed, () => true) != null && rates.Count == 1, "Duplicate configuration accepted.");
            Check(RateConfiguration.Save(rates, rate, changed, () => true) == null && rates.Single() == changed, "Editing existing rule failed.");
            var persistence = new DataStore(Path.Combine(work, "billing-config"));
            persistence.Save(new AppData { RequestRates = rates });
            Check(persistence.Read().RequestRates.Single().Unit == 1000000 && persistence.Read().RequestRates.Single().Currency == "CNY", "Rule configuration did not persist.");
            var storage = new StatisticsStore(Path.Combine(work, "billing-records")); storage.Append(new[] { row, noTime, disabled });
            Check(storage.Read()[0].EstimatedCost == 10.04m && storage.Read()[0].PriceSource == row.PriceSource && storage.Read()[2].PricingStatus == disabled.PricingStatus, "Billing snapshot/status did not survive reload.");
            Console.WriteLine("PASS billing: legacy defaults, model/gateway priority, disabled/conflicting/invalid rules, token units, per-call/free prices, Beijing peak boundaries, holidays, snapshots, save rollback and persistence");
        }

        private static IEnumerable<DependencyObject> BillingControls(DependencyObject parent)
        {
            yield return parent;
            foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
                foreach (var item in BillingControls(child)) yield return item;
        }

        private static T BillingControl<T>(Window window, string name) where T : DependencyObject
        {
            return BillingControls(window).OfType<T>().Single(item => System.Windows.Automation.AutomationProperties.GetName(item) == name);
        }

        private static void CheckBillingDialog(Window owner, string work)
        {
            var rates = new List<RequestRate>(); int saves = 0;
            var dialog = new RequestRatesDialog(owner, rates, () => { saves++; return true; }, null,
                new[] { "deepseek-v4.1-flash-1017", "new-model", "new-model", "", null });
            var view = dialog.View;
            new System.Windows.Interop.WindowInteropHelper(view).EnsureHandle();
            var modelChoice = BillingControl<ComboBox>(view, "模型标识");
            Check(modelChoice.IsEditable && modelChoice.Items.Count == 2 && modelChoice.Items.Contains("deepseek-v4.1-flash-1017"),
                "Billing model dropdown lost configured models or duplicate filtering.");
            modelChoice.SelectedItem = "new-model";
            foreach (string label in new[] { "未缓存输入单价", "缓存写入单价", "缓存命中输入单价", "输出单价" }) BillingControl<TextBox>(view, label).Text = "1";
            BillingControl<ComboBox>(view, "结算币种（原币展示，分别汇总，不做换算）").SelectedItem = "CNY";
            var save = BillingControls(view).OfType<Button>().Single(button => (string)button.Content == "保存规则");
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(saves == 1 && rates.Single().Model == "new-model" && rates.Single().Currency == "CNY", "Dialog could not add a model without prior requests.");
            BillingControl<CheckBox>(view, "启用状态").IsChecked = false;
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(saves == 2 && rates.Count == 1 && !rates[0].Enabled, "Dialog disable/edit did not update existing rule.");
            BillingControl<CheckBox>(view, "启用状态").IsChecked = true;
            BillingControl<ComboBox>(view, "计费方式").SelectedIndex = 1;
            BillingControl<TextBox>(view, "每次请求单价").Text = "0.25";
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(saves == 3 && rates[0].Enabled && rates[0].BillingMode == "Request" && rates[0].Unit == 1 && rates[0].PerRequest == 0.25m, "Dialog mode change or enable failed.");
            BillingControl<ComboBox>(view, "计费方式").SelectedIndex = 0;
            BillingControl<ComboBox>(view, "计费单位").SelectedIndex = 2;
            string[] normalLabels = { "未缓存输入单价", "缓存写入单价", "缓存命中输入单价", "输出单价" };
            string[] peakLabels = { "高峰未缓存输入单价", "高峰缓存写入单价", "高峰缓存命中输入单价", "高峰输出单价" };
            decimal[] low = { 1m, 0m, 0.02m, 4m }, high = { 2m, 0m, 0.04m, 8m };
            for (int i = 0; i < 4; i++)
            {
                BillingControl<TextBox>(view, normalLabels[i]).Text = low[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
                BillingControl<TextBox>(view, peakLabels[i]).Text = high[i].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            var peakSwitch = BillingControl<CheckBox>(view, "峰谷价格（可选）");
            peakSwitch.IsChecked = true; peakSwitch.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
            BillingControl<TextBox>(view, "排除的节假日日期（yyyy-MM-dd，逗号或换行分隔）").Text = "2026-10-01";
            save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(saves == 4 && rates[0].PeakEnabled && rates[0].CacheRead == 0.02m && rates[0].PeakOutput == 8m, "Peak form did not persist configured prices.");
            string preview = Environment.GetEnvironmentVariable("CLAUDE_UI_BILLING_PREVIEW");
            if (!String.IsNullOrEmpty(preview))
            {
                foreach (bool dark in new[] { false, true })
                {
                    Theme.Apply(owner.Resources, dark);
                    var content = (FrameworkElement)view.Content;
                    content.Measure(new Size(810, 690)); content.Arrange(new Rect(0, 0, 810, 690)); content.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(810, 690, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                }
            }
            Theme.Apply(owner.Resources, false); view.Close();
            CheckBillingRuleDeletion(owner, work, rates[0]);
            var statsWindow = (Window)System.Windows.Markup.XamlReader.Load(System.Xml.XmlReader.Create(typeof(Smoke).Assembly.GetManifestResourceStream("ClaudeUI.MainWindow.xaml")));
            var store = new StatisticsStore(Path.Combine(work, "billing-view"));
            var dashboard = new RequestStatisticsView(statsWindow, store, rates, () => true);
            dashboard.Show(null);
            Check(statsWindow.FindName("StatsRatesButton") == null, "Billing configuration entry remained on the statistics page.");
            rates[0] = new RequestRate { Model = "new-model", Currency = "CNY", BillingMode = "Request", Unit = 1, PerRequest = 0.25m };
            var cny = new RequestStat { Model = "new-model", Input = 1, Output = 1 };
            var usd = new RequestStat { Model = "future-model", Input = 1000000, Output = 0 };
            var allRates = new[] { rates[0], new RequestRate { Model = "future-model", Currency = "USD", Input = 2 } };
            dashboard.Record(new[] { cny, usd }, allRates);
            string summary = OverviewText(statsWindow);
            Check(summary.Contains("CNY 0.25") && summary.Contains("USD 2"), "Dashboard converted or combined currencies.");
            rates[0] = new RequestRate { Model = "new-model", Currency = "CNY", BillingMode = "Request", Unit = 1, PerRequest = 9 };
            var inFlight = new RequestStat { Model = "new-model" };
            dashboard.Record(new[] { inFlight }, allRates);
            var nextRun = new RequestStat { Model = "new-model" };
            dashboard.Record(new[] { nextRun });
            Check(inFlight.EstimatedCost == 0.25m && nextRun.EstimatedCost == 9m, "Rule edit changed in-flight pricing or did not apply to next execution.");
            ((ListView)statsWindow.FindName("StatsRows")).SelectedItem = cny;
            string detail = ((TextBlock)statsWindow.FindName("StatsDetail")).Text;
            Check(detail.Contains("CNY 0.25") && !detail.Contains("CNY 9"), "Detail recomputed historical cost from latest rule.");
            statsWindow.Close();
            Console.WriteLine("PASS billing UI: empty-state create, edit, enable/disable, mode/unit changes, original-currency totals, historical detail");
        }

        private static void CheckBillingRuleDeletion(Window owner, string work, RequestRate original)
        {
            var rates = new List<RequestRate> { original, new RequestRate { Model = "another-model", Currency = "CNY", Input = 5 } };
            var disk = new DataStore(Path.Combine(work, "billing-delete"));
            disk.Save(new AppData { RequestRates = rates });
            bool approved = false, fail = false; int writes = 0;
            Func<bool> save = () => { writes++; if (fail) throw new IOException("fixture"); disk.Save(new AppData { RequestRates = rates }); return true; };
            var dialog = new RequestRatesDialog(owner, rates, save, null, null, rate => approved);
            var view = dialog.View;
            var list = BillingControl<ListBox>(view, "计费规则列表");
            var remove = BillingControl<Button>(view, "删除计费规则");
            var state = BillingControl<TextBlock>(view, "计费规则编辑状态");
            Check(list.SelectedItem == original && remove.IsEnabled && state.Text == "编辑计费规则",
                "Reopening billing settings did not open an existing rule for editing.");
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rates.Count == 2 && writes == 0, "Cancelled deletion changed billing settings.");
            approved = true; fail = true;
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rates.Count == 2 && rates[0] == original && disk.Read().RequestRates.Count == 2 && list.SelectedItem == original,
                "Failed deletion did not preserve the original rule and selection.");
            fail = false;
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rates.Count == 1 && disk.Read().RequestRates.Single().Model == "another-model" && list.SelectedItem == rates[0] && remove.IsEnabled,
                "Deleting one rule changed other rules or failed to persist.");
            var snapshot = new RequestStat { Model = "another-model", Input = 1000000, Output = 0 };
            RequestPricing.Apply(snapshot, rates); decimal? historical = snapshot.EstimatedCost;
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(rates.Count == 0 && disk.Read().RequestRates.Count == 0 && !remove.IsEnabled && state.Text == "新增计费规则" &&
                historical == 5m && snapshot.EstimatedCost == historical, "Deleting the last rule lost history or did not reset the form.");
            Check(RateConfiguration.Delete(rates, original, () => { throw new Exception("must not persist"); }) != null,
                "Missing rule deletion was accepted.");
            rates.Add(original);
            Check(RateConfiguration.Delete(rates, original, () => false) != null && rates.Single() == original,
                "False persistence result lost the original rule.");
            view.Close();
            Console.WriteLine("PASS billing rule deletion: reopen/edit, cancelled confirmation, save failure rollback, original ordering, selected-rule-only deletion, disk reload, empty form and historical snapshots");
        }

        private static void CheckUsageDashboard(string work)
        {
            var started = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
            var stream = new ClaudeStream();
            stream.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"request-A\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":100,\"cache_creation_input_tokens\":20,\"cache_read_input_tokens\":30}}}}", started);
            stream.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":10},\"delta\":{\"stop_reason\":\"end_turn\"}}}", started.AddSeconds(2));
            stream.Read("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_stop\"}}", started.AddSeconds(3));
            stream.Read("{\"type\":\"assistant\",\"message\":{\"id\":\"request-A\",\"model\":\"claude-sonnet-4-5\",\"stop_reason\":\"end_turn\",\"usage\":{\"output_tokens\":1}}}", started.AddSeconds(4));
            var rows = stream.Statistics(started.AddSeconds(-10), null, "fixture-session", "fallback", null, false, false);
            var row = rows.Single();
            Check(row.RequestId == "request-A" && row.DurationMs == 3000 && row.Status == "成功" && row.RunId != null && row.Output == 10,
                "Request identity, event timestamps, completion status or streamed deduplication failed.");
            var snapshot = new ClaudeStream();
            snapshot.Read("{\"type\":\"assistant\",\"message\":{\"id\":\"snapshot\",\"model\":\"claude-sonnet-4-5\",\"usage\":{\"input_tokens\":8,\"output_tokens\":1}}}", started);
            snapshot.Read("{\"type\":\"assistant\",\"message\":{\"id\":\"snapshot\",\"model\":\"claude-sonnet-4-5\",\"stop_reason\":\"end_turn\",\"usage\":{\"input_tokens\":8,\"output_tokens\":12}}}", started.AddSeconds(2));
            var snapshotRow = snapshot.Statistics(started, null, null, null, null, false, false).Single();
            Check(snapshotRow.Output == 12 && !snapshotRow.DurationMs.HasValue && snapshotRow.Status == "成功",
                "Non-streaming snapshots lost final token counts or invented request latency.");
            var empty = new List<RequestRate>();
            RequestPricing.Apply(row, empty);
            Check(row.EstimatedCost == 0.000534m && row.EstimateCurrency == "USD" && row.PriceSource.Contains("LiteLLM") && !row.HasBilledAmount,
                "Pinned public pricing or separation from billed amounts failed.");
            var configured = new RequestRate { Gateway = row.Gateway, Model = row.Model, Currency = "CNY", Input = 1, CacheWrite = 2, CacheRead = 3, Output = 4 };
            RequestPricing.Apply(row, new[] { configured });
            Check(row.EstimatedCost == 0.000534m, "Historical estimates changed after rate edits.");
            var custom = new RequestStat { Model = row.Model, Gateway = row.Gateway, Input = 100, CacheWrite = 20, CacheRead = 30, Output = 10 };
            RequestPricing.Apply(custom, new[] { configured });
            Check(custom.EstimatedCost == 0.000270m && custom.EstimateCurrency == "CNY", "Custom gateway rate did not take precedence.");
            var unknown = new RequestStat { Model = "alias-not-in-catalog", Input = 100, Output = 10 };
            RequestPricing.Apply(unknown, empty);
            Check(!unknown.EstimatedCost.HasValue, "An unknown model was assigned an invented price.");
            var incomplete = new RequestStat { Model = row.Model, Input = 100, Output = 10, Incomplete = true };
            RequestPricing.Apply(incomplete, empty);
            Check(!incomplete.EstimatedCost.HasValue, "Partial usage was priced as a complete request.");
            var day = started.ToLocalTime().Date;
            Check(new StatisticsFilter { Request = "request-A", Model = row.Model, FromLocal = day, ToLocal = day }.Apply(rows).Count == 1,
                "Combined ID, model and local-day filtering failed.");
            Check(new StatisticsFilter { Model = "other" }.Apply(rows).Count == 0 &&
                new StatisticsFilter { FromLocal = day.AddDays(1) }.Apply(rows).Count == 0 &&
                new StatisticsFilter { FromLocal = day.AddDays(1), ToLocal = day }.Apply(rows).Count == 0 &&
                new StatisticsFilter { Request = row.RunId, Status = "成功", Session = "fixture-session" }.Apply(rows).Count == 1,
                "Filter boundaries or run correlation failed.");
            var storage = new StatisticsStore(work);
            storage.Append(rows);
            File.AppendAllText(Path.Combine(work, "request-statistics.jsonl"), "\ninvalid truncated line\n");
            var restored = storage.Read().Last();
            Check(restored.RequestId == row.RequestId && restored.DurationMs == 3000 && restored.EstimatedCost == row.EstimatedCost,
                "New schema did not round-trip or tolerate a damaged trailing line.");
            // Leave the original fixture intact for the window navigation checks.
            var original = storage.Read().First();
            File.Delete(Path.Combine(work, "request-statistics.jsonl"));
            storage.Append(new[] { original });
            Console.WriteLine("PASS request dashboard: correlated IDs, exact event timing, deduplication, pinned pricing, overrides, unknown/partial usage, filters, schema compatibility");
        }

        private static void AddBillingResponse(ClaudeStream stream, string id, string data, bool complete)
        {
            const string start = "{\"type\":\"message_start\",\"message\":{\"id\":\"msg\",\"model\":\"fixture\",\"billing\":{\"amount\":99,\"currency\":\"CNY\"}}}";
            string envelope = "{\"type\":\"stream_event\",\"api_message_id\":\"" + id + "\",\"event\":";
            stream.Read(envelope + start + "}");
            stream.Read(envelope + data.Insert(1, "\"type\":\"message_delta\",") + "}");
            if (complete) stream.Read(envelope + "{\"type\":\"message_stop\"}}");
        }

        private static void CheckHistorySurface(ListBox history)
        {
            try
            {
                foreach (bool enabled in new[] { true, false })
                {
                    history.IsEnabled = enabled;
                    history.Measure(new Size(200, 180));
                    history.Arrange(new Rect(0, 0, 200, 180));
                    history.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(200, 180, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(history);
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(100, 100, 1, 1), pixel, 4, 0);
                    Check(pixel[3] == 0, "The history list paints over its sidebar while IsEnabled=" + enabled + ".");
                }
            }
            finally { history.IsEnabled = true; }
        }

        private static void CheckComposerSendState(MainWindow controller)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var kind = typeof(MainWindow); var window = controller.View;
            var update = kind.GetMethod("UpdateButtons", flags);
            var cancellationField = kind.GetField("cancellation", flags);
            var send = (Button)window.FindName("SendButton");
            var model = (Button)window.FindName("ModelsButton");
            object modelIcon = model.Content;
            var arrow = (System.Windows.Shapes.Path)window.FindName("SendArrow");
            var stop = (System.Windows.Shapes.Rectangle)window.FindName("SendStopIcon");
            Check(window.FindName("StopButton") == null, "Composer still has an extra stop button beside model settings.");
            update.Invoke(controller, null);
            Check(arrow.Visibility == Visibility.Visible && stop.Visibility == Visibility.Collapsed, "Idle send icon was not restored.");
            using (var token = new CancellationTokenSource())
            {
                cancellationField.SetValue(controller, token);
                try
                {
                    update.Invoke(controller, null);
                    Check(send.IsEnabled && arrow.Visibility == Visibility.Collapsed && stop.Visibility == Visibility.Visible &&
                        ((string)send.ToolTip).Contains("运行中") && ReferenceEquals(model.Content, modelIcon) && !model.IsEnabled,
                        "Running request did not replace the send action with stop while preserving the model icon.");
                    send.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Check(token.IsCancellationRequested && ((string)send.ToolTip).Contains("正在停止"), "Clicking the running send button did not cancel the request.");
                }
                finally { cancellationField.SetValue(controller, null); update.Invoke(controller, null); }
            }
            Check(arrow.Visibility == Visibility.Visible && stop.Visibility == Visibility.Collapsed && (string)send.ToolTip == "发送消息（Enter）" && model.IsEnabled,
                "Completing the request did not restore send and model controls.");
            Console.WriteLine("PASS composer send action: idle arrow, in-place running stop icon, unchanged model icon, click cancellation, stopping feedback and restored controls");
        }

        private static void CheckDialogVisuals(Window owner)
        {
            object originalTextStyle = owner.TryFindResource(typeof(TextBox));
            foreach (bool dark in new[] { true, false })
            {
                Theme.Apply(owner.Resources, dark);
                MessageBoxResult result = MessageBoxResult.None;
                var dialog = DialogVisuals.CreateMessage(owner, "删除选中的 3 条请求统计？\n此操作不可撤销，只删除统计记录。", "删除请求统计",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No, value => result = value);
                dialog.ApplyTemplate();
                var surface = (FrameworkElement)VisualTreeHelper.GetChild(dialog, 0);
                surface.Measure(new Size(500, Double.PositiveInfinity));
                surface.Arrange(new Rect(0, 0, 500, surface.DesiredSize.Height)); surface.UpdateLayout();
                Check(dialog.WindowStyle == WindowStyle.None && dialog.Template.FindName("DialogClose", dialog) is Button,
                    "Themed dialog is missing its title bar or close button.");
                Check(ReferenceEquals(originalTextStyle, owner.TryFindResource(typeof(TextBox))), "Dialog styles changed the main window resources.");
                var buttons = VisualChildren<Button>((FrameworkElement)dialog.Content).ToList();
                Check(buttons.Count == 2 && buttons.Single(button => button.IsDefault).IsCancel, "Destructive confirmation lost its safe default.");
                string preview = Environment.GetEnvironmentVariable("CLAUDEUI_DIALOG_PREVIEW");
                if (!String.IsNullOrWhiteSpace(preview))
                {
                    var bitmap = new RenderTargetBitmap(500, (int)Math.Ceiling(surface.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                }
                dialog.Close();
                Check(result == MessageBoxResult.No, "Closing the confirmation approved a destructive action.");
                result = MessageBoxResult.None;
                dialog = DialogVisuals.CreateMessage(owner, "确认消息", "提示", MessageBoxButton.YesNo, MessageBoxImage.None, MessageBoxResult.No, value => result = value);
                VisualChildren<Button>((FrameworkElement)dialog.Content).Single(button => (string)button.Content == "确认").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(result == MessageBoxResult.Yes, "Explicit confirmation did not return Yes.");
            }
            Console.WriteLine("PASS themed dialogs: both themes, custom chrome, isolated input styles, safe default/close and explicit confirmation");
        }

        private static void CheckRequestDetailLayout()
        {
            var host = new StackPanel();
            var layout = new RequestDetailLayout(host);
            var row = new RequestStat { Model = "example-model", Status = "成功", Input = 255, CacheWrite = 0, CacheRead = 13184, Output = 68,
                RequestId = Guid.NewGuid().ToString(), RunId = Guid.NewGuid().ToString(), SessionId = Guid.NewGuid().ToString(),
                DurationMs = 1847, RunDurationMs = 15087, CallsInRun = 2, RunEstimate = .027556m,
                CompletedUtc = "2026-11-20T05:09:49.631019Z", TimingSource = "首个响应事件至最终事件（不含首字等待）",
                Gateway = "https://gateway.example.test/anthropic", GatewaySource = "按本机 CLI 配置推断，CLI 输出未报告实际 URL", EndReason = "tool_use" };
            layout.Show(row);
            host.Resources[typeof(TextBox)] = new Style(typeof(TextBox));
            Theme.Apply(host.Resources, true);
            host.Measure(new Size(850, Double.PositiveInfinity));
            host.Arrange(new Rect(0, 0, 850, host.DesiredSize.Height)); host.UpdateLayout();
            string text = String.Join("\n", VisualChildren<TextBlock>(host).Select(label => label.Text));
            Check(!text.Contains("网关实际扣费") && !text.Contains("CLI 估算") && !text.Contains("USD 0.027556") && !text.Contains("缓存命中占比") && text.Contains("1.847 s") && text.Contains("15.087 s"),
                "Grouped request details confused missing billing, run-level estimates or timing scope.");
            var fields = VisualChildren<TextBox>(host).ToList();
            Check(fields.All(field => field.IsReadOnly && field.TextWrapping == TextWrapping.Wrap) &&
                fields.Any(field => field.Text == row.RunId) && fields.Any(field => field.Text == row.SessionId) &&
                fields.Any(field => field.Text == row.GatewaySource), "Request identifiers or provenance were truncated, lost or no longer selectable.");
            row.BilledAmount = 0; row.BilledCurrency = "CNY"; row.BillingField = "gateway_cost";
            layout.Show(row); host.UpdateLayout();
            text = String.Join("\n", VisualChildren<TextBlock>(host).Select(label => label.Text));
            Check(!text.Contains("网关实际扣费") && row.BilledAmount == 0 && row.BilledCurrency == "CNY", "Removing the billing card changed the stored charge.");
            row.Input = null; row.CacheRead = null; row.CacheWrite = null; row.DurationMs = null;
            layout.Show(row); host.UpdateLayout();
            text = String.Join("\n", VisualChildren<TextBlock>(host).Select(label => label.Text));
            Check(text.Contains("未记录响应耗时") && !text.Contains("缓存命中占比"), "Unknown usage or duration was replaced with a fabricated value.");
            layout.Show(null);
            Check(host.Children.Count == 1, "Clearing selection left stale request details.");
            Console.WriteLine("PASS request detail cards: missing and zero charges, hidden CLI cost estimates, duration scope, token usage, selectable provenance and clearing selection");
        }

        private static string OverviewText(Window window)
        {
            return String.Join("\n", LogicalText((DependencyObject)window.FindName("StatsOverviewCards")));
        }

        private static IEnumerable<string> LogicalText(DependencyObject root)
        {
            var text = root as TextBlock;
            if (text != null) { yield return text.Text; yield break; }
            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                var nested = child as DependencyObject;
                if (nested != null) foreach (string value in LogicalText(nested)) yield return value;
            }
        }

        private static void CheckRequestOverviewLayout()
        {
            var host = new StackPanel();
            var layout = new RequestOverviewLayout(host);
            Theme.Apply(host.Resources, true);
            var rows = new List<RequestStat> {
                new RequestStat { Input = 255, CacheWrite = 0, CacheRead = 13184, Output = 68, EstimatedCost = .1m, EstimateCurrency = "CNY", PricingStatus = "已按配置计费" },
                new RequestStat { Input = 100, CacheWrite = 0, CacheRead = 0, Output = 32, EstimatedCost = .2m, EstimateCurrency = "USD", PricingStatus = "参考估算" }
            };
            layout.Show(rows);
            host.Measure(new Size(850, Double.PositiveInfinity)); host.Arrange(new Rect(0, 0, 850, host.DesiredSize.Height)); host.UpdateLayout();
            string text = String.Join("\n", VisualChildren<TextBlock>(host).Select(label => label.Text));
            Check(text.Contains("2 次") && text.Contains("13,539") && text.Contains("100") && !text.Contains("网关实际扣费") &&
                text.Contains("配置费用 CNY 0.1") && text.Contains("参考估算 USD 0.2") && text.Contains("非账单"),
                "Overview cards changed filtered totals, combined currencies or presented estimates as billed charges.");
            rows[0].BilledAmount = 0; rows[0].BilledCurrency = "CNY";
            rows[1].BilledAmount = .5m; rows[1].BilledCurrency = "USD";
            layout.Show(rows); host.UpdateLayout();
            text = String.Join("\n", VisualChildren<TextBlock>(host).Select(label => label.Text));
            Check(!text.Contains("网关实际扣费") && !text.Contains("USD 0.5") && rows[1].BilledAmount == .5m, "The removed actual-billing card reappeared or stored charges changed.");
            layout.Show(new List<RequestStat>()); host.UpdateLayout();
            text = String.Join("\n", VisualChildren<TextBlock>(host).Select(label => label.Text));
            Check(text.Contains("0 次") && text.Contains("未估算") && !text.Contains("13,539"), "Empty filtered results retained stale overview values.");
            Console.WriteLine("PASS overview cards: filtered token totals, missing and zero charges, separate currencies, estimate provenance and empty results");
        }

        private static void CheckStatisticsListSpace(MainWindow controller)
        {
            var window = controller.View;
            var root = (Grid)window.Content;
            var page = (Grid)window.FindName("StatsPage");
            var chat = (Grid)window.FindName("ChatPage");
            var tools = (Grid)window.FindName("ToolsPage");
            var details = (Expander)window.FindName("StatsDetailsExpander");
            var list = (ListView)window.FindName("StatsRows");
            var oldPage = page.Visibility; var oldChat = chat.Visibility; var oldTools = tools.Visibility;
            try
            {
                page.Visibility = Visibility.Visible; chat.Visibility = tools.Visibility = Visibility.Collapsed;
                Check(!details.IsExpanded && window.FindName("StatsSummary") == null && window.FindName("StatsEstimateSummary") == null,
                    "Duplicate summary details or an expanded detail panel still consume list space by default.");
                foreach (int width in new[] { 1280, 820 })
                {
                    int height = width == 1280 ? 850 : 700;
                    root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();

                    string preview = Environment.GetEnvironmentVariable("CLAUDEUI_LIST_LAYOUT_PREVIEW_PATH");
                    if (!String.IsNullOrWhiteSpace(preview))
                    {
                        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(preview + "-" + width + ".png")) encoder.Save(file);
                    }
                    Check(list.ActualHeight >= (width == 1280 ? 350 : 180), "Request list is too short in the list-first layout at width " + width + ": " + list.ActualHeight);
                }
                root.Measure(new Size(1280, 850)); root.Arrange(new Rect(0, 0, 1280, 850)); root.UpdateLayout();
                double before = list.ActualHeight;
                details.IsExpanded = true; root.UpdateLayout();
                Check(list.ActualHeight > 100 && list.ActualHeight < before && ((ScrollViewer)window.FindName("StatsDetailScroll")).ActualHeight > 0,
                    "On-demand details did not open or consumed the entire request list.");
                var resize = (RequestDetailResizer)typeof(MainWindow).GetField("requestDetailResizer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller);
                resize.ApplyBounds(); root.UpdateLayout();
                var frame = (FrameworkElement)window.FindName("StatsDetailFrame");
                var detailContent = (ScrollViewer)window.FindName("StatsDetailScroll");
                var widthGrip = (System.Windows.Controls.Primitives.Thumb)window.FindName("StatsDetailWidthGrip");
                widthGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                double initialWidth = frame.ActualWidth;
                resize.ResizeBy(-120, 0); root.UpdateLayout();
                Check(frame.ActualWidth < initialWidth && frame.ActualWidth >= 300, "Request detail width did not shrink with its resize handle.");
                widthGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(-120, 0, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                var stateStore = (DataStore)typeof(MainWindow).GetField("store", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(controller);
                double savedWidth = stateStore.Read().RequestDetailWidth;
                Check(Math.Abs(savedWidth - frame.ActualWidth) < 2, "Request detail width was not persisted.");
                var heightGrip = (System.Windows.Controls.Primitives.Thumb)window.FindName("StatsDetailHeightGrip");
                heightGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
                double initialHeight = detailContent.ActualHeight;
                resize.ResizeBy(0, -40); root.UpdateLayout();
                Check(detailContent.ActualHeight < initialHeight && list.ActualHeight > 100, "Detail height resizing did not preserve usable request list space.");
                heightGrip.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 40, true) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
                root.UpdateLayout();
                Check(Math.Abs(detailContent.ActualHeight - initialHeight) < 2, "Cancelled detail resizing did not restore its original height.");
                root.Measure(new Size(820, 700)); root.Arrange(new Rect(0, 0, 820, 700)); root.UpdateLayout();
                resize.ApplyBounds(); root.UpdateLayout();
                Check(frame.ActualWidth <= page.ActualWidth && Math.Abs(stateStore.Read().RequestDetailWidth - savedWidth) < 2,
                    "Window resizing overflowed details or overwrote the preferred width.");
                root.Measure(new Size(1280, 850)); root.Arrange(new Rect(0, 0, 1280, 850)); root.UpdateLayout();
                resize.ApplyBounds(); root.UpdateLayout();
                Check(Math.Abs(frame.ActualWidth - savedWidth) < 2, "The preferred detail width was not restored when space became available.");
                Console.WriteLine("PASS detail resize width/height handles, persistence, cancellation, window bounds and restored preference");
                details.IsExpanded = false;
                Console.WriteLine("PASS statistics list priority, removed duplicate summary, desktop/compact heights and optional detail expansion");
            }
            finally { details.IsExpanded = false; page.Visibility = oldPage; chat.Visibility = oldChat; tools.Visibility = oldTools; }
        }

        private static void CheckCliOutputPresentation(MainWindow controller, string work)
        {
            var window = controller.View;
            var formatted = (RichTextBox)window.FindName("ToolResultBox");
            var raw = (TextBox)window.FindName("LogBox");
            const string noServers = "No MCP servers configured. Use `claude mcp add` to add a server.";
            var write = typeof(MainWindow).GetMethod("WriteLog", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            write.Invoke(controller, new object[] { noServers, true, true });
            string rendered = new TextRange(formatted.Document.ContentStart, formatted.Document.ContentEnd).Text;
            Check(raw.Text.Contains(noServers) && rendered.Contains("尚未配置 MCP 服务") && rendered.Contains("claude mcp add"),
                "MCP empty state lost its setup hint or altered the raw output.");
            var toggle = (Button)window.FindName("ToggleRawLogButton");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(raw.Visibility == Visibility.Visible && formatted.Visibility == Visibility.Collapsed, "Raw log switch did not show the original output.");
            toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(raw.Visibility == Visibility.Collapsed && formatted.Visibility == Visibility.Visible, "Formatted output could not be restored.");

            var fixture = new RichTextBox();
            var presenter = new CliOutputView(fixture);
            const string literal = "  **literal** <tag> [label](https://example.test)";
            presenter.Append(literal);
            Check(new TextRange(fixture.Document.ContentStart, fixture.Document.ContentEnd).Text.Contains(literal), "CLI text was interpreted or spacing was lost.");
            presenter.Append("[stderr] synthetic failure");
            var last = (Paragraph)fixture.Document.Blocks.LastBlock;
            Theme.Apply(fixture.Resources, false);
            Check(last.Foreground.ToString() == fixture.Resources["Failure"].ToString(), "stderr did not use the theme error color.");
            Theme.Apply(fixture.Resources, true);
            Check(last.Foreground.ToString() == fixture.Resources["Failure"].ToString(), "Output error color did not follow a theme change.");
            for (int i = 0; i < 1205; i++) presenter.Append("line " + i);
            Check(fixture.Document.Blocks.Count == 1200, "Large CLI output was not bounded.");
            presenter.Clear();
            presenter.Append(new string('x', 220001) + "tail");
            string truncated = new TextRange(fixture.Document.ContentStart, fixture.Document.ContentEnd).Text;
            Check(truncated.Contains("tail") && truncated.Length < 220000, "An oversized output line lost its newest content or exceeded the presentation limit.");
            presenter.Clear();
            Check(fixture.Document.Blocks.Count == 0, "Output did not reset between commands.");

            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_PREVIEW_PATH");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                ((Button)window.FindName("ToolsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                ((TextBlock)window.FindName("ToolResultTitle")).Text = "MCP 服务";
                ((TextBlock)window.FindName("ToolResultDescription")).Text = "列出 MCP 服务并检查已批准服务的连接";
                ((TextBlock)window.FindName("ToolBadge")).Text = "已完成";
                ((TextBlock)window.FindName("ToolBadge")).SetResourceReference(TextBlock.ForegroundProperty, "Success");
                ((Border)window.FindName("ToolBadgeSurface")).SetResourceReference(Border.BackgroundProperty, "SuccessSurface");
                ((Border)window.FindName("ToolCommandSurface")).Visibility = Visibility.Visible;
                ((TextBox)window.FindName("ToolCommand")).Text = "claude mcp list";
                ((StackPanel)window.FindName("ToolOutputEmpty")).Visibility = Visibility.Collapsed;
                ((TextBlock)window.FindName("ToolExitCode")).Text = "退出码 0";
                ((TextBlock)window.FindName("ToolStatus")).Text = "MCP 服务 · 已完成 · 1.4 秒";
                var surface = (FrameworkElement)window.Content;
                surface.SetResourceReference(Panel.BackgroundProperty, "Canvas");
                foreach (bool dark in new[] { false, true })
                {
                    Theme.Apply(window.Resources, dark);
                    surface.Measure(new Size(1280, 810));
                    surface.Arrange(new Rect(0, 0, 1280, 810));
                    surface.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1280, 810, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                }
            }
            Console.WriteLine("PASS CLI output presentation, literal text, raw log switching, theme colors, and output limits");
        }

        private static void CheckRunningNavigation(MainWindow controller)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var type = typeof(MainWindow);
            var current = (Conversation)type.GetField("current", flags).GetValue(controller);
            var cancel = type.GetField("cancellation", flags);
            var streamField = type.GetField("stream", flags);
            var previousStream = streamField.GetValue(controller);
            var token = new CancellationTokenSource();
            try
            {
                cancel.SetValue(controller, token);
                streamField.SetValue(controller, new ClaudeStream());
                type.GetMethod("UpdateButtons", flags).Invoke(controller, null);
                Check(current.IsTaskRunning && current.CanNavigate, "Running conversation is not marked or navigable.");
                var window = controller.View;
                Check(((ListBox)window.FindName("HistoryList")).IsEnabled && ((StackPanel)window.FindName("ProjectList")).IsEnabled,
                    "Sidebar remains disabled while running.");
                type.GetMethod("ShowStatistics", flags).Invoke(controller, null);
                type.GetMethod("OpenConversationAsync", flags).Invoke(controller, new object[] { current });
                Check(((Grid)window.FindName("ChatPage")).Visibility == Visibility.Visible &&
                    Object.ReferenceEquals(type.GetField("current", flags).GetValue(controller), current),
                    "Returning to running conversation replaced its live context.");
                type.GetMethod("OpenConversationAsync", flags).Invoke(controller, new object[] { new Conversation() });
                Check(Object.ReferenceEquals(type.GetField("current", flags).GetValue(controller), current), "Running context switched to another conversation.");
            }
            finally
            {
                cancel.SetValue(controller, null); streamField.SetValue(controller, previousStream); token.Dispose();
                type.GetMethod("UpdateButtons", flags).Invoke(controller, null);
            }
            Check(!current.IsTaskRunning && current.CanNavigate, "Finished conversation retained running state.");
            Console.WriteLine("PASS running navigation: live context preserved, sidebar enabled, task marker lifecycle and other-session guard");
        }

        private static void CheckReplyProgress(MainWindow controller, DataStore store)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var kind = typeof(MainWindow);
            var window = controller.View;
            var add = kind.GetMethod("AddMessage", flags);
            var current = (Conversation)kind.GetField("current", flags).GetValue(controller);
            var data = (AppData)kind.GetField("data", flags).GetValue(controller);
            if (!data.Conversations.Contains(current)) data.Conversations.Add(current);
            var messages = (StackPanel)window.FindName("Messages");
            messages.Children.Clear();
            add.Invoke(controller, new object[] { "user", "请查看项目结构。" });
            var body = (MarkdownReply)add.Invoke(controller, new object[] { "assistant", "" });
            var progress = (ReplyProgressView)body.Tag;
            progress.Start("medium");
            kind.GetField("reply", flags).SetValue(controller, body);
            kind.GetField("replyProgress", flags).SetValue(controller, progress);
            kind.GetField("clock", flags).SetValue(controller, System.Diagnostics.Stopwatch.StartNew());
            var parser = new ClaudeStream();
            kind.GetField("stream", flags).SetValue(controller, parser);
            var cancellation = new CancellationTokenSource();
            kind.GetField("cancellation", flags).SetValue(controller, cancellation);
            var queue = (System.Collections.Concurrent.ConcurrentQueue<Tuple<string, bool, DateTime>>)kind.GetField("pending", flags).GetValue(controller);
            var pump = kind.GetMethod("Pump", flags);
            Action<string> emit = line => { queue.Enqueue(Tuple.Create(line, false, DateTime.UtcNow)); pump.Invoke(controller, null); };
            emit("{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"fixture-model\"}");
            const string progressRequestId = "req_0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
            emit("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"id\":\"" + progressRequestId + "\",\"model\":\"fixture-model\"}}}");
            Check(progress.Summary.RequestIds.Single() == progressRequestId, "Live request ID did not reach the reply footer.");
            emit("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"thinking\"}}}");
            emit("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"synthetic-private-reasoning\"}}}");
            Check(progress.Summary.Stages.Last().Name == "思考中" && body.Markdown == "", "Thinking events did not update the stage or leaked into the reply.");
            kind.GetField("permissionDecision", flags).SetValue(controller, new System.Threading.Tasks.TaskCompletionSource<bool>());
            pump.Invoke(controller, null);
            Check(progress.Summary.Stages.Last().Name == "等待工具授权", "Permission waiting was not shown in the reply.");
            kind.GetField("permissionDecision", flags).SetValue(controller, null);
            emit("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"content_block\":{\"type\":\"tool_use\",\"name\":\"Read\"}}}");
            emit("{\"type\":\"tool_progress\",\"tool_name\":\"Read\"}");
            emit("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"content\":\"synthetic tool output\"}]}}");
            emit("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"已经整理好项目结构。\"}}}");
            Check(progress.Summary.Stages.Any(step => step.Name == "调用工具 · Read") && progress.Summary.Stages.Last().Name == "生成回复", "Tool and reply phases were missing or stale.");
            emit("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"已经整理好项目结构。\",\"num_turns\":1}");
            kind.GetField("cancellation", flags).SetValue(controller, null);
            cancellation.Dispose();
            kind.GetMethod("Finish", flags).Invoke(controller, new object[] { 0, false, null });
            var saved = store.Read().Conversations.First(item => item.Id == current.Id).Messages.Last(item => item.Role == "assistant");
            Check(saved.Text == "已经整理好项目结构。" && saved.RunSummary.Status == "已完成" && saved.RunSummary.Model == "fixture-model" && saved.RunSummary.CompletedUtc != null,
                "Completion metadata was not saved with its reply.");
            Check(!new System.Web.Script.Serialization.JavaScriptSerializer().Serialize(saved.RunSummary).Contains("synthetic-private-reasoning"), "Progress history retained reasoning text.");
            var restored = new ReplyProgressView((ImageSource)window.Resources["BrandIcon"]);
            restored.Restore(saved.RunSummary);
            Check(restored.Visibility == Visibility.Visible && restored.Summary.Stages.Count == saved.RunSummary.Stages.Count, "Saved progress was not restored.");
            Check(restored.Summary.RequestIds.Single() == progressRequestId, "Request ID did not survive reply persistence and restoration.");
            var restoredPanel = (StackPanel)restored.Child;
            var requestFold = restoredPanel.Children.OfType<Expander>().Single(item => item.Content is TextBox);
            Check(!requestFold.IsExpanded && requestFold.Visibility == Visibility.Visible, "Request IDs must start collapsed.");
            requestFold.IsExpanded = true;
            var requestText = (TextBox)requestFold.Content;
            requestText.SelectAll();
            Check(requestText.IsReadOnly && requestText.TextWrapping == TextWrapping.Wrap && requestText.SelectedText == "请求 ID：" + progressRequestId,
                "The reply footer request ID was truncated or not selectable.");
            restored.SetRequestIds(new[] { progressRequestId, "second-request", progressRequestId });
            Check(restored.Summary.RequestIds.Count == 2 && requestText.Text.Contains(Environment.NewLine + "请求 ID：second-request"),
                "Multiple request IDs were omitted or duplicated in the footer.");
            Check(requestFold.IsExpanded, "Streaming request IDs reset the expansion state.");
            requestFold.IsExpanded = false;
            Check(requestText.Text.Contains("second-request"), "Collapsing request IDs lost content.");
            var legacy = new ReplyProgressView((ImageSource)window.Resources["BrandIcon"]);
            legacy.Restore(null);
            Check(legacy.Visibility == Visibility.Collapsed, "Legacy replies acquired fabricated progress.");
            var failed = new ReplyProgressView((ImageSource)window.Resources["BrandIcon"]);
            failed.Start("");
            failed.Complete("执行失败", 2.4, DateTime.UtcNow);
            Check(failed.Summary.Status == "执行失败" && failed.Summary.Seconds == 2.4, "Failed requests lost their duration.");
            var stopped = new ReplyProgressView((ImageSource)window.Resources["BrandIcon"]);
            stopped.Start("high");
            for (int i = 0; i < 100; i++) stopped.Observe("阶段 " + i, i);
            stopped.Complete("已停止", 101, DateTime.UtcNow);
            Check(stopped.Summary.Status == "已停止" && stopped.Summary.Stages.Count == 80 && stopped.Summary.Stages[0].Seconds == 0,
                "Stopped requests or bounded stage history were incorrect.");

            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_PROGRESS_PREVIEW_PATH");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                progress.Summary.Seconds = 1.4;
                progress.Restore(progress.Summary);
                foreach (bool dark in new[] { false, true })
                {
                    Theme.Apply(window.Resources, dark);
                    messages.Measure(new Size(760, Double.PositiveInfinity));
                    int height = (int)Math.Ceiling(messages.DesiredSize.Height);
                    messages.Arrange(new Rect(0, 0, 760, height));
                    messages.UpdateLayout();
                    var visual = new DrawingVisual();
                    using (var dc = visual.RenderOpen())
                    {
                        dc.DrawRectangle((Brush)window.Resources["Canvas"], null, new Rect(0, 0, 808, height + 48));
                        dc.DrawRectangle(new VisualBrush(messages), null, new Rect(24, 24, 760, height));
                    }
                    var bitmap = new RenderTargetBitmap(808, height + 48, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                }
            }
            Console.WriteLine("PASS reply progress event flow, tool permission, completion timing, persistence, legacy replies, failures, and stopping");
        }

        private static void CheckFailedSessionRecovery(string work)
        {
            string root = Path.Combine(work, "failed-session-test");
            string folder = Path.Combine(root, "projects", "workspace");
            Directory.CreateDirectory(folder);
            var id = Guid.NewGuid().ToString();
            var file = Path.Combine(folder, id + ".jsonl");
            const string stamp = "2026-11-20T09:34:57Z";
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            File.WriteAllLines(file, new object[] {
                new { type = "user", uuid = "u1", cwd = work, message = new { content = "你好" } },
                new { type = "attachment", uuid = "env", parentUuid = "u1" },
                new { type = "assistant", uuid = "a1", parentUuid = "env", isApiErrorMessage = true, timestamp = stamp, message = new { content = "Model unavailable" } },
                new { type = "user", uuid = "u2", parentUuid = "a1", cwd = work, message = new { content = "重试" } },
                new { type = "assistant", uuid = "a2", parentUuid = "u2", message = new { content = "成功" } }
            }.Select(json.Serialize));
            var catalog = new CliConversationCatalog();
            var snapshot = catalog.Read(new[] { root });
            var imported = snapshot.Conversations.Single();
            var local = new Conversation { Workspace = work, CliConfigDirectory = root, Title = "你好",
                Messages = new List<StoredMessage> {
                    new StoredMessage { Role = "user", Text = "你好" },
                    new StoredMessage { Role = "assistant", Text = "Model unavailable",
                        RunSummary = new ChatRunSummary { Status = "执行失败", CompletedUtc = stamp } }
                } };
            Check(CliConversationCatalog.FindPersistedTranscript(root, work, id) == file &&
                CliConversationCatalog.FindPersistedTranscript(root, work, Guid.NewGuid().ToString()) == null,
                "Failed requests must link only when a CLI transcript exists for the session and workspace.");
            Check(CliHistoryMerge.MatchesFailedSession(local, imported), "The failed first turn was not matched across CLI attachments.");
            var unrelated = new Conversation { Workspace = work, CliConfigDirectory = root, Title = local.Title,
                Messages = new List<StoredMessage> { local.Messages[0], new StoredMessage { Role = "assistant", Text = local.Messages[1].Text,
                    RunSummary = new ChatRunSummary { Status = "执行失败", CompletedUtc = "2026-11-20T09:40:00Z" } } } };
            Check(!CliHistoryMerge.MatchesFailedSession(unrelated, imported), "A separate, identically titled conversation was matched.");
            imported.Messages = CliConversationCatalog.ReadMessages(file);
            var store = new DataStore(root);
            var fixture = new AppData { Workspace = work, ActiveConversationId = imported.Id };
            fixture.Conversations.AddRange(new[] { local, imported, unrelated });
            store.Save(fixture);
            var controller = new MainWindow(store);
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var state = (AppData)typeof(MainWindow).GetField("data", flags).GetValue(controller);
            var merge = typeof(MainWindow).GetMethod("MergeCatalog", flags);
            merge.Invoke(controller, new object[] { snapshot });
            merge.Invoke(controller, new object[] { snapshot });
            var recovered = state.Conversations.Single(item => item.SessionId == id);
            Check(state.Conversations.Count == 2 && recovered.Id == imported.Id && recovered.Messages.Count == 4 &&
                recovered.Messages[1].RunSummary.Status == "执行失败" && recovered.Messages[3].Text == "成功",
                "Recovering the failed session duplicated or lost its failed turn, subsequent messages, or metadata.");
            Console.WriteLine("PASS failed session linking, duplicate recovery, and independent same-title chats");
        }

        private static void CheckConversationDeletion(string work)
        {
            var root = Path.Combine(work, "delete-fixture");
            var project = Path.Combine(root, "workspace");
            var cli = Path.Combine(root, "cli");
            var store = new DataStore(Path.Combine(root, "data"));
            var projectStorage = Path.Combine(cli, "projects", "workspace");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(projectStorage);
            string id = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString();
            string ownTranscript = Path.Combine(projectStorage, id + ".jsonl");
            string otherTranscript = Path.Combine(projectStorage, other + ".jsonl");
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            string transcript = json.Serialize(new { type = "user", cwd = project, message = new { content = "same title" } });
            File.WriteAllText(ownTranscript, transcript);
            File.WriteAllText(otherTranscript, transcript);
            string history = Path.Combine(cli, "history.jsonl");
            string otherHistory = json.Serialize(new { sessionId = other, project = project });
            File.WriteAllLines(history, new[] { json.Serialize(new { sessionId = id, project = project }), otherHistory });
            string child = Path.Combine(projectStorage, id, "subagents");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "agent.jsonl"), "child");
            string original = Path.Combine(project, "file.txt");
            File.WriteAllText(original, "original");
            var ownAttachment = AttachmentFiles.Import(original, store.AttachmentDirectory);
            var shared = AttachmentFiles.Import(original, store.AttachmentDirectory);
            var cliShared = AttachmentFiles.Import(original, store.AttachmentDirectory);
            File.AppendAllText(otherTranscript, Environment.NewLine + json.Serialize(new { type = "user", cwd = project,
                message = new { content = cliShared.LocalPath } }));
            var target = new Conversation { SessionId = id, Workspace = project, CliConfigDirectory = cli,
                DraftAttachments = new List<ChatAttachment> { ownAttachment, shared, cliShared, new ChatAttachment { LocalPath = original } } };
            var remaining = new Conversation { SessionId = other, Workspace = project,
                CliTranscriptPath = otherTranscript, DraftAttachments = new List<ChatAttachment> { shared } };
            target.Messages.Add(new StoredMessage { Role = "assistant", RunSummary = new ChatRunSummary {
                RequestIds = new List<string> { "local", "shared" } } });
            remaining.Messages.Add(new StoredMessage { Role = "assistant", RunSummary = new ChatRunSummary {
                RequestIds = new List<string> { "shared" } } });
            store.Statistics.Append(new[] { new RequestStat { SessionId = id }, new RequestStat { SessionId = other },
                new RequestStat { RequestId = "local" }, new RequestStat { RequestId = "shared" } });

            var wrongProject = new Conversation { SessionId = id, Workspace = root, CliConfigDirectory = cli };
            bool blocked = false;
            try { ConversationDeletion.DeleteRecords(store, wrongProject, new[] { remaining }, new[] { cli }); }
            catch (IOException) { blocked = true; }
            Check(blocked && File.Exists(ownTranscript) && File.ReadAllLines(history).Length == 2,
                "Deleting a session owned by another workspace was not blocked before modifying files.");
            ConversationDeletion.DeleteRecords(store, target, new[] { remaining }, new[] { cli });
            ConversationDeletion.DeleteRecords(store, target, new[] { remaining }, new[] { cli });
            Check(!File.Exists(ownTranscript) && !Directory.Exists(Path.Combine(projectStorage, id)) &&
                File.Exists(otherTranscript) && File.ReadAllLines(history).SequenceEqual(new[] { otherHistory }),
                "Conversation deletion changed another session or left its own CLI records.");
            Check(!File.Exists(ownAttachment.LocalPath) && File.Exists(shared.LocalPath) && File.Exists(cliShared.LocalPath) && File.Exists(original) &&
                store.Statistics.Read().Count == 2 && store.Statistics.Read().Any(row => row.SessionId == other) &&
                store.Statistics.Read().Any(row => row.RequestId == "shared"),
                "Conversation deletion removed a project file or shared record, or missed an owned record.");
            Console.WriteLine("PASS permanent conversation deletion, idempotent retry, and unrelated files preserved");
        }

        private static CliCatalogSnapshot CheckCliCatalog(string work)
        {
            CheckLocalCommandHistory(work);
            string root = Path.Combine(work, "cli-catalog"), project = Path.Combine(work, "project-one"), empty = Path.Combine(work, "project-empty");
            Directory.CreateDirectory(project);
            Directory.CreateDirectory(empty);
            string folder = Path.Combine(root, "projects", "encoded-project");
            Directory.CreateDirectory(folder);
            string id = Guid.NewGuid().ToString(), other = Guid.NewGuid().ToString();
            string file = Path.Combine(folder, id + ".jsonl");
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var records = new object[] {
                new { type = "user", uuid = "u1", parentUuid = (string)null, sessionId = id, cwd = project, message = new { content = "第一条用户消息" } },
                new { type = "assistant", uuid = "a1", parentUuid = "u1", sessionId = id, cwd = project, message = new { content = new[] { new { type = "thinking", text = "private" }, new { type = "text", text = "第一条回复" } } } },
                new { type = "user", uuid = "abandoned", parentUuid = "a1", sessionId = id, cwd = project, message = new { content = "放弃的分支" } },
                new { type = "attachment", uuid = "env1", parentUuid = "a1", attachment = new { type = "environment" } },
                new { type = "attachment", uuid = "env2", parentUuid = "env1", attachment = new { type = "skill_listing" } },
                new { type = "user", uuid = "u2", parentUuid = "env2", sessionId = id, cwd = project, message = new { content = "当前分支" } },
                new { type = "assistant", uuid = "a2", parentUuid = "u2", sessionId = id, cwd = project, message = new { content = new[] { new { type = "tool_use", text = "tool-only" } } } },
                new { type = "assistant", uuid = "a3", parentUuid = "a2", sessionId = id, cwd = project, message = new { content = new[] { new { type = "text", text = "当前回复" } } } },
                new { type = "user", uuid = "rename-meta", parentUuid = "a3", isMeta = true, message = new { content = "<local-command-caveat>Internal CLI operation</local-command-caveat>" } },
                new { type = "user", uuid = "rename-record", parentUuid = "rename-meta", message = new { content = "<command-name>/rename</command-name>\n<command-message>rename</command-message>\n<command-args>旧缓存</command-args>" } },
                new { type = "system", uuid = "rename-output", parentUuid = "rename-record", subtype = "local_command" },
                new { type = "custom-title", sessionId = id, customTitle = "CLI 重命名的会话" }
            };
            File.WriteAllLines(file, records.Select(record => json.Serialize(record)).Concat(new[] { "{partial" }), Encoding.UTF8);
            string subagents = Path.Combine(folder, "subagents");
            Directory.CreateDirectory(subagents);
            File.WriteAllText(Path.Combine(subagents, other + ".jsonl"), json.Serialize(records[0]));
            File.WriteAllText(Path.Combine(folder, other + ".jsonl"), json.Serialize(new { type = "user", isSidechain = true, cwd = project, message = new { content = "subagent" } }));
            File.WriteAllText(Path.Combine(root, ".claude.json"), json.Serialize(new { projects = new Dictionary<string, object> { { empty, new object() } } }));
            File.WriteAllText(Path.Combine(root, "history.jsonl"), json.Serialize(new { project = project, sessionId = id }));
            var catalog = new CliConversationCatalog();
            var snapshot = catalog.Read(new[] { root, root + Path.DirectorySeparatorChar });
            Check(snapshot.Conversations.Count == 1 && snapshot.Conversations[0].Title == "CLI 重命名的会话" &&
                snapshot.Projects.Contains(CliConversationCatalog.Normalize(empty)), "CLI discovery missed sessions, project settings, names, or duplicate roots.");
            var transcript = CliConversationCatalog.ReadMessages(file);
            Check(transcript.Count == 4 && transcript[0].Text == "第一条用户消息" && transcript[3].Text == "当前回复" &&
                !transcript.Any(message => message.Text.Contains("private") || message.Text.Contains("放弃的分支") || message.Text.Contains("tool-only")),
                "CLI transcript loading lost branch ancestry or included private/tool-only data.");
            File.AppendAllText(file, Environment.NewLine + json.Serialize(new { type = "custom-title", customTitle = "更新后的会话" }) + Environment.NewLine);
            Check(catalog.Read(new[] { root }).Conversations.Single().Title == "更新后的会话", "CLI metadata cache did not refresh when the transcript changed.");
            var fixture = new AppData();
            fixture.ProjectFolders.Add(CliConversationCatalog.Normalize(empty));
            fixture.CollapsedProjects.Add(CliConversationCatalog.Normalize(project));
            fixture.HiddenCliSessions.Add(id);
            fixture.Conversations.Add(snapshot.Conversations[0]);
            fixture.ConversationsCollapsed = true;
            var persistence = new DataStore(Path.Combine(work, "catalog-state"));
            persistence.Save(fixture);
            var restored = persistence.Read();
            Check(restored.ProjectFolders.Count == 1 && restored.CollapsedProjects.Count == 1 && restored.HiddenCliSessions.Contains(id) &&
                restored.ConversationsCollapsed && restored.Conversations[0].CliConfigDirectory == snapshot.Conversations[0].CliConfigDirectory,
                "Projects, collapse state, hidden sessions, or CLI routing did not persist.");
            Console.WriteLine("PASS CLI catalog, project discovery, lazy transcript reading, branch ancestry, metadata refresh, and navigation persistence");
            return snapshot;
        }

        private static void CheckHistoryMerge(string work)
        {
            var first = new StoredMessage { Role = "user", Text = "重复问题" };
            var answer = new StoredMessage { Role = "assistant", Text = "第一轮回复", RunSummary = new ChatRunSummary(),
                Attachments = new List<ChatAttachment>() };
            var repeat = new StoredMessage { Role = "user", Text = "重复问题" };
            var notice = new StoredMessage { Role = "notice", Text = "客户端过程提示" };
            var last = new StoredMessage { Role = "assistant", Text = "末轮回复" };
            var saved = new List<StoredMessage> { first, answer, repeat, notice, last };
            var partial = new CliTranscript();
            partial.Messages.Add(new StoredMessage { Role = "assistant", Text = last.Text });
            var merged = CliHistoryMerge.Merge(saved, partial);
            Check(merged.SequenceEqual(saved), "A partial CLI tail deleted cached history or client-only notices.");
            Check(CliHistoryMerge.Merge(saved, new CliTranscript()).SequenceEqual(saved), "An empty/incomplete CLI file erased saved history.");
            var complete = new CliTranscript();
            complete.Messages.AddRange(new[] { first, answer, repeat, last });
            merged = CliHistoryMerge.Merge(new[] { last }, complete);
            Check(merged.SequenceEqual(complete.Messages), "Full CLI ancestry did not restore previously lost messages.");
            merged = CliHistoryMerge.Merge(saved, complete);
            for (int restart = 0; restart < 5; restart++) merged = CliHistoryMerge.Merge(merged, complete);
            Check(merged.SequenceEqual(saved) && Object.ReferenceEquals(merged[1], answer),
                "Repeated restart imports dropped/duplicated repeated messages or lost run/attachment metadata.");
            var internalRecord = new StoredMessage { Role = "user", Text = "<command-name>/rename</command-name>" };
            complete.InternalMessages.Add(internalRecord);
            merged = CliHistoryMerge.Merge(new[] { first, internalRecord, answer, repeat, notice, last }, complete);
            Check(merged.SequenceEqual(saved), "Confirmed internal CLI record was not cleaned from the cache.");
            complete.Messages.Insert(1, internalRecord); // The same literal XML was also sent as a real message.
            merged = CliHistoryMerge.Merge(new[] { first, internalRecord, internalRecord, answer, repeat, notice, last }, complete);
            Check(merged.Count(item => item.Text == internalRecord.Text) == 1,
                "Cleaning an internal record deleted the genuine matching user message.");
            var cache = new DataStore(Path.Combine(work, "history-restarts"));
            var state = new AppData();
            state.Conversations.Add(new Conversation { Workspace = work, Messages = saved });
            for (int restart = 0; restart < 5; restart++)
            {
                cache.Save(state);
                state = cache.Read();
                state.Conversations[0].Messages = CliHistoryMerge.Merge(state.Conversations[0].Messages, partial);
                Check(state.Conversations[0].Messages.Select(item => item.Role + item.Text).SequenceEqual(saved.Select(item => item.Role + item.Text)) &&
                    state.Conversations[0].Messages[1].RunSummary != null && state.Conversations[0].Messages[1].Attachments != null,
                    "Save/restart/import cycles reduced history or lost metadata.");
            }
            Console.WriteLine("PASS history merge: incomplete/empty CLI files, source recovery, repeated text, five restart imports, notices, run metadata and verified internal-record cleanup");
        }

        private static void CheckReplyTurnGrouping(string work)
        {
            string file = Path.Combine(work, "reply-turns.jsonl");
            var records = new object[] {
                new { type = "user", uuid = "q1", parentUuid = (string)null, message = new { content = "同样的问题" } },
                new { type = "assistant", uuid = "before", parentUuid = "q1", message = new { content = "我先检查一下。" } },
                new { type = "assistant", uuid = "tool", parentUuid = "before", message = new { content = new[] { new { type = "tool_use", text = "not displayed" } } } },
                new { type = "user", uuid = "tool-result", parentUuid = "tool", message = new { content = new[] { new { type = "tool_result", text = "not displayed" } } } },
                new { type = "attachment", uuid = "environment", parentUuid = "tool-result", attachment = new { type = "environment" } },
                new { type = "user", uuid = "context", parentUuid = "environment", isMeta = true, message = new { content = "internal context" } },
                new { type = "assistant", uuid = "after", parentUuid = "context", message = new { content = "检查结束，结果正常。" } },
                new { type = "user", uuid = "q2", parentUuid = "after", message = new { content = "同样的问题" } },
                new { type = "assistant", uuid = "second-reply", parentUuid = "q2", message = new { content = "另一轮回复。" } }
            };
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            File.WriteAllLines(file, records.Select(record => json.Serialize(record)), Encoding.UTF8);
            var history = CliConversationCatalog.ReadTranscript(file);
            Check(history.Messages.Count == 4 && history.Messages[1].Role == "assistant" &&
                history.Messages[1].Text == "我先检查一下。\n\n检查结束，结果正常。" && history.Messages[3].Text == "另一轮回复。",
                "One request produced multiple assistant cards or crossed a real user turn.");
            var cached = new List<StoredMessage> {
                history.Messages[0], new StoredMessage { Role = "assistant", Text = "我先检查一下。" },
                new StoredMessage { Role = "assistant", Text = "检查结束，结果正常。" }, history.Messages[2], history.Messages[3]
            };
            var merged = CliHistoryMerge.Merge(cached, history);
            Check(merged.Count == 4 && merged[1].Text == history.Messages[1].Text, "Old split cache was duplicated during turn migration.");
            var summary = new ChatRunSummary { Model = "test", Seconds = 7 };
            var live = new List<StoredMessage> { history.Messages[0],
                new StoredMessage { Role = "assistant", Text = "我先检查一下。检查结束，结果正常。", RunSummary = summary },
                history.Messages[2], history.Messages[3] };
            merged = CliHistoryMerge.Merge(live, history);
            Check(merged.Count == 4 && Object.ReferenceEquals(merged[1].RunSummary, summary),
                "Live concatenated deltas were duplicated against grouped CLI records or lost run metadata.");
            var cache = new DataStore(Path.Combine(work, "grouped-restarts"));
            var state = new AppData();
            state.Conversations.Add(new Conversation { Workspace = work, Messages = merged });
            for (int restart = 0; restart < 5; restart++)
            {
                cache.Save(state);
                state = cache.Read();
                state.Conversations[0].Messages = CliHistoryMerge.Merge(state.Conversations[0].Messages, CliConversationCatalog.ReadTranscript(file));
                Check(state.Conversations[0].Messages.Count == 4 && state.Conversations[0].Messages[1].RunSummary.Seconds == 7,
                    "Grouped reply grew or split after restarting.");
            }
            Check(CliReplyGroups.Group(new[] { new StoredMessage { Role = "assistant", Text = "run1", RunSummary = new ChatRunSummary() },
                new StoredMessage { Role = "assistant", Text = "run2", RunSummary = new ChatRunSummary() } }).Count == 2,
                "Independent recorded requests were merged.");
            Console.WriteLine("PASS reply turns: tool-before/after grouping, real-user boundaries, old split-cache migration, live deltas, run metadata and five persisted restart cycles");
        }

        private static void CheckLocalCommandHistory(string work)
        {
            string root = Path.Combine(work, "local-command-history");
            string folder = Path.Combine(root, "projects", "fixture");
            Directory.CreateDirectory(folder);
            string id = Guid.NewGuid().ToString(), file = Path.Combine(folder, id + ".jsonl");
            const string rename = "<command-name>/rename</command-name>\n    <command-message>rename</command-message>\n    <command-args>测试</command-args>";
            const string caveat = "<local-command-caveat>Local command context</local-command-caveat>";
            var records = new object[] {
                new { type = "user", uuid = "meta", parentUuid = (string)null, promptId = "rename-1", isMeta = true, cwd = work, message = new { content = caveat } },
                new { type = "user", uuid = "rename", parentUuid = "meta", promptId = "rename-1", cwd = work, message = new { content = rename } },
                new { type = "system", subtype = "local_command", uuid = "result", parentUuid = "rename", content = "<local-command-stdout>Session renamed to: 测试</local-command-stdout>" },
                new { type = "user", uuid = "u1", parentUuid = "result", cwd = work, message = new { content = "真正发送的消息" } },
                new { type = "assistant", uuid = "a1", parentUuid = "u1", cwd = work, message = new { content = "正常回复" } },
                new { type = "user", uuid = "meta2", parentUuid = "a1", promptId = "rename-2", isMeta = true, cwd = work, message = new { content = caveat } },
                new { type = "user", uuid = "rename2", parentUuid = "meta2", promptId = "rename-2", cwd = work,
                    message = new { content = new[] { new { type = "text", text = rename } } } },
                new { type = "user", uuid = "output", parentUuid = "rename2", promptId = "rename-2", cwd = work,
                    message = new { content = "<local-command-stdout>Session renamed to: 测试</local-command-stdout>" } },
                new { type = "user", uuid = "quoted", parentUuid = "output", promptId = "real-user", cwd = work, message = new { content = rename } },
                new { type = "assistant", uuid = "quoted-reply", parentUuid = "quoted", cwd = work, message = new { content = rename } },
                new { type = "user", uuid = "plain", parentUuid = "quoted-reply", cwd = work, message = new { content = "/rename 用户实际输入" } }
            };
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            File.WriteAllLines(file, records.Select(record => json.Serialize(record)), Encoding.UTF8);
            var messages = CliConversationCatalog.ReadMessages(file);
            Check(messages.Select(message => message.Text).SequenceEqual(new[] { "真正发送的消息", "正常回复", rename, rename, "/rename 用户实际输入" }),
                "Local command records leaked into chat or genuine pasted XML / slash commands were removed.");
            Check(messages[2].Role == "user" && messages[3].Role == "assistant", "Command filtering changed genuine message roles.");
            Check(new CliConversationCatalog().Read(new[] { root }).Conversations.Single().Title == "真正发送的消息",
                "Internal rename XML became the conversation title.");
            Check(File.ReadAllLines(file).Length == records.Length, "History filtering changed the source transcript.");
            Console.WriteLine("PASS local command history: internal rename/string/array/output hidden, ancestry preserved, genuine XML/slash messages retained, title and source preserved");
        }

        private static void CheckSidebarNavigation(MainWindow controller, DataStore store, CliCatalogSnapshot snapshot, string work)
        {
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var data = (AppData)typeof(MainWindow).GetField("data", flags).GetValue(controller);
            var merge = typeof(MainWindow).GetMethod("MergeCatalog", flags);
            merge.Invoke(controller, new object[] { snapshot });
            merge.Invoke(controller, new object[] { snapshot });
            var source = snapshot.Conversations.Single();
            var copied = new CliCatalogSnapshot();
            copied.Conversations.Add(new Conversation { SessionId = source.SessionId, Workspace = source.Workspace, Title = source.Title,
                CliConfigDirectory = Path.Combine(work, "other-config"), CliTranscriptPath = source.CliTranscriptPath, UpdatedUtc = source.UpdatedUtc });
            merge.Invoke(controller, new object[] { copied });
            Check(data.Conversations.Count(item => item.SessionId == source.SessionId) == 1, "Session copies across CLI roots were duplicated.");
            var imported = data.Conversations.Single(item => item.SessionId == source.SessionId);
            imported.CliConfigDirectory = source.CliConfigDirectory;
            imported.Messages.Add(new StoredMessage { Role = "user", Text = "<command-name>/rename</command-name>\n<command-message>rename</command-message>\n<command-args>旧缓存</command-args>" });
            var context = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext(controller.View.Dispatcher));
            try
            {
                var task = (System.Threading.Tasks.Task)typeof(MainWindow).GetMethod("OpenConversationAsync", flags).Invoke(controller, new object[] { imported });
                if (!task.IsCompleted)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();
                    task.ContinueWith(done => controller.View.Dispatcher.BeginInvoke(new Action(() => frame.Continue = false)));
                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                }
                task.GetAwaiter().GetResult();
            }
            finally { SynchronizationContext.SetSynchronizationContext(context); }
            Check(imported.Messages.Count == 4 && ((TextBlock)controller.View.FindName("ChatTitle")).Text == imported.Title &&
                ((ListBox)controller.View.FindName("HistoryList")).SelectedItem == imported,
                "Choosing a CLI session did not load its actual transcript and selected state.");
            Check(store.Read().Conversations.Single(item => item.SessionId == imported.SessionId).Messages.Count == 4,
                "Imported messages were not saved for reopening.");
            string projectFolder = imported.Workspace;
            var projectMenu = controller.CreateProjectMenu(projectFolder);
            Check(projectMenu.Items.OfType<MenuItem>().Select(item => (string)item.Header)
                .SequenceEqual(new[] { "置顶", "编辑", "归档聊天", "移除项目" }), "Project folder menu differs from requested actions.");
            controller.ChangeProjectNavigation(projectFolder, "pin");
            Check(store.Read().ProjectStates.Single(item => item.Folder == projectFolder).IsPinned,
                "Project pin state did not persist.");
            controller.RenameProject(projectFolder, "自定义项目名称");
            Check(store.Read().ProjectStates.Single(item => item.Folder == projectFolder).Name == "自定义项目名称" && imported.Workspace == projectFolder,
                "Project rename changed the workspace or failed to persist.");
            controller.ChangeProjectNavigation(projectFolder, "archive");
            var projectPanelBefore = (StackPanel)controller.View.FindName("ProjectList");
            Check(!VisualChildren<Expander>(projectPanelBefore).Any(group => (string)group.Tag == projectFolder) &&
                ((ListBox)controller.View.FindName("HistoryList")).Items.Contains(imported), "Project archive lost sessions or left the folder visible.");
            ((Button)controller.View.FindName("ArchivedProjectsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(VisualChildren<Expander>(projectPanelBefore).Any(group => (string)group.Tag == projectFolder), "Archived projects cannot be reached.");
            var restoreMenu = controller.CreateProjectMenu(projectFolder);
            restoreMenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "取消归档").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Check(!store.Read().ProjectStates.Single(item => item.Folder == projectFolder).IsArchived, "Unarchiving project did not persist.");
            ((Button)controller.View.FindName("ArchivedProjectsButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            controller.ChangeProjectNavigation(projectFolder, "remove");
            merge.Invoke(controller, new object[] { snapshot });
            typeof(MainWindow).GetMethod("UpdateHistory", flags).Invoke(controller, null);
            Check(store.Read().ProjectStates.Single(item => item.Folder == projectFolder).IsRemoved &&
                !VisualChildren<Expander>(projectPanelBefore).Any(group => (string)group.Tag == projectFolder) &&
                ((ListBox)controller.View.FindName("HistoryList")).Items.Contains(imported) && imported.Workspace == projectFolder,
                "Removed project returned after catalog refresh or lost its sessions.");
            Check(controller.CreateSessionMenu(imported).Items.OfType<MenuItem>().Select(item => (string)item.Header)
                .SequenceEqual(new[] { "压缩上下文", "重命名…", "创建分支…", "复制会话 ID", "从对话列表移除…", "彻底删除…" }),
                "Project session actions were not preserved.");
            var projectState = data.ProjectStates.Single(item => item.Folder == projectFolder);
            projectState.IsRemoved = false;
            projectState.Name = null;
            controller.ChangeProjectNavigation(projectFolder, "pin");
            data.HiddenCliSessions.Add(imported.SessionId);
            data.Conversations.Remove(imported);
            merge.Invoke(controller, new object[] { snapshot });
            Check(!data.Conversations.Any(item => item.SessionId == imported.SessionId), "A hidden CLI session reappeared during refresh.");
            data.HiddenCliSessions.Remove(imported.SessionId);
            data.Conversations.Add(imported);
            ((Button)controller.View.FindName("NewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(((ListBox)controller.View.FindName("HistoryList")).SelectedItem == null, "New conversation incorrectly resumed imported history.");

            for (int i = 0; i < 24; i++)
                data.Conversations.Add(new Conversation { Title = "滚动测试 " + i, Workspace = imported.Workspace });
            typeof(MainWindow).GetMethod("UpdateHistory", flags).Invoke(controller, null);
            var root = (FrameworkElement)controller.View.Content;
            root.SetResourceReference(Panel.BackgroundProperty, "Canvas");
            foreach (bool dark in new[] { true, false })
            {
                Theme.Apply(controller.View.Resources, dark);
                root.Measure(new Size(1280, 810));
                root.Arrange(new Rect(0, 0, 1280, 810));
                root.UpdateLayout();
                var projectPanel = (StackPanel)controller.View.FindName("ProjectList");
                var bars = VisualChildren<System.Windows.Controls.Primitives.ScrollBar>(projectPanel)
                    .Where(bar => bar.Visibility == Visibility.Visible && bar.ActualHeight > 0 && bar.Orientation == Orientation.Vertical).ToList();
                Check(bars.Count > 0, "Overflowing project conversations did not expose a scrollbar.");
                foreach (var bar in bars)
                {
                    var track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
                    Check(track != null && track.DecreaseRepeatButton.Opacity == 0 && track.IncreaseRepeatButton.Opacity == 0,
                        "Project scrollbars fell back to the system arrow/track appearance.");
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(bar.ActualWidth), (int)Math.Ceiling(bar.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(bar);
                    var pixel = new byte[4];
                    bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
                    Check(pixel[3] == 0, "The project scrollbar paints an opaque edge over its sidebar.");
                    var viewer = VisualChildren<ScrollViewer>(projectPanel).First(item => item.ScrollableHeight > 0);
                    viewer.ScrollToBottom();
                    root.UpdateLayout();
                    Check(viewer.VerticalOffset > 0, "Removing the scrollbar edge disabled scrolling.");
                }
                string scrollPreview = Environment.GetEnvironmentVariable("CLAUDEUI_SCROLLBAR_PREVIEW_PATH");
                if (!String.IsNullOrWhiteSpace(scrollPreview))
                {
                    var bitmap = new RenderTargetBitmap(240, 810, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(scrollPreview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                }
            }
            Console.WriteLine("PASS project scrollbar transparency, theme styling, and overflow scrolling");
            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_SIDEBAR_PREVIEW_PATH");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                data.Conversations.Clear();
                data.Conversations.Add(imported);
                data.Conversations.Add(new Conversation { Title = "整理项目结构", Workspace = imported.Workspace });
                data.ProjectFolders.Clear();
                data.ProjectFolders.Add(imported.Workspace);
                data.ProjectFolders.Add(Path.Combine(work, "project-empty"));
                data.CollapsedProjects.Clear();
                typeof(MainWindow).GetMethod("UpdateHistory", flags).Invoke(controller, null);
                var surface = (FrameworkElement)controller.View.Content;
                surface.SetResourceReference(Panel.BackgroundProperty, "Canvas");
                foreach (bool dark in new[] { false, true })
                {
                    Theme.Apply(controller.View.Resources, dark);
                    surface.Measure(new Size(1280, 810));
                    surface.Arrange(new Rect(0, 0, 1280, 810));
                    surface.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1280, 810, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(surface);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(file);
                    string composerPreview = Environment.GetEnvironmentVariable("CLAUDEUI_COMPOSER_PREVIEW_PATH");
                    if (!String.IsNullOrWhiteSpace(composerPreview))
                    {
                        var composer = (Border)controller.View.FindName("ComposerSurface");
                        var composerBitmap = new RenderTargetBitmap((int)Math.Ceiling(composer.ActualWidth), (int)Math.Ceiling(composer.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                        composerBitmap.Render(composer); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(composerBitmap));
                        using (var file = File.Create(composerPreview + (dark ? "-dark.png" : "-light.png"))) png.Save(file);
                    }
                }
            }
            Console.WriteLine("PASS project groups, global chats, cross-root deduplication, selecting CLI history, hidden sessions, and new chat isolation");
        }

        private static void CheckStatisticsChatNavigation(MainWindow controller, AppData data)
        {
            var window = controller.View;
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            data = (AppData)typeof(MainWindow).GetField("data", flags).GetValue(controller);
            var currentField = typeof(MainWindow).GetField("current", flags);
            var original = (Conversation)currentField.GetValue(controller);
            var activate = typeof(MainWindow).GetMethod("Activate", flags);
            var first = new Conversation { Title = "navigation first", Workspace = original.Workspace };
            var second = new Conversation { Title = "navigation second", Workspace = original.Workspace };
            data.Conversations.Add(first); data.Conversations.Add(second);
            activate.Invoke(controller, new object[] { first });
            var history = (ListBox)window.FindName("HistoryList");
            var stats = (Grid)window.FindName("StatsPage");
            var chat = (Grid)window.FindName("ChatPage");
            var statsButton = (Button)window.FindName("StatsButton");
            Check(window.FindName("StatsBackButton") == null, "The removed statistics back button is still present.");
            statsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var surface = (FrameworkElement)window.Content;
            surface.Measure(new Size(1280, 810)); surface.Arrange(new Rect(0, 0, 1280, 810)); surface.UpdateLayout();
            var row = (ListBoxItem)history.ItemContainerGenerator.ContainerFromItem(first);
            Check(row != null && row.IsSelected, "The current conversation row was not generated.");
            var menu = VisualChildren<Button>(row).First(item => item.Name == "SessionMenuButton");
            menu.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(stats.Visibility == Visibility.Visible, "Clicking a conversation menu unexpectedly navigated away.");
            row.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(chat.Visibility == Visibility.Visible && stats.Visibility == Visibility.Collapsed && currentField.GetValue(controller) == first && statsButton.Tag == null,
                "Clicking the selected global conversation did not return to its chat.");
            statsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var project = (StackPanel)window.FindName("ProjectList");
            surface.UpdateLayout();
            var projectList = VisualChildren<ListBox>(project).First(list => list.Items.Contains(first));
            var projectRow = (ListBoxItem)projectList.ItemContainerGenerator.ContainerFromItem(first);
            Check(projectRow != null, "The current project conversation row was not generated.");
            projectRow.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
            Check(chat.Visibility == Visibility.Visible && stats.Visibility == Visibility.Collapsed && currentField.GetValue(controller) == first,
                "Clicking the selected project conversation did not return to its chat.");
            statsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            history.SelectedItem = second;
            Check(chat.Visibility == Visibility.Visible && stats.Visibility == Visibility.Collapsed && currentField.GetValue(controller) == second,
                "Selecting a different conversation from statistics did not open that conversation.");
            data.Conversations.Remove(first); data.Conversations.Remove(second);
            activate.Invoke(controller, new object[] { original });
            Console.WriteLine("PASS statistics navigation: current global/project chats, other conversation, menu isolation and removed back button");
        }

        private static void CheckComposerOptionMenus(MainWindow controller, DataStore store, string work)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var data = (AppData)typeof(MainWindow).GetField("data", flags).GetValue(controller);
            var window = controller.View;
            var permission = (ComboBox)window.FindName("PermissionBox"); var oldPermission = permission.SelectedItem;
            var effort = (ComboBox)window.FindName("EffortBox"); var oldEffort = effort.SelectedItem;
            var menus = (ComposerOptionMenus)typeof(MainWindow).GetField("optionMenus", flags).GetValue(controller);
            Check(window.FindName("TurnsBox") == null && window.FindName("ConfigPanel") == null && window.FindName("EffortButton") is Button,
                "Legacy max-turn controls remain or effort has no functional button.");
            Check(permission.Items.Count == 6, "The permission menu does not expose all six CLI modes.");
            foreach (var mode in ComposerOptionMenus.Permissions)
            {
                ((Button)window.FindName("ConfigButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var item = menus.PermissionMenu.Items.OfType<MenuItem>().Single(row => (string)row.Tag == mode.Value);
                var header = (Grid)item.Header;
                Check(((StackPanel)header.Children[1]).Children.OfType<TextBlock>().Any(label => label.Text == mode.Description), "Permission mode has no real description.");
                item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(data.Permission == mode.Value && store.Read().Permission == mode.Value && ((TextBlock)window.FindName("PermissionSummary")).Text == mode.Title,
                    "Permission selection did not persist or update the composer label: " + mode.Value);
                var request = (RequestOptions)typeof(MainWindow).GetMethod("ChatOptions", flags).Invoke(controller, new object[] { false });
                var arguments = request.Arguments(); int index = Array.IndexOf(arguments, "--permission-mode");
                Check(index >= 0 && arguments[index + 1] == mode.Value && !arguments.Contains("--max-turns"), "The selected mode did not reach the real chat request or max-turn override remains.");
                menus.ShowPermissions((Button)window.FindName("ConfigButton"));
                var checks = menus.PermissionMenu.Items.OfType<MenuItem>().Select(row => new { Mode = (string)row.Tag, Check = (System.Windows.Shapes.Path)((Grid)row.Header).Children[2] }).ToList();
                Check(checks.Count(row => row.Check.Visibility == Visibility.Visible) == 1 && checks.Single(row => row.Check.Visibility == Visibility.Visible).Mode == mode.Value,
                    "The mode menu checkmark does not match the selected mode.");
            }
            foreach (var level in ComposerOptionMenus.Efforts)
            {
                ((Button)window.FindName("EffortButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                menus.EffortMenu.Items.OfType<MenuItem>().Single(row => (string)row.Tag == level.Value).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Check(data.Effort == level.Value && store.Read().Effort == level.Value && ((TextBlock)window.FindName("ConfigSummary")).Text == level.Title,
                    "Effort selection did not persist or update its button.");
                var request = (RequestOptions)typeof(MainWindow).GetMethod("ChatOptions", flags).Invoke(controller, new object[] { false });
                var arguments = request.Arguments(); int index = Array.IndexOf(arguments, "--effort");
                Check(level.Value.Length == 0 ? index < 0 : index >= 0 && arguments[index + 1] == level.Value, "Effort selection does not match CLI arguments.");
            }
            string migration = Path.Combine(work, "old-max-turns"); Directory.CreateDirectory(migration);
            File.WriteAllText(Path.Combine(migration, "state.json"), "{\"MaxTurns\":\"1\",\"Conversations\":[]}");
            var legacy = new DataStore(migration); legacy.Save(legacy.Read());
            Check(!File.ReadAllText(Path.Combine(migration, "state.json")).Contains("MaxTurns"), "Legacy max-turn configuration is still persisted.");
            permission.SelectedItem = oldPermission; effort.SelectedItem = oldEffort; menus.Close();
            foreach (bool dark in new[] { false, true })
            {
                Theme.Apply(window.Resources, dark);
                menus.ShowPermissions((Button)window.FindName("ConfigButton"));
                var menu = menus.PermissionMenu; menu.Measure(new Size(490, 700)); menu.Arrange(new Rect(0, 0, menu.DesiredSize.Width, menu.DesiredSize.Height)); menu.UpdateLayout();
                Check(menu.ActualHeight > 250 && menu.ActualWidth <= 490, "The permission descriptions overflow or the menu failed to render.");
                string preview = Environment.GetEnvironmentVariable("CLAUDEUI_OPTION_MENU_PREVIEW");
                if (!String.IsNullOrWhiteSpace(preview))
                {
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(menu);
                    var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-permissions-dark.png" : "-permissions-light.png"))) png.Save(file);
                    menus.ShowEfforts((Button)window.FindName("EffortButton"));
                    var levels = menus.EffortMenu; levels.Measure(new Size(490, 700)); levels.Arrange(new Rect(0, 0, levels.DesiredSize.Width, levels.DesiredSize.Height)); levels.UpdateLayout();
                    bitmap = new RenderTargetBitmap((int)Math.Ceiling(levels.ActualWidth), (int)Math.Ceiling(levels.ActualHeight), 96, 96, PixelFormats.Pbgra32); bitmap.Render(levels);
                    png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var file = File.Create(preview + (dark ? "-effort-dark.png" : "-effort-light.png"))) png.Save(file);
                }
            }
            menus.Close(); Theme.Apply(window.Resources, data.Dark);
            Console.WriteLine("PASS composer menus: six permission modes, selected checks, immediate persistence, six effort choices, actual CLI arguments, legacy max-turn migration and native rendering");
        }

        private static void CheckComposerCommandRoutes(MainWindow controller)
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var route = typeof(MainWindow).GetMethod("HandleComposerCommand", flags);
            var data = (AppData)typeof(MainWindow).GetField("data", flags).GetValue(controller);
            var original = (Conversation)typeof(MainWindow).GetField("current", flags).GetValue(controller);
            var window = controller.View;
            var effort = (ComboBox)window.FindName("EffortBox"); var oldEffort = effort.SelectedItem;
            var prompt = (TextBox)window.FindName("PromptBox"); string draft = prompt.Text;
            var menus = (ComposerOptionMenus)typeof(MainWindow).GetField("optionMenus", flags).GetValue(controller);
            int history = data.Conversations.Count;
            Check(((System.Threading.Tasks.Task<bool>)route.Invoke(controller, new object[] { "/permissions" })).GetAwaiter().GetResult() && menus.PermissionMenu != null,
                "The permissions command did not open the real parameter controls.");
            Check(((System.Threading.Tasks.Task<bool>)route.Invoke(controller, new object[] { "/effort high" })).GetAwaiter().GetResult() && effort.SelectedItem.ToString() == "高" && data.Effort == "high",
                "The effort command did not update the persisted CLI option.");
            Check(!((System.Threading.Tasks.Task<bool>)route.Invoke(controller, new object[] { "/compact" })).GetAwaiter().GetResult() &&
                !((System.Threading.Tasks.Task<bool>)route.Invoke(controller, new object[] { "/custom-skill arguments" })).GetAwaiter().GetResult(),
                "CLI-owned commands were intercepted instead of being passed through.");
            prompt.Text = "/new";
            Check(((System.Threading.Tasks.Task<bool>)route.Invoke(controller, new object[] { "/new" })).GetAwaiter().GetResult() &&
                typeof(MainWindow).GetField("current", flags).GetValue(controller) != original && data.Conversations.Count == history && original.Draft != "/new",
                "The new-chat command removed history or left the command in the old draft.");
            effort.SelectedItem = oldEffort;
            typeof(MainWindow).GetMethod("CaptureOptions", flags).Invoke(controller, null);
            typeof(MainWindow).GetMethod("Activate", flags).Invoke(controller, new object[] { original });
            prompt.Text = draft; original.Draft = draft; menus.Close();
            typeof(MainWindow).GetMethod("Save", flags).Invoke(controller, null);
            Console.WriteLine("PASS composer command routing: real permissions, persisted effort, CLI passthrough, new conversation and retained history");
        }

        private static void CheckPromptCompletions(string work)
        {
            string folder = Path.Combine(work, "completion-project"), config = Path.Combine(work, "completion-config");
            Directory.CreateDirectory(Path.Combine(folder, ".git")); Directory.CreateDirectory(config);
            Directory.CreateDirectory(Path.Combine(folder, "src")); Directory.CreateDirectory(Path.Combine(folder, "node_modules"));
            File.WriteAllText(Path.Combine(folder, "src", "My File.cs"), "fixture");
            File.WriteAllText(Path.Combine(folder, "src", "入口.cs"), "fixture");
            File.WriteAllText(Path.Combine(folder, "node_modules", "ignored.cs"), "fixture");
            string skill = Path.Combine(folder, ".claude", "skills", "test-helper"), hidden = Path.Combine(folder, ".claude", "skills", "test-hidden");
            Directory.CreateDirectory(skill); Directory.CreateDirectory(hidden);
            File.WriteAllText(Path.Combine(skill, "SKILL.md"), "---\nname: test-helper\ndescription: fixture skill\n---\nFixture");
            File.WriteAllText(Path.Combine(hidden, "SKILL.md"), "---\nname: test-hidden\nuser-invocable: false\n---\nFixture");
            string custom = Path.Combine(folder, ".claude", "commands", "notes"); Directory.CreateDirectory(custom); File.WriteAllText(Path.Combine(custom, "explain.md"), "Fixture");
            var commands = PromptCompletionCatalog.Commands(folder, config, CancellationToken.None);
            Check(commands.Any(item => item.Title == "/compact") && commands.Any(item => item.Title == "/test-helper") && commands.Any(item => item.Title == "/notes:explain") && !commands.Any(item => item.Title == "/test-hidden"),
                "Command completion lost built-ins, installed skills, nested custom commands or user-invocable restrictions.");
            var files = PromptCompletionCatalog.Files(folder, "My File", CancellationToken.None);
            Check(files.Count == 1 && files[0].Insert == "@\"src/My File.cs\" " && files[0].Description == "src/My File.cs", "Spaced filenames were not quoted as relative file references.");
            Check(!PromptCompletionCatalog.Files(folder, "ignored", CancellationToken.None).Any() && PromptCompletionCatalog.Files(folder, "入口", CancellationToken.None).Count == 1,
                "File completion included dependency trees or lost Unicode paths.");
            Check(PromptCompletionCatalog.Token("email@example.com", 17) == null && PromptCompletionCatalog.Token("https://example.test", 20) == null && PromptCompletionCatalog.Token("/model sonnet", 13) == null,
                "Completion activated for an email, URL or command argument.");
            var token = PromptCompletionCatalog.Token("请检查 @src/My", 11);
            Check(token != null && token.File && token.Query == "src/My" && token.Start == 4, "File completion token boundaries were incorrect.");
            Window window;
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("ClaudeUI.MainWindow.xaml")) window = (Window)System.Windows.Markup.XamlReader.Load(stream);
            Theme.Apply(window.Resources, true);
            var prompt = (TextBox)window.FindName("PromptBox");
            using (var completion = new PromptCompletions(prompt, (Border)window.FindName("ComposerSurface"), () => folder, () => config, () => true, window.Resources))
            {
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var list = (ListBox)typeof(PromptCompletions).GetField("list", flags).GetValue(completion);
                prompt.Text = "请检查 @src/My 之后说明"; prompt.CaretIndex = "请检查 @src/My".Length;
                list.ItemsSource = files; list.SelectedIndex = 0; completion.Accept();
                Check(prompt.Text == "请检查 @\"src/My File.cs\"  之后说明", "File completion replaced text outside the active mention.");
                prompt.Text = "/com"; prompt.CaretIndex = prompt.Text.Length;
                list.ItemsSource = commands; list.SelectedItem = commands.First(item => item.Title == "/compact"); completion.Accept();
                Check(prompt.Text == "/compact ", "Command completion did not insert the selected command.");
                var popup = (System.Windows.Controls.Primitives.Popup)typeof(PromptCompletions).GetField("popup", flags).GetValue(completion);
                Check(((Border)popup.Child).Background != null && !popup.IsOpen, "Completion popup lost its theme resources or stayed open after accepting.");
            }
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel(); bool stopped = false;
                try { PromptCompletionCatalog.Files(folder, "", cancellation.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "File completion ignored cancellation.");
            }
            Console.WriteLine("PASS composer completion: slash commands, skills, legacy commands, hidden skills, Unicode/spaced references, token boundaries, dependency filtering, insertion and cancellation");
        }

        private static void CheckNativeDeveloperVisuals()
        {
            foreach (string name in new[] { "terminal", "badge-info", "stethoscope", "shield-check", "blocks", "plug", "list-todo", "circle-help", "folder-open", "search", "eye", "arrow-right", "sparkles", "copy", "activity", "arrow-down-left", "arrow-up-right", "wallet" })
            {
                var icon = (Viewbox)DeveloperVisuals.Icon(name);
                var canvas = (Canvas)icon.Child;
                var path = (System.Windows.Shapes.Path)canvas.Children[0];
                Rect bounds = path.Data.Bounds;
                Check(!bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -.01 && bounds.Top >= -.01 && bounds.Right <= 24.01 && bounds.Bottom <= 24.01,
                    "Lucide SVG subpaths escaped the original viewbox: " + name + " " + bounds);
            }
            var panel = new ResponsiveCardPanel();
            for (int i = 0; i < 3; i++) panel.Children.Add(new Border { Height = 120 + i * 20 });
            foreach (int width in new[] { 984, 524 })
            {
                panel.Measure(new Size(width, Double.PositiveInfinity)); panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height)); panel.UpdateLayout();
                Rect first = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)panel.Children[0]), second = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutSlot((FrameworkElement)panel.Children[1]);
                Check(width == 984 ? second.X > first.X && second.Y == first.Y : second.X == first.X && second.Y > first.Y,
                    "Marketplace cards did not reflow between desktop and compact widths.");
                foreach (FrameworkElement child in panel.Children)
                    Check(child.TranslatePoint(new Point(child.ActualWidth, 0), panel).X <= width + .1, "A native market card overflowed horizontally.");
            }
            Window window;
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("ClaudeUI.MainWindow.xaml")) window = (Window)System.Windows.Markup.XamlReader.Load(stream);
            Theme.Apply(window.Resources, true);
            var date = (DatePicker)window.FindName("StatsFromFilter"); date.ApplyTemplate();
            var popup = (System.Windows.Controls.Primitives.Popup)date.Template.FindName("PART_Popup", date);
            var calendar = popup.Child as Calendar;
            Check(calendar != null && date.Template.FindName("PART_Button", date) is Button, "The themed date picker lost its native calendar parts.");
            var value = new DateTime(2026, 11, 22); date.SelectedDate = value;
            Check(calendar.SelectedDate == value, "The themed date picker no longer synchronizes its calendar.");
            calendar.SelectedDate = value.AddDays(-1);
            Check(date.SelectedDate == value.AddDays(-1), "Calendar selection no longer updates the date filter.");
            Console.WriteLine("PASS native design: Lucide geometry bounds, responsive card reflow, no horizontal overflow, themed calendar selection");
        }

        private static void CheckThemeTransitions()
        {
            Window window;
            using (var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("ClaudeUI.MainWindow.xaml"))
                window = (Window)System.Windows.Markup.XamlReader.Load(stream);
            window.ShowInTaskbar = false; window.ShowActivated = false;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Left = -30000; window.Top = -30000;
            window.Show();
            Theme.Apply(window.Resources, false);
            var button = (Button)window.FindName("ThemeButton");
            ThemeToggleIcon.Update(button, false, false);
            var knob = (Border)button.Template.FindName("Knob", button);
            var sun = (Image)button.Template.FindName("SunIcon", button);
            var moon = (Image)button.Template.FindName("MoonIcon", button);
            var position = (TranslateTransform)knob.RenderTransform;
            var brush = (SolidColorBrush)window.Resources["Canvas"];
            Color light = brush.Color;
            var target = new ResourceDictionary(); Theme.Apply(target, true);
            Color dark = ((SolidColorBrush)target["Canvas"]).Color;
            Check(position.X == 31 && sun.Opacity == 1 && moon.Opacity == 0, "Initial theme icon is incorrect.");
            Theme.Animate(window.Resources, true); ThemeToggleIcon.Update(button, true, true);
            if (SystemParameters.ClientAreaAnimation)
            {
                PumpThemeAnimation(150);
                Check(brush.Color != light && brush.Color != dark, "Theme colors jumped instead of interpolating: " + brush.Color + ", animated=" + brush.HasAnimatedProperties + ", frozen=" + brush.IsFrozen);
                Check(position.X > 0 && position.X < 31 && sun.Opacity > 0 && sun.Opacity < 1 && moon.Opacity > 0 && moon.Opacity < 1,
                    "Theme icon did not slide and crossfade.");
                Color intermediate = brush.Color; double offset = position.X;
                Theme.Animate(window.Resources, false); ThemeToggleIcon.Update(button, false, true);
                PumpThemeAnimation(25);
                Check(Math.Abs(brush.Color.R - intermediate.R) < 20 && Math.Abs(position.X - offset) < 4 && brush.Color != light,
                    "Reversing the theme animation jumped.");
                PumpThemeAnimation(500);
                Check(brush.Color == light && position.X == 31 && sun.Opacity == 1 && moon.Opacity == 0,
                    "Interrupted theme animation did not finish in light mode.");
                for (int i = 0; i < 12; i++)
                {
                    bool next = i % 2 == 0;
                    Theme.Animate(window.Resources, next); ThemeToggleIcon.Update(button, next, true);
                }
                PumpThemeAnimation(500);
                Check(brush.Color == light && position.X == 31, "Rapid theme toggles did not retain the latest target.");
                Theme.Animate(window.Resources, true); ThemeToggleIcon.Update(button, true, true);
                PumpThemeAnimation(500);
            }
            Check(Object.ReferenceEquals(brush, window.Resources["Canvas"]) && brush.Color == dark && position.X == 0 && moon.Opacity == 1 && sun.Opacity == 0,
                "Theme animation did not settle or preserve shared brush references.");
            Theme.Apply(window.Resources, false);
            Check(brush.Color == light && !brush.HasAnimatedProperties, "Immediate theme apply did not clear animation clocks.");
            window.Close();
            Console.WriteLine("PASS theme transitions: intermediate colors, icon slide/crossfade, reversal continuity, rapid toggles, final colors and shared brushes");
        }

        private static void PumpThemeAnimation(int milliseconds)
        {
            var frame = new System.Windows.Threading.DispatcherFrame();
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += delegate { timer.Stop(); frame.Continue = false; };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
        }

        private static void CheckPortableData(string work)
        {
            string root = Path.Combine(work, "portable-app");
            string legacy = Path.Combine(work, "legacy-app-data");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(legacy, "attachments"));
            string image = Path.Combine(legacy, "attachments", "截图.png");
            File.WriteAllBytes(image, new byte[] { 1, 2, 3 });
            var conversation = new Conversation { Title = "迁移测试", Workspace = work,
                DraftAttachments = new List<ChatAttachment> { new ChatAttachment { Name = "截图.png", LocalPath = image } } };
            conversation.Messages.Add(new StoredMessage { Role = "user", Text = "历史文字保持原样", Attachments =
                new List<ChatAttachment> { new ChatAttachment { Name = "截图.png", LocalPath = image } } });
            var data = new AppData { Dark = true, Workspace = work };
            data.Conversations.Add(conversation);
            new DataStore(legacy).Save(data);
            File.WriteAllText(Path.Combine(legacy, "request-statistics.jsonl"), "statistics fixture\n");
            string target = PortableData.Prepare(root, legacy);
            var migrated = new DataStore(target).Read();
            string expected = Path.Combine(root, "Data", "attachments", "截图.png");
            Check(target == Path.Combine(root, "Data") && migrated.Dark && migrated.Conversations.Single().Title == "迁移测试",
                "Portable data location or settings changed during migration.");
            Check(migrated.Conversations[0].DraftAttachments[0].LocalPath == expected &&
                migrated.Conversations[0].Messages[0].Attachments[0].LocalPath == expected &&
                File.ReadAllBytes(expected).SequenceEqual(new byte[] { 1, 2, 3 }), "Attachment copies or migrated paths are incorrect.");
            Check(File.Exists(image) && File.Exists(Path.Combine(legacy, "state.json")) &&
                File.ReadAllText(Path.Combine(target, "request-statistics.jsonl")) == "statistics fixture\n", "Migration lost legacy backup or statistics.");
            File.WriteAllText(Path.Combine(legacy, "state.json"), "invalid changed legacy");
            Check(PortableData.Prepare(root, legacy) == target && new DataStore(target).Read().Dark,
                "Existing portable data was overwritten on restart.");
            string fresh = PortableData.Prepare(Path.Combine(work, "fresh-app"), Path.Combine(work, "no-legacy"));
            Check(Directory.Exists(fresh), "Fresh installation did not use local Data.");
            string conflict = Path.Combine(work, "conflicting-app");
            Directory.CreateDirectory(Path.Combine(conflict, "Data"));
            File.WriteAllText(Path.Combine(conflict, "Data", "keep.txt"), "keep");
            bool rejected = false;
            try { PortableData.Prepare(conflict, legacy); } catch (IOException) { rejected = true; }
            Check(rejected && File.ReadAllText(Path.Combine(conflict, "Data", "keep.txt")) == "keep", "Migration overwrote a conflicting directory.");
            Console.WriteLine("PASS portable data: local directory, migration, attachment rebasing, statistics copy, legacy backup, restart idempotence, fresh setup, conflict preservation");
        }

        private static void CheckAttachments(MainWindow controller, DataStore store, string work)
        {
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            var kind = typeof(MainWindow);
            string file = Path.Combine(work, "中文 附件.txt");
            File.WriteAllText(file, "attachment fixture");
            kind.GetMethod("AddAttachments", flags).Invoke(controller, new object[] { new[] { file } });
            var current = (Conversation)kind.GetField("current", flags).GetValue(controller);
            var attachment = current.DraftAttachments.Single();
            File.WriteAllText(file, "changed source");
            Check(File.ReadAllText(attachment.LocalPath) == "attachment fixture", "Attachment was not a stable local copy.");
            var pixels = new byte[] { 0, 0, 255, 255 };
            var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4);
            var clipboard = new DataObject(); clipboard.SetData(DataFormats.Bitmap, image);
            var pasteTarget = new TextBox();
            bool pasteBlocked = false;
            int pasteCalls = 0;
            IDataObject clipboardData = clipboard;
            AttachmentPaste.Attach(pasteTarget, () => clipboardData, () => pasteBlocked || pasteTarget.IsReadOnly,
                data => {
                    pasteCalls++;
                    kind.GetMethod("PasteAttachmentData", flags).Invoke(controller, new object[] { data });
                }, error => { throw new Exception(error); });
            Check(!clipboard.GetDataPresent(DataFormats.UnicodeText) && ApplicationCommands.Paste.CanExecute(null, pasteTarget),
                "Image-only clipboard did not enable the routed Paste command.");
            ApplicationCommands.Paste.Execute(null, pasteTarget);
            Check(pasteCalls == 1 && current.DraftAttachments.Count == 2 && current.DraftAttachments[1].MediaType == "image/png",
                "Routed Paste did not create exactly one PNG attachment.");
            pasteBlocked = true;
            Check(!ApplicationCommands.Paste.CanExecute(null, pasteTarget), "Busy composer accepted Paste.");
            ApplicationCommands.Paste.Execute(null, pasteTarget);
            Check(pasteCalls == 1, "Blocked Paste created an attachment.");
            pasteBlocked = false; pasteTarget.IsReadOnly = true;
            Check(!ApplicationCommands.Paste.CanExecute(null, pasteTarget), "Read-only composer accepted Paste.");
            pasteTarget.IsReadOnly = false;
            clipboardData = new DataObject(DataFormats.UnicodeText, "普通文字");
            ApplicationCommands.Paste.Execute(null, pasteTarget);
            Check(pasteCalls == 1 && !AttachmentPaste.HasAttachment(clipboardData), "Text paste was intercepted as an attachment.");
            var copiedFiles = new DataObject(); copiedFiles.SetData(DataFormats.FileDrop, new[] { file });
            clipboardData = copiedFiles;
            Check(ApplicationCommands.Paste.CanExecute(null, pasteTarget), "File-only clipboard did not enable Paste.");
            ApplicationCommands.Paste.Execute(null, pasteTarget);
            Check(pasteCalls == 2 && current.DraftAttachments.Count == 3, "File paste did not route to attachment import.");
            current.DraftAttachments.RemoveAt(2);
            kind.GetMethod("RenderAttachments", flags).Invoke(controller, null);
            store.Save((AppData)kind.GetField("data", flags).GetValue(controller));
            Console.WriteLine("PASS routed attachment Paste: image-only/file-only CanExecute, command execution, exactly once, busy/read-only guards, text fallback");
            string prompt = AttachmentFiles.Prompt("检查附件", current.DraftAttachments);
            object content = AttachmentFiles.Content(prompt, current.DraftAttachments);
            byte[] wire;
            using (var input = new MemoryStream())
            using (var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes("{\"type\":\"result\"}\n"))))
            {
                new CliPermissionChannel(input, (request, token) => System.Threading.Tasks.Task.FromResult(false))
                    .ReadAsync(reader, prompt, (line, error) => {}, CancellationToken.None, content).GetAwaiter().GetResult();
                wire = input.ToArray();
            }
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            var envelope = json.DeserializeObject(Encoding.UTF8.GetString(wire)) as IDictionary<string, object>;
            var parts = (object[])((IDictionary<string, object>)envelope["message"])["content"];
            var source = (IDictionary<string, object>)((IDictionary<string, object>)parts[1])["source"];
            Check(parts.Length == 2 && (string)source["media_type"] == "image/png" &&
                Convert.FromBase64String((string)source["data"]).SequenceEqual(File.ReadAllBytes(current.DraftAttachments[1].LocalPath)),
                "Image bytes were not delivered as native stream-json content.");
            Check(store.Read().Conversations.Any(c => c.Id == current.Id && c.DraftAttachments.Count == 2), "Attachment draft did not persist.");
            var newConversation = (Conversation)kind.GetMethod("NewConversation", flags).Invoke(controller, null);
            kind.GetMethod("Activate", flags).Invoke(controller, new object[] { newConversation });
            Check(newConversation.DraftAttachments.Count == 0, "Attachments leaked into another conversation.");
            kind.GetMethod("Activate", flags).Invoke(controller, new object[] { current });
            var panel = (WrapPanel)controller.View.FindName("AttachmentList");
            Check(panel.Children.Count == 2 && VisualChildren<Image>(panel).Any(), "Restored attachment preview was missing.");
            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_ATTACHMENT_PREVIEW");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                var root = (FrameworkElement)controller.View.Content;
                root.SetResourceReference(Panel.BackgroundProperty, "Canvas");
                foreach (bool dark in new[] { false, true })
                {
                    Theme.Apply(controller.View.Resources, dark);
                    root.Measure(new Size(1280, 900)); root.Arrange(new Rect(0, 0, 1280, 900)); root.UpdateLayout();
                    var bitmap = new RenderTargetBitmap(1280, 900, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (var output = File.Create(preview + (dark ? "-dark.png" : "-light.png"))) encoder.Save(output);
                }
            }
            VisualChildren<Button>(panel).First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(current.DraftAttachments.Count == 1 && File.Exists(attachment.LocalPath), "Removing a draft damaged a stored attachment.");
            current.DraftAttachments.Clear();
            kind.GetMethod("RenderAttachments", flags).Invoke(controller, null);
            string large = Path.Combine(work, "large.bin");
            using (var stream = File.Create(large)) stream.SetLength(AttachmentFiles.MaxFileSize + 1);
            bool rejected = false;
            try { AttachmentFiles.Import(large, store.AttachmentDirectory); } catch (IOException) { rejected = true; }
            Check(rejected, "Oversized attachment was accepted.");
            Console.WriteLine("PASS attachments: stable copies, screenshot paste, preview/remove, conversation isolation, persistence, size limits, native image wire payload");
        }

        private static IEnumerable<T> VisualChildren<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                var match = child as T;
                if (match != null) yield return match;
                foreach (var nested in VisualChildren<T>(child)) yield return nested;
            }
        }

        private static void CheckMarkdownReply()
        {
            string first = "## 标题\r\n**重点*";
            string second = "* 与 _说明_\r\n- **使用 `a**b` 管道**\r\n";
            string third = "``";
            string fourth = "`cs\r\n## literal **code**\r\n```\r\n\\*literal\\* a_b_c";
            var reply = new MarkdownReply(first);
            var committedHeading = (Paragraph)reply.Document.Blocks.FirstBlock;
            reply.AppendMarkdown(second);
            reply.AppendMarkdown(third);
            reply.AppendMarkdown(fourth);
            var blocks = reply.Document.Blocks.Cast<Paragraph>().ToArray();
            Check(blocks.Length == 5 && blocks[0] == committedHeading && blocks[0].FontWeight == FontWeights.SemiBold,
                "A streamed heading was not formatted or was replaced by a later chunk.");
            Check(blocks[1].Inlines.OfType<Span>().Any(span => span.FontWeight == FontWeights.Bold) &&
                blocks[1].Inlines.OfType<Span>().Any(span => span.FontStyle == FontStyles.Italic),
                "Streamed emphasis was not formatted.");
            string visible = new TextRange(reply.Document.ContentStart, reply.Document.ContentEnd).Text;
            Check(visible.Contains("标题") && visible.Contains("重点 与 说明") && visible.Contains("• 使用 a**b 管道") &&
                visible.Contains("## literal **code**") && visible.Contains("*literal* a_b_c") && !visible.Contains("```"),
                "The rendered response leaked Markdown markers or changed literal code.");
            Check(reply.Markdown == first + second + third + fourth, "Rendering changed the stored model response.");
            var restored = new MarkdownReply(reply.Markdown);
            Check(new TextRange(restored.Document.ContentStart, restored.Document.ContentEnd).Text == visible,
                "Restored and streamed Markdown were rendered differently.");
            Console.WriteLine("PASS assistant Markdown formatting, streaming, and literal code");
        }

        private static void CheckMarkdownTables()
        {
            const string sample = "| 类别 | 具体能力 |\n| :--- | --- |\n| **写代码** | 新建/修改文件、实现功能、重构、修 bug |\n| 读代码 | 搜索全项目、解释代码逻辑、追踪调用链 |\n| 调试 | 跑命令、看报错、定位并修复问题 |\n| 运行验证 | 执行测试、启动应用、验证改动是否生效 |\n| 工程操作 | git 提交/建分支、装依赖、跑构建（需要你授权） |\n| 其他 | 查文档/网页、写脚本、数据可视化、安全审查 |";
            var reply = new MarkdownReply(sample);
            var table = reply.Document.Blocks.OfType<Table>().Single();
            Check(table.Columns.Count == 2 && table.RowGroups[0].Rows.Count == 7, "Markdown table dimensions were incorrect.");
            Check(table.RowGroups[0].Rows[0].Cells[0].FontWeight == FontWeights.SemiBold &&
                ((Paragraph)table.RowGroups[0].Rows[1].Cells[1].Blocks.FirstBlock).TextAlignment == TextAlignment.Left,
                "Table headers or alignment markers were ignored.");
            string visible = new TextRange(reply.Document.ContentStart, reply.Document.ContentEnd).Text;
            Check(visible.Contains("写代码") && !visible.Contains("**写代码**") && !visible.Contains("---"), "Table cells were not formatted or separator text leaked.");
            Check(reply.Markdown == sample, "Table rendering changed the stored Markdown.");

            var streamed = new MarkdownReply("");
            foreach (char c in sample) streamed.AppendMarkdown(c.ToString());
            Check(streamed.Document.Blocks.OfType<Table>().Count() == 1 &&
                new TextRange(streamed.Document.ContentStart, streamed.Document.ContentEnd).Text == visible && streamed.Markdown == sample,
                "Character-by-character streaming duplicated rows or differed from restored tables.");
            streamed.AppendMarkdown("\n\n表格后的文字。");
            Check(streamed.Document.Blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 7 &&
                streamed.Document.Blocks.LastBlock is Paragraph, "The table consumed following prose.");
            var partialDivider = new MarkdownReply("| a | b |\n| --- | --- |");
            Check(partialDivider.Document.Blocks.OfType<Table>().Count() == 1, "A separator without a final newline was not rendered.");
            partialDivider.AppendMarkdown("\n| 1 | 2 |");
            Check(partialDivider.Document.Blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 2, "Preview separator rollback lost the header.");
            var escaped = new MarkdownReply("a | b\n:---: | ---:\n`x|y` | escaped\\|pipe\nshort |\n");
            var escapedTable = escaped.Document.Blocks.OfType<Table>().Single();
            Check(((Paragraph)escapedTable.RowGroups[0].Rows[0].Cells[0].Blocks.FirstBlock).TextAlignment == TextAlignment.Center &&
                ((Paragraph)escapedTable.RowGroups[0].Rows[0].Cells[1].Blocks.FirstBlock).TextAlignment == TextAlignment.Right,
                "Center or right-aligned column markers were ignored.");
            string escapedText = new TextRange(escapedTable.ContentStart, escapedTable.ContentEnd).Text;
            Check(escapedText.Contains("x|y") && escapedText.Contains("escaped|pipe") && escapedTable.RowGroups[0].Rows.Last().Cells.Count == 2,
                "Escaped pipes, inline code, or short rows broke cell boundaries.");
            var literal = new MarkdownReply("```text\n| a | b |\n| --- | --- |\n| 1 | 2 |\n```\n\n| a | b |\n| -- | --- |\n");
            Check(!literal.Document.Blocks.OfType<Table>().Any(), "Code fences or invalid separators were converted into tables.");
            var extra = new MarkdownReply("| a | b |\n| --- | --- |\n| 1 | 2 | 3 |\n");
            Check(new TextRange(extra.Document.ContentStart, extra.Document.ContentEnd).Text.Contains("3"), "Extra columns silently lost text.");

            var output = new RichTextBox();
            var cli = new CliOutputView(output);
            foreach (string line in sample.Split('\n')) cli.Append(line);
            cli.Flush();
            Check(output.Document.Blocks.OfType<Table>().Single().RowGroups[0].Rows.Count == 7, "CLI formatted results did not render pipe tables.");
            cli.Clear();
            cli.Append("```text\n| a | b |\n| --- | --- |\n| 1 | 2 |\n```");
            Check(!output.Document.Blocks.OfType<Table>().Any(), "CLI code fences were formatted as tables.");

            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_TABLE_PREVIEW_PATH");
            foreach (bool dark in new[] { false, true })
            {
                Theme.Apply(reply.Resources, dark);
                reply.FontSize = 15;
                reply.Foreground = (Brush)reply.Resources["Ink"];
                foreach (int width in new[] { 760, 460 })
                {
                    reply.Width = width;
                    reply.Height = 570;
                    reply.Measure(new Size(width, 570));
                    reply.Arrange(new Rect(0, 0, width, 570));
                    reply.UpdateLayout();
                    double columns = table.Columns.Cast<TableColumn>().Sum(column => column.Width.Value);
                    Check(columns <= width && columns > width - 25, "Table columns did not fit the available width.");
                    if (!String.IsNullOrWhiteSpace(preview))
                    {
                        var visual = new DrawingVisual();
                        using (var dc = visual.RenderOpen())
                        {
                            dc.DrawRectangle((Brush)reply.Resources["Canvas"], null, new Rect(0, 0, width + 40, 610));
                            dc.DrawRectangle(new VisualBrush(reply), null, new Rect(20, 20, width, 570));
                        }
                        var bitmap = new RenderTargetBitmap(width + 40, 610, 96, 96, PixelFormats.Pbgra32);
                        bitmap.Render(visual);
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using (var file = File.Create(preview + (dark ? "-dark-" : "-light-") + width + ".png")) encoder.Save(file);
                    }
                }
            }
            Console.WriteLine("PASS local Markdown tables, streaming, escaping, code fences, CLI output, alignment, and responsive widths");
        }

        private static void CheckSessionMenu(MainWindow window, Conversation conversation)
        {
            var menu = window.CreateSessionMenu(conversation);
            menu.Measure(new Size(260, 320));
            menu.Arrange(new Rect(menu.DesiredSize));
            menu.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth), (int)Math.Ceiling(menu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(menu);
            var expected = ((SolidColorBrush)window.View.Resources["Panel"]).Color;
            var sample = new byte[4];
            foreach (int y in new[] { 18, 45, 70 })
            {
                bitmap.CopyPixels(new Int32Rect(8, y, 1, 1), sample, 4, 0);
                Check(sample[0] == expected.B && sample[1] == expected.G && sample[2] == expected.R && sample[3] == 255,
                    "The session menu gutter does not match the themed surface.");
            }
            var firstItem = (MenuItem)menu.Items[0];
            Check(firstItem.ActualHeight > 0 && (string)firstItem.Header == "压缩上下文" && menu.Items.Count == 7 &&
                menu.Items.OfType<MenuItem>().All(item => item.Icon is System.Windows.Shapes.Path) &&
                !menu.Items.OfType<MenuItem>().Any(item => (string)item.Header == "继续对话"),
                "Session actions are missing from the rendered menu.");
            string sessionPreview = Environment.GetEnvironmentVariable("CLAUDEUI_SESSION_MENU_PREVIEW_PATH");
            if (!String.IsNullOrWhiteSpace(sessionPreview))
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var file = File.Create(sessionPreview + (expected.R < 128 ? "-dark.png" : "-light.png"))) encoder.Save(file);
            }
            var projectMenu = window.CreateProjectMenu(conversation.Workspace);
            projectMenu.Measure(new Size(260, 320));
            projectMenu.Arrange(new Rect(projectMenu.DesiredSize));
            projectMenu.UpdateLayout();
            Check(projectMenu.Items.OfType<MenuItem>().All(item => item.ActualHeight > 0 && item.Icon is System.Windows.Shapes.Path && ((System.Windows.Shapes.Path)item.Icon).ActualWidth > 0),
                "Project menu actions or icons did not render.");
            string preview = Environment.GetEnvironmentVariable("CLAUDEUI_PROJECT_MENU_PREVIEW_PATH");
            if (!String.IsNullOrWhiteSpace(preview))
            {
                var projectBitmap = new RenderTargetBitmap((int)Math.Ceiling(projectMenu.ActualWidth), (int)Math.Ceiling(projectMenu.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                projectBitmap.Render(projectMenu);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(projectBitmap));
                using (var file = File.Create(preview + (expected.R < 128 ? "-dark.png" : "-light.png"))) encoder.Save(file);
            }
        }

        private static ClaudeStream EditSession(CliRunner runner, CliInstallation cli, string work, SessionEdit edit, CancellationToken token)
        {
            var response = new ClaudeStream();
            int code = runner.RunAsync(cli, work, edit.Arguments(), edit.Input,
                (line, error) => { if (!error) response.Read(line); }, token).GetAwaiter().GetResult();
            Check(code == 0 && edit.Validate(response) == null, "Session edit did not succeed (" + code + ").");
            return response;
        }

        private static ClaudeStream LiveRun(CliRunner runner, CliInstallation cli, string work,
            RequestOptions options, string[] testArguments, string prompt, string expected, CancellationToken token)
        {
            var response = new ClaudeStream();
            var received = new StringBuilder();
            DateTime started = DateTime.UtcNow;
            int code = runner.RunAsync(cli, work, options.Arguments().Concat(testArguments), prompt,
                (line, error) => { if (!error) received.Append(response.Read(line)); }, token).GetAwaiter().GetResult();
            Check(code == 0 && response.Finished && response.Failure == null, "Live Claude CLI command failed (" + code + ").");
            string text = received.Length > 0 ? received.ToString() : response.Result ?? "";
            Check(text.Trim() == expected, "Claude CLI returned an unexpected result.");
            var usage = response.Statistics(started, GatewayRoute.Resolve(work, null), response.SessionId, options.Model, null, false, false);
            Check(usage.Any(row => !row.ExecutionOnly && row.Input.HasValue && row.Output > 0 && row.Status == "成功" && row.DurationMs.HasValue && row.RequestId != null),
                "Live model usage was not captured at request level.");
            foreach (var row in usage) RequestPricing.Apply(row, new RequestRate[0]);
            var statsStore = new StatisticsStore(work);
            statsStore.Append(usage);
            Check(statsStore.Read().Any(row => row.RunId == usage[0].RunId), "Live request statistics did not persist.");
            Console.WriteLine("PASS live request usage capture, timing, status, and JSONL persistence");
            return response;
        }

        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
    }
}
