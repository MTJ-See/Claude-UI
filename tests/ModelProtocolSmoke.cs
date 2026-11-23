using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUI
{
    internal static class ModelProtocolSmoke
    {
        private const string RequestJson = "{\"model\":\"ignored\",\"max_tokens\":100,\"stream\":true,\"system\":[{\"type\":\"text\",\"text\":\"Be helpful\"}],\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"Hello\"},{\"type\":\"image\",\"source\":{\"type\":\"base64\",\"media_type\":\"image/png\",\"data\":\"AAAA\"}}]}],\"tools\":[{\"name\":\"read_file\",\"description\":\"Read\",\"input_schema\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\"}}}}],\"tool_choice\":{\"type\":\"auto\"}}";
        private static object Parse(string json) { return ModelProtocol.Json().DeserializeObject(json); }
        private static object Get(object value, string key) { return ModelProtocol.Get(value, key); }
        private static string Str(object value) { return ModelProtocol.Str(value); }
        private static object First(object value) { return ModelProtocol.Items(value).First(); }
        private static void Check(bool test, string message) { if (!test) throw new Exception(message); }
        [STAThread]
        public static int Main(string[] args)
        {
            try
            {
                if (args.Contains("--fixture"))
                {
                    Check(Environment.GetEnvironmentVariable("CLAUDEUI_API_PROTOCOL") == null, "Internal protocol variable leaked to CLI.");
                    string secret = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
                    Check(secret != "synthetic-secret", "Upstream secret leaked to CLI.");
                    Console.WriteLine(Post(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL") + "/v1/messages", secret, RequestJson));
                    return 0;
                }
                if (args.Contains("--audit-effort")) { AuditEffort(); return 0; }
                Addresses();
                foreach (string protocol in new[] { ModelProtocol.Chat, ModelProtocol.Responses, ModelProtocol.Gemini })
                {
                    Conversion(protocol);
                    Transport(protocol, false);
                    Transport(protocol, true);
                    Failure(protocol);
                }
                Cancellation();
                Console.WriteLine("PASS protocol detection, endpoint validation, persistence, text/images/tools, Gemini signatures, usage, SSE, CLI bridge, auth, HTTP failures, cancellation.");
                return 0;
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        }
        private static ModelProfile Profile(string url, string protocol, bool apiKey)
        {
            return new ModelProfile { Name = "Protocol fixture", ModelId = "fixture-model", BaseUrl = url, ApiProtocol = protocol,
                Authentication = apiKey ? ModelProfile.ApiKey : ModelProfile.Bearer, ProtectedCredential = ModelProfile.Protect("synthetic-secret") };
        }
        private static void Addresses()
        {
            string root = "https://aitokendance.com/gateway";
            Check(ModelProtocol.Resolve(ModelProtocol.Auto, root + "/v1/chat/completions") == ModelProtocol.Chat, "Chat detection");
            Check(ModelProtocol.Resolve(ModelProtocol.Auto, root + "/v1/responses") == ModelProtocol.Responses, "Responses detection");
            Check(ModelProtocol.Resolve(ModelProtocol.Auto, root + "/v1beta/models/gemini-4-hydrogen:generateContent") == ModelProtocol.Gemini, "Gemini detection");
            Check(ModelProtocol.Endpoint(root + "/v1", ModelProtocol.Chat, "x") == root + "/v1/chat/completions", "Duplicate v1");
            Check(ModelProtocol.Endpoint(root + "/v1beta/models/old:generateContent", ModelProtocol.Gemini, "new") == root + "/v1beta/models/new:generateContent", "Gemini model ID must come from profile");
            Check(Profile(root + "/v1/messages", ModelProtocol.Anthropic, false).CliEnvironment()["ANTHROPIC_BASE_URL"] == root, "Anthropic endpoint normalization");
            Check(ModelProtocol.Normalize("https://user:password@example.com") == null && ModelProtocol.Normalize("http://example.com") == null &&
                ModelProtocol.Normalize("https://example.com?key=secret") == null, "Unsafe URL validation");
            foreach (string protocol in new[] { ModelProtocol.Chat, ModelProtocol.Responses, ModelProtocol.Gemini })
            {
                string wrong = protocol == ModelProtocol.Chat ? root + "/v1/responses" : root + "/v1/chat/completions";
                bool rejected = false; try { ModelProtocol.Endpoint(wrong, protocol, "x"); } catch (InvalidOperationException) { rejected = true; }
                Check(rejected, "Mismatched endpoint accepted");
            }
            var persisted = ModelProtocol.Json().Deserialize<ModelProfile>(ModelProtocol.Json().Serialize(Profile(root, ModelProtocol.Responses, false)));
            Check(persisted.ApiProtocol == ModelProtocol.Responses && !ModelProtocol.Json().Serialize(persisted).Contains("synthetic-secret"), "Profile persistence");
        }
        private static string Upstream(string protocol, bool tool)
        {
            if (protocol == ModelProtocol.Chat) return tool ?
                "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"tool_calls\":[{\"id\":\"call_fixture\",\"function\":{\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}}]}}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":3}}}" :
                "{\"model\":\"actual-model\",\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"OK\"}}],\"usage\":{\"prompt_tokens\":20,\"completion_tokens\":5,\"prompt_tokens_details\":{\"cached_tokens\":3}}}";
            if (protocol == ModelProtocol.Responses) return tool ?
                "{\"status\":\"completed\",\"output\":[{\"type\":\"function_call\",\"call_id\":\"call_fixture\",\"name\":\"read_file\",\"arguments\":\"{\\\"path\\\":\\\"a.txt\\\"}\"}],\"usage\":{\"input_tokens\":20,\"output_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":3}}}" :
                "{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"output_text\",\"text\":\"OK\"}]}],\"usage\":{\"input_tokens\":20,\"output_tokens\":5,\"input_tokens_details\":{\"cached_tokens\":3}}}";
            return "{\"candidates\":[{\"finishReason\":\"STOP\",\"content\":{\"parts\":[" +
                (tool ? "{\"thoughtSignature\":\"opaque/signature+fixture==\",\"functionCall\":{\"name\":\"read_file\",\"args\":{\"path\":\"a.txt\"}}}" : "{\"text\":\"OK\"}") +
                "]}}],\"usageMetadata\":{\"promptTokenCount\":20,\"candidatesTokenCount\":4,\"thoughtsTokenCount\":1,\"cachedContentTokenCount\":3}}";
        }
        private static void Conversion(string protocol)
        {
            object converted = ModelProtocol.Request(Parse(RequestJson), protocol, "fixture-model");
            string json = ModelProtocol.Json().Serialize(converted);
            Check(json.Contains("AAAA") && json.Contains("read_file") && json.Contains("Be helpful"), "Lost image, tool or system");
            Check(!json.Contains("max_tokens"), "Anthropic parameters leaked");
            object message = ModelProtocol.Response(Parse(Upstream(protocol, true)), protocol, "fixture-model");
            Check(Str(Get(message, "stop_reason")) == "tool_use", "Tool stop reason");
            object tool = First(Get(message, "content")); string id = Str(Get(tool, "id"));
            Check(Str(Get(Get(tool, "input"), "path")) == "a.txt", "Tool arguments");
            var history = (IDictionary<string, object>)Parse(RequestJson);
            history["messages"] = new object[] {
                ModelProtocol.Obj("role", "assistant", "content", new[] { tool }),
                ModelProtocol.Obj("role", "user", "content", new[] { ModelProtocol.Obj("type", "tool_result", "tool_use_id", id, "content", "file contents") })
            };
            string followup = ModelProtocol.Json().Serialize(ModelProtocol.Request(history, protocol, "fixture-model"));
            Check(followup.Contains("file contents"), "Tool output lost");
            if (protocol == ModelProtocol.Gemini) Check(followup.Contains("opaque/signature+fixture=="), "Gemini signature lost on replay");
            object usage = Get(message, "usage");
            Check(Convert.ToInt64(Get(usage, "input_tokens")) == 17 && Convert.ToInt64(Get(usage, "cache_read_input_tokens")) == 3 &&
                Convert.ToInt64(Get(usage, "output_tokens")) == 5, "Usage mapping double counted cached tokens");
            string events = ModelProtocol.Events(message);
            Check(events.Contains("input_json_delta") && events.Contains("message_stop"), "Tool SSE events");
            var stream = new ClaudeStream();
            foreach (string line in events.Split('\n').Where(x => x.StartsWith("data: ")))
                stream.Read("{\"type\":\"stream_event\",\"event\":" + line.Substring(6) + "}");
            Check(stream.HasCompleteModelResponse, "App parser rejected bridge SSE");
            bool rejected = false;
            try { ModelProtocol.Response(Parse("{}"), protocol, "x"); } catch (InvalidOperationException) { rejected = true; }
            Check(rejected, "Empty success accepted");
        }
        private sealed class Server : IDisposable
        {
            private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            public readonly string Url;
            public readonly Task Work;
            public string Header, Body;
            public Server(string response, int status, string contentType = "application/json")
            {
                listener.Start(); Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/gateway";
                Work = Task.Run(async () => {
                    using (TcpClient client = await listener.AcceptTcpClientAsync())
                    using (NetworkStream stream = client.GetStream())
                    {
                        stream.ReadTimeout = 10000;
                        var header = new StringBuilder(); int next;
                        while (!header.ToString().EndsWith("\r\n\r\n") && (next = stream.ReadByte()) >= 0) header.Append((char)next);
                        Header = header.ToString();
                        string length = Header.Split('\n').First(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)).Split(':')[1].Trim();
                        byte[] body = new byte[Int32.Parse(length)]; int offset = 0;
                        while (offset < body.Length) { int read = stream.Read(body, offset, body.Length - offset); if (read == 0) throw new IOException(); offset += read; }
                        Body = Encoding.UTF8.GetString(body);
                        byte[] bytes = Encoding.UTF8.GetBytes(response);
                        byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " Fixture\r\nContent-Type: " + contentType + "\r\nConnection: close\r\nContent-Length: " + bytes.Length + "\r\n\r\n");
                        stream.Write(headers, 0, headers.Length); stream.Write(bytes, 0, bytes.Length);
                    }
                });
            }
            public void Dispose() { listener.Stop(); }
        }
        private static string Post(string url, string secret, string json)
        {
            var request = (HttpWebRequest)WebRequest.Create(url); request.Method = "POST"; request.Proxy = null;
            request.Timeout = 10000; request.ContentType = "application/json"; request.Headers["Authorization"] = "Bearer " + secret;
            byte[] bytes = Encoding.UTF8.GetBytes(json); request.ContentLength = bytes.Length;
            using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            using (var response = request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
        }
        private static void Transport(string protocol, bool apiKey)
        {
            using (var server = new Server(Upstream(protocol, false), 200))
            {
                var profile = Profile(server.Url, protocol, apiKey);
                var environment = profile.CliEnvironment(); string original = environment["ANTHROPIC_BASE_URL"];
                var output = new StringBuilder();
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                {
                    int exit = new CliRunner().RunAsync(new CliInstallation(Process.GetCurrentProcess().MainModule.FileName),
                        System.Environment.CurrentDirectory, new[] { "--fixture" }, null, (line, error) => output.AppendLine(line), cancellation.Token, environment).GetAwaiter().GetResult();
                    Check(exit == 0 && output.ToString().Contains("message_stop") && output.ToString().Contains("OK"), "CLI bridge integration " + protocol + ": " + output);
                }
                server.Work.GetAwaiter().GetResult();
                string expectedPath = protocol == ModelProtocol.Chat ? "/gateway/v1/chat/completions" :
                    protocol == ModelProtocol.Responses ? "/gateway/v1/responses" : "/gateway/v1beta/models/fixture-model:generateContent";
                Check(server.Header.StartsWith("POST " + expectedPath + " "), "Upstream path " + server.Header);
                Check(server.Header.Contains(apiKey ? (protocol == ModelProtocol.Gemini ? "x-goog-api-key: synthetic-secret" : "x-api-key: synthetic-secret") : "Authorization: Bearer synthetic-secret"), "Upstream auth");
                Check(environment["ANTHROPIC_BASE_URL"] == original && GatewayRoute.Resolve(".", environment).Endpoint == original, "Gateway statistics mutated");
            }
        }
        private static void Failure(string protocol)
        {
            using (var server = new Server("{\"error\":\"secret must not leak\"}", 429))
            using (var bridge = ModelProtocolBridge.Start(Profile(server.Url, protocol, false).CliEnvironment(), CancellationToken.None))
            {
                bool denied = false;
                try { Post(bridge.Environment["ANTHROPIC_BASE_URL"] + "/v1/messages", "wrong", RequestJson); }
                catch (WebException ex) { denied = ((HttpWebResponse)ex.Response).StatusCode == HttpStatusCode.Unauthorized; ex.Response.Close(); }
                Check(denied, "Bridge accepted unauthenticated request");
                try { Post(bridge.Environment["ANTHROPIC_BASE_URL"] + "/v1/messages", bridge.Environment["ANTHROPIC_AUTH_TOKEN"], RequestJson); throw new Exception("Missing upstream failure"); }
                catch (WebException ex)
                {
                    Check((int)((HttpWebResponse)ex.Response).StatusCode == 429, "Upstream status lost");
                    using (var reader = new StreamReader(ex.Response.GetResponseStream())) Check(!reader.ReadToEnd().Contains("secret must not leak"), "Raw upstream body leaked");
                    ex.Response.Close();
                }
                server.Work.GetAwaiter().GetResult();
            }
        }
        private static void AuditEffort()
        {
            var cli = CliInstallation.Find(null, System.Environment.CurrentDirectory);
            Check(cli != null, "Local Claude CLI not found.");
            string work = Path.Combine(Path.GetTempPath(), "ClaudeUI-effort-audit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            foreach (string model in new[] { "deepseek-v4.1-flash-1017", "claude-opus-4-6" })
            foreach (string effort in new[] { "low", "medium", "high" })
            {
                object response = ModelProtocol.Response(Parse(Upstream(ModelProtocol.Chat, false)), ModelProtocol.Chat, model);
                using (var server = new Server(ModelProtocol.Events(response), 200, "text/event-stream"))
                using (var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    var profile = Profile(server.Url, null, false); profile.ModelId = model;
                    var environment = profile.CliEnvironment();
                    environment["CLAUDE_CONFIG_DIR"] = Path.Combine(work, "config");
                    environment["CLAUDE_CODE_MAX_RETRIES"] = "0";
                    environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "256";
                    var options = new RequestOptions { Model = model, Effort = effort, Permission = "default" };
                    var arguments = options.Arguments().Concat(new[] { "--safe-mode", "--strict-mcp-config", "--tools", "",
                        "--max-turns", "1", "--no-session-persistence", "--system-prompt", "Reply with OK." });
                    var output = new StringBuilder();
                    int exit = new CliRunner().RunAsync(cli, work, arguments, "hello",
                        (line, error) => { if (error) output.AppendLine(line); }, cancellation.Token, environment).GetAwaiter().GetResult();
                    Check(server.Work.Wait(5000), "No request received: " + output);
                    server.Work.GetAwaiter().GetResult();
                    object body = Parse(server.Body);
                    Check(Str(Get(Get(body, "output_config"), "effort")) == effort, "Selected effort did not reach actual CLI HTTP request.");
                    var conversions = new List<object>();
                    foreach (string protocol in new[] { ModelProtocol.Chat, ModelProtocol.Responses, ModelProtocol.Gemini })
                    {
                        object converted = ModelProtocol.Request(body, protocol, model);
                        conversions.Add(ModelProtocol.Obj("protocol", protocol,
                            "reasoning_effort", Get(converted, "reasoning_effort"),
                            "reasoning", Get(converted, "reasoning"),
                            "thinkingConfig", Get(Get(converted, "generationConfig"), "thinkingConfig")));
                    }
                    Console.WriteLine("CONVERSION " + ModelProtocol.Json().Serialize(conversions));
                    Console.WriteLine(ModelProtocol.Json().Serialize(ModelProtocol.Obj("model", model, "selected_effort", effort,
                        "cli_exit", exit, "output_config", Get(body, "output_config"), "thinking", Get(body, "thinking"),
                        "request_path", server.Header.Split('\r')[0])));
                }
            }
        }
        private static void Cancellation()
        {
            using (var cancellation = new CancellationTokenSource())
            using (var bridge = ModelProtocolBridge.Start(Profile("http://127.0.0.1:1", ModelProtocol.Chat, false).CliEnvironment(), cancellation.Token))
            {
                string url = bridge.Environment["ANTHROPIC_BASE_URL"];
                cancellation.Cancel(); bool stopped = false;
                try { Post(url + "/v1/messages", bridge.Environment["ANTHROPIC_AUTH_TOKEN"], RequestJson); }
                catch (WebException) { stopped = true; }
                Check(stopped, "Bridge listener survived cancellation");
            }
        }
    }
}
