using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace ClaudeUI
{
    internal sealed class InstalledExtensionDetailsDialog
    {
        private readonly Window window;
        internal InstalledExtensionDetailsDialog(Window owner,MarketplaceItem detail,bool canRemove,string restriction,Func<Window,Task<bool>> remove)
        {
            window=new Window { Owner=owner,Title="已安装详情 · "+detail.Name,Width=860,Height=Math.Min(850,SystemParameters.WorkArea.Height-40),MinWidth=600,MinHeight=420,Resources=owner.Resources,WindowStartupLocation=WindowStartupLocation.CenterOwner };
            window.SetResourceReference(Window.BackgroundProperty,"Canvas"); window.SetResourceReference(Window.ForegroundProperty,"Ink");
            DialogVisuals.Apply(window);
            var grid=new DockPanel {Margin=new Thickness(24)}; window.Content=grid;
            var footer=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right}; DockPanel.SetDock(footer,Dock.Bottom); grid.Children.Add(footer);
            var close=new Button {Content="关闭",Margin=new Thickness(0,10,0,0),Padding=new Thickness(16,7,16,7)}; close.SetResourceReference(FrameworkElement.StyleProperty,"Ghost"); footer.Children.Add(close); close.Click+=delegate{window.Close();};
            var delete=new Button {Content="删除",Padding=new Thickness(16,7,16,7),IsEnabled=canRemove,ToolTip=restriction}; delete.SetResourceReference(FrameworkElement.StyleProperty,"Ghost"); delete.SetResourceReference(Control.ForegroundProperty,"Failure");
            bool removing=false;
            delete.Click+=async delegate
            {
                delete.IsEnabled=false; close.IsEnabled=false; removing=true;
                try { if(await remove(window)) { removing=false; window.DialogResult=true; } }
                catch(Exception ex) { DialogVisuals.Show(window,ex is InvalidOperationException?ex.Message:"删除未完成，请刷新检查条目状态与文件权限。","删除失败",MessageBoxButton.OK,MessageBoxImage.Error); }
                finally { removing=false; delete.IsEnabled=canRemove; close.IsEnabled=true; }
            };
            window.Closing+=(s,e)=>{if(removing) e.Cancel=true;};
            Button source=null; Uri url;
            if(MarketplaceView.SafeLink(detail.SourceUrl,out url))
            {
                source=new Button {Content="源码仓库 ↗",Padding=new Thickness(12,7,12,7)}; source.SetResourceReference(FrameworkElement.StyleProperty,"Ghost");
                source.Click+=delegate{Process.Start(new ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});};
            }
            var scroll=new ScrollViewer {Content=MarketplaceDetailView.Create(detail,delete,source),VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};
            scroll.Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar),owner.FindResource("QuietScrollBar")); grid.Children.Add(scroll);
        }
        public bool Show() {return window.ShowDialog()==true;}
    }
}
