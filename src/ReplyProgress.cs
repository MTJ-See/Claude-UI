using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClaudeUI
{
    public sealed class RunStage
    {
        public string Name { get; set; }
        public double Seconds { get; set; }
    }

    public sealed class ChatRunSummary
    {
        public string Status { get; set; }
        public string Effort { get; set; }
        public string Model { get; set; }
        public double Seconds { get; set; }
        public string CompletedUtc { get; set; }
        public List<RunStage> Stages { get; set; }
        public List<string> RequestIds { get; set; }
    }

    internal sealed class ReplyProgressView : Border
    {
        private readonly TextBlock status = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock detail = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(30, 5, 0, 0) };
        private readonly StackPanel stages = new StackPanel { Margin = new Thickness(4, 8, 0, 0) };
        private readonly TextBox requestIds = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(30, 5, 0, 0), FontSize = 12, Visibility = Visibility.Collapsed };
        private readonly Expander history;
        private readonly RotateTransform rotation = new RotateTransform();
        private ChatRunSummary summary;
        private bool running;
        private string currentStage;
        private string lastStatus, lastDetail;

        public ReplyProgressView(ImageSource mark)
        {
            Visibility = Visibility.Collapsed;
            Margin = new Thickness(0, 14, 0, 0);
            Padding = new Thickness(12, 10, 12, 10);
            CornerRadius = new CornerRadius(6);
            SetResourceReference(BackgroundProperty, "Sidebar");
            var panel = new StackPanel();
            var heading = new DockPanel();
            var icon = new Image { Source = mark, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0),
                RenderTransform = rotation, RenderTransformOrigin = new Point(.5, .5), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(icon, Dock.Left);
            heading.Children.Add(icon);
            status.SetResourceReference(TextBlock.ForegroundProperty, "AssistantInk");
            heading.Children.Add(status);
            panel.Children.Add(heading);
            detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            panel.Children.Add(detail);
            requestIds.SetResourceReference(StyleProperty, "SelectableMetadata");
            System.Windows.Automation.AutomationProperties.SetName(requestIds, "本轮请求 ID，可选择复制");
            panel.Children.Add(requestIds);
            history = new Expander { Header = "执行过程", Content = stages, FontSize = 12, Margin = new Thickness(27, 8, 0, 0) };
            history.SetResourceReference(Control.ForegroundProperty, "MutedInk");
            panel.Children.Add(history);
            Child = panel;
            Loaded += delegate { Animate(); };
            Unloaded += delegate { rotation.BeginAnimation(RotateTransform.AngleProperty, null); };
            System.Windows.Automation.AutomationProperties.SetName(this, "本轮调用过程与耗时");
        }

        public ChatRunSummary Summary { get { return summary; } }

        public void Start(string effort)
        {
            summary = new ChatRunSummary { Effort = effort, Stages = new List<RunStage>() };
            ShowRequestIds();
            stages.Children.Clear();
            currentStage = null;
            running = true;
            Visibility = Visibility.Visible;
            Observe("正在启动 Claude CLI", 0);
            Update(0, null);
            Animate();
        }

        public void Observe(string stage, double seconds)
        {
            if (!running || String.IsNullOrWhiteSpace(stage) || stage == currentStage) return;
            currentStage = stage;
            var step = new RunStage { Name = stage, Seconds = Math.Max(0, seconds) };
            summary.Stages.Add(step);
            AddStage(step);
            // Keep large agent/tool loops bounded while retaining the start and most recent stages.
            if (summary.Stages.Count > 80) { summary.Stages.RemoveAt(1); stages.Children.RemoveAt(1); }
        }

        public void Update(double seconds, string model)
        {
            if (!running) return;
            summary.Seconds = Math.Max(0, seconds);
            if (!String.IsNullOrWhiteSpace(model)) summary.Model = model;
            SetText(currentStage + "… · 已用时 " + Math.Floor(summary.Seconds).ToString("0", CultureInfo.CurrentCulture) + " 秒",
                Describe());
        }

        public ChatRunSummary Complete(string state, double seconds, DateTime completedUtc)
        {
            if (summary == null) return null;
            Observe(state, seconds);
            summary.Status = state;
            summary.Seconds = Math.Max(0, seconds);
            summary.CompletedUtc = completedUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
            running = false;
            ShowCompleted();
            Animate();
            return summary;
        }

        public void SetRequestIds(IEnumerable<string> ids)
        {
            if (summary == null) return;
            var values = ids.Where(id => !String.IsNullOrWhiteSpace(id)).Distinct().ToList();
            if (summary.RequestIds != null && summary.RequestIds.SequenceEqual(values)) return;
            summary.RequestIds = values;
            ShowRequestIds();
        }

        private void ShowRequestIds()
        {
            string text = summary == null || summary.RequestIds == null ? "" :
                String.Join(Environment.NewLine, summary.RequestIds.Select(id => "请求 ID：" + id));
            if (requestIds.Text != text) requestIds.Text = text;
            requestIds.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        public void Restore(ChatRunSummary saved)
        {
            if (saved == null) return;
            summary = saved;
            ShowRequestIds();
            running = false;
            Visibility = Visibility.Visible;
            stages.Children.Clear();
            if (saved.Stages != null)
                foreach (var step in saved.Stages) AddStage(step);
            ShowCompleted();
            Animate();
        }

        private void ShowCompleted()
        {
            DateTime completed;
            string time = DateTime.TryParse(summary.CompletedUtc, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out completed)
                ? " · 完成于 " + completed.ToLocalTime().ToString("HH:mm") : "";
            string state = String.IsNullOrEmpty(summary.Status) ? "已完成" : summary.Status;
            string verb = state == "已完成" ? "处理完成" : state;
            status.SetResourceReference(TextBlock.ForegroundProperty, state == "执行失败" ? "Failure" : "MutedInk");
            SetText(verb + " · 用时 " + summary.Seconds.ToString("0.0", CultureInfo.CurrentCulture) + " 秒" + time, Describe());
        }

        private string Describe()
        {
            string effort;
            switch (summary.Effort)
            {
                case "low": effort = "低"; break;
                case "medium": effort = "中"; break;
                case "high": effort = "高"; break;
                case "xhigh": effort = "很高"; break;
                case "max": effort = "最高"; break;
                default: effort = "沿用 CLI"; break;
            }
            return "推理力度：" + effort + (String.IsNullOrWhiteSpace(summary.Model) ? "" : " · " + summary.Model);
        }

        private void SetText(string state, string metadata)
        {
            if (state != lastStatus) { status.Text = state; lastStatus = state; }
            if (metadata != lastDetail) { detail.Text = metadata; lastDetail = metadata; }
        }

        private void AddStage(RunStage stage)
        {
            var text = new TextBlock { Text = stage.Seconds.ToString("0.0", CultureInfo.CurrentCulture) + "s  ·  " + stage.Name,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), FontSize = 12 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            stages.Children.Add(text);
        }

        private void Animate()
        {
            if (running && IsLoaded && SystemParameters.ClientAreaAnimation)
                rotation.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(3)) { RepeatBehavior = RepeatBehavior.Forever });
            else rotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }
    }
}
