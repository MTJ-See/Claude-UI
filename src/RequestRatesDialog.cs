using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;

namespace ClaudeUI
{
    internal static class RateConfiguration
    {
        // Replace in place so both failed saves and edits preserve deterministic rule ordering.
        internal static string Save(IList<RequestRate> rates, RequestRate previous, RequestRate changed, Func<bool> persist)
        {
            string error = changed.Validate();
            if (error != null) return error;
            if (rates.Any(rate => rate != null && rate != previous && rate.Model == changed.Model &&
                (rate.Gateway ?? "") == (changed.Gateway ?? ""))) return "此模型与网关范围已有规则，请编辑原规则。";
            int index = previous == null ? rates.Count : rates.IndexOf(previous);
            if (index < 0) return "原规则已更改，请重新打开配置。";
            if (previous == null) rates.Add(changed); else rates[index] = changed;
            bool saved = false;
            try { saved = persist(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally
            {
                if (!saved)
                {
                    if (previous == null) rates.RemoveAt(index); else rates[index] = previous;
                }
            }
            return saved ? null : "保存失败，配置保持原值。请检查本地文件权限或磁盘空间后重试。";
        }
        // 删除失败时按原位置恢复，避免内存与磁盘配置不一致。
        internal static string Delete(IList<RequestRate> rates, RequestRate previous, Func<bool> persist)
        {
            int index = previous == null ? -1 : rates.IndexOf(previous);
            if (index < 0) return "原规则已更改，请重新选择。";
            rates.RemoveAt(index);
            bool saved = false;
            try { saved = persist(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { if (!saved) rates.Insert(index, previous); }
            return saved ? null : "删除失败，原规则已保留。请检查本地文件权限或磁盘空间后重试。";
        }
    }

    internal sealed class RequestRatesDialog
    {
        private readonly Window view;
        private readonly IList<RequestRate> rates;
        private readonly Func<bool> persist;
        private readonly ListBox list;
        private readonly TextBox gateway, holidays;
        private readonly ComboBox model;
        private readonly ComboBox mode, unit, currency;
        private readonly CheckBox enabled, peak;
        private readonly StackPanel tokenFields, requestFields, peakFields;
        private readonly TextBlock error;
        private readonly TextBox[] prices = new TextBox[4], peakPrices = new TextBox[4];
        private readonly TextBox perRequest, peakPerRequest;
        private RequestRate editing;
        private readonly Button remove;
        private readonly TextBlock editingState;
        private readonly Func<RequestRate, bool> confirmDeletion;

        internal RequestRatesDialog(Window owner, IList<RequestRate> rates, Func<bool> persist, RequestStat selected,
            IEnumerable<string> configuredModels = null, Func<RequestRate, bool> confirmDeletion = null)
        {
            this.rates = rates;
            this.persist = persist;
            view = new Window { Owner = owner, Title = "模型计费标准", Width = 850, Height = 760,
                MinWidth = 740, MinHeight = 520, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = owner.FontSize };
            this.confirmDeletion = confirmDeletion ?? (rate => DialogVisuals.Show(view,
                "删除模型「" + rate.Model + "」的计费规则？\n历史请求的费用快照会保留。下一轮执行起，未配置的模型可能使用公开参考价格。",
                "删除计费规则", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
            view.Resources.MergedDictionaries.Add(owner.Resources);
            view.SetResourceReference(Window.BackgroundProperty, "Canvas");
            view.SetResourceReference(Window.ForegroundProperty, "Ink");
            var layout = new Grid { Margin = new Thickness(18) };
            layout.SetResourceReference(Panel.BackgroundProperty, "Canvas");
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(250) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            var sidebar = new DockPanel { Margin = new Thickness(0, 0, 18, 0) };
            var add = new Button { Content = "＋ 新增计费规则", Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(add, "新增计费规则");
            DockPanel.SetDock(add, Dock.Top);
            sidebar.Children.Add(add);
            var listHint = new TextBlock { Text = "选择已有规则可修改或删除", TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(2, 0, 2, 12) };
            listHint.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            DockPanel.SetDock(listHint, Dock.Top); sidebar.Children.Add(listHint);
            list = new ListBox { ItemContainerStyle = (Style)owner.Resources["NavItem"] };
            var item = new FrameworkElementFactory(typeof(TextBlock));
            item.SetBinding(TextBlock.TextProperty, new Binding("DisplayName"));
            item.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap);
            list.ItemTemplate = new DataTemplate { VisualTree = item };
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            list.SetResourceReference(Control.BackgroundProperty, "Sidebar");
            list.SetResourceReference(Control.BorderBrushProperty, "Line");
            AutomationProperties.SetName(list, "计费规则列表");
            sidebar.Children.Add(list);
            layout.Children.Add(sidebar);
            var right = new DockPanel();
            Grid.SetColumn(right, 1);
            layout.Children.Add(right);
            var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            error.SetResourceReference(TextBlock.ForegroundProperty, "Failure");
            footer.Children.Add(error);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var close = new Button { Content = "关闭", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button { Content = "保存规则", IsDefault = true, Style = (Style)owner.Resources["Primary"] };
            remove = new Button { Content = "删除规则", IsEnabled = false, Margin = new Thickness(0, 0, 8, 0) };
            remove.SetResourceReference(FrameworkElement.StyleProperty, "Ghost");
            remove.SetResourceReference(Control.ForegroundProperty, "Failure");
            AutomationProperties.SetName(remove, "删除计费规则");
            buttons.Children.Add(remove); buttons.Children.Add(close); buttons.Children.Add(save); footer.Children.Add(buttons);
            DockPanel.SetDock(footer, Dock.Bottom); right.Children.Add(footer);
            var form = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
            right.Children.Add(new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            editingState = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
            AutomationProperties.SetName(editingState, "计费规则编辑状态");
            form.Children.Add(editingState);
            form.Children.Add(new TextBlock { Text = "优先精确匹配响应模型；无对应规则时匹配本轮请求的模型标识，支持网关返回不同模型名。网关留空适用于全部网关；同一模型下指定网关的规则优先。每轮执行开始时固定规则，编辑从下一轮生效，历史费用保留快照。", TextWrapping = TextWrapping.Wrap });
            model = Field(form, "模型标识", new ComboBox { IsEditable = true, IsTextSearchEnabled = true,
                ItemsSource = (configuredModels ?? Enumerable.Empty<string>()).Where(id => !String.IsNullOrWhiteSpace(id))
                    .Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToList(),
                ToolTip = "选择已配置的模型，也可直接输入其他模型标识。", MaxDropDownHeight = 260 });
            gateway = Field(form, "网关范围（留空为全部；指定时须与请求明细完全一致）", new TextBox { MaxLength = 1024 });
            enabled = Field(form, "启用状态", new CheckBox { Content = "启用此规则（取消勾选并保存即可停用）" });
            mode = Field(form, "计费方式", new ComboBox { ItemsSource = new[] { "按 Token", "按次（每次完整模型响应）" } });
            unit = Field(form, "计费单位", new ComboBox());
            currency = Field(form, "结算币种（原币展示，分别汇总，不做换算）", new ComboBox { ItemsSource = new[] { "USD", "CNY" } });
            tokenFields = new StackPanel(); form.Children.Add(tokenFields);
            for (int i = 0; i < prices.Length; i++) prices[i] = Field(tokenFields, new[] { "未缓存输入单价", "缓存写入单价", "缓存命中输入单价", "输出单价" }[i], new TextBox());
            requestFields = new StackPanel(); form.Children.Add(requestFields);
            perRequest = Field(requestFields, "每次请求单价", new TextBox());
            peak = Field(form, "峰谷价格（可选）", new CheckBox { Content = "区分高峰 / 空闲价格" });
            peakFields = new StackPanel(); form.Children.Add(peakFields);
            peakFields.Children.Add(new TextBlock { Text = "上方为常规 / 空闲价格。以下为高峰价格。以请求开始时间为准：北京时间周一至周五 09:00–12:00、14:00–18:00（不含结束时刻）为高峰；周末、下方节假日全天为空闲。节假日须自行维护，未填写的工作日按高峰时段处理。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
            for (int i = 0; i < peakPrices.Length; i++) peakPrices[i] = Field(peakFields, new[] { "高峰未缓存输入单价", "高峰缓存写入单价", "高峰缓存命中输入单价", "高峰输出单价" }[i], new TextBox());
            peakPerRequest = Field(peakFields, "高峰每次请求单价", new TextBox());
            holidays = Field(peakFields, "排除的节假日日期（yyyy-MM-dd，逗号或换行分隔）", new TextBox { AcceptsReturn = true, MinHeight = 65, TextWrapping = TextWrapping.Wrap });
            form.Children.Add(new TextBlock { Text = "单价范围 0–1,000,000，免费项填 0。停用规则后，新请求显示停用状态，不会改用公开参考价格。未配置模型仍可使用已有公开参考价；未知模型显示未配置。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            list.SelectionChanged += delegate { if (list.SelectedItem is RequestRate) Load((RequestRate)list.SelectedItem, null); };
            add.Click += delegate { list.SelectedItem = null; Load(null, null); };
            mode.SelectionChanged += delegate { UpdateMode(); };
            peak.Click += delegate { UpdateVisibility(); };
            close.Click += delegate { view.Close(); };
            save.Click += delegate { Save(); };
            remove.Click += delegate { Delete(); };
            DialogVisuals.Apply(view);
            view.Content = layout;
            RefreshList();
            var previous = selected == null ? null : rates.FirstOrDefault(r => r != null && r.Matches(selected) && !String.IsNullOrEmpty(r.Gateway)) ?? rates.FirstOrDefault(r => r != null && r.Matches(selected));
            // 没有指定请求时优先打开已有规则，便于再次进入后直接修改。
            if (previous == null && selected == null) previous = rates.FirstOrDefault(rate => rate != null);
            if (previous != null) list.SelectedItem = previous;
            else Load(null, selected);
        }

        internal void Show() { view.ShowDialog(); }
        internal Window View { get { return view; } }

        private static T Field<T>(Panel panel, string label, T control) where T : Control
        {
            panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 3) });
            AutomationProperties.SetName(control, label);
            if (control is CheckBox) control.SetResourceReference(Control.ForegroundProperty, "Ink");
            panel.Children.Add(control);
            return control;
        }

        private void RefreshList() { list.ItemsSource = null; list.ItemsSource = rates.Where(r => r != null).ToList(); }

        private void Load(RequestRate rate, RequestStat selected)
        {
            editing = rate;
            remove.IsEnabled = rate != null;
            editingState.Text = rate == null ? "新增计费规则" : "编辑计费规则";
            var value = rate ?? new RequestRate { Model = selected == null ? "" : selected.Model, Currency = "CNY" };
            model.Text = value.Model ?? ""; gateway.Text = value.Gateway ?? "";
            enabled.IsChecked = value.Enabled;
            mode.SelectedIndex = value.BillingMode == "Request" ? 1 : 0;
            UpdateMode();
            unit.SelectedIndex = value.BillingMode == "Request" ? 0 : value.Unit == 1 ? 0 : value.Unit == 1000 ? 1 : 2;
            currency.SelectedItem = value.Currency;
            var normal = new[] { value.Input, value.CacheWrite, value.CacheRead, value.Output };
            var high = new[] { value.PeakInput, value.PeakCacheWrite, value.PeakCacheRead, value.PeakOutput };
            for (int i = 0; i < 4; i++)
            {
                prices[i].Text = rate == null ? "" : normal[i].ToString(CultureInfo.InvariantCulture);
                peakPrices[i].Text = rate == null ? "" : high[i].ToString(CultureInfo.InvariantCulture);
            }
            perRequest.Text = value.PerRequest.ToString(CultureInfo.InvariantCulture);
            peakPerRequest.Text = value.PeakPerRequest.ToString(CultureInfo.InvariantCulture);
            peak.IsChecked = value.PeakEnabled; holidays.Text = value.Holidays ?? "";
            error.Text = ""; UpdateVisibility();
        }

        private void UpdateMode()
        {
            unit.ItemsSource = mode.SelectedIndex == 1 ? new[] { "每 1 次请求" } : new[] { "每 1 Token", "每 1,000 Token", "每 1,000,000 Token" };
            unit.SelectedIndex = mode.SelectedIndex == 1 ? 0 : 2;
            UpdateVisibility();
        }

        private void UpdateVisibility()
        {
            bool byRequest = mode.SelectedIndex == 1;
            tokenFields.Visibility = byRequest ? Visibility.Collapsed : Visibility.Visible;
            requestFields.Visibility = byRequest ? Visibility.Visible : Visibility.Collapsed;
            peakFields.Visibility = peak.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            foreach (var box in peakPrices) { box.Visibility = byRequest ? Visibility.Collapsed : Visibility.Visible; ((FrameworkElement)peakFields.Children[peakFields.Children.IndexOf(box) - 1]).Visibility = box.Visibility; }
            peakPerRequest.Visibility = byRequest ? Visibility.Visible : Visibility.Collapsed;
            ((FrameworkElement)peakFields.Children[peakFields.Children.IndexOf(peakPerRequest) - 1]).Visibility = peakPerRequest.Visibility;
        }

        private decimal ReadPrice(TextBox box)
        {
            decimal value;
            if (!Decimal.TryParse(box.Text.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) || value < 0 || value > 1000000)
            { box.Focus(); throw new ArgumentException("请填写 0 到 1,000,000 之间的单价，小数使用英文句点，免费项填 0。"); }
            return value;
        }

        private void Delete()
        {
            var previous = editing;
            if (previous == null || !confirmDeletion(previous)) return;
            error.SetResourceReference(TextBlock.ForegroundProperty, "Failure");
            int index = rates.IndexOf(previous);
            string failure = RateConfiguration.Delete(rates, previous, persist);
            if (failure != null) { error.Text = failure; return; }
            RefreshList();
            if (list.Items.Count > 0) list.SelectedIndex = Math.Min(Math.Max(0, index), list.Items.Count - 1);
            else Load(null, null);
            error.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            error.Text = "规则已删除；历史请求费用保持原值，下一轮执行使用更新后的规则。";
        }

        private void Save()
        {
            error.SetResourceReference(TextBlock.ForegroundProperty, "Failure");
            try
            {
                bool byRequest = mode.SelectedIndex == 1, hasPeak = peak.IsChecked == true;
                var changed = new RequestRate { Model = model.Text.Trim(), Gateway = gateway.Text.Trim(), Currency = currency.SelectedItem as string,
                    Enabled = enabled.IsChecked == true, BillingMode = byRequest ? "Request" : "Token",
                    Unit = byRequest ? 1 : unit.SelectedIndex == 0 ? 1 : unit.SelectedIndex == 1 ? 1000 : 1000000,
                    PeakEnabled = hasPeak, Holidays = holidays.Text.Trim() };
                if (byRequest) { changed.PerRequest = ReadPrice(perRequest); if (hasPeak) changed.PeakPerRequest = ReadPrice(peakPerRequest); }
                else
                {
                    changed.Input = ReadPrice(prices[0]); changed.CacheWrite = ReadPrice(prices[1]);
                    changed.CacheRead = ReadPrice(prices[2]); changed.Output = ReadPrice(prices[3]);
                    if (hasPeak) { changed.PeakInput = ReadPrice(peakPrices[0]); changed.PeakCacheWrite = ReadPrice(peakPrices[1]); changed.PeakCacheRead = ReadPrice(peakPrices[2]); changed.PeakOutput = ReadPrice(peakPrices[3]); }
                }
                string failure = RateConfiguration.Save(rates, editing, changed, persist);
                if (failure != null) { error.Text = failure; return; }
                RefreshList(); list.SelectedItem = changed;
                error.SetResourceReference(TextBlock.ForegroundProperty, "Success");
                error.Text = "已保存；下一轮执行使用此规则。";
            }
            catch (ArgumentException failure) { error.Text = failure.Message; }
        }
    }
}
