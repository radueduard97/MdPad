using System.Collections.Generic;
using System.Text;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

using Windows.UI;

namespace MdPad;

/// <summary>
/// Painting search matches into the preview.
///
/// The editor can only ever show one match at a time — a WinUI TextBox has a selection and
/// nothing else to mark text with — so it shows the match you are on, kept visible even
/// while the caret is in the find box. The preview is a control tree MdPad builds itself,
/// so every match in it can be highlighted at once, which is what makes a search legible:
/// not "12 in this file" but twelve marks you can see the shape of.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>Warm enough to read as a marker over both the light and the dark preview.</summary>
    private static readonly Color MatchColor = Color.FromArgb(112, 255, 190, 60);

    /// <summary>Stands in for the system selection colour if the theme does not offer one.</summary>
    private static readonly Color FallbackSelection = Color.FromArgb(140, 76, 141, 255);

    /// <summary>Surfaces highlighted last time, so they can be cleared without walking the tree again.</summary>
    private readonly List<DependencyObject> _highlighted = new();

    /// <summary>
    /// A TextBox hides its selection the moment it loses focus, which during a search is
    /// always — the caret is in the find box. Painting the unfocused selection is what
    /// turns "12 in this file" into a mark you can actually see, so the current match is
    /// visible while the query is still being typed.
    /// </summary>
    private void ApplyMatchSelectionBrush()
    {
        Color color = Settings.Current.Appearance.Accent == AccentSource.Custom
            ? ParseColor(Settings.Current.Appearance.AccentColor) ?? FallbackSelection
            : SelectionColor();

        // Muted against the focused selection, so which pane is being typed into stays legible.
        Editor.SelectionHighlightColorWhenNotFocused =
            new SolidColorBrush(Color.FromArgb(150, color.R, color.G, color.B));
    }

    private static Color SelectionColor() =>
        Application.Current.Resources.TryGetValue("TextControlSelectionHighlightColor", out object value)
        && value is SolidColorBrush brush
            ? brush.Color
            : FallbackSelection;

    /// <summary>
    /// Mark every occurrence of the current query in the preview. Called after each search
    /// and after each re-render, since a render builds a whole new control tree.
    /// </summary>
    private void HighlightMatches()
    {
        ClearHighlights();

        if (FindBar is null || FindBar.Visibility != Visibility.Visible || PreviewHost is null)
        {
            return;
        }

        string query = FindBox.Text;
        if (query.Length == 0)
        {
            return;
        }

        foreach (DependencyObject surface in TextSurfaces(PreviewHost))
        {
            Highlight(surface, query);
        }
    }

    private void ClearHighlights()
    {
        foreach (DependencyObject surface in _highlighted)
        {
            switch (surface)
            {
                case TextBlock block:
                    block.TextHighlighters.Clear();
                    break;
                case RichTextBlock rich:
                    rich.TextHighlighters.Clear();
                    break;
            }
        }
        _highlighted.Clear();
    }

    private void Highlight(DependencyObject surface, string query)
    {
        string? text = surface switch
        {
            TextBlock block => block.Text,
            RichTextBlock rich => PlainText(rich),
            _ => null,
        };

        if (text is null || text.Length < query.Length)
        {
            return;
        }

        var highlighter = new TextHighlighter { Background = new SolidColorBrush(MatchColor) };
        foreach (int at in DocumentSearch.Matches(text, query, MatchCase))
        {
            highlighter.Ranges.Add(new TextRange { StartIndex = at, Length = query.Length });
        }

        if (highlighter.Ranges.Count == 0)
        {
            return;
        }

        switch (surface)
        {
            case TextBlock block:
                block.TextHighlighters.Add(highlighter);
                break;
            case RichTextBlock rich:
                rich.TextHighlighters.Add(highlighter);
                break;
        }
        _highlighted.Add(surface);
    }

    /// <summary>
    /// The text of a RichTextBlock in the same character space its highlight ranges use.
    /// Null when the block holds an inline image: the placeholder that stands in for it
    /// would shift every range after it, and a highlight over the wrong word is worse than
    /// no highlight at all.
    /// </summary>
    private static string? PlainText(RichTextBlock rich)
    {
        var builder = new StringBuilder();
        foreach (Block block in rich.Blocks)
        {
            if (block is not Paragraph paragraph)
            {
                return null;
            }

            // Paragraphs within one block are separated the way the text layer counts them.
            if (builder.Length > 0)
            {
                builder.Append('\n');
            }

            if (!AppendInlines(paragraph.Inlines, builder))
            {
                return null;
            }
        }
        return builder.ToString();
    }

    private static bool AppendInlines(InlineCollection inlines, StringBuilder builder)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    builder.Append(run.Text);
                    break;
                case LineBreak:
                    builder.Append('\n');
                    break;
                // Bold, italic and hyperlinks are all Spans; their text counts as their own.
                case Span span:
                    if (!AppendInlines(span.Inlines, builder))
                    {
                        return false;
                    }
                    break;
                default:
                    return false; // An image, or something else that does not count as text.
            }
        }
        return true;
    }

    /// <summary>
    /// Every text surface under <paramref name="root"/>. The preview is walked through the
    /// containers the renderer actually builds rather than the visual tree, so highlights
    /// can be applied in the same pass that creates it, before layout has run.
    /// </summary>
    private static IEnumerable<DependencyObject> TextSurfaces(DependencyObject root)
    {
        switch (root)
        {
            case TextBlock or RichTextBlock:
                yield return root;
                break;

            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    foreach (DependencyObject surface in TextSurfaces(child))
                    {
                        yield return surface;
                    }
                }
                break;

            case Border { Child: { } child }:
                foreach (DependencyObject surface in TextSurfaces(child))
                {
                    yield return surface;
                }
                break;

            case ContentControl { Content: DependencyObject content }:
                foreach (DependencyObject surface in TextSurfaces(content))
                {
                    yield return surface;
                }
                break;
        }
    }
}
