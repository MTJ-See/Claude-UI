using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ClaudeUI
{
    internal sealed class RequestStatisticsView
    {
        private readonly Window window;
        private readonly RequestDetailLayout detailLayout;
        private readonly RequestOverviewLayout overviewLayout;
        private readonly StatisticsStore store;
        private readonly IList<RequestRate> rates;
        private readonly Func<bool> save;
        private readonly Func<IEnumerable<string>> configuredModels;
        private readonly Func<IEnumerable<ModelProfile>> modelProfiles;
        private readonly ListView list;
        private readonly Button ratesButton;
        private readonly CheckBox currentOnly;
        private readonly List<RequestStat> rows;
        private string session;
        private readonly TextBox requestFilter;
        private readonly ComboBox modelFilter, statusFilter;
        private readonly DatePicker fromFilter, toFilter;
        private bool updatingFilters;
        private readonly Func<int, bool, bool> confirmDeletion;
        private RequestStat selectionAnchor;

        public RequestStatisticsView(Window window, StatisticsStore store, IList<RequestRate> rates, Func<bool> save,
            Func<int, bool, bool> confirmDeletion = null, Func<IEnumerable<string>> configuredModels = null,
            Func<IEnumerable<ModelProfile>> modelProfiles = null)
        {
            this.window = window;
            detailLayout = new RequestDetailLayout((StackPanel)window.FindName("StatsDetailCards"));
            overviewLayout = new RequestOverviewLayout((StackPanel)window.FindName("StatsOverviewCards"));
            this.store = store;
            this.rates = rates;
            this.save = save;
            this.configuredModels = configuredModels;
            this.modelProfiles = modelProfiles;
            this.confirmDeletion = confirmDeletion ?? ((count, all) => MessageBox.Show(window,
                (all ? "清空全部 " + count + " 条请求统计？当前筛选条件不会限制清空范围。" : "删除选中的 " + count + " 条请求统计？") +
                "\n此操作不可撤销，只删除统计记录，不删除聊天内容、回复中的请求 ID 或 CLI 会话。",
                all ? "清空请求统计" : "删除请求统计", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes);
            list = (ListView)window.FindName("StatsRows");
            ratesButton = (Button)window.FindName("StatsRatesButton");
            currentOnly = (CheckBox)window.FindName("StatsCurrentOnly");
            try { rows = store.Read(); }
            catch (IOException) { rows = new List<RequestStat>(); Warning("无法读取本地请求统计。"); }
            catch (UnauthorizedAccessException) { rows = new List<RequestStat>(); Warning("没有权限读取本地请求统计。"); }
            list.SelectionChanged += delegate { ShowDetails(); };
            list.PreviewMouseDown += SelectRequest;
            currentOnly.Click += delegate { Refresh(); };
            ratesButton.Click += delegate { EditRate(); };
            ((Button)window.FindName("StatsSelectAll")).Click += delegate { list.SelectAll(); };
            ((Button)window.FindName("StatsDeselectAll")).Click += delegate { list.UnselectAll(); };
            ((Button)window.FindName("StatsDeleteSelected")).Click += delegate { Delete(false); };
            ((Button)window.FindName("StatsClearAll")).Click += delegate { Delete(true); };
            requestFilter = (TextBox)window.FindName("StatsRequestFilter");
            modelFilter = (ComboBox)window.FindName("StatsModelFilter");
            statusFilter = (ComboBox)window.FindName("StatsStatusFilter");
            fromFilter = (DatePicker)window.FindName("StatsFromFilter");
            toFilter = (DatePicker)window.FindName("StatsToFilter");
            statusFilter.ItemsSource = new[] { "全部状态", "成功", "失败", "已取消", "不完整", "历史记录" };
            statusFilter.SelectedIndex = 0;
            requestFilter.TextChanged += delegate { if (!updatingFilters) Refresh(); };
            modelFilter.SelectionChanged += delegate { if (!updatingFilters) Refresh(); };
            statusFilter.SelectionChanged += delegate { if (!updatingFilters) Refresh(); };
            fromFilter.SelectedDateChanged += delegate { if (!updatingFilters) Refresh(); };
            toFilter.SelectedDateChanged += delegate { if (!updatingFilters) Refresh(); };
            ((Button)window.FindName("StatsResetFilters")).Click += delegate {
                updatingFilters = true;
                requestFilter.Clear(); modelFilter.SelectedIndex = 0; statusFilter.SelectedIndex = 0;
                fromFilter.SelectedDate = null; toFilter.SelectedDate = null; currentOnly.IsChecked = false;
                updatingFilters = false;
                Refresh();
            };
            UpdateModels();
            ShowDetails();
        }

        public void Show(string sessionId)
        {
            session = sessionId;
            UpdateModels();
            Refresh();
        }

        public bool Record(IList<RequestStat> finished, IEnumerable<RequestRate> pricingRates = null)
        {
            if (finished.Count == 0) return true;
            foreach (var row in finished) RequestPricing.Apply(row, pricingRates ?? rates);
            rows.AddRange(finished);
            bool saved = true;
            try { store.Append(finished); }
            catch (IOException) { saved = false; }
            catch (UnauthorizedAccessException) { saved = false; }
            if (!saved) Warning("这些记录暂存在本次窗口，未能写入本地统计文件。");
            UpdateModels();
            Refresh();
            return saved;
        }

        private void UpdateModels()
        {
            updatingFilters = true;
            var selected = modelFilter.SelectedItem as string;
            var profiles = modelProfiles == null ? null : modelProfiles();
            foreach (var row in rows) row.ModelDisplayName = RequestModelDisplay.Resolve(row, profiles);
            modelFilter.ItemsSource = new[] { "全部模型" }.Concat(rows.Select(row => row.ModelDisplayName).Distinct().OrderBy(model => model)).ToList();
            modelFilter.SelectedItem = selected;
            if (modelFilter.SelectedIndex < 0) modelFilter.SelectedIndex = 0;
            updatingFilters = false;
        }

        private void Delete(bool all)
        {
            var selected = all ? rows.ToList() : list.SelectedItems.Cast<RequestStat>().ToList();
            if (selected.Count == 0 || !confirmDeletion(selected.Count, all)) return;
            try { store.Delete(selected, all); }
            catch (IOException) { Warning("删除失败，统计记录保持不变，请稍后重试。"); return; }
            catch (UnauthorizedAccessException) { Warning("没有权限删除本地统计，记录保持不变。"); return; }
            if (all) rows.Clear();
            else foreach (var row in selected) rows.Remove(row);
            Warning("");
            UpdateModels();
            Refresh();
        }

        private void SelectRequest(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            var source = e.OriginalSource as DependencyObject;
            if (source == null) return;
            var container = ItemsControl.ContainerFromElement(list, source) as ListViewItem;
            if (container == null) return;
            var row = container.Content as RequestStat;
            if (row == null) return;
            // Checkboxes explicitly toggle selection; ID text keeps native selection/copy behavior.
            for (var node = source; node != null && node != container; )
            {
                if (node is CheckBox || node is TextBox)
                {
                    selectionAnchor = row;
                    return;
                }
                var content = node as FrameworkContentElement;
                node = content != null ? content.Parent : VisualTreeHelper.GetParent(node);
            }
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 && selectionAnchor != null && list.Items.Contains(selectionAnchor))
            {
                int start = list.Items.IndexOf(selectionAnchor), end = list.Items.IndexOf(row);
                for (int i = Math.Min(start, end); i <= Math.Max(start, end); i++)
                    if (!list.SelectedItems.Contains(list.Items[i])) list.SelectedItems.Add(list.Items[i]);
            }
            else
            {
                if (!container.IsSelected) container.IsSelected = true;
                selectionAnchor = row;
            }
            container.Focus();
            ShowDetails(row);
            e.Handled = true;
        }

        private void Refresh()
        {
            var selected = list.SelectedItems.Cast<RequestStat>().ToList();
            var visible = new StatisticsFilter {
                Request = requestFilter.Text, ModelDisplayName = modelFilter.SelectedIndex > 0 ? modelFilter.SelectedItem as string : null,
                Status = statusFilter.SelectedIndex > 0 ? statusFilter.SelectedItem as string : null,
                FromLocal = fromFilter.SelectedDate, ToLocal = toFilter.SelectedDate,
                Session = currentOnly.IsChecked == true ? session ?? "<no-session>" : null
            }.Apply(rows);
            list.ItemsSource = visible;
            overviewLayout.Show(visible);
            bool invalidDates = fromFilter.SelectedDate > toFilter.SelectedDate;
            Label("StatsDateWarning").Text = invalidDates ? "开始日期不能晚于结束日期" : "";
            Label("StatsDateWarning").Visibility = invalidDates ? Visibility.Visible : Visibility.Collapsed;
            foreach (var row in selected.Where(visible.Contains)) list.SelectedItems.Add(row);
            Label("StatsEmpty").Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowDetails();
        }

        private void ShowDetails(RequestStat inspected = null)
        {
            var row = inspected ?? list.SelectedItem as RequestStat;
            Label("StatsSelectionCount").Text = "已选 " + list.SelectedItems.Count + " 条";
            ((Button)window.FindName("StatsSelectAll")).IsEnabled = list.Items.Count > 0;
            ((Button)window.FindName("StatsDeselectAll")).IsEnabled = list.SelectedItems.Count > 0;
            ((Button)window.FindName("StatsDeleteSelected")).IsEnabled = list.SelectedItems.Count > 0;
            ((Button)window.FindName("StatsClearAll")).IsEnabled = rows.Count > 0;
            ((TextBox)window.FindName("StatsRequestId")).Text = row == null ? "" : "请求 ID：" + (row.RequestId ?? "历史记录未保存");
            ratesButton.IsEnabled = true;
            detailLayout.Show(row);
            ((ScrollViewer)window.FindName("StatsDetailScroll")).ScrollToTop();
            if (row == null) { Label("StatsDetail").Text = "选择一条记录查看缓存明细和费率。"; return; }
            string estimate = row.EstimatedCost.HasValue ? null :
                row.CliEstimate.HasValue ? "CLI 估算 " + Money(row.CliEstimate.Value, "USD") :
                row.RunEstimate.HasValue && row.CallsInRun > 1 ?
                    "本轮 " + row.CallsInRun + " 次调用的 CLI 估算合计 " + Money(row.RunEstimate.Value, "USD") : null;
            string cost = row.HasBilledAmount ? "网关扣费 " + row.BilledText + "（" + row.BillingField +
                (String.IsNullOrEmpty(row.BilledCurrency) ? "，未声明币种）" : "）") :
                row.ExecutionOnly ? "未获得模型响应" : "未收到网关扣费金额";
            string usage = row.Input.HasValue ?
                "未缓存 " + row.Input.Value.ToString("N0") + "  ·  写缓存 " + (row.CacheWrite ?? 0).ToString("N0") +
                "  ·  读缓存 " + (row.CacheRead ?? 0).ToString("N0") : "未收到 token 用量";
            Label("StatsDetail").Text = usage + (row.Incomplete ? "（仅收到部分数据）" : "") + "  ·  " + cost + Environment.NewLine +
                "请求 " + (row.RequestId ?? "历史记录未保存") + " · 执行 " + (row.RunId ?? "—") + " · " + row.StatusText + " · " + row.DurationText + Environment.NewLine +
                "耗时口径：" + (row.TimingSource ?? "历史记录无耗时") + " · 完成时间 " + (row.CompletedUtc ?? "—") + Environment.NewLine +
                (row.RunDurationMs.HasValue ? "本轮 CLI 总耗时 " + (row.RunDurationMs.Value / 1000d).ToString("0.000", CultureInfo.InvariantCulture) + " s（含等待）" + Environment.NewLine : "") +
                (row.EstimatedCost.HasValue ? "采集时费用 " + row.EstimateText +
                    ((row.PriceSource ?? "").Contains("/次") ? "（按次计费）" : "（输入 " + row.EstimatedInputCost + " / 缓存 " + row.EstimatedCacheCost + " / 输出 " + row.EstimatedOutputCost + "）") + Environment.NewLine + row.PriceSource + Environment.NewLine : "") +
                (row.PricingStatus == null ? "" : "计费状态：" + row.PricingStatus + Environment.NewLine) +
                (estimate == null ? "" : estimate + Environment.NewLine) +
                "网关 " + row.Gateway + "（" + row.GatewaySource + "） · 结束原因 " + row.EndReason +
                (String.IsNullOrEmpty(row.SessionId) ? "" : Environment.NewLine + "会话 " + row.SessionId);
        }

        private void EditRate()
        {
            new RequestRatesDialog(window, rates, save, list.SelectedItem as RequestStat,
                configuredModels == null ? null : configuredModels()).Show();
            Refresh();
        }

        private static string Money(decimal value, string currency) { return currency + " " + value.ToString("0.000000", CultureInfo.InvariantCulture); }
        private TextBlock Label(string name) { return (TextBlock)window.FindName(name); }
        private void Warning(string value) { Label("StatsWarning").Text = value; }
    }
}
