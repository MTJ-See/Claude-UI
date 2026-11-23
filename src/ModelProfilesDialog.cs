using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ClaudeUI
{
    internal sealed class ModelProfilesDialog
    {
        private readonly Window view;
        private readonly IList<ModelProfile> profiles;
        private readonly ListBox list;
        private readonly TextBox name, modelId, endpoint;
        private readonly CheckBox custom;
        private readonly ComboBox authentication, protocol;
        private readonly PasswordBox credential;
        private readonly TextBlock hint, status;
        private readonly StackPanel endpointFields;
        private readonly Button remove;
        private ModelProfile editing;
        private CancellationTokenSource testCancellation;
        private bool closed;

        public ModelProfile Saved { get; private set; }
        public string RemovedId { get; private set; }

        public ModelProfilesDialog(Window owner, IList<ModelProfile> profiles, string selectedId,
            Func<ModelProfile, CancellationToken, Task<string>> testProfile, Action<Window, ModelProfile> configureRates = null)
        {
            this.profiles = profiles;
            view = new Window {
                Owner = owner, Title = "模型配置", Width = 690, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, FontSize = owner.FontSize, FontFamily = owner.FontFamily
            };
            view.Resources.MergedDictionaries.Add(owner.Resources);
            view.SetResourceReference(Window.BackgroundProperty, "Canvas");
            view.SetResourceReference(Window.ForegroundProperty, "Ink");
            var layout = new Grid { Margin = new Thickness(18) };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var sidebar = new DockPanel { Margin = new Thickness(0, 0, 14, 0) };
            var add = new Button { Content = "＋ 新增模型", Margin = new Thickness(0, 0, 0, 8) };
            DockPanel.SetDock(add, Dock.Top);
            sidebar.Children.Add(add);
            list = new ListBox {
                ItemsSource = profiles, DisplayMemberPath = "Name", Padding = new Thickness(4),
                ItemContainerStyle = (Style)owner.Resources["NavItem"]
            };
            list.SetResourceReference(Control.BackgroundProperty, "Sidebar");
            list.SetResourceReference(Control.BorderBrushProperty, "Line");
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(list, "已配置的模型");
            sidebar.Children.Add(list);
            layout.Children.Add(sidebar);

            var form = new StackPanel();
            Grid.SetColumn(form, 1);
            layout.Children.Add(form);
            form.Children.Add(new TextBlock {
                Text = "在发送按钮旁选择已保存的模型。留在「本机默认」时使用现有 Claude CLI 设置。",
                Style = (Style)owner.Resources["Muted"], TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 9)
            });
            var fields = new StackPanel();
            form.Children.Add(fields);
            name = AddField(fields, "显示名称", new TextBox { MaxLength = 60 });
            modelId = AddField(fields, "模型 ID", new TextBox { MaxLength = 256 });
            modelId.ToolTip = "填写服务商提供的模型 ID，例如 deepseek-flash。也可填写本机 Claude CLI 支持的模型别名。";
            custom = new CheckBox { Content = "使用自定义 API 端点", Margin = new Thickness(0, 12, 0, 0) };
            custom.SetResourceReference(Control.ForegroundProperty, "Ink");
            fields.Children.Add(custom);

            endpointFields = new StackPanel();
            fields.Children.Add(endpointFields);
            protocol = AddField(endpointFields, "API 协议", new ComboBox { ItemsSource = ModelProtocol.Labels, SelectedIndex = 0 });
            endpoint = AddField(endpointFields, "API 地址（基础地址或完整接口）", new TextBox { MaxLength = 1024 });
            endpoint.ToolTip = "可直接粘贴 /v1/chat/completions、/v1/responses 或 /v1beta/models/模型:generateContent 完整地址。只填基础地址时请选择协议。";
            authentication = AddField(endpointFields, "认证方式", new ComboBox {
                ItemsSource = new[] { "Bearer Token", "API Key（Gemini: x-goog-api-key；其他: x-api-key）" }, SelectedIndex = 0
            });
            credential = AddField(endpointFields, "API 凭据", new PasswordBox { MaxLength = 4096, Padding = new Thickness(8, 5, 8, 5) });
            credential.SetResourceReference(Control.BackgroundProperty, "Panel");
            credential.SetResourceReference(Control.ForegroundProperty, "Ink");
            credential.SetResourceReference(Control.BorderBrushProperty, "Line");
            credential.SetResourceReference(PasswordBox.CaretBrushProperty, "Ink");
            hint = new TextBlock { Style = (Style)owner.Resources["Muted"], Margin = new Thickness(0, 5, 0, 0), TextWrapping = TextWrapping.Wrap };
            endpointFields.Children.Add(hint);
            status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 7, 0, 0) };
            form.Children.Add(status);

            var buttons = new DockPanel { Margin = new Thickness(0, 11, 0, 0), LastChildFill = false };
            remove = new Button { Content = "移除配置" };
            buttons.Children.Add(remove);
            var save = new Button { Content = "保存配置", IsDefault = true, Style = (Style)owner.Resources["Primary"] };
            DockPanel.SetDock(save, Dock.Right);
            buttons.Children.Add(save);
            var cancel = new Button { Content = "取消", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            buttons.Children.Add(cancel);
            var test = new Button { Content = "测试连接", Margin = new Thickness(0, 0, 8, 0),
                ToolTip = "使用当前配置固定发送 hello 测试连通性，可能产生少量 token 用量。" };
            DockPanel.SetDock(test, Dock.Right);
            buttons.Children.Add(test);
            form.Children.Add(buttons);
            var ratesButton = new Button {
                Content = "模型计费标准", HorizontalAlignment = HorizontalAlignment.Left,
                IsEnabled = configureRates != null, ToolTip = "配置模型价格、计费方式和计费单位"
            };
            AutomationProperties.SetName(ratesButton, "模型计费标准");
            ratesButton.Click += delegate {
                configureRates(view, new ModelProfile { ModelId = modelId.Text.Trim(),
                    BaseUrl = custom.IsChecked == true ? endpoint.Text.Trim() : null });
            };
            var ratesFooter = new Border { BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(0, 12, 0, 0), Child = ratesButton };
            ratesFooter.SetResourceReference(Border.BorderBrushProperty, "Line");
            Grid.SetRow(ratesFooter, 1);
            Grid.SetColumnSpan(ratesFooter, 2);
            layout.Children.Add(ratesFooter);
            DialogVisuals.Apply(view);
            view.Content = layout;

            list.SelectionChanged += delegate { ShowProfile((ModelProfile)list.SelectedItem); };
            add.Click += delegate { list.SelectedItem = null; ShowProfile(null); name.Focus(); };
            custom.Checked += delegate { UpdateEndpointFields(); status.Text = ""; };
            custom.Unchecked += delegate { UpdateEndpointFields(); status.Text = ""; };
            modelId.TextChanged += delegate { status.Text = ""; };
            endpoint.TextChanged += delegate { status.Text = ""; };
            authentication.SelectionChanged += delegate { status.Text = ""; };
            protocol.SelectionChanged += delegate { status.Text = ""; };
            credential.PasswordChanged += delegate { status.Text = ""; };
            remove.Click += delegate
            {
                if (editing == null || DialogVisuals.Show(view, "移除「" + editing.Name + "」？使用该配置的会话将需要重新选择模型。",
                    "移除模型配置", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
                RemovedId = editing.Id;
                view.DialogResult = true;
            };
            save.Click += delegate { Save(); };
            test.Click += async delegate
            {
                if (testCancellation != null) return;
                var profile = ReadProfile(false);
                if (profile == null) return;
                sidebar.IsEnabled = fields.IsEnabled = save.IsEnabled = remove.IsEnabled = test.IsEnabled = ratesButton.IsEnabled = false;
                try { await TestAsync(profile, testProfile); }
                finally
                {
                    sidebar.IsEnabled = fields.IsEnabled = save.IsEnabled = test.IsEnabled = true;
                    remove.IsEnabled = editing != null;
                    ratesButton.IsEnabled = configureRates != null;
                }
            };
            view.Closed += delegate
            {
                closed = true;
                if (testCancellation != null) testCancellation.Cancel();
            };
            list.SelectedItem = profiles.FirstOrDefault(item => item.Id == selectedId);
            ShowProfile((ModelProfile)list.SelectedItem);
        }

        public bool ShowDialog() { return view.ShowDialog() == true; }

        private static T AddField<T>(Panel form, string label, T field) where T : Control
        {
            form.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 7, 0, 4) });
            field.MinHeight = 32;
            field.VerticalContentAlignment = VerticalAlignment.Center;
            AutomationProperties.SetName(field, label);
            form.Children.Add(field);
            return field;
        }

        private void ShowProfile(ModelProfile profile)
        {
            editing = profile;
            name.Text = profile == null ? "" : profile.Name;
            modelId.Text = profile == null ? "" : profile.ModelId;
            endpoint.Text = profile == null ? "" : (String.IsNullOrEmpty(profile.ApiProtocol) ? ModelProfile.NormalizeEndpoint(profile.BaseUrl) : ModelProtocol.Normalize(profile.BaseUrl)) ?? profile.BaseUrl ?? "";
            protocol.SelectedIndex = profile == null ? 0 : Math.Max(0, Array.IndexOf(ModelProtocol.Values, profile.ApiProtocol ?? ModelProtocol.Anthropic));
            authentication.SelectedIndex = profile != null && profile.Authentication == ModelProfile.ApiKey ? 1 : 0;
            credential.Clear();
            custom.IsChecked = profile != null && profile.UsesEndpoint;
            remove.IsEnabled = profile != null;
            status.Text = "";
            UpdateEndpointFields();
        }

        private void UpdateEndpointFields()
        {
            endpointFields.Visibility = custom.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = editing != null && !String.IsNullOrEmpty(editing.ProtectedCredential)
                ? "同一地址和认证方式留空时保留已存凭据。"
                : "凭据使用 Windows 当前账户加密保存。";
            hint.Text += " 自动识别依赖完整接口路径；只填基础地址时请选择协议。转换协议会在完整响应后显示回复。";
        }

        private void Save()
        {
            var profile = ReadProfile(true);
            if (profile == null) return;
            credential.Clear();
            Saved = profile;
            view.DialogResult = true;
        }

        private async Task TestAsync(ModelProfile profile, Func<ModelProfile, CancellationToken, Task<string>> testProfile, Action<Window, ModelProfile> configureRates = null)
        {
            ShowStatus("正在发送测试请求…", "MutedInk");
            using (var token = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
            {
                testCancellation = token;
                try
                {
                    string failure = await testProfile(profile, token.Token);
                    if (!closed)
                        ShowStatus(failure ?? "测试成功，模型已响应。", failure == null ? "Success" : "Failure");
                }
                catch (OperationCanceledException)
                {
                    if (!closed) ShowStatus("测试超时，请检查网络或服务端状态后重试。", "Failure");
                }
                catch (InvalidOperationException ex)
                {
                    if (!closed) ShowStatus(ex.Message, "Failure");
                }
                catch (Exception)
                {
                    if (!closed) ShowStatus("无法运行 Claude CLI 测试，请检查安装和工作目录。", "Failure");
                }
                finally { testCancellation = null; }
            }
        }

        private void ShowStatus(string text, string resource)
        {
            status.Text = text;
            status.SetResourceReference(TextBlock.ForegroundProperty, resource);
        }

        private ModelProfile ReadProfile(bool requireName)
        {
            string title = name.Text.Trim(), model = modelId.Text.Trim();
            if (requireName && (title.Length == 0 || title.Any(Char.IsControl) || title == "本机默认" ||
                profiles.Any(item => item != editing && String.Equals(item.Name, title, StringComparison.OrdinalIgnoreCase))))
            {
                ShowStatus("请填写一个非空且未使用的名称。", "Failure");
                name.Focus();
                return null;
            }
            if (!ModelProfile.IsModelId(model))
            {
                ShowStatus("请输入有效的模型 ID，模型 ID 中不能包含空格。", "Failure");
                modelId.Focus();
                return null;
            }
            var profile = new ModelProfile {
                Id = editing == null ? Guid.NewGuid().ToString() : editing.Id, Name = title, ModelId = model
            };
            if (custom.IsChecked == true)
            {
                profile.ApiProtocol = ModelProtocol.Values[Math.Max(0, protocol.SelectedIndex)];
                profile.BaseUrl = ModelProtocol.Normalize(endpoint.Text.Trim());
                if (profile.BaseUrl == null)
                {
                    ShowStatus("请输入有效的 HTTPS API 地址，可填写完整接口。本机回环地址可使用 HTTP。", "Failure");
                    endpoint.Focus();
                    return null;
                }
                try { ModelProtocol.Endpoint(profile.BaseUrl, ModelProtocol.Resolve(profile.ApiProtocol, profile.BaseUrl), profile.ModelId); }
                catch (InvalidOperationException ex) { ShowStatus(ex.Message, "Failure"); return null; }
                profile.Authentication = authentication.SelectedIndex == 1 ? ModelProfile.ApiKey : ModelProfile.Bearer;
                string secret = credential.Password.Trim();
                if (secret.Length > 0 && !secret.Any(Char.IsControl))
                {
                    try { profile.ProtectedCredential = ModelProfile.Protect(secret); }
                    catch (System.Security.Cryptography.CryptographicException)
                    {
                        ShowStatus("Windows 无法加密凭据，请检查当前账户的凭据存储。", "Failure");
                        return null;
                    }
                }
                else if (secret.Length == 0 && editing != null && ModelProtocol.Normalize(editing.BaseUrl) == profile.BaseUrl &&
                    editing.Authentication == profile.Authentication)
                    profile.ProtectedCredential = editing.ProtectedCredential;
                if (String.IsNullOrEmpty(profile.ProtectedCredential))
                {
                    ShowStatus("请填写此端点的 API 凭据。更换地址或认证方式时需重新填写。", "Failure");
                    credential.Focus();
                    return null;
                }
            }
            return profile;
        }
    }
}
