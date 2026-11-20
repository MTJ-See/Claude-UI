using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace ClaudeUI
{
    internal sealed class RequestDetailResizer
    {
        private readonly Grid page;
        private readonly FrameworkElement frame;
        private readonly ScrollViewer content;
        private readonly Expander expander;
        private readonly AppData data;
        private readonly Action save;
        private double preferredWidth, preferredHeight, originalWidth, originalHeight;
        private bool queued;

        public RequestDetailResizer(Window window, AppData data, Action save)
        {
            this.data = data; this.save = save;
            page = (Grid)window.FindName("StatsPage");
            frame = (FrameworkElement)window.FindName("StatsDetailFrame");
            content = (ScrollViewer)window.FindName("StatsDetailScroll");
            expander = (Expander)window.FindName("StatsDetailsExpander");
            preferredWidth = Valid(data.RequestDetailWidth) ? Math.Max(300, data.RequestDetailWidth) : 0;
            preferredHeight = Valid(data.RequestDetailHeight) ? Math.Max(64, Math.Min(420, data.RequestDetailHeight)) : 220;
            Connect((Thumb)window.FindName("StatsDetailWidthGrip"), true);
            Connect((Thumb)window.FindName("StatsDetailHeightGrip"), false);
            page.SizeChanged += delegate { QueueLayout(); };
            expander.Expanded += delegate { QueueLayout(); };
            frame.Loaded += delegate { ApplyBounds(); };
        }

        private void Connect(Thumb grip, bool horizontal)
        {
            grip.DragStarted += delegate { originalWidth = preferredWidth; originalHeight = preferredHeight; };
            grip.DragDelta += delegate(object sender, DragDeltaEventArgs e) { ResizeBy(horizontal ? e.HorizontalChange : 0, horizontal ? 0 : -e.VerticalChange); };
            grip.DragCompleted += delegate(object sender, DragCompletedEventArgs e)
            {
                if (e.Canceled) { preferredWidth = originalWidth; preferredHeight = originalHeight; ApplyBounds(); }
                else { data.RequestDetailWidth = preferredWidth; data.RequestDetailHeight = preferredHeight; save(); }
            };
        }

        private void QueueLayout()
        {
            if (queued) return;
            queued = true;
            page.Dispatcher.BeginInvoke(new Action(() => { queued = false; ApplyBounds(); }), DispatcherPriority.Loaded);
        }

        internal void ResizeBy(double widthChange, double heightChange)
        {
            if (widthChange != 0) preferredWidth = Math.Max(Math.Min(300, AvailableWidth), Math.Min(AvailableWidth, frame.ActualWidth + widthChange));
            if (heightChange != 0) preferredHeight = Math.Max(64, Math.Min(MaximumHeight, content.ActualHeight + heightChange));
            ApplyBounds();
        }

        private double AvailableWidth { get { return Math.Max(1, page.ActualWidth - 4); } }
        private double MaximumHeight
        {
            get
            {
                var footer = page.Children[3] as Grid;
                double actions = footer == null || footer.Children.Count == 0 ? 64 : ((FrameworkElement)footer.Children[0]).DesiredSize.Height;
                // Reserve space for the main request list even when the details are enlarged.
                double available = page.ActualHeight - page.RowDefinitions[0].ActualHeight - page.RowDefinitions[1].ActualHeight - actions - 44 - 140;
                return Math.Max(64, Math.Min(420, available));
            }
        }

        internal void ApplyBounds()
        {
            if (!expander.IsExpanded || page.ActualWidth <= 0) return;
            frame.MinWidth = Math.Min(300, AvailableWidth);
            frame.MaxWidth = AvailableWidth;
            frame.Width = preferredWidth > 0 ? Math.Min(preferredWidth, AvailableWidth) : AvailableWidth;
            content.Height = Math.Min(preferredHeight, MaximumHeight);
        }

        private static bool Valid(double value) { return value > 0 && !Double.IsNaN(value) && !Double.IsInfinity(value); }
    }
}
