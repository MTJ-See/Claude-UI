using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeUI
{
    internal static class ProjectActionVisuals
    {
        public static FrameworkElement Icon(string name, bool danger = false)
        {
            string path;
            switch (name)
            {
                case "compact": path = "M3 3 L9 9 M4 9 H9 V4 M21 21 L15 15 M15 20 V15 H20 M15 3 H21 V9 M3 15 V21 H9"; break;
                case "branch": path = "M6 7 V17 M6 12 H13 Q18 12 18 7 M6 3 A2 2 0 1 0 6 7 A2 2 0 1 0 6 3 M6 17 A2 2 0 1 0 6 21 A2 2 0 1 0 6 17 M18 3 A2 2 0 1 0 18 7 A2 2 0 1 0 18 3"; break;
                case "copy": path = "M8 8 H20 V21 H8 Z M16 4 V2 H3 V16 H5"; break;
                case "pin": path = "M9 3 H15 M10 3 V9 L7 12 V14 H17 V12 L14 9 V3 M12 14 V21"; break;
                case "unpin": path = "M9 3 H15 M14 3 V9 L17 12 V14 H11 M10 3 V6 M7 12 V14 H8 M12 16 V21 M3 3 L21 21"; break;
                case "edit": path = "M10 4 H6 Q3 4 3 7 V18 Q3 21 6 21 H17 Q20 21 20 18 V15 M9 15 L10 11 L18 3 Q19.5 1.5 21 3 Q22.5 4.5 21 6 L13 14 Z"; break;
                case "restore": path = "M3 4 H21 V8 H3 Z M5 8 V20 H19 V8 M12 17 V11 M9 14 L12 11 L15 14"; break;
                case "remove": path = "M8 12 H16 M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3"; break;
                case "back": path = "M20 12 H4 M10 6 L4 12 L10 18"; break;
                default: path = "M3 4 H21 V8 H3 Z M5 8 V20 H19 V8 M10 12 H14"; break;
            }
            var icon = new Path {
                Data = Geometry.Parse(path), Width = 15, Height = 15, Stretch = Stretch.Uniform,
                StrokeThickness = 1.4, StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            icon.SetResourceReference(Shape.StrokeProperty, danger ? "Failure" : "Ink");
            return icon;
        }

        public static FrameworkElement Label(string name, string text)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            var icon = Icon(name);
            icon.Width = icon.Height = 12;
            icon.Margin = new Thickness(0, 0, 5, 0);
            label.Children.Add(icon);
            label.Children.Add(new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            return label;
        }
    }
}
