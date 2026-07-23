using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

using Markdig;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MdPad;

/// <summary>How a file relates to the document being edited.</summary>
public enum ReferenceState
{
    /// <summary>Linked from the document, and the file is there.</summary>
    Linked,

    /// <summary>Linked from the document, but nothing is at that path.</summary>
    Missing,

    /// <summary>Sitting in the skill folder, but nothing links to it.</summary>
    Orphan,
}

/// <summary>A file the document points at (or should).</summary>
public sealed record DocumentReference(string Display, string? FullPath, ReferenceState State, int Tokens);

/// <summary>
/// What a skill costs an agent, split by when each part enters the context window:
/// name + description are always present, the body arrives when the skill is invoked,
/// and referenced files are only read on demand.
/// </summary>
public sealed record SkillBudget(
    bool HasFrontMatter,
    int AlwaysTokens,
    int InvokeTokens,
    int OnDemandTokens,
    IReadOnlyList<DocumentReference> References)
{
    public static readonly SkillBudget Empty = new(false, 0, 0, 0, Array.Empty<DocumentReference>());

    public int LinkedCount => References.Count(r => r.State == ReferenceState.Linked);

    public int MissingCount => References.Count(r => r.State == ReferenceState.Missing);

    public int OrphanCount => References.Count(r => r.State == ReferenceState.Orphan);
}

public static class SkillAnalyzer
{
    /// <summary>Largest file worth reading for a token estimate.</summary>
    private const long MaxReferenceBytes = 512 * 1024;

    /// <summary>Cap on folder enumeration, so a skill inside a huge tree stays responsive.</summary>
    private const int MaxFolderFiles = 300;

