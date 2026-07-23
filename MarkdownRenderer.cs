using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

// Markdig and WinUI both define Block / Inline; alias the Markdig document model.
using MdBlock = Markdig.Syntax.Block;
using MdContainerBlock = Markdig.Syntax.ContainerBlock;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdInlineElement = Microsoft.UI.Xaml.Documents.Inline;

namespace MdPad;

/// <summary>A heading found while rendering: what it says, where it came from, and where it landed.</summary>
/// <param name="Level">Heading level, 1-6.</param>
/// <param name="Text">Heading text with inline formatting stripped.</param>
/// <param name="Line">Zero-based line of the heading in the Markdown source.</param>
/// <param name="Element">The rendered element, used to scroll the preview to this section.</param>
public sealed record OutlineEntry(int Level, string Text, int Line, FrameworkElement Element);

/// <summary>
/// Renders a Markdown string into a tree of native WinUI controls (no WebView2).
/// One top-level Markdown block becomes one FrameworkElement in the host panel;
/// inline formatting is rendered with XAML <see cref="Inline"/> runs.
/// </summary>
public static class MarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        // Agent skill files (SKILL.md) and static-site pages open with a YAML block.
        // Without this it parses as a setext heading and swallows the document.
        .UseYamlFrontMatter()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    // WinUI's FontFamily takes a single family name -- unlike CSS/WPF it does not
    // accept a comma-separated fallback list.
    private static readonly FontFamily MonoFont = new("Consolas");

    /// <summary>Folder of the document being rendered, for resolving relative links.</summary>
    private static string? _documentPath;

    /// <summary>Invoked when a link to a local file is clicked.</summary>
    private static Action<string>? _openLocalFile;

    /// <summary>
    /// Parse <paramref name="markdown"/> and (re)populate <paramref name="host"/> with rendered controls.
    /// Returns the document's headings in order, for the outline sidebar.
    /// </summary>
    /// <param name="documentPath">Path of the file being rendered; relative links resolve against it.</param>
    /// <param name="openLocalFile">Called with a full path when the reader clicks a link to a local file.</param>
    public static IReadOnlyList<OutlineEntry> Render(
        string? markdown,
        Panel host,
        string? documentPath = null,
        Action<string>? openLocalFile = null)
    {
        _documentPath = documentPath;
        _openLocalFile = openLocalFile;
        host.Children.Clear();
        var outline = new List<OutlineEntry>();
        MarkdownDocument doc = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        foreach (MdBlock block in doc)
        {
            RenderBlock(block, host.Children, outline);
        }
        return outline;
    }

    // ---- Block-level rendering ------------------------------------------------

    private static void RenderBlock(MdBlock block, UIElementCollection host, List<OutlineEntry> outline)
    {
        switch (block)
        {
            case HeadingBlock heading:
                FrameworkElement headingElement = BuildHeading(heading);
                host.Add(headingElement);
                outline.Add(new OutlineEntry(heading.Level, InlineText(heading.Inline), heading.Line, headingElement));
                break;
            case ParagraphBlock paragraph:
                host.Add(BuildParagraph(paragraph));
                break;
            case ListBlock list:
                host.Add(BuildList(list, outline));
                break;
            case QuoteBlock quote:
                host.Add(BuildQuote(quote, outline));
                break;
            // Must precede CodeBlock: YamlFrontMatterBlock derives from it.
            case YamlFrontMatterBlock frontMatter:
                host.Add(BuildFrontMatter(frontMatter));
                break;
            case CodeBlock code: // covers FencedCodeBlock and indented code
                host.Add(BuildCodeBlock(code));
                break;
            case ThematicBreakBlock:
                host.Add(BuildThematicBreak());
                break;
            case Table table:
                host.Add(BuildTable(table, outline));
                break;
            case HtmlBlock html:
                host.Add(BuildRawText(html.Lines.ToString()));
                break;
            default:
                // Unknown container blocks: recurse into children so nothing is lost.
                if (block is MdContainerBlock container)
                {
                    foreach (MdBlock child in container)
                    {
                        RenderBlock(child, host, outline);
                    }
                }
                break;
        }
    }

    private static FrameworkElement BuildHeading(HeadingBlock heading)
    {
        double size = heading.Level switch
        {
            1 => 30,
            2 => 24,
            3 => 20,
            4 => 17,
            5 => 15,
            _ => 13,
        };

        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = size,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, heading.Level <= 2 ? 16 : 12, 0, 6),
        };
        if (heading.Inline is not null)
        {
            RenderInlines(heading.Inline, tb.Inlines);
        }

        if (heading.Level <= 2)
        {
            // Underline larger headings with a subtle divider, GitHub-style.
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(tb);
            panel.Children.Add(new Border
            {
                Height = 1,
                Background = Brush("DividerStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128)),
                Margin = new Thickness(0, 0, 0, 4),
            });
            return panel;
        }
        return tb;
    }

    private static FrameworkElement BuildParagraph(ParagraphBlock paragraph)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var p = new Paragraph { LineHeight = 22 };
        if (paragraph.Inline is not null)
        {
            RenderInlines(paragraph.Inline, p.Inlines);
        }
        rtb.Blocks.Add(p);
        return rtb;
    }

    private static FrameworkElement BuildList(ListBlock list, List<OutlineEntry> outline)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0, 0, 10) };
        int number = 1;
        if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start))
        {
            number = start;
        }

        foreach (MdBlock item in list)
        {
            if (item is not ListItemBlock listItem)
            {
                continue;
            }

            string marker = list.IsOrdered ? $"{number}." : "•";
            bool? taskState = GetTaskState(listItem);
            if (taskState is not null)
            {
                marker = taskState.Value ? "☑" : "☐"; // ☑ / ☐
            }

            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(list.IsOrdered ? 28 : 20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var markerBlock = new TextBlock
            {
                Text = marker,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 6, 0),
                Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 120, 120, 120)),
            };
            if (taskState is not null)
            {
                // Checkbox glyphs line up only in a fixed-width font.
                markerBlock.FontFamily = MonoFont;
            }
            Grid.SetColumn(markerBlock, 0);
            row.Children.Add(markerBlock);

            var content = new StackPanel();
            Grid.SetColumn(content, 1);
            foreach (MdBlock child in listItem)
            {
                RenderBlock(child, content.Children, outline);
            }
            TrimLastMargin(content);
            row.Children.Add(content);

            panel.Children.Add(row);
            number++;
        }
        return panel;
    }

    private static FrameworkElement BuildQuote(QuoteBlock quote, List<OutlineEntry> outline)
    {
        var inner = new StackPanel();
        foreach (MdBlock child in quote)
        {
            RenderBlock(child, inner.Children, outline);
        }
        TrimLastMargin(inner);

        var border = new Border
        {
            BorderThickness = new Thickness(4, 0, 0, 0),
            BorderBrush = Brush("AccentFillColorDefaultBrush", Color.FromArgb(255, 90, 140, 220)),
            Padding = new Thickness(12, 4, 8, 4),
            Margin = new Thickness(0, 0, 0, 10),
            Child = inner,
        };
        return border;
    }

    private static FrameworkElement BuildCodeBlock(CodeBlock code)
    {
        string text = code.Lines.ToString();
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = 13,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.NoWrap,
        };

        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = tb,
        };

        return new Border
        {
            Background = Brush("LayerFillColorDefaultBrush", Color.FromArgb(60, 128, 128, 128)),
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(50, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = scroller,
        };
    }

    /// <summary>
    /// Render a YAML front matter block as a header card: the name as a title, the
    /// description as body text, and everything else as a key/value list. Agent skill
    /// files lead with exactly this, and it reads far better than raw YAML.
    /// </summary>
    private static FrameworkElement BuildFrontMatter(YamlFrontMatterBlock block)
    {
        List<(string Key, string Value)> fields = FrontMatter.Parse(block);

        string? name = FrontMatter.Find(fields, "name") ?? FrontMatter.Find(fields, "title");
        string? description = FrontMatter.Find(fields, "description") ?? FrontMatter.Find(fields, "summary");

        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            Text = name is null ? "FRONT MATTER" : "SKILL",
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("AccentTextFillColorPrimaryBrush", Color.FromArgb(255, 120, 170, 255)),
        });

        if (name is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 26,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            });
        }

        if (description is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Thickness(0, 2, 0, 0),
                Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 170, 170, 170)),
            });
        }

        // Remaining keys (allowed-tools, license, user-invocable, …) as a compact table.
        var rest = fields
            .Where(f => !IsHeadlineKey(f.Key, name, description))
            .ToList();

        if (rest.Count > 0)
        {
            var grid = new Grid { Margin = new Thickness(0, 10, 0, 0), ColumnSpacing = 12, RowSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            for (int i = 0; i < rest.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var key = new TextBlock
                {
                    Text = rest[i].Key,
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("TextFillColorTertiaryBrush", Color.FromArgb(255, 140, 140, 140)),
                };
                Grid.SetRow(key, i);
                Grid.SetColumn(key, 0);
                grid.Children.Add(key);

                var value = new TextBlock
                {
                    Text = rest[i].Value,
                    FontSize = 12,
                    FontFamily = MonoFont,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                };
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(value);
            }

            stack.Children.Add(grid);
        }

        return new Border
        {
            Background = Brush("CardBackgroundFillColorSecondaryBrush", Color.FromArgb(40, 128, 128, 128)),
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(60, 128, 128, 128)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 14, 18, 16),
            Margin = new Thickness(0, 0, 0, 18),
            Child = stack,
        };
    }

    private static bool IsHeadlineKey(string key, string? name, string? description) =>
        (name is not null && (key is "name" or "title"))
        || (description is not null && (key is "description" or "summary"));

    private static FrameworkElement BuildThematicBreak() => new Border
    {
        Height = 1,
        Background = Brush("DividerStrokeColorDefaultBrush", Color.FromArgb(60, 128, 128, 128)),
        Margin = new Thickness(0, 8, 0, 14),
    };

    private static FrameworkElement BuildRawText(string text) => new TextBlock
    {
        Text = text,
        FontFamily = MonoFont,
        FontSize = 12,
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 120, 120, 120)),
        Margin = new Thickness(0, 0, 0, 10),
    };

    private static FrameworkElement BuildTable(Table table, List<OutlineEntry> outline)
    {
        var grid = new Grid
        {
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(80, 128, 128, 128)),
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, 12),
        };

        int columnCount = table.OfType<TableRow>().Select(r => r.Count).DefaultIfEmpty(0).Max();
        for (int c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }

        var rows = table.OfType<TableRow>().ToList();
        for (int r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var headerBg = Brush("LayerFillColorDefaultBrush", Color.FromArgb(50, 128, 128, 128));
        var cellStroke = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(80, 128, 128, 128));

        for (int r = 0; r < rows.Count; r++)
        {
            TableRow row = rows[r];
            int c = 0;
            foreach (MdBlock cellBlock in row)
            {
                if (cellBlock is not TableCell cell)
                {
                    continue;
                }

                var cellContent = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
                foreach (MdBlock child in cell)
                {
                    RenderBlock(child, cellContent.Children, outline);
                }
                TrimLastMargin(cellContent);

                var cellBorder = new Border
                {
                    BorderBrush = cellStroke,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = row.IsHeader ? headerBg : null,
                    Child = cellContent,
                };
                Grid.SetRow(cellBorder, r);
                Grid.SetColumn(cellBorder, c);
                grid.Children.Add(cellBorder);
                c++;
            }
        }

        // Let a wide table scroll horizontally instead of forcing the window wider.
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = grid,
            Margin = new Thickness(0, 0, 0, 2),
        };
    }

    // ---- Inline-level rendering -----------------------------------------------

    private static void RenderInlines(ContainerInline container, InlineCollection target)
    {
        foreach (MdInline inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal:
                    target.Add(new Run { Text = literal.Content.ToString() });
                    break;

                case EmphasisInline emphasis:
                    target.Add(BuildEmphasis(emphasis));
                    break;

                case CodeInline code:
                    target.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = MonoFont,
                        Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 200, 90, 90)),
                    });
                    break;

                case LinkInline { IsImage: true } image:
                    target.Add(BuildImage(image));
                    break;

                case LinkInline link:
                    target.Add(BuildLink(link));
                    break;

                case AutolinkInline auto:
                    var autoLink = new Hyperlink();
                    TrySetNavigateUri(autoLink, auto.Url);
                    autoLink.Inlines.Add(new Run { Text = auto.Url });
                    target.Add(autoLink);
                    break;

                case LineBreakInline lineBreak:
                    target.Add(lineBreak.IsHard ? new LineBreak() : new Run { Text = " " });
                    break;

                case TaskList task:
                    target.Add(new Run
                    {
                        Text = (task.Checked ? "☑" : "☐") + " ",
                        FontFamily = MonoFont,
                    });
                    break;

                case HtmlEntityInline entity:
                    target.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;

                case HtmlInline:
                    // Skip raw inline HTML tags in the native preview.
                    break;

                case ContainerInline nested:
                    RenderInlines(nested, target);
                    break;

                default:
                    string? text = inline.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        target.Add(new Run { Text = text });
                    }
                    break;
            }
        }
    }

    private static Span BuildEmphasis(EmphasisInline emphasis)
    {
        var span = new Span();
        switch (emphasis.DelimiterChar)
        {
            case '~':
                span.TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough;
                break;
            case '*':
            case '_':
                if (emphasis.DelimiterCount >= 2)
                {
                    span.FontWeight = FontWeights.Bold;
                }
                else
                {
                    span.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }
                break;
        }
        RenderInlines(emphasis, span.Inlines);
        return span;
    }

    private static MdInlineElement BuildLink(LinkInline link)
    {
        // A link to a file next to the document opens in a tab rather than a browser.
        if (SkillAnalyzer.IsLocalPath(link.Url))
        {
            return BuildLocalLink(link);
        }

        var hyperlink = new Hyperlink();
        TrySetNavigateUri(hyperlink, link.Url);
        FillLinkText(link, hyperlink.Inlines);
        return hyperlink;
    }

    /// <summary>
    /// Relative links (./rules/styling.md) are the backbone of multi-file skills.
    /// Resolve them: existing files become clickable, missing ones are called out
    /// in place rather than silently reading as ordinary text.
    /// </summary>
    private static MdInlineElement BuildLocalLink(LinkInline link)
    {
        string? resolved = SkillAnalyzer.ResolveLocalPath(link.Url, _documentPath);

        if (resolved is not null && System.IO.File.Exists(resolved))
        {
            var hyperlink = new Hyperlink();
            hyperlink.Click += (_, _) => _openLocalFile?.Invoke(resolved);
            FillLinkText(link, hyperlink.Inlines);
            return hyperlink;
        }

        var broken = new Span
        {
            Foreground = Brush("SystemFillColorCriticalBrush", Color.FromArgb(255, 220, 90, 90)),
            TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough,
        };
        FillLinkText(link, broken.Inlines);
        broken.Inlines.Add(new Run
        {
            Text = "  (missing)",
            FontSize = 11,
            TextDecorations = Windows.UI.Text.TextDecorations.None,
        });
        return broken;
    }

    private static void FillLinkText(LinkInline link, InlineCollection target)
    {
        if (link.FirstChild is not null)
        {
            RenderInlines(link, target);
        }
        else if (!string.IsNullOrEmpty(link.Url))
        {
            target.Add(new Run { Text = link.Url });
        }
    }

    private static InlineUIContainer BuildImage(LinkInline image)
    {
        var img = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 480,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (Uri.TryCreate(image.Url, UriKind.Absolute, out Uri? uri))
        {
            img.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(uri);
        }
        return new InlineUIContainer { Child = img };
    }

    // ---- Helpers --------------------------------------------------------------

    /// <summary>Flatten an inline tree to plain text, for outline captions.</summary>
    private static string InlineText(ContainerInline? container)
    {
        if (container is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        Append(container, sb);
        return sb.ToString().Trim();

        static void Append(ContainerInline container, StringBuilder sb)
        {
            foreach (MdInline inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        sb.Append(literal.Content.ToString());
                        break;
                    case CodeInline code:
                        sb.Append(code.Content);
                        break;
                    case HtmlEntityInline entity:
                        sb.Append(entity.Transcoded.ToString());
                        break;
                    case LineBreakInline:
                        sb.Append(' ');
                        break;
                    case ContainerInline nested:
                        Append(nested, sb);
                        break;
                }
            }
        }
    }

    private static bool? GetTaskState(ListItemBlock item)
    {
        if (item.FirstOrDefault() is ParagraphBlock { Inline: { } inline })
        {
            if (inline.FirstChild is TaskList task)
            {
                return task.Checked;
            }
        }
        return null;
    }

    private static void TrySetNavigateUri(Hyperlink hyperlink, string? url)
    {
        if (!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            hyperlink.NavigateUri = uri;
        }
    }

    /// <summary>Drop the trailing bottom margin of the last child so nested content packs tightly.</summary>
    private static void TrimLastMargin(Panel panel)
    {
        if (panel.Children.Count > 0 && panel.Children[^1] is FrameworkElement last)
        {
            Thickness m = last.Margin;
            last.Margin = new Thickness(m.Left, m.Top, m.Right, 0);
        }
    }

    /// <summary>Resolve a theme brush by resource key, falling back to a fixed color if absent.</summary>
    private static Brush Brush(string themeResourceKey, Color fallback)
    {
        if (Application.Current.Resources.TryGetValue(themeResourceKey, out object value) && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }
}
