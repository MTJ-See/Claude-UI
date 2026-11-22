using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal static class InstalledExtensionsSmoke
    {
        private static void Check(bool value,string name) { if(!value) throw new Exception(name); Console.WriteLine("PASS "+name); }
        private static Dictionary<string,object> Read(string path) { return (Dictionary<string,object>)new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)); }
        private static void Write(string path,object value) { Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllText(path,new JavaScriptSerializer().Serialize(value)); }
        internal static void Run()
        {
            string root=Path.Combine(Path.GetTempPath(),"claudeui-installed-"+Guid.NewGuid().ToString("N"));
            string config=Path.Combine(root,"config"), project=Path.Combine(root,"project"), managed=Path.Combine(root,"managed");
            Directory.CreateDirectory(Path.Combine(project,".git")); Directory.CreateDirectory(config);
            var context=new MarketplaceInstallContext { Workspace=project,ConfigDirectory=config };
            var manager=new InstalledExtensions(context,managed);
            string user=Path.Combine(config,".claude.json"), local=Path.Combine(project,".claude","settings.local.json");
            try
            {
                Write(user,new { marker="keep-me",mcpServers=new { fetch=new { command="uvx",env=new { API_KEY="synthetic-secret" } } }, projects=new Dictionary<string,object>{{project.Replace('\\','/'),new { trust="preserve", mcpServers=new { localserver=new { command="node" } },disabledMcpServers=new[]{"other"} }}} });
                Write(Path.Combine(project,".mcp.json"),new { mcpServers=new { remote=new { type="http",url="https://example.com/mcp?key=hidden" } } });
                Directory.CreateDirectory(Path.Combine(config,"skills","review"));
                File.WriteAllText(Path.Combine(config,"skills","review","SKILL.md"),"---\nname: code-review\ndescription: Review code\n---\nExample");
                var snapshot=manager.Scan();
                Check(snapshot.Items.Count(i=>i.Kind==MarketplaceKind.Mcp)==3,"Scan user, project and private MCP configurations");
                Check(snapshot.Items.Any(i=>i.Kind==MarketplaceKind.Skill && i.Name=="code-review"),"Skill name comes from SKILL.md frontmatter");
                Check(!snapshot.Items.Any(i=>(i.Description??"").Contains("hidden") || (i.Description??"").Contains("synthetic-secret")),"Installed summaries do not expose credentials");
                var mcp=snapshot.Items.First(i=>i.Name=="fetch"); manager.SetEnabled(mcp,false);
                Check(!manager.Scan().Items.First(i=>i.Name=="fetch").Enabled,"MCP off state persists in project disabledMcpServers");
                var saved=Read(user);
                Check(Convert.ToString(saved["marker"])=="keep-me" && new JavaScriptSerializer().Serialize(saved["mcpServers"]).Contains("synthetic-secret"),"MCP toggle preserves other settings and credentials");
                manager.SetEnabled(mcp,true);
                Check(manager.Scan().Items.First(i=>i.Name=="fetch").Enabled && File.ReadAllText(user).Contains("other"),"MCP re-enable preserves unrelated disabled names");
                var skill=manager.Scan().Items.First(i=>i.Kind==MarketplaceKind.Skill);
                Write(local,new { permissions=new { allow=new[]{"Read"} }, custom="preserved" });
                manager.SetEnabled(skill,false);
                Check(!manager.Scan().Items.First(i=>i.Kind==MarketplaceKind.Skill).Enabled && File.Exists(skill.Location),"Skill off uses settings override and preserves installed files");
                Check(Convert.ToString(Read(local)["custom"])=="preserved" && File.ReadAllText(local).Contains("Read"),"Skill toggle preserves local settings");
                manager.SetEnabled(skill,true);
                Check(manager.Scan().Items.First(i=>i.Kind==MarketplaceKind.Skill).Enabled,"Skill re-enable persists");
                Write(local,new { permissions=new { deny=new[]{"Skill(code-review)"} } });
                var blocked=manager.Scan().Items.First(i=>i.Kind==MarketplaceKind.Skill);
                Check(!blocked.CanToggle,"Skill permission restrictions are surfaced");
                Write(local,new { skillOverrides=new Dictionary<string,string>{{"code-review","off"}} });
                Write(Path.Combine(managed,"managed-settings.json"),new { skillOverrides=new Dictionary<string,string>{{"code-review","off"}} });
                Check(!manager.Scan().Items.First(i=>i.Kind==MarketplaceKind.Skill).CanToggle,"Managed Skill policy is read-only");
                File.Delete(Path.Combine(managed,"managed-settings.json"));
                File.WriteAllText(local,"{broken-json"); string original=File.ReadAllText(local);
                Check(manager.Scan().Warnings.Count>0,"Unreadable configuration is reported");
                try { manager.SetEnabled(skill,true); throw new Exception("overwrote invalid settings"); } catch(InvalidOperationException) { Check(File.ReadAllText(local)==original,"Invalid configuration is never overwritten"); }
                File.Delete(local);
                string plugin=Path.Combine(root,"plugin"); Directory.CreateDirectory(Path.Combine(plugin,"skills","tool"));
                File.WriteAllText(Path.Combine(plugin,"skills","tool","SKILL.md"),"---\nname: tool\n---\nExample");
                Write(Path.Combine(plugin,".mcp.json"),new { demo=new { command="node" } });
                Write(Path.Combine(config,"plugins","installed_plugins.json"),new { plugins=new Dictionary<string,object>{{"demo@market",new[]{new { scope="user",installPath=plugin }}}} });
                Write(Path.Combine(config,"settings.json"),new { enabledPlugins=new Dictionary<string,bool>{{"demo@market",true}} });
                snapshot=manager.Scan();
                Check(snapshot.Items.Any(i=>i.Name=="demo:tool" && !i.CanToggle) && snapshot.Items.Any(i=>i.Name=="plugin:demo:demo" && i.Enabled),"Plugin MCP and skills are identified with supported controls");
            }
            finally { MarketplaceInstaller.CheckTree(root); Directory.Delete(root,true); }
        }
    }
}
