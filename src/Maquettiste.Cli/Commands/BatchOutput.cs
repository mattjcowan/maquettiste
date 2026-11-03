using System.Globalization;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// One projection of a batch's answers (next-phase-1-design.md section 5.1), shared by the MCP tool <c>apply_batch</c> and
/// <c>maquettiste model apply</c>: the batch document a request or file holds, the dry-run answer without the files' full texts, and
/// the text lines the CLI prints. An apply answers the <see cref="BatchResult"/> itself, which carries <c>ids</c> and <c>facts</c>.
/// </summary>
internal static class BatchOutput
{
    /// <summary>The batch document a request or file holds: a <c>{ "operations": [...] }</c> object as it is, a bare array wrapped.</summary>
    /// <param name="json">The JSON text.</param>
    /// <returns>The UTF-8 bytes of the batch document.</returns>
    public static byte[] Document(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        return Encoding.UTF8.GetBytes(json.TrimStart().StartsWith('{') ? json : "{\"operations\":" + json + "}");
    }

    /// <summary>
    /// The answer of a dry run: what an apply would answer (<c>outcome</c>, <c>items</c>), each file without its full texts but with
    /// its unified diff, the diagnostics the change would add and remove over its scope, the ids it would resolve and mint, and the
    /// migration facts it would record (<see langword="null"/> when the change reaches every database).
    /// </summary>
    /// <param name="preview">The dry run.</param>
    /// <returns>The answer.</returns>
    public static BatchPreviewAnswer Preview(BatchPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return new BatchPreviewAnswer(preview.Outcome, preview.Items,
            [.. preview.Files.Select(f => new BatchPreviewFileAnswer(f.Path, f.Action, f.Id, f.Kind, f.Name, f.BeforeHash, f.Diff))],
            preview.DiagnosticsAdded, preview.DiagnosticsRemoved, preview.Ids, preview.Facts);
    }

    /// <summary>
    /// The text of a saved apply: a usage line per reference-type refactor (<see cref="UsageLines"/>), a line per written or deleted path
    /// (<c>written &lt;path&gt;</c>, <c>deleted &lt;path&gt;</c>), then <c>name  id</c> per name the batch resolved and placeholder it
    /// declared.
    /// </summary>
    /// <param name="result">The saved result.</param>
    /// <param name="before">The model before the batch, where a deleted element's path and a refactored type's name are read.</param>
    /// <param name="batch">The batch as parsed, whose refactors get a usage line.</param>
    /// <returns>The text, each line ending with a line feed.</returns>
    public static string AppliedText(BatchResult result, Engine.Model.ModelSnapshot before, ModelBatch batch)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(batch);
        var text = new StringBuilder();
        foreach (var line in UsageLines(batch, result.Items, result.Ids, before))
            text.Append(line).Append('\n');
        foreach (var change in result.Changes?.Changed ?? [])
            text.Append("written ").Append(change.Path).Append('\n');
        foreach (var id in result.Changes?.Deleted ?? [])
            text.Append("deleted ").Append(before.GetDocument(id)?.Path ?? id).Append('\n');
        Names(text, result.Ids);
        return text.ToString();
    }

    /// <summary>
    /// The text of a dry run that would save: a usage line per reference-type refactor (<see cref="UsageLines"/>), each file's unified
    /// diff, then <c>name  id</c> per name, then a <c>+ </c> line per diagnostic the change would add and a <c>- </c> line per diagnostic
    /// it would remove.
    /// </summary>
    /// <param name="preview">The dry run.</param>
    /// <param name="before">The model before the batch, where a refactored type's name is read.</param>
    /// <param name="batch">The batch as parsed, whose refactors get a usage line.</param>
    /// <returns>The text, each line ending with a line feed.</returns>
    public static string PreviewText(BatchPreview preview, Engine.Model.ModelSnapshot before, ModelBatch batch)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(batch);
        var text = new StringBuilder();
        foreach (var line in UsageLines(batch, preview.Items, preview.Ids, before))
            text.Append(line).Append('\n');
        foreach (var file in preview.Files)
        {
            if (file.Diff is { Length: > 0 } diff)
                text.Append(diff).Append(diff.EndsWith('\n') ? "" : "\n");
            else
                text.Append(file.Action).Append(' ').Append(file.Path).Append('\n');
        }

        Names(text, preview.Ids);
        foreach (var d in preview.DiagnosticsAdded)
            text.Append("+ ").Append(DiagnosticOutput.Line(d)).Append('\n');
        foreach (var d in preview.DiagnosticsRemoved)
            text.Append("- ").Append(DiagnosticOutput.Line(d)).Append('\n');
        return text.ToString();
    }

    /// <summary>
    /// One line per reference-type refactor of a batch (next-phase-2-design.md section 3.5), from the usage on the item of the type it
    /// names: <c>&lt;op&gt; &lt;type&gt;: &lt;n&gt; uses, &lt;m&gt; rows moved, &lt;k&gt; rows removed, &lt;t&gt; translations removed</c>.
    /// </summary>
    /// <param name="batch">The batch as parsed (its ids may be names, which <paramref name="ids"/> resolves).</param>
    /// <param name="items">The items of the answer.</param>
    /// <param name="ids">The names the batch resolved.</param>
    /// <param name="before">The model before the batch, where the type's name is read.</param>
    /// <returns>The lines, in operation order.</returns>
    public static IEnumerable<string> UsageLines(ModelBatch batch, IReadOnlyList<SaveResult> items, BatchIds ids, Engine.Model.ModelSnapshot before)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(before);
        foreach (var o in batch.Operations)
        {
            if (o.Op is not (BatchOp.RenameCode or BatchOp.RetireCode or BatchOp.MergeReferenceTypes or BatchOp.RenameReferenceType) || o.Id is null)
                continue;
            var id = ids.Names.GetValueOrDefault(o.Id) ?? o.Id;
            if (items.FirstOrDefault(i => i.Id == id && i.Usage is not null)?.Usage is not { } usage)
                continue;
            var name = before.Get<Engine.Model.ReferenceType>(id)?.Name ?? id;
            yield return string.Create(CultureInfo.InvariantCulture,
                $"{JsonSerializer.Serialize(o.Op, Engine.Model.EngineJson.Options).Trim('"')} {name}: {Count(usage.UseCount, "use")}, {Count(usage.RowsMoved, "row")} moved, {Count(usage.RowsRemoved, "row")} removed, {Count(usage.TranslationsRemoved, "translation")} removed");
        }

        static string Count(int n, string noun) => string.Create(CultureInfo.InvariantCulture, $"{n} {noun}{(n == 1 ? "" : "s")}");
    }

    /// <summary>
    /// The lines that say why a batch was not saved: each failed operation's diagnostics (<see cref="DiagnosticOutput.Line"/>), or its
    /// outcome when it has none, with the referrers of a refused delete.
    /// </summary>
    /// <param name="items">The result per operation.</param>
    /// <returns>The lines.</returns>
    public static IEnumerable<string> FailureLines(IReadOnlyList<SaveResult> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.Outcome == SaveOutcome.Saved && item.Diagnostics.Count == 0)
                continue;
            var operation = string.Create(CultureInfo.InvariantCulture, $"operation {i}{(item.Id is null ? "" : " (" + item.Id + ")")}");
            var errors = item.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
            foreach (var d in errors)
                yield return DiagnosticOutput.Line(d);
            if (errors.Count > 0)
                continue;
            yield return item.Outcome switch
            {
                SaveOutcome.Conflict => $"{operation}: the file changed since it was read (its hash is now {item.Hash}); read it again.",
                SaveOutcome.Referenced => $"{operation}: other elements reference it ({string.Join(", ", item.Referrers.Select(r => r.FromElementId).Distinct(StringComparer.Ordinal).Take(10))}); pass a resolution.",
                SaveOutcome.NotFound => $"{operation}: no such element.",
                SaveOutcome.Saved => $"{operation}: no error of its own.",
                _ => $"{operation}: invalid.",
            };
        }
    }

    /// <summary>The CLI exit code of a batch outcome: saved 0, conflict 3, anything else 1.</summary>
    /// <param name="outcome">The outcome.</param>
    /// <returns>The exit code.</returns>
    public static int ExitCode(SaveOutcome outcome) => outcome switch
    {
        SaveOutcome.Saved => Program.ExitCodes.Success,
        SaveOutcome.Conflict => Program.ExitCodes.Conflicts,
        _ => Program.ExitCodes.Invalid,
    };

    private static void Names(StringBuilder text, BatchIds ids)
    {
        foreach (var (name, id) in ids.Names)
            text.Append(name).Append("  ").Append(id).Append('\n');
    }
}

