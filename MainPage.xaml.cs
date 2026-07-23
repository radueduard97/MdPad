using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;

namespace MdPad;

/// <summary>One row in the outline sidebar; wraps an <see cref="OutlineEntry"/> with its presentation.</summary>
public sealed class OutlineItem
{
    public OutlineItem(OutlineEntry entry) => Entry = entry;

    public OutlineEntry Entry { get; }

    public string Text => string.IsNullOrWhiteSpace(Entry.Text) ? "(untitled)" : Entry.Text;

    /// <summary>Nested headings step to the right so the document shape is readable at a glance.</summary>
    public Thickness Indent => new(Math.Min(Entry.Level - 1, 4) * 14, 0, 0, 0);

    public double FontSize => Entry.Level <= 1 ? 13 : 12;

    public Windows.UI.Text.FontWeight Weight => Entry.Level <= 2 ? FontWeights.SemiBold : FontWeights.Normal;

    public double Opacity => Entry.Level <= 2 ? 1.0 : 0.8;
}

/// <summary>One row in the sidebar's reference list.</summary>
public sealed class ReferenceItem
{
    public ReferenceItem(DocumentReference reference) => Reference = reference;

    public DocumentReference Reference { get; }

    public string Display => Reference.Display;

    public string TokenText => Reference.Tokens > 0 ? SkillAnalyzer.Format(Reference.Tokens) : string.Empty;

    public string Glyph => Reference.State switch
    {
        ReferenceState.Linked => "\uE7C3",   // Page
        ReferenceState.Missing => "\uE783",  // Error
        _ => "\uE7BA",                       // Warning
    };

    public Brush Accent => new SolidColorBrush(Reference.State switch
    {
        ReferenceState.Linked => Color.FromArgb(255, 130, 170, 255),
        ReferenceState.Missing => Color.FromArgb(255, 230, 100, 100),
        _ => Color.FromArgb(255, 220, 180, 90),
    });

    public string Tip => Reference.State switch
    {
        ReferenceState.Linked => $"{Reference.FullPath}\nLinked · ~{SkillAnalyzer.Format(Reference.Tokens)} tokens when read",
        ReferenceState.Missing => $"{Reference.FullPath}\nLinked, but no file at this path",
        _ => $"{Reference.FullPath}\nIn the folder, but nothing links to it",
    };
}

public sealed partial class MainPage : Page
{
    private const string AppName = "MdPad";

    /// <summary>Deepest heading level shown in the outline.</summary>
    private const int MaxOutlineLevel = 4;

    /// <summary>VK_OEM_5 — the backslash key has no <see cref="VirtualKey"/> member.</summary>
    private const VirtualKey BackslashKey = (VirtualKey)0xDC;

    private readonly DispatcherTimer _renderDebounce;
    private readonly ObservableCollection<OutlineItem> _outline = new();
    private readonly ObservableCollection<MdDocument> _documents = new();
    private readonly ObservableCollection<ReferenceItem> _references = new();

    private MdDocument _current = new();
    private SkillBudget _budget = SkillBudget.Empty;
    private int _words;
    private int _chars;
    private bool _suppressOutlineNavigation;
    private bool _switchingTabs;

