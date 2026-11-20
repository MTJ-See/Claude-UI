using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace ClaudeUI
{
    internal sealed class SidebarResizer
    {
        private const double DefaultWidth = 240;
        private readonly Grid layout;
        private readonly ColumnDefinition column;
        private readonly AppData data;
        private readonly Action save;
        private double preferred;

        public SidebarResizer(Window window, AppData data, Action save)
        {
            this.data = data;
            this.save = save;
            layout = (Grid)window.FindName("RootLayout");
            column = (ColumnDefinition)window.FindName("SidebarColumn");
            var splitter = (GridSplitter)window.FindName("SidebarSplitter");
            preferred = Double.IsNaN(data.SidebarWidth) || Double.IsInfinity(data.SidebarWidth) || data.SidebarWidth <= 0
                ? DefaultWidth : Math.Max(200, Math.Min(420, data.SidebarWidth));
            column.Width = new GridLength(preferred);
            layout.SizeChanged += delegate { ApplyAvailableWidth(window.ActualWidth > 0 ? Math.Min(window.ActualWidth, layout.ActualWidth) : layout.ActualWidth); };
            window.SizeChanged += delegate(object sender, SizeChangedEventArgs e) { ApplyAvailableWidth(e.NewSize.Width); };
            splitter.DragCompleted += delegate(object sender, DragCompletedEventArgs e) { if (!e.Canceled) Remember(); };
            splitter.KeyUp += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Left || e.Key == Key.Right) Remember(); };
        }

        internal void ApplyAvailableWidth(double availableWidth)
        {
            if (availableWidth <= 0) return;
            double maximum = Math.Max(200, Math.Min(420, availableWidth - 540));
            column.MaxWidth = maximum;
            column.Width = new GridLength(Math.Min(preferred, maximum));
        }

        private void Remember()
        {
            preferred = Math.Max(200, Math.Min(column.MaxWidth, column.ActualWidth));
            column.Width = new GridLength(preferred);
            data.SidebarWidth = preferred;
            save();
        }
    }
}
