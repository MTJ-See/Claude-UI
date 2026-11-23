using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal static class ModelProtocol
    {
        public const string Auto = "auto", Anthropic = "anthropic", Chat = "openai-chat", Responses = "openai-responses", Gemini = "gemini";
        public static readonly string[] Values = { Auto, Anthropic, Chat, Responses, Gemini };
        public static readonly string[] Labels = { "自动识别（按接口地址）", "Anthropic Messages", "OpenAI Chat Completions", "OpenAI Responses", "Gemini generateContent" };
        internal static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 200 }; }
        internal static Dictionary<string, object> Obj(params object[] pairs)
        {
            var result = new Dictionary<string, object>();
            for (int i = 0; i < pairs.Length; i += 2) result[(string)pairs[i]] = pairs[i + 1];
            return result;
        }
        internal static object Get(object value, string key)
        {
            var map = value as IDictionary<string, object>; object found;
            return map != null && map.TryGetValue(key, out found) ? found : null;
        }
        internal static string Str(object value) { return value == null ? "" : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture); }
        internal static IEnumerable<object> Items(object value)
        {
            return value is string || value == null ? Enumerable.Empty<object>() : (value as IEnumerable ?? new object[0]).Cast<object>();
        }
        internal static string Text(object content)
        {
            if (content is string) return (string)content;
            return String.Join("\n", Items(content).Where(x => Str(Get(x, "type")) == "text").Select(x => Str(Get(x, "text"))));
        }
        internal static string Resolve(string protocol, string url)
        {
            if (protocol != Auto && !String.IsNullOrEmpty(protocol)) return protocol;
            // Saved profiles without this field retain the legacy Anthropic route.
            if (String.IsNullOrEmpty(protocol)) return Anthropic;
            string path = new Uri(url).AbsolutePath.TrimEnd('/');
            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return Chat;
            if (path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase)) return Responses;
            if (path.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase)) return Gemini;
            return Anthropic;
        }
        internal static string Normalize(string value)
        {
            Uri uri;
            if (!Uri.TryCreate(value, UriKind.Absolute, out uri) || String.IsNullOrEmpty(uri.Host) ||
                (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)) ||
                uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0) return null;
            return uri.AbsoluteUri.TrimEnd('/');
        }
        internal static string Endpoint(string url, string protocol, string model)
        {
            string path = new Uri(url).AbsolutePath.TrimEnd('/');
            string suffix = protocol == Chat ? "/chat/completions" : protocol == Responses ? "/responses" : "/messages";
            if (protocol == Gemini)
            {
                if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("接口地址与所选协议不一致，请修改协议或地址。");
                int marker = url.IndexOf("/models/", StringComparison.OrdinalIgnoreCase);
                string root = marker >= 0 ? url.Substring(0, marker) : url;
                if (!root.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase) && !root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) root += "/v1beta";
                return root + "/models/" + Uri.EscapeDataString(model) + ":generateContent";
            }
            if (path.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) {
                if (protocol == Anthropic && !path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Anthropic 完整接口应以 /v1/messages 结尾。");
                return url;
            }
            if (path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/responses", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/messages", StringComparison.OrdinalIgnoreCase) || path.IndexOf("/models/", StringComparison.OrdinalIgnoreCase) >= 0)
                throw new InvalidOperationException("接口地址与所选协议不一致，请修改协议或地址。");
            return url + (path.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "" : "/v1") + suffix;
        }
        internal static object Request(object source, string protocol, string model)
        {
            var input = new List<object>(); var toolNames = new Dictionary<string, string>();
            foreach (object message in Items(Get(source, "messages")))
                foreach (object block in Items(Get(message, "content")))
                    if (Str(Get(block, "type")) == "tool_use") toolNames[Str(Get(block, "id"))] = Str(Get(block, "name"));
            string system = Text(Get(source, "system"));
            if (protocol == Chat && system.Length > 0) input.Add(Obj("role", "system", "content", system));
            foreach (object message in Items(Get(source, "messages")))
            {
                string role = Str(Get(message, "role"));
                var content = new List<object>(); var calls = new List<object>(); object raw = Get(message, "content");
                IEnumerable<object> blocks = raw is string ? new[] { Obj("type", "text", "text", raw) } : Items(raw);
                foreach (object block in blocks)
                {
                    string type = Str(Get(block, "type"));
                    if (type == "text")
                        content.Add(protocol == Gemini ? Obj("text", Get(block, "text")) :
                            Obj("type", protocol == Responses ? (role == "assistant" ? "output_text" : "input_text") : "text", "text", Get(block, "text")));
                    else if (type == "image")
                    {
                        object image = Get(block, "source");
                        string uri = Str(Get(image, "type")) == "url" ? Str(Get(image, "url")) :
                            "data:" + Str(Get(image, "media_type")) + ";base64," + Str(Get(image, "data"));
                        if (protocol == Gemini)
                        {
                            if (Str(Get(image, "type")) != "base64") throw new InvalidOperationException("Gemini 图片需要本地附件，请重新附加图片。");
                            content.Add(Obj("inlineData", Obj("mimeType", Get(image, "media_type"), "data", Get(image, "data"))));
                        }
                        else content.Add(protocol == Chat ? Obj("type", "image_url", "image_url", Obj("url", uri)) : Obj("type", "input_image", "image_url", uri));
                    }
                    else if (type == "tool_use")
                    {
                        string id = Str(Get(block, "id")), name = Str(Get(block, "name")); object args = Get(block, "input") ?? Obj();
                        if (protocol == Chat) calls.Add(Obj("id", id, "type", "function", "function", Obj("name", name, "arguments", Json().Serialize(args))));
                        else if (protocol == Responses)
                        {
                            Flush(input, content, role, protocol);
                            input.Add(Obj("type", "function_call", "call_id", id, "name", name, "arguments", Json().Serialize(args)));
                        }
                        else
                        {
                            var part = Obj("functionCall", Obj("name", name, "args", args));
                            if (id.StartsWith("gemini_", StringComparison.Ordinal))
                            {
                                string encoded = id.Substring(40).Replace('-', '+').Replace('_', '/');
                                part["thoughtSignature"] = Encoding.UTF8.GetString(Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '=')));
                            }
                            content.Add(part);
                        }
                    }
                    else if (type == "tool_result")
                    {
                        string id = Str(Get(block, "tool_use_id")); string output = Text(Get(block, "content"));
                        if (protocol != Gemini) Flush(input, content, role, protocol);
                        if (protocol == Chat) input.Add(Obj("role", "tool", "tool_call_id", id, "content", output));
                        else if (protocol == Responses) input.Add(Obj("type", "function_call_output", "call_id", id, "output", output));
                        else
                        {
                            string name; if (!toolNames.TryGetValue(id, out name)) throw new InvalidOperationException("工具结果缺少对应的工具调用。");
                            content.Add(Obj("functionResponse", Obj("name", name, "response", Obj("output", output))));
                        }
                    }
                    else if (type != "thinking" && type != "redacted_thinking")
                        throw new InvalidOperationException("此协议暂不支持消息内容类型：" + type);
                }
                if (protocol == Chat && calls.Count > 0)
                {
                    input.Add(Obj("role", "assistant", "content", content.Count == 0 ? null : (object)content.ToArray(), "tool_calls", calls.ToArray()));
                    content.Clear();
                }
                Flush(input, content, role, protocol);
            }
            var request = protocol == Gemini ? Obj("contents", input.ToArray()) :
                Obj("model", model, protocol == Chat ? "messages" : "input", input.ToArray(), "stream", false);
            object max = Get(source, "max_tokens");
            if (protocol == Gemini)
            {
                if (system.Length > 0) request["systemInstruction"] = Obj("parts", new[] { Obj("text", system) });
                var generation = Obj(); if (max != null) generation["maxOutputTokens"] = max;
                foreach (string key in new[] { "temperature", "top_p", "top_k" })
                    if (Get(source, key) != null) generation[key == "top_p" ? "topP" : key == "top_k" ? "topK" : key] = Get(source, key);
                request["generationConfig"] = generation;
            }
            else
            {
                if (max != null) request[protocol == Chat ? "max_completion_tokens" : "max_output_tokens"] = max;
                if (protocol == Responses) { request["store"] = false; if (system.Length > 0) request["instructions"] = system; }
                foreach (string key in new[] { "temperature", "top_p" }) if (Get(source, key) != null) request[key] = Get(source, key);
            }
            var tools = new List<object>();
            foreach (object tool in Items(Get(source, "tools")))
            {
                if (Get(tool, "name") == null || Get(tool, "input_schema") == null) throw new InvalidOperationException("此协议暂不支持服务端专用工具。");
                var definition = Obj("name", Get(tool, "name"), "description", Str(Get(tool, "description")), "parameters", Get(tool, "input_schema"));
                if (protocol == Responses) { definition["type"] = "function"; definition["strict"] = false; }
                tools.Add(protocol == Chat ? Obj("type", "function", "function", definition) : definition);
            }
            if (tools.Count > 0) request["tools"] = protocol == Gemini ? new[] { Obj("functionDeclarations", tools.ToArray()) } : (object)tools.ToArray();
            object choice = Get(source, "tool_choice"); string mode = Str(Get(choice, "type"));
            if (mode.Length > 0 && tools.Count > 0)
            {
                if (protocol == Gemini)
                {
                    var config = Obj("mode", mode == "any" || mode == "tool" ? "ANY" : mode == "none" ? "NONE" : "AUTO");
                    if (mode == "tool") config["allowedFunctionNames"] = new[] { Str(Get(choice, "name")) };
                    request["toolConfig"] = Obj("functionCallingConfig", config);
                }
                else request["tool_choice"] = mode == "tool" ?
                    (protocol == Chat ? Obj("type", "function", "function", Obj("name", Get(choice, "name"))) : Obj("type", "function", "name", Get(choice, "name"))) :
                    (object)(mode == "any" ? "required" : mode == "none" ? "none" : "auto");
            }
            if (protocol == Chat && Get(source, "stop_sequences") != null) request["stop"] = Get(source, "stop_sequences");
            if (protocol == Gemini && Get(source, "stop_sequences") != null) ((Dictionary<string, object>)request["generationConfig"])["stopSequences"] = Get(source, "stop_sequences");
            return request;
        }
        private static void Flush(List<object> input, List<object> content, string role, string protocol)
        {
            if (content.Count == 0) return;
            input.Add(protocol == Gemini ? Obj("role", role == "assistant" ? "model" : "user", "parts", content.ToArray()) :
                Obj("role", role, "content", content.ToArray())); content.Clear();
        }
        internal static object Response(object source, string protocol, string model)
        {
            if (Get(source, "error") != null) throw new InvalidOperationException("上游接口返回错误。");
            var content = new List<object>(); string reason = "end_turn"; object usage = Get(source, "usage");
            long input = 0, output = 0, cached = 0;
            if (protocol == Chat)
            {
                object choice = Items(Get(source, "choices")).FirstOrDefault(), message = Get(choice, "message");
                string text = Str(Get(message, "content")); if (text.Length > 0) content.Add(Obj("type", "text", "text", text));
                foreach (object call in Items(Get(message, "tool_calls")))
                    content.Add(Tool(Str(Get(call, "id")), Get(Get(call, "function"), "name"), Get(Get(call, "function"), "arguments")));
                string finish = Str(Get(choice, "finish_reason"));
                if (finish == "length") reason = "max_tokens";
                if (finish == "content_filter") throw new InvalidOperationException("模型响应被上游内容过滤器拦截。");
                input = Number(Get(usage, "prompt_tokens")); output = Number(Get(usage, "completion_tokens"));
                cached = Number(Get(Get(usage, "prompt_tokens_details"), "cached_tokens"));
            }
            else if (protocol == Responses)
            {
                if (Str(Get(source, "status")) == "failed" || Str(Get(source, "status")) == "cancelled") throw new InvalidOperationException("上游 Responses 请求未成功完成。");
                foreach (object item in Items(Get(source, "output")))
                {
                    if (Str(Get(item, "type")) == "function_call") content.Add(Tool(Str(Get(item, "call_id")), Get(item, "name"), Get(item, "arguments")));
                    foreach (object part in Items(Get(item, "content")))
                    {
                        string type = Str(Get(part, "type"));
                        if (type == "output_text") content.Add(Obj("type", "text", "text", Get(part, "text")));
                        if (type == "refusal") content.Add(Obj("type", "text", "text", Get(part, "refusal")));
                    }
                }
                if (Str(Get(Get(source, "incomplete_details"), "reason")) == "max_output_tokens") reason = "max_tokens";
                input = Number(Get(usage, "input_tokens")); output = Number(Get(usage, "output_tokens")); cached = Number(Get(Get(usage, "input_tokens_details"), "cached_tokens"));
            }
            else
            {
                object candidate = Items(Get(source, "candidates")).FirstOrDefault(); string finish = Str(Get(candidate, "finishReason"));
                if (finish == "MAX_TOKENS") reason = "max_tokens";
                if (finish.Length > 0 && finish != "STOP" && finish != "MAX_TOKENS") throw new InvalidOperationException("Gemini 未正常完成响应：" + finish);
                foreach (object part in Items(Get(Get(candidate, "content"), "parts")))
                {
                    if (Get(part, "text") != null && !Object.Equals(Get(part, "thought"), true)) content.Add(Obj("type", "text", "text", Get(part, "text")));
                    object call = Get(part, "functionCall");
                    if (call != null)
                    {
                        string signature = Str(Get(part, "thoughtSignature"));
                        string id = signature.Length == 0 ? "call_" + Guid.NewGuid().ToString("N") :
                            "gemini_" + Guid.NewGuid().ToString("N") + "_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(signature)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                        content.Add(Obj("type", "tool_use", "id", id, "name", Get(call, "name"), "input", Get(call, "args") ?? Obj()));
                    }
                }
                usage = Get(source, "usageMetadata"); input = Number(Get(usage, "promptTokenCount")); cached = Number(Get(usage, "cachedContentTokenCount"));
                output = Number(Get(usage, "candidatesTokenCount")) + Number(Get(usage, "thoughtsTokenCount"));
            }
            if (content.Count == 0) throw new InvalidOperationException("上游未返回文本或工具调用，请检查模型能力与协议。");
            if (content.Any(x => Str(Get(x, "type")) == "tool_use")) reason = "tool_use";
            string reportedModel = Str(Get(source, protocol == Gemini ? "modelVersion" : "model"));
            return Obj("id", "msg_" + Guid.NewGuid().ToString("N"), "type", "message", "role", "assistant", "model", reportedModel.Length == 0 ? model : reportedModel, "content", content.ToArray(),
                "stop_reason", reason, "stop_sequence", null, "usage", Obj("input_tokens", Math.Max(0, input - cached), "output_tokens", output, "cache_read_input_tokens", cached, "cache_creation_input_tokens", 0));
        }
        private static long Number(object value) { return value == null ? 0 : Convert.ToInt64(value); }
        private static object Tool(string id, object name, object arguments)
        {
            object parsed = arguments is string ? Json().DeserializeObject((string)arguments) : arguments;
            if (!(parsed is IDictionary<string, object>)) throw new InvalidOperationException("模型返回了无效的工具调用参数。");
            return Obj("type", "tool_use", "id", String.IsNullOrEmpty(id) ? "call_" + Guid.NewGuid().ToString("N") : id, "name", name, "input", parsed);
        }
        internal static string Events(object message)
        {
            var events = new StringBuilder(); var start = new Dictionary<string, object>((IDictionary<string, object>)message);
            start["content"] = new object[0]; start["stop_reason"] = null;
            var usage = new Dictionary<string, object>((IDictionary<string, object>)Get(message, "usage")); usage["output_tokens"] = 0; start["usage"] = usage;
            Event(events, "message_start", Obj("message", start)); int index = 0;
            foreach (object block in Items(Get(message, "content")))
            {
                bool tool = Str(Get(block, "type")) == "tool_use";
                Event(events, "content_block_start", Obj("index", index, "content_block", tool ?
                    Obj("type", "tool_use", "id", Get(block, "id"), "name", Get(block, "name"), "input", Obj()) : Obj("type", "text", "text", "")));
                Event(events, "content_block_delta", Obj("index", index, "delta", tool ?
                    Obj("type", "input_json_delta", "partial_json", Json().Serialize(Get(block, "input"))) : Obj("type", "text_delta", "text", Get(block, "text"))));
                Event(events, "content_block_stop", Obj("index", index++));
            }
            Event(events, "message_delta", Obj("delta", Obj("stop_reason", Get(message, "stop_reason"), "stop_sequence", null), "usage", Get(message, "usage")));
            Event(events, "message_stop", Obj()); return events.ToString();
        }
        private static void Event(StringBuilder buffer, string type, Dictionary<string, object> data)
        {
            data["type"] = type; buffer.Append("event: ").Append(type).Append("\ndata: ").Append(Json().Serialize(data)).Append("\n\n");
        }
    }
}
