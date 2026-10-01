using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

public sealed partial class ModelStore
{
    /// <summary>
    /// What deleting <paramref name="ids"/> with <paramref name="resolution"/> would do, computed like the delete itself (the same
    /// planner and the same validation) without writing anything (engine-design.md section 15.1).
    /// </summary>
    /// <param name="ids">The elements to delete, in the order a batch would name them.</param>
    /// <param name="resolution">The resolution.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan: what would be deleted, cleared, removed, refused, and the settings entries removed with it.</returns>
    public async Task<DeletePlan> GetDeletePlanAsync(IReadOnlyList<string> ids, DeleteResolution resolution, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var distinct = ids.Distinct(StringComparer.Ordinal).ToList();
        var changes = distinct
            .Select(id => new PlannedChange(BatchOp.Delete, id, snapshot.GetDocument(id) is { } d && d.Element.Id == id ? d.Hash : null, null, resolution))
            .ToList();
        var plan = Plan(snapshot, changes, ct);
        if (!plan.Failed)
            await ValidateAsync(plan, snapshot, ct).ConfigureAwait(false);

        var report = plan.DeleteReport;
        var refused = new List<DeletePlanRefusal>(report?.Refused ?? []);
        for (var i = 0; i < plan.Outcomes.Count; i++)
        {
            var outcome = plan.Outcomes[i];
            switch (outcome.Outcome)
            {
                case SaveOutcome.NotFound:
                    refused.Add(new DeletePlanRefusal(distinct[i], null, null, null, "no element has this id.", "not-found"));
                    break;
                case SaveOutcome.Conflict:
                    refused.Add(new DeletePlanRefusal(distinct[i], null, null, null, "the file changed on disk; read the model again.", "conflict"));
                    break;
                case SaveOutcome.Referenced:
                    foreach (var r in outcome.Referrers)
                    {
                        var from = snapshot.GetDocument(r.FromElementId)?.Element;
                        refused.Add(new DeletePlanRefusal(r.FromElementId, from?.KindName, from is null ? null : ChangePlanner.ReadableName(snapshot, from), r.JsonPointer,
                            "still references " + Label(snapshot, r.ToId) + ".", "referenced"));
                    }

                    break;
                case SaveOutcome.Invalid:
                    foreach (var d in outcome.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error && d.Rule != "MQ2001").Distinct().Order(Diagnostic.Order))
                    {
                        var element = d.ElementId is { } e ? snapshot.GetDocument(e)?.Element : null;
                        refused.Add(new DeletePlanRefusal(d.ElementId, element?.KindName, element is null ? null : ChangePlanner.ReadableName(snapshot, element), d.JsonPointer,
                            d.Message, d.Rule));
                    }

                    if (report is null || report.Refused.Count == 0)
                    {
                        foreach (var d in outcome.Diagnostics.Where(d => d.Rule == "MQ2001").Distinct().Order(Diagnostic.Order))
                        {
                            var element = d.ElementId is { } e ? snapshot.GetDocument(e)?.Element : null;
                            refused.Add(new DeletePlanRefusal(d.ElementId, element?.KindName, element is null ? null : ChangePlanner.ReadableName(snapshot, element),
                                d.JsonPointer, d.Message, d.Rule));
                        }
                    }

                    break;
            }
        }

        // Owned seeds go with their target in every resolution; the refuse planner deletes them without a report.
        var deletes = report?.Deletes.ToList() ?? [];
        if (report is null)
        {
            foreach (var id in plan.ChangeByElement.Keys.Where(id => !distinct.Contains(id)).Order(StringComparer.Ordinal))
            {
                if (snapshot.GetDocument(id) is { } document && document.Element.Id == id && plan.Deletes.Contains(Paths.FromRepoPath(document.Path)))
                {
                    var target = (document.Element as Seed)?.Target;
                    deletes.Add(new DeletePlanDelete(id, document.Element.KindName, ChangePlanner.ReadableName(snapshot, document.Element), document.Path,
                        target is null ? "goes with the deleted element" : "belongs to " + Label(snapshot, target)));
                }
            }
        }

        var deletedDatabases = distinct.Concat(deletes.Select(d => d.Id))
            .Select(id => snapshot.Get<Database>(id))
            .OfType<Database>()
            .Select(d => d.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var warnings = PackFiltersNaming(deletedDatabases);
        var first = plan.Outcomes.Select(o => o.Outcome).FirstOrDefault(o => o != SaveOutcome.Saved, SaveOutcome.Saved);
        return new DeletePlan(distinct, resolution, first, deletes, report?.Clears ?? [], report?.Removes ?? [], refused,
            first == SaveOutcome.Saved ? report?.Settings ?? [] : [], warnings);
    }

    private ModelPaths Paths => _paths.Value;

    private static string Label(ModelSnapshot snapshot, string id) => ChangePlanner.Describe(snapshot, id);

    /// <summary>
    /// The units of local packs whose <c>where.database</c> names one of <paramref name="names"/>. A delete leaves pack manifests as they
    /// are (they are not model files and a pack may name a database before it exists); the plan warns that such a unit stops matching.
    /// </summary>
    private List<DeletePlanWarning> PackFiltersNaming(IReadOnlyCollection<string> names)
    {
        var warnings = new List<DeletePlanWarning>();
        if (names.Count == 0)
            return warnings;
        var templates = Path.Combine(Paths.ModelRoot, "templates");
        if (!Directory.Exists(templates))
            return warnings;
        foreach (var folder in Directory.EnumerateDirectories(templates).Order(StringComparer.Ordinal))
        {
            var pack = Path.GetFileName(folder);
            var manifest = Path.Combine(folder, "pack.json");
            if (pack.StartsWith('.') || !File.Exists(manifest))
                continue;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllBytes(manifest), DocumentReader.ParseOptions);
                if (!document.RootElement.TryGetProperty("units", out var units) || units.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var unit in units.EnumerateArray())
                {
                    if (unit.ValueKind != JsonValueKind.Object || !unit.TryGetProperty("where", out var where) || where.ValueKind != JsonValueKind.Object
                        || !where.TryGetProperty("database", out var database) || database.ValueKind != JsonValueKind.String)
                        continue;
                    var name = database.GetString()!;
                    if (!names.Contains(name))
                        continue;
                    var unitId = unit.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                    warnings.Add(new DeletePlanWarning(
                        $"Unit {unitId ?? "(no id)"} of pack {pack} filters on database {name}; it will match nothing until a database takes that name again or the filter changes.",
                        pack,
                        unitId));
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                // An unreadable manifest is the pack loader's to report.
            }
        }

        return warnings;
    }
}