    /// <summary>Token counts for reference files, keyed by path and invalidated by write time.</summary>
    private static readonly Dictionary<string, (DateTime Stamp, int Tokens)> TokenCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rough token estimate. Real tokenisers are model-specific; ~4 characters per
    /// token is the usual approximation for English prose and code, and it is close
    /// enough to make budget decisions with.
    /// </summary>
    public static int EstimateTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    public static SkillBudget Analyze(string? markdown, string? documentPath)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return SkillBudget.Empty;
        }

        MarkdownDocument doc = Markdown.Parse(markdown, MarkdownRenderer.Pipeline);

        YamlFrontMatterBlock? yaml = doc.OfType<YamlFrontMatterBlock>().FirstOrDefault();
        List<(string Key, string Value)> fields = yaml is null
            ? new List<(string, string)>()
            : FrontMatter.Parse(yaml);

        string? name = FrontMatter.Find(fields, "name") ?? FrontMatter.Find(fields, "title");
        string? description = FrontMatter.Find(fields, "description") ?? FrontMatter.Find(fields, "summary");

        // The skill listing carries name + description for every skill, always.
        int always = EstimateTokens(name) + EstimateTokens(description);
        int invoke = EstimateTokens(markdown);

        List<DocumentReference> references = CollectReferences(doc, documentPath);
        int onDemand = references.Where(r => r.State != ReferenceState.Missing).Sum(r => r.Tokens);

        return new SkillBudget(yaml is not null, always, invoke, onDemand, references);
    }

    private static List<DocumentReference> CollectReferences(MarkdownDocument doc, string? documentPath)
    {
        var references = new List<DocumentReference>();
        if (documentPath is null)
        {
            return references;
        }

        string? folder = Path.GetDirectoryName(documentPath);
        if (folder is null)
        {
            return references;
        }

        var linked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (LinkInline link in doc.Descendants<LinkInline>())
        {
            string? url = link.Url;
            if (!IsLocalPath(url))
            {
                continue;
            }

            string relative = StripAnchor(Uri.UnescapeDataString(url!));
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(folder, relative));
            }
            catch (ArgumentException)
            {
                continue; // Illegal characters in the link target.
            }

            if (!linked.Add(full))
            {
                continue; // Same file linked more than once.
            }

            references.Add(File.Exists(full)
                ? new DocumentReference(Relative(folder, full), full, ReferenceState.Linked, CountTokens(full))
                : new DocumentReference(relative, full, ReferenceState.Missing, 0));
        }

        // Anything in the folder that nothing points at: usually a rename left behind,
        // or a file the author forgot to wire up.
        foreach (string file in EnumerateSkillFiles(folder))
        {
            if (!linked.Contains(file) && !string.Equals(file, documentPath, StringComparison.OrdinalIgnoreCase))
            {
                references.Add(new DocumentReference(Relative(folder, file), file, ReferenceState.Orphan, CountTokens(file)));
            }
        }

        return references
            .OrderBy(r => r.State)
            .ThenBy(r => r.Display, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Markdown files under the document's folder, excluding noise directories.</summary>
    private static IEnumerable<string> EnumerateSkillFiles(string folder)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*.md", SearchOption.AllDirectories);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            yield break;
        }

        int count = 0;
        foreach (string file in files)
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                || file.Contains($"{Path.DirectorySeparatorChar}.git{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            yield return file;
            if (++count >= MaxFolderFiles)
            {
                yield break;
            }
        }
    }

    /// <summary>True for links that point at a file next to the document rather than the web.</summary>
    public static bool IsLocalPath(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('#'))
        {
            return false;
        }
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !uri.IsFile)
        {
            return false; // http, https, mailto, …
        }
        return true;
    }

    public static string StripAnchor(string url)
    {
        int hash = url.IndexOf('#');
        return hash < 0 ? url : url[..hash];
    }

    /// <summary>Resolve a link target against the folder of <paramref name="documentPath"/>.</summary>
    public static string? ResolveLocalPath(string? url, string? documentPath)
    {
        if (!IsLocalPath(url) || documentPath is null)
        {
            return null;
        }

        string? folder = Path.GetDirectoryName(documentPath);
        if (folder is null)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(Path.Combine(folder, StripAnchor(Uri.UnescapeDataString(url!))));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Relative(string folder, string full)
    {
        string relative = Path.GetRelativePath(folder, full);
        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static int CountTokens(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxReferenceBytes)
            {
                return 0;
            }

            if (TokenCache.TryGetValue(path, out var cached) && cached.Stamp == info.LastWriteTimeUtc)
            {
                return cached.Tokens;
            }

            int tokens = EstimateTokens(File.ReadAllText(path));
            TokenCache[path] = (info.LastWriteTimeUtc, tokens);
            return tokens;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>Compact token count for status display: 850, 2.4k, 15k.</summary>
    public static string Format(int tokens) => tokens switch
    {
        < 1000 => tokens.ToString(),
        < 10000 => (tokens / 1000.0).ToString("0.0") + "k",
        _ => (tokens / 1000).ToString() + "k",
    };
}

/// <summary>
/// Minimal YAML front matter reader: top-level "key: value" pairs, with indented
/// lines and "- item" entries folded into the preceding value. Enough for the
/// metadata blocks that lead skill files and static-site pages.
/// </summary>
public static class FrontMatter
{
    public static List<(string Key, string Value)> Parse(YamlFrontMatterBlock block)
    {
        var fields = new List<(string Key, string Value)>();
        var pending = new StringBuilder();
        string? key = null;

        foreach (string raw in block.Lines.Lines.Take(block.Lines.Count).Select(l => l.ToString()))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0 || line.Trim() is "---" or "...")
            {
                continue;
            }

            bool isContinuation = char.IsWhiteSpace(raw[0]) || line.TrimStart().StartsWith("- ", StringComparison.Ordinal);
            int colon = line.IndexOf(':');

            if (!isContinuation && colon > 0)
            {
                Flush();
                key = line[..colon].Trim();
                pending.Append(line[(colon + 1)..].Trim());
            }
            else if (key is not null)
            {
                pending.Append(pending.Length > 0 ? " " : string.Empty).Append(line.Trim());
            }
        }
        Flush();
        return fields;

        void Flush()
        {
            if (key is not null)
            {
                fields.Add((key, Unquote(pending.ToString())));
            }
            key = null;
            pending.Clear();
        }

        static string Unquote(string value) =>
            value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                ? value[1..^1]
                : value;
    }

    public static string? Find(List<(string Key, string Value)> fields, string key) =>
        fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
}
