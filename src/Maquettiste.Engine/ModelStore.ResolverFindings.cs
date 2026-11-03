using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

public sealed partial class ModelStore
{
    private readonly Lock _findingsGate = new();
    private (ModelSnapshot Snapshot, Task<ResolverFindings> Task)? _findings;
    private readonly Lock _resolvedGate = new();
    private (ModelSnapshot Snapshot, Task<ResolvedSnapshot> Task)? _resolved;
    private readonly Lock _wholeGate = new();
    private (ModelSnapshot Snapshot, Task<ValidationReport> Task)? _whole;
    private readonly Lock _modelGate = new();
    private (ModelSnapshot Snapshot, Task<Resolution.ResolvedModel> Task)? _model;

    /// <summary>
    /// A snapshot's whole-model validation report and, when it has no error (pending migration facts aside, MQ4101), its resolved model:
    /// computed once per snapshot and shared
    /// by everything that needs them (validate's resolver findings, the resolved model's paged reader, template previews and path
    /// listings), so an edit costs one validation and one resolution however many of them ask. Concurrent callers wait for the same
    /// work (single flight), which runs without any caller's token; the last snapshot's result is kept. The validation leaves seeds
    /// with a rows script unjudged (<see cref="ValidationScope.Previews"/>; next-phase-2-design.md section 3.4, decision 31), so no
    /// preview runs a rows script its unit does not read; <see cref="WholeReportAsync"/> judges them for validate.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="ct">Cancellation of the wait only.</param>
    /// <returns>The report and the resolved model (<see langword="null"/> when validation has errors other than MQ4101).</returns>
    internal Task<ResolvedSnapshot> ResolvedAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        Task<ResolvedSnapshot> task;
        lock (_resolvedGate)
        {
            if (_resolved is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
            {
                task = cached.Task;
            }
            else
            {
                task = Task.Run(() => ResolveSnapshotAsync(snapshot), CancellationToken.None);
                _resolved = (snapshot, task);
            }
        }

        return task.WaitAsync(ct);
    }

