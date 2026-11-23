using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUI
{
    // A per-CLI-run loopback bridge. The upstream credential never reaches the CLI.
    internal sealed class ModelProtocolBridge : IDisposable
    {
        private readonly TcpListener listener;
        private readonly CancellationTokenSource lifetime;
        private readonly CancellationToken stop;
        private readonly CancellationTokenRegistration registration;
        private readonly string endpoint, protocol, model, credential, authentication;
        private readonly string localSecret = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        private int disposed;
        internal readonly IDictionary<string, string> Environment;
        internal static ModelProtocolBridge Start(IDictionary<string, string> environment, CancellationToken cancellation)
        {
            string protocol;
            if (environment == null || !environment.TryGetValue("CLAUDEUI_API_PROTOCOL", out protocol)) return null;
            return new ModelProtocolBridge(environment, cancellation);
        }
        private ModelProtocolBridge(IDictionary<string, string> environment, CancellationToken cancellation)
        {
            protocol = environment["CLAUDEUI_API_PROTOCOL"];
            model = environment["ANTHROPIC_MODEL"];
            endpoint = ModelProtocol.Endpoint(environment["ANTHROPIC_BASE_URL"], protocol, model);
            credential = environment["ANTHROPIC_AUTH_TOKEN"] ?? environment["ANTHROPIC_API_KEY"];
            authentication = environment["CLAUDEUI_API_AUTH"];
            lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellation); stop = lifetime.Token;
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            registration = stop.Register(() => listener.Stop());
            Environment = new Dictionary<string, string>(environment);
            Environment.Remove("CLAUDEUI_API_PROTOCOL"); Environment.Remove("CLAUDEUI_API_AUTH");
            Environment["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            Environment["ANTHROPIC_AUTH_TOKEN"] = localSecret; Environment["ANTHROPIC_API_KEY"] = null;
            Task.Run((Func<Task>)AcceptAsync);
        }
        private async Task AcceptAsync()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    // Each handler owns its client and observes bridge cancellation.
                    Ignore(HandleAsync(client));
                }
            }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }
        private static void Ignore(Task task) { task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted); }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            using (stop.Register(() => client.Close()))
            {
                try
                {
                    using (NetworkStream stream = client.GetStream())
                    {
                        int status = 200; string contentType = "application/json", reply;
                        try
                        {
                            string header = await Header(stream).ConfigureAwait(false);
                            string[] lines = header.Split(new[] { "\r\n" }, StringSplitOptions.None);
                            string[] first = lines[0].Split(' ');
                            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            foreach (string line in lines.Skip(1))
                            {
                                int colon = line.IndexOf(':');
                                if (colon > 0) headers[line.Substring(0, colon)] = line.Substring(colon + 1).Trim();
                            }
                            string auth;
                            string path = first.Length > 1 ? first[1].Split('?')[0] : "";
                            if (!headers.TryGetValue("Authorization", out auth) || auth != "Bearer " + localSecret)
                            {
                                status = 401; reply = Error("本地协议桥认证失败。");
                            }
                            else if (first[0] != "POST" || path != "/v1/messages")
                            {
                                status = 404; reply = Error("此协议桥仅支持 Messages 请求；请使用 CLI 本地 token 估算。");
                            }
                            else
                            {
                                byte[] bytes = await Body(stream, headers).ConfigureAwait(false);
                                object source = ModelProtocol.Json().DeserializeObject(Encoding.UTF8.GetString(bytes));
                                object request = ModelProtocol.Request(source, protocol, model);
                                object response = await Send(request, stop).ConfigureAwait(false);
                                object message = ModelProtocol.Response(response, protocol, model);
                                bool streaming = Object.Equals(ModelProtocol.Get(source, "stream"), true);
                                contentType = streaming ? "text/event-stream" : "application/json";
                                reply = streaming ? ModelProtocol.Events(message) : ModelProtocol.Json().Serialize(message);
                            }
                        }
                        catch (WebException ex)
                        {
                            var upstream = ex.Response as HttpWebResponse;
                            status = upstream == null ? 502 : (int)upstream.StatusCode;
                            if (upstream != null) upstream.Dispose();
                            reply = Error("上游接口请求失败（HTTP " + status + "），请检查协议、模型 ID、认证及服务额度。");
                        }
                        catch (InvalidOperationException ex) { status = 400; reply = Error(ex.Message); }
                        catch (ArgumentException) { status = 502; reply = Error("协议转换失败：JSON 或工具参数无效。"); }
                        catch (FormatException) { status = 400; reply = Error("协议转换失败：工具签名或请求格式无效。"); }
                        catch (Exception ex)
                        {
                            if (ex is OperationCanceledException || ex is ObjectDisposedException || ex is IOException) return;
                            status = 502; reply = Error("协议转换失败，请检查接口返回格式。");
                        }
                        await Reply(stream, status, contentType, reply).ConfigureAwait(false);
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (SocketException) { }
                catch (OperationCanceledException) { }
            }
        }
        private async Task<object> Send(object data, CancellationToken token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(endpoint);
            request.Method = "POST"; request.ContentType = "application/json"; request.Accept = "application/json";
            request.AllowAutoRedirect = false; request.Timeout = 600000; request.ReadWriteTimeout = 600000;
            if (authentication == ModelProfile.Bearer) request.Headers["Authorization"] = "Bearer " + credential;
            else request.Headers[protocol == ModelProtocol.Gemini ? "x-goog-api-key" : "x-api-key"] = credential;
            byte[] body = Encoding.UTF8.GetBytes(ModelProtocol.Json().Serialize(data)); request.ContentLength = body.Length;
            using (token.Register(() => request.Abort()))
            {
                try
                {
                    using (Stream output = await request.GetRequestStreamAsync().ConfigureAwait(false))
                        await output.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
                    using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                    {
                        if ((int)response.StatusCode >= 300) throw new InvalidOperationException("接口发生重定向，请填写最终 API 地址。");
                        using (Stream input = response.GetResponseStream())
                        using (var buffer = new MemoryStream())
                        {
                            var chunk = new byte[8192]; int count;
                            while ((count = await input.ReadAsync(chunk, 0, chunk.Length, token).ConfigureAwait(false)) > 0)
                            {
                                if (buffer.Length + count > 64 * 1024 * 1024) throw new InvalidOperationException("接口响应超过 64 MB。");
                                buffer.Write(chunk, 0, count);
                            }
                            return ModelProtocol.Json().DeserializeObject(Encoding.UTF8.GetString(buffer.ToArray()));
                        }
                    }
                }
                catch (WebException) { token.ThrowIfCancellationRequested(); throw; }
            }
        }
        private static async Task<string> Header(Stream stream)
        {
            using (var buffer = new MemoryStream())
            {
                var one = new byte[1]; int state = 0;
                while (buffer.Length < 65536)
                {
                    if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) throw new IOException("Incomplete HTTP header.");
                    buffer.WriteByte(one[0]);
                    state = one[0] == (state % 2 == 0 ? 13 : 10) ? state + 1 : (one[0] == 13 ? 1 : 0);
                    if (state == 4) return Encoding.ASCII.GetString(buffer.ToArray());
                }
                throw new IOException("HTTP header too large.");
            }
        }
        private static async Task<byte[]> Body(Stream stream, IDictionary<string, string> headers)
        {
            string value;
            if (headers.TryGetValue("Transfer-Encoding", out value) && value.Equals("chunked", StringComparison.OrdinalIgnoreCase))
            {
                using (var result = new MemoryStream())
                {
                    while (true)
                    {
                        string line = await Line(stream).ConfigureAwait(false);
                        int size = Int32.Parse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber);
                        if (size == 0) break;
                        if (size < 0 || result.Length + size > 64 * 1024 * 1024) throw new IOException("Request too large.");
                        byte[] chunk = await Exact(stream, size).ConfigureAwait(false); result.Write(chunk, 0, size);
                        if (await Line(stream).ConfigureAwait(false) != "") throw new IOException("Invalid chunk.");
                    }
                    return result.ToArray();
                }
            }
            int length;
            if (!headers.TryGetValue("Content-Length", out value) || !Int32.TryParse(value, out length) || length < 0 || length > 64 * 1024 * 1024)
                throw new IOException("Invalid content length.");
            return await Exact(stream, length).ConfigureAwait(false);
        }
        private static async Task<string> Line(Stream stream)
        {
            var text = new StringBuilder(); var one = new byte[1];
            while (text.Length < 8192)
            {
                if (await stream.ReadAsync(one, 0, 1).ConfigureAwait(false) == 0) throw new IOException("Incomplete line.");
                if (one[0] == 10) return text.ToString().TrimEnd('\r');
                text.Append((char)one[0]);
            }
            throw new IOException("Line too large.");
        }
        private static async Task<byte[]> Exact(Stream stream, int length)
        {
            var bytes = new byte[length]; int offset = 0;
            while (offset < length)
            {
                int read = await stream.ReadAsync(bytes, offset, length - offset).ConfigureAwait(false);
                if (read == 0) throw new IOException("Incomplete HTTP body."); offset += read;
            }
            return bytes;
        }
        private static string Error(string message)
        {
            return ModelProtocol.Json().Serialize(ModelProtocol.Obj("type", "error", "error", ModelProtocol.Obj("type", "api_error", "message", message)));
        }
        private static async Task Reply(Stream stream, int status, string contentType, string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            byte[] header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " " + (status == 200 ? "OK" : "Error") +
                "\r\nContent-Type: " + contentType + "; charset=utf-8\r\nContent-Length: " + bytes.Length + "\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, 0, header.Length).ConfigureAwait(false);
            await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            lifetime.Cancel(); listener.Stop(); registration.Dispose();
            lifetime.Dispose();
        }
    }
}
