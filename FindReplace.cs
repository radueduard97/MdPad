using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MdPad;

/// <summary>One occurrence of the search text, in a file on disk or in an open tab.</summary>
/// <param name="Path">Full path of the file the hit is in.</param>
/// <param name="Display">Path relative to the searched folder, for the results list.</param>
/// <param name="Line">Zero-based line the hit starts on.</param>
/// <param name="Offset">Character offset of the hit within the file.</param>
/// <param name="Preview">The hit's line, trimmed, for the results list.</param>
public sealed record SearchHit(string Path, string Display, int Line, int Offset, int Length, string Preview);

/// <summary>
/// Plain-text search and replace over the current document and over the folder around
/// it. A skill is a folder, not a file — renaming a section or a tool means touching
/// <c>SKILL.md</c> and every reference beside it — so folder scope is the point of this
/// rather than a nicety.
/// </summary>
public static class DocumentSearch
{
    /// <summary>Cap on files visited, so a skill nested in a large tree stays responsive.</summary>
    private const int MaxFiles = 500;

    /// <summary>Cap on hits collected; beyond this the list stops being something to read.</summary>
    public const int MaxHits = 500;

    /// <summary>Extensions searched in folder scope: the text a skill is actually made of.</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".mdown", ".txt", ".yaml", ".yml", ".json", ".toml", ".ini",
        ".csv", ".xml", ".html", ".css", ".js", ".ts", ".py", ".sh", ".ps1", ".rb", ".go",
        ".rs", ".cs", ".java", ".sql", ".env", ".gitignore",
    };

    private static StringComparison Comparison(bool matchCase) =>
        matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Offsets of every occurrence of <paramref name="query"/>, left to right.</summary>
    public static IEnumerable<int> Matches(string? text, string query, bool matchCase)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            yield break;
        }

        StringComparison comparison = Comparison(matchCase);
        int at = 0;
        while (at <= text.Length - query.Length)
        {
            int found = text.IndexOf(query, at, comparison);
            if (found < 0)
            {
                yield break;
            }
            yield return found;
            at = found + query.Length;  // Non-overlapping, so "aa" in "aaa" is one hit.
        }
    }

    public static int Count(string? text, string query, bool matchCase) => Matches(text, query, matchCase).Count();

    /// <summary>
    /// The first match at or after <paramref name="from"/>, wrapping to the top of the
    /// document if there is none. Returns -1 when the text does not occur at all.
    /// </summary>
    public static int NextMatch(string? text, string query, bool matchCase, int from)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        int start = Math.Clamp(from, 0, text.Length);
        int found = start <= text.Length - query.Length
            ? text.IndexOf(query, start, Comparison(matchCase))
            : -1;
        return found >= 0 ? found : text.IndexOf(query, 0, Comparison(matchCase));
    }

    /// <summary>The last match before <paramref name="before"/>, wrapping to the bottom.</summary>
    public static int PreviousMatch(string? text, string query, bool matchCase, int before)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return -1;
        }

        int last = -1;
        int wrapped = -1;
        foreach (int offset in Matches(text, query, matchCase))
        {
            wrapped = offset;
            if (offset < before)
            {
                last = offset;
            }
        }
        return last >= 0 ? last : wrapped;
    }

    /// <summary>Replace every occurrence, reporting how many were rewritten.</summary>
    public static string ReplaceAll(string text, string query, string replacement, bool matchCase, out int replaced)
    {
        replaced = 0;
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query))
        {
            return text;
        }

        var sb = new System.Text.StringBuilder(text.Length);
        int at = 0;
        foreach (int offset in Matches(text, query, matchCase))
        {
            sb.Append(text, at, offset - at).Append(replacement);
            at = offset + query.Length;
            replaced++;
        }
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    /// <summary>
    /// Every occurrence under <paramref name="folder"/>. <paramref name="liveText"/> lets
    /// the caller answer with the unsaved contents of an open tab, so results match what
    /// the author sees rather than what was last written to disk.
    /// </summary>
    public static List<SearchHit> SearchFolder(
        string folder,
        string query,
        bool matchCase,
        Func<string, string?>? liveText = null)
    {
        var hits = new List<SearchHit>();
        if (string.IsNullOrEmpty(query) || !Directory.Exists(folder))
        {
            return hits;
        }

        int files = 0;
        foreach (string path in EnumerateTextFiles(folder))
        {
            if (++files > MaxFiles || hits.Count >= MaxHits)
            {
                break;
            }

            string? text = liveText?.Invoke(path) ?? ReadOrNull(path);
            if (text is null)
            {
                continue;
            }

            CollectHits(text, query, matchCase, path, Relative(folder, path), hits);
        }

        return hits;
    }

    /// <summary>Hits within a single document already in memory.</summary>
    public static List<SearchHit> SearchText(string? text, string query, bool matchCase, string? path)
    {
        var hits = new List<SearchHit>();
        if (text is not null)
        {
            CollectHits(text, query, matchCase, path ?? string.Empty, path is null ? "Untitled" : Path.GetFileName(path), hits);
        }
        return hits;
    }

    private static void CollectHits(
        string text,
        string query,
        bool matchCase,
        string path,
        string display,
        List<SearchHit> hits)
    {
        foreach (int offset in Matches(text, query, matchCase))
        {
            if (hits.Count >= MaxHits)
            {
                return;
            }

            (int line, int lineStart, int lineEnd) = LineAt(text, offset);
            hits.Add(new SearchHit(path, display, line, offset, query.Length, Preview(text, lineStart, lineEnd)));
        }
    }

    /// <summary>Line index of an offset, with the bounds of that line.</summary>
    private static (int Line, int Start, int End) LineAt(string text, int offset)
    {
        int line = 0;
        int start = 0;
        for (int i = 0; i < offset; i++)
        {
            if (text[i] == '\n' || (text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')))
            {
                line++;
                start = i + 1;
            }
        }

        int end = text.IndexOfAny(new[] { '\r', '\n' }, offset);
        return (line, start, end < 0 ? text.Length : end);
    }

    private static string Preview(string text, int start, int end)
    {
        const int max = 120;
        string line = text[start..end].Trim();
        return line.Length <= max ? line : line[..max] + "…";
    }

    private static IEnumerable<string> EnumerateTextFiles(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        foreach (string file in files)
        {
            if (PathFilter.IsExcluded(file, folder, Settings.Current.Find.ExcludePatterns))
            {
                continue;
            }

            if (TextExtensions.Contains(Path.GetExtension(file)))
            {
                yield return file;
            }
        }
    }

    private static string? ReadOrNull(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // Largest file worth reading into memory to search.
            long limit = (long)Settings.Current.Find.MaxFileSizeKb * 1024;
            return !info.Exists || info.Length > limit ? null : File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string Relative(string folder, string full)
    {
        try
        {
            return Path.GetRelativePath(folder, full).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (ArgumentException)
        {
            return Path.GetFileName(full);
        }
    }
}
