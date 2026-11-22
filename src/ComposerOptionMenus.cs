using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace ClaudeUI
{
    internal sealed class ComposerChoice
    {
        internal string Value, Title, Description, Icon;
        internal bool Caution;
        internal ComposerChoice(string value, string title, string description, string icon, bool caution = false)
        { Value = value; Title = title; Description = description; Icon = icon; Caution = caution; }
    }

    // 模式值直接传给 CLI；菜单说明不改变 CLI 的权限规则或模型能力。
    internal sealed class ComposerOptionMenus
    {
        internal static readonly ComposerChoice[] Permissions = {
            new ComposerChoice("default", "请求批准", "文件编辑、命令及网络操作按 CLI 规则请求审批；已允许的操作直接执行。", "shield-check"),
            new ComposerChoice("acceptEdits", "自动接受编辑", "自动批准文件编辑和常见文件系统操作；其他操作仍按规则审批。", "folder-open"),
            new ComposerChoice("plan", "只读规划", "读取与分析项目，不修改源码；用于先形成计划再执行。", "list-todo"),
            new ComposerChoice("auto", "帮我批准", "由后台分类模型审查操作，减少手动审批；需要 CLI 支持的模型及策略允许。", "sparkles"),
            new ComposerChoice("dontAsk", "仅预授权", "只执行无需审批或已预先允许的操作；其余需要审批的调用直接拒绝。", "shield-check"),
            new ComposerChoice("bypassPermissions", "完全访问权限", "跳过通常的工具权限审批；CLI 的少数强制限制仍然生效。", "badge-info", true)
        };
        internal static readonly ComposerChoice[] Efforts = {
            new ComposerChoice("", "默认", "沿用 CLI 与所选模型的默认推理强度。", "activity"),
            new ComposerChoice("low", "低", "更快响应，适合简单问题与小范围修改。", "activity"),
            new ComposerChoice("medium", "中", "兼顾速度与推理深度，适合日常开发。", "activity"),
            new ComposerChoice("high", "高", "更深入地检查与推理，适合复杂调试和边界问题。", "activity"),
            new ComposerChoice("xhigh", "很高", "投入更多推理，适合高复杂度任务；需要模型支持。", "activity"),
            new ComposerChoice("max", "最高", "使用最高推理强度，可能增加耗时与用量；实际能力由模型决定。", "activity")
        };
        private readonly Window window;
        private readonly Func<string> permission, effort;
        private readonly Action<string> choosePermission, chooseEffort;
        private readonly Func<bool> enabled;
        internal ContextMenu PermissionMenu { get; private set; }
        internal ContextMenu EffortMenu { get; private set; }

        internal ComposerOptionMenus(Window window, Func<string> permission, Action<string> choosePermission,
            Func<string> effort, Action<string> chooseEffort, Func<bool> enabled)
        {
            this.window = window; this.permission = permission; this.choosePermission = choosePermission;
            this.effort = effort; this.chooseEffort = chooseEffort; this.enabled = enabled;
        }
        internal static ComposerChoice Permission(string value)
        { return Permissions.FirstOrDefault(item => item.Value == (value == "manual" ? "default" : value)) ?? Permissions[0]; }
        internal static ComposerChoice Effort(string value)
        { return Efforts.FirstOrDefault(item => item.Value == (value ?? "")) ?? Efforts[0]; }

        internal void ShowPermissions(Button anchor)
        {
            if (!enabled()) return;
            Close();
            PermissionMenu = Build(Permissions, Permission(permission()).Value, choosePermission);
            Open(PermissionMenu, anchor);
        }
        internal void ShowEfforts(Button anchor)
        {
            if (!enabled()) return;
            Close();
            EffortMenu = Build(Efforts, Effort(effort()).Value, chooseEffort);
            Open(EffortMenu, anchor);
        }
        private void Open(ContextMenu menu, Button anchor)
        {
            Close(); anchor.ContextMenu = menu; menu.PlacementTarget = anchor; menu.Placement = PlacementMode.Top;
            // 不可见的窗口只构建菜单，避免测试和启动时弹出独立窗口。
            if (window.IsVisible) menu.IsOpen = true;
        }
        internal void Close()
        {
            if (PermissionMenu != null) PermissionMenu.IsOpen = false;
            if (EffortMenu != null) EffortMenu.IsOpen = false;
        }
        internal ContextMenu Build(ComposerChoice[] choices, string selected, Action<string> choose)
        {
            double width = Math.Min(490, Math.Max(320, (window.ActualWidth > 0 ? window.ActualWidth : window.Width) - 60));
            var menu = new ContextMenu { Width = width, Padding = new Thickness(6), HasDropShadow = false,
                FontFamily = window.FontFamily, FontSize = 14, MaxHeight = Math.Max(220, SystemParameters.WorkArea.Height - 100) };
            menu.Resources.MergedDictionaries.Add(window.Resources);
            var shell = new FrameworkElementFactory(typeof(Border)); shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(18));
            shell.SetResourceReference(Border.BackgroundProperty, "Composer"); shell.SetResourceReference(Border.BorderBrushProperty, "ComposerLine");
            shell.SetValue(Border.BorderThicknessProperty, new Thickness(1)); shell.SetValue(Border.PaddingProperty, new Thickness(6));
            var scroll = new FrameworkElementFactory(typeof(ScrollViewer)); scroll.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto); scroll.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled);
            scroll.AppendChild(new FrameworkElementFactory(typeof(ItemsPresenter))); shell.AppendChild(scroll);
            menu.Template = new ControlTemplate(typeof(ContextMenu)) { VisualTree = shell };
            menu.Resources[typeof(ScrollBar)] = window.Resources["QuietScrollBar"];
            foreach (var choice in choices)
            {
                var item = new MenuItem { Tag = choice.Value, Header = Header(choice, choice.Value == selected), Padding = new Thickness(12, 10, 12, 10), Cursor = System.Windows.Input.Cursors.Hand };
                var surface = new FrameworkElementFactory(typeof(Border)); surface.Name = "Surface";
                surface.SetValue(Border.CornerRadiusProperty, new CornerRadius(12)); surface.SetValue(Border.PaddingProperty, new Thickness(12, 10, 12, 10));
                surface.SetResourceReference(Border.BackgroundProperty, choice.Value == selected ? "ComposerHover" : "Composer");
                var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header"); surface.AppendChild(presenter);
                var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = surface };
                var hover = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
                hover.Setters.Add(new Setter(Border.BackgroundProperty, window.Resources["ComposerHover"], "Surface")); template.Triggers.Add(hover);
                var focus = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
                focus.Setters.Add(new Setter(Border.BorderBrushProperty, window.Resources["Accent"], "Surface")); focus.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1), "Surface")); template.Triggers.Add(focus);
                item.Template = template;
                System.Windows.Automation.AutomationProperties.SetName(item, choice.Title + "，" + choice.Description + (choice.Value == selected ? "，已选择" : ""));
                item.Click += delegate { choose(choice.Value); menu.IsOpen = false; };
                menu.Items.Add(item);
            }
            return menu;
        }
        private static Grid Header(ComposerChoice choice, bool selected)
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            string ink = choice.Caution ? "PermissionWarning" : "Ink";
            var icon = DeveloperVisuals.Icon(choice.Icon, 22, ink); icon.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(icon);
            var body = new StackPanel { Margin = new Thickness(9, 0, 10, 0) }; Grid.SetColumn(body, 1); row.Children.Add(body);
            var title = new TextBlock { Text = choice.Title, FontSize = 15, FontWeight = FontWeights.SemiBold }; title.SetResourceReference(TextBlock.ForegroundProperty, ink); body.Children.Add(title);
            var description = new TextBlock { Text = choice.Description, FontSize = 12, LineHeight = 18, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
            description.SetResourceReference(TextBlock.ForegroundProperty, choice.Caution ? "PermissionWarning" : "MutedInk"); body.Children.Add(description);
            var check = new Path { Data = Geometry.Parse("M 2,8 L 7,13 L 17,2"), Width = 17, Height = 17, Stretch = Stretch.Uniform,
                StrokeThickness = 1.7, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Visibility = selected ? Visibility.Visible : Visibility.Hidden, VerticalAlignment = VerticalAlignment.Center };
            check.SetResourceReference(Shape.StrokeProperty, "Ink"); Grid.SetColumn(check, 2); row.Children.Add(check);
            return row;
        }
    }
}
