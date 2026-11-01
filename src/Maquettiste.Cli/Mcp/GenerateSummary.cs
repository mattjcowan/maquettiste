using Maquettiste.Cli.Commands;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Cli.Mcp;

/// <summary>
/// The answer of the MCP tool <c>generate</c> (next-phase-1-design.md section 5.2): a run's outcome and counts, the files it changed
/// grouped by pack, and its diagnostics, small enough for an agent's context. Files left alone as identical (<c>unchanged</c>) or
/// not rendered (<c>not-rendered</c>) are left out, each pack lists its first <see cref="FileLimit"/> paths (ordinal) and counts the
/// rest in <see cref="GeneratePackSummary.More"/>, and at most <see cref="DiagnosticLimit"/> diagnostics come, errors first, then in
/// <see cref="Diagnostic.Order"/>. The run id and timings are left out, so equal runs answer equal text.
/// </summary>
/// <param name="Outcome">The run's outcome.</param>
/// <param name="UnitsRendered">Units rendered.</param>
/// <param name="UnitsSkipped">Units skipped (their inputs did not change).</param>
/// <param name="FilesWritten">Files written on disk.</param>
/// <param name="FilesDeleted">Files deleted on disk.</param>
/// <param name="Packs">The changed files by pack, in ordinal pack order; a pack that changed nothing is left out.</param>
/// <param name="Diagnostics">The first diagnostics, errors first.</param>
/// <param name="MoreDiagnostics">How many diagnostics <paramref name="Diagnostics"/> leaves out.</param>
internal sealed record GenerateSummary(RunOutcome Outcome, int UnitsRendered, int UnitsSkipped, int FilesWritten, int FilesDeleted,
    IReadOnlyList<GeneratePackSummary> Packs, IReadOnlyList<Diagnostic> Diagnostics, int MoreDiagnostics)
{
    /// <summary>The most paths one pack lists.</summary>
    public const int FileLimit = 50;

    /// <summary>The most diagnostics the answer carries.</summary>
    public const int DiagnosticLimit = 100;

    /// <summary>Projects a run's result.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The summary.</returns>
    public static GenerateSummary Of(GenerationResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var packs = result.Changes
            .Where(c => c.Kind is not (FileChangeKind.Unchanged or FileChangeKind.NotRendered))
            .GroupBy(c => c.Pack, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
                foreach (var change in g)
                    counts[GenerateCommand.KindName(change.Kind)] = counts.GetValueOrDefault(GenerateCommand.KindName(change.Kind)) + 1;
                var files = g.OrderBy(c => c.Path, StringComparer.Ordinal).Select(c => new GenerateFile(c.Path, c.Kind)).ToList();
                return new GeneratePackSummary(g.Key, counts, [.. files.Take(FileLimit)], Math.Max(0, files.Count - FileLimit));
            })
            .ToList();
        var diagnostics = result.Diagnostics
            .OrderBy(d => d.Severity == DiagnosticSeverity.Error ? 0 : 1)
            .ThenBy(d => d, Diagnostic.Order)
            .ToList();
        return new GenerateSummary(result.Outcome, result.UnitsRendered, result.UnitsSkipped, result.FilesWritten, result.FilesDeleted, packs,
            [.. diagnostics.Take(DiagnosticLimit)], Math.Max(0, diagnostics.Count - DiagnosticLimit));
    }
}

/// <summary>The files one pack's units changed in a <c>generate</c> run.</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Counts">The files by kind (<c>added</c>, <c>modified</c>, <c>deleted</c>, <c>hand-edited</c>, ...), only the kinds that occur.</param>
/// <param name="Files">The first <see cref="GenerateSummary.FileLimit"/> files, ordinal by path.</param>
/// <param name="More">How many files <paramref name="Files"/> leaves out.</param>
internal sealed record GeneratePackSummary(string Pack, IReadOnlyDictionary<string, int> Counts, IReadOnlyList<GenerateFile> Files, int More);

/// <summary>One changed file of a <c>generate</c> run.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Kind">What the run did to it.</param>
internal sealed record GenerateFile(string Path, FileChangeKind Kind);
