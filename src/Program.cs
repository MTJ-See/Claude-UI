using System;
using System.IO;
using System.Threading;
using System.Windows;

namespace ClaudeUI
{
    internal static class Program
    {
        [STAThread]
        public static int Main()
        {
            bool firstInstance;
            using (var instance = new Mutex(true, @"Local\ClaudeUI.Desktop", out firstInstance))
            {
                if (!firstInstance)
                {
                    MessageBox.Show("Claude UI 已打开，请从任务栏切换到现有窗口。", "Claude UI");
                    return 0;
                }
                try
                {
                    var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                    string legacyData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeUI");
                    string localData = PortableData.Prepare(AppDomain.CurrentDomain.BaseDirectory, legacyData);
                    var window = new MainWindow(new DataStore(localData));
                    return app.Run(window.View);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.GetBaseException().Message, "Claude UI 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
            }
        }
    }
}
