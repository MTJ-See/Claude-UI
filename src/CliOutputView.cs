using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeUI
{
    // Presentation only: the original CLI output remains in MainWindow's raw log.
    internal sealed class CliOutputView
    {
        private readonly RichTextBox view;
        private int characters;
        private Paragraph tableHeader;
        private string[] tableHeaderCells;
        private PipeTable activeTable;
        private string fence;
        private readonly Dictionary<Table, PipeTable> tables = new Dictionary<Table, PipeTable>();
        private bool follow = true;

        public CliOutputView(RichTextBox view)
        {
            this.view = view;
            view.Document.PagePadding = new Thickness(0);
            view.Document.Blocks.Clear();
            view.SizeChanged += delegate { ResizeTables(); };
        }

        public void Clear()
        {
            view.Document.Blocks.Clear();
            characters = 0;
            tableHeader = null;
            tableHeaderCells = null;
            activeTable = null;
            fence = null;
            tables.Clear();
            follow = true;
        }

        public void Append(string text)
        {
            if (view.Document.Blocks.Count == 0)
                follow = true;
            else if (view.IsVisible)
                follow = view.ExtentHeight - view.VerticalOffset - view.ViewportHeight < 30;

            foreach (string line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string trimmed = line.TrimStart();
                bool marker = trimmed.StartsWith("```", StringComparison.Ordinal) || trimmed.StartsWith("~~~", StringComparison.Ordinal);
                if (marker)
                {
                    if (fence == null) fence = trimmed.Substring(0, 3);
                    else if (trimmed.StartsWith(fence, StringComparison.Ordinal)) fence = null;
                }
                if (!marker && fence == null && TryTable(line)) continue;
                activeTable = null;
                tableHeader = null;
                tableHeaderCells = null;
                if (line.StartsWith("No MCP servers configured.", StringComparison.Ordinal))
                {
                    Add("尚未配置 MCP 服务", "Ink", true);
                    Add("当前环境还没有添加 MCP 服务。配置后，可在这里查看服务列表和连接状态。", "MutedInk", false);
                    Add("添加服务：claude mcp add", "MutedInk", false, true);
                    continue;
                }
                bool error = line.StartsWith("[stderr] ", StringComparison.Ordinal) || line.StartsWith("错误 · ", StringComparison.Ordinal);
                // Preserve spacing and JSON literally; only validated pipe tables receive structured layout.
                bool code = line.StartsWith(" ", StringComparison.Ordinal) || line.StartsWith("\t", StringComparison.Ordinal) ||
                    line.TrimStart().StartsWith("{", StringComparison.Ordinal) || line.TrimStart().StartsWith("[", StringComparison.Ordinal);
                var paragraph = Add(line, error ? "Failure" : "Ink", false, code);
                string[] cells;
                if (!error && !marker && fence == null && PipeTable.TrySplit(line, out cells))
                {
                    tableHeader = paragraph;
                    tableHeaderCells = cells;
                }
            }
        }

        private bool TryTable(string line)
        {
            string[] cells;
            TextAlignment[] alignment;
            if (!PipeTable.TrySplit(line, out cells)) return false;
            if (activeTable != null && cells.Length <= activeTable.Columns && activeTable.RowCount < 1000 && !PipeTable.TryDivider(cells, out alignment))
            {
                activeTable.AddRow(cells);
                activeTable.View.Tag = (int)activeTable.View.Tag + line.Length;
                characters += line.Length;
                Trim();
                return true;
            }
            if (tableHeader != null && cells.Length == tableHeaderCells.Length && PipeTable.TryDivider(cells, out alignment))
            {
                int count = (int)tableHeader.Tag;
                view.Document.Blocks.Remove(tableHeader);
                var table = new PipeTable(tableHeaderCells, alignment, (inlines, value) => inlines.Add(new Run(value.Replace("\\|", "|"))));
                table.View.Tag = count + line.Length;
                characters += line.Length;
                view.Document.Blocks.Add(table.View);
                tables.Add(table.View, table);
                activeTable = table;
                tableHeader = null;
                tableHeaderCells = null;
                Trim();
                return true;
            }
            return false;
        }

        private void ResizeTables()
        {
            double width = view.ActualWidth > 0 ? view.ActualWidth - view.Padding.Left - view.Padding.Right - 8 : 700;
            foreach (var table in tables.Values) table.Resize(width);
        }

        private Paragraph Add(string text, string color, bool heading, bool code = false)
        {
            if (text.Length > 220000) text = "[仅显示该行最近 20 万字符] " + text.Substring(text.Length - 200000);
            var paragraph = new Paragraph(new Run(text)) {
                Margin = new Thickness(0, 0, 0, heading ? 12 : 5),
                FontSize = heading ? 21 : 14,
                FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
                LineHeight = heading ? 30 : 24,
                Tag = text.Length
            };
            if (code) paragraph.FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI");
            paragraph.SetResourceReference(TextElement.ForegroundProperty, color);
            view.Document.Blocks.Add(paragraph);
            characters += text.Length;
            Trim();
            return paragraph;
        }

        private void Trim()
        {
            while (view.Document.Blocks.Count > 1200 || characters > 220000)
            {
                var first = view.Document.Blocks.FirstBlock;
                characters -= (int)first.Tag;
                if (first == tableHeader) { tableHeader = null; tableHeaderCells = null; }
                if (activeTable != null && first == activeTable.View) activeTable = null;
                var table = first as Table;
                if (table != null) tables.Remove(table);
                view.Document.Blocks.Remove(first);
            }
        }

        public void Flush()
        {
            ResizeTables();
            if (follow) view.ScrollToEnd();
        }
    }
}
