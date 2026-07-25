using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MdPad;

/// <summary>When a block of text reaches the model.</summary>
public enum AgentTier
{
    /// <summary>In the prompt already, for every request, whether the skill is used or not.</summary>
    Always,

    /// <summary>Delivered when the skill is invoked.</summary>
    OnInvoke,

    /// <summary>Never delivered unless the agent decides to read it.</summary>
    OnDemand,
}

/// <summary>
/// One block of the transcript: the text a model receives at a given tier, with the
/// heading and cost to display alongside it.
/// </summary>
public sealed record AgentBlock(AgentTier Tier, string Title, string Note, string Text, int Tokens);

/// <summary>
/// Turns a document into what a model actually receives from it, split by tier.
/// The preview answers "how does this read"; this answers "what lands in the context
/// window, and when" — which for a skill is a different, and more useful, question.
/// </summary>
public static class AgentTranscript
{
    /// <summary>Placeholder for a field the loader needs and the file does not have.</summary>
    private const string Missing = "(missing)";

    public static IReadOnlyList<AgentBlock> Build(string? markdown, string? documentPath, SkillBudget budget)
    {
        string text = markdown ?? string.Empty;
        var blocks = new List<AgentBlock>();

        if (budget.HasFrontMatter)
        {
            List<FrontMatterField> fields = FrontMatter.Parse(text);
            string name = FrontMatter.Find(fields, "name") ?? FrontMatter.Find(fields, "title") ?? Missing;
            string description = FrontMatter.Find(fields, "description") ?? FrontMatter.Find(fields, "summary") ?? Missing;

            // How a skill appears in the listing every prompt carries: one line, name first.
            string listing = $"- {name}: {description}";
            blocks.Add(new AgentBlock(
                AgentTier.Always,
                "Always loaded",
                "One line in the skill listing, in every prompt of every session — this is all the model has to decide from.",
                listing,
                SkillAnalyzer.EstimateTokens(listing)));

            blocks.Add(new AgentBlock(
                AgentTier.OnInvoke,
                "On invoke",
                "The file as it is delivered once the model picks the skill, front matter included.",
                text,
                budget.InvokeTokens));
        }
        else
        {
            blocks.Add(new AgentBlock(
                AgentTier.OnInvoke,
                "When read",
                documentPath is null
                    ? "No front matter, so nothing here is preloaded: a model sees this file only after reading it."
                    : "No front matter, so nothing here is preloaded: a model sees this file only after reading it by path.",
                text,
                SkillAnalyzer.EstimateTokens(text)));
        }

        blocks.Add(new AgentBlock(
            AgentTier.OnDemand,
            "On demand",
            "Linked files. None of this is in context until the agent opens them — that is the point of moving detail out here.",
            BuildReferenceList(budget, budget.HasFrontMatter),
            budget.OnDemandTokens));

        return blocks;
    }

    private static string BuildReferenceList(SkillBudget budget, bool isSkill)
    {
        var linked = budget.References.Where(r => r.State == ReferenceState.Linked).ToList();
        var missing = budget.References.Where(r => r.State == ReferenceState.Missing).ToList();

        if (linked.Count == 0 && missing.Count == 0)
        {
            return isSkill
                ? "Nothing linked — the whole skill is in the block above."
                : "Nothing linked — this file stands alone.";
        }

        var builder = new StringBuilder();
        foreach (DocumentReference reference in linked)
        {
            builder.AppendLine($"{reference.Display}  ~{SkillAnalyzer.Format(reference.Tokens)} tokens if read");
        }

        foreach (DocumentReference reference in missing)
        {
            builder.AppendLine($"{reference.Display}  — no file at this path; the agent gets an error, not content");
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>The whole transcript as plain text, for the clipboard.</summary>
    public static string Compose(IReadOnlyList<AgentBlock> blocks)
    {
        var builder = new StringBuilder();
        foreach (AgentBlock block in blocks)
        {
            builder
                .Append("=== ").Append(block.Title)
                .Append("  (~").Append(SkillAnalyzer.Format(block.Tokens)).AppendLine(" tokens)")
                .AppendLine()
                .AppendLine(block.Text)
                .AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }
}
