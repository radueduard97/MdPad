using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace MdPad;

/// <summary>How badly a front matter problem hurts.</summary>
public enum IssueSeverity
{
    /// <summary>The skill will not load, or will load under the wrong name.</summary>
    Error,

    /// <summary>It loads, but an agent is likely to mis-route or waste context on it.</summary>
    Warning,

    /// <summary>Worth tightening; nothing breaks.</summary>
    Suggestion,
}

/// <summary>One problem found in a document's front matter.</summary>
/// <param name="Line">Zero-based source line to jump to, or -1 when there is nothing to point at.</param>
/// <param name="Rule">Id from <see cref="SkillRules"/>, so a severity can be overridden or the rule turned off.</param>
public sealed record SkillIssue(IssueSeverity Severity, string Field, string Message, string? Hint, int Line, string Rule = "");

/// <summary>
/// The result of checking a document's front matter against the skill metadata contract.
/// <paramref name="Applies"/> is false for ordinary Markdown, where none of these rules mean anything.
/// </summary>
public sealed record SkillValidationResult(bool Applies, IReadOnlyList<SkillIssue> Issues)
{
    public static readonly SkillValidationResult NotASkill = new(false, Array.Empty<SkillIssue>());

    public int ErrorCount => Issues.Count(i => i.Severity == IssueSeverity.Error);

    public int WarningCount => Issues.Count(i => i.Severity == IssueSeverity.Warning);

    public int SuggestionCount => Issues.Count(i => i.Severity == IssueSeverity.Suggestion);
}

/// <summary>One rule the settings pane can demote, promote, or switch off.</summary>
/// <param name="Id">Stable key stored in settings.json.</param>
/// <param name="Label">How the rule reads in the settings list.</param>
/// <param name="Note">Why it exists, so demoting it is an informed choice.</param>
public sealed record SkillRule(string Id, string Label, string Note, IssueSeverity Default);

/// <summary>
/// The rules worth arguing about. Everything the loader itself rejects — a missing
/// name, an unparsable tool list — stays fixed at Error and is not listed here; these
/// are the house-style calls where a team can reasonably disagree with MdPad.
/// </summary>
public static class SkillRules
{
    public const string NameFolderMismatch = "name-folder-mismatch";
    public const string DescriptionThin = "description-thin";
    public const string DescriptionBloated = "description-bloated";
    public const string DescriptionTrigger = "description-trigger";
    public const string DescriptionThisSkill = "description-this-skill";
    public const string DescriptionFirstPerson = "description-first-person";
    public const string ToolsEmpty = "tools-empty";
    public const string ToolsUnknown = "tools-unknown";
    public const string ToolsEmptySpec = "tools-empty-spec";
    public const string ToolsNoSpec = "tools-no-spec";
    public const string ToolsDuplicate = "tools-duplicate";
    public const string KeyUnknown = "key-unknown";

    public static readonly IReadOnlyList<SkillRule> Configurable = new[]
    {
        new SkillRule(NameFolderMismatch, "name does not match its folder",
            "The loader keys a skill by its folder, so a mismatch invokes it under a name that appears nowhere in the file.", IssueSeverity.Error),
        new SkillRule(DescriptionThin, "description is too short",
            "Below the thin threshold there is not enough for a model to route on.", IssueSeverity.Warning),
        new SkillRule(DescriptionBloated, "description is too long",
            "Past the bloated threshold, every prompt pays for text most of them will not use.", IssueSeverity.Warning),
        new SkillRule(DescriptionTrigger, "description has no trigger phrase",
            "House style: \"Use when …\" tells the model which requests should reach the skill.", IssueSeverity.Warning),
        new SkillRule(DescriptionThisSkill, "description starts with \"This skill\"",
            "The words are read as part of a listing, so leading with the capability reads better.", IssueSeverity.Suggestion),
        new SkillRule(DescriptionFirstPerson, "description is in the first person",
            "\"Reviews …\" rather than \"I review …\".", IssueSeverity.Suggestion),
        new SkillRule(ToolsEmpty, "allowed-tools is present but empty",
            "An empty list is almost always a half-finished edit.", IssueSeverity.Warning),
        new SkillRule(ToolsUnknown, "unknown tool name",
            "Checked against the built-in tool list; MCP tools (mcp__…) are always taken on trust.", IssueSeverity.Warning),
        new SkillRule(ToolsEmptySpec, "empty permission spec",
            "Tool() allows nothing extra over Tool on its own.", IssueSeverity.Warning),
        new SkillRule(ToolsNoSpec, "permission spec on a tool that ignores one",
            "Only Bash, Edit, Glob, Grep, Read, WebFetch and Write read a (…) spec.", IssueSeverity.Warning),
        new SkillRule(ToolsDuplicate, "tool listed twice",
            "Harmless, but usually a merge artefact.", IssueSeverity.Suggestion),
        new SkillRule(KeyUnknown, "unrecognised front matter key",
            "Ignored on load, but it still costs characters to store.", IssueSeverity.Suggestion),
    };

