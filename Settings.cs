using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace MdPad;

public enum AppTheme { System, Light, Dark }

/// <summary>
/// The preview can be themed against the app deliberately: a README is going to be read
/// on GitHub in whichever theme the reader uses, and checking that is the point.
/// </summary>
public enum PreviewTheme { FollowApp, Light, Dark }

public enum BackdropKind { Mica, MicaAlt, Acrylic, Solid }

public enum AccentSource { System, Custom }

public enum LineEndingKind { Crlf, Lf }

public enum AutosaveMode { Off, OnFocusLoss, Interval }

public enum StartupMode { RestoreSession, BlankTab, Ask }

/// <summary>What to do when a file changes on disk while it is open in a tab.</summary>
public enum ExternalChangeMode { ReloadClean, AlwaysAsk, Ignore }

/// <summary>Which documents the skill panels apply to.</summary>
public enum SkillDetection { SkillFileOrFrontMatter, SkillFileOnly }

public enum OrphanScope { MarkdownOnly, AllFiles }

public enum SearchScope { File, Folder }

/// <summary>A per-rule override of a validation issue's severity; <c>Default</c> leaves it alone.</summary>
public enum RuleSeverity { Default, Error, Warning, Suggestion, Off }

public sealed class AppearanceSettings
{
    public AppTheme Theme { get; set; } = AppTheme.System;

    public PreviewTheme PreviewTheme { get; set; } = PreviewTheme.FollowApp;

    public BackdropKind Backdrop { get; set; } = BackdropKind.Mica;

    public AccentSource Accent { get; set; } = AccentSource.System;

    /// <summary>Accent colour as <c>#RRGGBB</c>, used only when <see cref="Accent"/> is Custom.</summary>
    public string AccentColor { get; set; } = "#4C8DFF";

    /// <summary>Widest the rendered preview is allowed to get, in DIPs. 0 means unlimited.</summary>
    public double PreviewMaxWidth { get; set; } = 820;
}

public sealed class FontSettings
{
    public string EditorFamily { get; set; } = "Consolas";

    public double EditorSize { get; set; } = 14;

    public string PreviewFamily { get; set; } = "Segoe UI";

    public double PreviewSize { get; set; } = 14;

    /// <summary>Preview body line height in DIPs; the editor's is fixed by WinUI's TextBox.</summary>
    public double PreviewLineHeight { get; set; } = 22;

    public string CodeFamily { get; set; } = "Consolas";

    public double CodeSize { get; set; } = 13;

    /// <summary>Multiplier over every configured size, driven by Ctrl+= / Ctrl+-.</summary>
    public double Zoom { get; set; } = 1.0;
}

public sealed class EditorSettings
{
    public bool WordWrap { get; set; } = true;

    public bool SpellCheck { get; set; }

    public bool ShowLineNumbers { get; set; }

    public bool TabInsertsSpaces { get; set; } = true;

    public int TabWidth { get; set; } = 2;

    public bool ContinueLists { get; set; } = true;

    public bool AutoCloseMarkers { get; set; } = true;

    public bool TrimTrailingWhitespace { get; set; }

    public bool EnsureTrailingNewline { get; set; } = true;

    public LineEndingKind LineEnding { get; set; } = LineEndingKind.Lf;
}

public sealed class SkillSettings
{
    /// <summary>Where <em>New skill</em> starts, and the listing MdPad compares against.</summary>
    public string SkillsFolder { get; set; } = string.Empty;

    /// <summary>Characters per token for the estimate; the usual approximation is 4.</summary>
    public double CharsPerToken { get; set; } = 4.0;

    /// <summary>On-invoke token count past which the budget meter turns amber.</summary>
    public int BudgetWarnTokens { get; set; } = 5000;

    /// <summary>On-invoke token count past which it turns red.</summary>
    public int BudgetErrorTokens { get; set; } = 10000;

    public SkillDetection Detection { get; set; } = SkillDetection.SkillFileOrFrontMatter;

    public OrphanScope OrphanScope { get; set; } = OrphanScope.MarkdownOnly;