    private async Task<ResolvedSnapshot> ResolveSnapshotAsync(ModelSnapshot snapshot)
    {
        var report = await _services.Validator.ValidateAsync(snapshot, ValidationScope.Previews, null, CancellationToken.None).ConfigureAwait(false);
        if (report.Diagnostics.Any(Generation.Outcomes.BlocksResolution))
            return new ResolvedSnapshot(snapshot, report, null);
        Task<Resolution.ResolvedModel> resolving;
        lock (_modelGate)
        {
            if (_model is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
            {
                resolving = cached.Task;
            }
            else
            {
                resolving = _services.Resolver.ResolveAsync(snapshot, null, CancellationToken.None);
                _model = (snapshot, resolving);
            }
        }

        return new ResolvedSnapshot(snapshot, report, await resolving.ConfigureAwait(false));
    }

    /// <summary>
    /// The snapshot's resolved model when the store already holds it or is resolving it (<see cref="ResolvedAsync"/>, or a generation run
    /// that shared its own), else <see langword="null"/>: a plan or generate after a validate of the same snapshot resolves nothing again,
    /// and its checks find the values the validate evaluated in the same host.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The resolution, or <see langword="null"/>.</returns>
    internal Task<Resolution.ResolvedModel>? SharedModel(ModelSnapshot snapshot)
    {
        lock (_modelGate)
            return _model is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled ? cached.Task : null;
    }

    /// <summary>Keeps a generation run's resolved model of a snapshot whose validation has no error, unless the store holds one of it.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="model">Its resolved model.</param>
    internal void ShareModel(ModelSnapshot snapshot, Resolution.ResolvedModel model)
    {
        lock (_modelGate)
        {
            if (_model is not { } cached || !ReferenceEquals(cached.Snapshot, snapshot) || cached.Task.IsFaulted || cached.Task.IsCanceled)
                _model = (snapshot, Task.FromResult(model));
        }
    }

    /// <summary>
    /// A snapshot's whole-model validation report with every seed's rows judged, scripted rows included (next-phase-2-design.md section
    /// 3.4, decision 31): the shared report of <see cref="ResolvedAsync"/> when no seed has a rows script, else a validation of its own,
    /// once per snapshot (single flight, without any caller's token; the last snapshot's result is kept).
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="ct">Cancellation of the wait only.</param>
    /// <returns>The report.</returns>
    private async Task<ValidationReport> WholeReportAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        if (!snapshot.All<Seed>().Any(s => s.RowsFrom?.Script is not null))
            return (await ResolvedAsync(snapshot, ct).ConfigureAwait(false)).Report;
        Task<ValidationReport> task;
        lock (_wholeGate)
        {
            if (_whole is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
            {
                task = cached.Task;
            }
            else
            {
                task = Task.Run(() => _services.Validator.ValidateAsync(snapshot, ValidationScope.All, null, CancellationToken.None), CancellationToken.None);
                _whole = (snapshot, task);
            }
        }

        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// What the resolver finds in a snapshot that validation cannot see (MQ4005 over the resolved columns, MQ4061 on what a foreign key's
    /// on-delete sets, MQ4001 on conventional names, MQ4008, MQ4009 and MQ4011 on what resolution leaves out), so every validate path (the editor's loop and <c>POST /api/validate</c>,
    /// <c>maquettiste validate</c> and its SARIF, the MCP <c>validate</c> tool) reports what generation would. The resolver runs only on
    /// a snapshot whose whole-model validation (load diagnostics included) has no error, as a generation run does, and once per snapshot
    /// (<see cref="ResolvedAsync"/>): the findings are kept with the snapshot they belong to, and the shared work runs without the
    /// caller's token.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="ct">Cancellation of the wait only.</param>
    /// <returns>The resolver's diagnostics, or none when the model has validation errors.</returns>
    private Task<ResolverFindings> ResolverFindingsAsync(ModelSnapshot snapshot, CancellationToken ct)
    {
        Task<ResolverFindings> task;
        lock (_findingsGate)
        {
            if (_findings is { } cached && ReferenceEquals(cached.Snapshot, snapshot) && !cached.Task.IsFaulted && !cached.Task.IsCanceled)
            {
                task = cached.Task;
            }
            else
            {
                task = Task.Run(() => FindAsync(snapshot), CancellationToken.None);
                _findings = (snapshot, task);
            }
        }

        return task.WaitAsync(ct);
    }

    private async Task<ResolverFindings> FindAsync(ModelSnapshot snapshot)
    {
        if (snapshot.LoadDiagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
            return ResolverFindings.Empty;
        var shared = await ResolvedAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
        if (shared.Model is not { } resolved)
            return ResolverFindings.Empty;
        // The checks after resolution (next-phase-2-design.md section 5.5) run here, once per snapshot, so every validate path reports
        // the calculated values that fail and the resolved rules' findings; the latter apart, for a validation without script rules.
        var checks = _services.Checks is { } run
            ? await run.RunAsync(snapshot, resolved, CancellationToken.None).ConfigureAwait(false)
            : Validation.ResolvedCheckResult.Empty;

        // Positioned like validation's findings, so a text line, SARIF and the Problems panel point into the file.
        var positions = new Validation.Positions(_options, snapshot);
        var found = new List<Diagnostic>(resolved.Diagnostics.Count + checks.Findings.Count);
        foreach (var d in resolved.Diagnostics)
        {
            if (d.Line is null && d.FilePath is not null && d.JsonPointer is not null
                && await positions.LocateAsync(d.FilePath, d.JsonPointer, CancellationToken.None).ConfigureAwait(false) is { } at)
                found.Add(d with { Line = at.Line, Column = at.Column });
            else
                found.Add(d);
        }

        found.AddRange(checks.Findings);

        // The findings the elements' suppressions moved (section 5.6), positioned as the reported ones are.
        var suppressed = new List<SuppressedFinding>(resolved.Suppressed.Count + checks.Suppressed.Count);
        foreach (var s in resolved.Suppressed)
        {
            var d = s.Finding;
            suppressed.Add(d.Line is null && d.FilePath is not null && d.JsonPointer is not null
                && await positions.LocateAsync(d.FilePath, d.JsonPointer, CancellationToken.None).ConfigureAwait(false) is { } at
                ? s with { Finding = d with { Line = at.Line, Column = at.Column } } : s);
        }

        suppressed.AddRange(checks.Suppressed);
        return new ResolverFindings(found, checks.ScriptFindings) { Suppressed = suppressed, ScriptSuppressed = checks.ScriptSuppressed };
    }

    /// <summary>The resolver's findings with the checks' (<see cref="ResolverFindingsAsync"/>).</summary>
    /// <param name="Findings">The resolver's diagnostics and the checks' findings other than script rules'.</param>
    /// <param name="ScriptFindings">The resolved rules' findings, which a validation without script rules leaves out.</param>
    private sealed record ResolverFindings(IReadOnlyList<Diagnostic> Findings, IReadOnlyList<Diagnostic> ScriptFindings)
    {
        public static ResolverFindings Empty { get; } = new([], []) { Resolved = false };

        /// <summary>Whether the model resolved (MQ5020 is judged only then).</summary>
        public bool Resolved { get; init; } = true;

        /// <summary>The resolver's and the checks' suppressed findings, other than script rules'.</summary>
        public IReadOnlyList<SuppressedFinding> Suppressed { get; init; } = [];

        /// <summary>The resolved rules' suppressed findings.</summary>
        public IReadOnlyList<SuppressedFinding> ScriptSuppressed { get; init; } = [];
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

/// <summary>A snapshot's whole-model validation report and resolved model (<see cref="ModelStore.ResolvedAsync"/>).</summary>
/// <param name="Snapshot">The snapshot.</param>
/// <param name="Report">The whole-model validation report.</param>
/// <param name="Model">The resolved model, or <see langword="null"/> when the report has errors other than MQ4101.</param>
internal sealed record ResolvedSnapshot(ModelSnapshot Snapshot, ValidationReport Report, Resolution.ResolvedModel? Model);
