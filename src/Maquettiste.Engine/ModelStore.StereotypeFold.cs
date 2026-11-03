using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>
/// The batch operation <c>fold-into-stereotype</c> (next-phase-2-design.md section 3.1, "Fold into a stereotype"): the planning lives
/// in <see cref="StereotypeFold"/>; here the databases of the target tables are resolved before the fold and on its candidate (only
/// those, as every write's classifier does), so the fold is held to changing nothing in the database, and its changes and key maps
/// join the batch.
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>
    /// Expands one fold: its updates join <paramref name="changes"/> and its key maps <paramref name="keyMaps"/> (one per database with
    /// moves, for the write's <c>rekey</c>). Answers the refusals, MQ4077 at the operation and one per refused table, or
    /// <see langword="null"/> when the fold applies (a fold where nothing matches changes nothing).
    /// </summary>
    private async Task<IReadOnlyList<Diagnostic>?> FoldIntoStereotypeAsync(ModelSnapshot snapshot, BatchOperation o, IReadOnlyDictionary<string, string?> written,
        List<PlannedChange> changes, List<MaterializeResult> keyMaps, string pointer, CancellationToken ct)
    {
        IReadOnlyList<Diagnostic> Refused(IEnumerable<string> messages) => [.. messages.Select(m => RuleCatalog.Create("MQ4077", m, o.Id, null, pointer))];

        if (StereotypeFold.Read(snapshot, o, written, out var problems) is not { } fold)
            return Refused(problems);
        var databases = fold.Databases;
        if (databases.Count == 0)
            return null;
        if (await FoldDatabasesAsync(snapshot, databases, ct).ConfigureAwait(false) is not { } before)
            return Refused([$"The databases of the target tables do not resolve, so the fold cannot be held to changing nothing in them."]);

        fold.Plan(before);
        if (fold.Refusals.Count > 0)
            return Refused(fold.Refusals);

        // Step 5: resolve the candidate, compare each folded member with the own one, and confirm once more after redefinitions. A
        // candidate that does not plan or resolve cannot be compared, so the fold adds no change it has not verified.
        var (after, failure) = await CandidateAsync(snapshot, fold, databases, ct).ConfigureAwait(false);
        if (after is null)
            return Refused([$"The fold cannot be held to changing nothing in the database: {failure}. Nothing is written."]);
        fold.Verify(before, after);
        if (fold.Refusals.Count > 0)
            return Refused(fold.Refusals);
        if (fold.Redefined)
        {
            (after, failure) = await CandidateAsync(snapshot, fold, databases, ct).ConfigureAwait(false);
            if (after is null)
                return Refused([$"The fold cannot be held to changing nothing in the database after its redefinitions: {failure}. Nothing is written."]);
        }

        fold.Confirm(before, after);
        if (fold.Refusals.Count > 0)
            return Refused(fold.Refusals);

        var (result, maps) = fold.Result();
        changes.AddRange(ToChanges(result, o.ExpectedHashes));
        keyMaps.AddRange(maps);
        return null;
    }

    /// <summary>
    /// The target databases resolved on the fold's candidate, or <see langword="null"/> and why when the candidate does not plan (its
    /// first error) or its databases do not resolve.
    /// </summary>
    private async Task<(IReadOnlyDictionary<string, RDatabase>? Databases, string? Failure)> CandidateAsync(ModelSnapshot snapshot, StereotypeFold fold,
        IReadOnlyList<string> databases, CancellationToken ct)
    {
        var plan = Plan(snapshot, [.. ToChanges(fold.Result().Changes)], ct);
        if (plan.Failed || plan.Candidate is not { } candidate)
        {
            var first = plan.Outcomes.SelectMany(o => o.Diagnostics).FirstOrDefault(d => d.Severity == DiagnosticSeverity.Error);
            return (null, first is null ? "the folded files do not plan" : $"the folded files do not plan ({first.Rule} {first.Message})");
        }

        return await FoldDatabasesAsync(candidate, databases, ct).ConfigureAwait(false) is { } resolved
            ? (resolved, null)
            : (null, "the databases of the target tables do not resolve once folded");
    }

    /// <summary>The given databases of a model, resolved (one conceptual run), by id; <see langword="null"/> when one does not resolve.</summary>
    private async Task<IReadOnlyDictionary<string, RDatabase>?> FoldDatabasesAsync(ModelSnapshot model, IReadOnlyList<string> databases, CancellationToken ct)
    {
        if (await ResolveDatabasesAsync(model, databases, freeze: false, ct).ConfigureAwait(false) is not { } resolved)
            return null;
        var byId = new Dictionary<string, RDatabase>(StringComparer.Ordinal);
        for (var i = 0; i < databases.Count; i++)
        {
            if (resolved[i] is not { } database)
                return null;
            byId[databases[i]] = database;
        }

        return byId;
    }
}
