using System.Globalization;
using System.Text;

namespace Maquettiste.Engine;

/// <summary>What the editor's panel says the user is looking at, sent with a message (the context chips the user kept).</summary>
/// <param name="Workspace">The workspace (entities, database, generate, ...).</param>
/// <param name="ElementId">The open element's id.</param>
/// <param name="ElementName">The open element's name.</param>
/// <param name="ElementKind">The open element's kind.</param>
/// <param name="Selection">The selected element ids (at most 200 are read).</param>
/// <param name="Problems">A summary of the current problems.</param>
public sealed record AssistantContext(string? Workspace = null, string? ElementId = null, string? ElementName = null, string? ElementKind = null,
    IReadOnlyList<string>? Selection = null, AssistantProblems? Problems = null);

/// <summary>The problems summary the panel sends: counts and the first few problems.</summary>
/// <param name="Errors">The error count.</param>
/// <param name="Warnings">The warning count.</param>
/// <param name="Infos">The info count.</param>
/// <param name="Top">The first problems (at most 20 are read).</param>
public sealed record AssistantProblems(int Errors, int Warnings, int Infos, IReadOnlyList<AssistantProblem>? Top = null);

/// <summary>One problem of the summary.</summary>
/// <param name="Rule">The rule id.</param>
/// <param name="Message">The message.</param>
/// <param name="ElementId">The element it is about.</param>
public sealed record AssistantProblem(string Rule, string Message, string? ElementId = null);

/// <summary>The system prompt of the editor's assistant and the context block of a user message (erratum E44).</summary>
public static class AssistantPrompt
{
    /// <summary>The most characters of the project's instructions the prompt carries.</summary>
    public const int MaxInstructions = 8000;

    /// <summary>
    /// The system prompt: what Maquettiste and the assistant are, what it may and may not do, the modeling conventions (the embedded skill
    /// and the repository's <c>CONVENTIONS.md</c>) and the project's own instructions (<c>assistant.instructions</c>).
    /// </summary>
    /// <param name="projectName">The project's name.</param>
    /// <param name="conventions">The conventions text (<see cref="AgentConventions.WithProjectConventions"/>).</param>
    /// <param name="instructions">The project's instructions, or <see langword="null"/>.</param>
    /// <returns>The prompt, LF line endings.</returns>
    public static string System(string projectName, string conventions, string? instructions)
    {
        ArgumentNullException.ThrowIfNull(conventions);
        var text = new StringBuilder();
        text.Append("You are the assistant inside the Maquettiste editor, working on the project \"").Append(projectName).Append("\".\n\n");
        text.Append("""
            Maquettiste is a modeling workbench: the model (entities, relations, enums, types, reference data, databases, tables, queries,
            mappings, processes, diagrams) is one canonical JSON document per element under .maquettiste/, every reference between
            elements is an id, and template packs generate code from it. The user sees the model in the editor while talking to you.

            # What you can do

            - Read anything with your tools: the project, the model index, element documents, references, the resolved model, the
              database views, validation, JSON schemas, query and binding SQL, materialize previews, stored generation plans and unit
              previews. Read before you answer; never guess an id or a document's fields.
            - Change the model only by proposing: call propose_changes with batch operations and a one-line summary. Nothing is written
              until the user reviews the diff and applies it (one undo step) or discards it. Never say a change was made; say what you
              proposed. Read get_schema for a kind before building its document, and send whole documents for updates.
            - You cannot run or apply generation, and you cannot write template packs, extension files, settings or translations. When
              asked, say what the user can do in the editor instead.

            The conventions below were written for agents that use the maquettiste MCP server. Here the write tools they name
            (save_element, create_element, delete_element, apply_batch, apply_plan, write_pack_file and the like) do not exist: put
            model changes in one propose_changes call as batch operations instead.

            Answer in short Markdown: lead with the answer, name elements by name (with their kind), keep lists tight.

            # Modeling conventions


            """.Replace("\r\n", "\n", StringComparison.Ordinal));
        text.Append(conventions.Replace("\r\n", "\n", StringComparison.Ordinal).Trim('\n')).Append('\n');
        var rules = instructions?.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (!string.IsNullOrEmpty(rules))
        {
            if (rules.Length > MaxInstructions)
                rules = rules[..MaxInstructions];
            text.Append("\n---\n\n# This project's instructions for the assistant\n\n").Append(rules).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>The user's message as the model sees it: the text, then the editor context the user kept, in a tagged block.</summary>
    /// <param name="message">The user's text.</param>
    /// <param name="context">The context, or <see langword="null"/>.</param>
    /// <returns>The message text.</returns>
    public static string UserMessage(string message, AssistantContext? context)
    {
        ArgumentNullException.ThrowIfNull(message);
        var block = ContextBlock(context);
        return block.Length == 0 ? message : message + "\n\n" + block;
    }

    /// <summary>The context block (empty when there is no context).</summary>
    /// <param name="context">The context.</param>
    /// <returns>The block, or an empty string.</returns>
    public static string ContextBlock(AssistantContext? context)
    {
        if (context is null)
            return "";
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(context.Workspace))
            lines.Add("Workspace: " + Clip(context.Workspace, 40));
        if (!string.IsNullOrWhiteSpace(context.ElementId))
        {
            var what = string.IsNullOrWhiteSpace(context.ElementName) ? context.ElementId : $"{Clip(context.ElementName, 120)} ({Clip(context.ElementKind ?? "element", 40)}, {context.ElementId})";
            lines.Add("Open element: " + what);
        }

        if (context.Selection is { Count: > 0 } selection)
        {
            var shown = selection.Take(200).Select(s => Clip(s, 40)).ToList();
            lines.Add("Selected ids: " + string.Join(", ", shown) + (selection.Count > shown.Count ? $" (and {selection.Count - shown.Count} more)" : ""));
        }

        if (context.Problems is { } problems)
        {
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"Problems: {problems.Errors} errors, {problems.Warnings} warnings, {problems.Infos} infos"));
            foreach (var p in (problems.Top ?? []).Take(20))
                lines.Add($"- {Clip(p.Rule, 20)}{(p.ElementId is null ? "" : " on " + Clip(p.ElementId, 40))}: {Clip(p.Message, 300)}");
        }

        return lines.Count == 0 ? "" : "<editor-context>\n" + string.Join("\n", lines) + "\n</editor-context>";
    }

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length] + "…";
}
