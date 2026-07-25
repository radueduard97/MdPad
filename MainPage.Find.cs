using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

using Windows.System;

namespace MdPad;

/// <summary>One row in the find results list.</summary>
public sealed class SearchResultItem
{
    public SearchResultItem(SearchHit hit, int indexInFile)
    {
        Hit = hit;
        IndexInFile = indexInFile;
    }

    public SearchHit Hit { get; }

    /// <summary>
    /// Which occurrence within its own file this is. Offsets from disk do not survive the
    /// editor's newline rewriting, so a clicked result is found again by counting rather
    /// than by position.
    /// </summary>
    public int IndexInFile { get; }

    public string Location => $"{Hit.Display}:{Hit.Line + 1}";

    public string Preview => Hit.Preview;

    public string Tip => $"{Hit.Path}\nLine {Hit.Line + 1}";
}

/// <summary>
/// Find and replace, over the open document or over the folder around it. A skill is a
/// folder — <c>SKILL.md</c> plus its references — so renaming a section or a tool is a
/// folder-wide edit, and doing it one tab at a time is how references drift out of sync.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>Folder search runs on every keystroke; give the typist this long to finish.</summary>
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(300);

    private readonly ObservableCollection<SearchResultItem> _results = new();

    private DispatcherTimer? _searchDebounce;

    /// <summary>
    /// Where typing searches from. Anchored when the bar opens and moved along by each
    /// step, so refining a query walks forward from where you are rather than snapping
    /// back to the top of the file on every keystroke.
    /// </summary>
    private int _findAnchor;

    private bool MatchCase => MatchCaseToggle.IsChecked == true;

    private bool FolderScope => ScopeBox.SelectedIndex == 1;

    /// <summary>The folder a folder-scoped search covers: the one holding the current file.</summary>
    private string? SearchFolder => _current.Path is null ? null : Path.GetDirectoryName(_current.Path);

    // ---- Opening and closing --------------------------------------------------

    private void OnFindCommand(object sender, RoutedEventArgs e) => OpenFind(replace: false);

    private void OnReplaceCommand(object sender, RoutedEventArgs e) => OpenFind(replace: true);

    private void OnCloseFind(object sender, RoutedEventArgs e) => CloseFind();

    private void OpenFind(bool replace)
    {
        if (_searchDebounce is null)
        {
            _searchDebounce = new DispatcherTimer { Interval = SearchDelay };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce!.Stop();
                RunSearch(jump: true);
            };
            ResultsList.ItemsSource = _results;
        }

        // Matches are shown by selecting them in the editor, so it has to be on screen.
        if (_mode is ViewMode.Preview or ViewMode.Agent)
        {
            SetViewMode(ViewMode.Split);
        }

        ApplyFindDefaults();

        FindBar.Visibility = Visibility.Visible;
        ReplaceRow.Visibility = replace ? Visibility.Visible : Visibility.Collapsed;

        // A selection in the editor is almost always what the user means to search for.
        if (Editor.SelectionLength is > 0 and < 200)
        {
            string selected = Editor.SelectedText.Trim();
            if (selected.Length > 0 && !selected.Contains('\r') && !selected.Contains('\n'))
            {
                FindBox.Text = selected;
            }
        }

        _findAnchor = Editor.SelectionStart;

        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
        RunSearch(jump: true);
    }

    private void CloseFind()
    {
        FindBar.Visibility = Visibility.Collapsed;
        _results.Clear();
        ResultsList.Visibility = Visibility.Collapsed;
        _searchDebounce?.Stop();
        HighlightMatches();   // The bar is gone, so this clears the preview marks.
        Editor.Focus(FocusState.Programmatic);
    }

    // ---- Search ---------------------------------------------------------------

    private void OnFindTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce?.Stop();
        _searchDebounce?.Start();
    }

    /// <summary>
    /// Where the bar starts each time it is opened. Match case is remembered between
    /// sessions when asked for, because it is a property of what you are searching for
    /// rather than of the search you are about to run.
    /// </summary>
    private void ApplyFindDefaults()
    {
        FindSettings find = Settings.Current.Find;

        // Only reset scope when the bar was closed: reopening mid-task should not
        // throw away a folder scope the user just chose.
        if (FindBar.Visibility != Visibility.Visible)
        {
            ScopeBox.SelectedIndex = find.DefaultScope == SearchScope.Folder ? 1 : 0;
        }

        if (find.RememberOptions)
        {
            MatchCaseToggle.IsChecked = find.MatchCase;
        }
    }

    private void OnMatchCaseToggled(object sender, RoutedEventArgs e)
    {
        if (Settings.Current.Find.RememberOptions)
        {
            Settings.Current.Find.MatchCase = MatchCase;
            Settings.Apply();
        }
        RunSearch();
    }

    private void OnScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        // The bar is built before the page has a document; nothing to search yet.
        if (FindBar is not null && FindBar.Visibility == Visibility.Visible)
        {
            RunSearch();
        }
    }

    private void OnFindBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                _searchDebounce?.Stop();
                RunSearch();
                StepMatch(forward: !IsShiftDown());
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                CloseFind();
                break;
        }
    }

    private void OnReplaceBoxKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                ReplaceCurrentMatch();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                CloseFind();
                break;
        }
    }

    private static bool IsShiftDown() =>
        Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>Re-run the search and repaint the count and, in folder scope, the results.</summary>
    /// <param name="jump">
    /// Whether to move to the first match from the anchor. True while typing, so the match
    /// is on screen and selected as the query takes shape; false when the caller has just
    /// moved the selection itself and would only be dragged backwards by it.
    /// </param>
    private void RunSearch(bool jump = false)
    {
        string query = FindBox.Text;
        _results.Clear();

        if (query.Length == 0)
        {
            FindStatusText.Text = string.Empty;
            ResultsList.Visibility = Visibility.Collapsed;
            HighlightMatches();
            return;
        }

        if (!FolderScope)
        {
            ResultsList.Visibility = Visibility.Collapsed;
            int count = DocumentSearch.Count(Editor.Text, query, MatchCase);
            FindStatusText.Text = count == 0 ? "No matches" : $"{count} in this file";

            if (jump)
            {
                JumpToMatch(query);
            }
            HighlightMatches();
            return;
        }

        HighlightMatches();

        if (SearchFolder is not { } folder)
        {
            ResultsList.Visibility = Visibility.Collapsed;
            FindStatusText.Text = "Save the file first";
            return;
        }

        List<SearchHit> hits = DocumentSearch.SearchFolder(folder, query, MatchCase, LiveText);
        var perFile = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (SearchHit hit in hits)
        {
            int index = perFile.TryGetValue(hit.Path, out int seen) ? seen : 0;
            perFile[hit.Path] = index + 1;
            _results.Add(new SearchResultItem(hit, index));
        }

        ResultsList.Visibility = _results.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        FindStatusText.Text = hits.Count switch
        {
            0 => "No matches",
            >= DocumentSearch.MaxHits => $"{DocumentSearch.MaxHits}+ matches",
            _ => $"{hits.Count} in {perFile.Count} file(s)",
        };
    }

    /// <summary>
    /// Unsaved contents for a path, so folder results reflect what is on screen rather
    /// than what was last written. Null means "read the file".
    /// </summary>
    private string? LiveText(string path)
    {
        MdDocument? document = _documents.FirstOrDefault(
            d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

        if (document is null)
        {
            return null;
        }
        return ReferenceEquals(document, _current) ? Editor.Text : document.IsDirty ? document.Text : null;
    }

    // ---- Stepping through the current document --------------------------------

    private void OnFindNext(object sender, RoutedEventArgs e) => StepMatch(forward: true);

    private void OnFindPrevious(object sender, RoutedEventArgs e) => StepMatch(forward: false);

    /// <summary>
    /// Select the first match from the anchor, so a query that is still being typed already
    /// shows where it landed. The find box keeps focus — the editor's selection stays
    /// visible while unfocused, which is the whole reason this reads as a highlight.
    /// </summary>
    private void JumpToMatch(string query)
    {
        int at = DocumentSearch.NextMatch(Editor.Text, query, MatchCase, _findAnchor);
        if (at < 0)
        {
            return;
        }

        Editor.Select(at, query.Length);
        ShowMatchInPreview(at);
    }

    /// <summary>Move the selection to the next or previous match, wrapping at the ends.</summary>
    private void StepMatch(bool forward)
    {
        if (FindBar.Visibility != Visibility.Visible || FindBox.Text.Length == 0)
        {
            return;
        }

        string query = FindBox.Text;
        int at = forward
            ? DocumentSearch.NextMatch(Editor.Text, query, MatchCase, Editor.SelectionStart + Editor.SelectionLength)
            : DocumentSearch.PreviousMatch(Editor.Text, query, MatchCase, Editor.SelectionStart);

        if (at < 0)
        {
            FindStatusText.Text = "No matches";
            return;
        }

        Editor.Select(at, query.Length);
        _findAnchor = at;
        ShowMatchInPreview(at);
    }

    /// <summary>Bring the preview to the section the match sits in, so both panes agree.</summary>
    private void ShowMatchInPreview(int offset)
    {
        if (PreviewScroller.Visibility != Visibility.Visible || _headings.Count == 0)
        {
            return;
        }

        int line = Editor.Text.Take(offset).Count(c => c == '\r' || c == '\n');
        OutlineEntry? section = _headings.LastOrDefault(h => h.Line <= line);
        section?.Element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0 });
    }

    /// <summary>Clicking a folder result opens that file and selects the occurrence clicked.</summary>
    private async void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultItem item)
        {
            return;
        }

        await OpenPathAsync(item.Hit.Path);
        if (_mode is ViewMode.Preview or ViewMode.Agent)
        {
            SetViewMode(ViewMode.Split);
        }

        // Count to the same occurrence rather than trusting the on-disk offset.
        int at = DocumentSearch.Matches(Editor.Text, FindBox.Text, MatchCase)
            .Skip(item.IndexInFile)
            .Select(o => (int?)o)
            .FirstOrDefault() ?? -1;

        if (at >= 0)
        {
            Editor.Focus(FocusState.Programmatic);
            Editor.Select(at, FindBox.Text.Length);
            _findAnchor = at;
            ShowMatchInPreview(at);
        }
    }

    // ---- Replace --------------------------------------------------------------

    private void OnReplaceOne(object sender, RoutedEventArgs e) => ReplaceCurrentMatch();

    /// <summary>Replace the selected match if that is what is selected, then move on.</summary>
    private void ReplaceCurrentMatch()
    {
        string query = FindBox.Text;
        if (query.Length == 0)
        {
            return;
        }

        bool onMatch = Editor.SelectionLength == query.Length
            && string.Equals(
                Editor.SelectedText,
                query,
                MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

        if (onMatch)
        {
            int at = Editor.SelectionStart;
            string replacement = ReplaceBox.Text;
            ReplaceSelection(at, query.Length, replacement, at + replacement.Length, 0);
        }

        StepMatch(forward: true);
        RunSearch();
    }

    private async void OnReplaceAll(object sender, RoutedEventArgs e)
    {
        string query = FindBox.Text;
        if (query.Length == 0)
        {
            return;
        }

        if (!FolderScope)
        {
            string updated = DocumentSearch.ReplaceAll(Editor.Text, query, ReplaceBox.Text, MatchCase, out int count);
            if (count > 0)
            {
                int caret = Math.Min(Editor.SelectionStart, updated.Length);
                Editor.Text = updated;
                Editor.Select(caret, 0);
            }
            FindStatusText.Text = count == 0 ? "No matches" : $"Replaced {count}";
            RunSearch();
            return;
        }

        if (SearchFolder is { } folder)
        {
            await ReplaceAcrossFolderAsync(folder, query, ReplaceBox.Text);
        }
    }

    private async Task<bool> ConfirmFolderReplaceAsync(int hits, int files, string query, string folder)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Replace across the folder?",
            Content = $"Replace {hits} occurrence(s) of \"{query}\" in {files} file(s) under "
                + $"{Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}.\n\n"
                + "Files that are not open are written to disk; open tabs are changed but left unsaved.",
            PrimaryButtonText = "Replace all",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await confirm.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Rewrite every occurrence under <paramref name="folder"/>. Files with no tab open are
    /// written straight to disk; anything open is changed in its buffer instead and left
    /// dirty, so the author reviews and saves rather than having a tab rewritten under them.
    /// </summary>
    private async Task ReplaceAcrossFolderAsync(string folder, string query, string replacement)
    {
        List<SearchHit> hits = DocumentSearch.SearchFolder(folder, query, MatchCase, LiveText);
        if (hits.Count == 0)
        {
            FindStatusText.Text = "No matches";
            return;
        }

        List<string> paths = hits.Select(h => h.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Small, obviously-safe replacements can be waved through; the threshold is a
        // setting because "small" is a matter of how much of the folder you trust.
        if (hits.Count > Settings.Current.Find.ConfirmReplaceAbove && !await ConfirmFolderReplaceAsync(hits.Count, paths.Count, query, folder))
        {
            return;
        }

        int written = 0;
        int inTabs = 0;
        int replaced = 0;
        var failures = new List<string>();

        foreach (string path in paths)
        {
            MdDocument? open = _documents.FirstOrDefault(
                d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));

            if (open is not null)
            {
                string source = ReferenceEquals(open, _current) ? Editor.Text : open.Text;
                string updated = DocumentSearch.ReplaceAll(source, query, replacement, MatchCase, out int count);
                if (count == 0)
                {
                    continue;
                }

                if (ReferenceEquals(open, _current))
                {
                    int caret = Math.Min(Editor.SelectionStart, updated.Length);
                    Editor.Text = updated;   // Through the editor, so Ctrl+Z still undoes it.
                    Editor.Select(caret, 0);
                }
                else
                {
                    open.Text = updated;
                    open.IsDirty = true;
                }

                replaced += count;
                inTabs++;
                continue;
            }

            try
            {
                string text = await File.ReadAllTextAsync(path);
                string updated = DocumentSearch.ReplaceAll(text, query, replacement, MatchCase, out int count);
                if (count == 0)
                {
                    continue;
                }

                await File.WriteAllTextAsync(path, updated);
                replaced += count;
                written++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures.Add($"{DocumentSearch.Relative(folder, path)} — {ex.Message}");
            }
        }

        RenderPreview();
        RunSearch();
        MarkSessionDirty();

        var summary = new List<string> { $"Replaced {replaced} occurrence(s)" };
        if (written > 0)
        {
            summary.Add($"{written} file(s) written to disk");
        }
        if (inTabs > 0)
        {
            summary.Add($"{inTabs} open tab(s) changed but not saved");
        }
        if (failures.Count > 0)
        {
            summary.Add("Could not write:\n" + string.Join("\n", failures));
        }

        FindStatusText.Text = $"Replaced {replaced}";
        await ShowMessageAsync("Replace all", string.Join("\n", summary));
    }
}
