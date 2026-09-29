using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// Maps a run's diagnostics and file decisions to its <see cref="RunOutcome"/> with the precedence of engine-design.md section 16
/// (4, 1, 3, 2): errors other than hand-edit, region and schema-drift ones are <see cref="RunOutcome.Invalid"/>; conflicts (and, in
/// check mode, any hand edit) are <see cref="RunOutcome.Conflicts"/>; in check mode an added, modified, deleted or orphaned file
/// (including an owned orphan, <see cref="FileChangeKind.OrphanedOwned"/>, whose manifest line an apply would drop), a stale manifest
/// entry or a stale schema snapshot is <see cref="RunOutcome.Drift"/>.
/// </summary>
internal static class Outcomes
{
    /// <summary>Hand edits (MQ6009).</summary>
    public const string HandEdit = "MQ6009";

    /// <summary>Protected regions lost (MQ6010).</summary>
    public const string RegionLost = "MQ6010";

    /// <summary>Stale schema snapshot (MQ6018).</summary>
    public const string StaleSnapshot = "MQ6018";

    /// <summary>Computes the outcome.</summary>
    /// <param name="mode">The run mode.</param>
    /// <param name="diagnostics">Every diagnostic of the run.</param>
    /// <param name="changes">The file decisions.</param>
    /// <param name="staleManifest">Whether check mode found a manifest entry that differs from what an apply would record.</param>
    /// <returns>The outcome.</returns>
    public static RunOutcome Of(GenerationMode mode, IReadOnlyList<Diagnostic> diagnostics, IReadOnlyList<FileChange> changes, bool staleManifest = false)
    {
        if (diagnostics.Any(IsInvalid))
            return RunOutcome.Invalid;
        var check = mode == GenerationMode.Check;
        if (changes.Any(c => c.Kind == FileChangeKind.Conflict || (check && c.Kind == FileChangeKind.HandEdited))
            || diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error && d.Rule is HandEdit or RegionLost))
            return RunOutcome.Conflicts;
        if (check && (staleManifest
            || changes.Any(c => c.Kind is FileChangeKind.Added or FileChangeKind.Modified or FileChangeKind.Deleted or FileChangeKind.OrphanedOwned)
            || diagnostics.Any(d => d.Rule == StaleSnapshot)))
            return RunOutcome.Drift;
        return RunOutcome.Succeeded;
    }

    /// <summary>Whether a diagnostic makes a run invalid (exit 1).</summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <returns><see langword="true"/> for errors other than MQ6009, MQ6010 and MQ6018.</returns>
    public static bool IsInvalid(Diagnostic diagnostic) =>
        diagnostic.Severity == DiagnosticSeverity.Error && diagnostic.Rule is not (HandEdit or RegionLost or StaleSnapshot);

    /// <summary>
    /// Whether a planning error is unit-level: it skipped one unit, and the pack's other units still render (MQ6019, generation-ui.md
    /// section 5.3). The run's outcome is still <see cref="RunOutcome.Invalid"/>.
    /// </summary>
    /// <param name="diagnostic">The diagnostic.</param>
    /// <returns><see langword="true"/> for MQ6019.</returns>
    public static bool IsUnitLevel(Diagnostic diagnostic) => diagnostic.Rule == "MQ6019";

    /// <summary>Sorts diagnostics: path, line, column, rule, message, element.</summary>
    /// <param name="diagnostics">The diagnostics.</param>
    /// <returns>The sorted, de-duplicated list.</returns>
    public static IReadOnlyList<Diagnostic> Sort(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.Distinct()
            .OrderBy(d => d.FilePath ?? "", StringComparer.Ordinal).ThenBy(d => d.Line ?? 0).ThenBy(d => d.Column ?? 0)
            .ThenBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)
            .ThenBy(d => d.ElementId ?? "", StringComparer.Ordinal).ThenBy(d => d.JsonPointer ?? "", StringComparer.Ordinal)];
}
