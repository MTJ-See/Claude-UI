using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ClaudeUI
{
    internal static class MarketplaceDetailView
    {
        internal static TextBlock Label(string value, double size, string color = "Ink")
        {
            var label = new TextBlock { Text = value ?? "", FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
            label.SetResourceReference(TextBlock.ForegroundProperty, color); return label;
        }
        internal static Border Card(UIElement body)
        {
            var card = new Border { Child = body, Padding = new Thickness(22), Margin = new Thickness(0, 0, 0, 16), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
            card.SetResourceReference(Border.BorderBrushProperty, "Line"); card.SetResourceReference(Border.BackgroundProperty, "Panel"); return card;
        }
        internal static StackPanel Section(string title, string subtitle = null)
        {
            var panel = new StackPanel(); var heading = Label(title, 17); heading.FontWeight = FontWeights.SemiBold;
            panel.Children.Add(heading);
            if (!String.IsNullOrEmpty(subtitle)) panel.Children.Add(Label(subtitle, 12, "MutedInk"));
            return panel;
        }
        internal static UIElement Create(MarketplaceItem item, Button install, Button source)
        {
            var page = new StackPanel { MaxWidth = 1100, HorizontalAlignment = HorizontalAlignment.Stretch };
            var hero = new StackPanel();
            hero.Children.Add(Label(item.Kind == MarketplaceKind.Mcp ? "MCP SERVER" : "CLAUDE SKILL", 11, "MutedInk"));
            var heading = Label(item.Name, 28); heading.FontWeight = FontWeights.SemiBold; hero.Children.Add(heading);
            hero.Children.Add(Label(item.Description, 14));
            var tags = new WrapPanel { Margin = new Thickness(0, 4, 0, 14) };
            foreach (string tag in item.Tags)
            {
                var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(9, 4, 9, 1), Margin = new Thickness(0, 0, 7, 5), Child = Label(tag, 11, "MutedInk") };
                chip.SetResourceReference(Border.BackgroundProperty, "Composer"); tags.Children.Add(chip);
            }
            hero.Children.Add(tags);
            hero.Children.Add(Label("作者  " + item.Author + "     ·     " + item.Popularity + (String.IsNullOrEmpty(item.License) ? "" : "     ·     " + item.License), 12, "MutedInk"));
            var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; actions.Children.Add(install); if (source != null) actions.Children.Add(source); hero.Children.Add(actions);
            page.Children.Add(Card(hero));
            var intro = Section("功能介绍");
            string markdown = String.IsNullOrWhiteSpace(item.Readme) ? item.Description : item.Readme;
            // Markdown renderer is text-only; strip structural HTML tags from platform READMEs.
            markdown = Regex.Replace(markdown ?? "", @"</?(details|summary|div|span|br|p)(\s[^>]*)?>", "\n", RegexOptions.IgnoreCase);
            var prose = new MarkdownReply(markdown) { FontSize = 14, Margin = new Thickness(0, 8, 0, 0) };
            prose.SetResourceReference(Control.ForegroundProperty, "Ink"); intro.Children.Add(prose); page.Children.Add(Card(intro));
            var config = Section("配置参数", item.Kind == MarketplaceKind.Skill ? "Skill 按 Claude 目录规范安装，无需 MCP 服务配置。" : "安装时可填写参数；密钥不会显示在日志中。");
            if (item.Parameters.Count == 0) config.Children.Add(Label(item.Kind == MarketplaceKind.Skill ? "安装时选择用户级或项目级目录。" : "平台未声明额外环境参数，请参考功能介绍。", 13, "MutedInk"));
            foreach (var parameter in item.Parameters)
            {
                var row = new StackPanel { Margin = new Thickness(0, 10, 0, 5) };
                row.Children.Add(Label(parameter.Name + (parameter.Required ? "  ·  必填" : "  ·  可选"), 13));
                row.Children.Add(Label(parameter.Description, 12, "MutedInk"));
                if (!String.IsNullOrEmpty(parameter.Default)) row.Children.Add(Label("默认值：" + parameter.Default, 12, "MutedInk"));
                config.Children.Add(row);
            }
            page.Children.Add(Card(config));
            var security = Section("权限与来源"); security.Children.Add(Label(item.Permissions, 13, "MutedInk"));
            security.Children.Add(Label(item.Kind == MarketplaceKind.Mcp ? "本地 MCP 在你的账户权限下运行，远程 MCP 可接收工具调用中的数据。" : "Skill 可包含脚本和工具指令，使用时会影响 Claude 的行为。", 12, "MutedInk"));
            if (!String.IsNullOrEmpty(item.SourceUrl)) security.Children.Add(Label(item.SourceUrl, 12, "MutedInk"));
            page.Children.Add(Card(security));
            var advanced = new Expander { Header = "查看官方安装配置", Margin = new Thickness(0, 2, 0, 16) };
            advanced.SetResourceReference(Control.ForegroundProperty, "Ink");
            var code = new TextBox { Text = item.Installation ?? "平台未提供", IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(14), BorderThickness = new Thickness(0) };
            code.SetResourceReference(Control.BackgroundProperty, "Composer"); code.SetResourceReference(Control.ForegroundProperty, "Ink"); advanced.Content = code; page.Children.Add(advanced);
            return page;
        }
    }
}
