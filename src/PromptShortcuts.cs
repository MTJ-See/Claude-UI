using System;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClaudeUI
{
    internal enum PromptAction { None, Send, NewLine }

    internal static class PromptShortcuts
    {
        public static void Attach(TextBox prompt, Action send, Func<KeyEventArgs, bool> complete = null)
        {
            bool composing = false;
            TextCompositionManager.AddPreviewTextInputStartHandler(prompt, (sender, e) => composing = true);
            TextCompositionManager.AddPreviewTextInputHandler(prompt, (sender, e) => composing = false);
            prompt.LostKeyboardFocus += (sender, e) => composing = false;
            prompt.PreviewKeyDown += (sender, e) =>
            {
                // The IME owns Enter while it is committing a composition.
                if (composing || e.Key == Key.ImeProcessed)
                {
                    if (e.Key == Key.Escape || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape))
                        composing = false;
                    return;
                }
                if (complete != null && complete(e)) { e.Handled = true; return; }
                if (e.Key != Key.Enter) return;
                e.Handled = true;
                if (prompt.IsReadOnly) return;
                var action = GetAction(e.Key, e.KeyboardDevice.Modifiers, e.IsRepeat);
                if (action == PromptAction.Send) send();
                else if (action == PromptAction.NewLine)
                {
                    int insertion = prompt.SelectionStart;
                    prompt.SelectedText = Environment.NewLine;
                    prompt.Select(insertion + Environment.NewLine.Length, 0);
                }
            };
        }

        internal static PromptAction GetAction(Key key, ModifierKeys modifiers, bool repeat)
        {
            if (key != Key.Enter || repeat) return PromptAction.None;
            if (modifiers == ModifierKeys.None) return PromptAction.Send;
            return modifiers == ModifierKeys.Control ? PromptAction.NewLine : PromptAction.None;
        }
    }
}
