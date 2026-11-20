using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUI
{
    internal static class ModelProfileProbe
    {
        public static async Task<string> RunAsync(CliRunner runner, CliInstallation cli, string workspace,
            string configuration, ModelProfile profile, CancellationToken token, Action<IList<RequestStat>> record = null)
        {
            var environment = profile.CliEnvironment() ?? new Dictionary<string, string>();
            environment["CLAUDE_CONFIG_DIR"] = configuration;
            environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            environment["CLAUDE_CODE_MAX_RETRIES"] = "0";
            environment["CLAUDE_CODE_MAX_OUTPUT_TOKENS"] = "256";
            environment["MAX_THINKING_TOKENS"] = "0";
            var options = new[] {
                "--print", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--no-session-persistence",
                "--safe-mode", "--strict-mcp-config", "--tools", "", "--max-turns", "1",
                "--model", profile.ModelId, "--system-prompt", "Reply with OK."
            };
            var result = new ClaudeStream();
            string modelName = profile.Name;
            string stderr = null;
            DateTime started = DateTime.UtcNow;
            bool failed = true;
            try
            {
                int exit = await runner.RunAsync(cli, workspace, options, "OK?", (line, error) =>
                {
                    if (!error) result.Read(line);
                    else if (!String.IsNullOrWhiteSpace(line)) stderr = line;
                }, token, environment).ConfigureAwait(false);
                failed = !(exit == 0 && result.Finished && result.Failure == null && result.HasCompleteModelResponse);
                if (!failed) return null;
                if (result.HasCompleteModelResponse && !result.ApiErrorStatus.HasValue)
                    return "网关已返回完整模型响应，但 CLI 执行未正常结束（退出码 " + exit + "）。请检查 CLI 状态。";
                return ExplainFailure(result.ApiErrorStatus, result.Failure ?? stderr ?? "");
            }
            finally
            {
                if (record != null)
                {
                    var rows = result.Statistics(started, GatewayRoute.Resolve(workspace, environment), null,
                        profile.ModelId, 0m, token.IsCancellationRequested, failed, "取消测试", "连接测试");
                    foreach (var row in rows) row.RequestedModelName = modelName;
                    record(rows);
                }
            }
        }

        private static string ExplainFailure(int? status, string detail)
        {
            if (!status.HasValue)
            {
                var match = Regex.Match(detail, @"(?:^|\bAPI Error:\s*|\bHTTP\s+)([45]\d{2})\b", RegexOptions.IgnoreCase);
                if (match.Success) status = Int32.Parse(match.Groups[1].Value);
            }
            switch (status)
            {
                case 400: return "测试失败（400），请检查模型 ID 和端点是否兼容 Anthropic API。";
                case 401:
                case 403: return "测试失败（" + status.Value + "），请检查 API 凭据、认证方式和模型权限。";
                case 404: return "测试失败（404），请检查 API 基础地址和模型 ID。";
                case 429: return "测试失败（429），请求受限，请检查服务的额度或限流状态。";
            }
            if (status >= 500) return "测试失败（" + status.Value + "），模型服务暂时无法处理请求。";
            if (Regex.IsMatch(detail, @"/login\b|not logged in", RegexOptions.IgnoreCase))
                return "测试失败，请检查本机 Claude CLI 的登录状态。";
            if (Regex.IsMatch(detail, @"unable to connect|ECONN|ENOTFOUND|certificate|fetch failed", RegexOptions.IgnoreCase))
                return "测试失败，无法连接模型服务，请检查网络和 API 基础地址。";
            return "测试失败，未收到有效的模型响应。请检查模型 ID、端点和认证配置。";
        }
    }
}
