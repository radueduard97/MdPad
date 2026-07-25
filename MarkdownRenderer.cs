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
/// <param name="Anchor">GitHub-style slug this heading answers to in a <c>#link</c>.</param>
public sealed record OutlineEntry(int Level, string Text, int Line, FrameworkElement Element, string Anchor);

/// <summary>
/// The parts of the preview's appearance the settings pane owns. Body font and size
/// are set on the host panel and inherit down the tree; these are the values the
/// renderer sets explicitly and so has to be told about.
/// </summary>
public static class RenderOptions
{
    /// <summary>Family for code fences, <c>&lt;pre&gt;</c>, inline code and task-list markers.</summary>
    public static string MonoFamily { get; set; } = "Consolas";

    public static double CodeSize { get; set; } = 13;

    /// <summary>Body line height in DIPs; 0 lets the font decide.</summary>
    public static double LineHeight { get; set; } = 22;

    /// <summary>Multiplier over every size the renderer sets itself, tracking the body size.</summary>
    public static double Scale { get; set; } = 1.0;

    /// <summary>Accent for links, quote bars and the skill label; null follows the system accent.</summary>
    public static Color? Accent { get; set; }
}

/// <summary>
/// Renders a Markdown string into a tree of native WinUI controls (no WebView2).
/// One top-level Markdown block becomes one FrameworkElement in the host panel;
/// inline formatting is rendered with XAML <see cref="Inline"/> runs.
/// </summary>
public static partial class MarkdownRenderer
{
    internal static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        // Agent skill files (SKILL.md) and static-site pages open with a YAML block.
        // Without this it parses as a setext heading and swallows the document.
        .UseYamlFrontMatter()
        .UseSoftlineBreakAsHardlineBreak()
        .Build();

    // WinUI's FontFamily takes a single family name -- unlike CSS/WPF it does not
    // accept a comma-separated fallback list. Cached because one render creates a
    // great many code spans and each FontFamily is a resolved font resource.
    private static FontFamily _monoFont = new("Consolas");
    private static string _monoFamilyName = "Consolas";

    private static FontFamily MonoFont
    {
        get
        {
            if (!string.Equals(_monoFamilyName, RenderOptions.MonoFamily, StringComparison.Ordinal))
            {
                _monoFamilyName = RenderOptions.MonoFamily;
                _monoFont = new FontFamily(_monoFamilyName);
            }
            return _monoFont;
        }
    }

    /// <summary>A size the renderer sets itself, scaled to the configured body size.</summary>
    private static double Scaled(double size) => Math.Round(size * RenderOptions.Scale, 1);

    /// <summary>Folder of the document being rendered, for resolving relative links.</summary>
    private static string? _documentPath;

    /// <summary>Invoked when a link to a local file is clicked, with its path and any <c>#fragment</c>.</summary>
    private static Action<string, string?>? _openLocalFile;

    /// <summary>Invoked when a link into this document (<c>#section</c>) is clicked.</summary>
    private static Action<string>? _navigateAnchor;

    /// <summary>Slugs handed out during the current render, for disambiguating repeats.</summary>
    private static readonly Dictionary<string, int> SlugCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Parse <paramref name="markdown"/> and (re)populate <paramref name="host"/> with rendered controls.
    /// Returns the document's headings in order, for the outline sidebar.
    /// </summary>
    /// <param name="documentPath">Path of the file being rendered; relative links resolve against it.</param>
    /// <param name="openLocalFile">Called with a full path and fragment when a link to a local file is clicked.</param>
    /// <param name="navigateAnchor">Called with a slug when a link within this document is clicked.</param>
    public static IReadOnlyList<OutlineEntry> Render(
        string? markdown,
        Panel host,
        string? documentPath = null,
        Action<string, string?>? openLocalFile = null,
        Action<string>? navigateAnchor = null)
    {
        _documentPath = documentPath;
        _openLocalFile = openLocalFile;
        _navigateAnchor = navigateAnchor;
        SlugCounts.Clear();
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
            {
                FrameworkElement headingElement = BuildHeading(heading);
                host.Add(headingElement);
                string caption = InlineText(heading.Inline);
                outline.Add(new OutlineEntry(heading.Level, caption, heading.Line, headingElement, TakeSlug(caption)));
                break;
            }
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
                RenderHtmlBlock(html.Lines.ToString(), html.Line, host, outline);
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

    /// <summary>Font size for a heading level, shared by Markdown and HTML headings.</summary>
    private static double HeadingFontSize(int level) => Scaled(level switch
    {
        1 => 30,
        2 => 24,
        3 => 20,
        4 => 17,
        5 => 15,
        _ => 13,
    });

    private static FrameworkElement BuildHeading(HeadingBlock heading)
    {
        RichTextBlock tb = HeadingSurface(heading.Level, TextAlignment.Left, out Paragraph line);
        if (heading.Inline is not null)
        {
            RenderInlines(heading.Inline, line.Inlines);
        }
        return WithHeadingRule(tb, heading.Level);
    }

    /// <summary>
    /// The text surface for a heading of <paramref name="level"/>. A RichTextBlock rather
    /// than a TextBlock because a heading may contain an image, and only RichTextBlock
    /// accepts an InlineUIContainer.
    /// </summary>
    private static RichTextBlock HeadingSurface(int level, TextAlignment align, out Paragraph paragraph)
    {
        var tb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = HeadingFontSize(level),
            FontWeight = FontWeights.SemiBold,
            TextAlignment = align,
            Margin = new Thickness(0, level <= 2 ? 16 : 12, 0, 6),
        };
        paragraph = new Paragraph();
        tb.Blocks.Add(paragraph);
        return tb;
    }

