using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

using Windows.UI;

using MdInlineElement = Microsoft.UI.Xaml.Documents.Inline;

namespace MdPad;

/// <summary>
/// Inline and block HTML support. Markdown authors reach for HTML when Markdown runs
/// out — centred title blocks, badge rows, <c>&lt;kbd&gt;</c>, <c>&lt;br&gt;</c>, the
/// odd table — so the preview renders those into the same native controls the rest of
/// the document uses instead of dumping the source.
/// </summary>
public static partial class MarkdownRenderer
{
    /// <summary>An HTML element left open by an inline tag, and the collection its content flows into.</summary>
    /// <param name="Href">Set for an open &lt;a&gt;, so an image inside it stays clickable.</param>
    private sealed record HtmlOpenSpan(string Name, InlineCollection Content, string? Href = null)
    {
        public bool IsLink => Name == "a";
    }

    // ---- Block-level HTML -----------------------------------------------------

    /// <param name="line">Source line the block starts on, so HTML headings can join the outline.</param>
    private static void RenderHtmlBlock(string html, int line, UIElementCollection host, List<OutlineEntry> outline)
    {
        RenderHtmlNodes(HtmlParser.Parse(html), host, outline, line, TextAlignment.Left);
    }

    private static void RenderHtmlNodes(
        IReadOnlyList<HtmlNode> nodes,
        UIElementCollection host,
        List<OutlineEntry> outline,
        int line,
        TextAlignment align)
    {
        var pending = new List<HtmlNode>();

        foreach (HtmlNode node in nodes)
        {
            if (!node.IsText && HtmlParser.IsBlock(node.Name))
            {
                Flush();
                RenderHtmlBlockNode(node, host, outline, line, align);
            }
            else
            {
                pending.Add(node);
            }
        }
        Flush();

        void Flush()
        {
            if (pending.Count == 0)
            {
                return;
            }
            List<HtmlNode> run = pending.ToList();
            pending.Clear();
            if (run.All(n => n.IsText && string.IsNullOrWhiteSpace(n.TextContent)))
            {
                return; // Formatting whitespace between block tags.
            }
            host.Add(BuildHtmlParagraph(run, align));
        }
    }

    private static void RenderHtmlBlockNode(
        HtmlNode node,
        UIElementCollection host,
        List<OutlineEntry> outline,
        int line,
        TextAlignment inherited)
    {
        TextAlignment align = AlignmentOf(node, inherited);

        switch (node.Name)
        {
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
            {
                int level = node.Name[1] - '0';
                FrameworkElement heading = BuildHtmlHeading(node, level, align);
                host.Add(heading);
                outline.Add(new OutlineEntry(level, HtmlText(node), line, heading));
                break;
            }

            case "hr":
                host.Add(BuildThematicBreak());
                break;

            case "pre":
                host.Add(BuildCodeSurface(HtmlText(node, preserveWhitespace: true).Trim('\r', '\n')));
                break;

            case "blockquote":
            {
                var inner = new StackPanel();
                RenderHtmlNodes(node.Children, inner.Children, outline, line, align);
                TrimLastMargin(inner);
                host.Add(new Border
                {
                    BorderThickness = new Thickness(4, 0, 0, 0),
                    BorderBrush = Brush("AccentFillColorDefaultBrush", Color.FromArgb(255, 90, 140, 220)),
                    Padding = new Thickness(12, 4, 8, 4),
                    Margin = new Thickness(0, 0, 0, 10),
                    Child = inner,
                });
                break;
            }

            case "ul" or "ol":
                host.Add(BuildHtmlList(node, outline, line, align));
                break;

            case "table":
                host.Add(BuildHtmlTable(node, outline, line, align));
                break;

            case "summary":
            {
                // The disclosure label of <details>: give it weight so it reads as a header.
                var rtb = BuildHtmlParagraph(node.Children, align);
                if (rtb is RichTextBlock text)
                {
                    text.FontWeight = FontWeights.SemiBold;
                }
                host.Add(rtb);
                break;
            }

            case "dd":
            {
                var indented = new StackPanel { Margin = new Thickness(20, 0, 0, 0) };
                RenderHtmlNodes(node.Children, indented.Children, outline, line, align);
                host.Add(indented);
                break;
            }

            // Grouping elements carry no visual of their own; their children do.
            default:
                RenderHtmlNodes(node.Children, host, outline, line, align);
                break;
        }
    }

