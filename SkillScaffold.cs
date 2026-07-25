using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace MdPad;

/// <summary>
/// Creates the smallest thing that is already a working skill: a kebab-case folder, a
/// <c>SKILL.md</c> whose front matter passes <see cref="SkillValidator"/>, and a
/// <c>references/</c> folder with one file the body actually links to. Starting from a
/// valid skeleton means the sidebar shows a clean bill of health from the first render,
/// so the first red mark a author sees is one they introduced.
/// </summary>
public static class SkillScaffold
{
    /// <summary>The conventional home for the files a skill loads on demand.</summary>
    public const string ReferencesFolder = "references";

    /// <summary>The starter file inside <see cref="ReferencesFolder"/>, so nothing is orphaned or broken.</summary>
    public const string StarterReference = "reference.md";

    /// <summary>Where new skills go unless the author picks somewhere else.</summary>
    public static string DefaultParentFolder()
    {
        string configured = Settings.Current.Skills.SkillsFolder;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string skills = Path.Combine(home, ".claude", "skills");
        return Directory.Exists(skills)
            ? skills
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>A description that already clears the contract, for the author to edit down.</summary>
    public static string DefaultDescription(string name) =>
        $"{Title(name)} — say what this does in a sentence. Use when the user asks for it by name or describes the problem it solves.";

    /// <summary>
    /// Why the given name cannot be used here, or null if it can. Checked live in the
    /// dialog so "Create" is only ever offered for something that will work.
    /// </summary>
    public static string? Problem(string parentFolder, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Give the skill a name.";
        }

        string kebab = SkillValidator.ToKebabCase(name);
        if (name != kebab)
        {
            return kebab.Length == 0
                ? "Names are kebab-case: lowercase letters, digits and single hyphens."
                : $"Names are kebab-case — try \"{kebab}\".";
        }

        if (string.IsNullOrWhiteSpace(parentFolder) || !Directory.Exists(parentFolder))
        {
            return "Pick a folder to create the skill in.";
        }

        string target = Path.Combine(parentFolder, name);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            return $"\"{name}\" already exists here and is not empty.";
        }

        return null;
    }

    /// <summary>Create the folder and its files; returns the path of the new <c>SKILL.md</c>.</summary>
    public static async Task<string> CreateAsync(string parentFolder, string name, string description)
    {
        string root = Path.Combine(parentFolder, name);
        string references = Path.Combine(root, ReferencesFolder);
        Directory.CreateDirectory(references);

        string skillPath = Path.Combine(root, SkillValidator.SkillFileName);
        await File.WriteAllTextAsync(skillPath, BuildSkillMarkdown(name, description));
        await File.WriteAllTextAsync(Path.Combine(references, StarterReference), BuildReferenceMarkdown(name));
        return skillPath;
    }

    /// <summary>
    /// The starter <c>SKILL.md</c>. The body is deliberately short: everything here is
    /// paid for on invoke, and a template that arrives half-full of prose teaches the
    /// wrong habit.
    /// </summary>
    public static string BuildSkillMarkdown(string name, string description) =>
        "---\n" +
        $"name: {name}\n" +
        $"description: {Escape(description)}\n" +
        "---\n" +
        "\n" +
        $"# {Title(name)}\n" +
        "\n" +
        "One paragraph on what this skill is for. The front matter above is what the model\n" +
        "reads before invoking; everything from here down arrives only once it does.\n" +
        "\n" +
        "## When to use this\n" +
        "\n" +
        "- The request looks like …\n" +
        "- Do not use this for …\n" +
        "\n" +
        "## How to do it\n" +
        "\n" +
        "1. First step.\n" +
        "2. Second step.\n" +
        "3. Check the result against [the reference](" + ReferencesFolder + "/" + StarterReference + ").\n" +
        "\n" +
        "## References\n" +
        "\n" +
        $"- [{ReferencesFolder}/{StarterReference}]({ReferencesFolder}/{StarterReference}) — details kept out of the body so they load only when needed.\n";

    private static string BuildReferenceMarkdown(string name) =>
        $"# {Title(name)} reference\n" +
        "\n" +
        "Detail that the skill body links to rather than carries: tables, option lists,\n" +
        "worked examples. This file costs nothing until an agent decides to read it.\n";

    /// <summary>"find-skills" → "Find Skills", for the document's own H1.</summary>
    private static string Title(string name)
    {
        string[] words = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0
            ? "New skill"
            : string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    /// <summary>
    /// Front matter is written as a plain scalar, which cannot start with an indicator
    /// character or contain ": ". Quote when it would otherwise parse as something else.
    /// </summary>
    private static string Escape(string description)
    {
        string value = description.Trim().Replace('\n', ' ').Replace('\r', ' ');
        bool needsQuotes = value.Length > 0
            && ("-?:,[]{}#&*!|>'\"%@`".Contains(value[0]) || value.Contains(": ", StringComparison.Ordinal));
        return needsQuotes ? "\"" + value.Replace("\"", "\\\"") + "\"" : value;
    }
}
