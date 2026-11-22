using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ClaudeUI
{
    internal static class ThemeToggleIcon
    {
        public static void Update(Button button, bool dark, bool animate)
        {
            button.Content = Create(dark);
            button.ApplyTemplate();
            var knob = button.Template.FindName("Knob", button) as Border;
            var sun = button.Template.FindName("SunIcon", button) as Image;
            var moon = button.Template.FindName("MoonIcon", button) as Image;
            if (knob == null || sun == null || moon == null) return;
            if (sun.Source == null) sun.Source = Create(false);
            if (moon.Source == null) moon.Source = Create(true);
            if (knob.RenderTransform.IsFrozen) knob.RenderTransform = knob.RenderTransform.Clone();
            if (sun.RenderTransform.IsFrozen) sun.RenderTransform = sun.RenderTransform.Clone();
            if (moon.RenderTransform.IsFrozen) moon.RenderTransform = moon.RenderTransform.Clone();
            animate = animate && SystemParameters.ClientAreaAnimation;
            Move((TranslateTransform)knob.RenderTransform, TranslateTransform.XProperty, dark ? 0 : 31, animate);
            Move(sun, UIElement.OpacityProperty, dark ? 0 : 1, animate);
            Move(moon, UIElement.OpacityProperty, dark ? 1 : 0, animate);
            Move((RotateTransform)sun.RenderTransform, RotateTransform.AngleProperty, dark ? 90 : 0, animate);
            Move((RotateTransform)moon.RenderTransform, RotateTransform.AngleProperty, dark ? 0 : -90, animate);
        }

        private static void Move(IAnimatable target, DependencyProperty property, double value, bool animate)
        {
            var dependency = (DependencyObject)target;
            double current = (double)dependency.GetValue(property);
            target.BeginAnimation(property, null);
            dependency.SetValue(property, value);
            if (animate) target.BeginAnimation(property, new DoubleAnimation(current, value, Theme.TransitionDuration) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }, FillBehavior = FillBehavior.Stop
            });
        }

        public static ImageSource Create(bool dark)
        {
            string[] pixels = dark ? new[] {
                "00000000000000", "00000001000100", "00000110000000", "00001100000000",
                "00011100000000", "00111100000000", "00111100000000", "00111110000000",
                "00111111000000", "00011111110000", "00001111111000", "00000011110000",
                "00000000000000", "00000000000000"
            } : new[] {
                "00000010000000", "00000010000000", "00100000001000", "00010000010000",
                "00000111000000", "00001111100000", "11001111100110", "00001111100000",
                "00000111000000", "00010000010000", "00100000001000", "00000010000000",
                "00000010000000", "00000000000000"
            };
            var drawing = new DrawingGroup();
            using (var context = drawing.Open())
            {
                context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, 14, 14));
                var ink = dark ? Brushes.WhiteSmoke : new SolidColorBrush(Color.FromRgb(63, 53, 32));
                for (int y = 0; y < pixels.Length; y++)
                    for (int x = 0; x < pixels[y].Length; x++)
                        if (pixels[y][x] == '1') context.DrawRectangle(ink, null, new Rect(x, y, .82, .82));
            }
            var image = new DrawingImage(drawing);
            image.Freeze();
            return image;
        }
    }
}
