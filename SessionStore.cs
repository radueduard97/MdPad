using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdPad;

/// <summary>One tab as it was when the window closed.</summary>
public sealed class SessionTab
{
    /// <summary>File the tab is on, or null for an untitled document.</summary>
    public string? Path { get; set; }

    /// <summary>Editor contents, stored only when they differ from what is on disk.</summary>
    public string? UnsavedText { get; set; }

    public int SelectionStart { get; set; }

    public double PreviewOffset { get; set; }
}

/// <summary>The window's state between runs: which documents were open, and how they were shown.</summary>
public sealed class SessionState
{
    public List<SessionTab> Tabs { get; set; } = new();

    public int ActiveIndex { get; set; }

    /// <summary>Name of the <c>ViewMode</c> value in force; unknown values fall back to Split.</summary>
    public string? ViewMode { get; set; }

    public bool OutlineVisible { get; set; } = true;
}

/// <summary>
/// Reads and writes the session file. Closing MdPad on a half-written skill and finding
/// the same tabs — including the unsaved ones — on the next launch is the difference
/// between an editor you leave open and one you have to set up each time.
/// </summary>
public static class SessionStore
{
    /// <summary>Ceiling on unsaved text kept per tab; past this the path alone is restored.</summary>
    private const int MaxUnsavedChars = 512 * 1024;

    /// <summary>Cap on restored tabs, so a runaway session file cannot stall startup.</summary>
    public const int MaxTabs = 40;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdPad",
        "session.json");

    /// <summary>The last saved session, or null when there is none to restore.</summary>
    public static SessionState? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return null;
            }

            SessionState? state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(FilePath), Options);
            if (state is null || state.Tabs.Count == 0)
            {
                return null;
            }

            if (state.Tabs.Count > MaxTabs)
            {
                state.Tabs = state.Tabs.GetRange(0, MaxTabs);
                state.ActiveIndex = Math.Clamp(state.ActiveIndex, 0, MaxTabs - 1);
            }
            return state;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unreadable session is not worth a dialog: start clean.
            return null;
        }
    }

    public static void Save(SessionState state)
    {
        foreach (SessionTab tab in state.Tabs)
        {
            if (tab.UnsavedText is { Length: > MaxUnsavedChars })
            {
                tab.UnsavedText = null;
            }
        }

        try
        {
            string? folder = Path.GetDirectoryName(FilePath);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(state, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Losing the session is a nuisance, not an error worth interrupting for.
        }
    }

    /// <summary>Forget the stored session — used when the user closes every tab deliberately.</summary>
    public static void Clear()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                File.Delete(FilePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
