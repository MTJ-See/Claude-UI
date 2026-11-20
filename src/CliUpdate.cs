using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal sealed class CliUpdatePlan
    {
        public CliInstallation Installation;
        public string[] Arguments;
        public string Command;
    }

    internal static class CliUpdate
    {
        public const string LatestUrl = "https://registry.npmjs.org/@anthropic-ai%2fclaude-code/latest";

        public static Version ParseVersion(string text)
        {
            text = Regex.Replace(text ?? "", @"\x1B\[[0-?]*[ -/]*[@-~]", "").Trim();
            var match = Regex.Match(text, @"^(\d+\.\d+\.\d+)(?:\s+\(Claude Code\))?$");
            Version version;
            if (!match.Success || !Version.TryParse(match.Groups[1].Value, out version))
                throw new InvalidDataException("无法识别 Claude Code 版本：" + text);
            return version;
        }

        public static string ParseLatest(string json)
        {
            var data = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            object name, value;
            if (data == null || !data.TryGetValue("name", out name) || (name as string) != "@anthropic-ai/claude-code" ||
                !data.TryGetValue("version", out value) || !(value is string))
                throw new InvalidDataException("最新版本服务返回了无效数据。");
            return ParseVersion((string)value).ToString();
        }

        public static async Task<string> LatestAsync(CancellationToken token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var request = (HttpWebRequest)WebRequest.Create(LatestUrl);
            request.UserAgent = "ClaudeUI";
            request.Accept = "application/json";
            request.Timeout = 20000;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(20));
                using (timeout.Token.Register(request.Abort))
                {
                    try
                    {
                        using (var response = await request.GetResponseAsync())
                        using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        {
                            string json = await reader.ReadToEndAsync();
                            token.ThrowIfCancellationRequested();
                            if (json.Length > 1000000) throw new InvalidDataException("最新版本响应过大。");
                            return ParseLatest(json);
                        }
                    }
                    catch (WebException)
                    {
                        token.ThrowIfCancellationRequested();
                        throw new IOException("无法获取最新版本，请检查网络后重试。本机 CLI 未被更新。");
                    }
                }
            }
        }

        public static async Task<Version> LocalAsync(CliRunner runner, CliInstallation cli,
            string directory, CancellationToken token)
        {
            var output = new StringBuilder();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                int exit = await runner.RunAsync(cli, directory, new[] { "--version" }, null,
                    (line, error) => { if (!error) output.AppendLine(line); }, timeout.Token);
                if (exit != 0) throw new IOException("本机 CLI 版本检查失败，退出码 " + exit + "。");
            }
            return ParseVersion(output.ToString());
        }

        public static CliUpdatePlan Plan(CliInstallation cli, string latest)
        {
            latest = ParseVersion(latest).ToString();
            // Keep npm updates in the exact prefix that supplied the selected CLI.
            var folder = new DirectoryInfo(Path.GetDirectoryName(cli.DisplayPath));
            while (folder != null)
            {
                if (folder.Name.Equals("claude-code", StringComparison.OrdinalIgnoreCase) && folder.Parent != null &&
                    folder.Parent.Name.Equals("@anthropic-ai", StringComparison.OrdinalIgnoreCase) && folder.Parent.Parent != null &&
                    folder.Parent.Parent.Name.Equals("node_modules", StringComparison.OrdinalIgnoreCase))
                {
                    string prefix = folder.Parent.Parent.Parent.FullName;
                    var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
                        .Select(path => path.Trim().Trim('"')).Where(Path.IsPathRooted).ToList();
                    paths.Insert(0, prefix);
                    paths.Insert(0, Path.GetDirectoryName(cli.Program));
                    foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        string node = Path.Combine(path, "node.exe");
                        string npm = Path.Combine(path, "node_modules", "npm", "bin", "npm-cli.js");
                        if (!File.Exists(node) || !File.Exists(npm)) continue;
                        var args = new[] { "install", "--global", "--prefix", prefix,
                            "@anthropic-ai/claude-code@" + latest, "--registry=https://registry.npmjs.org", "--no-audit", "--no-fund" };
                        return new CliUpdatePlan { Installation = new CliInstallation(node, npm), Arguments = args,
                            Command = "npm " + String.Join(" ", args.Select(CliRunner.QuoteArgument)) };
                    }
                    throw new IOException("此 CLI 由 npm 安装，但未找到配套的 Node.js 和 npm。请安装 Node.js 后重试。");
                }
                folder = folder.Parent;
            }
            if (cli.DisplayPath.IndexOf("\\WinGet\\", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "WindowsApps", "winget.exe");
                if (!File.Exists(winget)) throw new IOException("未找到 WinGet，请安装或修复应用安装程序后重试。");
                var args = new[] { "upgrade", "--id", "Anthropic.ClaudeCode", "--exact", "--source", "winget",
                    "--disable-interactivity" };
                return new CliUpdatePlan { Installation = new CliInstallation(winget), Arguments = args,
                    Command = "winget " + String.Join(" ", args) };
            }
            return new CliUpdatePlan { Installation = cli, Arguments = new[] { "update" }, Command = "claude update" };
        }
    }
}
