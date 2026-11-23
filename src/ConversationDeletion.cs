using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal static class ConversationDeletion
    {
        private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;

        public static void DeleteRecords(DataStore store, Conversation conversation, IEnumerable<Conversation> others, IEnumerable<string> roots)
        {
            var otherChats = others.ToList();
            var files = new List<string>();
            var directories = new List<string>();
            var histories = new List<string>();
            var transcripts = new List<string>();
            Guid session;
            string id = null;
            if (!String.IsNullOrWhiteSpace(conversation.SessionId))
            {
                if (!Guid.TryParse(conversation.SessionId, out session)) throw new IOException("会话 ID 无效，无法核对 CLI 文件。");
                id = session.ToString();
                if (otherChats.Any(item => String.Equals(item.SessionId, id, StringComparison.OrdinalIgnoreCase)))
                    throw new IOException("还有另一条对话关联此会话 ID，请先刷新对话列表。");
                var ownRoot = CliConversationCatalog.Normalize(conversation.CliConfigDirectory);
                var locations = new[] { ownRoot }.Concat(roots.Select(CliConversationCatalog.Normalize))
                    .Where(root => root != null && Directory.Exists(root)).Distinct(Paths);
                foreach (string root in locations)
                {
                    string projects = Path.Combine(root, "projects");
                    CheckPath(projects);
                    var found = new List<string>();
                    if (Directory.Exists(projects))
                        foreach (string project in Directory.EnumerateDirectories(projects))
                        {
                            if ((File.GetAttributes(project) & FileAttributes.ReparsePoint) != 0) continue;
                            string file = Path.Combine(project, id + ".jsonl");
                            if (!File.Exists(file)) continue;
                            CheckPath(file);
                            if (!CliConversationCatalog.TranscriptMatchesWorkspace(file, root, id, conversation.Workspace))
                                throw new IOException("无法确认同一会话 ID 的磁盘记录属于当前项目。");
                            found.Add(file);
                            CollectTree(Path.Combine(project, id), files, directories);
                        }
                    if (found.Count == 0 && !Paths.Equals(root, ownRoot)) continue;
                    transcripts.AddRange(found);
                    foreach (string area in new[] { "file-history", "session-env", "tasks", "session-memory" })
                        CollectTree(Path.Combine(root, area, id), files, directories);
                    CollectFile(Path.Combine(root, "debug", id + ".txt"), files);
                    string history = Path.Combine(root, "history.jsonl");
                    if (File.Exists(history)) { CheckPath(history); histories.Add(history); }
                }
                if (File.Exists(conversation.CliTranscriptPath) &&
                    !transcripts.Contains(Path.GetFullPath(conversation.CliTranscriptPath), Paths))
                    throw new IOException("当前会话文件不在可验证的 CLI 存储位置内。");
            }

            string attachmentRoot = CliConversationCatalog.Normalize(store.AttachmentDirectory);
            var shared = new HashSet<string>(otherChats.SelectMany(AttachmentPaths), Paths);
            var candidates = AttachmentPaths(conversation).Distinct(Paths).Where(file =>
                Paths.Equals(CliConversationCatalog.Normalize(Path.GetDirectoryName(file)), attachmentRoot) &&
                Regex.IsMatch(Path.GetFileName(file), @"^[0-9a-fA-F]{32}-") && !shared.Contains(file) && File.Exists(file)).ToList();
            if (candidates.Count > 0)
            {
                // An imported CLI branch can reference a copy without attachment metadata in Claude UI.
                foreach (string transcript in otherChats.Select(item => item.CliTranscriptPath).Where(File.Exists).Distinct(Paths))
                {
                    using (var stream = new FileStream(transcript, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string line;
                        while (candidates.Count > 0 && (line = reader.ReadLine()) != null)
                            candidates.RemoveAll(file => line.IndexOf(Path.GetFileName(file).Substring(0, 32), StringComparison.OrdinalIgnoreCase) >= 0);
                    }
                    if (candidates.Count == 0) break;
                }
                foreach (string file in candidates) CollectFile(file, files);
            }
            var requestIds = new HashSet<string>(RequestIds(conversation), StringComparer.Ordinal);
            requestIds.ExceptWith(otherChats.SelectMany(RequestIds));

            foreach (string history in histories) RemoveHistoryEntries(history, id);
            foreach (string file in files.Distinct(Paths)) File.Delete(file);
            foreach (string directory in directories.Distinct(Paths)) Directory.Delete(directory);
            store.Statistics.DeleteConversation(id, requestIds);
            foreach (string transcript in transcripts) File.Delete(transcript);
        }

        private static IEnumerable<string> AttachmentPaths(Conversation conversation)
        {
            var attachments = (conversation.DraftAttachments ?? Enumerable.Empty<ChatAttachment>())
                .Concat(conversation.Messages.SelectMany(message => message.Attachments ?? Enumerable.Empty<ChatAttachment>()));
            return attachments.Where(item => item != null && !String.IsNullOrWhiteSpace(item.LocalPath))
                .Select(item => Path.GetFullPath(item.LocalPath));
        }

        private static IEnumerable<string> RequestIds(Conversation conversation)
        {
            return conversation.Messages.Where(message => message.RunSummary != null)
                .SelectMany(message => message.RunSummary.RequestIds ?? Enumerable.Empty<string>()).Where(id => !String.IsNullOrEmpty(id));
        }

        private static void CollectFile(string path, List<string> files)
        {
            if (!File.Exists(path)) return;
            CheckPath(path);
            files.Add(path);
        }

        private static void CollectTree(string path, List<string> files, List<string> directories)
        {
            if (!Directory.Exists(path)) return;
            CheckPath(path);
            foreach (string child in Directory.EnumerateDirectories(path)) CollectTree(child, files, directories);
            foreach (string file in Directory.EnumerateFiles(path)) CollectFile(file, files);
            directories.Add(path);
        }

        private static void CheckPath(string path)
        {
            for (string item = Path.GetFullPath(path); item != null; item = Path.GetDirectoryName(item))
                if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("记录路径包含链接或目录联接，无法安全删除。");
        }

        private static void RemoveHistoryEntries(string path, string id)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            RewriteJsonLines(path, line =>
            {
                try
                {
                    var entry = json.DeserializeObject(line) as IDictionary<string, object>;
                    object value;
                    if (entry != null && entry.TryGetValue("sessionId", out value) &&
                        String.Equals(value as string, id, StringComparison.OrdinalIgnoreCase)) return null;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                return line;
            });
        }

        internal static void RewriteJsonLines(string path, Func<string, string> filter)
        {
            if (!File.Exists(path)) return;
            CheckPath(path);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Keep writers out while rewriting the shared index.
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
                using (var reader = new StreamReader(source, Encoding.UTF8))
                {
                    bool changed = false;
                    using (var writer = new StreamWriter(temporary, false, new UTF8Encoding(false)))
                    {
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            string updated = filter(line);
                            if (updated != null) writer.WriteLine(updated);
                            changed |= updated != line;
                        }
                    }
                    if (changed) File.Replace(temporary, path, null);
                }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
}
