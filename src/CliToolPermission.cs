using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal sealed class CliToolPermission
    {
        public readonly string RequestId, Tool, Reason;
        public readonly IDictionary<string, object> Input;

        public CliToolPermission(string id, string tool, string reason, IDictionary<string, object> input)
        {
            RequestId = id;
            Tool = tool;
            Reason = reason;
            Input = input;
        }

        public string DescribeInput()
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var text = new StringBuilder();
            foreach (var value in Input)
            {
                if (text.Length != 0) text.AppendLine().AppendLine();
                text.AppendLine(value.Key).Append(value.Value as string ?? json.Serialize(value.Value));
            }
            return text.Length == 0 ? "无参数" : text.ToString();
        }
    }

    internal sealed class CliPermissionChannel
    {
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
        private readonly Stream input;
        private readonly Func<CliToolPermission, CancellationToken, Task<bool>> authorize;
        private readonly List<CliToolPermission> waiting = new List<CliToolPermission>();
        private CliToolPermission active;
        private CancellationTokenSource promptLifetime;
        private Task<bool> decision;
        private bool rejected;

        public CliPermissionChannel(Stream input, Func<CliToolPermission, CancellationToken, Task<bool>> authorize)
        {
            this.input = input;
            this.authorize = authorize;
        }

        public async Task ReadAsync(StreamReader reader, string prompt, Action<string, bool> output, CancellationToken cancellation, object userContent = null)
        {
            var next = reader.ReadLineAsync();
            try
            {
                await WriteAsync(new {
                    type = "user", message = new { role = "user", content = userContent ?? prompt },
                    parent_tool_use_id = (string)null, session_id = ""
                }, cancellation);
                while (true)
                {
                    if (decision != null) await Task.WhenAny(next, decision);
                    if (decision != null && !next.IsCompleted)
                    {
                        bool allowed = await decision;
                        string id = active.RequestId;
                        object answer = allowed
                            ? (object)new { behavior = "allow", updatedInput = active.Input }
                            : new { behavior = "deny", message = "用户拒绝授权，结束当前请求。", interrupt = true };
                        ClearPrompt();
                        rejected = !allowed;
                        await ReplyAsync(id, answer, cancellation);
                    }
                    else
                    {
                        string line = await next;
                        if (line == null) break;
                        output(line, false);
                        if (await HandleAsync(line, cancellation))
                        {
                            ClearPrompt();
                            input.Close();
                            while ((line = await reader.ReadLineAsync()) != null) output(line, false);
                            return;
                        }
                        next = reader.ReadLineAsync();
                    }
                    if (active == null && !rejected && waiting.Count > 0)
                    {
                        active = waiting[0];
                        waiting.RemoveAt(0);
                        promptLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                        decision = authorize(active, promptLifetime.Token);
                    }
                }
            }
            finally
            {
                ClearPrompt();
                input.Close();
            }
        }

        private async Task<bool> HandleAsync(string line, CancellationToken cancellation)
        {
            if (line.Length == 0 || line[0] != '{' ||
                (line.IndexOf("\"control_", StringComparison.Ordinal) < 0 &&
                 line.IndexOf("\"result\"", StringComparison.Ordinal) < 0)) return false;
            IDictionary<string, object> envelope;
            try { envelope = json.DeserializeObject(line) as IDictionary<string, object>; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }

            string type = ReadString(envelope, "type");
            if (type == "result") return true;
            string id = ReadString(envelope, "request_id");
            if (String.IsNullOrEmpty(id)) return false;
            if (type == "control_cancel_request")
            {
                if (active != null && active.RequestId == id) ClearPrompt();
                waiting.RemoveAll(item => item.RequestId == id);
            }
            else if (type == "control_request")
            {
                if (rejected) return false;
                var request = StatisticsJson.Map(envelope, "request");
                string tool = ReadString(request, "tool_name");
                var arguments = StatisticsJson.Map(request, "input");
                if (ReadString(request, "subtype") == "can_use_tool" && !String.IsNullOrEmpty(tool) && arguments != null)
                    waiting.Add(new CliToolPermission(id, tool, ReadString(request, "decision_reason"), arguments));
                else
                    await WriteAsync(new {
                        type = "control_response",
                        response = new { subtype = "error", request_id = id, error = "Claude UI 无法处理此 CLI 控制请求。" }
                    }, cancellation);
            }
            return false;
        }

        private void ClearPrompt()
        {
            if (promptLifetime != null)
            {
                promptLifetime.Cancel();
                promptLifetime.Dispose();
            }
            promptLifetime = null;
            active = null;
            decision = null;
        }

        private Task ReplyAsync(string id, object answer, CancellationToken cancellation)
        {
            return WriteAsync(new {
                type = "control_response",
                response = new { subtype = "success", request_id = id, response = answer }
            }, cancellation);
        }

        private async Task WriteAsync(object message, CancellationToken cancellation)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json.Serialize(message) + "\n");
            await input.WriteAsync(bytes, 0, bytes.Length, cancellation);
            await input.FlushAsync(cancellation);
        }

        private static string ReadString(IDictionary<string, object> value, string key)
        {
            object entry;
            return value != null && value.TryGetValue(key, out entry) ? entry as string : null;
        }
    }
}
