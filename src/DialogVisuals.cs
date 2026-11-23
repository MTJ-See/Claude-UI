using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media.Animation;
using System.Windows.Shell;

namespace ClaudeUI
{
    // 应用内弹窗统一使用中性深灰外框、紫色主操作和独立标题栏。
    internal static class DialogVisuals
    {
        internal static void Apply(Window window)
        {
            // 单独的资源层避免影响主窗口，同时去除输入框模板重复的内边距。
            var resources = new ResourceDictionary();
            resources.MergedDictionaries.Add(window.Resources);
            window.Resources = resources;
            var input = new Style(typeof(TextBox), (Style)window.TryFindResource(typeof(TextBox)));
            input.Setters.Add(new Setter(Control.TemplateProperty, (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='TextBox'>
 <Border x:Name='Input' CornerRadius='8' Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='1'><ScrollViewer x:Name='PART_ContentHost'/></Border>
 <ControlTemplate.Triggers>
  <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Input' Property='BorderBrush' Value='{DynamicResource CardHoverLine}'/></Trigger>
  <Trigger Property='IsKeyboardFocusWithin' Value='True'><Setter TargetName='Input' Property='BorderBrush' Value='{DynamicResource Accent}'/></Trigger>
  <Trigger Property='IsEnabled' Value='False'><Setter TargetName='Input' Property='Opacity' Value='.45'/></Trigger>
 </ControlTemplate.Triggers>
</ControlTemplate>")));
            resources[typeof(TextBox)] = input;
            window.WindowStyle = WindowStyle.None;
            window.ShowInTaskbar = false;
            window.SetResourceReference(Control.BackgroundProperty, "Canvas");
            window.SetResourceReference(Control.ForegroundProperty, "Ink");
            WindowChrome.SetWindowChrome(window, new WindowChrome {
                CaptionHeight = 54, GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(14),
                ResizeBorderThickness = new Thickness(window.ResizeMode == ResizeMode.NoResize ? 0 : 6)
            });
            window.Template = (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
 xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' xmlns:shell='clr-namespace:System.Windows.Shell;assembly=PresentationFramework' TargetType='Window'>
 <Border Background='{DynamicResource Canvas}' BorderBrush='{DynamicResource Line}' BorderThickness='1' CornerRadius='14' ClipToBounds='True'>
  <DockPanel LastChildFill='True'>
   <Border DockPanel.Dock='Top' Background='{DynamicResource Panel}' BorderBrush='{DynamicResource Line}' BorderThickness='0,0,0,1' CornerRadius='13,13,0,0' Height='54' Padding='20,0,12,0'>
    <DockPanel LastChildFill='True'>
     <Button x:Name='DialogClose' DockPanel.Dock='Right' Content='&#x2715;' Width='32' Height='32' Padding='0' VerticalAlignment='Center' Style='{DynamicResource Ghost}' ToolTip='关闭' AutomationProperties.Name='关闭弹窗' shell:WindowChrome.IsHitTestVisibleInChrome='True'/>
     <TextBlock Text='{TemplateBinding Title}' Foreground='{DynamicResource Ink}' FontSize='15' FontWeight='SemiBold' VerticalAlignment='Center' TextTrimming='CharacterEllipsis' Margin='0,0,12,0'/>
    </DockPanel>
   </Border>
   <AdornerDecorator><ContentPresenter ContentSource='Content'/></AdornerDecorator>
  </DockPanel>
 </Border>
</ControlTemplate>");
            window.Loaded += delegate {
                var close = window.Template.FindName("DialogClose", window) as Button;
                if (close != null) close.Click += delegate { window.Close(); };
                window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
            };
        }

        // 确认弹窗沿用原有按钮、默认焦点与返回值，关闭窗口不会触发肯定操作。
        internal static Window CreateMessage(Window owner, string message, string title, MessageBoxButton buttons,
            MessageBoxImage image, MessageBoxResult defaultResult, Action<MessageBoxResult> completed)
        {
            var view = new Window { Owner = owner, Title = title, Width = 500, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                FontFamily = owner.FontFamily, FontSize = owner.FontSize };
            view.Resources.MergedDictionaries.Add(owner.Resources);
            Apply(view);
            var body = new StackPanel { Margin = new Thickness(24) };
            var label = new TextBlock { Text = image == MessageBoxImage.Error ? "操作未完成" : image == MessageBoxImage.Warning ? "请确认此操作" : "提示",
                FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) };
            label.SetResourceReference(TextBlock.ForegroundProperty, image == MessageBoxImage.Error ? "Failure" : "Ink");
            body.Children.Add(label);
            var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
            text.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk");
            body.Children.Add(new ScrollViewer { Content = text, MaxHeight = Math.Max(160, SystemParameters.WorkArea.Height - 250),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            body.Children.Add(footer);
            bool yesNo = buttons == MessageBoxButton.YesNo || buttons == MessageBoxButton.YesNoCancel;
            var results = buttons == MessageBoxButton.OK ? new[] { MessageBoxResult.OK } :
                buttons == MessageBoxButton.YesNo ? new[] { MessageBoxResult.No, MessageBoxResult.Yes } :
                buttons == MessageBoxButton.YesNoCancel ? new[] { MessageBoxResult.Cancel, MessageBoxResult.No, MessageBoxResult.Yes } :
                new[] { MessageBoxResult.Cancel, MessageBoxResult.OK };
            var safe = yesNo && buttons == MessageBoxButton.YesNo ? MessageBoxResult.No : buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
            MessageBoxResult chosen = safe;
            Button focus = null;
            foreach (var result in results)
            {
                var value = result;
                var button = new Button { Content = result == MessageBoxResult.Yes ? "确认" : result == MessageBoxResult.No ? (buttons == MessageBoxButton.YesNoCancel ? "否" : "取消") : result == MessageBoxResult.Cancel ? "取消" : "确定",
                    MinWidth = 82, Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(8, 0, 0, 0), IsDefault = result == defaultResult, IsCancel = result == safe };
                button.SetResourceReference(FrameworkElement.StyleProperty, result == MessageBoxResult.Yes || result == MessageBoxResult.OK ? "Primary" : "Ghost");
                button.Click += delegate { chosen = value; view.Close(); };
                if (result == defaultResult || (focus == null && result == safe)) focus = button;
                footer.Children.Add(button);
            }
            view.Content = body;
            view.Loaded += delegate { if (focus != null) focus.Focus(); };
            view.Closed += delegate { completed(chosen); };
            return view;
        }

        internal static MessageBoxResult Show(Window owner, string message, string title, MessageBoxButton buttons,
            MessageBoxImage image = MessageBoxImage.None, MessageBoxResult defaultResult = MessageBoxResult.None)
        {
            MessageBoxResult result = MessageBoxResult.None;
            CreateMessage(owner, message, title, buttons, image, defaultResult, value => result = value).ShowDialog();
            return result;
        }
    }
}
