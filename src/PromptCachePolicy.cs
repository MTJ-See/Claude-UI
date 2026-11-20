using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeUI
{
    // Only CLI cache controls: never transform prompts, tool results or transcripts.
    internal static class PromptCachePolicy
    {
        public static readonly string[] Values = { "inherit", "5m", "1h", "off" };
        public static readonly string[] Labels = {
            "沿用 CLI（默认）", "启用 · 5 分钟", "启用 · 1 小时（写入较贵）", "禁用（端点不兼容时）"
        };

        public static IDictionary<string, string> Apply(string policy, IDictionary<string, string> environment)
        {
            if (String.IsNullOrEmpty(policy) || policy == "inherit") return environment;
            if (Array.IndexOf(Values, policy) < 0)
                throw new InvalidOperationException("缓存策略无效，请在「模型配置」中重新选择。");
            var result = environment == null ? new Dictionary<string, string>() : new Dictionary<string, string>(environment);
            result["DISABLE_PROMPT_CACHING"] = policy == "off" ? "1" : "0";
            if (policy == "off") return result;
            foreach (string tier in new[] { "HAIKU", "SONNET", "OPUS", "FABLE" })
                result["DISABLE_PROMPT_CACHING_" + tier] = "0";
            result["FORCE_PROMPT_CACHING_5M"] = policy == "5m" ? "1" : "0";
            result["ENABLE_PROMPT_CACHING_1H"] = policy == "1h" ? "1" : "0";
            result["CLAUDE_CODE_PROMPT_CACHE_TTL"] = policy;
            result["CLAUDE_CODE_SUBAGENT_PROMPT_CACHE_TTL"] = policy;
            return result;
        }

        public static string Describe(RequestStat row)
        {
            // Do not turn absent usage, failed executions or partial streams into a claimed saving.
            if (row.Incomplete || row.ExecutionOnly || !row.Input.HasValue || !row.CacheWrite.HasValue || !row.CacheRead.HasValue ||
                row.Input < 0 || row.CacheWrite < 0 || row.CacheRead < 0)
                return "缓存命中占比未知（用量不完整）";
            long total = (long)row.Input.Value + row.CacheWrite.Value + row.CacheRead.Value;
            if (total == 0) return "缓存命中占比不适用（输入为 0）";
            return "缓存命中占比 " + (100m * row.CacheRead.Value / total).ToString("0.0", CultureInfo.InvariantCulture) +
                "%（读缓存 / 总输入）；缓存不缩短上下文，费用以端点费率为准。";
        }
    }
}
