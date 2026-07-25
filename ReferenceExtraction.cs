using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace MdPad;

/// <summary>A run of the document that can be moved out into its own file.</summary>
/// <param name="Start">Offset of the first character, always at the start of a line.</param>
/// <param name="End">Offset just past the last non-blank character.</param>
/// <param name="Title">What the section is called: its heading, or its opening words.</param>
/// <param name="Level">Heading level of the opening line, or 0 when the section does not start with one.</param>
public sealed record ExtractionSection(int Start, int End, string Text, string Title, int Level)
{
    public bool StartsWithHeading => Level > 0;

    /// <summary>Lines the section covers, for the "what moves" line in the dialog.</summary>
    public int LineCount => Text.Split('\n').Length;
}

/// <summary>Everything the extraction will do, worked out before anything is written.</summary>
/// <param name="LinkTarget">Relative, forward-slashed path as it will appear in the link.</param>
/// <param name="FileContent">What lands in the new file, with \n breaks.</param>
/// <param name="Replacement">What takes the section's place in the editor, with \r breaks.</param>
public sealed record ExtractionPlan(
    ExtractionSection Section,
    string FilePath,
    string LinkTarget,
    string FileContent,
    string Replacement,
    int MovedTokens,
    int RemainingTokens);

/// <summary>
/// Moving a section out of a skill body and into <c>references/</c>, leaving a link behind.
///
/// This is the edit the budget meter asks for. A skill body is paid for on every invoke,
/// so detail that only some runs need belongs in a file the agent reads on demand — but
/// doing that by hand means cutting, naming, fixing heading levels and writing the link,
/// which is enough friction that bodies just keep growing instead.
///
/// The logic here is deliberately free of UI: given the text and a selection it works out
/// the whole edit, so the dialog can show the resulting token count before committing to it.
/// </summary>
public static class ReferenceExtractor
{
    /// <summary>Used when a section has no usable words to name it after.</summary>
    private const string FallbackSlug = "reference";

    /// <summary>Longest title taken from prose when the section has no heading.</summary>
    private const int MaxDerivedTitle = 60;

    /// <summary>
    /// The section to extract for the given selection. A selection that spans lines is
    /// taken as written; a caret or a selection inside one line means "the section I am
    /// in", which is the enclosing heading and everything under it. Null when neither
    /// applies — a caret above the first heading has no section to take.
    /// </summary>
    public static ExtractionSection? Section(string text, int selectionStart, int selectionLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        List<SourceLine> lines = Scan(text);
        int start = Math.Clamp(selectionStart, 0, text.Length);
        int end = Math.Clamp(selectionStart + Math.Max(selectionLength, 0), 0, text.Length);

        int firstLine = LineAt(lines, start);
        int lastLine = LineAt(lines, Math.Max(end - 1, start));

        (int from, int to) = lastLine > firstLine
            ? (firstLine, lastLine)
            : Enclosing(lines, firstLine);

        if (from < 0 || to < from)
        {
            return null;
        }

        // Front matter is the one part of the body that cannot be moved: it is the skill's
        // contract, not its content.
        while (from <= to && lines[from].IsFrontMatter)
        {
            from++;
        }
        while (to >= from && string.IsNullOrWhiteSpace(lines[to].Text))
        {
            to--;
        }
        while (from <= to && string.IsNullOrWhiteSpace(lines[from].Text))
        {
            from++;
        }
        if (from > to)
        {
            return null;
        }

        int spanStart = lines[from].Start;
        int spanEnd = lines[to].End;
        string span = text[spanStart..spanEnd];
        int level = lines[from].HeadingLevel;

        return new ExtractionSection(spanStart, spanEnd, span, TitleOf(lines, from, to), level);
    }

    /// <summary>
    /// Where the new file goes: <c>references/</c> beside the document, unless the document
    /// already lives in a references folder, in which case its neighbours are the right place.
    /// </summary>
    public static string ReferencesFolder(string documentPath)
    {
        string folder = Path.GetDirectoryName(documentPath) ?? string.Empty;
        return string.Equals(Path.GetFileName(folder), SkillScaffold.ReferencesFolder, StringComparison.OrdinalIgnoreCase)
            ? folder
            : Path.Combine(folder, SkillScaffold.ReferencesFolder);
    }

