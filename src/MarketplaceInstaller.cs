using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal sealed class MarketplaceParameter
    {
        public string Name, Description, Default;
        public bool Required;
    }
    internal sealed class McpInstallOption
    {
        public string Name;
        public Dictionary<string, object> Config;
        public override string ToString() { return Config == null ? Name + " · 云端部署" : Name + (Config.ContainsKey("command") ? " · 本地 stdio" : " · 远程连接"); }
    }
    internal sealed class MarketplaceInstallContext
    {
        public CliInstallation Cli;
        public string Workspace, ConfigDirectory;
    }
    internal static class MarketplaceInstaller
    {
        internal static async Task<Dictionary<string, object>> Deploy(string id, Dictionary<string, string> fields, string credential, CancellationToken token)
        {
            if (!Regex.IsMatch(id ?? "", "^@?[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$")) throw new InvalidOperationException("无效的服务 ID。");
            return await Task.Run(() =>
            {
                var request = (HttpWebRequest)WebRequest.Create("https://modelscope.cn/openapi/v1/mcp/servers/" + String.Join("/", id.Split('/').Select(Uri.EscapeDataString)) + "/deploy");
                request.Method = "POST"; request.ContentType = "application/json"; request.Timeout = request.ReadWriteTimeout = 30000; request.AllowAutoRedirect = false;
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + credential;
                byte[] body = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new { transport_type = "streamable_http", auth_check = true, env_info = fields }));
                request.ContentLength = body.Length;
                using (token.Register(request.Abort))
                try
                {
                    using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                    using (var response = request.GetResponse())
                    using (var reader = new StreamReader(response.GetResponseStream()))
                    {
                        var data = ModelScopeMarketplaceProvider.Data(reader.ReadToEnd());
                        string url = ModelScopeMarketplaceProvider.Text(data, "url"); Uri endpoint;
                        if (!Uri.TryCreate(url, UriKind.Absolute, out endpoint) || endpoint.Scheme != "https" ||
                            !(endpoint.Host == "modelscope.cn" || endpoint.Host.EndsWith(".modelscope.cn", StringComparison.OrdinalIgnoreCase) || endpoint.Host == "mcp.api-inference.modelscope.net"))
                            throw new InvalidOperationException("部署响应未返回有效魔搭地址，请到魔搭控制台检查服务。");
                        return new Dictionary<string, object> { { "type", "http" }, { "url", url }, { "headers", new Dictionary<string, object> { { "Authorization", "Bearer " + credential } } } };
                    }
                }
                catch (WebException ex)
                {
                    token.ThrowIfCancellationRequested();
                    using (var response = ex.Response as HttpWebResponse)
                        throw new InvalidOperationException("魔搭部署失败" + (response == null ? "，请检查网络。" : "（HTTP " + (int)response.StatusCode + "），请检查 Token、配额与参数。") + "若请求已提交，请到魔搭控制台检查实际状态。");
                }
            }, token);
        }
        internal static string SkillName(string id)
        {
            string name = (id ?? "").Split('/').Last();
            if (!Regex.IsMatch(name, "^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$") || name.Equals("synced", StringComparison.OrdinalIgnoreCase) || name.Equals("anthropic-skills", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("该名称不适用于 Claude，请填写其他安装名称。");
            return name;
        }
        internal static string SkillsRoot(MarketplaceInstallContext context, bool project)
        {
            if (project && !Directory.Exists(context.Workspace)) throw new InvalidOperationException("当前项目目录不存在，请先选择工作目录。");
            string config = !String.IsNullOrWhiteSpace(context.ConfigDirectory) ? context.ConfigDirectory :
                Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return project ? Path.Combine(context.Workspace, ".claude", "skills") :
                Path.Combine(String.IsNullOrWhiteSpace(config) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude") : config, "skills");
        }
        internal static string ArchiveUrl(MarketplaceItem item)
        {
            if (item.Provider != "modelscope" || !Regex.IsMatch(item.Id ?? "", "^@?[a-zA-Z0-9_.-]+/[a-zA-Z0-9_.-]+$"))
                throw new InvalidOperationException("该 Skill 没有可识别的魔搭下载地址。");
            return "https://www.modelscope.cn/skills/" + String.Join("/", item.Id.TrimStart('@').Split('/').Select(Uri.EscapeDataString)) + "/archive/zip/master";
        }
        internal static void CheckTree(string root)
        {
            string current = Path.GetFullPath(root);
            while (!String.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("安装路径包含目录链接，请改用普通目录。");
                current = Path.GetDirectoryName(current);
            }
        }
        internal static void Extract(string archive, string target, CancellationToken token)
        {
            string prefix = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            Directory.CreateDirectory(target);
            long total = 0;
            using (var zip = ZipFile.OpenRead(archive))
            {
                if (zip.Entries.Count > 3000) throw new InvalidOperationException("Skill 文件数量超过限制。");
                foreach (var entry in zip.Entries)
                {
                    token.ThrowIfCancellationRequested();
                    string relative = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (relative.Contains(":") || relative.Split(Path.DirectorySeparatorChar).Any(s => s == ".." || s.EndsWith(".") || s.EndsWith(" ")) || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
                        throw new InvalidOperationException("下载包包含不安全的路径或符号链接。");
                    string path = Path.GetFullPath(Path.Combine(target, relative));
                    if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("下载包路径越界。");
                    total += entry.Length;
                    if (total > 150L * 1024 * 1024) throw new InvalidOperationException("Skill 解压大小超过 150 MB 限制。");
                    if (entry.FullName.EndsWith("/")) { Directory.CreateDirectory(path); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    using (var input = entry.Open())
                    using (var output = new FileStream(path, FileMode.CreateNew))
                    {
                        var buffer = new byte[81920]; int count; long written = 0;
                        while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            token.ThrowIfCancellationRequested(); written += count;
                            if (written > entry.Length || written > 150L * 1024 * 1024) throw new InvalidOperationException("下载包解压大小不一致。");
                            output.Write(buffer, 0, count);
                        }
                    }
                }
            }
        }
        internal static async Task DownloadSkill(MarketplaceItem item, string root, string name, string credential, Action<string> log, CancellationToken token)
        {
            name = SkillName(name);
            string url = ArchiveUrl(item);
            string target = Path.Combine(root, name);
            CheckTree(root);
            if (Directory.Exists(target) || File.Exists(target)) throw new InvalidOperationException("已存在同名 Skill，请更换名称；原目录未修改。");
            Directory.CreateDirectory(root);
            string staging = Path.Combine(root, ".claudeui-install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                string archive = Path.Combine(staging, "download.zip"), unpacked = Path.Combine(staging, "unpacked");
                log("下载魔搭官方 Skill 发布包…");
                await Task.Run(() => Download(url, archive, credential, log, token), token);
                log("校验并解压文件…");
                await Task.Run(() => Extract(archive, unpacked, token), token);
                string skillRoot = unpacked;
                if (!File.Exists(Path.Combine(skillRoot, "SKILL.md")))
                {
                    var children = Directory.GetDirectories(unpacked);
                    if (children.Length != 1 || !File.Exists(Path.Combine(children[0], "SKILL.md")))
                        throw new InvalidOperationException("下载包根目录缺少唯一的 SKILL.md，未写入安装目录。");
                    skillRoot = children[0];
                }
                if (new FileInfo(Path.Combine(skillRoot, "SKILL.md")).Length == 0) throw new InvalidOperationException("SKILL.md 为空，安装中止。");
                var metadata = new { provider = item.Provider, id = item.Id, source = item.SourceUrl, method = "modelscope-archive", installedUtc = DateTime.UtcNow.ToString("o") };
                File.WriteAllText(Path.Combine(skillRoot, ".claudeui-install.json"), new JavaScriptSerializer().Serialize(metadata), Encoding.UTF8);
                token.ThrowIfCancellationRequested(); CheckTree(root);
                if (Directory.Exists(target) || File.Exists(target)) throw new InvalidOperationException("安装期间出现同名目录，安装已停止。");
                Directory.Move(skillRoot, target);
                log("安装完成：" + target);
            }
            finally
            {
                try { CheckTree(staging); if (Directory.Exists(staging)) Directory.Delete(staging, true); }
                catch (IOException) { log("暂存目录清理失败：" + staging); }
                catch (UnauthorizedAccessException) { log("暂存目录清理失败：" + staging); }
                catch (InvalidOperationException) { log("暂存目录路径已变化，已跳过清理。"); }
            }
        }
        private static void Download(string address, string file, string credential, Action<string> log, CancellationToken token)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Uri uri = new Uri(address);
            for (int hop = 0; hop < 6; hop++)
            {
                if (uri.Scheme != "https" || uri.IsLoopback || uri.UserInfo.Length > 0) throw new InvalidOperationException("下载地址不安全。");
                var request = (HttpWebRequest)WebRequest.Create(uri);
                request.Timeout = request.ReadWriteTimeout = 30000; request.AllowAutoRedirect = false;
                if ((uri.Host == "modelscope.cn" || uri.Host == "www.modelscope.cn") && !String.IsNullOrWhiteSpace(credential))
                    request.Headers[HttpRequestHeader.Authorization] = "Bearer " + credential;
                using (token.Register(request.Abort))
                try
                {
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                        {
                            uri = new Uri(uri, response.Headers["Location"]); continue;
                        }
                        if (response.ContentLength > 50L * 1024 * 1024) throw new InvalidOperationException("Skill 下载包超过 50 MB 限制。");
                        using (var stream = response.GetResponseStream())
                        using (var output = File.Create(file))
                        {
                            var buffer = new byte[81920]; int count; long total = 0, last = 0;
                            while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested(); total += count;
                                if (total > 50L * 1024 * 1024) throw new InvalidOperationException("Skill 下载包超过 50 MB 限制。");
                                output.Write(buffer, 0, count);
                                if (total - last > 1024 * 1024) { log("已下载 " + total / 1024 + " KB" + (response.ContentLength > 0 ? " / " + response.ContentLength / 1024 + " KB" : "")); last = total; }
                            }
                        }
                        return;
                    }
                }
                catch (WebException ex)
                {
                    token.ThrowIfCancellationRequested();
                    using (var response = ex.Response as HttpWebResponse)
                    {
                        if (response != null && ((int)response.StatusCode == 401 || (int)response.StatusCode == 403))
                            throw new InvalidOperationException("下载被拒绝，请检查魔搭 Token 或 Skill 访问权限。");
                        throw new InvalidOperationException("Skill 下载失败，请检查网络后重试。");
                    }
                }
            }
            throw new InvalidOperationException("下载重定向次数过多。");
        }
        internal static Dictionary<string, object> PrepareConfig(McpInstallOption option, IDictionary<string, string> fields)
        {
            string json = new JavaScriptSerializer().Serialize(option.Config);
            var config = (Dictionary<string, object>)new JavaScriptSerializer().DeserializeObject(json);
            foreach (var field in fields)
            {
                // Replace only explicit placeholders, without passing values through a shell.
                config = Replace(config, field.Key, field.Value);
            }
            if (config.ContainsKey("command"))
            {
                var env = ModelScopeMarketplaceProvider.Value(config, "env") as Dictionary<string, object> ?? new Dictionary<string, object>();
                foreach (var field in fields) if (!String.IsNullOrEmpty(field.Value)) env[field.Key] = field.Value;
                if (env.Count > 0) config["env"] = env;
                string command = Convert.ToString(config["command"]);
                if (command == "npx" || command == "npx.cmd")
                {
                    var node = FindExecutable("node.exe");
                    string script = node == null ? "" : Path.Combine(Path.GetDirectoryName(node), "node_modules", "npm", "bin", "npx-cli.js");
                    if (!File.Exists(script)) throw new InvalidOperationException("此 MCP 需要 Node.js / npm，请安装后重试。");
                    config["command"] = node;
                    var args = new List<object> { script };
                    var raw = ModelScopeMarketplaceProvider.Value(config, "args") as IEnumerable;
                    if (raw != null && !(raw is string)) args.AddRange(raw.Cast<object>());
                    config["args"] = args.ToArray();
                }                else
                {
                    string executable = Path.IsPathRooted(command) ? command : FindExecutable(command + ".exe") ?? FindExecutable(command);
                    if (String.IsNullOrEmpty(executable) || !File.Exists(executable))
                        throw new InvalidOperationException("此 MCP 需要启动程序 " + Path.GetFileName(command) + "，请先安装对应运行依赖，或选择其他官方安装方式。");
                    config["command"] = executable;
                }
            }
            string rendered = new JavaScriptSerializer().Serialize(config);
            if (Regex.IsMatch(rendered, @"\$\{[^}]+\}|\{\{[^}]+\}\}|<[A-Z][A-Z0-9_]+>"))
                throw new InvalidOperationException("安装配置仍包含未填写的占位符，请参考官方说明补全配置。");
            return config;
        }
        private static Dictionary<string, object> Replace(Dictionary<string, object> config, string key, string value)
        {
            foreach (string name in config.Keys.ToArray()) config[name] = ReplaceValue(config[name], key, value);
            return config;
        }
        private static object ReplaceValue(object node, string key, string value)
        {
            if (node is string) return ((string)node).Replace("${" + key + "}", value).Replace("{{" + key + "}}", value).Replace("<" + key + ">", value);
            var dict = node as Dictionary<string, object>; if (dict != null) return Replace(dict, key, value);
            var array = node as object[]; if (array != null) return array.Select(v => ReplaceValue(v, key, value)).ToArray();
            return node;
        }
        internal static string FindExecutable(string name)
        {
            foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
                try { string path = Path.Combine(directory.Trim('"'), name); if (File.Exists(path)) return path; } catch (ArgumentException) { }
            return null;
        }
        internal static string Redact(string text, Dictionary<string, object> config)
        {
            var secrets = new List<string>();
            Gather(config, secrets);
            foreach (string secret in secrets.Where(s => !String.IsNullOrEmpty(s)).OrderByDescending(s => s.Length)) text = text.Replace(secret, "***");
            text = Regex.Replace(text, @"(?i)(Bearer\s+)[^\s""']+", "$1***");
            return text.Length > 1500 ? text.Substring(0, 1500) : text;
        }
        private static void Gather(object node, List<string> values)
        {
            if (node is string) { values.Add((string)node); return; }
            var dict = node as Dictionary<string, object>;
            if (dict != null) { foreach (var pair in dict) Gather(pair.Value, values); return; }
            var sequence = node as IEnumerable;
            if (sequence != null) foreach (var value in sequence) Gather(value, values);
        }
        internal static async Task CheckMcpConflict(MarketplaceInstallContext context, string name, Action<string> log, CancellationToken token)
        {
            if (!Regex.IsMatch(name ?? "", "^[a-zA-Z0-9_-]{1,64}$")) throw new InvalidOperationException("MCP 名称只能包含英文字母、数字、下划线或连字符。");
            if (context.Cli == null) throw new InvalidOperationException("尚未检测到 Claude CLI。");
            var environment = new Dictionary<string, string>();
            if (!String.IsNullOrEmpty(context.ConfigDirectory)) environment["CLAUDE_CONFIG_DIR"] = context.ConfigDirectory;
            string directory = Directory.Exists(context.Workspace) ? context.Workspace : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            log("检查同名 MCP 配置…");
            var output = new StringBuilder();
            using (var check = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                check.CancelAfter(20000);
                int exists;
                try
                {
                    exists = await new CliRunner().RunAsync(context.Cli, directory, new[] { "mcp", "get", name }, null,
                        (line, error) => { lock (output) { if (output.Length < 8000) output.AppendLine(line); } }, check.Token, environment);
                }
                catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); throw new InvalidOperationException("同名配置检查超时，安装已停止，请检查 Claude CLI。"); }
                if (exists == 0) throw new InvalidOperationException("已存在同名 MCP，请修改安装名称。原配置未修改。");
                if (output.ToString().IndexOf("No MCP server found", StringComparison.OrdinalIgnoreCase) < 0)
                    throw new InvalidOperationException("无法确认 MCP 名称是否可用，请检查 CLI 状态后重试。");
            }
        }
        internal static async Task InstallMcp(MarketplaceInstallContext context, string name, string scope, Dictionary<string, object> config, Action<string> log, CancellationToken token)
        {
            if (!Regex.IsMatch(name ?? "", "^[a-zA-Z0-9_-]{1,64}$")) throw new InvalidOperationException("MCP 名称只能包含英文字母、数字、下划线或连字符。");
            if (context.Cli == null) throw new InvalidOperationException("尚未检测到 Claude CLI，请先在 CLI 工具中配置。");
            string directory = Directory.Exists(context.Workspace) ? context.Workspace : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var environment = new Dictionary<string, string>();
            if (!String.IsNullOrEmpty(context.ConfigDirectory)) environment["CLAUDE_CONFIG_DIR"] = context.ConfigDirectory;
            var runner = new CliRunner();
            await CheckMcpConflict(context, name, log, token);
            log("使用 Claude CLI 注册 MCP（" + scope + "）…");
            // Redact configuration values before publishing subprocess output.
            bool confirmed = false, notSaved = false;
            int code = await runner.RunAsync(context.Cli, directory,
                new[] { "mcp", "add-json", name, new JavaScriptSerializer().Serialize(config), "--scope", scope },
                null, (line, error) => {
                    if (line.IndexOf("Added ", StringComparison.OrdinalIgnoreCase) >= 0) confirmed = true;
                    if (line.IndexOf("was not saved", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("may not have been saved", StringComparison.OrdinalIgnoreCase) >= 0) notSaved = true;
                    log(Redact(line, config));
                }, token, environment);
            if (code != 0) throw new InvalidOperationException("Claude CLI 注册失败（退出码 " + code + "）。请检查参数、依赖与配置目录权限。请查看上方已脱敏日志。");
            if (!confirmed || notSaved) throw new InvalidOperationException("CLI 未确认配置已保存，请查看日志并在 MCP 服务中检查实际状态。");
            log("Claude CLI 已保存 MCP 配置。可在 CLI 工具 → MCP 服务中检查连接；远程 OAuth 服务可能还需在 Claude 中授权。");
        }
    }
}
