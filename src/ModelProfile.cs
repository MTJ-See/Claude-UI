using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ClaudeUI
{
    public sealed class ModelProfile
    {
        public const string Bearer = "bearer";
        public const string ApiKey = "api-key";

        public string Id { get; set; }
        public string Name { get; set; }
        public string ModelId { get; set; }
        public string BaseUrl { get; set; }
        public string Authentication { get; set; }
        public string ProtectedCredential { get; set; }
        public string PromptCaching { get; set; }

        internal bool UsesEndpoint { get { return !String.IsNullOrWhiteSpace(BaseUrl); } }

        internal static bool IsModelId(string value)
        {
            return !String.IsNullOrWhiteSpace(value) && value.Length <= 256 &&
                !value.StartsWith("-", StringComparison.Ordinal) && !value.Any(Char.IsWhiteSpace) && !value.Any(Char.IsControl);
        }

        internal static string NormalizeEndpoint(string value)
        {
            Uri endpoint;
            if (!Uri.TryCreate(value, UriKind.Absolute, out endpoint) || String.IsNullOrEmpty(endpoint.Host) ||
                (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
                endpoint.UserInfo.Length > 0 || endpoint.Query.Length > 0 || endpoint.Fragment.Length > 0)
                return null;
            string path = endpoint.AbsolutePath.TrimEnd('/');
            if (endpoint.Scheme == Uri.UriSchemeHttps && endpoint.IsDefaultPort &&
                endpoint.Host.Equals("tokendance.space", StringComparison.OrdinalIgnoreCase) &&
                path.Equals("/gateway/v1/chat/completions", StringComparison.Ordinal))
                return "https://tokendance.space/gateway";
            if (path.EndsWith("/v1/messages", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith("/v1/chat/completions", StringComparison.OrdinalIgnoreCase))
                return null;
            return endpoint.AbsoluteUri.TrimEnd('/');
        }

        internal static string Protect(string credential)
        {
            byte[] clear = Encoding.UTF8.GetBytes(credential);
            try { return Convert.ToBase64String(ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser)); }
            finally { Array.Clear(clear, 0, clear.Length); }
        }

        internal IDictionary<string, string> CliEnvironment()
        {
            if (!UsesEndpoint) return PromptCachePolicy.Apply(PromptCaching, null);
            string endpoint = NormalizeEndpoint(BaseUrl);
            if (endpoint == null || !IsModelId(ModelId) || (Authentication != Bearer && Authentication != ApiKey))
                throw new InvalidOperationException("模型配置无效，请在「模型配置」中检查端点、模型 ID 和认证方式。");
            string credential = ReadCredential();
            return PromptCachePolicy.Apply(PromptCaching, new Dictionary<string, string> {
                { "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST", "1" },
                { "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1" },
                { "CLAUDE_CODE_USE_BEDROCK", null },
                { "CLAUDE_CODE_USE_VERTEX", null },
                { "CLAUDE_CODE_USE_FOUNDRY", null },
                { "CLAUDE_CODE_USE_CLAUDE_AI", null },
                { "CLAUDE_CODE_OAUTH_TOKEN", null },
                { "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", null },
                { "CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR", null },
                { "ANTHROPIC_CUSTOM_HEADERS", null },
                { "ANTHROPIC_BASE_URL", endpoint },
                { "ANTHROPIC_AUTH_TOKEN", Authentication == Bearer ? credential : null },
                { "ANTHROPIC_API_KEY", Authentication == ApiKey ? credential : null },
                { "ANTHROPIC_MODEL", ModelId },
                { "ANTHROPIC_DEFAULT_HAIKU_MODEL", ModelId },
                { "ANTHROPIC_DEFAULT_SONNET_MODEL", ModelId },
                { "ANTHROPIC_DEFAULT_OPUS_MODEL", ModelId },
                { "CLAUDE_CODE_SUBAGENT_MODEL", ModelId }
            });
        }

        private string ReadCredential()
        {
            try
            {
                byte[] clear = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedCredential ?? ""), null, DataProtectionScope.CurrentUser);
                try
                {
                    string credential = Encoding.UTF8.GetString(clear);
                    if (String.IsNullOrWhiteSpace(credential)) throw new FormatException();
                    return credential;
                }
                finally { Array.Clear(clear, 0, clear.Length); }
            }
            catch (Exception ex)
            {
                if (!(ex is FormatException) && !(ex is CryptographicException)) throw;
                throw new InvalidOperationException("无法读取模型凭据，请在「模型配置」中重新填写。");
            }
        }
    }
}
