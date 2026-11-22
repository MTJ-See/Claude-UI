using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUI
{
    internal sealed class CliAction
    {
        public readonly string Name, Description;
        public readonly string[] Arguments;

        private CliAction(string name, string description, params string[] arguments)
        {
            Name = name;
            Description = description;
            Arguments = arguments;
        }

        public static readonly CliAction[] All = {
            new CliAction("版本", "检查本机与最新版本，确认后自动更新 Claude Code", "--version"),
            new CliAction("健康诊断", "检查安装、更新设置和运行环境", "doctor"),
            new CliAction("登录状态", "查看 Claude CLI 的认证信息", "auth", "status"),
            new CliAction("MCP 服务", "列出 MCP 服务并检查已批准服务的连接", "mcp", "list"),
            new CliAction("插件列表", "列出已安装的 Claude Code 插件", "plugin", "list"),
            new CliAction("后台任务", "列出 Claude CLI 的活动会话，包括后台任务", "agents", "--json"),
            new CliAction("CLI 帮助", "查看本机版本支持的命令和参数", "--help")
        };
    }

    internal sealed class CliInstallation
    {
        public readonly string Program, Script;
        public string DisplayPath { get { return Script ?? Program; } }

        internal CliInstallation(string program, string script = null)
        {
            Program = program;
            Script = script;
        }

        public static CliInstallation Find(string configured, string applicationDirectory)
        {
            if (!String.IsNullOrWhiteSpace(configured))
            {
                if (Path.GetExtension(configured).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(configured))
                    return new CliInstallation(configured);
                return null;
            }

            var folders = PathFolders().Concat(new[] {
                Path.GetFullPath(Path.Combine(applicationDirectory, "..", "CLI")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links")
            });
            foreach (string folder in folders.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    string exe = Path.Combine(folder, "claude.exe");
                    if (File.Exists(exe)) return new CliInstallation(exe);
                    string package = Path.Combine(folder, "node_modules", "@anthropic-ai", "claude-code");
                    exe = Path.Combine(package, "bin", "claude.exe");
                    if (File.Exists(exe)) return new CliInstallation(exe);
                    string script = Path.Combine(package, "cli.js");
                    if (File.Exists(script))
                    {
                        string node = PathFolders().PrependFolder(folder).Select(path => Path.Combine(path, "node.exe"))
                            .FirstOrDefault(File.Exists);
                        if (node != null) return new CliInstallation(node, script);
                    }
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return null;
        }

        private static IEnumerable<string> PathFolders()
        {
            return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                .Select(path => path.Trim().Trim('"'))
                .Where(path => path.Length > 2 && path[1] == ':' && (path[2] == '\\' || path[2] == '/'));
        }
    }

    internal static class FolderSequence
    {
        public static IEnumerable<string> PrependFolder(this IEnumerable<string> folders, string folder)
        {
            yield return folder;
            foreach (string path in folders) yield return path;
        }
    }

    internal sealed class RequestOptions
    {
        public string Model, Effort, Permission, SessionId;
        public bool AskToolPermission;

        public string[] Arguments()
        {
            var arguments = new List<string> {
                "--print", "--input-format", AskToolPermission ? "stream-json" : "text", "--output-format", "stream-json",
                "--verbose", "--include-partial-messages", "--permission-mode", Permission
            };
            if (AskToolPermission) arguments.AddRange(new[] { "--permission-prompt-tool", "stdio" });
            if (!String.IsNullOrEmpty(Model)) arguments.AddRange(new[] { "--model", Model });
            if (!String.IsNullOrEmpty(Effort)) arguments.AddRange(new[] { "--effort", Effort });
            if (!String.IsNullOrEmpty(SessionId)) arguments.AddRange(new[] { "--resume", SessionId });
            return arguments.ToArray();
        }
    }

    internal sealed class SessionEdit
    {
        public readonly string SourceId, Title;
        public readonly bool Fork;

        public SessionEdit(string sourceId, string title, bool fork)
        {
            SourceId = Guid.Parse(sourceId).ToString();
            Title = title;
            Fork = fork;
        }

        public string Input { get { return "/rename " + Title; } }

        public string[] Arguments()
        {
            var args = new List<string> {
                "--print", "--input-format", "text", "--output-format", "stream-json", "--verbose",
                "--resume", SourceId, "--safe-mode", "--strict-mcp-config", "--tools", "", "--max-turns", "1"
            };
            // The local /rename command gives the fork a title without a model turn.
            if (Fork) args.Add("--fork-session");
            return args.ToArray();
        }

        public string Validate(ClaudeStream result)
        {
            if (result.Failure != null) return result.Failure;
            if (!result.Finished || result.ModelTurns != 0 || result.SessionId == null ||
                (Fork ? result.SessionId == SourceId : result.SessionId != SourceId) ||
                !String.Equals(result.Result.Trim(), "Session renamed to: " + Title, StringComparison.Ordinal))
                return "Claude CLI 未确认会话操作成功。请查看执行日志。";
            return null;
        }
    }

    internal sealed class CliRunner
    {
        public Task<int> RunAsync(CliInstallation installation, string directory, IEnumerable<string> arguments,
            string input, Action<string, bool> output, CancellationToken cancellation, IDictionary<string, string> environment = null,
            Func<CliToolPermission, CancellationToken, Task<bool>> authorize = null, object userContent = null)
        {
            return Task.Run(async () =>
            {
                cancellation.ThrowIfCancellationRequested();
                var command = new List<string>();
                if (installation.Script != null) command.Add(installation.Script);
                command.AddRange(arguments);
                using (var process = new Process())
                using (var job = new ChildProcessJob())
                {
                    process.StartInfo = new ProcessStartInfo {
                        FileName = installation.Program,
                        Arguments = String.Join(" ", command.Select(QuoteArgument)),
                        WorkingDirectory = directory,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    if (environment != null)
                    {
                        foreach (var entry in environment)
                        {
                            if (entry.Value == null) process.StartInfo.EnvironmentVariables.Remove(entry.Key);
                            else process.StartInfo.EnvironmentVariables[entry.Key] = entry.Value;
                        }
                    }
                    process.Start();
                    job.Attach(process);
                    using (cancellation.Register(() =>
                    {
                        job.Terminate();
                        Kill(process);
                    }))
                    {
                        var stderr = Drain(process.StandardError, output, true);
                        var stdout = authorize == null
                            ? Drain(process.StandardOutput, output, false)
                            : new CliPermissionChannel(process.StandardInput.BaseStream, authorize)
                                .ReadAsync(process.StandardOutput, input, output, cancellation, userContent);
                        if (authorize == null)
                        {
                            try
                            {
                                if (input != null)
                                {
                                    byte[] bytes = Encoding.UTF8.GetBytes(input);
                                    await process.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length, cancellation);
                                }
                                process.StandardInput.Close();
                            }
                            catch (IOException) { }
                        }
                        // The CLI can exit while a child still has its output pipe open.
                        // Reap the process tree before waiting for end-of-stream.
                        var exited = Task.Run(() => process.WaitForExit());
                        if (await Task.WhenAny(exited, stdout) == stdout && stdout.IsFaulted)
                        {
                            job.Terminate();
                            Kill(process);
                        }
                        await exited;
                        job.Terminate();
                        try { await Task.WhenAll(stdout, stderr); }
                        catch (IOException) { cancellation.ThrowIfCancellationRequested(); throw; }
                        cancellation.ThrowIfCancellationRequested();
                        return process.ExitCode;
                    }
                }
            });
        }

        private static void Kill(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static async Task Drain(StreamReader reader, Action<string, bool> output, bool error)
        {
            string line;
            while ((line = await reader.ReadLineAsync()) != null) output(line, error);
        }

        public static string QuoteArgument(string value)
        {
            if (value.Length > 0 && !value.Any(character => Char.IsWhiteSpace(character) || character == '"')) return value;
            var quoted = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                quoted.Append(character);
                backslashes = 0;
            }
            return quoted.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }

    internal sealed class ChildProcessJob : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
        private readonly IntPtr handle = CreateJobObject(IntPtr.Zero, null);

        public void Attach(Process process)
        {
            if (handle == IntPtr.Zero) return;
            try { AssignProcessToJobObject(handle, process.Handle); }
            catch (InvalidOperationException) { }
        }

        public void Terminate()
        {
            if (handle != IntPtr.Zero) TerminateJobObject(handle, 1);
        }

        public void Dispose()
        {
            if (handle != IntPtr.Zero)
            {
                Terminate();
                CloseHandle(handle);
            }
        }
    }
}