/// <summary>The answer of a batch's dry run (<see cref="BatchOutput.Preview"/>).</summary>
/// <param name="Outcome">Saved when every operation would succeed; otherwise the first failure's outcome.</param>
/// <param name="Items">The result per operation, as an apply would report it.</param>
/// <param name="Files">When saved, every file the batch would write or delete, ordered by path; empty otherwise.</param>
/// <param name="DiagnosticsAdded">When saved, the diagnostics the change would add over its scope.</param>
/// <param name="DiagnosticsRemoved">When saved, the diagnostics the change would remove over its scope.</param>
/// <param name="Ids">When saved, the names resolved, placeholders declared and ids minted.</param>
/// <param name="Facts">When saved, the migration facts the batch would record; <see langword="null"/> when the change reaches every
/// database or when not saved.</param>
internal sealed record BatchPreviewAnswer(SaveOutcome Outcome, IReadOnlyList<SaveResult> Items, IReadOnlyList<BatchPreviewFileAnswer> Files,
    IReadOnlyList<Diagnostic> DiagnosticsAdded, IReadOnlyList<Diagnostic> DiagnosticsRemoved, BatchIds Ids, IReadOnlyList<RecordedFact>? Facts);

/// <summary>A file of a dry-run answer: <see cref="BatchPreviewFile"/> without the texts before and after.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Action"><c>created</c>, <c>changed</c> or <c>deleted</c>.</param>
/// <param name="Id">The element id the file holds, when it is an element document.</param>
/// <param name="Kind">The element kind, when it is an element document.</param>
/// <param name="Name">The element name, when it has one.</param>
/// <param name="BeforeHash">The hash of the file on disk, or <see langword="null"/> for a created file.</param>
/// <param name="Diff">The change as a unified diff with three lines of context.</param>
internal sealed record BatchPreviewFileAnswer(string Path, string Action, string? Id, string? Kind, string? Name, string? BeforeHash, string? Diff);