    /// <summary>Names, folders or <c>*.ext</c> patterns skipped when scanning a skill folder.</summary>
    public List<string> IgnorePatterns { get; set; } = new() { ".git", "node_modules", "bin", "obj", "*.png", "*.jpg", "*.gif", "*.zip" };

    public int ThinDescriptionLength { get; set; } = 40;

    public int BloatedDescriptionLength { get; set; } = 600;

    /// <summary>Per-rule severity overrides, keyed by the ids in <see cref="SkillRules"/>.</summary>
    public Dictionary<string, RuleSeverity> RuleSeverities { get; set; } = new(StringComparer.Ordinal);
}

public sealed class FileSettings
{
    public StartupMode Startup { get; set; } = StartupMode.RestoreSession;

    public AutosaveMode Autosave { get; set; } = AutosaveMode.Off;

    public int AutosaveSeconds { get; set; } = 30;

    public ExternalChangeMode ExternalChange { get; set; } = ExternalChangeMode.ReloadClean;
}

public sealed class FindSettings
{
    public SearchScope DefaultScope { get; set; } = SearchScope.File;

    public bool RememberOptions { get; set; } = true;

    /// <summary>Last state of the match-case toggle, restored when <see cref="RememberOptions"/> is set.</summary>
    public bool MatchCase { get; set; }

    /// <summary>Files larger than this are skipped by a folder search.</summary>
    public int MaxFileSizeKb { get; set; } = 2048;

    /// <summary>Above this many hits, a folder-wide Replace all asks first.</summary>
    public int ConfirmReplaceAbove { get; set; } = 1;

    public List<string> ExcludePatterns { get; set; } = new() { ".git", "node_modules", "bin", "obj" };
}

/// <summary>
/// Everything the settings pane writes. Nested rather than flat because the file is
/// meant to be readable — an agent author who keeps dotfiles in a repo will edit it
/// by hand as often as through the dialog.
/// </summary>
public sealed class AppSettings
{
    public AppearanceSettings Appearance { get; set; } = new();

    public FontSettings Fonts { get; set; } = new();

    public EditorSettings Editor { get; set; } = new();

    public SkillSettings Skills { get; set; } = new();

    public FileSettings Files { get; set; } = new();

    public FindSettings Find { get; set; } = new();
}

