using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ClaudeUI
{
    internal sealed class MarkdownReply : RichTextBox
    {
        private static readonly Regex Fence = new Regex(@"^ {0,3}(?<marker>~{3,}|`{3,})(?<info>.*)$");
        private static readonly Regex Heading = new Regex(@"^ {0,3}(?<level>#{1,6})(?:[ \t]+|$)(?<text>.*)$");
        private static readonly Regex HeadingSuffix = new Regex(@"[ \t]+#+[ \t]*$");
        private static readonly Regex ListItem = new Regex(@"^(?<indent> *)(?<marker>[-+*]|\d{1,9}[.)])[ \t]+(?<text>.*)$");
        private static readonly Regex Quote = new Regex(@"^ {0,3}>[ \t]?(?<text>.*)$");
        private static readonly Regex Rule = new Regex(@"^ {0,3}(?:(?:\*[ \t]*){3,}|(?:-[ \t]*){3,}|(?:_[ \t]*){3,})$");
        private static readonly Regex Inline = new Regex(
            @"\\(?<escape>[!""#$%&'()*+,\-./:;<=>?@\[\]\\^_`{|}~])" +
            @"|(?<!`)(?<ticks>`+)(?!`)(?<code>.+?)(?<!`)\k<ticks>(?!`)" +
            @"|\[(?<label>[^\[\]]+)\]\((?<target>[^()\s]+)\)" +
            @"|(?<style>\*{1,3}|(?<underscore>(?<!\w)_{1,3})|~~)(?![\s*_~])" +
            @"(?<text>(?>\\[\\`*_~]|(?<!`)(?<innerTicks>`+)(?!`).+?(?<!`)\k<innerTicks>(?!`)|.)+?)" +
            @"(?<![\s*_~])\k<style>(?![*_~])(?(underscore)(?!\w))");
        private static readonly FontFamily CodeFont = new FontFamily("Consolas");
        private readonly StringBuilder source = new StringBuilder();
        private Block preview;
        private Paragraph headerCandidate, hiddenPreviewHeader;
        private string[] headerCells;
        private PipeTable activeTable, previewTable;
        private TableRow previewRow;
        private readonly List<PipeTable> tables = new List<PipeTable>();
        private string fence;
        private int lineStart;

        public MarkdownReply(string text)
        {
            IsReadOnly = true;
            IsUndoEnabled = false;
            BorderThickness = new Thickness(0);
            Padding = new Thickness(0);
            Background = Brushes.Transparent;
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            Document.PagePadding = new Thickness(0);
            Document.Blocks.Clear();
            SizeChanged += delegate { ResizeTables(); };
            AppendMarkdown(text);
        }

        public string Markdown { get { return source.ToString(); } }

        public void AppendMarkdown(string text)
        {
            if (String.IsNullOrEmpty(text)) return;
            BeginChange();
            try
            {
                if (preview != null) Document.Blocks.Remove(preview);
                preview = null;
                if (previewRow != null && activeTable != null) activeTable.RemoveRow(previewRow);
                previewRow = null;
                if (hiddenPreviewHeader != null) Document.Blocks.Add(hiddenPreviewHeader);
                hiddenPreviewHeader = null;
                previewTable = null;
                int chunkStart = source.Length;
                source.Append(text);
                for (int i = 0; i < text.Length; i++)
                {
                    if (text[i] != '\n') continue;
                    int length = chunkStart + i - lineStart;
                    if (length > 0 && source[lineStart + length - 1] == '\r') length--;
                    var block = RenderTableLine(source.ToString(lineStart, length), true);
                    if (block != null) Document.Blocks.Add(block);
                    lineStart = chunkStart + i + 1;
                }
                preview = lineStart < source.Length
                    ? RenderTableLine(source.ToString(lineStart, source.Length - lineStart), false)
                    : null;
                if (preview != null) Document.Blocks.Add(preview);
                ResizeTables();
            }
            finally { EndChange(); }
        }

        private void ResizeTables()
        {
            double width = ActualWidth > 0 ? ActualWidth - Padding.Left - Padding.Right - 8 : 700;
            foreach (var table in tables) table.Resize(width);
            if (previewTable != null) previewTable.Resize(width);
        }

        private Block RenderTableLine(string line, bool commit)
        {
            string[] cells = null;
            TextAlignment[] alignment;
            bool eligible = fence == null && !Fence.IsMatch(line) && !Heading.IsMatch(line) && !Quote.IsMatch(line) && !ListItem.IsMatch(line);
            bool row = eligible && PipeTable.TrySplit(line, out cells);
            if (!row) cells = null;
            if (activeTable != null)
            {
                if (row && cells.Length <= activeTable.Columns && activeTable.RowCount < 1000 && !PipeTable.TryDivider(cells, out alignment))
                {
                    var added = activeTable.AddRow(cells);
                    if (!commit) previewRow = added;
                    return null;
                }
                if (commit) activeTable = null;
            }
            if (row && headerCandidate != null && cells.Length == headerCells.Length && PipeTable.TryDivider(cells, out alignment))
            {
                var table = new PipeTable(headerCells, alignment, (inlines, value) => AddInline(inlines, value));
                Document.Blocks.Remove(headerCandidate);
                if (commit)
                {
                    activeTable = table;
                    tables.Add(table);
                    headerCandidate = null;
                    headerCells = null;
                }
                else
                {
                    hiddenPreviewHeader = headerCandidate;
                    previewTable = table;
                }
                return table.View;
            }
            var paragraph = RenderLine(line, commit);
            if (commit)
            {
                headerCandidate = row ? paragraph : null;
                headerCells = row ? cells : null;
            }
            return paragraph;
        }

        private Paragraph RenderLine(string line, bool commit)
        {
            var marker = Fence.Match(line);
            var block = new Paragraph { Margin = new Thickness(0) };
            if (fence != null)
            {
                if (marker.Success && marker.Groups["marker"].Value[0] == fence[0] &&
                    marker.Groups["marker"].Length >= fence.Length && String.IsNullOrWhiteSpace(marker.Groups["info"].Value))
                {
                    if (commit) fence = null;
                    return null;
                }
                block.FontFamily = CodeFont;
                block.SetResourceReference(Block.BackgroundProperty, "Canvas");
                block.Inlines.Add(new Run(line));
                return block;
            }
            if (marker.Success)
            {
                if (commit) fence = marker.Groups["marker"].Value;
                return null;
            }
            if (Rule.IsMatch(line))
            {
                block.BorderThickness = new Thickness(0, 0, 0, 1);
                block.Margin = new Thickness(0, 7, 0, 7);
                block.SetResourceReference(Block.BorderBrushProperty, "Line");
                return block;
            }
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                block.FontWeight = FontWeights.SemiBold;
                block.FontSize = Math.Max(14, 23 - heading.Groups["level"].Length * 2);
                block.Margin = new Thickness(0, 5, 0, 3);
                line = HeadingSuffix.Replace(" " + heading.Groups["text"].Value, "").TrimStart();
            }
            else
            {
                var list = ListItem.Match(line);
                var quote = Quote.Match(line);
                if (list.Success)
                {
                    string bullet = list.Groups["marker"].Value;
                    block.Margin = new Thickness(14 + Math.Min(40, list.Groups["indent"].Length * 6), 0, 0, 0);
                    block.TextIndent = -14;
                    block.Inlines.Add(new Run((bullet.Length == 1 ? "•" : bullet) + " "));
                    line = list.Groups["text"].Value;
                }
                else if (quote.Success)
                {
                    block.Padding = new Thickness(8, 0, 0, 0);
                    block.BorderThickness = new Thickness(2, 0, 0, 0);
                    block.SetResourceReference(Block.BorderBrushProperty, "Line");
                    line = quote.Groups["text"].Value;
                }
            }
            if (line.Length == 0 && !heading.Success) block.LineHeight = 7;
            AddInline(block.Inlines, line);
            return block;
        }

        private static void AddInline(InlineCollection inlines, string text, int depth = 0)
        {
            if (depth == 8) { inlines.Add(new Run(text)); return; }
            int offset = 0;
            foreach (Match match in Inline.Matches(text))
            {
                if (match.Index > offset) inlines.Add(new Run(text.Substring(offset, match.Index - offset)));
                if (match.Groups["escape"].Success)
                    inlines.Add(new Run(match.Groups["escape"].Value));
                else if (match.Groups["code"].Success)
                {
                    var code = new Run(match.Groups["code"].Value) { FontFamily = CodeFont };
                    code.SetResourceReference(TextElement.BackgroundProperty, "Canvas");
                    inlines.Add(code);
                }
                else if (match.Groups["label"].Success)
                {
                    var link = new Span { TextDecorations = TextDecorations.Underline };
                    ToolTipService.SetToolTip(link, match.Groups["target"].Value);
                    AddInline(link.Inlines, match.Groups["label"].Value, depth + 1);
                    inlines.Add(link);
                }
                else
                {
                    string style = match.Groups["style"].Value;
                    var span = new Span();
                    if (style == "~~") span.TextDecorations = TextDecorations.Strikethrough;
                    else
                    {
                        if (style.Length >= 2) span.FontWeight = FontWeights.Bold;
                        if (style.Length != 2) span.FontStyle = FontStyles.Italic;
                    }
                    AddInline(span.Inlines, match.Groups["text"].Value, depth + 1);
                    inlines.Add(span);
                }
                offset = match.Index + match.Length;
            }
            if (offset < text.Length) inlines.Add(new Run(text.Substring(offset)));
        }
    }
}
