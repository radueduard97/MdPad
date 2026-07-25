using System;
using System.IO;
using System.Threading.Tasks;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MdPad;

/// <summary>
/// Session restore: the tabs that were open, where the caret and preview were in each,
/// and the view the window was in. Unsaved work is kept too — a skill half-written when
/// the machine reboots is exactly the work worth not losing.
/// </summary>
public sealed partial class MainPage : Page
{
    /// <summary>How long changes are allowed to accumulate before the session is rewritten.</summary>
    private static readonly TimeSpan SessionSaveDelay = TimeSpan.FromSeconds(4);

    private DispatcherTimer? _sessionTimer;

    /// <summary>False until the restore has run, so an early save cannot clobber the stored session.</summary>
    private bool _sessionReady;

    private void InitializeSession()
    {
        _sessionTimer = new DispatcherTimer { Interval = SessionSaveDelay };
        _sessionTimer.Tick += (_, _) =>
        {
            _sessionTimer!.Stop();
            SaveSession();
        };
    }

    /// <summary>
    /// Note that the session has moved on. The timer is left running rather than
    /// restarted, so continuous typing still gets written out on a fixed cadence
    /// instead of being deferred until the user pauses.
    /// </summary>
    private void MarkSessionDirty()
    {
        if (_sessionReady && _sessionTimer is { IsEnabled: false })
        {
            _sessionTimer.Start();
        }
    }

    /// <summary>Reopen the previous session; false when there was nothing worth restoring.</summary>
    private async Task<bool> RestoreSessionAsync()
    {
        StartupMode startup = Settings.Current.Files.Startup;
        if (startup == StartupMode.BlankTab)
        {
            return false;
        }

        SessionState? state = SessionStore.Load();
        if (state is null)
        {
            return false;
        }

        if (startup == StartupMode.Ask && !await ConfirmRestoreAsync(state.Tabs.Count))
        {
            return false;
        }

        foreach (SessionTab tab in state.Tabs)
        {
            if (LoadSessionTab(tab) is { } document)
            {
                _documents.Add(document);
            }
        }

        if (_documents.Count == 0)
        {
            return false;  // Every file was moved or deleted since last time.
        }

        SetOutlineVisible(state.OutlineVisible);
        SelectDocument(_documents[Math.Clamp(state.ActiveIndex, 0, _documents.Count - 1)]);

        if (Enum.TryParse(state.ViewMode, out ViewMode mode))
        {
            SetViewMode(mode);
        }

        // The restored caret only lands once the editor has the text and focus.
        Editor.Focus(FocusState.Programmatic);
        Editor.Select(Math.Min(_current.SelectionStart, Editor.Text.Length), 0);
        RefreshWatchers();
        return true;
    }

    private async Task<bool> ConfirmRestoreAsync(int tabCount)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Reopen last session?",
            Content = $"{tabCount} tab{(tabCount == 1 ? string.Empty : "s")} were open when MdPad last closed.",
            PrimaryButtonText = "Reopen",
            CloseButtonText = "Start fresh",
            DefaultButton = ContentDialogButton.Primary,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>
    /// Rebuild one document. A file that has since been edited elsewhere is reloaded from
    /// disk; one with unsaved changes keeps them, and stays marked dirty so the difference
    /// is visible rather than silently resolved.
    /// </summary>
    private static MdDocument? LoadSessionTab(SessionTab tab)
    {
        string? onDisk = null;
        if (tab.Path is not null)
        {
            try
            {
                if (!File.Exists(tab.Path))
                {
                    return null;
                }
                onDisk = File.ReadAllText(tab.Path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        string text = tab.UnsavedText ?? onDisk ?? string.Empty;
        if (tab.Path is null && string.IsNullOrWhiteSpace(text))
        {
            return null;  // An empty untitled tab is not worth carrying over.
        }

        string savedText = onDisk is null ? string.Empty : Normalize(onDisk);
        var document = new MdDocument
        {
            Path = tab.Path,
            Text = text,
            SavedText = savedText,
            SelectionStart = tab.SelectionStart,
            PreviewOffset = tab.PreviewOffset,
        };
        document.IsDirty = !string.Equals(Normalize(text), savedText, StringComparison.Ordinal);
        return document;
    }

    /// <summary>Write the current window state out, or clear the file when nothing is worth keeping.</summary>
    private void SaveSession()
    {
        if (!_sessionReady)
        {
            return;
        }

        StashCurrent();

        var state = new SessionState
        {
            ViewMode = _mode.ToString(),
            OutlineVisible = OutlineMenuItem.IsChecked,
        };

        foreach (MdDocument document in _documents)
        {
            // A saved file is remembered by path; an untitled tab only earns a place
            // once it has content the user would miss. The welcome sample has neither.
            if (document.Path is null && !document.IsDirty)
            {
                continue;
            }

            if (ReferenceEquals(document, _current))
            {
                state.ActiveIndex = state.Tabs.Count;
            }

            state.Tabs.Add(new SessionTab
            {
                Path = document.Path,
                UnsavedText = document.IsDirty ? document.Text : null,
                SelectionStart = document.SelectionStart,
                PreviewOffset = document.PreviewOffset,
            });

            if (state.Tabs.Count >= SessionStore.MaxTabs)
            {
                break;
            }
        }

        if (state.Tabs.Count == 0)
        {
            SessionStore.Clear();
            return;
        }

        SessionStore.Save(state);
    }
}
