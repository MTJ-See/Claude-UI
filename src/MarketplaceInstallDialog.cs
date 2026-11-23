using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeUI
{
    internal sealed class MarketplaceInstallDialog
    {
        private readonly Window dialog;
        private readonly MarketplaceItem item;
        private readonly MarketplaceInstallContext context;
        private readonly MarketplaceCredentials credentials;
        private readonly ComboBox scope = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
        private readonly ComboBox method = new ComboBox { Margin = new Thickness(0, 0, 0, 14) };
        private readonly TextBox name = new TextBox { Margin = new Thickness(0, 0, 0, 14) };
        private readonly TextBlock destination = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        private readonly TextBox logs = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 170, Margin = new Thickness(0, 10, 0, 8) };
        private readonly Dictionary<string, Control> inputs = new Dictionary<string, Control>();
        private readonly PasswordBox accessToken = new PasswordBox();
        private readonly Button start = new Button { Content = "确认安装", Padding = new Thickness(18, 8, 18, 8), Margin = new Thickness(0, 0, 10, 0) };
        private readonly Button stop = new Button { Content = "关闭", Padding = new Thickness(18, 8, 18, 8) };
        private readonly CheckBox consent = new CheckBox { Content = "已了解安装方式和权限，确认安装", Margin = new Thickness(0, 12, 0, 12) };
        private CancellationTokenSource cancellation;
        private bool running, succeeded;
        public MarketplaceInstallDialog(Window owner, MarketplaceItem item, MarketplaceInstallContext context, MarketplaceCredentials credentials)
        {
            this.item = item; this.context = context; this.credentials = credentials;
            dialog = new Window { Owner = owner, Title = "安装 · " + item.Name, Width = 680, Height = Math.Min(810, SystemParameters.WorkArea.Height - 35), MinHeight = 540, WindowStartupLocation = WindowStartupLocation.CenterOwner, Resources = owner.Resources };
            dialog.SetResourceReference(Window.BackgroundProperty, "Canvas"); dialog.SetResourceReference(Window.ForegroundProperty, "Ink");
            DialogVisuals.Apply(dialog);
            consent.SetResourceReference(Control.ForegroundProperty, "Ink");
            logs.SetResourceReference(Control.ForegroundProperty, "Ink");
            accessToken.SetResourceReference(Control.ForegroundProperty, "Ink");
            accessToken.SetResourceReference(Control.BackgroundProperty, "Composer");
            accessToken.SetResourceReference(Control.BorderBrushProperty, "Line");
            var panel = new StackPanel { Margin = new Thickness(26) };
            dialog.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            panel.Children.Add(MarketplaceDetailView.Label("安装到 Claude", 23));
            panel.Children.Add(MarketplaceDetailView.Label(item.Kind == MarketplaceKind.Skill ? "使用魔搭官方发布包，安装到 Claude skills 目录。" : "使用 Claude CLI 官方 MCP 配置命令。stdio 依赖由服务启动器按需获取。", 13, "MutedInk"));
            panel.Children.Add(MarketplaceDetailView.Label("安装名称", 12, "MutedInk"));
            try { name.Text = MarketplaceInstaller.SkillName(item.Id); } catch (InvalidOperationException) { name.Text = "my-extension"; }
            panel.Children.Add(name);
            panel.Children.Add(MarketplaceDetailView.Label("安装范围", 12, "MutedInk"));
            scope.Items.Add("用户级 · 所有项目"); scope.Items.Add("项目级 · 当前项目"); scope.SelectedIndex = 0; panel.Children.Add(scope);
            panel.Children.Add(destination);
            scope.SelectionChanged += delegate { Target(); }; name.TextChanged += delegate { Target(); };
            if (item.Kind == MarketplaceKind.Mcp)
            {
                panel.Children.Add(MarketplaceDetailView.Label("官方安装方式", 12, "MutedInk"));
                foreach (var option in item.McpOptions) method.Items.Add(option);
                if (item.Hosted) method.Items.Add(new McpInstallOption { Name = "部署到魔搭并连接" });
                if (method.Items.Count > 0) method.SelectedIndex = 0;
                panel.Children.Add(method);
                method.SelectionChanged += delegate { Target(); };
            }
            foreach (var parameter in item.Parameters)
            {
                panel.Children.Add(MarketplaceDetailView.Label(parameter.Name + (parameter.Required ? " · 必填" : " · 可选"), 13));
                panel.Children.Add(MarketplaceDetailView.Label(parameter.Description, 12, "MutedInk"));
                Control input;
                if (System.Text.RegularExpressions.Regex.IsMatch(parameter.Name, "(?i)key|token|secret|password|credential")) input = new PasswordBox { Password = parameter.Default ?? "" };
                else input = new TextBox { Text = parameter.Default ?? "" };
                input.SetResourceReference(Control.ForegroundProperty, "Ink");
                input.SetResourceReference(Control.BackgroundProperty, "Composer");
                input.SetResourceReference(Control.BorderBrushProperty, "Line");
                input.Margin = new Thickness(0, 0, 0, 12);
                inputs[parameter.Name] = input; panel.Children.Add(input);
            }
            var tokenPanel = new StackPanel();
            tokenPanel.Children.Add(MarketplaceDetailView.Label("魔搭 Token（仅下载私有 Skill / 托管部署时需要，本次使用）", 12, "MutedInk"));
            try { accessToken.Password = credentials.ModelScope; } catch (InvalidOperationException) { }
            tokenPanel.Children.Add(accessToken);
            var authentication = new Expander { Header = "需要平台认证？", Content = tokenPanel, Margin = new Thickness(0, 4, 0, 10) };
            authentication.SetResourceReference(Control.ForegroundProperty, "Ink"); panel.Children.Add(authentication);
            panel.Children.Add(MarketplaceDetailView.Card(MarketplaceDetailView.Label(item.Kind == MarketplaceKind.Mcp ?
                "MCP 可访问本机或第三方服务。API Key 会写入 Claude 原生配置，请勿提交含密钥的项目配置。托管方式会向魔搭提交配置并创建服务，可能产生平台资源用量；本地注册失败时请到魔搭控制台检查已部署服务。" :
                "Skill 可能包含脚本和工具指令。此步骤仅下载文件，不执行包内脚本；后续使用时由 Claude 的权限规则控制。已有同名目录将阻止安装。", 12, "MutedInk")));
            panel.Children.Add(consent);
            var buttons = new WrapPanel(); buttons.Children.Add(start); buttons.Children.Add(stop);
            var copyLog = new Button { Content = "复制日志", Margin = new Thickness(10,0,0,0), Padding = new Thickness(12,8,12,8) };
            copyLog.SetResourceReference(FrameworkElement.StyleProperty,"Ghost");
            copyLog.Click += delegate { try { if(logs.Text.Length>0) Clipboard.SetText(logs.Text); } catch(System.Runtime.InteropServices.ExternalException) { Log("剪贴板被占用，请稍后重试。"); } };
            buttons.Children.Add(copyLog); panel.Children.Add(buttons);
            panel.Children.Add(logs);
            start.SetResourceReference(FrameworkElement.StyleProperty, "Primary"); stop.SetResourceReference(FrameworkElement.StyleProperty, "Ghost");
            start.Click += async delegate { if (!running) await Install(); };
            stop.Click += delegate { if (running) { cancellation.Cancel(); stop.IsEnabled = false; Log("正在停止…"); } else dialog.Close(); };
            dialog.Closing += (s, e) => { if (running) { e.Cancel = true; cancellation.Cancel(); Log("正在停止，请等待操作结束后关闭。"); } };
            Target();
        }
        private void Target()
        {
            if (item.Kind == MarketplaceKind.Skill)
                try { destination.Text = System.IO.Path.Combine(MarketplaceInstaller.SkillsRoot(context, scope.SelectedIndex == 1), name.Text); }
                catch (Exception) { destination.Text = "请先选择有效的项目目录。"; }
            else destination.Text = "目标：" + (scope.SelectedIndex == 1 ? System.IO.Path.Combine(context.Workspace ?? "", ".mcp.json") : "Claude 用户级 MCP 配置") +
                ((method.SelectedItem as McpInstallOption) != null && ((McpInstallOption)method.SelectedItem).Config == null ? "\n将先在魔搭部署 HTTP 托管服务，再注册到 Claude。" : "");
        }
        private void Log(string line)
        {
            dialog.Dispatcher.Invoke(new Action(() =>
            {
                if (logs.Text.Length > 80000) logs.Text = logs.Text.Substring(logs.Text.Length - 60000);
                logs.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\n"); logs.ScrollToEnd();
            }));
        }
        private async Task Install()
        {
            if (consent.IsChecked != true) { Log("请先确认安装方式与权限。"); return; }
            if (succeeded) return;
            cancellation = new CancellationTokenSource(); running = true;
            start.IsEnabled = false; stop.Content = "停止安装"; name.IsEnabled = scope.IsEnabled = method.IsEnabled = consent.IsEnabled = false;
            foreach (var input in inputs.Values) input.IsEnabled = false;
            try
            {
                Log("安装条目：" + item.Name + "（" + item.Id + "）");
                Log("范围：" + (scope.SelectedIndex == 1 ? "当前项目" : "用户级"));
                string token = accessToken.Password;
                if (item.Kind == MarketplaceKind.Skill)
                    await MarketplaceInstaller.DownloadSkill(item, MarketplaceInstaller.SkillsRoot(context, scope.SelectedIndex == 1), name.Text.Trim(), token, Log, cancellation.Token);
                else
                {
                    if (scope.SelectedIndex == 1 && !System.IO.Directory.Exists(context.Workspace)) throw new InvalidOperationException("当前项目目录不存在。");
                    var option = method.SelectedItem as McpInstallOption;
                    if (option == null) throw new InvalidOperationException("平台没有提供可自动安装的 MCP 配置，请参考源码说明。");
                    var fields = inputs.ToDictionary(pair => pair.Key, pair => pair.Value is PasswordBox ? ((PasswordBox)pair.Value).Password : ((TextBox)pair.Value).Text);
                    foreach (var parameter in item.Parameters)
                        if (parameter.Required && String.IsNullOrWhiteSpace(fields[parameter.Name])) throw new InvalidOperationException("请填写必填参数：" + parameter.Name);
                    Dictionary<string, object> config;
                    if (option.Config == null)
                    {
                        if (String.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("魔搭托管部署需要 Token，请展开认证填写。");
                        await MarketplaceInstaller.CheckMcpConflict(context, name.Text.Trim(), Log, cancellation.Token);
                        Log("正在魔搭创建 HTTP 托管服务…");
                        config = await MarketplaceInstaller.Deploy(item.Id, fields, token, cancellation.Token);
                        Log("魔搭已创建托管服务，开始注册本地配置。若后续失败，请到魔搭控制台管理该服务。");
                    }
                    else config = MarketplaceInstaller.PrepareConfig(option, fields);
                    await MarketplaceInstaller.InstallMcp(context, name.Text.Trim(), scope.SelectedIndex == 1 ? "project" : "user", config, Log, cancellation.Token);
                }
                succeeded = true; start.Content = "安装成功"; Log("成功。重新启动 Claude 会话后使用新安装的扩展。");
            }
            catch (OperationCanceledException) { Log("安装已停止。若操作已提交，请检查 CLI 配置或魔搭控制台中的实际状态。"); }
            catch (System.IO.InvalidDataException) { Log("安装失败：下载的发布包不是有效 ZIP 或文件已损坏，请重试；若仍失败，请复制日志反馈该条目。"); }
            catch (UnauthorizedAccessException) { Log("安装失败：无权写入目标目录或配置文件，请选择可写目录或检查权限。"); }
            catch (System.IO.IOException ex) { Log("安装失败：文件读写失败（错误码 " + ex.HResult.ToString("X8") + "），请检查磁盘空间、同名文件及目录是否被占用。"); }
            catch (System.ComponentModel.Win32Exception) { Log("安装失败：无法启动 CLI 程序，请在 CLI 工具中重新检查安装路径。"); }
            catch (Exception ex) { Log("安装失败：" + (ex is InvalidOperationException ? ex.Message : "操作未完成（" + ex.GetType().Name + "），请复制日志以便定位。")); }
            finally
            {
                running = false; cancellation.Dispose(); cancellation = null;
                stop.Content = "关闭"; stop.IsEnabled = true; start.IsEnabled = !succeeded;
                name.IsEnabled = scope.IsEnabled = method.IsEnabled = consent.IsEnabled = !succeeded;
                foreach (var input in inputs.Values) input.IsEnabled = !succeeded;
            }
        }
        public bool Show() { dialog.ShowDialog(); return succeeded; }
    }
}
