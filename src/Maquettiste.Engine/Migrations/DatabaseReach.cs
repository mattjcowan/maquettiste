using System.Collections.Frozen;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Migrations;

/// <summary>
/// A database's reach (next-phase-1-design.md section 3.2, "Recording, captures and cost"): the element ids its resolved objects can
/// depend on, computed from the snapshot alone, with nothing resolved. A write uses it to choose the databases it may reshape when
/// the store holds no capture (and so no footprint) of a database yet. The reach is a superset of the footprint, the element ids in
/// the <c>e:</c> dependency keys of the database's resolved objects (<c>FactRecordingTests.Database_reach_covers_every_resolved_footprint</c>).
/// </summary>
/// <remarks>
/// It starts from the database, every element that names it (its tables, views, sequences, routines, types, SQL objects, queries,
/// mappings and facts), the elements that name one of those (entity and reference type bindings, seeds) and the entities its
/// convention takes (<see cref="DatabaseScope.ByConvention"/>). It then closes over what each reached element references (base
/// entities, value objects, scalar types, enums, reference types, stereotypes, packages and their parents), over the relations
/// that name a reached entity (and so the entities at their other ends, every entity an end with several types covers included where a mapping
/// of the relation in the database states storage) and
/// over the entities that derive from a reached one. A
/// reached stereotype's <c>implies</c> are references too, so the stereotypes it implies are reached: an element's effective set
/// (next-phase-1-design.md section 4.2) is in the reach of every database that reaches the element. Default stereotypes
/// (next-phase-2-design.md section 3.1) need no walk of their own: the database's schema entries and every package on a reached
/// element's chain are reached, and their <c>defaultStereotypes</c> are references; the package a table without a file or an
/// overlay takes its defaults from is its entity's or relation's, reached with it, and every package on a holder's chain (the
/// <c>e:</c> keys resolution adds) is reached through the parents.
/// </remarks>
internal static class DatabaseReach
{
    /// <summary>Computes a database's reach.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="database">The database.</param>
    /// <returns>The element ids.</returns>
    public static FrozenSet<string> Of(ModelSnapshot model, Database database)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(database);
        var reached = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        void Reach(string? id)
        {
            if (id is null)
                return;
            var owner = model.TryGetEntry(id, out var entry) ? entry.OwnerId : id;
            if (model.GetDocument(owner) is not null && reached.Add(owner))
                queue.Enqueue(owner);
        }

        Reach(database.Id);
        foreach (var reference in model.ReferencesTo(database.Id))
        {
            Reach(reference.FromElementId);
            // What names the database's own elements: an entity or reference type bound to one of its tables, a seed of one.
            foreach (var inward in model.ReferencesTo(reference.FromElementId))
                Reach(inward.FromElementId);
        }

        foreach (var entity in model.All<Entity>())
        {
            if (DatabaseScope.ByConvention(model, database, entity.Package))
                Reach(entity.Id);
        }

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            foreach (var reference in model.ReferencesFrom(id))
                Reach(reference.ToId);
            switch (model.GetDocument(id)?.Element)
            {
                case Entity entity:
                    for (var package = entity.Package; package is not null && model.Get<Package>(package) is { } p; package = p.Parent)
                    {
                        if (!reached.Add(p.Id))
                            break;
                        queue.Enqueue(p.Id);
                    }

                    // The relations that name it (and through them the entities at their other ends) and the entities that derive from it.
                    foreach (var inward in model.ReferencesTo(id))
                    {
                        if (model.GetDocument(inward.FromElementId)?.Element is Relation or Entity)
                            Reach(inward.FromElementId);
                    }

                    break;
                case Package package when package.Parent is { } parent:
                    Reach(parent);
                    break;
                case Relation relation when EndCoverage.HasSeveralTypes(relation) && StatesStorage(model, relation.Id, database.Id):
                    // An end with several types covers entities its file doesn't name (derived entities, a stereotype's carriers, any entity):
                    // a mapping of the relation that states storage in this database keys on each of them (several-types-design.md section 19.8,
                    // "Dependency keys"). Without one, nothing in the database reads them, so a model that only uses part 1 keeps its reach.
                    foreach (var end in relation.Ends.Where(e => e.HasSeveralTypes))
                    {
                        foreach (var covered in EndCoverage.CoveredBy(model, end))
                            Reach(covered);
                    }

                    break;
            }
        }

        return reached.ToFrozenSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether the relation's mapping in the database (the first by id, as the database run reads it) states storage: not ignored, with a
    /// <c>junctionTable</c> or an <c>ends</c> entry (decision 49).
    /// </summary>
    private static bool StatesStorage(ModelSnapshot model, string relationId, string databaseId)
    {
        Mapping? first = null;
        foreach (var reference in model.ReferencesTo(relationId))
        {
            if (model.GetDocument(reference.FromElementId)?.Element is Mapping m && string.Equals(m.Relation, relationId, StringComparison.Ordinal)
                && string.Equals(m.Database, databaseId, StringComparison.Ordinal) && (first is null || string.CompareOrdinal(m.Id, first.Id) < 0))
                first = m;
        }

        return first is { Ignore: false } && (first.JunctionTable is not null || first.Ends.Count > 0);
    }
}