    /// <summary>A kebab-case file name from the section's title, stepped until it is free.</summary>
    public static string DefaultFileName(ExtractionSection section, string documentPath)
    {
        string slug = SkillValidator.ToKebabCase(section.Title);
        if (slug.Length == 0)
        {
            slug = FallbackSlug;
        }

        string folder = ReferencesFolder(documentPath);
        string candidate = slug + ".md";
        for (int n = 2; File.Exists(Path.Combine(folder, candidate)); n++)
        {
            candidate = $"{slug}-{n}.md";
        }
        return candidate;
    }

    /// <summary>Why this file name cannot be used, or null if it can.</summary>
    public static string? FileNameProblem(string documentPath, string fileName)
    {
        string name = WithExtension(fileName);
        if (name.Length <= ".md".Length)
        {
            return "Give the reference a file name.";
        }

        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "That name contains characters a file cannot have.";
        }

        return File.Exists(Path.Combine(ReferencesFolder(documentPath), name))
            ? $"\"{name}\" already exists — pick another name."
            : null;
    }

    /// <summary>Work out the whole edit: the new file, the link left behind, and what it saves.</summary>
    /// <param name="note">Optional half-sentence after the link, saying when the file is worth reading.</param>
    public static ExtractionPlan Plan(string text, ExtractionSection section, string documentPath, string fileName, string? note)
    {
        string name = WithExtension(fileName);
        string folder = ReferencesFolder(documentPath);
        string fullPath = Path.Combine(folder, name);

        string documentFolder = Path.GetDirectoryName(documentPath) ?? string.Empty;
        string linkTarget = Path.GetRelativePath(documentFolder, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(" ", "%20");

        string replacement = BuildReplacement(section, linkTarget, note);
        string after = text.Remove(section.Start, section.End - section.Start).Insert(section.Start, replacement);

        return new ExtractionPlan(
            section,
            fullPath,
            linkTarget,
            BuildFileContent(section),
            replacement,
            SkillAnalyzer.EstimateTokens(section.Text),
            SkillAnalyzer.EstimateTokens(after));
    }

    /// <summary>
    /// The new file, as a document in its own right: headings are lifted so the section's
    /// own title becomes the H1, because a file that opens at <c>###</c> reads like a
    /// fragment of something else.
    /// </summary>
    private static string BuildFileContent(ExtractionSection section)
    {
        List<SourceLine> lines = Scan(section.Text);
        int top = lines.Where(l => l.HeadingLevel > 0).Select(l => l.HeadingLevel).DefaultIfEmpty(0).Min();

        // With a heading of its own the section becomes the H1; without one it gets a title
        // and everything inside nests below it.
        int shift = top == 0 ? 0 : (section.StartsWithHeading ? 1 : 2) - top;

        var builder = new StringBuilder();
        if (!section.StartsWithHeading)
        {
            builder.Append("# ").Append(section.Title).Append("\n\n");
        }

        foreach (SourceLine line in lines)
        {
            builder.Append(line.HeadingLevel > 0 && shift != 0
                ? Reheading(line.Text, Math.Clamp(line.HeadingLevel + shift, 1, 6))
                : line.Text).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// What stays in the body. A heading keeps its place — the outline, and any link that
    /// points at its anchor, should survive the move — with a single line under it saying
    /// where the detail went.
    /// </summary>
    private static string BuildReplacement(ExtractionSection section, string linkTarget, string? note)
    {
        string trimmed = note?.Trim().TrimEnd('.') ?? string.Empty;
        string sentence = $"See [{LinkLabel(section.Title)}]({linkTarget})"
            + (trimmed.Length > 0 ? $" — {trimmed}." : ".");

        if (!section.StartsWithHeading)
        {
            return sentence;
        }

        string heading = section.Text.Split('\n', '\r')[0].TrimEnd();
        return heading + "\r\r" + sentence;
    }

    /// <summary>Brackets inside link text would close it early; parentheses read the same.</summary>
    private static string LinkLabel(string title) => title.Replace('[', '(').Replace(']', ')');

    private static string Reheading(string line, int level)
    {
        string trimmed = line.TrimStart();
        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }
        return new string('#', level) + trimmed[hashes..];
    }

    /// <summary>The heading that owns <paramref name="line"/>, and the last line under it.</summary>
    private static (int From, int To) Enclosing(List<SourceLine> lines, int line)
    {
        int from = line;
        while (from >= 0 && (lines[from].HeadingLevel == 0 || lines[from].IsFrontMatter))
        {
            from--;
        }
        if (from < 0)
        {
            return (-1, -1);
        }

        int level = lines[from].HeadingLevel;
        int to = from;
        // A section ends where the next heading at the same level, or a shallower one,
        // begins; everything deeper belongs to it.
        while (to + 1 < lines.Count && !(lines[to + 1].HeadingLevel is int next && next > 0 && next <= level))
        {
            to++;
        }
        return (from, to);
    }

    private static string TitleOf(List<SourceLine> lines, int from, int to)
    {
        if (lines[from].HeadingLevel > 0)
        {
            string heading = lines[from].Text.TrimStart().TrimStart('#').Trim().TrimEnd('#').Trim();
            if (heading.Length > 0)
            {
                return heading;
            }
        }

        // No heading: name it after its opening words, which is what an author would have
        // called it anyway.
        for (int i = from; i <= to; i++)
        {
            string line = lines[i].Text.Trim().TrimStart('#', '-', '*', '>', ' ').Trim();
            if (line.Length == 0)
            {
                continue;
            }

            return line.Length <= MaxDerivedTitle
                ? line
                : line[..(line.LastIndexOf(' ', MaxDerivedTitle) is int cut and > 0 ? cut : MaxDerivedTitle)].TrimEnd() + "…";
        }

        return "Reference";
    }

    private static string WithExtension(string fileName)
    {
        string name = fileName.Trim();
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name : name + ".md";
    }

    private static int LineAt(List<SourceLine> lines, int offset)
    {
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (offset >= lines[i].Start)
            {
                return i;
            }
        }
        return 0;
    }

    /// <summary>One source line, with what the extractor needs to know about it.</summary>
    private sealed record SourceLine(int Start, int End, string Text, int HeadingLevel, bool IsFrontMatter);

    /// <summary>
    /// Split into lines, marking headings. Fenced code is tracked so a <c>#</c> comment in a
    /// shell block never reads as a heading, and front matter is marked so it stays put.
    /// Setext underlines are not recognised: skill files are written with ATX headings, and
    /// a lone "---" is too easily a rule or a front matter terminator to guess at.
    /// </summary>
    private static List<SourceLine> Scan(string text)
    {
        var lines = new List<SourceLine>();
        bool inFence = false;
        string? fence = null;
        bool inFrontMatter = false;
        bool seenContent = false;

        int index = 0;
        while (index <= text.Length)
        {
            int breakAt = text.IndexOfAny(new[] { '\r', '\n' }, index);
            int end = breakAt < 0 ? text.Length : breakAt;
            string line = text[index..end];
            string trimmed = line.TrimStart();

            bool frontMatterLine = false;
            if (!inFence && trimmed.StartsWith("---", StringComparison.Ordinal) && trimmed.TrimEnd() == "---")
            {
                if (!seenContent && !inFrontMatter && lines.All(l => string.IsNullOrWhiteSpace(l.Text)))
                {
                    inFrontMatter = true;
                    frontMatterLine = true;
                }
                else if (inFrontMatter)
                {
                    inFrontMatter = false;
                    frontMatterLine = true;
                }
            }
            else if (inFrontMatter)
            {
                frontMatterLine = true;
            }
            else if (trimmed.Length > 0)
            {
                seenContent = true;
            }

            string? marker = FenceMarker(trimmed);
            if (marker is not null && !frontMatterLine)
            {
                if (!inFence)
                {
                    (inFence, fence) = (true, marker);
                }
                else if (marker[0] == fence![0])
                {
                    (inFence, fence) = (false, null);
                }
            }

            lines.Add(new SourceLine(
                index,
                end,
                line,
                inFence || frontMatterLine ? 0 : HeadingLevel(trimmed),
                frontMatterLine));

            if (breakAt < 0)
            {
                break;
            }

            index = end + 1;
            if (text[end] == '\r' && index < text.Length && text[index] == '\n')
            {
                index++;
            }
        }

        return lines;
    }

    private static string? FenceMarker(string trimmed) =>
        trimmed.StartsWith("```", StringComparison.Ordinal) ? "```"
        : trimmed.StartsWith("~~~", StringComparison.Ordinal) ? "~~~"
        : null;

    private static int HeadingLevel(string trimmed)
    {
        int hashes = 0;
        while (hashes < trimmed.Length && trimmed[hashes] == '#')
        {
            hashes++;
        }
        return hashes is > 0 and <= 6 && (hashes == trimmed.Length || trimmed[hashes] == ' ') ? hashes : 0;
    }
}
