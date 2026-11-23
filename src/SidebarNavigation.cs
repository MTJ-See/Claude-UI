using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUI
{
    internal sealed class SidebarNavigation
    {
        private static readonly Geometry ClosedFolder = IconGeometry("M 2,6 L 2,5 Q 2,3 4,3 L 8,3 Q 9,3 10,5 L 17,5 Q 19,5 19,7 L 19,15 Q 19,17 17,17 L 4,17 Q 2,17 2,15 Z");
        private static readonly Geometry OpenFolder = IconGeometry("M 3.4,15.5 L 2,5.5 Q 1.7,3 4,3 L 8,3 Q 9,3 10.3,5 L 16.5,5 Q 18.5,5 18.5,7.5 M 3.4,15.5 L 5.2,9 Q 5.5,8 7,8 L 19,8 Q 20.5,8 19.8,9.7 L 17.2,15.7 Q 16.7,17 15.2,17 L 5,17 Q 3.7,17 3.4,15.5 Z");
        private static readonly Geometry NewConversationIcon = IconGeometry("M 10,4 L 6,4 Q 3,4 3,7 L 3,18 Q 3,21 6,21 L 17,21 Q 20,21 20,18 L 20,15 M 9,15 L 10,11 L 18,3 Q 19.5,1.5 21,3 Q 22.5,4.5 21,6 L 13,14 Z");
        private readonly Window window;
        private readonly AppData data;
        private readonly Action<Conversation> open;
        private readonly Action<string> create;
        private readonly Action save;
        private readonly Func<string, ContextMenu> projectMenu;
        public bool ShowArchived { get; set; }
        public SidebarNavigation(Window window, AppData data, Action<Conversation> open, Action<string> create, Action save, Func<string, ContextMenu> projectMenu)
        {
            this.window = window; this.data = data; this.open = open; this.create = create; this.save = save;
            this.projectMenu = projectMenu;
            ((Button)window.FindName("ProjectsToggleButton")).Click += delegate { data.ProjectsCollapsed = !data.ProjectsCollapsed; UpdateSections(); save(); };
            ((Button)window.FindName("ConversationsToggleButton")).Click += delegate { data.ConversationsCollapsed = !data.ConversationsCollapsed; UpdateSections(); save(); };
            UpdateSections();
        }

        private static Geometry IconGeometry(string path)
        {
            var geometry = Geometry.Parse(path);
            geometry.Freeze();
            return geometry;
        }

        private void UpdateSections()
        {
            ((FrameworkElement)window.FindName("ProjectsBody")).Visibility = data.ProjectsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ((FrameworkElement)window.FindName("ConversationsBody")).Visibility = data.ConversationsCollapsed ? Visibility.Collapsed : Visibility.Visible;
            ((TextBlock)window.FindName("ProjectsChevron")).Text = data.ProjectsCollapsed ? "\uE76C" : "\uE70D";
            ((TextBlock)window.FindName("ConversationsChevron")).Text = data.ConversationsCollapsed ? "\uE76C" : "\uE70D";
        }

        public void Render(Conversation current)
        {
            var panel = (StackPanel)window.FindName("ProjectList");
            panel.Children.Clear();
            ((Button)window.FindName("ArchivedProjectsButton")).Content = ProjectActionVisuals.Label(ShowArchived ? "back" : "archive", ShowArchived ? "返回项目" : "已归档");
            var projects = data.ProjectFolders.Concat(data.Conversations.Select(item => item.Workspace))
                .Select(CliConversationCatalog.Normalize).Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => { var state = State(path); return (state == null || !state.IsRemoved) && (state != null && state.IsArchived) == ShowArchived; })
                .OrderByDescending(path => State(path) != null && State(path).IsPinned)
                .ThenBy(path => State(path) != null && !String.IsNullOrWhiteSpace(State(path).Name) ? State(path).Name : System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)), StringComparer.CurrentCultureIgnoreCase);
            foreach (string folder in projects)
            {
                var sessions = data.Conversations.Where(item => String.Equals(CliConversationCatalog.Normalize(item.Workspace), folder, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(item => item.UpdatedUtc ?? "").ToList();
                string name = System.IO.Path.GetFileName(folder.TrimEnd(System.IO.Path.DirectorySeparatorChar));
                if (String.IsNullOrEmpty(name)) name = folder;
                var projectState = State(folder);
                if (projectState != null && !String.IsNullOrWhiteSpace(projectState.Name)) name = projectState.Name;
                var header = new DockPanel();
                var icon = new System.Windows.Shapes.Path {
                    Width = 13, Height = 12, Stretch = Stretch.Uniform, StrokeThickness = 1.2,
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                    Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = .85
                };
                icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
                header.Children.Add(icon);
                string countText = sessions.Count.ToString("D2");
                var badgeDrawing = new Canvas { Width = 58, Height = 34 };
                var circle = new System.Windows.Shapes.Ellipse {
                    Width = 28, Height = 28, Fill = new SolidColorBrush(Color.FromRgb(40, 233, 83))
                };
                Canvas.SetLeft(circle, 28);
                Canvas.SetTop(circle, 3);
                badgeDrawing.Children.Add(circle);
                var orbit = new System.Windows.Shapes.Path {
                    Data = IconGeometry("M 19,10 A 16,16 0 1 1 19,24"), StrokeThickness = 1.2,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                };
                orbit.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
                badgeDrawing.Children.Add(orbit);
                var point = new TextBlock {
                    Text = "POINT", FontFamily = new FontFamily("Segoe UI"), FontSize = 8,
                    FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
                point.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
                var pointLabel = new Border { Child = point, Width = 26, Height = 14 };
                Canvas.SetLeft(pointLabel, 0);
                Canvas.SetTop(pointLabel, 10);
                badgeDrawing.Children.Add(pointLabel);
                var count = new TextBlock {
                    Text = countText, FontFamily = new FontFamily("Segoe UI"), FontSize = countText.Length <= 2 ? 14 : countText.Length == 3 ? 12 : 10,
                    FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(Color.FromRgb(25, 38, 28)),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                };
                var number = new Border { Child = count, Width = 28, Height = 28 };
                Canvas.SetLeft(number, 28);
                Canvas.SetTop(number, 3);
                badgeDrawing.Children.Add(number);
                var countBadge = new Viewbox {
                    Child = badgeDrawing, Width = 36, Height = 22, Stretch = Stretch.Uniform,
                    Margin = new Thickness(5, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = sessions.Count + " 条会话"
                };
                System.Windows.Automation.AutomationProperties.SetName(countBadge, sessions.Count + " 条会话");
                DockPanel.SetDock(countBadge, Dock.Right);
                header.Children.Add(countBadge);
                header.Children.Add(new TextBlock { Text = name, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
                var group = new Expander { Header = header, ToolTip = folder, Style = (Style)window.Resources["ProjectGroup"],
                    Tag = folder,
                    IsExpanded = !data.CollapsedProjects.Contains(folder, StringComparer.OrdinalIgnoreCase) };
                icon.Data = group.IsExpanded ? OpenFolder : ClosedFolder;
                if (sessions.Count > 0)
                {
                    var list = new ListBox { ItemsSource = sessions, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                        Template = ((ListBox)window.FindName("HistoryList")).Template,
                        ItemContainerStyle = ((ListBox)window.FindName("HistoryList")).ItemContainerStyle, ItemTemplate = ((ListBox)window.FindName("HistoryList")).ItemTemplate,
                        Margin = new Thickness(13, 0, 0, 5), MaxHeight = 190 };
                    ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
                    list.SelectedItem = sessions.Contains(current) ? current : null;
                    list.SelectionChanged += delegate { var selected = list.SelectedItem as Conversation; if (selected != null && selected != current) open(selected); };
                    group.Content = list;
                }
                else
                {
                    var empty = new TextBlock { Text = "暂无对话，点击右侧新建", Margin = new Thickness(25, 3, 0, 10) };
                    empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
                    group.Content = empty;
                }
                group.Expanded += delegate { icon.Data = OpenFolder; data.CollapsedProjects.RemoveAll(item => String.Equals(item, folder, StringComparison.OrdinalIgnoreCase)); save(); };
                group.Collapsed += delegate { icon.Data = ClosedFolder; if (!data.CollapsedProjects.Contains(folder, StringComparer.OrdinalIgnoreCase)) data.CollapsedProjects.Add(folder); save(); };
                var row = new Grid();
                row.Children.Add(group);
                var compose = new System.Windows.Shapes.Path {
                    Data = NewConversationIcon, Width = 12, Height = 12, Stretch = Stretch.Uniform,
                    StrokeThickness = 1.2, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = .85
                };
                compose.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "Ink");
                var add = new Button { Style = (Style)window.Resources["Ghost"], Content = compose, Width = 22, Height = 28, MinHeight = 24,
                    Margin = new Thickness(0, 1, 1, 0),
                    Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                    ToolTip = "在此项目中新建对话", IsEnabled = System.IO.Directory.Exists(folder) };
                System.Windows.Automation.AutomationProperties.SetName(add, "在此项目中新建对话");
                add.Click += delegate { create(folder); };
                row.Children.Add(add);
                var more = new Button { Style = (Style)window.Resources["Ghost"], Content = "⋯", FontSize = 13,
                    Width = 22, Height = 28, MinHeight = 24, Padding = new Thickness(0), Margin = new Thickness(0, 1, 24, 0),
                    HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, ToolTip = "项目操作" };
                System.Windows.Automation.AutomationProperties.SetName(more, "项目操作");
                more.Click += delegate(object sender, RoutedEventArgs e) {
                    e.Handled = true;
                    var menu = projectMenu(folder);
                    menu.PlacementTarget = more;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                    menu.IsOpen = true;
                };
                header.MouseRightButtonUp += delegate(object sender, System.Windows.Input.MouseButtonEventArgs e) {
                    e.Handled = true;
                    var menu = projectMenu(folder);
                    menu.PlacementTarget = header;
                    menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
                    menu.IsOpen = true;
                };
                row.Children.Add(more);
                panel.Children.Add(row);
            }
            ((TextBlock)window.FindName("NoProjects")).Visibility = panel.Children.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ((TextBlock)window.FindName("NoProjects")).Text = ShowArchived ? "暂无已归档项目" : "添加文件夹，开始整理项目";
        }

        private ProjectNavigationState State(string folder)
        {
            return data.ProjectStates.FirstOrDefault(item => String.Equals(CliConversationCatalog.Normalize(item.Folder), folder, StringComparison.OrdinalIgnoreCase));
        }
    }
}
