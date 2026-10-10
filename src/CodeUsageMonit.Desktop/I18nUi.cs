// Avalonia 版 I18n.Localize（逻辑树遍历）：
// 遍历逻辑树，翻译 TextBlock.Text 与 Run.Text（含混合段落）、字符串形式的
// Content（Button 等）与 Header（MenuItem）、ToolTip、ContextMenu 与 AutomationProperties.Name。
// Swap 语义：记住每个元素每个属性的原文与上次显示值；代码改过文本时以新文本为新原文。
// （DarkMenu 的行在构建时已经过 I18n.T，打开即为目标语言。）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Automation;

namespace CodeUsageMonit {
    internal static class I18nUi {
        // Per element and property: the text it was built with and what was shown last.
        private sealed class Shown { public string Source, Text; }
        private static readonly ConditionalWeakTable<object, Dictionary<object, Shown>> shown = new ConditionalWeakTable<object, Dictionary<object, Shown>>();
        private static readonly object automationNameKey = new object();

        public static void Localize(Visual root) {
            if (root == null || (!I18n.English && !I18n.EverEnglish)) return;
            var visited = new HashSet<object>();
            Visit(root, visited);
        }
        private static void Visit(object node, HashSet<object> visited) {
            if (node == null || !visited.Add(node)) return;
            var text = node as TextBlock;
            // Text set directly leaves Inlines empty; built-up text is a list of runs.
            // (A label followed by added runs, e.g. a bold percentage, keeps its runs.)
            // 只有 Text 被直接设置过的文本块才走 Text 分支：Avalonia 12 无 ReadLocalValue，
            // 未设 Text 时 CLR 属性返回 null（已实测），据此分派。
            // “恰好 1 个 Run 且未设 Text”的文本块按内联分支翻译。
            if (text != null && text.Inlines.Count <= 1 && text.Text != null) Swap(text, TextBlock.TextProperty);
            else if (text != null && text.Inlines != null) {
                foreach (Inline inline in text.Inlines.ToList()) {
                    var run = inline as Run; if (run != null) Swap(run, Run.TextProperty);
                    var span = inline as Span; if (span != null) foreach (Inline inner in span.Inlines.ToList()) { var r = inner as Run; if (r != null) Swap(r, Run.TextProperty); }
                }
            }
            var window = node as Window; if (window != null) Swap(window, Window.TitleProperty);
            var content = node as ContentControl; if (content != null && content.Content is string) Swap(content, ContentControl.ContentProperty);
            var header = node as MenuItem; if (header != null && header.Header is string) Swap(header, MenuItem.HeaderProperty);
            var element = node as Control;
            if (element != null) {
                object tip = ToolTip.GetTip(element);
                if (tip is string) Swap(element, ToolTip.TipProperty);
                else if (tip is Visual) Visit((Visual)tip, visited);
                if (element.ContextMenu != null) Visit(element.ContextMenu, visited);
                // 文本框的水印。
                var box = element as TextBox;
                if (box != null && !String.IsNullOrEmpty(box.PlaceholderText)) Swap(box, TextBox.PlaceholderTextProperty);
            }
            var styled = node as Avalonia.StyledElement;
            if (styled != null) {
                string name = AutomationProperties.GetName(styled);
                if (!String.IsNullOrEmpty(name)) SwapProperty(styled, automationNameKey, name, wanted => AutomationProperties.SetName(styled, wanted));
            }
            if (text != null) return;
            var logical = node as ILogical;
            if (logical != null) foreach (object child in logical.GetLogicalChildren().ToList()) Visit(child as Visual, visited);
        }
        private static void Swap(AvaloniaObject d, AvaloniaProperty property) {
            SwapProperty(d, property, d.GetValue(property) as string, wanted => d.SetValue(property, wanted));
        }
        private static void SwapProperty(AvaloniaObject d, object property, string current, Action<string> set) {
            if (current == null) return;
            Dictionary<object, Shown> map = shown.GetValue(d, x => new Dictionary<object, Shown>());
            Shown last;
            // Text changed by the code since the last pass is the new original.
            if (!map.TryGetValue(property, out last) || last.Text != current) last = new Shown { Source = current };
            string wanted = I18n.T(last.Source);
            if (wanted != current) set(wanted);
            last.Text = wanted; map[property] = last;
        }
    }
}
