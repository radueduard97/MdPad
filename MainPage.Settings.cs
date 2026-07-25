using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

using Windows.System;
using Windows.UI;

namespace MdPad;

/// <summary>
/// Everything the settings pane reaches: the appearance the window and preview take,
/// the typing assists in the editor, autosave, and the watch that notices a file being
/// rewritten underneath an open tab — the normal case when an agent is editing the
/// same skill you are.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>VK_OEM_COMMA / VK_OEM_PLUS / VK_OEM_MINUS have no <see cref="VirtualKey"/> members.</summary>
    private const VirtualKey CommaKey = (VirtualKey)0xBC;
    private const VirtualKey PlusKey = (VirtualKey)0xBB;
    private const VirtualKey MinusKey = (VirtualKey)0xBD;

    /// <summary>A list item: indent, marker, an optional task box, and the text after it.</summary>
    private static readonly Regex ListItem = new(
        @"^(?<indent>[ \t]*)(?<marker>[-*+]|\d+[.)])(?<task>[ \t]+\[[ xX]\])?(?<space>[ \t]+)(?<text>.*)$",
        RegexOptions.Compiled);

    private static readonly Regex QuoteLine = new(@"^(?<indent>[ \t]*)(?<marker>(> ?)+)(?<text>.*)$", RegexOptions.Compiled);

    /// <summary>Pairs the editor closes for you; <c>*</c> is left out — it collides with bullets.</summary>
    private static readonly Dictionary<char, char> ClosingPairs = new()
    {
        ['`'] = '`',
        ['('] = ')',
        ['['] = ']',
        ['{'] = '}',
        ['"'] = '"',
    };

    private ScrollViewer? _editorScroller;
    private DispatcherTimer? _autosaveTimer;
    private DispatcherTimer? _watchTimer;
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _changedOnDisk = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Editor text as of the last change, for spotting a single typed character.</summary>
    private string _lastEditorText = string.Empty;

    /// <summary>Set while a typing assist rewrites the buffer, so it does not re-enter.</summary>
    private bool _applyingAssist;

    /// <summary>One reload prompt at a time; a rebuilt folder can raise a dozen events.</summary>
    private bool _reloadPromptOpen;

    private void InitializeSettings()
    {
        Settings.Changed += ApplySettings;

        AddAccelerator(CommaKey, VirtualKeyModifiers.Control, () => _ = ShowSettingsAsync());
        AddAccelerator(PlusKey, VirtualKeyModifiers.Control, () => Zoom(0.1));
        AddAccelerator(VirtualKey.Add, VirtualKeyModifiers.Control, () => Zoom(0.1));
        AddAccelerator(MinusKey, VirtualKeyModifiers.Control, () => Zoom(-0.1));
        AddAccelerator(VirtualKey.Subtract, VirtualKeyModifiers.Control, () => Zoom(-0.1));
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, () => SetZoom(1.0));

        Editor.KeyDown += OnEditorKeyDown;
        Editor.LostFocus += OnEditorLostFocus;
    }

    private async void OnSettings(object sender, RoutedEventArgs e) => await ShowSettingsAsync();

    private async Task ShowSettingsAsync() => await SettingsDialog.ShowAsync(XamlRoot);

    // ---- Applying -------------------------------------------------------------

    /// <summary>
    /// Push the whole configuration onto the UI. Cheap enough to run wholesale on every
    /// change: one re-render and a handful of property sets, against a settings pane
    /// that live-applies and so has no OK button to batch behind.
    /// </summary>
    private void ApplySettings()
    {
        AppSettings settings = Settings.Current;
        double zoom = settings.Fonts.Zoom;

        App.MainWindow?.ApplyAppearance();

        // The preview can be themed against the app on purpose: checking a README in the
        // other theme is a real question, and native controls answer it for free.
        ElementTheme previewTheme = settings.Appearance.PreviewTheme switch
        {
            PreviewTheme.Light => ElementTheme.Light,
            PreviewTheme.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        PreviewScroller.RequestedTheme = previewTheme;
        AgentScroller.RequestedTheme = previewTheme;

        Editor.FontFamily = new FontFamily(settings.Fonts.EditorFamily);
        Editor.FontSize = settings.Fonts.EditorSize * zoom;
        Editor.TextWrapping = settings.Editor.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ScrollViewer.SetHorizontalScrollBarVisibility(
            Editor,
            settings.Editor.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
        Editor.IsSpellCheckEnabled = settings.Editor.SpellCheck;

        // Set on the scroller, not the panel: a StackPanel carries no text properties,
        // and the rendered TextBlocks inherit these down the tree from a Control.
        PreviewScroller.FontFamily = new FontFamily(settings.Fonts.PreviewFamily);
        PreviewScroller.FontSize = settings.Fonts.PreviewSize * zoom;
        PreviewHost.MaxWidth = settings.Appearance.PreviewMaxWidth <= 0
            ? double.PositiveInfinity
            : settings.Appearance.PreviewMaxWidth * zoom;

        RenderOptions.MonoFamily = settings.Fonts.CodeFamily;
        RenderOptions.CodeSize = settings.Fonts.CodeSize * zoom;
        RenderOptions.LineHeight = settings.Fonts.PreviewLineHeight * zoom;
        RenderOptions.Scale = settings.Fonts.PreviewSize * zoom / 14.0;
        RenderOptions.Accent = settings.Appearance.Accent == AccentSource.Custom
            ? ParseColor(settings.Appearance.AccentColor)
            : null;

        ApplyGutter();
        ApplyAutosave();
        ApplyMatchSelectionBrush();
        RefreshWatchers();

        // Sizes, fonts and the accent all live inside the rendered tree, and the budget
        // panel is drawn from the token estimate the settings can change.
        RenderPreview();
    }

    private static Color? ParseColor(string? value)
    {
        if (value is null)
        {
            return null;
        }

        string hex = value.TrimStart('#');
        if (hex.Length == 6 && uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out uint rgb))
        {
            return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        }
        return null;
    }

    // ---- Zoom -----------------------------------------------------------------

    private void Zoom(double delta) => SetZoom(Settings.Current.Fonts.Zoom + delta);

    private void SetZoom(double zoom)
    {
        Settings.Current.Fonts.Zoom = Math.Clamp(Math.Round(zoom, 2), 0.5, 3.0);
        Settings.Apply();
        StatusText.Text = $"Zoom {Settings.Current.Fonts.Zoom * 100:0}%";
    }

    // ---- Line-number gutter ---------------------------------------------------

    /// <summary>
    /// Numbers count source lines, which only line up with what is on screen when lines
    /// are not being wrapped — so with word wrap on the gutter stays hidden rather than
    /// showing numbers that drift further out the further down you read.
    /// </summary>
    private void ApplyGutter()
    {
        bool show = Settings.Current.Editor.ShowLineNumbers && !Settings.Current.Editor.WordWrap;
        GutterScroller.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        GutterText.FontFamily = Editor.FontFamily;
        GutterText.FontSize = Editor.FontSize;
        HookEditorScroller();
        UpdateGutterNumbers();
    }

    private void UpdateGutterNumbers()
    {
        if (GutterScroller.Visibility != Visibility.Visible)
        {
            return;
        }

        int lines = 1;
        foreach (char c in Editor.Text)
        {
            if (c is '\r' or '\n')
            {
                lines++;
            }
        }

        // TextBox reports \r for a break, so \r\n would double-count; it never stores both.
        var sb = new StringBuilder(lines * 4);
        for (int i = 1; i <= lines; i++)
        {
            sb.Append(i);
            if (i < lines)
            {
                sb.Append('\n');
            }
        }
        GutterText.Text = sb.ToString();
    }

    /// <summary>The editor's own scroll viewer lives inside its template; find it once.</summary>
    private void HookEditorScroller()
    {
        if (_editorScroller is not null)
        {
            return;
        }

        _editorScroller = FindDescendant<ScrollViewer>(Editor);
        if (_editorScroller is not null)
        {
            _editorScroller.ViewChanged += (_, _) =>
                GutterScroller.ChangeView(null, _editorScroller.VerticalOffset, null, disableAnimation: true);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                return match;
            }
            if (FindDescendant<T>(child) is { } nested)
            {
                return nested;
            }
        }
        return null;
    }

    // ---- Typing assists -------------------------------------------------------

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool shift = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        bool control = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

        if (control)
        {
            return;
        }

        if (e.Key == VirtualKey.Tab)
        {
            e.Handled = HandleTab(shift);
        }
        else if (e.Key == VirtualKey.Enter && !shift)
        {
            e.Handled = HandleEnter();
        }
    }

    /// <summary>
    /// Tab indents rather than moving focus. A caret inside a line inserts to the next
    /// stop; a selection spanning lines shifts the whole block, which is what a nested
    /// list actually needs.
    /// </summary>
    private bool HandleTab(bool outdent)
    {
        EditorSettings settings = Settings.Current.Editor;
        string unit = settings.TabInsertsSpaces ? new string(' ', settings.TabWidth) : "\t";
        string text = Editor.Text;
        (int lineStart, int lineEnd) = SelectedLineSpan(text);
        bool multiLine = Editor.SelectionLength > 0 && text[lineStart..lineEnd].Contains('\r');

        if (!multiLine && !outdent)
        {
            int start = Editor.SelectionStart;
            ReplaceSelection(start, Editor.SelectionLength, unit, start + unit.Length, 0);
            return true;
        }

        string[] lines = text[lineStart..lineEnd].Split('\r');
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = outdent ? Outdent(lines[i], settings.TabWidth) : unit + lines[i];
        }

        string replacement = string.Join('\r', lines);
        ReplaceSelection(lineStart, lineEnd - lineStart, replacement, lineStart, replacement.Length);
        return true;

        static string Outdent(string line, int width)
        {
            if (line.StartsWith('\t'))
            {
                return line[1..];
            }
            int spaces = 0;
            while (spaces < width && spaces < line.Length && line[spaces] == ' ')
            {
                spaces++;
            }
            return line[spaces..];
        }
    }

    /// <summary>
    /// Carry a list or quote marker onto the next line. An item with nothing after the
    /// marker ends the list instead — the same Enter that would otherwise leave a stray
    /// bullet behind clears it.
    /// </summary>
    private bool HandleEnter()
    {
        if (!Settings.Current.Editor.ContinueLists || Editor.SelectionLength > 0)
        {
            return false;
        }

        string text = Editor.Text;
        int caret = Editor.SelectionStart;
        int lineStart = text.LastIndexOf('\r', Math.Max(0, caret - 1)) + 1;
        if (caret > 0 && text[caret - 1] == '\r')
        {
            lineStart = caret;
        }

        string line = text[lineStart..caret];

        if (ListItem.Match(line) is { Success: true } item)
        {
            string marker = item.Groups["marker"].Value;
            bool hasTask = item.Groups["task"].Success;

            if (item.Groups["text"].Value.Length == 0)
            {
                // An empty item: drop the marker rather than adding another one.
                ReplaceSelection(lineStart, caret - lineStart, string.Empty, lineStart, 0);
                return true;
            }

            string next = char.IsDigit(marker[0])
                ? (int.TryParse(marker[..^1], out int n) ? n + 1 : 1) + marker[^1..]
                : marker;

            string continuation = "\r" + item.Groups["indent"].Value + next + (hasTask ? " [ ]" : string.Empty)
                + item.Groups["space"].Value;
            ReplaceSelection(caret, 0, continuation, caret + continuation.Length, 0);
            return true;
        }

        if (QuoteLine.Match(line) is { Success: true } quote && quote.Groups["text"].Value.Length > 0)
        {
            string continuation = "\r" + quote.Groups["indent"].Value + quote.Groups["marker"].Value;
            ReplaceSelection(caret, 0, continuation, caret + continuation.Length, 0);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Close a bracket or backtick the moment it is typed. Detected by diffing the buffer
    /// rather than by key code, so it works the same on a layout where <c>`</c> is not
    /// where a US keyboard puts it.
    /// </summary>
    private void HandleAutoClose()
    {
        string text = Editor.Text;
        string previous = _lastEditorText;
        _lastEditorText = text;

        if (_applyingAssist || !Settings.Current.Editor.AutoCloseMarkers || text.Length != previous.Length + 1)
        {
            return;
        }

        int caret = Editor.SelectionStart;
        if (caret < 1 || caret > text.Length || Editor.SelectionLength > 0)
        {
            return;
        }

        // The single new character has to be the one just behind the caret.
        if (!string.Equals(text[..(caret - 1)], previous[..(caret - 1)], StringComparison.Ordinal)
            || !string.Equals(text[caret..], previous[(caret - 1)..], StringComparison.Ordinal))
        {
            return;
        }

        if (!ClosingPairs.TryGetValue(text[caret - 1], out char closer))
        {
            return;
        }

        // Only ahead of nothing, whitespace, or a closing bracket: mid-word this is noise.
        if (caret < text.Length && !char.IsWhiteSpace(text[caret]) && !")]}".Contains(text[caret]))
        {
            return;
        }

        _applyingAssist = true;
        Editor.Text = text.Insert(caret, closer.ToString());
        Editor.Select(caret, 0);
        _lastEditorText = Editor.Text;
        _applyingAssist = false;
    }

    // ---- Saving ---------------------------------------------------------------

    /// <summary>
    /// The text to write, after the on-save tidy-ups. The editor is updated to match, so
    /// the buffer and the file stay identical and the tab does not come back dirty the
    /// moment it is saved.
    /// </summary>
    private string PrepareForSave()
    {
        EditorSettings settings = Settings.Current.Editor;
        string text = Editor.Text;

        if (settings.TrimTrailingWhitespace)
        {
            string[] lines = text.Split('\r');
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = lines[i].TrimEnd(' ', '\t');
            }
            text = string.Join('\r', lines);
        }

        if (settings.EnsureTrailingNewline && text.Length > 0 && text[^1] != '\r')
        {
            text += '\r';
        }

        if (!string.Equals(text, Editor.Text, StringComparison.Ordinal))
        {
            int caret = Editor.SelectionStart;
            _applyingAssist = true;
            Editor.Text = text;
            Editor.Select(Math.Min(caret, text.Length), 0);
            _applyingAssist = false;
            _lastEditorText = text;
        }

        return WithLineEndings(text);
    }

    /// <summary>TextBox stores every break as \r; what lands on disk is the configured ending.</summary>
    private static string WithLineEndings(string text)
    {
        string unified = Normalize(text);
        return Settings.Current.Editor.LineEnding == LineEndingKind.Crlf
            ? unified.Replace("\n", "\r\n")
            : unified;
    }

    // ---- Autosave -------------------------------------------------------------

    private void ApplyAutosave()
    {
        FileSettings files = Settings.Current.Files;

        if (files.Autosave != AutosaveMode.Interval)
        {
            _autosaveTimer?.Stop();
            return;
        }

        _autosaveTimer ??= CreateAutosaveTimer();
        _autosaveTimer.Interval = TimeSpan.FromSeconds(files.AutosaveSeconds);
        _autosaveTimer.Start();
    }

    private DispatcherTimer CreateAutosaveTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) => AutosaveNow();
        return timer;
    }

    private void OnEditorLostFocus(object sender, RoutedEventArgs e)
    {
        if (Settings.Current.Files.Autosave == AutosaveMode.OnFocusLoss)
        {
            AutosaveNow();
        }
    }

    /// <summary>Autosave only writes files that already have a path — it never opens a picker.</summary>
    private void AutosaveNow()
    {
        StashCurrent();
        foreach (MdDocument document in _documents.Where(d => d.IsDirty && d.Path is not null).ToList())
        {
            try
            {
                string text = ReferenceEquals(document, _current)
                    ? PrepareForSave()
                    : WithLineEndings(document.Text);

                File.WriteAllText(document.Path!, text);
                document.SavedText = Normalize(ReferenceEquals(document, _current) ? Editor.Text : document.Text);
                document.IsDirty = false;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file that is locked or read-only is not worth interrupting typing for;
                // the tab stays dirty and an explicit Save will report the problem.
            }
        }
        UpdateTitle();
    }

    // ---- Watching the files behind the tabs -----------------------------------

    /// <summary>
    /// Watch the folder of every open file. A skill rewritten by an agent while it sits
    /// open here is the normal case, not the exception, and a stale buffer silently
    /// overwrites that work on the next save.
    /// </summary>
    private void RefreshWatchers()
    {
        if (Settings.Current.Files.ExternalChange == ExternalChangeMode.Ignore)
        {
            DisposeWatchers();
            return;
        }

        var wanted = new HashSet<string>(
            _documents.Select(d => d.Path).Where(p => p is not null).Select(p => Path.GetDirectoryName(p)!)
                .Where(f => f.Length > 0),
            StringComparer.OrdinalIgnoreCase);

        foreach (string folder in _watchers.Keys.Where(f => !wanted.Contains(f)).ToList())
        {
            _watchers[folder].Dispose();
            _watchers.Remove(folder);
        }

        foreach (string folder in wanted.Where(f => !_watchers.ContainsKey(f)))
        {
            try
            {
                var watcher = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += OnFileSystemEvent;
                watcher.Created += OnFileSystemEvent;
                watcher.Renamed += OnFileSystemEvent;
                _watchers[folder] = watcher;
            }
            catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
            {
                // A folder on a removed drive or a path we cannot watch: carry on unwatched.
            }
        }
    }

    private void DisposeWatchers()
    {
        foreach (FileSystemWatcher watcher in _watchers.Values)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
    }

    /// <summary>Watcher callbacks arrive on a pool thread, and a single save raises several.</summary>
    private void OnFileSystemEvent(object sender, FileSystemEventArgs e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _changedOnDisk.Add(e.FullPath);

            _watchTimer ??= CreateWatchTimer();
            _watchTimer.Stop();
            _watchTimer.Start();
        });
    }

    private DispatcherTimer CreateWatchTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += async (_, _) =>
        {
            _watchTimer!.Stop();
            await ProcessExternalChangesAsync();
        };
        return timer;
    }

    private async Task ProcessExternalChangesAsync()
    {
        string[] paths = _changedOnDisk.ToArray();
        _changedOnDisk.Clear();

        foreach (string path in paths)
        {
            MdDocument? document = _documents.FirstOrDefault(
                d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase));
            if (document is null)
            {
                continue;
            }

            string onDisk;
            try
            {
                onDisk = await File.ReadAllTextAsync(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue; // Still being written; the next event will catch it.
            }

            string normalized = Normalize(onDisk);
            if (string.Equals(normalized, document.SavedText, StringComparison.Ordinal))
            {
                continue; // Our own write, or a touch that changed nothing.
            }

            bool ask = document.IsDirty
                || Settings.Current.Files.ExternalChange == ExternalChangeMode.AlwaysAsk;

            if (ask && !await ConfirmReloadAsync(document))
            {
                // Declining leaves the buffer alone but adopts the new baseline, so the
                // same change is not offered again on every subsequent save elsewhere.
                document.SavedText = normalized;
                document.IsDirty = true;
                UpdateTitle();
                continue;
            }

            ReloadDocument(document, onDisk, normalized);
        }
    }

    private async Task<bool> ConfirmReloadAsync(MdDocument document)
    {
        if (_reloadPromptOpen)
        {
            return false;
        }

        _reloadPromptOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "File changed on disk",
                Content = document.IsDirty
                    ? $"\"{document.Name}\" was changed by something else, and this tab has unsaved edits. Reloading discards them."
                    : $"\"{document.Name}\" was changed by something else.",
                PrimaryButtonText = "Reload",
                CloseButtonText = "Keep mine",
                DefaultButton = document.IsDirty ? ContentDialogButton.Close : ContentDialogButton.Primary,
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _reloadPromptOpen = false;
        }
    }

    private void ReloadDocument(MdDocument document, string text, string normalized)
    {
        document.Text = text;
        document.SavedText = normalized;
        document.IsDirty = false;

        if (ReferenceEquals(document, _current))
        {
            int caret = Editor.SelectionStart;
            double offset = PreviewScroller.VerticalOffset;
            SwitchTo(document, stashCurrent: false);
            Editor.Select(Math.Min(caret, Editor.Text.Length), 0);
            PreviewScroller.ChangeView(null, offset, null, disableAnimation: true);
        }

        UpdateTitle();
        StatusText.Text = $"{document.Path} — reloaded from disk";
    }
}
