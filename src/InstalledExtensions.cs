using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal sealed class InstalledExtension
    {
        public string Name, Scope, Location, Description, Restriction;
        public MarketplaceKind Kind;
        public bool Enabled, CanToggle = true;
    }
    internal sealed class InstalledExtensionSnapshot
    {
        public readonly List<InstalledExtension> Items = new List<InstalledExtension>();
        public readonly List<string> Warnings = new List<string>();
    }
    internal sealed class InstalledExtensions
    {
        private static readonly object writeGate = new object();
        private readonly string workspace, configRoot, userConfig, localSettings, managedRoot;
        internal InstalledExtensions(MarketplaceInstallContext context, string managedRoot = null)
        {
            workspace = Path.GetFullPath(String.IsNullOrWhiteSpace(context.Workspace) ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : context.Workspace);
            string configured = String.IsNullOrWhiteSpace(context.ConfigDirectory) ? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") : context.ConfigDirectory;
            string defaultRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
            configRoot = Path.GetFullPath(String.IsNullOrWhiteSpace(configured) ? defaultRoot : configured);
            userConfig = String.Equals(configRoot.TrimEnd('\\'), defaultRoot.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ?
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json") : Path.Combine(configRoot, ".claude.json");
            localSettings = Path.Combine(workspace, ".claude", "settings.local.json");
            this.managedRoot = managedRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClaudeCode");
        }
        internal InstalledExtensionSnapshot Scan()
        {
            var result = new InstalledExtensionSnapshot();
            var user = ReadSafe(userConfig, result);
            var projects = Dict(user, "projects");
            var project = projects.Where(p => SamePath(p.Key, workspace)).Select(p => p.Value as Dictionary<string, object>).FirstOrDefault() ?? Empty();
            var disabled = Strings(Value(project, "disabledMcpServers"));
            var settings = new[] { ReadSafe(Path.Combine(configRoot,"settings.json"),result),
                ReadSafe(Path.Combine(workspace,".claude","settings.json"),result), ReadSafe(localSettings,result) };
            var managed = ReadSafe(Path.Combine(managedRoot,"managed-settings.json"),result);
            AddMcp(Dict(user,"mcpServers"),"用户级",userConfig,result);
            AddMcp(Dict(ReadSafe(Path.Combine(workspace,".mcp.json"),result),"mcpServers"),"项目共享",Path.Combine(workspace,".mcp.json"),result);
            AddMcp(Dict(project,"mcpServers"),"项目私有",userConfig,result);
            var managedMcp = ReadSafe(Path.Combine(managedRoot,"managed-mcp.json"),result);
            AddMcp(Dict(managedMcp,"mcpServers"),"组织管理",Path.Combine(managedRoot,"managed-mcp.json"),result);
            AddSkills(Path.Combine(configRoot,"skills"),"用户级",null,result);
            foreach (string root in ProjectRoots()) AddSkills(Path.Combine(root,".claude","skills"), SamePath(root,workspace) ? "当前项目" : "父级项目",null,result);
            AddSkills(Path.Combine(managedRoot,".claude","skills"),"组织管理",null,result);
            AddPlugins(settings,result);
            foreach (var item in result.Items)
            {
                if (item.Kind == MarketplaceKind.Mcp)
                {
                    item.Enabled = item.Enabled && !disabled.Contains(item.Name);

                    if (item.Scope == "项目共享" && settings.Concat(new[]{managed}).Any(s=>Strings(Value(s,"disabledMcpjsonServers")).Contains(item.Name)))
                    { item.CanToggle=false; item.Enabled=false; item.Restriction="此服务被 Claude 项目审批设置拒绝，请先检查 CLI 设置。"; }
                }
                else if (!item.Scope.StartsWith("插件",StringComparison.Ordinal))
                {
                    string mode = "on";
                    foreach (var source in settings) { string value=Text(Dict(source,"skillOverrides"),item.Name); if(value.Length>0) mode=value; }
                    string policy=Text(Dict(managed,"skillOverrides"),item.Name);
                    if(policy.Length>0) { mode=policy; item.CanToggle=false; item.Restriction="可见性由组织策略管理。"; }
                    item.Enabled=mode!="off";
                    if(mode=="user-invocable-only") item.Description += " · 仅手动调用";
                    if(mode=="name-only") item.Description += " · 仅向模型显示名称";
                    if(settings.Concat(new[]{managed}).Any(s=>Denied(s,item.Name)))
                    { item.CanToggle=false; item.Enabled=false; item.Restriction="Skill 工具受权限规则限制，请先在 Claude /permissions 中调整。"; }

                }
                if(!Directory.Exists(workspace)) { item.CanToggle=false; item.Restriction="当前工作目录不存在，请先选择有效项目。"; }
            }
            if(result.Warnings.Count>0)
                foreach(var item in result.Items) { item.CanToggle=false; item.Restriction="扫描不完整，请先处理读取错误并刷新，避免覆盖未知设置。"; }
            return result;
        }
        private static bool Denied(Dictionary<string,object> settings,string name)
        {
            return Strings(Value(Dict(settings,"permissions"),"deny")).Any(rule=> rule=="Skill" || rule=="Skill("+name+")" || rule=="Skill("+name+" *)");
        }
        private IEnumerable<string> ProjectRoots()
        {
            var root=new DirectoryInfo(workspace);
            while(root!=null)
            {
                yield return root.FullName;
                if(Directory.Exists(Path.Combine(root.FullName,".git")) || File.Exists(Path.Combine(root.FullName,".git"))) yield break;
                root=root.Parent;
            }
        }
        private static void AddMcp(Dictionary<string,object> servers,string scope,string location,InstalledExtensionSnapshot result)
        {
            foreach(var pair in servers)
            {
                var config=pair.Value as Dictionary<string,object>; if(config==null) continue;
                string command=Text(config,"command"), url=Text(config,"url"); Uri endpoint;
                string description=command.Length>0 ? "本地服务 · "+Path.GetFileName(command) :
                    (Uri.TryCreate(url,UriKind.Absolute,out endpoint) ? "远程服务 · "+endpoint.Host : "MCP 服务配置");
                result.Items.Add(new InstalledExtension { Kind=MarketplaceKind.Mcp, Name=pair.Key, Scope=scope, Location=location, Description=description, Enabled=true });
            }
        }
        private static void AddSkills(string directory,string scope,string plugin,InstalledExtensionSnapshot result)
        {
            if(!Directory.Exists(directory)) return;
            try
            {
                foreach(string path in Directory.GetDirectories(directory))
                {
                    if(Path.GetFileName(path).StartsWith(".",StringComparison.Ordinal)) continue;
                    string manifest=Path.Combine(path,"SKILL.md"); if(!File.Exists(manifest)) continue;
                    if(result.Items.Any(e=>e.Kind==MarketplaceKind.Skill && SamePath(e.Location,manifest))) continue;
                    string name=Path.GetFileName(path), description="Claude Skill";
                    // Parse only simple scalar frontmatter; never execute or resolve content.
                    using(var reader=new StreamReader(manifest))
                    {
                        string first=reader.ReadLine();
                        if(first!=null && first.Trim()=="---")
                        for(int i=0;i<100;i++)
                        {
                            string line=reader.ReadLine(); if(line==null || line.Trim()=="---") break;
                            var match=Regex.Match(line,@"^(name|description):\s*(.*)$"); if(!match.Success) continue;
                            string value=match.Groups[2].Value.Trim().Trim('"','\'');
                            if(match.Groups[1].Value=="name") { string candidate=value.Split(new[]{" #"},StringSplitOptions.None)[0].Trim().Trim('"','\''); if(Regex.IsMatch(candidate,@"^[a-zA-Z0-9_:-]+$")) name=candidate; }
                            if(match.Groups[1].Value=="description" && value.Length>0 && value!="|" && value!=">") description=value;
                        }
                    }
                    result.Items.Add(new InstalledExtension { Kind=MarketplaceKind.Skill, Name=plugin==null?name:plugin+":"+name, Scope=scope, Location=manifest, Description=description, Enabled=true,
                        CanToggle=plugin==null, Restriction=plugin==null?null:"插件 Skill 不受 skillOverrides 控制，请在 Claude /plugin 中启停插件。" });
                }
            }
            catch(IOException) { result.Warnings.Add("部分 Skill 无法读取："+directory); }
            catch(UnauthorizedAccessException) { result.Warnings.Add("无权读取 Skill 目录："+directory); }
        }
        private void AddPlugins(Dictionary<string,object>[] settings,InstalledExtensionSnapshot result)
        {
            string manifest=Path.Combine(configRoot,"plugins","installed_plugins.json");
            var plugins=Dict(ReadSafe(manifest,result),"plugins");
            foreach(var plugin in plugins)
            {
                var entries=plugin.Value as IEnumerable; if(entries==null || plugin.Value is string) continue;
                foreach(var value in entries)
                {
                    var entry=value as Dictionary<string,object>; if(entry==null) continue;
                    string project=Text(entry,"projectPath"); if(project.Length>0 && !SamePath(project,workspace)) continue;
                    string path=Text(entry,"installPath"); if(!Directory.Exists(path)) continue;
                    string key=plugin.Key.Split('@')[0];
                    bool enabled=false;
                    foreach(var setting in settings) { object state=Value(Dict(setting,"enabledPlugins"),plugin.Key); if(state is bool) enabled=(bool)state; }
                    int start=result.Items.Count;
                    AddSkills(Path.Combine(path,"skills"),"插件 · "+plugin.Key,key,result);
                    var serverConfig=Dict(ReadSafe(Path.Combine(path,".mcp.json"),result),"mcpServers");
                    // Plugin .mcp.json may contain the server map directly.
                    if(serverConfig.Count==0) serverConfig=ReadSafe(Path.Combine(path,".mcp.json"),result);
                    foreach(var server in serverConfig.ToArray()) if(!(server.Value is Dictionary<string,object>)) serverConfig.Remove(server.Key);
                    var qualified=serverConfig.ToDictionary(p=>"plugin:"+key+":"+p.Key,p=>p.Value);
                    AddMcp(qualified,"插件 · "+plugin.Key,Path.Combine(path,".mcp.json"),result);
                    for(int i=start;i<result.Items.Count;i++)
                    {
                        result.Items[i].Enabled=enabled;
                        if(!enabled) { result.Items[i].CanToggle=false; result.Items[i].Restriction="所属插件未启用，请先在 Claude /plugin 中启用。"; }
                    }
                }
            }
        }
        internal void SetEnabled(InstalledExtension item,bool enabled)
        {
            if(!item.CanToggle) throw new InvalidOperationException(item.Restriction??"该扩展不可在此修改。");
            if(!Directory.Exists(workspace)) throw new InvalidOperationException("当前工作目录不存在。");
            // Refresh restrictions and installed state immediately before writing.
            var fresh=Scan().Items.FirstOrDefault(e=>e.Kind==item.Kind && e.Name==item.Name && e.Location==item.Location);
            if(fresh==null) throw new InvalidOperationException("扩展已移除，请刷新后重试。");
            if(!fresh.CanToggle) throw new InvalidOperationException(fresh.Restriction??"该扩展受其他设置限制。");
            if(item.Kind==MarketplaceKind.Mcp)
                Update(userConfig,root=>
                {
                    var projects=Ensure(root,"projects");
                    string key=projects.Keys.FirstOrDefault(k=>SamePath(k,workspace))??workspace;
                    var project=Ensure(projects,key);
                    object existing=Value(project,"disabledMcpServers");
                    if(existing!=null && (!(existing is IEnumerable) || existing is string || ((IEnumerable)existing).Cast<object>().Any(v=>!(v is string)))) throw new InvalidOperationException("禁用列表格式异常，原配置未修改。");
                    var disabled=Strings(existing).ToList();
                    disabled.RemoveAll(n=>n==item.Name); if(!enabled) disabled.Add(item.Name);
                    project["disabledMcpServers"]=disabled.ToArray();
                });
            else
                Update(localSettings,root=>Ensure(root,"skillOverrides")[item.Name]=enabled?"on":"off");
        }
        private static void Update(string path,Action<Dictionary<string,object>> mutate)
        {
            lock(writeGate)
            {
                MarketplaceInstaller.CheckTree(Path.GetDirectoryName(path));
                if(File.Exists(path) && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new InvalidOperationException("配置文件是链接，已停止修改。");
                string original=File.Exists(path)?File.ReadAllText(path):null;
                var root=original==null?Empty():Parse(original);
                mutate(root);
                string updated=new JavaScriptSerializer { MaxJsonLength=8*1024*1024 }.Serialize(root);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                string temporary=path+".claudeui-"+Guid.NewGuid().ToString("N")+".tmp";
                try
                {
                    File.WriteAllText(temporary,updated,new UTF8Encoding(false));
                    if((File.Exists(path)?File.ReadAllText(path):null)!=original) throw new InvalidOperationException("配置刚被其他程序修改，请刷新后重试。");
                    if(original==null) File.Move(temporary,path); else File.Replace(temporary,path,null);
                }
                finally { if(File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        private static Dictionary<string,object> Parse(string text)
        {
            try { var root=new JavaScriptSerializer { MaxJsonLength=8*1024*1024 }.DeserializeObject(text) as Dictionary<string,object>; if(root==null) throw new InvalidOperationException(); return root; }
            catch(Exception ex) { if(ex is ArgumentException || ex is InvalidOperationException) throw new InvalidOperationException("配置不是有效 JSON，已停止修改以保护原文件。"); throw; }
        }
        private static Dictionary<string,object> ReadSafe(string path,InstalledExtensionSnapshot result)
        {
            try { return File.Exists(path)?Parse(File.ReadAllText(path)):Empty(); }
            catch(Exception ex) { if(!(ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)) throw; result.Warnings.Add("无法读取配置："+path); return Empty(); }
        }
        private static Dictionary<string,object> Ensure(Dictionary<string,object> root,string key)
        {
            object value;
            if(!root.TryGetValue(key,out value)) { var added=Empty(); root[key]=added; return added; }
            var dictionary=value as Dictionary<string,object>;
            if(dictionary==null) throw new InvalidOperationException("配置字段类型异常，已停止修改。");
            return dictionary;
        }
        private static Dictionary<string,object> Dict(Dictionary<string,object> root,string key) { return Value(root,key) as Dictionary<string,object>??Empty(); }
        private static Dictionary<string,object> Empty() { return new Dictionary<string,object>(); }
        private static object Value(Dictionary<string,object> root,string key) { object value; return root.TryGetValue(key,out value)?value:null; }
        private static string Text(Dictionary<string,object> root,string key) { return Convert.ToString(Value(root,key))??""; }
        private static string[] Strings(object value) { var array=value as IEnumerable; return array==null || value is string?new string[0]:array.Cast<object>().OfType<string>().ToArray(); }
        private static bool SamePath(string a,string b) { try { return String.Equals(Path.GetFullPath(a).TrimEnd('\\','/'),Path.GetFullPath(b).TrimEnd('\\','/'),StringComparison.OrdinalIgnoreCase); } catch(ArgumentException) { return false; } }
    }
}
