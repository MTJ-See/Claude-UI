using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;

namespace ClaudeUI
{
    internal static class InstalledExtensionView
    {
        private static readonly ControlTemplate SwitchTemplate = (ControlTemplate)XamlReader.Parse(@"
<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ToggleButton}'>
  <Grid Width='40' Height='24' Background='Transparent'>
    <Border x:Name='Track' Height='22' CornerRadius='11' Background='#686B70' VerticalAlignment='Center'/>
    <Ellipse x:Name='Thumb' Width='18' Height='18' Fill='White' HorizontalAlignment='Left' Margin='3,0,0,0'/>
  </Grid>
  <ControlTemplate.Triggers>
    <Trigger Property='IsChecked' Value='True'>
      <Setter TargetName='Track' Property='Background' Value='#1BC5AB'/>
      <Setter TargetName='Thumb' Property='HorizontalAlignment' Value='Right'/>
      <Setter TargetName='Thumb' Property='Margin' Value='0,0,3,0'/>
    </Trigger>
    <Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Track' Property='BorderBrush' Value='{DynamicResource Ink}'/><Setter TargetName='Track' Property='BorderThickness' Value='1'/></Trigger>
    <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.45'/></Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>");
        internal static ToggleButton Switch(bool enabled)
        {
            return new ToggleButton { IsChecked=enabled, Template=SwitchTemplate, Width=40, Height=24, Cursor=System.Windows.Input.Cursors.Hand, VerticalAlignment=VerticalAlignment.Center };
        }
        internal static UIElement Card(InstalledExtension item,Func<bool,Task> change,Func<Task> details=null,Func<Task> remove=null)
        {
            var row=new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width=GridLength.Auto });
            var body=new StackPanel { Margin=new Thickness(0,0,24,0) };
            var name=MarketplaceDetailView.Label(item.Name,17); name.FontWeight=FontWeights.SemiBold; body.Children.Add(name);
            body.Children.Add(MarketplaceDetailView.Label(item.Scope+" · "+item.Description,12,"MutedInk"));
            body.Children.Add(MarketplaceDetailView.Label(item.Location,11,"MutedInk"));
            var state=MarketplaceDetailView.Label(item.CanToggle?(item.Enabled?"已开启":"已关闭"):item.Restriction,12,"MutedInk"); body.Children.Add(state);
            row.Children.Add(body);
            var toggle=Switch(item.Enabled); toggle.IsEnabled=item.CanToggle;
            toggle.ToolTip=item.CanToggle?"为当前项目开启或关闭；新建 Claude 会话后生效。":item.Restriction;
            System.Windows.Automation.AutomationProperties.SetName(toggle,"启用 "+item.Name);
            Grid.SetColumn(toggle,1); row.Children.Add(toggle);
            toggle.Click+=async delegate
            {
                bool requested=toggle.IsChecked==true;
                toggle.IsEnabled=false; state.Text="正在保存…";
                try { await change(requested); item.Enabled=requested; state.Text=requested?"已开启 · 新会话生效":"已关闭 · 新会话生效"; }
                catch(Exception ex)
                {
                    toggle.IsChecked=item.Enabled;
                    state.Text=ex is InvalidOperationException?ex.Message:"保存失败，请检查配置文件权限后重试。";
                }
                finally { toggle.IsEnabled=item.CanToggle; }
            };
            var actions=new WrapPanel { Margin=new Thickness(0,8,0,0) };
            foreach(var action in new[]{new {Label="查看详情",Run=details},new {Label="删除",Run=remove}})
            {
                if(action.Run==null) continue;
                var execute=action.Run;
                var button=new Button {Content=action.Label,Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,0,8,0)};
                button.SetResourceReference(FrameworkElement.StyleProperty,"Ghost");
                if(action.Label=="删除") { button.IsEnabled=item.CanRemove; button.ToolTip=item.DeleteRestriction; button.SetResourceReference(Control.ForegroundProperty,"Failure"); }
                button.Click+=async delegate
                {
                    actions.IsEnabled=false; toggle.IsEnabled=false;
                    try { await execute(); }
                    catch(Exception ex) { state.Text=ex is InvalidOperationException?ex.Message:"操作失败，请刷新或检查文件权限。"; }
                    finally { actions.IsEnabled=true; toggle.IsEnabled=item.CanToggle; }
                };
                actions.Children.Add(button);
            }
            body.Children.Add(actions);
            return MarketplaceDetailView.Card(row);
        }
    }
}
