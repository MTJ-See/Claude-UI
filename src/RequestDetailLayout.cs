using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUI
{
    // Presentation only: all values come from the original stored request record.
    internal sealed class RequestDetailLayout
    {
        private readonly StackPanel host;
        public RequestDetailLayout(StackPanel host) { this.host = host; }

        public void Show(RequestStat row)
        {
            host.Children.Clear();
            if (row == null)
            {
                host.Children.Add(Text("选择一条请求，查看耗时、用量、缓存和费用明细。", 14, "MutedInk"));
                return;
            }
            var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
            string state = row.StatusText;
            string tone = state == "成功" ? "Success" : state == "失败" ? "Failure" : "MutedInk";
            var badge = new Border { Child = Text(state, 12, tone), Padding = new Thickness(9, 4, 9, 4), CornerRadius = new CornerRadius(5), Margin = new Thickness(12, 0, 0, 0) };
            badge.SetResourceReference(Border.BackgroundProperty, state == "成功" ? "SuccessSurface" : state == "失败" ? "FailureSurface" : "Selected");
            DockPanel.SetDock(badge, Dock.Right);
            heading.Children.Add(badge);
            var model = Text(Value(row.Model) + (String.IsNullOrWhiteSpace(row.Operation) ? "" : " · " + row.Operation), 15, "Ink");
            model.FontWeight = FontWeights.SemiBold;
            model.TextTrimming = TextTrimming.CharacterEllipsis;
            model.ToolTip = model.Text;
            heading.Children.Add(model);
            host.Children.Add(heading);
            if (!String.IsNullOrWhiteSpace(row.RequestedModel) && row.RequestedModel != row.Model)
                Field("请求模型", row.RequestedModel);

            var timing = new WrapPanel();
            timing.Children.Add(Metric("单次响应耗时", row.DurationText, row.DurationMs.HasValue ? "口径见下方时间与执行" : "未记录响应耗时", 156));
            timing.Children.Add(Metric("本轮 CLI 总耗时", row.RunDurationMs.HasValue ? Seconds(row.RunDurationMs.Value) : "—", "包含等待时间", 156));
            timing.Children.Add(Metric("本轮模型调用", row.CallsInRun > 0 ? row.CallsInRun.ToString("N0") + " 次" : "—", "同一轮 CLI 执行", 156));
            host.Children.Add(timing);

            Section("TOKEN 与缓存");
            var tokens = new WrapPanel();
            tokens.Children.Add(Metric("未缓存输入", Number(row.Input), null, 128));
            tokens.Children.Add(Metric("缓存写入", Number(row.CacheWrite), null, 128));
            tokens.Children.Add(Metric("缓存读取", Number(row.CacheRead), null, 128));
            tokens.Children.Add(Metric("输出", row.OutputText, null, 128));
            host.Children.Add(tokens);
            Field("输入总量", row.InputText + (row.Incomplete ? "（仅收到部分数据）" : ""));
            if (row.CacheWrite1Hour.HasValue) Field("1 小时缓存写入", Number(row.CacheWrite1Hour));

            Section("费用");
            var costs = new WrapPanel();
            string estimate = "未估算", estimateLabel = "本地费用计算", estimateNote = "参考数值，不是网关账单";
            if (row.EstimatedCost.HasValue) estimate = row.EstimateText;
            else if (row.CliEstimate.HasValue) { estimate = Money(row.CliEstimate.Value); estimateLabel = "单次 CLI 估算"; }
            else if (row.RunEstimate.HasValue && row.CallsInRun > 1)
            {
                estimate = Money(row.RunEstimate.Value);
                estimateLabel = "本轮 CLI 估算合计";
                estimateNote = row.CallsInRun + " 次调用合计，不是单次请求费用";
            }
            costs.Children.Add(Metric(estimateLabel, estimate, estimateNote, 258));
            host.Children.Add(costs);
            if (row.EstimatedCost.HasValue)
            {
                Field("估算明细", (row.PriceSource ?? "").Contains("/次") ? "按次计费" :
                    "输入 " + CostPart(row.EstimatedInputCost) + " / 缓存 " + CostPart(row.EstimatedCacheCost) + " / 输出 " + CostPart(row.EstimatedOutputCost));
                Field("价格来源", row.PriceSource);
            }
            if (!String.IsNullOrWhiteSpace(row.PricingStatus)) Field("计费状态", row.PricingStatus);

            Section("时间与执行");
            Field("开始时间", row.TimeText);
            Field("完成时间", LocalTime(row.CompletedUtc), row.CompletedUtc);
            Field("耗时口径", row.TimingSource ?? "历史记录无耗时");
            Field("执行 ID", row.RunId);
            Field("结束原因", row.EndReason);
            Section("网关与会话");
            Field("网关地址", row.Gateway);
            Field("地址来源", row.GatewaySource);
            Field("会话 ID", row.SessionId);
        }

        private void Section(string title)
        {
            var text = Text(title, 12, "MutedInk");
            text.FontWeight = FontWeights.SemiBold;
            var rule = new Border { Child = text, BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 14, 0, 0), Margin = new Thickness(0, 14, 0, 10) };
            rule.SetResourceReference(Border.BorderBrushProperty, "Line");
            host.Children.Add(rule);
        }

        private void Field(string label, string value, string tooltip = null)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var caption = Text(label, 12, "MutedInk");
            caption.Margin = new Thickness(0, 2, 10, 0);
            row.Children.Add(caption);
            var content = new TextBox { Text = Value(value), IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap,
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(0), FontSize = 13, ToolTip = tooltip ?? Value(value) };
            content.SetResourceReference(Control.ForegroundProperty, "Ink");
            content.SetResourceReference(TextBox.CaretBrushProperty, "Ink");
            System.Windows.Automation.AutomationProperties.SetName(content, label);
            Grid.SetColumn(content, 1);
            row.Children.Add(content);
            host.Children.Add(row);
        }

        private static Border Metric(string caption, string value, string note, double width)
        {
            var panel = new StackPanel();
            panel.Children.Add(Text(caption, 12, "MutedInk"));
            var main = Text(value, 19, "Ink");
            main.FontWeight = FontWeights.SemiBold;
            main.Margin = new Thickness(0, 6, 0, 0);
            main.ToolTip = value;
            panel.Children.Add(main);
            if (!String.IsNullOrWhiteSpace(note))
            {
                var description = Text(note, 12, "MutedInk");
                description.Margin = new Thickness(0, 5, 0, 0);
                panel.Children.Add(description);
            }
            var card = new Border { Child = panel, Width = width, CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 10, 12, 10), Margin = new Thickness(0, 0, 8, 8) };
            card.SetResourceReference(Border.BackgroundProperty, "Sidebar");
            return card;
        }

        private static TextBlock Text(string value, double size, string color)
        {
            var text = new TextBlock { Text = Value(value), FontSize = size, TextWrapping = TextWrapping.Wrap };
            text.SetResourceReference(TextBlock.ForegroundProperty, color);
            return text;
        }
        private static string Value(string text) { return String.IsNullOrWhiteSpace(text) ? "—" : text; }
        private static string Number(int? value) { return value.HasValue ? value.Value.ToString("N0") : "—"; }
        private static string Seconds(long milliseconds) { return (milliseconds / 1000d).ToString("0.000", CultureInfo.InvariantCulture) + " s"; }
        private static string Money(decimal value) { return "USD " + value.ToString("0.000000", CultureInfo.InvariantCulture); }
        private static string CostPart(decimal? value) { return value.HasValue ? value.Value.ToString("0.######", CultureInfo.InvariantCulture) : "—"; }
        private static string LocalTime(string value)
        {
            DateTimeOffset time;
            return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
                ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff") : Value(value);
        }
    }
}
