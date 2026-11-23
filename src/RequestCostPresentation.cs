using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeUI
{
    // 只关联同轮已保存的费用快照，不按当前费率重算，也不向终止事件复制收费金额。
    internal static class RequestCostPresentation
    {
        internal static void Attach(IList<RequestStat> rows)
        {
            foreach (var row in rows) row.CompletedCostRows = null;
            foreach (var run in rows.Where(row => !String.IsNullOrWhiteSpace(row.RunId)).GroupBy(row => row.RunId))
            {
                var completed = run.Where(row => !row.ExecutionOnly && !row.Incomplete).ToArray();
                foreach (var interrupted in run.Where(row => row.ExecutionOnly || row.Incomplete || row.Status == "已取消"))
                    interrupted.CompletedCostRows = completed.Where(row => !Object.ReferenceEquals(row, interrupted)).ToArray();
            }
        }

        internal static string CompletedText(IEnumerable<RequestStat> rows)
        {
            var charges = (rows ?? Enumerable.Empty<RequestStat>()).Select(row => new {
                Amount = row.HasBilledAmount ? row.BilledAmount : row.EstimatedCost,
                Currency = row.HasBilledAmount ? row.BilledCurrency : row.EstimateCurrency,
                Kind = row.HasBilledAmount ? "网关扣费" : row.PricingStatus == "已按配置计费" ? "配置费用" : "参考估算"
            }).Where(row => row.Amount.HasValue);
            return String.Join("；", charges.GroupBy(row => new { row.Kind, row.Currency })
                .Select(group => group.Key.Kind + " " + GatewayCharge.Format(group.Sum(row => row.Amount.Value), group.Key.Currency) +
                    (String.IsNullOrWhiteSpace(group.Key.Currency) ? "（币种未提供）" : "")));
        }

        internal static long? SumUsage(IEnumerable<RequestStat> rows, Func<RequestStat, long?> selector)
        {
            var values = (rows ?? Enumerable.Empty<RequestStat>()).Select(selector).Where(value => value.HasValue && value.Value >= 0).ToList();
            return values.Count == 0 ? (long?)null : values.Sum(value => value.Value);
        }

        internal static IList<RequestStat> CostScope(IList<RequestStat> visible)
        {
            // 仅筛选取消记录时也能看见关联费用；同时选中源请求时只计入一次。
            return visible.Concat(visible.SelectMany(row => row.CompletedCostRows ?? new RequestStat[0])).Distinct().ToList();
        }
    }
}
