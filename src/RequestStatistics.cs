using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    public sealed class RequestStat
    {
        public int SchemaVersion { get; set; }
        public string RequestId { get; set; }
        public string RunId { get; set; }
        public string RunStartedUtc { get; set; }
        public string RunCompletedUtc { get; set; }
        public long? RunDurationMs { get; set; }
        public string Operation { get; set; }
        public string CompletedUtc { get; set; }
        public long? DurationMs { get; set; }
        public string TimingSource { get; set; }
        public string Status { get; set; }
        public int? CacheWrite1Hour { get; set; }
        public decimal? EstimatedInputCost { get; set; }
        public decimal? EstimatedCacheCost { get; set; }
        public decimal? EstimatedOutputCost { get; set; }
        public decimal? EstimatedCost { get; set; }
        public string EstimateCurrency { get; set; }
        public string PriceSource { get; set; }
        public string PricingStatus { get; set; }
        [ScriptIgnore] public string StatusText { get { return Status ?? (Incomplete ? "不完整" : "历史记录"); } }
        [ScriptIgnore] public string DurationText { get { return DurationMs.HasValue ? (DurationMs.Value / 1000d).ToString("0.000", CultureInfo.InvariantCulture) + " s" : "—"; } }
        [ScriptIgnore] public string EstimateText { get { return EstimatedCost.HasValue ? GatewayCharge.Format(EstimatedCost.Value, EstimateCurrency) : PricingStatus ?? "未估算"; } }
        public string SessionId { get; set; }
        public string Model { get; set; }
        public string RequestedModel { get; set; }
        public string RequestedModelName { get; set; }
        [ScriptIgnore] public string ModelDisplayName { get; set; }
        public string Gateway { get; set; }
        public string GatewaySource { get; set; }
        public string EndReason { get; set; }
        public string StartedUtc { get; set; }
        public int? Input { get; set; }
        public int? CacheWrite { get; set; }
        public int? CacheRead { get; set; }
        public int? Output { get; set; }
        public decimal? CliEstimate { get; set; }
        public decimal? RunEstimate { get; set; }
        public decimal? BilledAmount { get; set; }
        public string BilledCurrency { get; set; }
        public string BillingField { get; set; }
        public int CallsInRun { get; set; }
        public bool Incomplete { get; set; }
        public bool ExecutionOnly { get; set; }

        [ScriptIgnore] public string TimeText
        {
            get
            {
                DateTimeOffset time;
                return DateTimeOffset.TryParse(StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out time)
                    ? time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : "—";
            }
        }
        [ScriptIgnore] public long? TotalInput
        {
            get { return Input.HasValue ? (long)Input.Value + (CacheWrite ?? 0) + (CacheRead ?? 0) : (long?)null; }
        }
        [ScriptIgnore] public string InputText { get { return TotalInput.HasValue ? TotalInput.Value.ToString("N0") : "—"; } }
        [ScriptIgnore] public string OutputText { get { return Output.HasValue ? Output.Value.ToString("N0") : "—"; } }
        [ScriptIgnore] public bool HasBilledAmount { get { return BilledAmount.HasValue; } }
        [ScriptIgnore] public string BilledText
        {
            get { return HasBilledAmount ? GatewayCharge.Format(BilledAmount.Value, BilledCurrency) : ExecutionOnly ? "—" : "未返回"; }
        }
        [ScriptIgnore] public string GatewayText
        {
            get { Uri uri; return Uri.TryCreate(Gateway, UriKind.Absolute, out uri) ? uri.Authority : Gateway; }
        }
    }

    internal static class RequestModelDisplay
    {
        internal static string Resolve(RequestStat row, IEnumerable<ModelProfile> profiles)
        {
            if (!String.IsNullOrWhiteSpace(row.RequestedModelName)) return row.RequestedModelName;
            var configured = (profiles ?? Enumerable.Empty<ModelProfile>()).Where(p => p != null && !String.IsNullOrWhiteSpace(p.Name)).ToList();
            string id = String.IsNullOrWhiteSpace(row.RequestedModel) ? row.Model : row.RequestedModel;
            var matches = configured.Where(p => p.ModelId == id).ToList();
            if (matches.Count != 1)
                matches = matches.Where(p => !String.IsNullOrEmpty(p.BaseUrl) && p.BaseUrl.TrimEnd('/') == (row.Gateway ?? "").TrimEnd('/')).ToList();
            // Legacy records did not retain the requested ID. Only resolve a gateway with one configured model.
            if (matches.Count == 0 && String.IsNullOrWhiteSpace(row.RequestedModel))
                matches = configured.Where(p => !String.IsNullOrEmpty(p.BaseUrl) && p.BaseUrl.TrimEnd('/') == (row.Gateway ?? "").TrimEnd('/')).ToList();
            return matches.Count == 1 ? matches[0].Name : "未记录配置名称";
        }
    }

    public sealed class RequestRate
    {
        public RequestRate() { Enabled = true; BillingMode = "Token"; Unit = 1000000; }
        public string Gateway { get; set; }
        public string Model { get; set; }
        public string Currency { get; set; }
        public decimal Input { get; set; }
        public decimal CacheWrite { get; set; }
        public decimal CacheRead { get; set; }
        public decimal Output { get; set; }
        public bool Enabled { get; set; }
        public string BillingMode { get; set; }
        public int Unit { get; set; }
        public decimal PerRequest { get; set; }
        public bool PeakEnabled { get; set; }
        public decimal PeakInput { get; set; }
        public decimal PeakCacheWrite { get; set; }
        public decimal PeakCacheRead { get; set; }
        public decimal PeakOutput { get; set; }
        public decimal PeakPerRequest { get; set; }
        public string Holidays { get; set; }

        [ScriptIgnore] public string DisplayName { get { return (Enabled ? "启用 · " : "停用 · ") + Model + Environment.NewLine + Currency + " · " + (String.IsNullOrEmpty(Gateway) ? "全部网关" : Gateway); } }

        internal bool Matches(RequestStat row) { return (String.IsNullOrEmpty(Gateway) || Gateway == row.Gateway) && Model == row.Model; }

        internal string Validate()
        {
            if (String.IsNullOrWhiteSpace(Model) || Model.Length > 256 || Model.Any(Char.IsWhiteSpace)) return "模型标识不能为空、包含空白或超过 256 个字符。";
            if (Gateway != null && (Gateway.Length > 1024 || Gateway != Gateway.Trim())) return "网关范围不能超过 1024 个字符或含首尾空白。";
            if (Currency != "USD" && Currency != "CNY") return "结算币种仅支持 USD 或 CNY。";
            if (BillingMode != "Token" && BillingMode != "Request") return "请选择按 Token 或按次计费。";
            if (BillingMode == "Token" && Unit != 1 && Unit != 1000 && Unit != 1000000 || BillingMode == "Request" && Unit != 1) return "计费单位无效。";
            if (new[] { Input, CacheWrite, CacheRead, Output, PerRequest, PeakInput, PeakCacheWrite, PeakCacheRead, PeakOutput, PeakPerRequest }.Any(x => x < 0 || x > 1000000)) return "单价必须在 0 到 1,000,000 之间。";
            DateTime day;
            if (HolidayDates().Any(s => !DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day))) return "节假日请填写 yyyy-MM-dd 日期，以逗号或换行分隔。";
            return null;
        }

        private string[] HolidayDates() { return (Holidays ?? "").Split(new[] { ',', '，', '\r', '\n', ';', '；' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).Where(s => s.Length > 0).ToArray(); }

        internal bool IsPeak(string startedUtc)
        {
            if (!PeakEnabled) return false;
            DateTimeOffset started;
            if (!DateTimeOffset.TryParse(startedUtc, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out started)) throw new ArgumentException("缺少有效请求时间，无法选择峰谷价格。");
            var beijing = started.ToOffset(TimeSpan.FromHours(8));
            return beijing.DayOfWeek != DayOfWeek.Saturday && beijing.DayOfWeek != DayOfWeek.Sunday &&
                !HolidayDates().Contains(beijing.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)) &&
                (beijing.TimeOfDay >= TimeSpan.FromHours(9) && beijing.TimeOfDay < TimeSpan.FromHours(12) ||
                 beijing.TimeOfDay >= TimeSpan.FromHours(14) && beijing.TimeOfDay < TimeSpan.FromHours(18));
        }

        internal decimal? Estimate(RequestStat row)
        {
            if (!Enabled || Validate() != null || row.Incomplete || row.ExecutionOnly) return null;
            bool peak = IsPeak(row.StartedUtc);
            if (BillingMode == "Request") return peak ? PeakPerRequest : PerRequest;
            if (!row.Input.HasValue || !row.Output.HasValue) return null;
            return (row.Input.Value * (peak ? PeakInput : Input) + (row.CacheWrite ?? 0) * (peak ? PeakCacheWrite : CacheWrite) +
                (row.CacheRead ?? 0) * (peak ? PeakCacheRead : CacheRead) + row.Output.Value * (peak ? PeakOutput : Output)) / Unit;
        }
    }

    internal static class StatisticsJson
    {
        public static IDictionary<string, object> Map(IDictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? value as IDictionary<string, object> : null;
        }

        public static decimal? Number(IDictionary<string, object> data, string key)
        {
            object value;
            decimal number;
            return data != null && data.TryGetValue(key, out value) && value != null &&
                Decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
                ? (decimal?)number : null;
        }
    }

    internal sealed class GatewayCharge
    {
        public readonly decimal Amount;
        public readonly string Currency, Field;

        private GatewayCharge(decimal amount, string currency, string field)
        {
            Amount = amount;
            Currency = currency;
            Field = field;
        }

        public static GatewayCharge Read(IDictionary<string, object> message)
        {
            string currency = CurrencyCode(message);
            return ReadMoney(StatisticsJson.Map(message, "billing"), "billing", currency, "billed_amount", "amount") ??
                ReadMoney(message, "", currency, "billed_amount") ??
                ReadMoney(StatisticsJson.Map(message, "usage"), "usage", currency, "billed_amount", "cost");
        }

        private static GatewayCharge ReadMoney(IDictionary<string, object> data, string path, string outerCurrency, params string[] amounts)
        {
            string currency = CurrencyCode(data) ?? outerCurrency;
            string prefix = path.Length == 0 ? "" : path + ".";
            foreach (string key in amounts)
            {
                var money = StatisticsJson.Map(data, key);
                decimal? amount = money == null ? StatisticsJson.Number(data, key) : StatisticsJson.Number(money, "amount");
                string unit = CurrencyCode(money) ?? currency;
                string field = prefix + key + (money == null ? "" : ".amount");
                if (amount >= 0 && unit != null)
                    return new GatewayCharge(amount.Value, unit, field);
                decimal? dollars = StatisticsJson.Number(data, key + "_usd");
                if (dollars >= 0 && (unit == null || unit == "USD"))
                    return new GatewayCharge(dollars.Value, "USD", prefix + key + "_usd");
                if (amount >= 0)
                    return new GatewayCharge(amount.Value, null, field);
            }
            return null;
        }

        private static string CurrencyCode(IDictionary<string, object> data)
        {
            object raw;
            if (data == null || !data.TryGetValue("currency", out raw) || !(raw is string)) return null;
            string code = ((string)raw).Trim().ToUpperInvariant();
            return code.Length == 3 && code.All(c => c >= 'A' && c <= 'Z') ? code : null;
        }

        public void Apply(RequestStat row)
        {
            row.BilledAmount = Amount;
            row.BilledCurrency = Currency;
            row.BillingField = Field;
        }

        public static string Format(decimal amount, string currency)
        {
            string value = amount.ToString("0.############################", CultureInfo.InvariantCulture);
            return String.IsNullOrEmpty(currency) ? value : currency + " " + value;
        }
    }

    internal sealed class StatisticsStore
    {
        private static readonly object Gate = new object();
        private readonly string path;
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };

        public StatisticsStore(string directory) { path = Path.Combine(directory, "request-statistics.jsonl"); }

        public List<RequestStat> Read()
        {
            var result = new List<RequestStat>();
            if (!File.Exists(path)) return result;
            foreach (string line in File.ReadLines(path, Encoding.UTF8))
            {
                if (String.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var batch = serializer.Deserialize<RequestStat[]>(line);
                    if (batch != null) result.AddRange(batch.Where(item => item != null));
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
            return result;
        }

        public void Append(IList<RequestStat> rows)
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.AppendAllText(path, Environment.NewLine + serializer.Serialize(rows) + Environment.NewLine, new UTF8Encoding(false));
            }
        }

        public void Delete(IList<RequestStat> selected, bool all)
        {
            lock (Gate)
            {
                var remaining = all ? new List<RequestStat>() : Read();
                if (!all)
                {
                    // Match complete records, including legacy rows without request/run IDs.
                    var counts = selected.GroupBy(row => serializer.Serialize(row)).ToDictionary(group => group.Key, group => group.Count());
                    remaining = remaining.Where(row => {
                        string key = serializer.Serialize(row);
                        int count;
                        if (!counts.TryGetValue(key, out count) || count == 0) return true;
                        counts[key] = count - 1;
                        return false;
                    }).ToList();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(file, new UTF8Encoding(false)))
                        foreach (var row in remaining) writer.WriteLine(serializer.Serialize(new[] { row }));
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }

    internal sealed class GatewayRoute
    {
        public readonly string Endpoint, Source;

        private GatewayRoute(string endpoint, string source) { Endpoint = endpoint; Source = source; }

        public static GatewayRoute Resolve(string workspace, IDictionary<string, string> environment)
        {
            const string key = "ANTHROPIC_BASE_URL";
            string endpoint, configuration;
            if (environment != null && environment.TryGetValue(key, out endpoint))
                return new GatewayRoute(SafeEndpoint(endpoint) ?? "自定义网关", "本次 CLI 进程指定");
            configuration = environment != null && environment.ContainsKey("CLAUDE_CONFIG_DIR") ? environment["CLAUDE_CONFIG_DIR"] : null;
            var resolved = new Dictionary<string, string>();
            foreach (string name in new[] { key, "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
                resolved[name] = Environment.GetEnvironmentVariable(name);
            const string source = "按本机 CLI 配置推断";
            try
            {
                foreach (string settings in new[] {
                    Path.Combine(configuration ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"), "settings.json"),
                    Path.Combine(workspace, ".claude", "settings.json"),
                    Path.Combine(workspace, ".claude", "settings.local.json"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode", "managed-settings.json")
                })
                {
                    if (!File.Exists(settings)) continue;
                    var values = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(settings)) as IDictionary<string, object>;
                    var variables = StatisticsJson.Map(values, "env");
                    if (variables == null) continue;
                    foreach (string name in resolved.Keys.ToArray())
                    {
                        object value;
                        if (variables.TryGetValue(name, out value) && value is string) resolved[name] = (string)value;
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            foreach (var provider in new[] { "BEDROCK", "VERTEX", "FOUNDRY" })
                if (new[] { "1", "true" }.Contains(resolved["CLAUDE_CODE_USE_" + provider], StringComparer.OrdinalIgnoreCase))
                    return new GatewayRoute(provider, source);
            return new GatewayRoute(SafeEndpoint(resolved[key]) ?? "CLI 默认", source + "，CLI 输出未报告实际 URL");
        }

        private static string SafeEndpoint(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) ||
                (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return null;
            // Remove user info, query and fragment before storing the configured address.
            return uri.Scheme + "://" + uri.Authority + uri.AbsolutePath.TrimEnd('/');
        }
    }

    internal static class SessionCost
    {
        public static decimal? ReadBaseline(Conversation session)
        {
            if (String.IsNullOrEmpty(session.SessionId)) return 0m;
            Guid id;
            if (!Guid.TryParse(session.SessionId, out id)) return null;
            try
            {
                string projects = Path.Combine(session.CliConfigDirectory, "projects");
                if (!Directory.Exists(projects)) return null;
                string[] locations = Directory.GetDirectories(projects);
                var serializer = new JavaScriptSerializer();
                foreach (string directory in locations)
                {
                    string path = Path.Combine(directory, id.ToString() + ".jsonl");
                    if (!File.Exists(path)) continue;
                    decimal? amount = null;
                    foreach (string line in File.ReadLines(path, Encoding.UTF8))
                    {
                        if (line.Length > 1000000 || line.IndexOf("\"cost-state\"", StringComparison.Ordinal) < 0) continue;
                        var entry = serializer.DeserializeObject(line) as IDictionary<string, object>;
                        object type;
                        if (entry != null && entry.TryGetValue("type", out type) && Object.Equals(type, "cost-state"))
                            amount = StatisticsJson.Number(entry, "totalCostUSD");
                    }
                    return amount;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            return null;
        }
    }
}