    /// <summary>The severity a rule carries after any override in settings; null when it is off.</summary>
    public static IssueSeverity? Resolve(string rule, IssueSeverity reported)
    {
        if (rule.Length == 0
            || !Settings.Current.Skills.RuleSeverities.TryGetValue(rule, out RuleSeverity over)
            || over == RuleSeverity.Default)
        {
            return reported;
        }

        return over switch
        {
            RuleSeverity.Error => IssueSeverity.Error,
            RuleSeverity.Warning => IssueSeverity.Warning,
            RuleSeverity.Suggestion => IssueSeverity.Suggestion,
            _ => null,
        };
    }
}

/// <summary>
/// Checks the metadata contract a <c>SKILL.md</c> has to satisfy: a kebab-case
/// <c>name</c> that matches its folder, a <c>description</c> that both says what the
/// skill does and when to reach for it, and a syntactically valid <c>allowed-tools</c>.
/// These are the parts an agent reads before it ever opens the file, so they are the
/// parts worth getting wrong loudly.
/// </summary>
public static class SkillValidator
{
    /// <summary>The file name an agent skill has to use.</summary>
    public const string SkillFileName = "SKILL.md";

    /// <summary>Ceiling on <c>name</c>; longer names are rejected when the skill is loaded.</summary>
    private const int MaxNameLength = 64;

    /// <summary>Ceiling on <c>description</c>; longer descriptions are rejected when the skill is loaded.</summary>
    private const int MaxDescriptionLength = 1024;

    /// <summary>Below this, a description is too thin to route on.</summary>
    private static int ThinDescriptionLength => Settings.Current.Skills.ThinDescriptionLength;

    /// <summary>Above this, the description is spending context that every prompt pays for.</summary>
    private static int BloatedDescriptionLength => Settings.Current.Skills.BloatedDescriptionLength;

    private static readonly Regex KebabCase = new(@"^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>A tool entry: a name, optionally followed by a parenthesised permission spec.</summary>
    private static readonly Regex ToolEntry = new(@"^(?<name>[A-Za-z_][A-Za-z0-9_]*)(?<spec>\((?<inner>.*)\))?$", RegexOptions.Compiled);

    /// <summary>Front matter keys an agent actually reads; anything else is dead weight.</summary>
    private static readonly HashSet<string> KnownKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "name",
        "description",
        "allowed-tools",
        "license",
        "metadata",
        "model",
        "argument-hint",
        "disable-model-invocation",
        "user-invocable",
        "version",
    };

    /// <summary>Built-in tools, for spotting a typo in <c>allowed-tools</c>.</summary>
    private static readonly HashSet<string> KnownTools = new(StringComparer.Ordinal)
    {
        "AskUserQuestion", "Bash", "BashOutput", "Edit", "ExitPlanMode", "Glob", "Grep",
        "KillShell", "ListMcpResources", "MultiEdit", "NotebookEdit", "Read",
        "ReadMcpResource", "Skill", "SlashCommand", "Task", "TodoWrite", "WebFetch",
        "WebSearch", "Write",
    };

