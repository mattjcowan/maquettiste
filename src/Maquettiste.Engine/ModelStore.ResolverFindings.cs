using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

public sealed partial class ModelStore
{
    private readonly Lock _findingsGate = new();
    private (ModelSnapshot Snapshot, Task<IReadOnlyList<Diagnostic>> Task)? _findings;

    /// <summary>
    /// What the resolver finds in a snapshot that validation cannot see (MQ4005 over the resolved columns, MQ4001 on conventional names,
    /// MQ4008, MQ4009 and MQ4011 on what resolution leaves out), so every validate path (the editor's loop and <c>POST /api/validate</c>,
    /// <c>maquettiste validate</c> and its SARIF, the MCP <c>validate</c> tool) reports what generation would. The resolver runs only on
    /// a snapshot whose whole-model validation (load diagnostics included) has no error, as a generation run does, and once per snapshot:
    /// the findings are kept with the snapshot they belong to, and the shared work runs without the caller's token.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="full">The snapshot's whole-model report when the caller has it, else it is computed.</param>
    /// <param name="ct">Cancellation of the wait only.</param>
    /// <returns>The resolver's diagnostics, or none when the model has validation errors.</returns>
    private Task<IReadOnlyList<Diagnostic>> ResolverFindingsAsync(ModelSnapshot snapshot, ValidationReport? full, CancellationToken ct)
    {
        Task<IReadOnlyList<Diagnostic>> task;
        lock (_findingsGate)
        {
            if (_findings is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
            {
                task = cached.Task;
            }
            else
            {
                task = Task.Run(() => FindAsync(snapshot, full), CancellationToken.None);
                _findings = (snapshot, task);
            }
        }

        return task.WaitAsync(ct);
    }

    private async Task<IReadOnlyList<Diagnostic>> FindAsync(ModelSnapshot snapshot, ValidationReport? full)
    {
        var report = full ?? await _services.Validator.ValidateAsync(snapshot, ValidationScope.All, null, CancellationToken.None).ConfigureAwait(false);
        if (report.HasErrors || snapshot.LoadDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return [];
        var resolved = await _services.Resolver.ResolveAsync(snapshot, null, CancellationToken.None).ConfigureAwait(false);
        // Positioned like validation's findings, so a text line, SARIF and the Problems panel point into the file.
        var positions = new Validation.Positions(_options, snapshot);
        var found = new List<Diagnostic>(resolved.Diagnostics.Count);
        foreach (var d in resolved.Diagnostics)
        {
            if (d.Line is null && d.FilePath is not null && d.JsonPointer is not null
                && await positions.LocateAsync(d.FilePath, d.JsonPointer, CancellationToken.None).ConfigureAwait(false) is { } at)
                found.Add(d with { Line = at.Line, Column = at.Column });
            else
                found.Add(d);
        }

        return found;
    }

    /// <summary>
    /// The resolver's findings a report does not already carry: an exact repeat is dropped, and so is a finding without a pointer whose
    /// rule validation already reports on the same element (validation's file-level MQ4001 or MQ4008 says it with the pointer). The
    /// resolved MQ4005 skips the pair the file-level MQ4005 reports on its own (DatabaseRun.CheckForeignKeyColumns).
    /// </summary>
    /// <param name="report">The validator's diagnostics.</param>
    /// <param name="findings">The resolver's diagnostics.</param>
    /// <returns>The findings to add.</returns>
    internal static IEnumerable<Diagnostic> NewFindings(IReadOnlyList<Diagnostic> report, IReadOnlyList<Diagnostic> findings)
    {
        var exact = report.Select(LoadKey).ToHashSet(StringComparer.Ordinal);
        var onElement = report.Select(d => d.Rule + "\u0000" + d.ElementId).ToHashSet(StringComparer.Ordinal);
        return findings.Where(d => !exact.Contains(LoadKey(d)) && (d.JsonPointer is not null || !onElement.Contains(d.Rule + "\u0000" + d.ElementId)));
    }
}
