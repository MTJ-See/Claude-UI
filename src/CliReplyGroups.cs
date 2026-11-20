using System;
using System.Collections.Generic;
using System.Linq;

namespace ClaudeUI
{
    internal static class CliReplyGroups
    {
        public static List<StoredMessage> Group(IEnumerable<StoredMessage> messages)
        {
            var result = new List<StoredMessage>();
            foreach (var message in messages)
            {
                var previous = result.Count == 0 ? null : result[result.Count - 1];
                // Tool results and internal CLI records have already been filtered out.
                // A real user message starts a new turn; keep independently recorded runs apart.
                if (message.Role != "assistant" || previous == null || previous.Role != "assistant" ||
                    (previous.RunSummary != null && message.RunSummary != null))
                {
                    result.Add(message);
                    continue;
                }
                var parts = new List<string>(previous.CliReplyParts ?? new List<string> { previous.Text ?? "" });
                parts.AddRange(message.CliReplyParts ?? new List<string> { message.Text ?? "" });
                result[result.Count - 1] = new StoredMessage {
                    Role = "assistant", Text = (previous.Text ?? "") + "\n\n" + (message.Text ?? ""),
                    RunSummary = message.RunSummary ?? previous.RunSummary,
                    Attachments = previous.Attachments == null ? message.Attachments : message.Attachments == null ? previous.Attachments :
                        previous.Attachments.Concat(message.Attachments).ToList(),
                    CliReplyParts = parts
                };
            }
            return result;
        }

        public static Dictionary<string, string> Aliases(IEnumerable<StoredMessage> messages)
        {
            var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var message in messages.Where(item => item.Role == "assistant" && item.CliReplyParts != null))
            {
                // The live renderer appends deltas directly; CLI history stores the
                // same reply in separate records. Match only exact known segment joins.
                foreach (string separator in new[] { "", "\n", "\r\n", "\n\n", "\r\n\r\n" })
                    aliases[String.Join(separator, message.CliReplyParts)] = message.Text;
            }
            return aliases;
        }
    }
}