    /// <summary>Tools whose entries may carry a <c>(…)</c> permission spec.</summary>
    private static readonly HashSet<string> SpecTools = new(StringComparer.Ordinal)
    {
        "Bash", "Edit", "Glob", "Grep", "Read", "WebFetch", "Write",
    };

    /// <summary>
    /// Phrases that tell an agent <em>when</em> to reach for a skill. A description that
    /// only says what the skill is gives the model nothing to match a request against.
    /// </summary>
    private static readonly string[] TriggerPhrases =
    {
        "use when", "use this when", "used when", "when the user", "when you",
        "when asked", "when working", "when building", "when creating", "when the",
        "invoke when", "trigger", "for when", "call this when", "reach for this when",
        "apply when", "run when", "whenever",
    };

    public static SkillValidationResult Validate(string? markdown, string? documentPath)
    {
        List<FrontMatterField> fields = FrontMatter.Parse(markdown);
        bool isSkillFile = documentPath is not null
            && string.Equals(Path.GetFileName(documentPath), SkillFileName, StringComparison.OrdinalIgnoreCase);

        if (fields.Count == 0)
        {
            // A file called SKILL.md with no front matter is a skill that cannot load;
            // anything else is just Markdown, and none of these rules apply to it.
            return isSkillFile
                ? new SkillValidationResult(true, new[]
                {
                    new SkillIssue(
                        IssueSeverity.Error,
                        "front matter",
                        "No YAML front matter",
                        "A skill starts with a --- block carrying name and description.",
                        0,
                        "front-matter-missing"),
                })
                : SkillValidationResult.NotASkill;
        }

        // Settings decide how wide the net is cast: strictly SKILL.md, or anything whose
        // front matter carries the pair a skill is identified by.
        bool looksLikeSkill = isSkillFile
            || (Settings.Current.Skills.Detection == SkillDetection.SkillFileOrFrontMatter
                && FrontMatter.Find(fields, "name") is not null
                && FrontMatter.Find(fields, "description") is not null);

        if (!looksLikeSkill)
        {
            return SkillValidationResult.NotASkill;
        }

        var issues = new List<SkillIssue>();
        CheckDuplicates(fields, issues);
        CheckName(fields, documentPath, isSkillFile, issues);
        CheckDescription(fields, issues);
        CheckAllowedTools(fields, issues);
        CheckUnknownKeys(fields, issues);

        return new SkillValidationResult(true, Rank(issues));
    }

    /// <summary>
    /// Apply the severity overrides from settings, drop anything switched off, and put
    /// what is left in the order the sidebar reads it: worst first, then by line.
    /// </summary>
    private static List<SkillIssue> Rank(List<SkillIssue> issues)
    {
        var kept = new List<SkillIssue>(issues.Count);
        foreach (SkillIssue issue in issues)
        {
            if (SkillRules.Resolve(issue.Rule, issue.Severity) is { } severity)
            {
                kept.Add(severity == issue.Severity ? issue : issue with { Severity = severity });
            }
        }

        return kept
            .OrderBy(i => i.Severity)
            .ThenBy(i => i.Line)
            .ToList();
    }

    // ---- name -----------------------------------------------------------------

