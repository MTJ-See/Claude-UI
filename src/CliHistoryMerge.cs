using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeUI
{
    internal static class CliHistoryMerge
    {
        private static string Key(StoredMessage message) { return message.Role + "\n" + message.Text; }

        public static bool MatchesFailedSession(Conversation saved, Conversation imported)
        {
            if (!String.IsNullOrEmpty(saved.SessionId) || String.IsNullOrEmpty(imported.FirstApiError) ||
                String.IsNullOrEmpty(saved.CliConfigDirectory) || saved.Messages.Count < 2 ||
                saved.Messages[0].Role != "user" || saved.Messages[0].Text != imported.FirstPrompt ||
                (saved.Messages[0].Attachments != null && saved.Messages[0].Attachments.Count != 0) ||
                saved.Messages.Count(message => message.Role == "user" || message.Role == "assistant") != 2 ||
                !String.Equals(CliConversationCatalog.Normalize(saved.Workspace), imported.Workspace, StringComparison.OrdinalIgnoreCase) ||
                !String.Equals(CliConversationCatalog.Normalize(saved.CliConfigDirectory), imported.CliConfigDirectory, StringComparison.OrdinalIgnoreCase))
                return false;
            var reply = saved.Messages[1];
            DateTimeOffset completed, recorded;
            return reply.Role == "assistant" && reply.Text == imported.FirstApiError && reply.RunSummary != null &&
                reply.RunSummary.Status == "执行失败" && DateTimeOffset.TryParse(reply.RunSummary.CompletedUtc, out completed) &&
                DateTimeOffset.TryParse(imported.FirstApiErrorUtc, out recorded) &&
                Math.Abs((completed - recorded).TotalSeconds) <= 5;
        }

        public static List<StoredMessage> Merge(IList<StoredMessage> saved, CliTranscript history)
        {
            var aliases = CliReplyGroups.Aliases(history.Messages);
            Func<StoredMessage, string> keyOf = message => {
                string canonical;
                return message.Role == "assistant" && aliases.TryGetValue(message.Text ?? "", out canonical)
                    ? message.Role + "\n" + canonical : Key(message);
            };
            // Remove old UI artifacts only when this exact internal record is present
            // in the source; reserve occurrences that are genuine visible messages.
            var visibleCounts = history.Messages.GroupBy(keyOf).ToDictionary(group => group.Key, group => group.Count());
            var savedCounts = saved.GroupBy(keyOf).ToDictionary(group => group.Key, group => group.Count());
            var remove = new Dictionary<string, int>();
            foreach (var group in history.InternalMessages.GroupBy(keyOf))
            {
                int count, visible;
                savedCounts.TryGetValue(group.Key, out count);
                visibleCounts.TryGetValue(group.Key, out visible);
                remove[group.Key] = Math.Min(group.Count(), Math.Max(0, count - visible));
            }
            var retained = new List<StoredMessage>();
            foreach (var message in saved)
            {
                string key = keyOf(message);
                int count;
                if (remove.TryGetValue(key, out count) && count > 0) { remove[key] = count - 1; continue; }
                retained.Add(message);
            }
            retained = CliReplyGroups.Group(retained);
            // Merge around ordered anchors. Unmatched cached messages survive truncated,
            // compacted or still-being-written CLI files, including client-only notices.
            var positions = new Dictionary<string, Queue<int>>();
            for (int index = 0; index < history.Messages.Count; index++)
            {
                string key = keyOf(history.Messages[index]);
                Queue<int> queue;
                if (!positions.TryGetValue(key, out queue)) positions[key] = queue = new Queue<int>();
                queue.Enqueue(index);
            }
            var result = new List<StoredMessage>();
            int cursor = 0;
            foreach (var message in retained)
            {
                Queue<int> queue;
                if (positions.TryGetValue(keyOf(message), out queue))
                {
                    while (queue.Count > 0 && queue.Peek() < cursor) queue.Dequeue();
                    if (queue.Count > 0)
                    {
                        int anchor = queue.Dequeue();
                        while (cursor < anchor) result.Add(history.Messages[cursor++]);
                        cursor++; // Keep the saved instance, including attachments and run metadata.
                    }
                }
                result.Add(message);
            }
            while (cursor < history.Messages.Count) result.Add(history.Messages[cursor++]);
            return result;
        }
    }
}