    public MainPage()
    {
        InitializeComponent();

        OutlineList.ItemsSource = _outline;
        ReferenceList.ItemsSource = _references;

        _renderDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _renderDebounce.Tick += (_, _) =>
        {
            _renderDebounce.Stop();
            RenderPreview();
        };

        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, () => NewTab());
        AddAccelerator(VirtualKey.T, VirtualKeyModifiers.Control, () => NewTab());
        AddAccelerator(VirtualKey.W, VirtualKeyModifiers.Control, async () => await CloseTabAsync(_current));
        // The TabView's own Ctrl+Tab never fires while the editor holds focus.
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control, () => CycleTab(1));
        AddAccelerator(VirtualKey.Tab, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, () => CycleTab(-1));
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, async () => await OpenAsync());
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, async () => await SaveAsync());
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, async () => await SaveAsAsync());

        AddAccelerator(VirtualKey.B, VirtualKeyModifiers.Control, () => Wrap("**", "**", "bold text"));
        AddAccelerator(VirtualKey.I, VirtualKeyModifiers.Control, () => Wrap("*", "*", "italic text"));
        AddAccelerator(VirtualKey.K, VirtualKeyModifiers.Control, InsertLink);
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => ApplyHeading(1));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, () => ApplyHeading(2));
        AddAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control, () => ApplyHeading(3));
        AddAccelerator(BackslashKey, VirtualKeyModifiers.Control, ToggleOutline);

        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AttachTabs();

        // Launched with a file ("Open with MdPad")? Start on it instead of the sample.
        bool hasStartupFile = App.StartupFile is not null;
        var first = hasStartupFile
            ? new MdDocument()
            : new MdDocument { Text = WelcomeSample, SavedText = Normalize(WelcomeSample) };

        _documents.Add(first);
        SelectDocument(first);

        if (App.StartupFile is { } path)
        {
            await OpenPathAsync(path);
        }
        else
        {
            Editor.Focus(FocusState.Programmatic);
        }
    }

    // ---- Tabs -----------------------------------------------------------------

    private TabView? _tabs;

    private void AttachTabs()
    {
        if (App.MainWindow is not { } window)
        {
            return;
        }

        _tabs = window.Tabs;
        _tabs.TabItemsSource = _documents;
        _tabs.SelectionChanged += OnTabSelectionChanged;
        _tabs.AddTabButtonClick += (_, _) => NewTab();
        _tabs.TabCloseRequested += async (_, args) =>
        {
            if (args.Item is MdDocument document)
            {
                await CloseTabAsync(document);
            }
        };
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_tabs?.SelectedItem is MdDocument document && !ReferenceEquals(document, _current))
        {
            SwitchTo(document);
        }
    }

    private MdDocument NewTab(string text = "", string? path = null)
    {
        var document = new MdDocument { Text = text, SavedText = Normalize(text), Path = path };
        _documents.Add(document);
        SelectDocument(document);
        Editor.Focus(FocusState.Programmatic);
        return document;
    }

    /// <summary>Move <paramref name="delta"/> tabs along, wrapping at both ends.</summary>
    private void CycleTab(int delta)
    {
        if (_documents.Count < 2)
        {
            return;
        }

        int index = _documents.IndexOf(_current);
        int next = ((index + delta) % _documents.Count + _documents.Count) % _documents.Count;
        SelectDocument(_documents[next]);
    }

    private void SelectDocument(MdDocument document)
    {
        if (_tabs is not null)
        {
            // Assigning SelectedItem raises SelectionChanged, which does the switch.
            _tabs.SelectedItem = document;
        }

        if (!ReferenceEquals(_current, document) || _documents.Count == 1)
        {
            SwitchTo(document);
        }
    }

    /// <summary>
    /// Park the outgoing document's state and load the incoming one into the shared editor.
    /// Pass <paramref name="stashCurrent"/> = false when the caller has just filled the
    /// document in — stashing would overwrite it with whatever the editor still shows.
    /// </summary>
    private void SwitchTo(MdDocument document, bool stashCurrent = true)
    {
        if (stashCurrent)
        {
            StashCurrent();
        }

        _switchingTabs = true;
        _current = document;
        Editor.Text = document.Text;
        Editor.Select(Math.Min(document.SelectionStart, Editor.Text.Length), 0);
        _switchingTabs = false;

        // A TextChanged raised during the swap can leave the flag stale; settle it here.
        document.IsDirty = !string.Equals(Normalize(Editor.Text), document.SavedText, StringComparison.Ordinal);

        _renderDebounce.Stop();
        RenderPreview();
        UpdateCounts();
        UpdateTitle();
        PreviewScroller.ChangeView(null, document.PreviewOffset, null, disableAnimation: true);
    }

    /// <summary>Copy live editor state back into the current document.</summary>
    private void StashCurrent()
    {
        if (_switchingTabs)
        {
            return;
        }
        _current.Text = Editor.Text;
        _current.SelectionStart = Editor.SelectionStart;
        _current.PreviewOffset = PreviewScroller.VerticalOffset;
    }

    private async Task CloseTabAsync(MdDocument document)
    {
        if (!await ConfirmDiscardAsync(document))
        {
            return;
        }

        int index = _documents.IndexOf(document);
        _documents.Remove(document);

        if (_documents.Count == 0)
        {
            // Closing the last tab leaves an empty document rather than the window.
            NewTab();
            return;
        }

        if (ReferenceEquals(document, _current))
        {
            SelectDocument(_documents[Math.Clamp(index, 0, _documents.Count - 1)]);
        }
    }

    // ---- Editor / preview -----------------------------------------------------

    private void OnEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_switchingTabs)
        {
            _current.Text = Editor.Text;
            _current.IsDirty = !string.Equals(Normalize(Editor.Text), _current.SavedText, StringComparison.Ordinal);
            UpdateTitle();
        }

        UpdateCounts();
        _renderDebounce.Stop();
        _renderDebounce.Start();
    }

    private void RenderPreview()
    {
        try
        {
            IReadOnlyList<OutlineEntry> headings = MarkdownRenderer.Render(
                Editor.Text,
                PreviewHost,
                _current.Path,
                path => _ = OpenPathAsync(path));

            UpdateOutline(headings);
            UpdateBudget();
        }
        catch (Exception ex)
        {
            PreviewHost.Children.Clear();
            PreviewHost.Children.Add(new TextBlock
            {
                Text = "Preview error: " + ex.Message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            });
        }
    }

    private void UpdateCounts()
    {
        string text = Editor.Text;
        _chars = text.Length;
        _words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        UpdateStatusLine();
    }

    // ---- Context budget -------------------------------------------------------

    /// <summary>Re-measure what this document costs an agent, and what it points at.</summary>
    private void UpdateBudget()
    {
        _budget = SkillAnalyzer.Analyze(Editor.Text, _current.Path);
        UpdateStatusLine();
        UpdateReferenceList();
        BuildBudgetBreakdown();
    }

    private void UpdateStatusLine()
    {
        // For a skill, the tier split is the number that matters; for anything else,
        // plain counts plus a single token estimate.
        CountText.Text = _budget.HasFrontMatter
            ? $"~{SkillAnalyzer.Format(_budget.AlwaysTokens)} always · " +
              $"~{SkillAnalyzer.Format(_budget.InvokeTokens)} on invoke · " +
              $"~{SkillAnalyzer.Format(_budget.OnDemandTokens)} on demand"
            : $"{_words} words · {_chars} chars · ~{SkillAnalyzer.Format(SkillAnalyzer.EstimateTokens(Editor.Text))} tokens";
    }

    private void UpdateReferenceList()
    {
        _references.Clear();
        foreach (DocumentReference reference in _budget.References)
        {
            _references.Add(new ReferenceItem(reference));
        }

        Visibility visibility = _references.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ReferencesHeader.Visibility = visibility;
        ReferenceList.Visibility = visibility;

        string suffix = _budget.MissingCount > 0 ? $" · {_budget.MissingCount} missing" : string.Empty;
        ReferencesHeader.Text = "REFERENCES" + suffix;
    }

    /// <summary>Fill the status-bar flyout with the per-tier and per-file breakdown.</summary>
    private void BuildBudgetBreakdown()
    {
        BudgetPanel.Children.Clear();

        BudgetPanel.Children.Add(new TextBlock
        {
            Text = "Estimated context cost",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
        });

        if (_budget.HasFrontMatter)
        {
            AddBudgetRow("Always loaded", _budget.AlwaysTokens, "name + description, in every prompt");
            AddBudgetRow("On invoke", _budget.InvokeTokens, "this file, when the skill runs");
            AddBudgetRow("On demand", _budget.OnDemandTokens, $"{_budget.LinkedCount} linked file(s), only if read");
        }
        else
        {
            AddBudgetRow("This file", SkillAnalyzer.EstimateTokens(Editor.Text), $"{_words} words");
            if (_budget.References.Count > 0)
            {
                AddBudgetRow("Linked files", _budget.OnDemandTokens, $"{_budget.LinkedCount} file(s)");
            }
        }

        var linked = _budget.References.Where(r => r.State == ReferenceState.Linked).Take(10).ToList();
        if (linked.Count > 0)
        {
            BudgetPanel.Children.Add(new Border
            {
                Height = 1,
                Margin = new Thickness(0, 10, 0, 8),
                Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
            });

            foreach (DocumentReference reference in linked)
            {
                AddBudgetRow(reference.Display, reference.Tokens, null, dim: true);
            }
        }

        if (_budget.MissingCount > 0 || _budget.OrphanCount > 0)
        {
            var notes = new List<string>();
            if (_budget.MissingCount > 0)
            {
                notes.Add($"{_budget.MissingCount} broken link(s)");
            }
            if (_budget.OrphanCount > 0)
            {
                notes.Add($"{_budget.OrphanCount} unreferenced file(s)");
            }

            BudgetPanel.Children.Add(new TextBlock
            {
                Text = string.Join(" · ", notes),
                FontSize = 12,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        }

        BudgetPanel.Children.Add(new TextBlock
        {
            Text = "Estimates assume ~4 characters per token.",
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
        });
    }

    private void AddBudgetRow(string label, int tokens, string? note, bool dim = false)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2), ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = (Brush)Application.Current.Resources[dim ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush"],
        });
        if (note is not null)
        {
            left.Children.Add(new TextBlock
            {
                Text = note,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            });
        }
        Grid.SetColumn(left, 0);
        row.Children.Add(left);

        var value = new TextBlock
        {
            Text = "~" + SkillAnalyzer.Format(tokens),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);

        BudgetPanel.Children.Add(row);
    }

    private void OnReferenceClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ReferenceItem { Reference: { State: not ReferenceState.Missing, FullPath: { } path } })
        {
            _ = OpenPathAsync(path);
        }
    }

    // ---- Outline sidebar ------------------------------------------------------

    private void UpdateOutline(IReadOnlyList<OutlineEntry> headings)
    {
        // Rebuilding drops the selection; don't let that scroll the preview around.
        _suppressOutlineNavigation = true;
        _outline.Clear();
        foreach (OutlineEntry entry in headings.Where(h => h.Level <= MaxOutlineLevel))
        {
            _outline.Add(new OutlineItem(entry));
        }
        _suppressOutlineNavigation = false;

        OutlineEmptyText.Visibility = _outline.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOutlineSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressOutlineNavigation || OutlineList.SelectedItem is not OutlineItem item)
        {
            return;
        }

        item.Entry.Element.StartBringIntoView(new BringIntoViewOptions
        {
            VerticalAlignmentRatio = 0,
            AnimationDesired = true,
        });

        // Park the caret on the heading so the editor is in the right place too.
        int offset = OffsetOfLine(Editor.Text, item.Entry.Line);
        Editor.Select(offset, 0);
    }

    private void OnToggleOutline(object sender, RoutedEventArgs e) => SetOutlineVisible(OutlineMenuItem.IsChecked);

    private void ToggleOutline() => SetOutlineVisible(!OutlineMenuItem.IsChecked);

    private void SetOutlineVisible(bool visible)
    {
        OutlineMenuItem.IsChecked = visible;
        Sidebar.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.Width = visible ? new GridLength(248) : new GridLength(0);
    }

    /// <summary>Character offset of a zero-based line, tolerating \r, \n and \r\n breaks.</summary>
    private static int OffsetOfLine(string text, int line)
    {
        int offset = 0;
        for (int current = 0; current < line; current++)
        {
            int next = text.IndexOfAny(new[] { '\r', '\n' }, offset);
            if (next < 0)
            {
                return Math.Min(offset, text.Length);
            }
            if (next + 1 < text.Length && text[next] == '\r' && text[next + 1] == '\n')
            {
                next++;
            }
            offset = next + 1;
        }
        return Math.Min(offset, text.Length);
    }

    // ---- Markdown formatting toolbar ------------------------------------------

    private void OnHeading(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && int.TryParse(tag, out int level))
        {
            ApplyHeading(level);
        }
    }

    private void OnBold(object sender, RoutedEventArgs e) => Wrap("**", "**", "bold text");

    private void OnItalic(object sender, RoutedEventArgs e) => Wrap("*", "*", "italic text");

    private void OnStrikethrough(object sender, RoutedEventArgs e) => Wrap("~~", "~~", "struck text");

    private void OnInlineCode(object sender, RoutedEventArgs e) => Wrap("`", "`", "code");

    private void OnLink(object sender, RoutedEventArgs e) => InsertLink();

    private void OnBulletList(object sender, RoutedEventArgs e) => ApplyLinePrefix((_, _) => "- ");

    private void OnNumberedList(object sender, RoutedEventArgs e) => ApplyLinePrefix((_, index) => $"{index + 1}. ");

    private void OnTaskList(object sender, RoutedEventArgs e) => ApplyLinePrefix((_, _) => "- [ ] ");

    private void OnQuote(object sender, RoutedEventArgs e) => ApplyLinePrefix((_, _) => "> ");

    private void OnCodeBlock(object sender, RoutedEventArgs e) => InsertBlock("```\n\n```", caretOffset: 4);

    private void OnTable(object sender, RoutedEventArgs e) => InsertBlock(
        "| Column | Column |\n| --- | --- |\n| Cell | Cell |",
        caretOffset: 2,
        selectLength: 6);

    private void OnRule(object sender, RoutedEventArgs e) => InsertBlock("---", caretOffset: 3);

    /// <summary>Wrap the selection (or a placeholder) in <paramref name="prefix"/>/<paramref name="suffix"/>, toggling if already wrapped.</summary>
    private void Wrap(string prefix, string suffix, string placeholder)
    {
        string text = Editor.Text;
        int start = Editor.SelectionStart;
        int length = Editor.SelectionLength;
        string selected = length > 0 ? text.Substring(start, length) : placeholder;

        // Already wrapped? Peel the markers off instead of nesting them.
        if (selected.Length >= prefix.Length + suffix.Length
            && selected.StartsWith(prefix, StringComparison.Ordinal)
            && selected.EndsWith(suffix, StringComparison.Ordinal))
        {
            string inner = selected[prefix.Length..^suffix.Length];
            ReplaceSelection(start, length, inner, start, inner.Length);
            return;
        }

        ReplaceSelection(start, length, prefix + selected + suffix, start + prefix.Length, selected.Length);
    }

    private void InsertLink()
    {
        string text = Editor.Text;
        int start = Editor.SelectionStart;
        int length = Editor.SelectionLength;
        string label = length > 0 ? text.Substring(start, length) : "text";
        string replacement = $"[{label}](url)";

        // Land the caret on "url" — that's the part still to fill in.
        ReplaceSelection(start, length, replacement, start + label.Length + 3, 3);
    }

    /// <summary>Rewrite the leading marker of every line the selection touches.</summary>
    private void ApplyLinePrefix(Func<string, int, string> markerFor)
    {
        string text = Editor.Text;
        (int spanStart, int spanEnd) = SelectedLineSpan(text);
        string[] lines = text[spanStart..spanEnd].Split('\r', '\n');

        var rebuilt = new List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string body = StripListMarker(lines[i]);
            string marker = markerFor(body, i);
            // A line that already carries this exact marker gets it removed instead.
            rebuilt.Add(lines[i].TrimStart() == (marker + body).TrimStart() ? body : marker + body);
        }

        string replacement = string.Join("\r", rebuilt);
        ReplaceSelection(spanStart, spanEnd - spanStart, replacement, spanStart, replacement.Length);
    }

    private void ApplyHeading(int level)
    {
        string marker = new('#', level);
        ApplyLinePrefix((_, _) => marker + " ");
    }

    /// <summary>Insert a standalone block below the current line, padded with blank lines.</summary>
    private void InsertBlock(string block, int caretOffset, int selectLength = 0)
    {
        string text = Editor.Text;
        (_, int spanEnd) = SelectedLineSpan(text);
        string normalized = block.Replace("\n", "\r");

        bool atStartOfEmptyLine = spanEnd == 0 || string.IsNullOrWhiteSpace(CurrentLine(text, spanEnd));
        string lead = atStartOfEmptyLine ? string.Empty : "\r\r";
        string insertion = lead + normalized + "\r";

        ReplaceSelection(spanEnd, 0, insertion, spanEnd + lead.Length + caretOffset, selectLength);
    }

    private static string CurrentLine(string text, int spanEnd)
    {
        int start = text.LastIndexOfAny(new[] { '\r', '\n' }, Math.Max(spanEnd - 1, 0));
        return text[(start + 1)..spanEnd];
    }

    /// <summary>Expand the selection to whole lines.</summary>
    private (int Start, int End) SelectedLineSpan(string text)
    {
        int start = Editor.SelectionStart;
        int end = start + Editor.SelectionLength;

        int lineStart = start == 0 ? 0 : text.LastIndexOfAny(new[] { '\r', '\n' }, start - 1) + 1;
        int lineEnd = text.IndexOfAny(new[] { '\r', '\n' }, end);
        return (lineStart, lineEnd < 0 ? text.Length : lineEnd);
    }

    private static string StripListMarker(string line)
    {
        string trimmed = line.TrimStart();
        string indent = line[..(line.Length - trimmed.Length)];

        // Headings, bullets, numbers, tasks and quotes are all mutually exclusive
        // line markers, so any of them is replaced rather than stacked.
        foreach (string marker in new[] { "- [ ] ", "- [x] ", "- ", "* ", "+ ", "> " })
        {
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                return indent + trimmed[marker.Length..];
            }
        }

        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }
        if (hashes is > 0 and <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
        {
            return indent + trimmed[(hashes + 1)..];
        }

        int digits = 0;
        while (digits < trimmed.Length && char.IsDigit(trimmed[digits]))
        {
            digits++;
        }
        if (digits > 0 && digits + 1 < trimmed.Length && trimmed[digits] == '.' && trimmed[digits + 1] == ' ')
        {
            return indent + trimmed[(digits + 2)..];
        }

        return line;
    }

    private void ReplaceSelection(int start, int length, string replacement, int newSelectionStart, int newSelectionLength)
    {
        string text = Editor.Text;
        Editor.Text = text.Remove(start, length).Insert(start, replacement);
        Editor.Select(Math.Min(newSelectionStart, Editor.Text.Length), newSelectionLength);
        Editor.Focus(FocusState.Programmatic);
    }

    // ---- View modes -----------------------------------------------------------

    private void OnEditMode(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Edit);

    private void OnSplitMode(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Split);

    private void OnPreviewMode(object sender, RoutedEventArgs e) => SetViewMode(ViewMode.Preview);

    private enum ViewMode { Edit, Split, Preview }

    private void SetViewMode(ViewMode mode)
    {
        EditModeButton.IsChecked = mode == ViewMode.Edit;
        SplitModeButton.IsChecked = mode == ViewMode.Split;
        PreviewModeButton.IsChecked = mode == ViewMode.Preview;

        EditModeMenuItem.IsChecked = mode == ViewMode.Edit;
        SplitModeMenuItem.IsChecked = mode == ViewMode.Split;
        PreviewModeMenuItem.IsChecked = mode == ViewMode.Preview;

        Editor.Visibility = mode == ViewMode.Preview ? Visibility.Collapsed : Visibility.Visible;
        PreviewScroller.Visibility = mode == ViewMode.Edit ? Visibility.Collapsed : Visibility.Visible;
        Splitter.Visibility = mode == ViewMode.Split ? Visibility.Visible : Visibility.Collapsed;
        FormatBar.Visibility = mode == ViewMode.Preview ? Visibility.Collapsed : Visibility.Visible;

        double editorStars = EditorColumn.Width.Value <= 0 ? 1 : EditorColumn.Width.Value;
        double previewStars = PreviewColumn.Width.Value <= 0 ? 1 : PreviewColumn.Width.Value;

        EditorColumn.Width = mode == ViewMode.Preview
            ? new GridLength(0)
            : new GridLength(editorStars, GridUnitType.Star);
        PreviewColumn.Width = mode == ViewMode.Edit
            ? new GridLength(0)
            : new GridLength(previewStars, GridUnitType.Star);

        if (mode == ViewMode.Preview)
        {
            RenderPreview();
        }
    }

    // ---- Draggable splitter ---------------------------------------------------

    private void OnSplitterDrag(object sender, ManipulationDeltaRoutedEventArgs e)
    {
        double total = EditorColumn.ActualWidth + PreviewColumn.ActualWidth;
        if (total <= 0)
        {
            return;
        }

        const double min = 120;
        double newEditor = Math.Clamp(EditorColumn.ActualWidth + e.Delta.Translation.X, min, total - min);
        EditorColumn.Width = new GridLength(newEditor, GridUnitType.Star);
        PreviewColumn.Width = new GridLength(total - newEditor, GridUnitType.Star);
    }

    private void OnSplitterPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (Splitter.Child is Border line)
        {
            line.Width = 2;
            line.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        }
    }

    private void OnSplitterPointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (Splitter.Child is Border line)
        {
            line.Width = 1;
            line.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"];
        }
    }

    // ---- File commands --------------------------------------------------------

    private void OnNew(object sender, RoutedEventArgs e) => NewTab();

    private async void OnOpen(object sender, RoutedEventArgs e) => await OpenAsync();

    private async void OnCloseTab(object sender, RoutedEventArgs e) => await CloseTabAsync(_current);

    private void OnExit(object sender, RoutedEventArgs e) => App.MainWindow?.Close();

    // ---- Edit menu ------------------------------------------------------------

    private void OnUndo(object sender, RoutedEventArgs e) => WithEditor(() => Editor.Undo());

    private void OnRedo(object sender, RoutedEventArgs e) => WithEditor(() => Editor.Redo());

    private void OnCut(object sender, RoutedEventArgs e) => WithEditor(() => Editor.CutSelectionToClipboard());

    private void OnCopy(object sender, RoutedEventArgs e) => WithEditor(() => Editor.CopySelectionToClipboard());

    private void OnPaste(object sender, RoutedEventArgs e) => WithEditor(() => Editor.PasteFromClipboard());

    private void OnSelectAll(object sender, RoutedEventArgs e) => WithEditor(() => Editor.SelectAll());

    /// <summary>Menu commands act on the editor, which has just lost focus to the menu.</summary>
    private void WithEditor(Action action)
    {
        Editor.Focus(FocusState.Programmatic);
        action();
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async void OnSaveAs(object sender, RoutedEventArgs e) => await SaveAsAsync();

    private async Task OpenAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeFilter.Add(".md");
        picker.FileTypeFilter.Add(".markdown");
        picker.FileTypeFilter.Add(".mdown");
        picker.FileTypeFilter.Add(".txt");
        picker.FileTypeFilter.Add("*");

        Windows.Storage.StorageFile file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await OpenPathAsync(file.Path);
    }

    /// <summary>Open <paramref name="path"/> in a tab, reusing one if it is already open.</summary>
    private async Task OpenPathAsync(string path)
    {
        // Already open? Just bring that tab forward.
        MdDocument? existing = _documents.FirstOrDefault(
            d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            SelectDocument(existing);
            SetViewMode(ViewMode.Preview);
            return;
        }

        try
        {
            string text = await File.ReadAllTextAsync(path);

            StashCurrent();
            MdDocument document = ReuseEmptyTab() ?? NewTab();
            document.Path = path;
            document.Text = text;
            document.SavedText = Normalize(text);
            document.SelectionStart = 0;
            document.PreviewOffset = 0;
            document.IsDirty = false;
            SwitchTo(document, stashCurrent: false);

            // Opening a file is a reading gesture: land in preview.
            SetViewMode(ViewMode.Preview);
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not open file", ex.Message);
        }
    }

    /// <summary>An untouched "Untitled" tab is reused rather than leaving a blank tab behind.</summary>
    private MdDocument? ReuseEmptyTab()
    {
        if (_current.Path is null && !_current.IsDirty && string.IsNullOrEmpty(Editor.Text.Trim()))
        {
            return _current;
        }
        return null;
    }

    private async Task<bool> SaveAsync()
    {
        if (_current.Path is null)
        {
            return await SaveAsAsync();
        }

        try
        {
            string text = Editor.Text;
            await File.WriteAllTextAsync(_current.Path, text);
            MarkSaved(text);
            return true;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not save file", ex.Message);
            return false;
        }
    }

    private async Task<bool> SaveAsAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow(picker);
        picker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        picker.FileTypeChoices.Add("Markdown", new[] { ".md" });
        picker.FileTypeChoices.Add("Plain text", new[] { ".txt" });
        picker.SuggestedFileName = _current.Path is null ? "Untitled" : Path.GetFileNameWithoutExtension(_current.Path);

        Windows.Storage.StorageFile file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return false;
        }

        try
        {
            string text = Editor.Text;
            await File.WriteAllTextAsync(file.Path, text);
            _current.Path = file.Path;
            MarkSaved(text);
            return true;
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("Could not save file", ex.Message);
            return false;
        }
    }

    private void MarkSaved(string text)
    {
        _current.Text = text;
        _current.SavedText = Normalize(text);
        _current.IsDirty = false;
        UpdateTitle();
    }

    /// <summary>Returns true if it is safe to discard <paramref name="document"/>.</summary>
    private async Task<bool> ConfirmDiscardAsync(MdDocument document)
    {
        if (!document.IsDirty)
        {
            return true;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Save changes?",
            Content = $"\"{document.Name}\" has unsaved changes.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Don't save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result switch
        {
            // Saving applies to the document being closed, so make it current first.
            ContentDialogResult.Primary => await SaveDocumentAsync(document),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    private async Task<bool> SaveDocumentAsync(MdDocument document)
    {
        if (!ReferenceEquals(document, _current))
        {
            SelectDocument(document);
        }
        return await SaveAsync();
    }

    // ---- State + helpers ------------------------------------------------------

    private void UpdateTitle()
    {
        StatusText.Text = _current.Path ?? "Untitled";
        App.MainWindow?.SetTitle($"{_current.Header} — {AppName}");
    }

    /// <summary>
    /// TextBox rewrites line breaks to \r, so a file loaded with \r\n would otherwise
    /// look modified the instant it is displayed. Compare dirty state on \n only.
    /// </summary>
    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(accelerator);
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    private static void InitializeWithWindow(object target)
    {
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(target, hwnd);
    }

    private const string WelcomeSample =
        "# Welcome to MdPad\n\n" +
        "A **lightweight, native** Markdown editor for Windows, built with *WinUI 3*.\n\n" +
        "## Features\n\n" +
        "- Live native preview (no WebView2)\n" +
        "- Open, edit, and save `.md` files\n" +
        "- Tabs, an outline sidebar, and split / editor / preview views\n\n" +
        "### Try some Markdown\n\n" +
        "> Blockquotes render with a nice accent bar.\n\n" +
        "```csharp\n" +
        "Console.WriteLine(\"Hello, Markdown!\");\n" +
        "```\n\n" +
        "| Feature | Status |\n" +
        "| --- | --- |\n" +
        "| Editing | ✅ |\n" +
        "| Preview | ✅ |\n\n" +
        "- [x] Parse Markdown\n" +
        "- [ ] Conquer the world\n\n" +
        "Learn more at [the CommonMark spec](https://commonmark.org).\n";
}
