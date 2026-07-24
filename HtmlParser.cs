using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace MdPad;

/// <summary>A parsed HTML tag: <c>&lt;img src="x"&gt;</c> or <c>&lt;/p&gt;</c>.</summary>
/// <param name="Name">Tag name, lower-cased.</param>
/// <param name="IsClosing">True for <c>&lt;/name&gt;</c>.</param>
/// <param name="SelfClosing">True for <c>&lt;name /&gt;</c> or a void element.</param>
internal readonly record struct HtmlTag(
    string Name,
    bool IsClosing,
    bool SelfClosing,
    Dictionary<string, string> Attributes)
{
    public string? this[string attribute] =>
        Attributes.TryGetValue(attribute, out string? value) ? value : null;
}

/// <summary>One node of the parsed HTML tree: either an element or a run of text.</summary>
internal sealed class HtmlNode
{
    private HtmlNode()
    {
    }

    public static HtmlNode Element(string name, Dictionary<string, string> attributes) =>
        new() { Name = name, Attributes = attributes };

    public static HtmlNode Text(string text) =>
        new() { Name = TextName, TextContent = text };

    public const string TextName = "#text";

    public string Name { get; private init; } = TextName;

    /// <summary>Raw (entity-decoded) text for a text node; empty for elements.</summary>
    public string TextContent { get; private init; } = string.Empty;

    public bool IsText => Name == TextName;

    public Dictionary<string, string> Attributes { get; private init; } =
        new(StringComparer.OrdinalIgnoreCase);

    public List<HtmlNode> Children { get; } = new();

    public string? this[string attribute] =>
        Attributes.TryGetValue(attribute, out string? value) ? value : null;
}

