using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    // Only MIT price data is embedded; no LiteLLM runtime or network dependency.
    internal static class RequestPricing
    {
        private static readonly IDictionary<string, object> catalog = Load();
        private static IDictionary<string, object> Load()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClaudeUI.Prices.json"))
            using (var reader = new StreamReader(stream))
                return new JavaScriptSerializer().DeserializeObject(reader.ReadToEnd()) as IDictionary<string, object>;
        }

        public static void Apply(RequestStat row, IEnumerable<RequestRate> overrides)
        {
            if (row.EstimatedCost.HasValue || row.PricingStatus != null) return;
            if (row.ExecutionOnly || row.Incomplete) { row.PricingStatus = "请求未完成，费用未知"; return; }
            if (row.Input < 0 || row.Output < 0 || row.CacheRead < 0 || row.CacheWrite < 0 || row.CacheWrite1Hour < 0)
            { row.PricingStatus = "Token 用量无效，未计费"; return; }
            var rules = (overrides ?? Enumerable.Empty<RequestRate>()).Where(rate => rate != null).ToList();
            var candidates = rules.Where(rate => rate.Matches(row)).ToList();
            // Gateways may return a canonical model ID instead of the requested alias.
            // Preserve the response ID and only use the explicit request ID if no response rule applies.
            if (candidates.Count == 0 && !String.IsNullOrWhiteSpace(row.RequestedModel))
                candidates = rules.Where(rate => rate.Model == row.RequestedModel &&
                    (String.IsNullOrEmpty(rate.Gateway) || rate.Gateway == row.Gateway)).ToList();
            // A gateway-specific rule wins over a model-wide rule, including when disabled.
            var exact = candidates.Where(rate => !String.IsNullOrEmpty(rate.Gateway)).ToList();
            if (exact.Count > 0) candidates = exact;
            if (candidates.Count > 1) { row.PricingStatus = "计费配置冲突，未计费"; return; }
            var configured = candidates.FirstOrDefault();
            if (configured != null)
            {
                if (!configured.Enabled) { row.PricingStatus = "计费规则已停用"; return; }
                string invalid = configured.Validate();
                if (invalid != null) { row.PricingStatus = "计费配置无效：" + invalid; return; }
                bool peak;
                try { peak = configured.IsPeak(row.StartedUtc); }
                catch (ArgumentException error) { row.PricingStatus = error.Message; return; }
                if (configured.BillingMode == "Request")
                {
                    row.EstimatedInputCost = 0;
                    row.EstimatedCacheCost = 0;
                    row.EstimatedOutputCost = 0;
                    row.EstimatedCost = peak ? configured.PeakPerRequest : configured.PerRequest;
                }
                else
                {
                    if (!row.Input.HasValue || !row.Output.HasValue || row.Input < 0 || row.Output < 0 || row.CacheRead < 0 || row.CacheWrite < 0)
                    { row.PricingStatus = "Token 用量缺失或无效，未计费"; return; }
                    row.EstimatedInputCost = row.Input.Value * (peak ? configured.PeakInput : configured.Input) / configured.Unit;
                    row.EstimatedCacheCost = ((row.CacheWrite ?? 0) * (peak ? configured.PeakCacheWrite : configured.CacheWrite) + (row.CacheRead ?? 0) * (peak ? configured.PeakCacheRead : configured.CacheRead)) / configured.Unit;
                    row.EstimatedOutputCost = row.Output.Value * (peak ? configured.PeakOutput : configured.Output) / configured.Unit;
                }
                row.EstimateCurrency = configured.Currency;
                row.PriceSource = "用户计费规则（采集时快照） · " + configured.Model + " · " +
                    (String.IsNullOrEmpty(configured.Gateway) ? "全部网关" : configured.Gateway) + " · " + configured.Currency + " · " +
                    (configured.PeakEnabled ? (peak ? "高峰" : "空闲") + " · " : "") +
                    (configured.BillingMode == "Request" ? GatewayCharge.Format(row.EstimatedCost.Value, configured.Currency) + "/次" :
                    "每 " + configured.Unit + " token：未缓存输入 " + (peak ? configured.PeakInput : configured.Input) +
                    " / 写缓存 " + (peak ? configured.PeakCacheWrite : configured.CacheWrite) + " / 读缓存 " + (peak ? configured.PeakCacheRead : configured.CacheRead) +
                    " / 输出 " + (peak ? configured.PeakOutput : configured.Output));
            }
            else
            {
                if (!row.Input.HasValue || !row.Output.HasValue) { row.PricingStatus = "Token 用量缺失，未估算"; return; }
                var price = StatisticsJson.Map(StatisticsJson.Map(catalog, "models"), row.Model ?? "");
                if (price == null) { row.PricingStatus = "未配置该模型计费规则"; return; } // Exact IDs only.
                string tier = row.TotalInput > 100000 && StatisticsJson.Number(price, "input_cost_per_token_above_100k_tokens").HasValue
                    ? "_above_100k_tokens" : "";
                decimal? input = StatisticsJson.Number(price, "input_cost_per_token" + tier);
                decimal? output = StatisticsJson.Number(price, "output_cost_per_token" + tier);
                decimal? write = StatisticsJson.Number(price, "cache_creation_input_token_cost" + tier);
                decimal? read = StatisticsJson.Number(price, "cache_read_input_token_cost" + tier);
                decimal? hour = StatisticsJson.Number(price, "cache_creation_input_token_cost_above_1hr" + tier);
                if (!input.HasValue || !output.HasValue || ((row.CacheWrite ?? 0) > 0 && !write.HasValue) ||
                    ((row.CacheRead ?? 0) > 0 && !read.HasValue) || ((row.CacheWrite1Hour ?? 0) > 0 && !hour.HasValue))
                { row.PricingStatus = "参考价格缺失，未估算"; return; }
                int hourTokens = Math.Min(row.CacheWrite1Hour ?? 0, row.CacheWrite ?? 0);
                row.EstimatedInputCost = row.Input.Value * input.Value;
                row.EstimatedOutputCost = row.Output.Value * output.Value;
                row.EstimatedCacheCost = ((row.CacheWrite ?? 0) - hourTokens) * (write ?? 0) + hourTokens * (hour ?? 0) + (row.CacheRead ?? 0) * (read ?? 0);
                row.EstimateCurrency = "USD";
                row.PriceSource = "LiteLLM " + catalog["revision"] + " · " + catalog["updated"] + " · 标准 API 参考价（非账单）";
            }
            if (!row.EstimatedCost.HasValue) row.EstimatedCost = row.EstimatedInputCost + row.EstimatedCacheCost + row.EstimatedOutputCost;
            row.PricingStatus = configured == null ? "公开参考价" : "已按配置计费";
        }
    }

    internal sealed class StatisticsFilter
    {
        public string Request, Model, Session, Status;
        public string ModelDisplayName;
        public DateTime? FromLocal, ToLocal;

        // Inclusive local calendar dates; offsets are evaluated separately at each boundary for DST.
        public IList<RequestStat> Apply(IEnumerable<RequestStat> rows)
        {
            if (FromLocal.HasValue && ToLocal.HasValue && FromLocal.Value.Date > ToLocal.Value.Date)
                return new List<RequestStat>();
            return rows.Where(row =>
            {
                if (!String.IsNullOrEmpty(Session) && row.SessionId != Session) return false;
                if (!String.IsNullOrEmpty(Model) && row.Model != Model) return false;
                if (!String.IsNullOrEmpty(ModelDisplayName) && row.ModelDisplayName != ModelDisplayName) return false;
                if (!String.IsNullOrEmpty(Status) && row.StatusText != Status) return false;
                if (!String.IsNullOrWhiteSpace(Request) && !(row.RequestId ?? "").Contains(Request.Trim()) && !(row.RunId ?? "").Contains(Request.Trim())) return false;
                DateTimeOffset started;
                if (FromLocal.HasValue || ToLocal.HasValue)
                {
                    if (!DateTimeOffset.TryParse(row.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out started)) return false;
                    var local = started.LocalDateTime.Date;
                    if (FromLocal.HasValue && local < FromLocal.Value.Date || ToLocal.HasValue && local > ToLocal.Value.Date) return false;
                }
                return true;
            }).Reverse().ToList();
        }
    }
}
