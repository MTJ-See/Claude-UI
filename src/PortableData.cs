using System;
using System.IO;
using System.Collections.Generic;

namespace ClaudeUI
{
    internal static class PortableData
    {
        public static string Prepare(string applicationDirectory, string legacyDirectory)
        {
            string destination = Path.Combine(applicationDirectory, "Data");
            if (File.Exists(Path.Combine(destination, "state.json"))) return destination;
            if (!File.Exists(Path.Combine(legacyDirectory, "state.json")))
            {
                Directory.CreateDirectory(destination);
                return destination;
            }
            if (Directory.Exists(destination) && Directory.GetFileSystemEntries(destination).Length > 0)
                throw new IOException("Data 目录已有文件但缺少 state.json，未覆盖现有数据。请先备份并检查此目录。");
            string staging = Path.Combine(applicationDirectory, ".data-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            // Publish the complete migration only after every file and state record succeeds.
            foreach (string name in new[] { "state.json", "request-statistics.jsonl" })
            {
                string source = Path.Combine(legacyDirectory, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name));
            }
            string oldAttachments = Path.Combine(legacyDirectory, "attachments");
            if (Directory.Exists(oldAttachments)) CopyDirectory(oldAttachments, Path.Combine(staging, "attachments"));
            var store = new DataStore(staging);
            var data = store.Read();
            foreach (var conversation in data.Conversations)
            {
                Rebase(conversation.DraftAttachments, oldAttachments, Path.Combine(destination, "attachments"));
                foreach (var message in conversation.Messages)
                    Rebase(message.Attachments, oldAttachments, Path.Combine(destination, "attachments"));
            }
            store.Save(data);
            if (Directory.Exists(destination)) Directory.Delete(destination); // verified empty above; never recursive
            Directory.Move(staging, destination);
            return destination;
        }

        private static void Rebase(IEnumerable<ChatAttachment> attachments, string source, string destination)
        {
            if (attachments == null) return;
            string prefix = Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var attachment in attachments)
                if (attachment.LocalPath != null && attachment.LocalPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    attachment.LocalPath = Path.Combine(destination, attachment.LocalPath.Substring(prefix.Length));
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (string directory in Directory.GetDirectories(source))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("附件目录包含链接，迁移已停止：" + directory);
                CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
            }
        }
    }
}
