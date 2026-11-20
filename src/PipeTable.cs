using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace ClaudeUI
{
    // Local, deterministic Markdown table parsing. Never rewrites the stored reply or invokes a model.
    internal sealed class PipeTable
    {
        private static readonly Regex Divider = new Regex(@"^:?-{3,}:?$");
        private readonly Action<InlineCollection, string> inline;
        private readonly TextAlignment[] alignment;
        private readonly double[] weights;
        private readonly TableRowGroup rows = new TableRowGroup();
        public readonly Table View = new Table { CellSpacing = 0, BorderThickness = new Thickness(1, 1, 0, 0), Margin = new Thickness(0, 12, 0, 16) };

        public PipeTable(string[] header, TextAlignment[] alignment, Action<InlineCollection, string> inline)
        {
            this.inline = inline;
            this.alignment = alignment;
            weights = new double[header.Length];
            View.SetResourceReference(Block.BorderBrushProperty, "Line");
            for (int i = 0; i < header.Length; i++)
            {
                View.Columns.Add(new TableColumn());
                weights[i] = Weight(header[i]);
            }
            View.RowGroups.Add(rows);
            AddRow(header, true);
        }

        public int Columns { get { return weights.Length; } }
        public int RowCount { get { return rows.Rows.Count; } }

        public TableRow AddRow(string[] cells, bool header = false)
        {
            var row = new TableRow();
            for (int i = 0; i < weights.Length; i++)
            {
                string text = i < cells.Length ? cells[i] : "";
                weights[i] = Math.Max(weights[i], Weight(text));
                var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = alignment[i], LineHeight = 24 };
                inline(paragraph.Inlines, text);
                var cell = new TableCell(paragraph) { Padding = new Thickness(12, 8, 12, 8), BorderThickness = new Thickness(0, 0, 1, 1),
                    FontSize = 14, FontWeight = header ? FontWeights.SemiBold : FontWeights.Normal };
                cell.SetResourceReference(TextElement.ForegroundProperty, "Ink");
                cell.SetResourceReference(TextElement.BackgroundProperty, header ? "Selected" : rows.Rows.Count % 2 == 0 ? "Sidebar" : "Panel");
                cell.SetResourceReference(TableCell.BorderBrushProperty, "Line");
                row.Cells.Add(cell);
            }
            rows.Rows.Add(row);
            return row;
        }

        public void RemoveRow(TableRow row) { rows.Rows.Remove(row); }

        public void Resize(double width)
        {
            double total = 0;
            foreach (double weight in weights) total += weight;
            // Reserve readable space in every column, then distribute the remainder by content length.
            double available = Math.Max(1, width - 3);
            double minimum = Math.Min(72, available / weights.Length);
            double remaining = Math.Max(0, available - minimum * weights.Length);
            for (int i = 0; i < weights.Length; i++)
            {
                double desired = Math.Floor(minimum + remaining * weights[i] / total);
                if (Math.Abs(View.Columns[i].Width.Value - desired) > 1 || View.Columns[i].Width.IsAuto)
                    View.Columns[i].Width = new GridLength(desired);
            }
        }

        private static double Weight(string text)
        {
            int length = 0;
            foreach (char c in text) length += c >= 0x2e80 ? 2 : 1;
            return Math.Max(6, Math.Min(48, length));
        }

        public static bool TryDivider(string[] cells, out TextAlignment[] alignment)
        {
            alignment = new TextAlignment[cells.Length];
            for (int i = 0; i < cells.Length; i++)
            {
                string cell = cells[i].Trim();
                if (!Divider.IsMatch(cell)) return false;
                alignment[i] = cell.EndsWith(":", StringComparison.Ordinal)
                    ? cell.StartsWith(":", StringComparison.Ordinal) ? TextAlignment.Center : TextAlignment.Right : TextAlignment.Left;
            }
            return true;
        }

        public static bool TrySplit(string line, out string[] cells)
        {
            var result = new List<string>();
            var part = new StringBuilder();
            bool pipe = false;
            int ticks = 0;
            string trimmed = line.Trim();
            for (int i = 0; i < trimmed.Length; i++)
            {
                char c = trimmed[i];
                if (c == '\\' && i + 1 < trimmed.Length)
                {
                    part.Append(c).Append(trimmed[++i]);
                    continue;
                }
                if (c == '`')
                {
                    int run = 1;
                    while (i + run < trimmed.Length && trimmed[i + run] == '`') run++;
                    if (ticks == 0) ticks = run;
                    else if (ticks == run) ticks = 0;
                    part.Append('`', run);
                    i += run - 1;
                    continue;
                }
                if (c == '|' && ticks == 0)
                {
                    result.Add(part.ToString().Trim());
                    part.Clear();
                    pipe = true;
                }
                else part.Append(c);
            }
            result.Add(part.ToString().Trim());
            if (result.Count > 1 && trimmed.StartsWith("|", StringComparison.Ordinal)) result.RemoveAt(0);
            if (result.Count > 1 && result[result.Count - 1].Length == 0 && trimmed.EndsWith("|", StringComparison.Ordinal)) result.RemoveAt(result.Count - 1);
            cells = result.ToArray();
            return pipe && cells.Length > 0 && cells.Length <= 24;
        }
    }
}