    private static FrameworkElement BuildHtmlHeading(HtmlNode node, int level, TextAlignment align)
    {
        RichTextBlock tb = HeadingSurface(level, align, out Paragraph line);
        RenderHtmlInlines(node.Children, line.Inlines);
        TrimEdgeWhitespace(line.Inlines);
        return WithHeadingRule(tb, level);
    }

    private static FrameworkElement BuildHtmlParagraph(IReadOnlyList<HtmlNode> nodes, TextAlignment align)
    {
        var rtb = new RichTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            TextAlignment = align,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var paragraph = new Paragraph { LineHeight = 22 };
        RenderHtmlInlines(nodes, paragraph.Inlines);
        TrimEdgeWhitespace(paragraph.Inlines);
        rtb.Blocks.Add(paragraph);
        return rtb;
    }

    private static FrameworkElement BuildHtmlList(HtmlNode list, List<OutlineEntry> outline, int line, TextAlignment align)
    {
        bool ordered = list.Name == "ol";
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(4, 0, 0, 10) };

        int number = 1;
        if (ordered && int.TryParse(list["start"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int start))
        {
            number = start;
        }

        foreach (HtmlNode item in list.Children.Where(c => c.Name == "li"))
        {
            var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ordered ? 28 : 20) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var marker = new TextBlock
            {
                Text = ordered ? $"{number}." : "•",
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 6, 0),
                Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 120, 120, 120)),
            };
            Grid.SetColumn(marker, 0);
            row.Children.Add(marker);

            var content = new StackPanel();
            Grid.SetColumn(content, 1);
            RenderHtmlNodes(item.Children, content.Children, outline, line, AlignmentOf(item, align));
            TrimLastMargin(content);
            row.Children.Add(content);

            panel.Children.Add(row);
            number++;
        }
        return panel;
    }

    private static FrameworkElement BuildHtmlTable(HtmlNode table, List<OutlineEntry> outline, int line, TextAlignment align)
    {
        // <tr> may sit directly under <table> or inside thead/tbody/tfoot.
        List<HtmlNode> rows = table.Children
            .SelectMany(c => c.Name is "thead" or "tbody" or "tfoot" ? c.Children : new List<HtmlNode> { c })
            .Where(c => c.Name == "tr")
            .ToList();

        var grid = new Grid
        {
            BorderBrush = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(80, 128, 128, 128)),
            BorderThickness = new Thickness(1, 1, 0, 0),
            Margin = new Thickness(0, 0, 0, 12),
        };

        int columnCount = rows
            .Select(r => r.Children.Where(IsCell).Sum(SpanOf))
            .DefaultIfEmpty(0)
            .Max();
        for (int c = 0; c < columnCount; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        }
        foreach (HtmlNode _ in rows)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        var headerBg = Brush("LayerFillColorDefaultBrush", Color.FromArgb(50, 128, 128, 128));
        var cellStroke = Brush("CardStrokeColorDefaultBrush", Color.FromArgb(80, 128, 128, 128));

        for (int r = 0; r < rows.Count; r++)
        {
            int c = 0;
            foreach (HtmlNode cell in rows[r].Children.Where(IsCell))
            {
                var cellContent = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
                RenderHtmlNodes(cell.Children, cellContent.Children, outline, line, AlignmentOf(cell, align));
                TrimLastMargin(cellContent);

                var border = new Border
                {
                    BorderBrush = cellStroke,
                    BorderThickness = new Thickness(0, 0, 1, 1),
                    Background = cell.Name == "th" ? headerBg : null,
                    Child = cellContent,
                };
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c);
                Grid.SetColumnSpan(border, Math.Min(SpanOf(cell), Math.Max(1, columnCount - c)));
                grid.Children.Add(border);
                c += SpanOf(cell);
            }
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = grid,
            Margin = new Thickness(0, 0, 0, 2),
        };

        static bool IsCell(HtmlNode node) => node.Name is "td" or "th";

        static int SpanOf(HtmlNode cell) =>
            int.TryParse(cell["colspan"], NumberStyles.Integer, CultureInfo.InvariantCulture, out int span) && span > 1
                ? span
                : 1;
    }

    // ---- Inline-level HTML ----------------------------------------------------

    /// <param name="href">Link target inherited from an enclosing &lt;a&gt;, for images inside it.</param>
    private static void RenderHtmlInlines(IReadOnlyList<HtmlNode> nodes, InlineCollection target, string? href = null)
    {
        foreach (HtmlNode node in nodes)
        {
            if (node.IsText)
            {
                string text = HtmlParser.CollapseWhitespace(node.TextContent);
                if (text.Length > 0)
                {
                    target.Add(new Run { Text = text });
                }
                continue;
            }

            switch (node.Name)
            {
                case "br":
                    target.Add(new LineBreak());
                    break;

                case "img":
                    target.Add(BuildImageInline(node["src"], node["alt"], node["width"], node["height"], href));
                    break;

                case "wbr":
                    break;

                case "a":
                {
                    // A Hyperlink cannot hold an image, so a badge-style link becomes a
                    // clickable image instead of a text hyperlink.
                    if (ContainsImage(node))
                    {
                        RenderHtmlInlines(node.Children, target, node["href"]);
                        break;
                    }
                    MdInlineElement link = BuildHtmlLink(node["href"]);
                    RenderHtmlInlines(node.Children, InlinesOf(link));
                    target.Add(link);
                    break;
                }

                default:
                {
                    Span? styled = StyleSpanFor(node.Name);
                    if (styled is null)
                    {
                        RenderHtmlInlines(node.Children, target, href); // Unstyled wrapper: keep the content.
                    }
                    else
                    {
                        RenderHtmlInlines(node.Children, styled.Inlines, href);
                        target.Add(styled);
                    }
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Handle one raw tag inside a Markdown paragraph. Markdig hands out inline HTML one
    /// tag at a time with the text between them as ordinary inlines, so an opening tag
    /// pushes a span that later inlines flow into and its closing tag pops it.
    /// </summary>
    private static void HandleHtmlInline(string? rawTag, InlineCollection root, List<HtmlOpenSpan> open)
    {
        if (!HtmlParser.TryParseTag(rawTag, out HtmlTag tag))
        {
            return; // Comment, doctype or malformed: nothing to draw.
        }

        InlineCollection current = open.Count > 0 ? open[^1].Content : root;

        if (tag.IsClosing)
        {
            for (int i = open.Count - 1; i >= 0; i--)
            {
                if (open[i].Name == tag.Name)
                {
                    open.RemoveRange(i, open.Count - i);
                    return;
                }
            }
            return;
        }

        switch (tag.Name)
        {
            case "br":
                current.Add(new LineBreak());
                return;
            case "img":
            {
                // Inside an <a>, the image has to sit outside the Hyperlink that tag opened.
                int link = open.FindLastIndex(o => o.IsLink);
                InlineCollection destination = link < 0
                    ? current
                    : link == 0 ? root : open[link - 1].Content;
                destination.Add(BuildImageInline(
                    tag["src"], tag["alt"], tag["width"], tag["height"], link < 0 ? null : open[link].Href));
                return;
            }
            case "wbr":
                return;
        }

        if (tag.SelfClosing)
        {
            return;
        }

        if (tag.Name == "a")
        {
            MdInlineElement anchor = BuildHtmlLink(tag["href"]);
            current.Add(anchor);
            open.Add(new HtmlOpenSpan("a", InlinesOf(anchor), tag["href"]));
            return;
        }

        // Unknown tags still get a span so their content keeps flowing and </tag> pops cleanly.
        Span span = StyleSpanFor(tag.Name) ?? new Span();
        current.Add(span);
        open.Add(new HtmlOpenSpan(tag.Name, span.Inlines));
    }

    /// <summary>Map an inline HTML tag to its formatting, or null if it carries none.</summary>
    private static Span? StyleSpanFor(string name)
    {
        switch (name)
        {
            case "b" or "strong":
                return new Span { FontWeight = FontWeights.Bold };
            case "i" or "em" or "cite" or "dfn" or "var" or "address":
                return new Span { FontStyle = Windows.UI.Text.FontStyle.Italic };
            case "u" or "ins":
                return new Span { TextDecorations = Windows.UI.Text.TextDecorations.Underline };
            case "s" or "del" or "strike":
                return new Span { TextDecorations = Windows.UI.Text.TextDecorations.Strikethrough };
            case "code" or "samp" or "tt":
                return new Span
                {
                    FontFamily = MonoFont,
                    Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 200, 90, 90)),
                };
            case "kbd":
                return new Span
                {
                    FontFamily = MonoFont,
                    Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 170, 170, 170)),
                };
            // Inlines cannot carry a background in WinUI, so a highlight becomes accent text.
            case "mark":
                return new Span
                {
                    Foreground = Brush("AccentTextFillColorPrimaryBrush", Color.FromArgb(255, 120, 170, 255)),
                };
            // No baseline shift on a Run either; smaller text is the closest honest fallback.
            case "small" or "sub" or "sup":
                return new Span { FontSize = 10 };
            default:
                return null;
        }
    }

    private static MdInlineElement BuildHtmlLink(string? href)
    {
        if (SkillAnalyzer.IsLocalPath(href))
        {
            string? resolved = SkillAnalyzer.ResolveLocalPath(href, _documentPath);
            if (resolved is not null && System.IO.File.Exists(resolved))
            {
                var local = new Hyperlink();
                local.Click += (_, _) => _openLocalFile?.Invoke(resolved);
                return local;
            }
        }

        var hyperlink = new Hyperlink();
        TrySetNavigateUri(hyperlink, href);
        return hyperlink;
    }

    private static InlineCollection InlinesOf(MdInlineElement inline) => inline switch
    {
        Hyperlink hyperlink => hyperlink.Inlines,
        Span span => span.Inlines,
        _ => new Span().Inlines,
    };

    /// <summary>
    /// An image inline, shared by Markdown <c>![]()</c> and HTML <c>&lt;img&gt;</c>.
    /// Relative sources resolve against the document's folder; if nothing loads, the
    /// alt text stands in so the reader still knows something was there.
    /// </summary>
    /// <param name="href">If set, the image is wrapped in a button that follows the link.</param>
    private static MdInlineElement BuildImageInline(
        string? src,
        string? alt,
        string? width = null,
        string? height = null,
        string? href = null)
    {
        Uri? uri = ResolveImageUri(src);
        if (uri is null)
        {
            return new Run
            {
                Text = string.IsNullOrWhiteSpace(alt) ? "🖼" : alt,
                Foreground = Brush("TextFillColorSecondaryBrush", Color.FromArgb(255, 140, 140, 140)),
            };
        }

        var image = new Image
        {
            Stretch = Stretch.Uniform,
            MaxHeight = 480,
            MaxWidth = 780,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        bool sized = false;
        if (TryParseLength(width, out double w))
        {
            image.Width = w;
            sized = true;
        }
        if (TryParseLength(height, out double h))
        {
            image.Height = h;
            image.MaxHeight = Math.Max(h, 480);
            sized = true;
        }
        LoadImage(image, uri, sized);
        ToolTipService.SetToolTip(image, string.IsNullOrWhiteSpace(alt) ? null : alt);

        UIElement child = href is null ? image : WrapInLinkButton(image, href, alt);
        return new InlineUIContainer { Child = child };
    }

    /// <summary>Make an image follow a link, since WinUI's Hyperlink accepts text only.</summary>
    private static UIElement WrapInLinkButton(Image image, string href, string? alt)
    {
        var button = new HyperlinkButton
        {
            Content = image,
            Padding = new Thickness(0),
            Margin = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = null,
        };
        ToolTipService.SetToolTip(button, string.IsNullOrWhiteSpace(alt) ? href : $"{alt} — {href}");

        string? resolved = SkillAnalyzer.IsLocalPath(href)
            ? SkillAnalyzer.ResolveLocalPath(href, _documentPath)
            : null;
        if (resolved is not null && System.IO.File.Exists(resolved))
        {
            button.Click += (_, _) => _openLocalFile?.Invoke(resolved);
        }
        else if (Uri.TryCreate(href, UriKind.Absolute, out Uri? target))
        {
            button.NavigateUri = target;
        }
        return button;
    }

    /// <summary>True if the subtree holds an image, which a text Hyperlink cannot contain.</summary>
    private static bool ContainsImage(HtmlNode node) =>
        node.Children.Any(c => !c.IsText && (c.Name == "img" || ContainsImage(c)));

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Point <paramref name="image"/> at <paramref name="uri"/>. Bitmaps load through
    /// WinUI directly; SVG needs a hand, because a shields.io badge is SVG served from a
    /// URL with no extension, and a bitmap decoder cannot tell you that in advance.
    /// </summary>
    /// <param name="sized">True if the markup already gave the image a width or height.</param>
    private static void LoadImage(Image image, Uri uri, bool sized)
    {
        if (uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            LoadSvg(image, uri, sized);
            return;
        }

        var bitmap = new BitmapImage(uri);
        bool retried = false;
        void RetryAsSvg()
        {
            if (retried)
            {
                return;
            }
            retried = true;
            LoadSvg(image, uri, sized);
        }
        bitmap.ImageFailed += (_, _) => RetryAsSvg();
        image.ImageFailed += (_, _) => RetryAsSvg();
        image.Source = bitmap;
    }

    private static async void LoadSvg(Image image, Uri uri, bool sized)
    {
        try
        {
            string markup = uri.IsFile
                ? System.IO.File.ReadAllText(uri.LocalPath)
                : await Http.GetStringAsync(uri);
            if (markup.IndexOf("<svg", StringComparison.OrdinalIgnoreCase) < 0)
            {
                image.Visibility = Visibility.Collapsed; // Not an image we can draw.
                return;
            }

            var svg = new SvgImageSource();
            using var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes(markup));
            if (await svg.SetSourceAsync(stream.AsRandomAccessStream()) != SvgImageSourceLoadStatus.Success)
            {
                image.Visibility = Visibility.Collapsed;
                return;
            }

            // An SVG has no pixel size for layout to fall back on, so read its own.
            if (!sized && TryReadSvgSize(markup, out double w, out double h))
            {
                image.Width = w;
                image.Height = h;
            }
            image.Source = svg;
        }
        catch (Exception)
        {
            // A preview should never take the window down over an image it cannot fetch.
            image.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Read the intrinsic size off the root &lt;svg&gt; tag, or its viewBox.</summary>
    private static bool TryReadSvgSize(string markup, out double width, out double height)
    {
        width = height = 0;
        int open = markup.IndexOf("<svg", StringComparison.OrdinalIgnoreCase);
        if (open < 0)
        {
            return false;
        }
        int close = markup.IndexOf('>', open);
        if (!HtmlParser.TryParseTag(markup[open..(close < 0 ? markup.Length : close + 1)], out HtmlTag tag))
        {
            return false;
        }

        if (TryParseLength(tag["width"], out width) && TryParseLength(tag["height"], out height))
        {
            return true;
        }

        string[] box = (tag["viewBox"] ?? string.Empty).Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        return box.Length == 4
            && TryParseLength(box[2], out width)
            && TryParseLength(box[3], out height);
    }

    private static Uri? ResolveImageUri(string? src)
    {
        if (string.IsNullOrWhiteSpace(src))
        {
            return null;
        }
        if (SkillAnalyzer.IsLocalPath(src))
        {
            string? resolved = SkillAnalyzer.ResolveLocalPath(src, _documentPath);
            return resolved is not null && System.IO.File.Exists(resolved) ? new Uri(resolved) : null;
        }
        return Uri.TryCreate(src, UriKind.Absolute, out Uri? uri) ? uri : null;
    }

    /// <summary>Read an HTML length attribute: bare numbers and <c>px</c> only, never percentages.</summary>
    private static bool TryParseLength(string? value, out double length)
    {
        length = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }
        string trimmed = value.Trim();
        if (trimmed.EndsWith("px", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^2];
        }
        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out length) && length > 0;
    }

    // ---- Shared helpers -------------------------------------------------------

    private static TextAlignment AlignmentOf(HtmlNode node, TextAlignment inherited)
    {
        if (node.Name == "center")
        {
            return TextAlignment.Center;
        }

        string? align = node["align"];
        if (align is null && node["style"] is { } style)
        {
            int at = style.IndexOf("text-align", StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                int colon = style.IndexOf(':', at);
                if (colon >= 0)
                {
                    int end = style.IndexOf(';', colon);
                    align = (end < 0 ? style[(colon + 1)..] : style[(colon + 1)..end]).Trim();
                }
            }
        }

        return align?.Trim().ToLowerInvariant() switch
        {
            "center" or "centre" or "middle" => TextAlignment.Center,
            "right" or "end" => TextAlignment.Right,
            "left" or "start" => TextAlignment.Left,
            "justify" => TextAlignment.Justify,
            _ => inherited,
        };
    }

    /// <summary>Flatten an HTML subtree to plain text (outline captions, &lt;pre&gt; content).</summary>
    private static string HtmlText(HtmlNode node, bool preserveWhitespace = false)
    {
        var sb = new System.Text.StringBuilder();
        Append(node);
        string text = sb.ToString();
        return preserveWhitespace ? text : HtmlParser.CollapseWhitespace(text).Trim();

        void Append(HtmlNode current)
        {
            foreach (HtmlNode child in current.Children)
            {
                if (child.IsText)
                {
                    sb.Append(child.TextContent);
                }
                else if (child.Name == "br")
                {
                    sb.Append('\n');
                }
                else
                {
                    Append(child);
                }
            }
        }
    }

    /// <summary>Drop the formatting whitespace HTML leaves at the edges of a paragraph.</summary>
    private static void TrimEdgeWhitespace(InlineCollection inlines)
    {
        if (inlines.Count > 0 && inlines[0] is Run first)
        {
            first.Text = first.Text.TrimStart();
        }
        if (inlines.Count > 0 && inlines[^1] is Run last)
        {
            last.Text = last.Text.TrimEnd();
        }
    }
}