/// <summary>
/// A deliberately small, forgiving HTML parser — enough for the HTML people actually
/// write inside Markdown (centred headers, <c>&lt;img&gt;</c>, <c>&lt;br&gt;</c>,
/// <c>&lt;kbd&gt;</c>, the occasional table). It is not a spec-compliant tokenizer:
/// unknown tags are kept as elements, mismatched closers are ignored, and script/style
/// content is dropped rather than shown.
/// </summary>
internal static class HtmlParser
{
    private static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input",
        "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Elements whose content is not markup and is not worth showing.</summary>
    private static readonly HashSet<string> RawTextElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "script", "style",
    };

    private static readonly HashSet<string> BlockElements = new(StringComparer.OrdinalIgnoreCase)
    {
        "address", "article", "aside", "blockquote", "center", "dd", "details", "div", "dl", "dt",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header",
        "hr", "li", "main", "nav", "ol", "p", "pre", "section", "summary", "table", "tbody",
        "td", "tfoot", "th", "thead", "tr", "ul",
    };

    public static bool IsVoid(string name) => VoidElements.Contains(name);

    public static bool IsBlock(string name) => BlockElements.Contains(name);

    /// <summary>Parse a fragment into a forest of nodes.</summary>
    public static List<HtmlNode> Parse(string? html)
    {
        var roots = new List<HtmlNode>();
        if (string.IsNullOrEmpty(html))
        {
            return roots;
        }

        var stack = new List<HtmlNode>();
        int i = 0;

        while (i < html.Length)
        {
            int lt = html.IndexOf('<', i);
            if (lt < 0)
            {
                AddText(html[i..]);
                break;
            }
            if (lt > i)
            {
                AddText(html[i..lt]);
            }

            if (SkipNonTag(html, lt, out int afterSkip))
            {
                i = afterSkip;
                continue;
            }

            if (!TryReadTag(html, lt, out HtmlTag tag, out int next))
            {
                // A stray '<' that starts nothing: keep it as text.
                AddText("<");
                i = lt + 1;
                continue;
            }
            i = next;

            if (tag.IsClosing)
            {
                CloseTag(tag.Name);
                continue;
            }

            if (RawTextElements.Contains(tag.Name))
            {
                i = SkipRawText(html, i, tag.Name);
                continue;
            }

            ImplyCloses(tag.Name);
            var element = HtmlNode.Element(tag.Name, tag.Attributes);
            Add(element);
            if (!tag.SelfClosing && !VoidElements.Contains(tag.Name))
            {
                stack.Add(element);
            }
        }

        return roots;

        void Add(HtmlNode node) => (stack.Count > 0 ? stack[^1].Children : roots).Add(node);

        void AddText(string text)
        {
            if (text.Length > 0)
            {
                Add(HtmlNode.Text(WebUtility.HtmlDecode(text)));
            }
        }

        void CloseTag(string name)
        {
            for (int s = stack.Count - 1; s >= 0; s--)
            {
                if (string.Equals(stack[s].Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    stack.RemoveRange(s, stack.Count - s);
                    return;
                }
            }
            // Unmatched closer: ignore it rather than unwinding the whole document.
        }

        // Minimal implied end tags, so <li>a<li>b and <td>x<td>y nest the way they read.
        void ImplyCloses(string name)
        {
            while (stack.Count > 0)
            {
                string top = stack[^1].Name;
                bool implied =
                    (top is "p" && BlockElements.Contains(name))
                    || (top is "li" && name is "li")
                    || (top is "dt" or "dd" && name is "dt" or "dd")
                    || (top is "td" or "th" && name is "td" or "th" or "tr")
                    || (top is "tr" && name is "tr")
                    || (top is "thead" or "tbody" or "tfoot" && name is "thead" or "tbody" or "tfoot");
                if (!implied)
                {
                    return;
                }
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    /// <summary>Parse a standalone tag string, as Markdig hands out for inline HTML.</summary>
    public static bool TryParseTag(string? text, out HtmlTag tag)
    {
        tag = default;
        if (string.IsNullOrEmpty(text) || text[0] != '<')
        {
            return false;
        }
        // Comments, doctypes and processing instructions carry no renderable content.
        return !SkipNonTag(text, 0, out _) && TryReadTag(text, 0, out tag, out _);
    }

    /// <summary>Collapse HTML whitespace (newlines and indentation) to single spaces.</summary>
    public static string CollapseWhitespace(string text)
    {
        var sb = new StringBuilder(text.Length);
        bool inSpace = false;
        foreach (char c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                inSpace = true;
                continue;
            }
            if (inSpace)
            {
                sb.Append(' ');
            }
            inSpace = false;
            sb.Append(c);
        }
        if (inSpace)
        {
            sb.Append(' ');
        }
        return sb.ToString();
    }

    /// <summary>Comments, doctypes, CDATA and processing instructions: consume and drop.</summary>
    private static bool SkipNonTag(string html, int start, out int next)
    {
        next = start;
        if (start + 1 >= html.Length || html[start] != '<')
        {
            return false;
        }

        char c = html[start + 1];
        if (c == '!' && html.AsSpan(start).StartsWith("<!--"))
        {
            int end = html.IndexOf("-->", start + 4, StringComparison.Ordinal);
            next = end < 0 ? html.Length : end + 3;
            return true;
        }
        if (c is '!' or '?')
        {
            int end = html.IndexOf('>', start + 2);
            next = end < 0 ? html.Length : end + 1;
            return true;
        }
        return false;
    }

    private static int SkipRawText(string html, int start, string tagName)
    {
        string closer = "</" + tagName;
        int end = html.IndexOf(closer, start, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
        {
            return html.Length;
        }
        int gt = html.IndexOf('>', end);
        return gt < 0 ? html.Length : gt + 1;
    }

    private static bool TryReadTag(string html, int start, out HtmlTag tag, out int next)
    {
        tag = default;
        next = start;

        int i = start + 1;
        bool closing = i < html.Length && html[i] == '/';
        if (closing)
        {
            i++;
        }

        int nameStart = i;
        while (i < html.Length && (char.IsLetterOrDigit(html[i]) || html[i] is '-' or ':' or '_'))
        {
            i++;
        }
        if (i == nameStart || !char.IsLetter(html[nameStart]))
        {
            return false;
        }
        string name = html[nameStart..i].ToLowerInvariant();

        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool selfClosing = false;

        while (i < html.Length)
        {
            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }
            if (i >= html.Length)
            {
                return false; // Unterminated tag.
            }
            if (html[i] == '/')
            {
                selfClosing = true;
                i++;
                continue;
            }
            if (html[i] == '>')
            {
                i++;
                break;
            }

            int attrStart = i;
            while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] is not ('=' or '>' or '/'))
            {
                i++;
            }
            if (i == attrStart)
            {
                i++; // Nothing consumed (stray character): step over it.
                continue;
            }
            string attrName = html[attrStart..i];

            while (i < html.Length && char.IsWhiteSpace(html[i]))
            {
                i++;
            }
            string value = string.Empty;
            if (i < html.Length && html[i] == '=')
            {
                i++;
                while (i < html.Length && char.IsWhiteSpace(html[i]))
                {
                    i++;
                }
                if (i < html.Length && html[i] is '"' or '\'')
                {
                    char quote = html[i++];
                    int valueStart = i;
                    while (i < html.Length && html[i] != quote)
                    {
                        i++;
                    }
                    value = html[valueStart..i];
                    if (i < html.Length)
                    {
                        i++; // closing quote
                    }
                }
                else
                {
                    int valueStart = i;
                    while (i < html.Length && !char.IsWhiteSpace(html[i]) && html[i] != '>')
                    {
                        i++;
                    }
                    value = html[valueStart..i];
                }
            }

            attributes[attrName] = WebUtility.HtmlDecode(value);
        }

        tag = new HtmlTag(name, closing, selfClosing || VoidElements.Contains(name), attributes);
        next = i;
        return true;
    }
}
