using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CodeMerger.Services
{
    public class XamlElementNode
    {
        public string ElementName { get; set; } = string.Empty;
        public string? XName { get; set; }
        public string? Header { get; set; }
        public string? Content { get; set; }
        public string? Text { get; set; }
        public string? Binding { get; set; }
        public string? Style { get; set; }
        public int StartLine { get; set; }
        public int EndLine { get; set; }
        public List<XamlElementNode> Children { get; set; } = new();
    }

    public class XamlTreeResult
    {
        public string FilePath { get; set; } = string.Empty;
        public int TotalLines { get; set; }
        public List<string> Bindings { get; set; } = new();
        public List<string> NamedElements { get; set; } = new();
        public XamlElementNode Root { get; set; } = new();

        public string ToMarkdown()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# XAML Tree: {Path.GetFileName(FilePath)}");
            sb.AppendLine($"**Lines:** {TotalLines}");
            sb.AppendLine();

            if (NamedElements.Count > 0)
            {
                sb.AppendLine("## Named Elements (x:Name)");
                foreach (var named in NamedElements)
                    sb.AppendLine($"- {named}");
                sb.AppendLine();
            }

            if (Bindings.Count > 0)
            {
                sb.AppendLine("## Bindings");
                foreach (var binding in Bindings.Distinct().OrderBy(b => b))
                    sb.AppendLine($"- {binding}");
                sb.AppendLine();
            }

            sb.AppendLine("## Element Tree");
            sb.AppendLine("```");
            RenderNode(sb, Root, 0);
            sb.AppendLine("```");
            return sb.ToString();
        }

        private void RenderNode(StringBuilder sb, XamlElementNode node, int indent)
        {
            var prefix = new string(' ', indent * 2);
            var lineRange = node.StartLine == node.EndLine
                ? $"L{node.StartLine}"
                : $"L{node.StartLine}-{node.EndLine}";

            var details = new List<string>();

            if (!string.IsNullOrEmpty(node.XName))
                details.Add($"x:Name=\"{node.XName}\"");
            if (!string.IsNullOrEmpty(node.Header))
                details.Add($"Header=\"{node.Header}\"");
            if (!string.IsNullOrEmpty(node.Content))
                details.Add($"Content=\"{node.Content}\"");
            if (!string.IsNullOrEmpty(node.Text))
                details.Add($"Text=\"{node.Text}\"");
            if (!string.IsNullOrEmpty(node.Binding))
                details.Add($"→ {{{node.Binding}}}");
            if (!string.IsNullOrEmpty(node.Style))
                details.Add($"Style={node.Style}");

            var detailStr = details.Count > 0 ? " " + string.Join(" | ", details) : "";
            sb.AppendLine($"{prefix}{lineRange}: {node.ElementName}{detailStr}");

            foreach (var child in node.Children)
                RenderNode(sb, child, indent + 1);
        }
    }

    public static class XamlAnalyzer
    {
        // Elements that clutter the tree without adding structural value
        private static readonly HashSet<string> SkipElements = new(StringComparer.OrdinalIgnoreCase)
        {
            "Setter", "Setter.Value", "Trigger", "DataTrigger", "MultiTrigger",
            "EventTrigger", "Style.Triggers", "ControlTemplate.Triggers",
            "GradientStop", "LinearGradientBrush", "RadialGradientBrush",
            "SolidColorBrush", "RotateTransform", "ScaleTransform", "TranslateTransform",
            "Storyboard", "DoubleAnimation", "ColorAnimation",
            "DataGridTextColumn", "DataGridTemplateColumn", "DataGridCheckBoxColumn",
            "ColumnDefinition", "RowDefinition",
            "Grid.ColumnDefinitions", "Grid.RowDefinitions",
            "BooleanToVisibilityConverter"
        };

        // Elements that are structural containers we always want to show
        private static readonly HashSet<string> AlwaysShow = new(StringComparer.OrdinalIgnoreCase)
        {
            "Window", "UserControl", "Page", "Grid", "StackPanel", "DockPanel",
            "WrapPanel", "Canvas", "Border", "ScrollViewer", "TabControl", "TabItem",
            "GroupBox", "Expander", "Menu", "MenuItem", "ToolBar", "StatusBar",
            "DataGrid", "ListView", "ListBox", "TreeView", "ComboBox",
            "Button", "ToggleButton", "RadioButton", "CheckBox",
            "TextBox", "TextBlock", "Label", "PasswordBox", "RichTextBox",
            "Image", "MediaElement", "Slider", "ProgressBar",
            "ContentControl", "ItemsControl", "Frame",
            "DatePicker", "Calendar"
        };

        public static XamlTreeResult Analyze(string filePath, string basePath)
        {
            var result = new XamlTreeResult
            {
                FilePath = Path.GetRelativePath(basePath, filePath)
            };

            try
            {
                var content = File.ReadAllText(filePath);
                result.TotalLines = content.Split('\n').Length;

                var doc = XDocument.Load(filePath, LoadOptions.SetLineInfo);
                if (doc.Root == null) return result;

                result.Root = BuildNode(doc.Root, result);
            }
            catch (Exception ex)
            {
                result.Root = new XamlElementNode { ElementName = $"ERROR: {ex.Message}", StartLine = 0, EndLine = 0 };
            }

            return result;
        }

        private static XamlElementNode BuildNode(XElement element, XamlTreeResult result)
        {
            var localName = element.Name.LocalName;
            var lineInfo = (IXmlLineInfo)element;

            var node = new XamlElementNode
            {
                ElementName = localName,
                StartLine = lineInfo.HasLineInfo() ? lineInfo.LineNumber : 0
            };

            // Extract key attributes
            var ns = element.Name.Namespace;
            var xNs = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");

            var xName = element.Attribute(xNs + "Name")?.Value ?? element.Attribute("Name")?.Value;
            if (!string.IsNullOrEmpty(xName))
            {
                node.XName = xName;
                result.NamedElements.Add($"{localName} → {xName} (L{node.StartLine})");
            }

            node.Header = element.Attribute("Header")?.Value;
            node.Content = element.Attribute("Content")?.Value;
            node.Text = element.Attribute("Text")?.Value;

            var style = element.Attribute("Style")?.Value;
            if (!string.IsNullOrEmpty(style))
            {
                // Extract style key: {StaticResource Btn} → Btn
                var match = System.Text.RegularExpressions.Regex.Match(style, @"\{(?:Static|Dynamic)Resource\s+(\w+)\}");
                node.Style = match.Success ? match.Groups[1].Value : style;
            }

            // Extract binding from ItemsSource or common binding attributes
            ExtractBindings(element, node, result);

            // Calculate end line from last descendant
            var lastDescendant = element.DescendantsAndSelf()
                .Where(e => e is IXmlLineInfo li && li.HasLineInfo())
                .Select(e => ((IXmlLineInfo)e).LineNumber)
                .DefaultIfEmpty(node.StartLine)
                .Max();
            node.EndLine = lastDescendant;

            // Process children, filtering noise
            foreach (var child in element.Elements())
            {
                var childLocal = child.Name.LocalName;

                // Skip noise elements
                if (IsSkippable(childLocal))
                    continue;

                // For property elements (e.g., Grid.RowDefinitions, DataGrid.Columns), skip them
                if (childLocal.Contains('.') && !IsStructuralProperty(childLocal))
                    continue;

                // Recurse into resources but don't show individual resource items
                if (childLocal.EndsWith(".Resources"))
                {
                    var resourceCount = child.Elements().Count();
                    var resNode = new XamlElementNode
                    {
                        ElementName = childLocal,
                        StartLine = ((IXmlLineInfo)child).HasLineInfo() ? ((IXmlLineInfo)child).LineNumber : 0,
                        EndLine = child.DescendantsAndSelf().Where(e => e is IXmlLineInfo li && li.HasLineInfo())
                            .Select(e => ((IXmlLineInfo)e).LineNumber).DefaultIfEmpty(0).Max(),
                        Content = $"{resourceCount} items"
                    };
                    node.Children.Add(resNode);
                    continue;
                }

                var childNode = BuildNode(child, result);

                // Only include if it's a known structural element, has children, has a name, or has a binding
                if (ShouldInclude(childNode, childLocal))
                    node.Children.Add(childNode);
            }

            return node;
        }

        private static void ExtractBindings(XElement element, XamlElementNode node, XamlTreeResult result)
        {
            string[] bindingAttrs = { "ItemsSource", "SelectedItem", "SelectedValue", "Text", "Content",
                "IsChecked", "Value", "Visibility", "IsEnabled", "Command", "CommandParameter" };

            foreach (var attrName in bindingAttrs)
            {
                var attr = element.Attribute(attrName)?.Value;
                if (attr != null && attr.Contains("{Binding"))
                {
                    var match = System.Text.RegularExpressions.Regex.Match(attr, @"\{Binding\s+([^}]+)\}");
                    if (match.Success)
                    {
                        var bindingPath = match.Groups[1].Value.Trim();
                        // Get the first path component (the property name)
                        var path = bindingPath.Split(',')[0].Trim();
                        if (path.StartsWith("Path="))
                            path = path.Substring(5);

                        node.Binding = $"{attrName}={path}";
                        result.Bindings.Add($"{path} ← {element.Name.LocalName}.{attrName} (L{node.StartLine})");
                    }
                }
            }
        }

        private static bool IsSkippable(string localName)
        {
            if (SkipElements.Contains(localName)) return true;
            // Skip Style definitions, ControlTemplate internals
            if (localName == "Style" || localName == "ControlTemplate") return true;
            return false;
        }

        private static bool IsStructuralProperty(string localName)
        {
            // Property elements that contain structural children we care about
            return localName.EndsWith(".Resources") ||
                   localName.EndsWith(".Content") ||
                   localName.EndsWith(".Items") ||
                   localName.EndsWith(".ContextMenu") ||
                   localName.EndsWith(".ToolTip");
        }

        private static bool ShouldInclude(XamlElementNode node, string localName)
        {
            if (AlwaysShow.Contains(localName)) return true;
            if (!string.IsNullOrEmpty(node.XName)) return true;
            if (!string.IsNullOrEmpty(node.Binding)) return true;
            if (!string.IsNullOrEmpty(node.Header)) return true;
            if (node.Children.Count > 0) return true;
            return false;
        }
    }
}
