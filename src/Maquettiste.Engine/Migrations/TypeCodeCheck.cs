using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Migrations;

/// <summary>One entity whose type code a type column of a relation mapping holds in a database (several-types-design.md decision 52).</summary>
/// <param name="EntityId">The entity's id.</param>
/// <param name="EntityName">The entity's name.</param>
/// <param name="Code">The working model's code: a string or an integer.</param>
/// <param name="Column">The type column's name.</param>
/// <param name="Table">The name of the table that holds it.</param>
/// <param name="Relation">The relation's name.</param>
/// <param name="RelationId">The relation's id.</param>
/// <param name="EndId">The id of the end whose type column it is.</param>
internal sealed record TypeCodeHolder(string EntityId, string EntityName, object Code, string Column, string Table, string Relation, string RelationId,
    string EndId);

/// <summary>
/// MQ4119 (several-types-design.md decision 52): a type code that changed since the baseline while a type column holds it. A changed code changes
/// no table, so no migration fact asks for it, and stored links that hold the old code stop matching. The working side's codes and type columns
/// come from the database run (<see cref="RDatabase.RelationStorage"/>); the baseline's codes are read from its files, with no resolution, by the
/// run's rule (<see cref="DatabaseRun.CodeValue"/>): a binding's <c>discriminatorValue</c>, else the mapping's, else the entity's name. A type
/// column the baseline's mapping didn't name holds no stored links, so it gives nothing. No fact settles it, so it repeats until the baseline moves
/// past the change; a suppression works, as for any finding.
/// </summary>
internal static class TypeCodeCheck
{
    /// <summary>
    /// Every entity a type column of a stored relation holds in a resolved database, by entity id, then table, then column; empty in a database
    /// with no such column (and so in every model with no relation with an end of several types).
    /// </summary>
    /// <param name="database">The resolved database.</param>
    /// <returns>The holders.</returns>
    public static IReadOnlyList<TypeCodeHolder> HoldersOf(RDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var found = new List<TypeCodeHolder>();
        foreach (var note in database.RelationStorage)
        {
            if (note.Mapping is not { } mapping || mapping.TypeCodes.Count == 0)
                continue;
            foreach (var end in mapping.Links.SelectMany(l => l.Ends.Select(e => (Link: l, End: e))).Where(x => x.End.TypeColumn is not null))
            {
                // The column holds the codes of the end's target and of the entities that derive from it.
                foreach (var code in mapping.TypeCodes.Where(c => DerivesFrom(c.Entity, end.End.Type)))
                {
                    found.Add(new TypeCodeHolder(code.Entity.Id, code.Entity.Name, code.Code, end.End.TypeColumn!.Name, end.End.TypeColumn.Table?.Name ?? end.Link.Table.Name,
                        note.Relation.Name, note.Relation.Id, end.End.End.Id));
                }
            }
        }

        return [.. found.Distinct().OrderBy(h => h.EntityId, StringComparer.Ordinal).ThenBy(h => h.Table, StringComparer.Ordinal)
            .ThenBy(h => h.Column, StringComparer.Ordinal).ThenBy(h => h.Relation, StringComparer.Ordinal)];

        static bool DerivesFrom(REntity entity, REntity type)
        {
            for (var e = entity; e is not null; e = e.Base)
            {
                if (ReferenceEquals(e, type) || string.Equals(e.Id, type.Id, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// An entity's type code in a database read from files (decision 42): the <c>discriminatorValue</c> of its binding to the database, else of
    /// its mapping element there, else its name; with the element and pointer that set it.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="databaseId">The database's id.</param>
    /// <returns>The code, a string or an integer, and where it is set.</returns>
    public static (object Code, string ElementId, string Pointer) CodeOf(ModelSnapshot model, Entity entity, string databaseId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(entity);
        // A binding to the database wins (MQ4054): its value, else the name; a mapping's value is then ignored, as the run ignores it.
        for (var i = 0; i < entity.Bindings.Count; i++)
        {
            if (!string.Equals(entity.Bindings[i].Database, databaseId, StringComparison.Ordinal))
                continue;
            return DatabaseRun.CodeValue(entity.Bindings[i].DiscriminatorValue) is { } bound
                ? (bound, entity.Id, "/bindings/" + i.ToString(CultureInfo.InvariantCulture) + "/discriminatorValue")
                : (entity.Name, entity.Id, "/name");
        }

        var mapping = model.All<Mapping>()
            .Where(m => string.Equals(m.Entity, entity.Id, StringComparison.Ordinal) && string.Equals(m.Database, databaseId, StringComparison.Ordinal))
            .OrderBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault();
        if (mapping is not null && DatabaseRun.CodeValue(mapping.DiscriminatorValue) is { } mapped)
            return (mapped, mapping.Id, "/discriminatorValue");
        return (entity.Name, entity.Id, "/name");
    }

    /// <summary>MQ4119 for each database and each entity a type column holds whose code differs from the baseline's.</summary>
    /// <param name="working">The working model.</param>
    /// <param name="baseline">The baseline model.</param>
    /// <param name="databases">The databases of both models whose baseline resolved, by name.</param>
    /// <param name="holders">Per database id, what its type columns hold in the working model.</param>
    /// <param name="label">The baseline's label (unused in the message, which names the baseline as "the baseline").</param>
    /// <returns>The findings, unsettled.</returns>
    public static IReadOnlyList<Diagnostic> Findings(ModelSnapshot working, ModelSnapshot baseline, IReadOnlyList<Database> databases,
        IReadOnlyDictionary<string, IReadOnlyList<TypeCodeHolder>> holders, string label)
    {
        ArgumentNullException.ThrowIfNull(working);
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(databases);
        ArgumentNullException.ThrowIfNull(holders);
        _ = label;
        var found = new List<Diagnostic>();
        foreach (var database in databases)
        {
            if (!holders.TryGetValue(database.Id, out var held) || held.Count == 0 || baseline.Get<Database>(database.Id) is null)
                continue;
            foreach (var byEntity in held.GroupBy(h => h.EntityId, StringComparer.Ordinal))
            {
                if (working.Get<Entity>(byEntity.Key) is not { } entity || baseline.Get<Entity>(byEntity.Key) is not { } old)
                    continue; // an entity the baseline lacks has no stored links
                var now = CodeOf(working, entity, database.Id);
                var before = CodeOf(baseline, old, database.Id).Code;
                if (Same(before, now.Code))
                    continue;
                // Only a type column the baseline had holds stored links: one added since then holds none yet.
                var stored = byEntity.Where(h => HeldInBaseline(working, baseline, database.Id, h)).ToList();
                if (stored.Count == 0)
                    continue;
                var columns = string.Join(" and ", stored.Select(h => $"column '{h.Column}' of table '{h.Table}' holds codes of relation '{h.Relation}'").Distinct());
                found.Add(RuleCatalog.Create("MQ4119",
                    $"Since the baseline, the type code of '{entity.Name}' in database '{database.Name}' changed from {Quoted(before)} to {Quoted(now.Code)}, and {columns}; links stored with the old code no longer find their object. Update those rows, or keep the stored code.",
                    now.ElementId, working.GetDocument(now.ElementId)?.Path, now.Pointer));
            }
        }

        return found;
    }

    /// <summary>
    /// Whether the baseline stored the holder's codes: its mapping of the relation in the database (the first by id, as the run reads it, and not
    /// ignored) names the same type column for the end as the working model's.
    /// </summary>
    private static bool HeldInBaseline(ModelSnapshot working, ModelSnapshot baseline, string databaseId, TypeCodeHolder holder) =>
        TypeColumnOf(working, databaseId, holder) is { } now && string.Equals(TypeColumnOf(baseline, databaseId, holder), now, StringComparison.Ordinal);

    private static string? TypeColumnOf(ModelSnapshot model, string databaseId, TypeCodeHolder holder) =>
        model.All<Mapping>()
            .Where(m => string.Equals(m.Relation, holder.RelationId, StringComparison.Ordinal) && string.Equals(m.Database, databaseId, StringComparison.Ordinal))
            .OrderBy(m => m.Id, StringComparer.Ordinal).FirstOrDefault() is { Ignore: false } mapping
            ? mapping.Ends.FirstOrDefault(e => string.Equals(e.End, holder.EndId, StringComparison.Ordinal))?.TypeColumn
            : null;

    private static bool Same(object a, object b) =>
        (a is string) == (b is string) && string.Equals(Text(a), Text(b), StringComparison.Ordinal);

    private static string Text(object value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";

    private static string Quoted(object code) => code is string ? "'" + code + "'" : Text(code);
}