    private static void CheckName(
        List<FrontMatterField> fields,
        string? documentPath,
        bool isSkillFile,
        List<SkillIssue> issues)
    {
        FrontMatterField? field = FrontMatter.Field(fields, "name");
        if (field is null)
        {
            FrontMatterField? title = FrontMatter.Field(fields, "title");
            issues.Add(title is not null
                ? new SkillIssue(
                    IssueSeverity.Error,
                    "name",
                    "Uses title instead of name",
                    "The skill loader reads name; title is ignored.",
                    title.Line,
                    "name-uses-title")
                : new SkillIssue(IssueSeverity.Error, "name", "name is missing", "Every skill needs a name.", 0, "name-missing"));
            return;
        }

        string name = field.Value.Trim();
        if (name.Length == 0)
        {
            issues.Add(new SkillIssue(IssueSeverity.Error, "name", "name is empty", null, field.Line, "name-empty"));
            return;
        }

        if (!KebabCase.IsMatch(name))
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Error,
                "name",
                $"\"{name}\" is not kebab-case",
                $"Lowercase letters, digits and single hyphens only — try \"{ToKebabCase(name)}\".",
                field.Line,
                "name-kebab-case"));
        }

        if (name.Length > MaxNameLength)
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Error,
                "name",
                $"name is {name.Length} characters, over the {MaxNameLength} limit",
                null,
                field.Line,
                "name-too-long"));
        }

        // The loader keys a skill by its folder, so a name that disagrees with the
        // folder is a skill invoked under a name that appears nowhere in the file.
        if (isSkillFile && documentPath is not null)
        {
            string? folder = Path.GetFileName(Path.GetDirectoryName(documentPath)?.TrimEnd(Path.DirectorySeparatorChar));
            if (!string.IsNullOrEmpty(folder) && !string.Equals(folder, name, StringComparison.Ordinal))
            {
                issues.Add(new SkillIssue(
                    IssueSeverity.Error,
                    "name",
                    $"name does not match the folder \"{folder}\"",
                    KebabCase.IsMatch(name)
                        ? $"Rename the folder to \"{name}\", or set name: {folder}."
                        : $"The skill is keyed by its folder — set name: {folder}.",
                    field.Line,
                    SkillRules.NameFolderMismatch));
            }
        }
    }

    /// <summary>Best-effort kebab-case rewrite, for the "try this instead" hint.</summary>
    public static string ToKebabCase(string value)
    {
        string spaced = Regex.Replace(value.Trim(), @"(?<=[a-z0-9])(?=[A-Z])", "-");
        string kebab = Regex.Replace(spaced.ToLowerInvariant(), @"[^a-z0-9]+", "-");
        return kebab.Trim('-');
    }

    // ---- description ----------------------------------------------------------

    private static void CheckDescription(List<FrontMatterField> fields, List<SkillIssue> issues)
    {
        FrontMatterField? field = FrontMatter.Field(fields, "description");
        if (field is null)
        {
            FrontMatterField? summary = FrontMatter.Field(fields, "summary");
            issues.Add(summary is not null
                ? new SkillIssue(
                    IssueSeverity.Error,
                    "description",
                    "Uses summary instead of description",
                    "The skill listing reads description; summary is ignored.",
                    summary.Line,
                    "description-uses-summary")
                : new SkillIssue(
                    IssueSeverity.Error,
                    "description",
                    "description is missing",
                    "This is the only text an agent sees before deciding to invoke the skill.",
                    0,
                    "description-missing"));
            return;
        }

        string description = field.Value.Trim();
        if (description.Length == 0)
        {
            issues.Add(new SkillIssue(IssueSeverity.Error, "description", "description is empty", null, field.Line, "description-empty"));
            return;
        }

        if (description.Length > MaxDescriptionLength)
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Error,
                "description",
                $"description is {description.Length} characters, over the {MaxDescriptionLength} limit",
                "Move the detail into the body — it loads on invoke instead of always.",
                field.Line,
                "description-too-long"));
        }
        else if (description.Length > BloatedDescriptionLength)
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Warning,
                "description",
                $"description is {description.Length} characters (~{SkillAnalyzer.EstimateTokens(description)} tokens in every prompt)",
                $"Under ~{BloatedDescriptionLength} characters keeps the always-loaded cost small.",
                field.Line,
                SkillRules.DescriptionBloated));
        }
        else if (description.Length < ThinDescriptionLength)
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Warning,
                "description",
                $"description is only {description.Length} characters",
                "Say what the skill does and when to use it — the model routes on this alone.",
                field.Line,
                SkillRules.DescriptionThin));
        }

        if (!TriggerPhrases.Any(p => description.Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Warning,
                "description",
                "No trigger phrase",
                "Add \"Use when …\" so the model knows what request should reach this skill.",
                field.Line,
                SkillRules.DescriptionTrigger));
        }

        if (description.StartsWith("This skill", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Suggestion,
                "description",
                "Starts with \"This skill\"",
                "Lead with the capability instead — the words are read as part of a listing.",
                field.Line,
                SkillRules.DescriptionThisSkill));
        }

        if (description.Contains(" I ", StringComparison.Ordinal) || description.StartsWith("I ", StringComparison.Ordinal))
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Suggestion,
                "description",
                "Written in the first person",
                "Descriptions read better in the third person: \"Reviews …\", not \"I review …\".",
                field.Line,
                SkillRules.DescriptionFirstPerson));
        }
    }

    // ---- allowed-tools --------------------------------------------------------

    private static void CheckAllowedTools(List<FrontMatterField> fields, List<SkillIssue> issues)
    {
        FrontMatterField? underscore = FrontMatter.Field(fields, "allowed_tools");
        if (underscore is not null && FrontMatter.Field(fields, "allowed-tools") is null)
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Error,
                "allowed-tools",
                "Key is allowed_tools, not allowed-tools",
                "The hyphenated spelling is the one that is read.",
                underscore.Line,
                "tools-underscore"));
        }

        // The misspelled key still gets its list checked, so both problems surface at once.
        FrontMatterField? field = FrontMatter.Field(fields, "allowed-tools") ?? underscore;
        if (field is null)
        {
            return;
        }

        string raw = field.Value.Trim();
        if (raw.Length == 0)
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Warning,
                "allowed-tools",
                "allowed-tools is empty",
                "Drop the key to inherit every tool, or list the ones the skill needs.",
                field.Line,
                SkillRules.ToolsEmpty));
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in SplitToolList(raw))
        {
            string tool = entry.Trim();
            if (tool.Length == 0)
            {
                issues.Add(new SkillIssue(
                    IssueSeverity.Error,
                    "allowed-tools",
                    "Empty entry in the tool list",
                    "A stray or trailing comma.",
                    field.Line,
                    "tools-entry-empty"));
                continue;
            }

            if (tool == "*")
            {
                continue; // Everything; nothing to check.
            }

            if (tool.Count(c => c == '(') != tool.Count(c => c == ')'))
            {
                // An unclosed "(" swallows every entry after it, so say so rather than
                // quoting the whole remainder of the list back at the author.
                int open = tool.IndexOf('(');
                issues.Add(new SkillIssue(
                    IssueSeverity.Error,
                    "allowed-tools",
                    $"Unclosed parenthesis after \"{Elide(open >= 0 ? tool[..(open + 1)] : tool)}\"",
                    "Permission specs look like Bash(git status:*); the rest of the list is being read as part of it.",
                    field.Line,
                    "tools-unclosed"));
                continue;
            }

            Match match = ToolEntry.Match(tool);
            if (!match.Success)
            {
                issues.Add(new SkillIssue(
                    IssueSeverity.Error,
                    "allowed-tools",
                    $"\"{Elide(tool)}\" is not a valid tool entry",
                    "Expected Tool or Tool(spec), comma-separated.",
                    field.Line,
                    "tools-invalid"));
                continue;
            }

            string name = match.Groups["name"].Value;
            bool hasSpec = match.Groups["spec"].Success;
            string inner = match.Groups["inner"].Value.Trim();

            if (!seen.Add(tool))
            {
                issues.Add(new SkillIssue(
                    IssueSeverity.Suggestion,
                    "allowed-tools",
                    $"\"{Elide(tool)}\" is listed twice",
                    null,
                    field.Line,
                    SkillRules.ToolsDuplicate));
            }

            if (name.StartsWith("mcp__", StringComparison.Ordinal))
            {
                continue; // MCP tool names are server-defined; there is nothing to check them against.
            }

            if (!KnownTools.Contains(name))
            {
                string? closest = KnownTools.FirstOrDefault(t => string.Equals(t, name, StringComparison.OrdinalIgnoreCase));
                issues.Add(new SkillIssue(
                    IssueSeverity.Warning,
                    "allowed-tools",
                    $"\"{name}\" is not a known tool",
                    closest is not null
                        ? $"Tool names are case-sensitive — did you mean {closest}?"
                        : "Built-in tools are PascalCase (Read, Bash, Grep …); MCP tools start with mcp__.",
                    field.Line,
                    SkillRules.ToolsUnknown));
                continue;
            }

            if (hasSpec && inner.Length == 0)
            {
                issues.Add(new SkillIssue(
                    IssueSeverity.Warning,
                    "allowed-tools",
                    $"\"{Elide(tool)}\" has an empty spec",
                    $"Write {name} on its own to allow it unrestricted.",
                    field.Line,
                    SkillRules.ToolsEmptySpec));
            }
            else if (hasSpec && !SpecTools.Contains(name))
            {
                issues.Add(new SkillIssue(
                    IssueSeverity.Warning,
                    "allowed-tools",
                    $"{name} does not take a permission spec",
                    $"The \"({inner})\" is ignored; list it as {name}.",
                    field.Line,
                    SkillRules.ToolsNoSpec));
            }
        }
    }

    /// <summary>Keep quoted fragments short enough to read in a sidebar row.</summary>
    private static string Elide(string value, int max = 36) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    /// <summary>
    /// Split a tool list written either inline (<c>Read, Bash(git:*)</c>) or as a YAML
    /// block sequence, which the front matter reader folds into "- Read - Bash(git:*)".
    /// Commas inside a permission spec are left alone.
    /// </summary>
    private static IEnumerable<string> SplitToolList(string raw)
    {
        string value = raw;
        if (value.StartsWith("[", StringComparison.Ordinal) && value.EndsWith("]", StringComparison.Ordinal))
        {
            value = value[1..^1];
        }

        bool isBlockList = value.StartsWith("- ", StringComparison.Ordinal);
        var current = new System.Text.StringBuilder();
        int depth = 0;

        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth = Math.Max(0, depth - 1);
            }

            bool isSeparator = depth == 0 && (c == ',' || (isBlockList && c == '-' && IsBlockBullet(value, i)));
            if (isSeparator)
            {
                if (current.Length > 0 || c == ',')
                {
                    yield return Unquote(current.ToString().Trim());
                }
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            yield return Unquote(current.ToString().Trim());
        }

        // A "- " that opens an item, rather than a hyphen inside a name or spec.
        static bool IsBlockBullet(string value, int index) =>
            (index == 0 || char.IsWhiteSpace(value[index - 1]))
            && index + 1 < value.Length
            && value[index + 1] == ' ';

        static string Unquote(string value) =>
            value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
                ? value[1..^1].Trim()
                : value;
    }

    // ---- keys -----------------------------------------------------------------

    private static void CheckDuplicates(List<FrontMatterField> fields, List<SkillIssue> issues)
    {
        foreach (var group in fields.GroupBy(f => f.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
        {
            issues.Add(new SkillIssue(
                IssueSeverity.Error,
                group.Key,
                $"\"{group.Key}\" appears {group.Count()} times",
                "YAML keeps the last one; the others are silently dropped.",
                group.Last().Line,
                "key-duplicate"));
        }
    }

    private static void CheckUnknownKeys(List<FrontMatterField> fields, List<SkillIssue> issues)
    {
        foreach (FrontMatterField field in fields)
        {
            if (KnownKeys.Contains(field.Key)
                || field.Key.Equals("allowed_tools", StringComparison.OrdinalIgnoreCase)
                || field.Key.Equals("title", StringComparison.OrdinalIgnoreCase)
                || field.Key.Equals("summary", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            issues.Add(new SkillIssue(
                IssueSeverity.Suggestion,
                field.Key,
                $"\"{field.Key}\" is not a recognised skill key",
                "It is ignored on load, but still costs characters to store.",
                field.Line,
                SkillRules.KeyUnknown));
        }
    }
}
