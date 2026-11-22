using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ClaudeUI
{
    internal sealed class RequestOverviewLayout
    {
        private readonly StackPanel host;
        public RequestOverviewLayout(StackPanel host) { this.host = host; }

        public void Show(IList<RequestStat> visible)
        {
            host.Children.Clear();
            var calls = visible.Where(row => !row.ExecutionOnly).ToList();
            var metrics = new ResponsiveCardPanel { MaximumColumns = 4, MinimumColumnWidth = 100, Gap = 12 };
            metrics.Children.Add(Card("模型调用", calls.Count.ToString("N0") + " 次", "当前筛选范围", 24));
            var inputs = calls.Where(row => row.TotalInput.HasValue).ToList();
            var outputs = calls.Where(row => row.Output.HasValue).ToList();
            metrics.Children.Add(Card("输入 TOKEN", inputs.Count == 0 ? "—" : inputs.Sum(row => row.TotalInput.Value).ToString("N0"),
                "含缓存写入与读取" + (inputs.Count < calls.Count ? " · 部分用量未返回" : ""), 24));
            metrics.Children.Add(Card("输出 TOKEN", outputs.Count == 0 ? "—" : outputs.Sum(row => (long)row.Output.Value).ToString("N0"),
                outputs.Count < calls.Count ? "部分用量未返回" : "已报告的输出用量", 24));

            var costs = new List<Border>();
            var estimates = visible.Where(row => row.EstimatedCost.HasValue)
                .GroupBy(row => new { row.EstimateCurrency, Configured = row.PricingStatus == "已按配置计费" })
                .OrderBy(group => group.Key.EstimateCurrency).ThenByDescending(group => group.Key.Configured)
                .Select(group => (group.Key.Configured ? "配置费用 " : "参考估算 ") + GatewayCharge.Format(group.Sum(row => row.EstimatedCost.Value), group.Key.EstimateCurrency));
            string estimate = String.Join(Environment.NewLine, estimates);
            int noEstimate = calls.Count(row => !row.EstimatedCost.HasValue);
            costs.Add(Card("本地费用（非账单）", String.IsNullOrWhiteSpace(estimate) ? "未估算" : estimate,
                "非网关账单" + (noEstimate > 0 ? " · " + noEstimate + " 次未估算" : " · 已估算全部调用"), 14));
            foreach (var card in costs) metrics.Children.Add(card);
            host.Children.Add(metrics);
        }

        private static Border Card(string title, string value, string note, double size)
        {
            var body = new StackPanel();
            var label = new TextBlock { Text = title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = title };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            body.Children.Add(label);
            var metric = new TextBlock { Text = value, FontSize = size, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 44, ToolTip = value };
            metric.SetResourceReference(TextBlock.ForegroundProperty, title.StartsWith("本地费用", StringComparison.Ordinal) ? "Accent" : "Ink");
            body.Children.Add(metric);
            var card = new Border { Child = body, CornerRadius = new CornerRadius(12), Padding = new Thickness(12), BorderThickness = new Thickness(1), ToolTip = note };
            card.SetResourceReference(Border.BackgroundProperty, "Panel");
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            return card;
        }
    }
}
