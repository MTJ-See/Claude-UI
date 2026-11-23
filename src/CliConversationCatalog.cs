using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace ClaudeUI
{
    internal sealed class CliCatalogSnapshot
    {
        public readonly List<Conversation> Conversations = new List<Conversation>();
        public readonly HashSet<string> Projects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public int UnreadableFiles;
    }

    internal sealed class CliTranscript
    {
        public readonly List<StoredMessage> Messages = new List<StoredMessage>();
        public readonly List<StoredMessage> InternalMessages = new List<StoredMessage>();
    }

    internal sealed class CliConversationCatalog
    {
        private sealed class CachedSession
        {
            public long Length;
            public DateTime Modified;
            public Conversation Conversation;
        }
        private readonly Dictionary<string, CachedSession> cache = new Dictionary<string, CachedSession>(StringComparer.OrdinalIgnoreCase);

        public static string Normalize(string path)
        {
            try { return String.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar; }
            catch (ArgumentException) { return null; }
            catch (NotSupportedException) { return null; }
            catch (PathTooLongException) { return null; }
        }

        public static string Key(Conversation conversation)
        {
            return conversation.SessionId ?? conversation.Id;
        }

        internal static bool TranscriptMatchesWorkspace(string file, string root, string id, string workspace)
        {
            var metadata = ReadMetadata(file, root, id);
            return metadata != null && String.Equals(metadata.Workspace, Normalize(workspace), StringComparison.OrdinalIgnoreCase);
        }

        public static string FindPersistedTranscript(string root, string workspace, string sessionId)
        {
            Guid id;
            if (String.IsNullOrEmpty(root) || !Guid.TryParse(sessionId, out id)) return null;
            string projects = Path.Combine(root, "projects");
            try
            {
                if (!Directory.Exists(projects)) return null;
                foreach (string folder in Directory.EnumerateDirectories(projects))
                {
                    string file = Path.Combine(folder, id.ToString() + ".jsonl");
                    if (!File.Exists(file)) continue;
                    var record = ReadMetadata(file, root, id.ToString());
                    if (record != null && String.Equals(record.Workspace, Normalize(workspace), StringComparison.OrdinalIgnoreCase))
                        return file;
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            return null;
        }

        public CliCatalogSnapshot Read(IEnumerable<string> roots)
        {
            var result = new CliCatalogSnapshot();
            foreach (string root in roots.Select(Normalize).Where(item => item != null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ReadProjectSettings(Path.Combine(root, ".claude.json"), result);
                ReadProjectSettings(root.TrimEnd(Path.DirectorySeparatorChar) + ".json", result);
                ReadHistoryProjects(Path.Combine(root, "history.jsonl"), result);
                string projects = Path.Combine(root, "projects");
                try
                {
                    if (!Directory.Exists(projects)) continue;
                    foreach (string folder in Directory.GetDirectories(projects))
                    {
                        try
                        {
                            // Only top-level session files: subagent transcripts are not independent chats.
                            foreach (string file in Directory.GetFiles(folder, "*.jsonl", SearchOption.TopDirectoryOnly))
                            {
                                Guid id;
                                if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out id)) continue;
                                try
                                {
                                    var info = new FileInfo(file);
                                    CachedSession previous;
                                    if (!cache.TryGetValue(file, out previous) || previous.Length != info.Length || previous.Modified != info.LastWriteTimeUtc)
                                    {
                                        var conversation = ReadMetadata(file, root, id.ToString());
                                        previous = new CachedSession { Length = info.Length, Modified = info.LastWriteTimeUtc, Conversation = conversation };
                                        cache[file] = previous;
                                    }
                                    if (previous.Conversation == null) continue;
                                    result.Conversations.Add(previous.Conversation);
                                    result.Projects.Add(previous.Conversation.Workspace);
                                }
                                catch (IOException) { result.UnreadableFiles++; }
                                catch (UnauthorizedAccessException) { result.UnreadableFiles++; }
                            }
                        }
                        catch (IOException) { result.UnreadableFiles++; }
                        catch (UnauthorizedAccessException) { result.UnreadableFiles++; }
                    }
                }
                catch (IOException) { result.UnreadableFiles++; }
                catch (UnauthorizedAccessException) { result.UnreadableFiles++; }
            }
            return result;
        }

        private static Conversation ReadMetadata(string file, string root, string id)
        {
            string workspace = null, title = null, first = null, firstError = null, firstErrorUtc = null;
            bool mainMessage = false, awaitingFirstReply = false;
            var localCommands = new LocalCommandRecords();
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            foreach (var item in ReadRecords(file, json))
            {
                if (Equals(Get(item, "isSidechain"), true)) continue;
                bool internalCommand = localCommands.Observe(item);
                string type = Text(item, "type");
                if (type == "user" || type == "assistant")
                {
                    mainMessage = true;
                    if (workspace == null) workspace = Normalize(Text(item, "cwd"));
                    if (type == "user" && !Equals(Get(item, "isMeta"), true) &&
                        !Equals(Get(item, "isCompactSummary"), true) && !internalCommand)
                    {
                        string content = MessageText(item);
                        awaitingFirstReply = first == null && !String.IsNullOrWhiteSpace(content);
                        if (awaitingFirstReply) first = content;
                    }
                    if (type == "assistant" && awaitingFirstReply)
                    {
                        awaitingFirstReply = false;
                        if (Equals(Get(item, "isApiErrorMessage"), true))
                        {
                            firstError = MessageText(item);
                            firstErrorUtc = Text(item, "timestamp");
                        }
                    }
                }
                if (type == "custom-title" && !String.IsNullOrWhiteSpace(Text(item, "customTitle"))) title = Text(item, "customTitle");
                else if (title == null && type == "summary") title = Text(item, "summary");
            }
            if (!mainMessage || workspace == null) return null;
            string display = title ?? first ?? ("CLI 会话 " + id.Substring(0, 8));
            display = System.Text.RegularExpressions.Regex.Replace(display, @"\s+", " ").Trim();
            return new Conversation {
                Id = "cli:" + root + ":" + id, SessionId = id, Workspace = workspace,
                CliConfigDirectory = root, CliTranscriptPath = file, Title = display.Length > 100 ? display.Substring(0, 100) : display,
                UpdatedUtc = File.GetLastWriteTimeUtc(file).ToString("o"),
                FirstPrompt = first, FirstApiError = firstError, FirstApiErrorUtc = firstErrorUtc
            };
        }

        public static List<StoredMessage> ReadMessages(string file)
        {
            return ReadTranscript(file).Messages;
        }

        public static CliTranscript ReadTranscript(string file)
        {
            var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
            var records = new List<IDictionary<string, object>>();
            var byId = new Dictionary<string, IDictionary<string, object>>(StringComparer.Ordinal);
            foreach (var record in ReadRecords(file, json))
            {
                if (Equals(Get(record, "isSidechain"), true)) continue;
                string type = Text(record, "type");
                // Non-display records (attachments, environment snapshots, etc.) also
                // connect conversation turns. Keep them in the ancestry index.
                string uuid = Text(record, "uuid");
                if (uuid.Length > 0) byId[uuid] = record;
                if (type == "user" || type == "assistant" || type == "system") records.Add(record);
            }
            // Follow the latest main conversation branch; keep fork ancestry but omit abandoned turns.
            var selected = new HashSet<string>(StringComparer.Ordinal);
            if (records.Count > 0)
            {
                var cursor = records[records.Count - 1];
                while (cursor != null)
                {
                    string uuid = Text(cursor, "uuid");
                    if (uuid.Length == 0 || !selected.Add(uuid)) break;
                    string parent = Text(cursor, "parentUuid");
                    if (!byId.TryGetValue(parent, out cursor)) break;
                }
            }
            var result = new CliTranscript();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var localCommands = new LocalCommandRecords();
            foreach (var record in records)
            {
                string type = Text(record, "type"), uuid = Text(record, "uuid");
                bool internalCommand = localCommands.Observe(record);
                if (type == "system" || Equals(Get(record, "isMeta"), true) || Equals(Get(record, "isCompactSummary"), true)) continue;
                if (selected.Count > 1 && uuid.Length > 0 && !selected.Contains(uuid)) continue;
                if (uuid.Length > 0 && !seen.Add(uuid)) continue;
                string text = MessageText(record);
                if (String.IsNullOrWhiteSpace(text)) continue;
                (internalCommand ? result.InternalMessages : result.Messages).Add(new StoredMessage { Role = type, Text = text });
            }
            var replies = CliReplyGroups.Group(result.Messages);
            result.Messages.Clear();
            result.Messages.AddRange(replies);
            return result;
        }

        // CLI local commands are stored as user records without isMeta. The preceding
        // metadata record identifies their origin; do not hide ordinary pasted XML.
        private sealed class LocalCommandRecords
        {
            private readonly HashSet<string> contexts = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> commands = new HashSet<string>(StringComparer.Ordinal);
            private readonly HashSet<string> prompts = new HashSet<string>(StringComparer.Ordinal);

            public bool Observe(IDictionary<string, object> record)
            {
                if (Text(record, "type") != "user") return false;
                string text = MessageText(record).Trim();
                string uuid = Text(record, "uuid"), prompt = Text(record, "promptId");
                if (Equals(Get(record, "isMeta"), true) && text.StartsWith("<local-command-caveat>", StringComparison.Ordinal))
                {
                    if (uuid.Length > 0) contexts.Add(uuid);
                    if (prompt.Length > 0) prompts.Add(prompt);
                    return true;
                }
                string parent = Text(record, "parentUuid");
                bool samePrompt = prompt.Length > 0 && prompts.Contains(prompt);
                bool command = (contexts.Contains(parent) || samePrompt) && Regex.IsMatch(text,
                    @"\A<command-name>/[^<\r\n]+</command-name>\s*<command-message>[^<\r\n]+</command-message>\s*(?:<command-args>[\s\S]*</command-args>\s*)?\z");
                bool output = (commands.Contains(parent) || samePrompt) &&
                    Regex.IsMatch(text, @"\A<local-command-(stdout|stderr)>[\s\S]*</local-command-\1>\z");
                if (!command && !output) return false;
                if (command && uuid.Length > 0) commands.Add(uuid);
                return true;
            }
        }

        private static string MessageText(IDictionary<string, object> record)
        {
            var message = Get(record, "message") as IDictionary<string, object>;
            object content = Get(message, "content");
            var plain = content as string;
            if (plain != null) return plain;
            var parts = content as object[];
            if (parts == null) return "";
            return String.Join(Environment.NewLine, parts.OfType<IDictionary<string, object>>()
                .Where(part => Text(part, "type") == "text").Select(part => Text(part, "text")));
        }

        private static IEnumerable<IDictionary<string, object>> ReadRecords(string file, JavaScriptSerializer json)
        {
            using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    IDictionary<string, object> record = null;
                    try { record = json.DeserializeObject(line) as IDictionary<string, object>; }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
                    if (record != null) yield return record;
                }
            }
        }

        private static void ReadHistoryProjects(string file, CliCatalogSnapshot result)
        {
            if (!File.Exists(file)) return;
            try
            {
                foreach (var record in ReadRecords(file, new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue }))
                {
                    string path = Normalize(Text(record, "project"));
                    if (path != null) result.Projects.Add(path);
                }
            }
            catch (IOException) { result.UnreadableFiles++; }
            catch (UnauthorizedAccessException) { result.UnreadableFiles++; }
        }

        private static void ReadProjectSettings(string file, CliCatalogSnapshot result)
        {
            if (!File.Exists(file)) return;
            try
            {
                var json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue };
                var settings = json.DeserializeObject(File.ReadAllText(file, Encoding.UTF8)) as IDictionary<string, object>;
                var projects = Get(settings, "projects") as IDictionary<string, object>;
                if (projects == null) return;
                foreach (string path in projects.Keys)
                {
                    string normalized = Normalize(path);
                    if (normalized != null) result.Projects.Add(normalized);
                }
            }
            catch (IOException) { result.UnreadableFiles++; }
            catch (UnauthorizedAccessException) { result.UnreadableFiles++; }
            catch (ArgumentException) { result.UnreadableFiles++; }
            catch (InvalidOperationException) { result.UnreadableFiles++; }
        }

        private static object Get(IDictionary<string, object> item, string key) { object value; return item != null && item.TryGetValue(key, out value) ? value : null; }
        private static string Text(IDictionary<string, object> item, string key) { return Get(item, key) as string ?? ""; }
    }
}
