using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUI
{
    internal sealed class MarketplaceResponse
    {
        public string Json, Notice;
        public DateTime Expires;
    }
    // Only memory is used. Credentials are supplied by the current UI session.
    internal sealed class MarketplaceHttp
    {
        private readonly Dictionary<string, MarketplaceResponse> cache = new Dictionary<string, MarketplaceResponse>();
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1);
        private readonly Func<string> credential;
        private readonly Func<string, string, string, CancellationToken, MarketplaceResponse> transport;
        private DateTime nextRequest;
        public MarketplaceHttp(Func<string> credential, Func<string, string, string, CancellationToken, MarketplaceResponse> transport = null) { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; this.credential = credential; this.transport = transport ?? Request; }
        public void Clear() { lock (cache) cache.Clear(); }
        public async Task<MarketplaceResponse> GetAsync(string path, string body, bool refresh, CancellationToken token)
        {
            string secret = credential() ?? "";
            string identity;
            using (var hash = SHA256.Create()) identity = Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(secret)));
            string key = identity + path + body;
            await gate.WaitAsync(token);
            try
            {
                MarketplaceResponse found;
                lock (cache) if (!refresh && cache.TryGetValue(key, out found) && found.Expires > DateTime.UtcNow) return found;
                TimeSpan wait = nextRequest - DateTime.UtcNow;
                if (wait > TimeSpan.FromSeconds(10)) throw new InvalidOperationException("平台正在限流，请在 " + Math.Ceiling(wait.TotalSeconds) + " 秒后重试。");
                if (wait > TimeSpan.Zero) await Task.Delay(wait, token);
                var response = await Task.Run(() => transport(path, body, secret, token), token);
                token.ThrowIfCancellationRequested();
                response.Expires = DateTime.UtcNow.AddSeconds(45);
                lock (cache)
                {
                    if (cache.Count >= 64) cache.Clear();
                    cache[key] = response;
                }
                return response;
            }
            finally { nextRequest = nextRequest > DateTime.UtcNow ? nextRequest : DateTime.UtcNow.AddMilliseconds(350); gate.Release(); }
        }
        private MarketplaceResponse Request(string path, string body, string secret, CancellationToken token)
        {
            if (!path.StartsWith("/skills", StringComparison.Ordinal) && !path.StartsWith("/mcp/servers", StringComparison.Ordinal))
                throw new InvalidOperationException("不支持的市场接口。");
            var request = (HttpWebRequest)WebRequest.Create("https://modelscope.cn/openapi/v1" + path);
            request.Method = body == null ? "GET" : "PUT";
            request.Accept = "application/json";
            request.UserAgent = "ClaudeUI-Marketplace/1.0";
            request.Timeout = request.ReadWriteTimeout = 20000;
            request.AllowAutoRedirect = false;
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!String.IsNullOrWhiteSpace(secret)) request.Headers[HttpRequestHeader.Authorization] = "Bearer " + secret;
            using (token.Register(request.Abort))
            try
            {
                if (body != null)
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(body);
                    request.ContentType = "application/json";
                    request.ContentLength = bytes.Length;
                    using (var stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
                }
                using (var response = (HttpWebResponse)request.GetResponse())
                {
                    if ((int)response.StatusCode >= 300) throw new InvalidOperationException("魔搭接口发生重定向，请检查接口地址。");
                    string notice = null;
                    int remaining;
                    if (Int32.TryParse(response.Headers["X-RateLimit-Remaining"], out remaining) && remaining <= 10)
                        notice = "平台请求额度剩余 " + remaining + "，请减少刷新频率。";
                    using (var stream = response.GetResponseStream())
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        var text = new StringBuilder(); var buffer = new char[8192]; int length;
                        while ((length = reader.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested(); text.Append(buffer, 0, length);
                            if (text.Length > 4 * 1024 * 1024) throw new InvalidOperationException("市场响应过大，请缩小查询范围。");
                        }
                        return new MarketplaceResponse { Json = text.ToString(), Notice = notice };
                    }
                }
            }
            catch (WebException ex)
            {
                token.ThrowIfCancellationRequested();
                using (var response = ex.Response as HttpWebResponse)
                {
                    int status = response == null ? 0 : (int)response.StatusCode;
                    if (status == 401 || status == 403) throw new InvalidOperationException("平台拒绝访问（HTTP " + status + "）。请检查可选魔搭 Token 或账户权限、查询配额。");
                    if (status == 429)
                    {
                        int seconds; DateTime until;
                        string retry = response.Headers["Retry-After"];
                        if (!Int32.TryParse(retry, out seconds)) seconds = DateTime.TryParse(retry, out until) ? (int)Math.Ceiling((until.ToUniversalTime() - DateTime.UtcNow).TotalSeconds) : 60;
                        seconds = Math.Max(1, Math.Min(seconds, 86400));
                        nextRequest = DateTime.UtcNow.AddSeconds(seconds);
                        throw new InvalidOperationException("魔搭请求过于频繁，请在 " + seconds + " 秒后重试。");
                    }
                    if (status == 404) throw new InvalidOperationException("该条目已下架或不存在，请返回列表刷新。");
                    throw new InvalidOperationException(status == 0 ? "无法连接魔搭，请检查网络后重试。" : "魔搭暂时不可用（HTTP " + status + "），请稍后重试。");
                }
            }
        }
    }
    internal sealed class MarketplaceCredentials
    {
        private readonly AppData data;
        private string modelScope, github;
        public MarketplaceCredentials(AppData data) { this.data = data; }
        public string ModelScope { get { return Read(modelScope, data.MarketplaceModelScopeToken, "MODELSCOPE_API_TOKEN", "MODELSCOPE_API_KEY"); } }
        public string GitHub { get { return Read(github, data.MarketplaceGitHubToken, "GITHUB_TOKEN", "GH_TOKEN"); } }
        private static string Read(string session, string saved, string env, string alternate)
        {
            if (session != null) return session;
            if (!String.IsNullOrEmpty(saved))
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved), null, DataProtectionScope.CurrentUser)); }
                catch (CryptographicException) { throw new InvalidOperationException("已保存的市场凭据无法解密，请重新填写或清除。"); }
                catch (FormatException) { throw new InvalidOperationException("已保存的市场凭据格式无效，请重新填写或清除。"); }
            return Environment.GetEnvironmentVariable(env) ?? Environment.GetEnvironmentVariable(alternate) ?? "";
        }
        public void Set(string ms, string gh, bool remember)
        {
            if (ms.IndexOfAny(new[] { '\r', '\n' }) >= 0 || gh.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new InvalidOperationException("Token 不能包含换行。");
            string savedMs = remember && ms.Length > 0 ? ModelProfile.Protect(ms) : null;
            string savedGh = remember && gh.Length > 0 ? ModelProfile.Protect(gh) : null;
            modelScope = ms; github = gh;
            data.MarketplaceModelScopeToken = savedMs; data.MarketplaceGitHubToken = savedGh;
        }
    }
}