    /// <summary>Underline a level 1-2 heading with a subtle divider, GitHub-style.</summary>
    private static FrameworkElement WithHeadingRule(FrameworkElement heading, int level)
    {
        if (level > 2)
        {
            return heading;
        }

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(heading);
        panel.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("DividerStrokeColorDefaultBrush", Color.FromArgb(40, 128, 128, 128)),
            Margin = new Thickness(0, 0, 0, 4),
        });
        return panel;
    }

    private static FrameworkElement BuildParagraph(ParagraphBlock paragraph)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var p = new Paragraph { LineHeight = RenderOptions.LineHeight };
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

    private static FrameworkElement BuildCodeBlock(CodeBlock code) => BuildCodeSurface(code.Lines.ToString());

    /// <summary>The bordered, scrollable monospace surface used by fenced code and &lt;pre&gt;.</summary>
    private static FrameworkElement BuildCodeSurface(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = MonoFont,
            FontSize = RenderOptions.CodeSize,
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
        List<FrontMatterField> fields = FrontMatter.Parse(block);

        string? name = FrontMatter.Find(fields, "name") ?? FrontMatter.Find(fields, "title");
        string? description = FrontMatter.Find(fields, "description") ?? FrontMatter.Find(fields, "summary");

        var stack = new StackPanel { Spacing = 6 };

        stack.Children.Add(new TextBlock
        {
            Text = name is null ? "FRONT MATTER" : "SKILL",
            FontSize = Scaled(11),
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("AccentTextFillColorPrimaryBrush", Color.FromArgb(255, 120, 170, 255)),
        });

        if (name is not null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = Scaled(26),
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
                FontSize = Scaled(13),
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
                    FontSize = Scaled(12),
                    FontWeight = FontWeights.SemiBold,
                    Foreground = Brush("TextFillColorTertiaryBrush", Color.FromArgb(255, 140, 140, 140)),
                };
                Grid.SetRow(key, i);
                Grid.SetColumn(key, 0);
                grid.Children.Add(key);

                var value = new TextBlock
                {
                    Text = rest[i].Value,
                    FontSize = RenderOptions.CodeSize,
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

    /// <param name="openHtml">
    /// Elements opened by an inline HTML tag and not yet closed. Markdig reports inline
    /// HTML one tag at a time, so the stack is what turns &lt;b&gt;…&lt;/b&gt; back into
    /// a span wrapping the inlines between the two tags.
    /// </param>
    private static void RenderInlines(ContainerInline container, InlineCollection target, List<HtmlOpenSpan>? openHtml = null)
    {
        openHtml ??= new List<HtmlOpenSpan>();

        foreach (MdInline inline in container)
        {
            // Content flows into the innermost open HTML element, if there is one.
            InlineCollection current = openHtml.Count > 0 ? openHtml[^1].Content : target;

            switch (inline)
            {
                case LiteralInline literal:
                    current.Add(new Run { Text = literal.Content.ToString() });
                    break;

                case EmphasisInline emphasis:
                    current.Add(BuildEmphasis(emphasis));
                    break;

                case CodeInline code:
                    current.Add(new Run
                    {
                        Text = code.Content,
                        FontFamily = MonoFont,
                        Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 200, 90, 90)),
                    });
                    break;

                case LinkInline { IsImage: true } image:
                    current.Add(BuildImageInline(image.Url, InlineText(image)));
                    break;

                // [![badge](img)](url): a Hyperlink cannot hold an image, so the image
                // itself becomes the clickable thing.
                case LinkInline { IsImage: false } imageLink when FirstImage(imageLink) is { } inner:
                    current.Add(BuildImageInline(inner.Url, InlineText(inner), href: imageLink.Url));
                    break;

                case LinkInline link:
                    current.Add(BuildLink(link));
                    break;

                case AutolinkInline auto:
                    var autoLink = WithAccent(new Hyperlink());
                    TrySetNavigateUri(autoLink, auto.Url);
                    autoLink.Inlines.Add(new Run { Text = auto.Url });
                    current.Add(autoLink);
                    break;

                case LineBreakInline lineBreak:
                    current.Add(lineBreak.IsHard ? new LineBreak() : new Run { Text = " " });
                    break;

                case TaskList task:
                    current.Add(new Run
                    {
                        Text = (task.Checked ? "☑" : "☐") + " ",
                        FontFamily = MonoFont,
                    });
                    break;

                case HtmlEntityInline entity:
                    current.Add(new Run { Text = entity.Transcoded.ToString() });
                    break;

                case HtmlInline html:
                    HandleHtmlInline(html.Tag, target, openHtml);
                    break;

                case ContainerInline nested:
                    RenderInlines(nested, target, openHtml);
                    break;

                default:
                    string? text = inline.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        current.Add(new Run { Text = text });
                    }
                    break;
            }
        }
    }

    /// <summary>The first image anywhere under <paramref name="container"/>, if any.</summary>
    private static LinkInline? FirstImage(ContainerInline container)
    {
        foreach (MdInline inline in container)
        {
            if (inline is LinkInline { IsImage: true } image)
            {
                return image;
            }
            if (inline is ContainerInline nested && FirstImage(nested) is { } found)
            {
                return found;
            }
        }
        return null;
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

    /// <summary>
    /// GitHub's heading slug: lowercased, punctuation dropped, spaces hyphenated. It is
    /// what a reader's <c>[Setup](#setup)</c> is written against, so MdPad has to agree
    /// with it rather than invent its own scheme.
    /// </summary>
    public static string Slugify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
            }
            else if (char.IsWhiteSpace(c))
            {
                sb.Append('-');
            }
            // Everything else — punctuation, emoji, markup leftovers — is dropped.
        }
        return sb.ToString().Trim('-');
    }

    /// <summary>Slug for a heading, suffixed like GitHub's when the same text appears twice.</summary>
    private static string TakeSlug(string text)
    {
        string slug = Slugify(text);
        if (slug.Length == 0)
        {
            return string.Empty;
        }

        int seen = SlugCounts.TryGetValue(slug, out int count) ? count : 0;
        SlugCounts[slug] = seen + 1;
        return seen == 0 ? slug : $"{slug}-{seen}";
    }

    private static MdInlineElement BuildLink(LinkInline link)
    {
        // A bare #fragment points inside this document: scroll rather than navigate.
        if (link.Url is { Length: > 1 } url && url[0] == '#')
        {
            var jump = WithAccent(new Hyperlink());
            string anchor = Uri.UnescapeDataString(url[1..]);
            jump.Click += (_, _) => _navigateAnchor?.Invoke(anchor);
            FillLinkText(link, jump.Inlines);
            return jump;
        }

        // A link to a file next to the document opens in a tab rather than a browser.
        if (SkillAnalyzer.IsLocalPath(link.Url))
        {
            return BuildLocalLink(link);
        }

        var hyperlink = WithAccent(new Hyperlink());
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
            string? anchor = SkillAnalyzer.AnchorOf(link.Url);
            var hyperlink = WithAccent(new Hyperlink());
            hyperlink.Click += (_, _) => _openLocalFile?.Invoke(resolved, anchor);
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
        // A configured accent stands in for every accent-derived system brush, so one
        // setting reaches quote bars, the skill label and links together.
        if (RenderOptions.Accent is { } accent && themeResourceKey.StartsWith("Accent", StringComparison.Ordinal))
        {
            return new SolidColorBrush(accent);
        }

        if (Application.Current.Resources.TryGetValue(themeResourceKey, out object value) && value is Brush brush)
        {
            return brush;
        }
        return new SolidColorBrush(fallback);
    }

    /// <summary>Links take the configured accent; left alone they follow the system one.</summary>
    private static Hyperlink WithAccent(Hyperlink hyperlink)
    {
        if (RenderOptions.Accent is { } accent)
        {
            hyperlink.Foreground = new SolidColorBrush(accent);
        }
        return hyperlink;
    }
}
