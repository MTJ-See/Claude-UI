using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ClaudeUI
{
    // Lucide 0.468.0 官方 SVG 路径，来源及 ISC 许可见 third_party/lucide。
    internal static class DeveloperVisuals
    {
        private static readonly Dictionary<string, string> Icons = new Dictionary<string, string> {
            { "terminal", "M 4 17 L 10 11 4 5|M 12,19 L 20,19" },
            { "badge-info", "M3.85 8.62a4 4 0 0 1 4.78-4.77 4 4 0 0 1 6.74 0 4 4 0 0 1 4.78 4.78 4 4 0 0 1 0 6.74 4 4 0 0 1-4.77 4.78 4 4 0 0 1-6.75 0 4 4 0 0 1-4.78-4.77 4 4 0 0 1 0-6.76Z|M 12,16 L 12,12|M 12,8 L 12.01,8" },
            { "stethoscope", "M11 2v2|M5 2v2|M5 3H4a2 2 0 0 0-2 2v4a6 6 0 0 0 12 0V5a2 2 0 0 0-2-2h-1|M8 15a6 6 0 0 0 12 0v-3|M 18.0,10.0 A 2.0,2.0 0 1 0 22.0,10.0 A 2.0,2.0 0 1 0 18.0,10.0" },
            { "shield-check", "M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z|m9 12 2 2 4-4" },
            { "blocks", "M 15.0,3.0 H 20.0 Q 21.0,3.0 21.0,4.0 V 9.0 Q 21.0,10.0 20.0,10.0 H 15.0 Q 14.0,10.0 14.0,9.0 V 4.0 Q 14.0,3.0 15.0,3.0 Z|M10 21V8a1 1 0 0 0-1-1H4a1 1 0 0 0-1 1v12a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1v-5a1 1 0 0 0-1-1H3" },
            { "plug", "M12 22v-5|M9 8V2|M15 8V2|M18 8v5a4 4 0 0 1-4 4h-4a4 4 0 0 1-4-4V8Z" },
            { "list-todo", "M 4.0,5.0 H 8.0 Q 9.0,5.0 9.0,6.0 V 10.0 Q 9.0,11.0 8.0,11.0 H 4.0 Q 3.0,11.0 3.0,10.0 V 6.0 Q 3.0,5.0 4.0,5.0 Z|m3 17 2 2 4-4|M13 6h8|M13 12h8|M13 18h8" },
            { "circle-help", "M 2.0,12.0 A 10.0,10.0 0 1 0 22.0,12.0 A 10.0,10.0 0 1 0 2.0,12.0|M9.09 9a3 3 0 0 1 5.83 1c0 2-3 3-3 3|M12 17h.01" },
            { "folder-open", "m6 14 1.5-2.9A2 2 0 0 1 9.24 10H20a2 2 0 0 1 1.94 2.5l-1.54 6a2 2 0 0 1-1.95 1.5H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h3.9a2 2 0 0 1 1.69.9l.81 1.2a2 2 0 0 0 1.67.9H18a2 2 0 0 1 2 2v2" },
            { "search", "M 3.0,11.0 A 8.0,8.0 0 1 0 19.0,11.0 A 8.0,8.0 0 1 0 3.0,11.0|m21 21-4.3-4.3" },
            { "eye", "M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0|M 9.0,12.0 A 3.0,3.0 0 1 0 15.0,12.0 A 3.0,3.0 0 1 0 9.0,12.0" },
            { "arrow-right", "M5 12h14|m12 5 7 7-7 7" },
            { "sparkles", "M9.937 15.5A2 2 0 0 0 8.5 14.063l-6.135-1.582a.5.5 0 0 1 0-.962L8.5 9.936A2 2 0 0 0 9.937 8.5l1.582-6.135a.5.5 0 0 1 .963 0L14.063 8.5A2 2 0 0 0 15.5 9.937l6.135 1.581a.5.5 0 0 1 0 .964L15.5 14.063a2 2 0 0 0-1.437 1.437l-1.582 6.135a.5.5 0 0 1-.963 0z|M20 3v4|M22 5h-4|M4 17v2|M5 18H3" },
            { "copy", "M 10.0,8.0 H 20.0 Q 22.0,8.0 22.0,10.0 V 20.0 Q 22.0,22.0 20.0,22.0 H 10.0 Q 8.0,22.0 8.0,20.0 V 10.0 Q 8.0,8.0 10.0,8.0 Z|M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2" },
            { "activity", "M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2" },
            { "arrow-down-left", "M17 7 7 17|M17 17H7V7" },
            { "arrow-up-right", "M7 7h10v10|M7 17 17 7" },
            { "wallet", "M19 7V4a1 1 0 0 0-1-1H5a2 2 0 0 0 0 4h15a1 1 0 0 1 1 1v4h-3a2 2 0 0 0 0 4h3a1 1 0 0 0 1-1v-2a1 1 0 0 0-1-1|M3 5v14a2 2 0 0 0 2 2h15a1 1 0 0 0 1-1v-4" }
        };

        private static Geometry IconGeometry(string name)
        {
            var group = new GeometryGroup();
            foreach (string path in (Icons.ContainsKey(name) ? Icons[name] : Icons["blocks"]).Split('|')) group.Children.Add(Geometry.Parse(path));
            return group;
        }

        internal static FrameworkElement Icon(string name, double size = 16, string resource = "MutedInk")
        {
            var canvas = new Canvas { Width = 24, Height = 24 };
            if (resource != null) canvas.SetResourceReference(TextElement.ForegroundProperty, resource);
            var path = new Path { Data = IconGeometry(name), StrokeThickness = 1.7,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
            path.SetBinding(Shape.StrokeProperty, new Binding { RelativeSource = new RelativeSource(RelativeSourceMode.Self), Path = new PropertyPath(TextElement.ForegroundProperty) });
            canvas.Children.Add(path);
            return new Viewbox { Width = size, Height = size, Child = canvas, Stretch = Stretch.Uniform, IsHitTestVisible = false };
        }

        internal static object ButtonContent(string icon, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var image = Icon(icon, 16, null); image.Margin = new Thickness(0, 0, 8, 0); image.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(image);
            var label = new TextBlock { Text = text, VerticalAlignment = VerticalAlignment.Center };
            label.SetBinding(TextBlock.ForegroundProperty, new Binding("Foreground") { RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1) });
            row.Children.Add(label); return row;
        }

        // 悬浮与键盘焦点共用反馈，避免仅鼠标可见的操作状态。
        internal static void CardInteraction(Border card)
        {
            var glow = new DropShadowEffect { Color = Color.FromRgb(139, 92, 246), ShadowDepth = 0, BlurRadius = 8, Opacity = 0 };
            card.Effect = glow;
            Action update = delegate {
                bool active = card.IsMouseOver || card.IsKeyboardFocusWithin;
                card.SetResourceReference(Border.BorderBrushProperty, active ? "CardHoverLine" : "Line");
                double target = active ? .18 : 0;
                if (SystemParameters.ClientAreaAnimation)
                    glow.BeginAnimation(DropShadowEffect.OpacityProperty, new DoubleAnimation(glow.Opacity, target, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.HoldEnd });
                else glow.Opacity = target;
            };
            card.MouseEnter += delegate { update(); }; card.MouseLeave += delegate { update(); };
            card.IsKeyboardFocusWithinChanged += delegate { update(); };
        }
    }

    // 以最小可读宽度决定列数：市场卡片 1/2 列，统计指标 2/4 列。
    internal sealed class ResponsiveCardPanel : Panel
    {
        internal int MaximumColumns = 2;
        internal double MinimumColumnWidth = 340;
        internal double Gap = 12;
        private int Columns(double width) { return Double.IsInfinity(width) ? 1 : Math.Max(1, Math.Min(MaximumColumns, (int)((width + Gap) / (MinimumColumnWidth + Gap)))); }
        protected override Size MeasureOverride(Size available)
        {
            int columns = Columns(available.Width);
            double width = Double.IsInfinity(available.Width) ? MinimumColumnWidth : Math.Max(0, (available.Width - Gap * (columns - 1)) / columns);
            double height = 0;
            for (int i = 0; i < InternalChildren.Count; i += columns)
            {
                double row = 0;
                for (int j = i; j < Math.Min(i + columns, InternalChildren.Count); j++) { InternalChildren[j].Measure(new Size(width, Double.PositiveInfinity)); row = Math.Max(row, InternalChildren[j].DesiredSize.Height); }
                height += row + (i == 0 ? 0 : Gap);
            }
            return new Size(Double.IsInfinity(available.Width) ? width : available.Width, height);
        }
        protected override Size ArrangeOverride(Size available)
        {
            int columns = Columns(available.Width); double width = Math.Max(0, (available.Width - Gap * (columns - 1)) / columns), y = 0;
            for (int i = 0; i < InternalChildren.Count; i += columns)
            {
                double row = 0;
                for (int j = i; j < Math.Min(i + columns, InternalChildren.Count); j++) row = Math.Max(row, InternalChildren[j].DesiredSize.Height);
                for (int j = i; j < Math.Min(i + columns, InternalChildren.Count); j++) InternalChildren[j].Arrange(new Rect((j - i) * (width + Gap), y, width, row));
                y += row + Gap;
            }
            return available;
        }
    }
}
