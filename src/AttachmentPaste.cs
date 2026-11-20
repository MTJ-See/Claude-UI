using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeUI
{
    internal static class AttachmentPaste
    {
        internal static bool HasAttachment(IDataObject data)
        {
            return data != null && (data.GetDataPresent(DataFormats.FileDrop) || data.GetDataPresent(DataFormats.Bitmap));
        }

        // TextBox's built-in Paste command rejects image-only clipboards before
        // DataObject.Pasting is raised. Handle both command stages first.
        public static void Attach(TextBox prompt, Func<IDataObject> readClipboard, Func<bool> blocked,
            Action<IDataObject> paste, Action<string> reportError)
        {
            CommandManager.AddPreviewCanExecuteHandler(prompt, (sender, e) => {
                if (e.Command != ApplicationCommands.Paste) return;
                if (blocked()) { e.CanExecute = false; e.Handled = true; return; }
                try
                {
                    if (HasAttachment(readClipboard())) { e.CanExecute = true; e.Handled = true; }
                }
                catch (System.Runtime.InteropServices.ExternalException) { e.CanExecute = false; e.Handled = true; }
            });
            CommandManager.AddPreviewExecutedHandler(prompt, (sender, e) => {
                if (e.Command != ApplicationCommands.Paste) return;
                if (blocked()) { e.Handled = true; return; }
                try
                {
                    var data = readClipboard();
                    if (!HasAttachment(data)) return;
                    e.Handled = true;
                    paste(data);
                }
                catch (Exception ex) { e.Handled = true; reportError("粘贴附件失败 · " + ex.Message); }
            });
        }
    }
}
