using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClaudeUI
{
    public sealed class ChatAttachment
    {
        public string Name { get; set; }
        public string LocalPath { get; set; }
        public string MediaType { get; set; }
        public long Size { get; set; }
    }

    internal static class AttachmentFiles
    {
        public const int MaxCount = 10;
        public const long MaxFileSize = 20 * 1024 * 1024;
        public const long MaxTotalSize = 30 * 1024 * 1024;

        public static ChatAttachment Import(string source, string directory)
        {
            var info = new FileInfo(source);
            if (!info.Exists) throw new IOException("文件不存在：" + info.Name);
            if (info.Length > MaxFileSize) throw new IOException("每个附件最多 20 MB：" + info.Name);
            byte[] bytes;
            using (var input = File.OpenRead(source))
            {
                if (input.Length > MaxFileSize) throw new IOException("附件超过 20 MB。");
                bytes = new byte[(int)input.Length];
                int offset = 0, read;
                while (offset < bytes.Length && (read = input.Read(bytes, offset, bytes.Length - offset)) > 0) offset += read;
                if (offset != bytes.Length) throw new IOException("读取文件时内容发生变化，请重试。");
            }
            string mime = ImageType(bytes);
            // Normalize Windows bitmap/TIFF files to a format accepted by vision models.
            string extension = info.Extension.ToLowerInvariant();
            if (mime == null && new[] { ".bmp", ".tif", ".tiff" }.Contains(extension))
            {
                var frame = BitmapFrame.Create(new Uri(info.FullName), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return SaveImage(frame, directory, info.Name + ".png");
            }
            return Save(bytes, directory, info.Name, mime);
        }

        public static ChatAttachment SaveImage(BitmapSource bitmap, string directory, string name)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var memory = new MemoryStream())
            {
                encoder.Save(memory);
                return Save(memory.ToArray(), directory, name, "image/png");
            }
        }

        private static ChatAttachment Save(byte[] bytes, string directory, string name, string mime)
        {
            if (bytes.LongLength > MaxFileSize) throw new IOException("每个附件最多 20 MB。");
            if (mime != null && bytes.Length > 5 * 1024 * 1024) throw new IOException("每张图片最多 5 MB，请缩小后添加。");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, Guid.NewGuid().ToString("N") + "-" + Path.GetFileName(name));
            File.WriteAllBytes(path, bytes);
            return new ChatAttachment { Name = name, LocalPath = path, Size = bytes.LongLength, MediaType = mime };
        }

        private static string ImageType(byte[] b)
        {
            if (b.Length >= 8 && b.Take(8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10})) return "image/png";
            if (b.Length >= 3 && b[0] == 255 && b[1] == 216 && b[2] == 255) return "image/jpeg";
            if (b.Length >= 6 && System.Text.Encoding.ASCII.GetString(b, 0, 6).StartsWith("GIF8")) return "image/gif";
            if (b.Length >= 12 && System.Text.Encoding.ASCII.GetString(b, 0, 4) == "RIFF" && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "WEBP") return "image/webp";
            return null;
        }

        public static string Prompt(string text, IList<ChatAttachment> attachments)
        {
            if (attachments.Count == 0) return text;
            return (String.IsNullOrWhiteSpace(text) ? "请查看这些附件。" : text) + "\n\n附件（本地副本；非图片文件请使用读取工具查看）：\n" +
                String.Join("\n", attachments.Select(a => a.Name + "\n" + a.LocalPath));
        }

        public static object Content(string text, IList<ChatAttachment> attachments)
        {
            foreach (var a in attachments)
                if (!File.Exists(a.LocalPath)) throw new IOException("附件副本已丢失，请移除并重新添加：" + a.Name);
            var parts = new List<object> { new { type = "text", text = text } };
            foreach (var a in attachments.Where(a => a.MediaType != null))
                parts.Add(new { type = "image", source = new { type = "base64", media_type = a.MediaType,
                    data = Convert.ToBase64String(File.ReadAllBytes(a.LocalPath)) } });
            return parts;
        }

        public static FrameworkElement Card(ChatAttachment attachment, Action remove = null)
        {
            var panel = new StackPanel { Width = 165 };
            if (attachment.MediaType != null)
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = 240; image.UriSource = new Uri(attachment.LocalPath); image.EndInit(); image.Freeze();
                    panel.Children.Add(new Image { Source = image, Height = 80, Stretch = Stretch.Uniform, Margin = new Thickness(0,0,0,5) });
                }
                catch (IOException) { }
                catch (NotSupportedException) { }
                catch (ArgumentException) { }
            }
            var name = new TextBlock { Text = attachment.Name, TextTrimming = TextTrimming.CharacterEllipsis, ToolTip = attachment.LocalPath };
            name.SetResourceReference(TextBlock.ForegroundProperty, "Ink");
            panel.Children.Add(name);
            var size = new TextBlock { Text = (attachment.Size / 1024d).ToString("0.#") + " KB", FontSize = 11 };
            size.SetResourceReference(TextBlock.ForegroundProperty, "MutedInk"); panel.Children.Add(size);
            if (remove != null)
            {
                var button = new Button { Content = "移除", Margin = new Thickness(0,4,0,0) };
                button.Click += delegate { remove(); }; panel.Children.Add(button);
            }
            var card = new Border { Child = panel, Padding = new Thickness(8), Margin = new Thickness(0,0,8,8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(5) };
            card.SetResourceReference(Border.BorderBrushProperty, "Line");
            return card;
        }
    }
}
