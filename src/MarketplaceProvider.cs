using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal enum MarketplaceKind { Mcp, Skill }
    internal sealed class MarketplaceItem
    {
        public string Provider, Id, Name, Description, IconUrl, Author, SourceUrl, Readme, License;
        public string[] Tags = new string[0];
        public string Popularity, Configuration, Permissions, Installation;
        public MarketplaceKind Kind;
        public List<McpInstallOption> McpOptions = new List<McpInstallOption>();
        public List<MarketplaceParameter> Parameters = new List<MarketplaceParameter>();
        public bool Hosted;
    }
    internal sealed class MarketplacePage
    {
        public List<MarketplaceItem> Items = new List<MarketplaceItem>();
        public int Total, Number;
        public bool HasNext;
        public string Notice;
    }
    internal interface IMarketplaceProvider
    {
        string Id { get; }
        Task<MarketplacePage> ListAsync(MarketplaceKind kind, int page, string search, bool refresh, CancellationToken token);
        Task<MarketplaceItem> DetailAsync(MarketplaceKind kind, string id, bool refresh, CancellationToken token);
    }
    internal sealed class ModelScopeMarketplaceProvider : IMarketplaceProvider
    {
        private readonly MarketplaceHttp http;
        public string Id { get { return "modelscope"; } }
        public ModelScopeMarketplaceProvider(MarketplaceHttp http) { this.http = http; }
        public async Task<MarketplacePage> ListAsync(MarketplaceKind kind, int page, string search, bool refresh, CancellationToken token)
        {
            if (page < 1 || page > (kind == MarketplaceKind.Mcp ? 5 : 150)) throw new ArgumentOutOfRangeException("page");
            string path, body = null;
            if (kind == MarketplaceKind.Mcp)
            {
                path = "/mcp/servers";
                body = Json(new { page_number = page, page_size = 20, search = search ?? "" });
            }
            else path = "/skills?page_size=20&page_number=" + page + "&search=" + Uri.EscapeDataString(search ?? "");
            var response = await http.GetAsync(path, body, refresh, token);
            var data = Data(response.Json);
            var key = kind == MarketplaceKind.Mcp ? "mcp_server_list" : "skills";
            object raw;
            if (!data.TryGetValue(key, out raw) || !(raw is IEnumerable) || raw is string)
                throw new InvalidOperationException("魔搭列表格式已变化，请稍后重试或更新应用。");
            var result = new MarketplacePage { Number = page, Notice = response.Notice };
            foreach (var item in (IEnumerable)raw)
            {
                var row = item as Dictionary<string, object>;
                if (row == null || String.IsNullOrWhiteSpace(Text(row, "id"))) continue;
                result.Items.Add(Map(kind, row));
            }
            int.TryParse(Text(data, kind == MarketplaceKind.Mcp ? "total_count" : "total"), out result.Total);
            result.HasNext = page * 20 < result.Total && page < (kind == MarketplaceKind.Mcp ? 5 : 150);
            if (page == 5 && kind == MarketplaceKind.Mcp && result.Total > 100)
                result.Notice = "魔搭 MCP 接口仅允许访问前 100 条；请使用搜索查找其他服务。";
            return result;
        }
        public async Task<MarketplaceItem> DetailAsync(MarketplaceKind kind, string id, bool refresh, CancellationToken token)
        {
            string encoded = String.Join("/", id.Split('/').Select(Uri.EscapeDataString));
            var response = await http.GetAsync((kind == MarketplaceKind.Mcp ? "/mcp/servers/" : "/skills/") + encoded, null, refresh, token);
            return Map(kind, Data(response.Json));
        }
        internal static Dictionary<string, object> Data(string json)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(json) as Dictionary<string, object>;
            object value;
            if (root == null || (root.TryGetValue("success", out value) && !Object.Equals(value, true)))
                throw new InvalidOperationException("魔搭未能完成请求，请重试或检查访问令牌。");
            if (!root.TryGetValue("data", out value) || !(value is Dictionary<string, object>))
                throw new InvalidOperationException("魔搭返回了无法识别的数据格式。");
            return (Dictionary<string, object>)value;
        }
        internal static MarketplaceItem Map(MarketplaceKind kind, Dictionary<string, object> row)
        {
            var item = new MarketplaceItem { Provider = "modelscope", Kind = kind, Id = Text(row, "id"),
                Description = Text(row, "description"), IconUrl = Text(row, "logo_url"), SourceUrl = Text(row, "source_url"),
                Readme = Text(row, "readme"), License = Text(row, "license") };
            item.Name = First(Text(row, kind == MarketplaceKind.Mcp ? "chinese_name" : "display_name"), Text(row, "name"), item.Id);
            item.Author = First(Text(row, "author"), Text(row, "developer"), Text(row, "owner"), item.Id.Split('/')[0].TrimStart('@'));
            item.Tags = Strings(Value(row, "categories")).Concat(Strings(Value(row, "category"))).Concat(Strings(Value(row, "tags"))).Distinct().ToArray();
            var heat = new List<string>();
            foreach (var pair in new[] { new[] { "downloads", "下载" }, new[] { "view_count", "浏览" }, new[] { "github_stars", "Stars" } })
                if (row.ContainsKey(pair[0])) heat.Add(pair[1] + " " + Text(row, pair[0]));
            item.Popularity = heat.Count == 0 ? "热度暂无数据" : String.Join(" · ", heat);
            item.Configuration = row.ContainsKey("env_schema") ? Json(row["env_schema"]) : "平台未提供配置参数说明";
            item.Permissions = row.ContainsKey("permissions") ? Json(row["permissions"]) : "平台未提供权限声明；请结合源码和功能说明评估。";
            item.Installation = row.ContainsKey("server_config") ? Json(row["server_config"]) :
                (row.ContainsKey("install_command") ? String.Join("\n", Strings(row["install_command"])) : "平台未提供安装元数据");
            item.Hosted = Object.Equals(Value(row, "is_hosted"), true);
            if (kind == MarketplaceKind.Mcp)
            {
                var schema = Value(row, "env_schema") as Dictionary<string, object>;
                if (schema != null)
                {
                    var required = Strings(Value(schema, "required")).ToArray();
                    var properties = Value(schema, "properties") as Dictionary<string, object>;
                    if (properties != null) foreach (var pair in properties)
                    {
                        var definition = pair.Value as Dictionary<string, object> ?? new Dictionary<string, object>();
                        item.Parameters.Add(new MarketplaceParameter { Name = pair.Key, Description = Text(definition, "description"), Default = Text(definition, "default"), Required = required.Contains(pair.Key) });
                    }
                }
                object configurations = Value(row, "server_config");
                if (configurations is string)
                    try { configurations = new JavaScriptSerializer().DeserializeObject((string)configurations); } catch (ArgumentException) { configurations = null; }
                var configs = configurations as IEnumerable;
                if (configurations is Dictionary<string, object>) configs = new[] { configurations };
                if (configs != null && !(configs is string)) foreach (object candidate in configs)
                {
                    var wrapper = candidate as Dictionary<string, object>;
                    if (wrapper == null) continue;
                    var servers = Value(wrapper, "mcpServers") as Dictionary<string, object>;
                    if (servers == null) continue;
                    foreach (var server in servers)
                    {
                        var config = server.Value as Dictionary<string, object>;
                        if (config == null) continue;
                        if (config.ContainsKey("url"))
                        {
                            string type = Text(config, "type");
                            config["type"] = type == "sse" ? "sse" : "http";
                        }
                        if (config.ContainsKey("command") || config.ContainsKey("url"))
                            item.McpOptions.Add(new McpInstallOption { Name = server.Key, Config = config });
                        var env = Value(config, "env") as Dictionary<string, object>;
                        if (env != null) foreach (var variable in env)
                            if (!item.Parameters.Any(p => p.Name == variable.Key))
                                item.Parameters.Add(new MarketplaceParameter { Name = variable.Key, Description = "服务环境变量", Required = true });
                    }
                }
            }
            return item;
        }
        internal static object Value(Dictionary<string, object> row, string key) { object value; return row.TryGetValue(key, out value) ? value : null; }
        internal static string Text(Dictionary<string, object> row, string key) { return Convert.ToString(Value(row, key)) ?? ""; }
        private static string First(params string[] values) { return values.FirstOrDefault(v => !String.IsNullOrWhiteSpace(v)) ?? ""; }
        private static IEnumerable<string> Strings(object value)
        {
            if (value is string) return new[] { (string)value };
            var values = value as IEnumerable;
            return values == null ? new string[0] : values.Cast<object>().Select(Convert.ToString).Where(v => !String.IsNullOrWhiteSpace(v));
        }
        private static string Json(object value) { return new JavaScriptSerializer().Serialize(value); }
    }
}