/// <summary>
/// The live settings object, the file behind it, and the one event everything else
/// listens to. Static because there is one window and one configuration; a change
/// notification is cheaper than threading a settings reference through every control.
/// </summary>
public static class Settings
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Writes are coalesced: a slider drag would otherwise rewrite the file per frame.</summary>
    private static readonly Timer SaveTimer = new(_ => Save(), null, Timeout.Infinite, Timeout.Infinite);

    private static AppSettings _current = Load();

    public static AppSettings Current => _current;

    /// <summary>Raised after any change, on the thread that made it.</summary>
    public static event Action? Changed;

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MdPad",
        "settings.json");

    /// <summary>Publish a change: everything re-reads <see cref="Current"/>, and the file follows.</summary>
    public static void Apply()
    {
        Changed?.Invoke();
        SaveTimer.Change(600, Timeout.Infinite);
    }

    public static void Reset()
    {
        _current = new AppSettings();
        Apply();
    }

    /// <summary>Re-read the file, for when it has been edited outside the dialog.</summary>
    public static void Reload()
    {
        _current = Load();
        Apply();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) is { } loaded)
            {
                return Sanitize(loaded);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A hand-edited file with a typo in it should not stop the app opening.
        }
        return new AppSettings();
    }

    public static void Save()
    {
        try
        {
            string? folder = Path.GetDirectoryName(FilePath);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_current, Options));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    /// <summary>
    /// Clamp anything a hand-edited file could put out of range. A zero font size or a
    /// negative tab width would otherwise take the window down on the next render.
    /// </summary>
    private static AppSettings Sanitize(AppSettings s)
    {
        s.Appearance ??= new AppearanceSettings();
        s.Fonts ??= new FontSettings();
        s.Editor ??= new EditorSettings();
        s.Skills ??= new SkillSettings();
        s.Files ??= new FileSettings();
        s.Find ??= new FindSettings();

        s.Fonts.EditorSize = Math.Clamp(s.Fonts.EditorSize, 8, 48);
        s.Fonts.PreviewSize = Math.Clamp(s.Fonts.PreviewSize, 8, 48);
        s.Fonts.CodeSize = Math.Clamp(s.Fonts.CodeSize, 8, 48);
        s.Fonts.PreviewLineHeight = Math.Clamp(s.Fonts.PreviewLineHeight, 0, 80);
        s.Fonts.Zoom = Math.Clamp(s.Fonts.Zoom, 0.5, 3.0);
        s.Fonts.EditorFamily = Fallback(s.Fonts.EditorFamily, "Consolas");
        s.Fonts.CodeFamily = Fallback(s.Fonts.CodeFamily, "Consolas");
        s.Fonts.PreviewFamily = Fallback(s.Fonts.PreviewFamily, "Segoe UI");

        s.Appearance.PreviewMaxWidth = s.Appearance.PreviewMaxWidth <= 0
            ? 0
            : Math.Clamp(s.Appearance.PreviewMaxWidth, 320, 4000);

        s.Editor.TabWidth = Math.Clamp(s.Editor.TabWidth, 1, 8);

        s.Skills.CharsPerToken = Math.Clamp(s.Skills.CharsPerToken, 1, 20);
        s.Skills.BudgetWarnTokens = Math.Max(0, s.Skills.BudgetWarnTokens);
        s.Skills.BudgetErrorTokens = Math.Max(s.Skills.BudgetWarnTokens, s.Skills.BudgetErrorTokens);
        s.Skills.ThinDescriptionLength = Math.Clamp(s.Skills.ThinDescriptionLength, 0, 1024);
        s.Skills.BloatedDescriptionLength = Math.Clamp(s.Skills.BloatedDescriptionLength, s.Skills.ThinDescriptionLength, 1024);
        s.Skills.IgnorePatterns ??= new List<string>();
        s.Skills.RuleSeverities ??= new Dictionary<string, RuleSeverity>(StringComparer.Ordinal);

        s.Files.AutosaveSeconds = Math.Clamp(s.Files.AutosaveSeconds, 5, 3600);

        s.Find.MaxFileSizeKb = Math.Clamp(s.Find.MaxFileSizeKb, 1, 102400);
        s.Find.ConfirmReplaceAbove = Math.Max(0, s.Find.ConfirmReplaceAbove);
        s.Find.ExcludePatterns ??= new List<string>();

        return s;

        static string Fallback(string? value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}

/// <summary>
/// Path matching for the ignore and exclude lists. Deliberately small: a bare name
/// (<c>node_modules</c>) hides a folder or file anywhere under the root, and a
/// <c>*.ext</c> pattern hides files by extension. Full glob syntax would be more
/// than these two lists are ever asked to express.
/// </summary>
public static class PathFilter
{
    public static bool IsExcluded(string fullPath, string root, IReadOnlyList<string> patterns)
    {
        if (patterns.Count == 0)
        {
            return false;
        }

        string relative;
        try
        {
            relative = Path.GetRelativePath(root, fullPath);
        }
        catch (ArgumentException)
        {
            relative = Path.GetFileName(fullPath);
        }

        string[] segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = segments.Length > 0 ? segments[^1] : relative;

        foreach (string raw in patterns)
        {
            string pattern = raw.Trim().TrimEnd('/', '\\');
            if (pattern.Length == 0)
            {
                continue;
            }

            if (pattern.Contains('*') || pattern.Contains('?'))
            {
                if (Matches(name, pattern))
                {
                    return true;
                }
                continue;
            }

            foreach (string segment in segments)
            {
                if (string.Equals(segment, pattern, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Wildcard match over a single name: <c>*</c> spans anything, <c>?</c> one character.</summary>
    private static bool Matches(string name, string pattern)
    {
        int n = 0, p = 0, starN = -1, starP = -1;
        while (n < name.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToLowerInvariant(pattern[p]) == char.ToLowerInvariant(name[n])))
            {
                n++;
                p++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                starP = p++;
                starN = n;
            }
            else if (starP >= 0)
            {
                p = starP + 1;
                n = ++starN;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*')
        {
            p++;
        }
        return p == pattern.Length;
    }
}
