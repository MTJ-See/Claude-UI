using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal sealed class ClaudeStream
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private bool streamedText;
        private readonly Queue<string> activityChanges = new Queue<string>();
        private readonly Dictionary<string, RequestStat> requestById = new Dictionary<string, RequestStat>();
        private readonly Dictionary<string, RequestStat> currentByParent = new Dictionary<string, RequestStat>();
        private readonly HashSet<RequestStat> streamedRequests = new HashSet<RequestStat>();
        private readonly Dictionary<RequestStat, GatewayCharge> responseCharges = new Dictionary<RequestStat, GatewayCharge>();
        private readonly List<RequestStat> requests = new List<RequestStat>();
        private readonly string runId = Guid.NewGuid().ToString("N");
        private DateTime observedUtc;
        private string resultReason, resultSubtype;
        private decimal? resultCost;
        private IDictionary<string, object> resultUsage;
        public string SessionId, Model, Result, Failure, Activity, Compaction;
        public int PermissionDenials;
        public int? ModelTurns, PreCompactTokens, PostCompactTokens, ApiErrorStatus;
        public bool Finished;
        internal bool HasCompleteModelResponse
        {
            get { return requests.Any(row => !row.Incomplete && !row.ExecutionOnly) ||
                Finished && ModelTurns == 1 && resultUsage != null &&
                Token(resultUsage, "input_tokens").HasValue && Token(resultUsage, "output_tokens").HasValue; }
        }
        public IEnumerable<string> RequestIds { get { return requests.Select(row => row.RequestId).Where(id => !String.IsNullOrEmpty(id)); } }

        public string Read(string line)
        {
            return Read(line, DateTime.UtcNow);
        }

        public string Read(string line, DateTime receivedUtc)
        {
            observedUtc = receivedUtc.ToUniversalTime();
            if (String.IsNullOrEmpty(line) || line[0] != '{') return null;
            IDictionary<string, object> item;
            try { item = json.DeserializeObject(line) as IDictionary<string, object>; }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            string type = GetString(item, "type");
            if (type == "system" && GetString(item, "subtype") == "init")
            {
                ReadSession(item);
                Model = GetString(item, "model");
                SetActivity("等待模型响应");
            }
            else if (type == "system" && GetString(item, "subtype") == "compact_boundary")
            {
                var metadata = GetObject(item, "compact_metadata");
                Compaction = GetString(metadata, "trigger");
                SetActivity("上下文压缩完成");
                PreCompactTokens = Token(metadata, "pre_tokens");
                PostCompactTokens = Token(metadata, "post_tokens");
            }
            else if (type == "stream_event")
            {
                var update = GetObject(item, "event");
                string updateType = GetString(update, "type");
                TrackStream(item, update, updateType);
                if (updateType == "message_start")
                {
                    streamedText = false;
                    string model = GetString(GetObject(update, "message"), "model");
                    if (model.Length > 0) Model = model;
                    SetActivity("模型处理中");
                }
                if (updateType == "content_block_start")
                {
                    var block = GetObject(update, "content_block");
                    string blockType = GetString(block, "type");
                    if (blockType == "thinking") SetActivity("思考中");
                    else if (blockType == "text") SetActivity("生成回复");
                    else if (blockType == "tool_use") SetActivity("调用工具 · " + GetString(block, "name"));
                }
                if (updateType == "content_block_delta")
                {
                    var delta = GetObject(update, "delta");
                    if (GetString(delta, "type") == "thinking_delta") SetActivity("思考中");
                    if (GetString(delta, "type") == "text_delta")
                    {
                        SetActivity("生成回复");
                        streamedText = true;
                        return GetString(delta, "text");
                    }
                }
            }
            else if (type == "assistant")
            {
                var message = GetObject(item, "message");
                TrackAssistant(item, message);
                var text = new List<string>();
                var blocks = GetArray(message, "content");
                foreach (object value in blocks)
                {
                    var block = value as IDictionary<string, object>;
                    switch (GetString(block, "type"))
                    {
                        case "thinking":
                            if (!streamedText) SetActivity("思考中");
                            break;
                        case "text":
                            SetActivity("生成回复");
                            if (!streamedText) text.Add(GetString(block, "text"));
                            break;
                        case "tool_use":
                            SetActivity("调用工具 · " + GetString(block, "name"));
                            break;
                    }
                }
                streamedText = false;
                return text.Count == 0 ? null : String.Join(Environment.NewLine, text);
            }
            else if (type == "tool_progress")
            {
                string tool = GetString(item, "tool_name");
                SetActivity("工具执行中" + (tool.Length > 0 ? " · " + tool : ""));
            }
            else if (type == "user")
            {
                foreach (object value in GetArray(GetObject(item, "message"), "content"))
                    if (GetString(value as IDictionary<string, object>, "type") == "tool_result")
                        SetActivity("工具执行完成，等待模型");
            }
            else if (type == "system" && GetString(item, "status") == "compacting")
                SetActivity("压缩上下文");
            else if (type == "result")
            {
                ReadSession(item);
                Finished = true;
                Result = GetString(item, "result");
                resultReason = GetString(item, "stop_reason");
                resultSubtype = GetString(item, "subtype");
                resultCost = StatisticsJson.Number(item, "total_cost_usd");
                resultUsage = StatisticsJson.Map(item, "usage");
                ModelTurns = Get(item, "num_turns") as int?;
                ApiErrorStatus = Get(item, "api_error_status") as int?;
                PermissionDenials = GetArray(item, "permission_denials").Length;
                string subtype = GetString(item, "subtype");
                if (Object.Equals(Get(item, "is_error"), true) || subtype.StartsWith("error", StringComparison.Ordinal))
                {
                    var errors = GetArray(item, "errors");
                    Failure = !String.IsNullOrWhiteSpace(Result) ? Result :
                        errors.Length != 0 ? String.Join(Environment.NewLine, errors) :
                        subtype == "error_max_turns" ? "已达到最大轮数。" : subtype;
                    if (String.IsNullOrWhiteSpace(Failure)) Failure = "Claude CLI 返回了执行错误。";
                }
            }
            return null;
        }

        private void SetActivity(string activity)
        {
            if (Activity == activity) return;
            Activity = activity;
            activityChanges.Enqueue(activity);
            if (activityChanges.Count > 128) activityChanges.Dequeue();
        }

        public string[] DrainActivities()
        {
            var stages = activityChanges.ToArray();
            activityChanges.Clear();
            return stages;
        }

        public string ValidateManualCompaction(string sessionId)
        {
            if (Compaction == "manual" && SessionId != null &&
                (sessionId == null || String.Equals(sessionId, SessionId, StringComparison.OrdinalIgnoreCase))) return null;
            return String.IsNullOrWhiteSpace(Result)
                ? "Claude CLI 未确认压缩完成，详情见执行日志。"
                : Result;
        }

        private void TrackStream(IDictionary<string, object> envelope, IDictionary<string, object> update, string type)
        {
            string parentId = GetString(envelope, "parent_tool_use_id");
            string id = GetString(envelope, "api_message_id");
            RequestStat row;
            if (type == "message_start")
            {
                var message = GetObject(update, "message");
                if (id.Length == 0) id = GetString(message, "id");
                row = GetRequest(id, GetString(message, "model"));
                if (row == null) return;
                currentByParent[parentId] = row;
                streamedRequests.Add(row);
                UpdateUsage(row, StatisticsJson.Map(message, "usage"));
            }
            else if (type == "message_delta" || type == "message_stop")
            {
                if (!(id.Length > 0 ? requestById.TryGetValue(id, out row) : currentByParent.TryGetValue(parentId, out row))) return;
                var charge = GatewayCharge.Read(update);
                if (charge != null) responseCharges[row] = charge;
                UpdateUsage(row, StatisticsJson.Map(update, "usage"));
                if (type == "message_stop")
                {
                    row.Incomplete = false;
                    Complete(row, observedUtc);
                    RequestStat active;
                    if (currentByParent.TryGetValue(parentId, out active) && active == row) currentByParent.Remove(parentId);
                }
                else
                {
                    string reason = GetString(GetObject(update, "delta"), "stop_reason");
                    if (reason.Length > 0) row.EndReason = reason;
                }
            }
        }

        private void TrackAssistant(IDictionary<string, object> envelope, IDictionary<string, object> message)
        {
            string id = GetString(envelope, "api_message_id");
            if (id.Length == 0) id = GetString(message, "id");
            var row = GetRequest(id, GetString(message, "model"));
            if (row == null) return;
            // Assistant blocks may arrive before or after the authoritative final delta.
            if (!streamedRequests.Contains(row) || !row.Input.HasValue) UpdateUsage(row, StatisticsJson.Map(message, "usage"));
            string reason = GetString(message, "stop_reason");
            if (reason.Length == 0) return;
            if (String.IsNullOrEmpty(row.EndReason)) row.EndReason = reason;
            if (!streamedRequests.Contains(row))
            {
                row.Incomplete = false;
                Complete(row, observedUtc);
                row.DurationMs = null;
                row.TimingSource = "仅收到完整 assistant 快照，单请求耗时未知";
                var charge = GatewayCharge.Read(message);
                if (charge != null) responseCharges[row] = charge;
            }
        }

        private RequestStat GetRequest(string id, string model)
        {
            RequestStat row;
            if (id.Length == 0 || model.Length == 0 || model == "<synthetic>") return null;
            if (requestById.TryGetValue(id, out row)) return row;
            row = new RequestStat { RequestId = id, Model = model, Incomplete = true, StartedUtc = observedUtc.ToString("o", CultureInfo.InvariantCulture),
                TimingSource = "首个响应事件至最终事件（不含首字等待）" };
            requestById.Add(id, row);
            requests.Add(row);
            return row;
        }

        public IList<RequestStat> Statistics(DateTime start, GatewayRoute gateway, string session, string fallbackModel,
            decimal? baseline, bool stopped, bool failed, string interruption = "手动停止", string operation = "对话")
        {
            string termination = stopped ? interruption : failed ?
                (String.IsNullOrEmpty(resultSubtype) || resultSubtype == "success" ? "CLI 执行失败" : resultSubtype) : null;
            if (requests.Count == 0 && Finished && ModelTurns == 1 && termination == null && resultUsage != null)
            {
                var row = new RequestStat { Model = Model ?? fallbackModel, EndReason = resultReason,
                    StartedUtc = start.ToString("o", CultureInfo.InvariantCulture), TimingSource = "CLI 执行耗时（无请求事件）" };
                UpdateUsage(row, resultUsage);
                requests.Add(row);
            }
            int actualCount = requests.Count;
            decimal? runCost = baseline.HasValue && resultCost.HasValue && resultCost.Value >= baseline.Value
                ? resultCost - baseline : null;
            if (actualCount == 1 && !requests[0].Incomplete && !failed && !stopped && ModelTurns == 1)
            {
                var row = requests[0];
                // Result usage can include historical/context/subagent usage. Prefer the request's own final events.
                if (resultUsage != null && !row.Input.HasValue) UpdateUsage(row, resultUsage);
                row.CliEstimate = runCost;
            }
            if (actualCount > 0 && termination != null && requests[actualCount - 1].Incomplete)
                requests[actualCount - 1].EndReason = termination;
            else if (termination != null)
                requests.Add(new RequestStat { Model = Model ?? fallbackModel, EndReason = termination, ExecutionOnly = true,
                    StartedUtc = start.ToString("o", CultureInfo.InvariantCulture), TimingSource = "CLI 执行耗时（未获得完整响应）" });
            for (int i = 0; i < requests.Count; i++)
            {
                var row = requests[i];
                row.SchemaVersion = 2;
                row.RequestedModel = fallbackModel;
                if (String.IsNullOrEmpty(row.RequestId)) row.RequestId = "local-" + runId + "-" + i;
                row.RunId = runId;
                row.RunStartedUtc = start.ToString("o", CultureInfo.InvariantCulture);
                row.RunCompletedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                row.RunDurationMs = Math.Max(0, (long)(DateTime.UtcNow - start).TotalMilliseconds);
                row.Operation = operation;
                if (row.CompletedUtc == null) Complete(row, DateTime.UtcNow);
                row.Status = row.ExecutionOnly || row.Incomplete ? (stopped ? "已取消" : failed ? "失败" : "不完整") : "成功";
                row.SessionId = session ?? SessionId;
                row.Gateway = gateway == null ? "—" : gateway.Endpoint;
                row.GatewaySource = gateway == null ? "CLI 未启动或未识别网关" : gateway.Source;
                GatewayCharge charge;
                if (!row.Incomplete && responseCharges.TryGetValue(row, out charge)) charge.Apply(row);
                row.CallsInRun = actualCount;
                row.RunEstimate = runCost;
                if (String.IsNullOrEmpty(row.EndReason))
                    row.EndReason = i == actualCount - 1 && !String.IsNullOrEmpty(resultReason) ? resultReason : "未报告";
            }
            return requests;
        }

        private static void UpdateUsage(RequestStat row, IDictionary<string, object> usage)
        {
            row.Input = Token(usage, "input_tokens") ?? row.Input;
            row.CacheWrite = Token(usage, "cache_creation_input_tokens") ?? row.CacheWrite;
            row.CacheRead = Token(usage, "cache_read_input_tokens") ?? row.CacheRead;
            row.CacheWrite1Hour = Token(StatisticsJson.Map(usage, "cache_creation"), "ephemeral_1h_input_tokens") ?? row.CacheWrite1Hour;
            row.Output = Token(usage, "output_tokens") ?? row.Output;
        }

        private static void Complete(RequestStat row, DateTime end)
        {
            if (row.CompletedUtc != null) return;
            row.CompletedUtc = end.ToString("o", CultureInfo.InvariantCulture);
            DateTimeOffset started;
            if (DateTimeOffset.TryParse(row.StartedUtc, CultureInfo.InvariantCulture, DateTimeStyles.None, out started))
                row.DurationMs = Math.Max(0, (long)(end - started.UtcDateTime).TotalMilliseconds);
        }

        private static int? Token(IDictionary<string, object> usage, string key)
        {
            decimal? value = StatisticsJson.Number(usage, key);
            return value.HasValue && value.Value >= 0 && value.Value <= Int32.MaxValue ? (int?)value.Value : null;
        }

        private void ReadSession(IDictionary<string, object> item)
        {
            Guid identifier;
            if (Guid.TryParse(GetString(item, "session_id"), out identifier)) SessionId = identifier.ToString();
        }

        private static object Get(IDictionary<string, object> item, string key)
        {
            object value;
            return item != null && item.TryGetValue(key, out value) ? value : null;
        }

        private static string GetString(IDictionary<string, object> item, string key) { return Get(item, key) as string ?? ""; }
        private static IDictionary<string, object> GetObject(IDictionary<string, object> item, string key)
        {
            return Get(item, key) as IDictionary<string, object>;
        }
        private static object[] GetArray(IDictionary<string, object> item, string key) { return Get(item, key) as object[] ?? new object[0]; }
    }
}
