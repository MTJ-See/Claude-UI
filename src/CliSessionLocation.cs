using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUI
{
    internal sealed class CliSessionLocation
    {
        private readonly CliRunner runner;
        private readonly string legacyDirectory;
        private readonly string userDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");

        public CliSessionLocation(CliRunner runner, string applicationDirectory)
        {
            this.runner = runner;
            legacyDirectory = Path.GetFullPath(Path.Combine(applicationDirectory, "..", "CLI_DefaultWorkSpace"));
        }

        public async Task<string> ResolveAsync(CliInstallation cli, Conversation conversation, CancellationToken cancellation)
        {
            string saved = FullPath(conversation.CliConfigDirectory, conversation.Workspace);
            if (saved != null && Directory.Exists(saved)) return saved;
            string preferred = FullPath(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), conversation.Workspace) ?? userDirectory;
            Guid id;
            if (!Guid.TryParse(conversation.SessionId, out id)) return preferred;
            foreach (string location in new[] { preferred, userDirectory, legacyDirectory }.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(Path.Combine(location, "projects"))) continue;
                if (await CanResume(cli, conversation.Workspace, location, id.ToString(), cancellation).ConfigureAwait(false))
                    return location;
            }
            cancellation.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Claude CLI 在当前及旧配置目录中无法接续这条会话。已保留本地聊天记录。");
        }

        private async Task<bool> CanResume(CliInstallation cli, string workspace, string directory, string id, CancellationToken cancellation)
        {
            var result = new ClaudeStream();
            var options = new[] {
                "--print", "--resume", id, "--no-session-persistence", "--output-format", "stream-json", "--verbose",
                "--safe-mode", "--strict-mcp-config", "--tools", "", "--max-turns", "1"
            };
            // /cost is handled locally. Keep this probe away from the provider even if the CLI cannot run the command.
            var environment = new Dictionary<string, string> {
                { "CLAUDE_CONFIG_DIR", directory },
                { "CLAUDE_CODE_PROVIDER_MANAGED_BY_HOST", "1" },
                { "CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC", "1" },
                { "CLAUDE_CODE_USE_BEDROCK", null },
                { "CLAUDE_CODE_USE_VERTEX", null },
                { "CLAUDE_CODE_USE_FOUNDRY", null },
                { "CLAUDE_CODE_OAUTH_TOKEN", null },
                { "ANTHROPIC_CUSTOM_HEADERS", null },
                { "ANTHROPIC_API_KEY", null },
                { "ANTHROPIC_AUTH_TOKEN", "local-session-check" },
                { "ANTHROPIC_BASE_URL", "http://127.0.0.1:1" },
                { "API_TIMEOUT_MS", "1000" },
                { "HTTP_PROXY", null },
                { "HTTPS_PROXY", null },
                { "ALL_PROXY", null }
            };
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    int code = await runner.RunAsync(cli, workspace, options, "/cost",
                        (line, error) => { if (!error) result.Read(line); }, deadline.Token, environment).ConfigureAwait(false);
                    return code == 0 && result.Finished && result.Failure == null && result.SessionId == id && result.ModelTurns == 0;
                }
                catch (OperationCanceledException)
                {
                    cancellation.ThrowIfCancellationRequested();
                    return false;
                }
            }
        }

        private static string FullPath(string path, string workspace)
        {
            if (String.IsNullOrWhiteSpace(path)) return null;
            return Path.GetFullPath(Path.Combine(workspace, path));
        }
    }
}
