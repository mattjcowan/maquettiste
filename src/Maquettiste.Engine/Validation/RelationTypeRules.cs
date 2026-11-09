using System.Globalization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of relations with several types at an end, part 1 (several-types-design.md section 6): MQ3041 (where such an end can't be),
/// MQ3042 (a one-entity list), MQ3043 (an entry another entry covers), MQ2003 and MQ9211 on an entry's stereotype, MQ4115 (a database
/// stores nothing for such a relation, info in part 1) and MQ4116 (a binding field, a query field or a query collection naming such an end).
/// Every rule here needs an end with several types, or an end that writes <c>types</c>, so a model without them gets nothing new and pays
/// one test per relation end.
/// </summary>
internal static class RelationTypeRules
{
    /// <summary>
    /// The types an end leads to, for messages (MQ3116, MQ9305): "Part, Assembly, or an entity derived from one", from the end's types in
    /// their order (decision 5); "no entity yet" when the end covers none, as a stereotype nothing carries.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="end">The end.</param>
    /// <returns>The text.</returns>
    public static string TypesText(ModelSnapshot model, RelationEnd end)
    {
        var types = EndCoverage.TypesOf(model, end);
        return types.IsEmpty ? "no entity yet" : string.Join(", ", types.Select(t => t.Name)) + ", or an entity derived from one";
    }

    /// <summary>The relation end with an id, with its relation; <see langword="null"/> when the id is no relation end.</summary>
    /// <param name="model">The model.</param>
    /// <param name="endId">The end id.</param>
    /// <returns>The relation and the end.</returns>
    public static (Relation Relation, RelationEnd End)? EndOf(ModelSnapshot model, string endId)
    {
        if (!model.TryGetEntry(endId, out var entry) || entry.Kind != "end" || model.Get<Relation>(entry.OwnerId) is not { } relation)
            return null;
        foreach (var end in relation.Ends)
        {
            if (string.Equals(end.Id, endId, StringComparison.Ordinal))
                return (relation, end);
        }

        return null;
    }

    /// <summary>
    /// The relation file's checks of its ends' types: MQ3041, MQ3042, MQ3043, MQ2003 and MQ9211. Nothing for a relation whose ends all
    /// write <c>entity</c>.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="report">The report.</param>
    public static void CheckRelation(ValidationContext context, Relation relation, Report report)
    {
        var ends = relation.Ends;
        if (!ends.Any(e => e.Types is not null || e.AnyEntity == true))
            return;
        var model = context.Model;
        var nAry = relation.RelationKind == RelationKind.NAry || ends.Count > 2;
        var several = 0;
        for (var i = 0; i < ends.Count; i++)
        {
            var end = ends[i];
            var pointer = Ptr.At("/ends", i);
            if (end.HasSeveralTypes)
            {
                several++;
                // MQ3041: an n-ary relation's end can't have several types (decision 8).
                if (nAry)
                    report.Add("MQ3041", $"Relation '{relation.Name}' is n-ary, so its end '{end.Role}' can't have several types; give it one entity.", pointer, end.Id);
            }

            if (end.Types is not { } types)
                continue;

            // MQ3042: a list that holds one entity means the same as entity (decision 2).
            if (!end.HasSeveralTypes && types is [{ Entity: { } only }])
                report.Add("MQ3042", $"End '{end.Role}' lists only entity '{model.Get<Entity>(only)?.Name ?? only}' in types; write it as entity.", pointer + "/types", end.Id);

            for (var j = 0; j < types.Count; j++)
            {
                // MQ2003 and MQ9211: an entry's stereotype is a known key of a stereotype that applies to entities.
                if (types[j].Stereotype is not { } key)
                    continue;
                var at = Ptr.At(pointer + "/types", j) + "/stereotype";
                if (model.GetStereotype(key) is not { } stereotype)
                    report.Add("MQ2003", $"Stereotype '{key}' is not defined in model/vocabularies/stereotypes.", at, end.Id);
                else if (!ProcessRules.AppliesToEntities(stereotype))
                    report.Add("MQ9211", $"Type {(j + 1).ToString(CultureInfo.InvariantCulture)} of end '{end.Role}' names stereotype '{key}', which applies to {string.Join(", ", stereotype.AppliesTo)}, not to entities, so no "
                        + "entity can carry it; name a stereotype entities carry, or an entity.", at, end.Id);
            }

            if (end.HasSeveralTypes)
                CheckCoveredEntries(model, end, pointer, report);
        }

        // MQ3041: several types on both ends (decision 8); an n-ary relation is reported per end above.
        if (!nAry && several > 1)
            report.Add("MQ3041", $"Relation '{relation.Name}' has several types on both ends; keep them on one end.", "/ends");

        // MQ3041: a composition's child end can't be any entity (decision 9).
        if (ValidationContext.CompositionEnds(relation) is { } composition && ends[composition.Child] is { AnyEntity: true } child)
        {
            report.Add("MQ3041", $"End '{child.Role}' is the part in composition '{relation.Name}', so it can't be any entity; list the types it may be.",
                Ptr.At("/ends", composition.Child) + "/anyEntity", child.Id);
        }
    }

    // MQ3043 (decision 6): an entity entry that another entry already covers, because it derives from a listed entity, or because it carries a
    // listed stereotype or derives from an entity that carries one. A warning: carriers change with no edit to the relation.
    private static void CheckCoveredEntries(ModelSnapshot model, RelationEnd end, string pointer, Report report)
    {
        var types = end.Types!;
        for (var j = 0; j < types.Count; j++)
        {
            if (types[j].Entity is not { } id || model.Get<Entity>(id) is not { } entity)
                continue;
            var at = Ptr.At(pointer + "/types", j);
            var chain = Processes.SubjectCoverage.Chain(model, entity);
            string? message = null;
            foreach (var other in types)
            {
                if (other.Entity is { } baseId && !string.Equals(baseId, id, StringComparison.Ordinal) && chain.Contains(baseId, StringComparer.Ordinal)
                    && model.Get<Entity>(baseId) is { } listed)
                {
                    message = $"End '{end.Role}' lists entity '{entity.Name}', which entity '{listed.Name}' already covers, since it derives from it; remove the entry.";
                    break;
                }
            }

            if (message is null)
            {
                foreach (var other in types)
                {
                    if (other.Stereotype is not { } key)
                        continue;
                    if (Carries(model, entity, key))
                    {
                        message = $"End '{end.Role}' lists entity '{entity.Name}', which carries stereotype '{key}', also listed; remove the entry.";
                        break;
                    }

                    if (chain.Skip(1).Select(model.Get<Entity>).FirstOrDefault(b => b is not null && Carries(model, b, key)) is { } carrier)
                    {
                        message = $"End '{end.Role}' lists entity '{entity.Name}', which stereotype '{key}' already covers, since it derives from entity '{carrier.Name}', which carries it; remove the entry.";
                        break;
                    }
                }
            }

            if (message is not null)
                report.Add("MQ3043", message, at, end.Id);
        }
    }

    private static bool Carries(ModelSnapshot model, Entity entity, string key) =>
        model.EffectiveStereotypes(entity).Any(a => string.Equals(a.Stereotype.Key, key, StringComparison.Ordinal));

    /// <summary>
    /// MQ4115 (decision 22, info in part 1): for a relation with an end of several types, each database where every end has a type it
    /// covers placed (a one-type end its entity; an end with several types any entity it covers), and no mapping of the relation there sets
    /// <c>ignore</c>, stores nothing for it. Reported at the first end with several types.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="relation">The relation, which has an end with several types.</param>
    /// <param name="report">The report.</param>
    public static void CheckStoredNothing(ValidationContext context, Relation relation, Report report)
    {
        var model = context.Model;
        var first = -1;
        for (var i = 0; i < relation.Ends.Count && first < 0; i++)
        {
            if (relation.Ends[i].HasSeveralTypes)
                first = i;
        }

        if (first < 0 || relation.Ends.Count < 2)
            return;
        foreach (var database in model.All<Database>())
        {
            if (context.MappingsOf(database.Id, relation.Id).Any(d => d.Element is Mapping { Ignore: true }))
                continue;
            var placed = relation.Ends.All(end => end.HasSeveralTypes
                ? EndCoverage.CoveredBy(model, end).Any(id => model.Get<Entity>(id) is { } entity && Placed(context, database, entity))
                : end.SingleEntity is { } single && model.Get<Entity>(single) is { } entity && Placed(context, database, entity));
            if (placed)
            {
                report.Add("MQ4115", $"Relation '{relation.Name}' has an end with several types, so database '{database.Name}' stores nothing for it.",
                    Ptr.At("/ends", first), relation.Ends[first].Id);
            }
        }
    }

    // Whether an entity lands in a database: a binding there, else a mapping that does not ignore it, else the database's convention
    // (as the database run places entities, erratum E43).
    private static bool Placed(ValidationContext context, Database database, Entity entity)
    {
        if (entity.Bindings.Any(b => string.Equals(b.Database, database.Id, StringComparison.Ordinal)))
            return true;
        var mappings = context.MappingsOf(database.Id, entity.Id);
        return Resolution.DatabaseScope.Places(context.Model, database, entity.Package, mappings.Length > 0 ? mappings[0].Element as Mapping : null);
    }

    /// <summary>
    /// MQ4116 on an entity's bindings: a field that names a relation end with several types, which leads to more than one table, or another
    /// end of a relation with such an end, which no database stores yet.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="entity">The entity.</param>
    /// <param name="report">The report.</param>
    public static void CheckBindingFields(ValidationContext context, Entity entity, Report report)
    {
        if (!EndCoverage.Of(context.Model).Any)
            return;
        for (var b = 0; b < entity.Bindings.Count; b++)
        {
            var fields = entity.Bindings[b].Fields;
            for (var i = 0; i < fields.Count; i++)
            {
                if (ThroughSeveralTypes(context.Model, fields[i].Attribute) is { } through)
                {
                    report.Add("MQ4116", $"Field {i.ToString(CultureInfo.InvariantCulture)} of the binding of '{entity.Name}' names {Named(through)}; a field can't map it.",
                        Ptr.At(Ptr.At("/bindings", b) + "/fields", i) + "/attribute");
                }
            }
        }
    }

    /// <summary>
    /// MQ4116 on a query: a field of its select list, or of a collection's nested select list, or a collection, that names a relation end
    /// with several types, which leads to more than one table, or another end of a relation with such an end (as a navigation from one of
    /// that end's types does), which no database stores yet. A nested field's text names its collection.
    /// </summary>
    /// <param name="context">The validation context.</param>
    /// <param name="query">The query.</param>
    /// <param name="report">The report.</param>
    public static void CheckQuery(ValidationContext context, Query query, Report report)
    {
        var model = context.Model;
        if (!EndCoverage.Of(model).Any)
            return;
        CheckFields(query.Select, "/select", "");
        for (var c = 0; c < query.Collections.Count; c++)
        {
            var collection = query.Collections[c];
            var pointer = Ptr.At("/collections", c);
            var index = c.ToString(CultureInfo.InvariantCulture);
            if (ThroughSeveralTypes(model, collection.Attribute) is { } through)
                report.Add("MQ4116", $"Collection {index} of query '{query.Name}' names {Named(through)}; a collection can't fill it.", pointer + "/attribute");

            CheckFields(collection.Query.Select, pointer + "/query/select", " of collection " + index);
        }

        void CheckFields(IReadOnlyList<QueryField> fields, string at, string within)
        {
            for (var i = 0; i < fields.Count; i++)
            {
                if (fields[i].Attribute is { } attribute && ThroughSeveralTypes(model, attribute) is { } through)
                {
                    report.Add("MQ4116", $"Field {i.ToString(CultureInfo.InvariantCulture)}{within} of query '{query.Name}' names {Named(through)}; a field can't map it.",
                        Ptr.At(at, i) + "/attribute");
                }
            }
        }
    }

    /// <summary>
    /// The relation with an end of several types that a binding field's, query field's or query collection's <c>attribute</c> goes through,
    /// and the end it names: its first id segment is an end of such a relation, the end with several types or another one (a path under the
    /// end included). <see langword="null"/> otherwise, and always for a relation whose ends each have one type.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="reference">The <c>attribute</c>.</param>
    /// <returns>The relation and the end named.</returns>
    public static (Relation Relation, RelationEnd End)? ThroughSeveralTypes(ModelSnapshot model, string reference)
    {
        var dot = reference.IndexOf('.', StringComparison.Ordinal);
        var first = dot > 0 ? reference[..dot] : reference;
        return EndOf(model, dot > 0 ? reference[..dot] : reference) is { } found && EndCoverage.HasSeveralTypes(found.Relation) ? found : null;
    }

    // "end 'attachedTo', which leads to several types" for the end with several types itself; "end 'comment' of relation 'AttachedTo', which
    // has an end with several types" for another end of such a relation, in the words MQ7105 uses.
    private static string Named((Relation Relation, RelationEnd End) through) => through.End.HasSeveralTypes
        ? $"end '{through.End.Role}', which leads to several types"
        : $"end '{through.End.Role}' of relation '{through.Relation.Name}', which has an end with several types";
}
