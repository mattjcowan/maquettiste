using System.Globalization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of an entity's <c>copy</c> block (parts-and-copy-design.md): MQ3045 (an attribute or link entry that can't stand), MQ3046 (an
/// <c>original</c> that can't hold the id of the record copied from), MQ3047 (a part entry that can't stand) and MQ3048 (a part choice that
/// can't stand). The choice's condition is an expression site of the entity, so the expression rules type it against the part's attributes.
/// Each block is checked on the entity that writes it, against that entity's attributes and parts; a derived entity holds everything its
/// bases do, so an entry a base may write stays right on it. A project without a block does no work here.
/// </summary>
internal static class CopyRules
{
    /// <summary>Runs the rules on the report's entity, when it writes a <c>copy</c> block.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        if (report.Document.Element is not Entity { Copy: { } copy } entity)
            return;
        var model = context.Model;
        CheckAttributes(model, entity, copy.Attributes ?? [], "/copy/attributes", report);
        CheckLinks(model, entity, copy.Links ?? [], report);
        CheckParts(model, entity, copy.Parts ?? [], report);
    }

    // ---- MQ3045 and MQ3046 on attributes ----

    private static void CheckAttributes(ModelSnapshot model, Entity entity, IReadOnlyList<CopyAttributeEntry> entries, string pointer, Report report)
    {
        if (entries.Count == 0)
            return;
        var held = ExprSites.ProcessAttributesOf(model, entity).ToDictionary(a => a.Id, StringComparer.Ordinal);
        var key = KeyOf(model, entity)?.Attributes ?? [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < entries.Count; j++)
        {
            var entry = entries[j];
            var at = Ptr.At(pointer, j);
            if (!seen.Add(entry.Attribute))
            {
                report.Add("MQ3045", $"'{entity.Name}' has two copy entries for attribute '{Name(held, entry.Attribute)}'; remove the second.", at + "/attribute");
                continue;
            }

            if (!held.TryGetValue(entry.Attribute, out var attribute))
            {
                report.Add("MQ3045", $"Attribute '{entry.Attribute}' is not one of the attributes of '{entity.Name}' (its own, an inherited one or one from a stereotype it carries); remove the entry.",
                    at + "/attribute");
                continue;
            }

            if (attribute.Derived is not null)
            {
                report.Add("MQ3045", $"Attribute '{attribute.Name}' of '{entity.Name}' is derived, worked out from others, so a copy doesn't carry it over; remove the entry.", at + "/attribute");
                continue;
            }

            if (key.Contains(attribute.Id, StringComparer.Ordinal))
            {
                report.Add("MQ3045", $"Attribute '{attribute.Name}' is part of the key of '{entity.Name}': a copy is a new record, so its key is always new and made the team's way; remove the entry.",
                    at + "/attribute");
                continue;
            }

            if (entry.OnCopy == AttributeOnCopy.Clear && attribute.Required)
                report.Add("MQ3045", $"Attribute '{attribute.Name}' of '{entity.Name}' is required, so a copy can't leave it empty; use reset, or remove the entry.", at + "/onCopy");
            else if (entry.OnCopy == AttributeOnCopy.Reset && attribute.Required && attribute.Default is null && attribute.DefaultExpression is null)
                report.Add("MQ3045", $"Attribute '{attribute.Name}' of '{entity.Name}' is required and has no default, so a reset copy would leave it empty; give it a default, or remove the entry.",
                    at + "/onCopy");
            if (entry.OnCopy == AttributeOnCopy.Original && OriginalProblem(model, entity, held, key, attribute) is { } problem)
                report.Add("MQ3046", problem, at + "/onCopy");
        }
    }

    /// <summary>Why an attribute can't hold the id of the record copied from, or <see langword="null"/> when it can.</summary>
    private static string? OriginalProblem(ModelSnapshot model, Entity entity, Dictionary<string, ModelAttribute> held, IReadOnlyList<string> key, ModelAttribute attribute)
    {
        if (key.Count != 1 || !held.TryGetValue(key[0], out var keyAttribute))
            return $"The key of '{entity.Name}' is not one attribute, so attribute '{attribute.Name}' can't hold the id of the record a copy was made from; remove the entry.";
        if (attribute.Collection)
            return $"Attribute '{attribute.Name}' of '{entity.Name}' holds a list, so it can't hold the id of the record a copy was made from; use a single attribute.";
        var wanted = ExprSites.TypeOf(model, keyAttribute.Type);
        var actual = ExprSites.TypeOf(model, attribute.Type);
        var fits = string.Equals(wanted.Kind, actual.Kind, StringComparison.Ordinal) && string.Equals(wanted.Of, actual.Of, StringComparison.Ordinal)
            && wanted.Kind is not ("unknown" or "value-object") && actual.Bits >= wanted.Bits;
        return fits
            ? null
            : $"Attribute '{attribute.Name}' of '{entity.Name}' is typed {TypeName(model, attribute.Type)}, and its key '{keyAttribute.Name}' is typed "
                + $"{TypeName(model, keyAttribute.Type)}, so it can't hold the id of the record a copy was made from; type it as the key is typed.";
    }

    // ---- MQ3045 and MQ3046 on links ----

    private static void CheckLinks(ModelSnapshot model, Entity entity, IReadOnlyList<CopyLinkEntry> entries, Report report)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < entries.Count; j++)
        {
            var entry = entries[j];
            var at = Ptr.At("/copy/links", j);
            if (PartTree.EndOf(model, entry.End) is not { } found)
                continue; // an unknown id is MQ2001
            var (relation, named) = found;
            if (!seen.Add(entry.End))
            {
                report.Add("MQ3045", $"'{entity.Name}' has two copy entries for end '{named.Role}'; remove the second.", at + "/end");
                continue;
            }

            var own = relation.Ends.Count == 2 ? (ReferenceEquals(relation.Ends[0], named) ? relation.Ends[1] : relation.Ends[0]) : null;
            string? problem = own is null
                ? $"End '{named.Role}' belongs to relation '{relation.Name}', which has more than two ends, so it is no link of a copy; remove the entry."
                : relation.RelationKind == RelationKind.Composition
                    ? $"End '{named.Role}' belongs to composition '{relation.Name}': a composition makes parts of a whole, which the copy's parts say, not a link; remove the entry."
                    : !EndCoverage.Covers(model, own, entity.Id)
                        ? $"'{entity.Name}' doesn't reach end '{named.Role}' of relation '{relation.Name}'; name the end at the other side of a relation of '{entity.Name}'."
                        : named.Max != MaxCardinality.One
                            ? $"End '{named.Role}' of relation '{relation.Name}' leads to many records, so it is no field of a copy; name a to-one end."
                            : null;
            if (problem is not null)
            {
                report.Add("MQ3045", problem, at + "/end");
                continue;
            }

            if (entry.OnCopy == LinkOnCopy.Original && !EndCoverage.Covers(model, named, entity.Id))
                report.Add("MQ3046", $"End '{named.Role}' of relation '{relation.Name}' can't lead to a '{entity.Name}', so it can't link a copy to the record it was made from; name an end whose type covers '{entity.Name}'.",
                    at + "/onCopy");
        }
    }

    // ---- MQ3047 and MQ3048 on parts ----

    private static void CheckParts(ModelSnapshot model, Entity entity, IReadOnlyList<CopyPartEntry> entries, Report report)
    {
        if (entries.Count == 0)
            return;
        var tree = PartTree.Of(model, entity);
        if (tree.Truncated)
            report.Add("MQ3047", DiagnosticSeverity.Warning,
                $"The parts of '{entity.Name}' pass {PartTree.MaxNodes.ToString("N0", CultureInfo.InvariantCulture)} and are listed only that far, so an end beyond them is not checked; fix the compositions with two owners (MQ3016).",
                "/copy/parts");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < entries.Count; j++)
        {
            var entry = entries[j];
            var at = Ptr.At("/copy/parts", j);
            if (PartTree.EndOf(model, entry.End) is not { } found)
                continue; // an unknown id is MQ2001
            var (relation, named) = found;
            if (!seen.Add(entry.End))
            {
                report.Add("MQ3047", $"'{entity.Name}' has two copy entries for end '{named.Role}'; remove the second.", at + "/end");
                continue;
            }

            if (!tree.Reaches(entry.End) && !tree.Truncated)
            {
                report.Add("MQ3047", ValidationContext.CompositionEnds(relation) is { } ends && ReferenceEquals(relation.Ends[ends.Child], named) && named.HasSeveralTypes
                    ? $"End '{named.Role}' of composition '{relation.Name}' has several types, and the parts of a record list only parts of one type; remove the entry."
                    : $"End '{named.Role}' of relation '{relation.Name}' is not the part end of a composition among the parts of '{entity.Name}'; name the end that leads to a kind of part.",
                    at + "/end");
                continue;
            }

            switch (entry.OnCopy, entry.Choose)
            {
                case (PartOnCopy.One, null):
                    report.Add("MQ3047", $"The copy of '{entity.Name}' keeps one '{named.Role}' part but doesn't say which; add choose with a condition, a state or latestBy.", at + "/onCopy");
                    break;
                case (not PartOnCopy.One, not null):
                    report.Add("MQ3047", $"The copy of '{entity.Name}' keeps {(entry.OnCopy == PartOnCopy.All ? "every" : "no")} '{named.Role}' part, so it chooses none; remove choose, or use one.",
                        at + "/choose");
                    break;
                case (PartOnCopy.One, { Condition: not null, State: not null }):
                    report.Add("MQ3047", $"The choice of one '{named.Role}' part names both a condition and a state; keep one.", at + "/choose");
                    break;
            }

            if (entry.OnCopy == PartOnCopy.None && entry.Attributes is { Count: > 0 })
                report.Add("MQ3047", $"The copy of '{entity.Name}' keeps no '{named.Role}' part, so it sets no values on them; remove attributes, or copy the parts.", at + "/attributes");
            if (!tree.Reaches(entry.End) || named.SingleEntity is not { } partId || model.Get<Entity>(partId) is not { } part)
                continue;
            if (entry.OnCopy == PartOnCopy.One && entry.Choose is { } choose)
                CheckChoice(model, part, named, choose, at + "/choose", report);
            if (entry.OnCopy != PartOnCopy.None)
                CheckAttributes(model, part, entry.Attributes ?? [], at + "/attributes", report);
        }
    }

    private static void CheckChoice(ModelSnapshot model, Entity part, RelationEnd end, CopyChoice choose, string pointer, Report report)
    {
        if (choose.State is { } stateId && model.TryGetEntry(stateId, out var stateEntry) && stateEntry.Kind == "state"
            && model.Get<Process>(stateEntry.OwnerId) is { } process)
        {
            var state = FindState(process.States, stateId);
            if (process.Use != ProcessUse.Lifecycle || !SubjectCoverage.Governs(model, process, part))
                report.Add("MQ3048", $"State '{state?.Name ?? stateId}' belongs to {(process.Use == ProcessUse.Lifecycle ? "lifecycle" : "process")} '{process.Name}', which doesn't govern '{part.Name}'; name a state of a lifecycle of '{part.Name}'.",
                    pointer + "/state");
            else if (state is { Type: StateType.History or StateType.Choice })
                report.Add("MQ3048", $"State '{state.Name}' of lifecycle '{process.Name}' is a {(state.Type == StateType.History ? "history" : "choice")} state, which no part is ever in; name another state.",
                    pointer + "/state");
        }

        if (choose.LatestBy is not { } latestBy)
            return;
        var attribute = ExprSites.ProcessAttributesOf(model, part).FirstOrDefault(a => string.Equals(a.Id, latestBy, StringComparison.Ordinal));
        if (attribute is null)
            report.Add("MQ3048", $"Attribute '{latestBy}' is not one of the attributes of '{part.Name}', the '{end.Role}' part; name one of its attributes.", pointer + "/latestBy");
        else if (attribute.Collection || !ExprSites.TypeOf(model, attribute.Type).IsOrdered)
            report.Add("MQ3048", $"Attribute '{attribute.Name}' of '{part.Name}' has no order{(attribute.Collection ? " (it holds a list)" : "")}, so no part is the latest by it; name a number, a text, a date or a time.",
                pointer + "/latestBy");
    }

    // ---- helpers ----

    /// <summary>The key of an entity: its own, else the nearest base's.</summary>
    private static EntityKey? KeyOf(ModelSnapshot model, Entity entity)
    {
        foreach (var id in SubjectCoverage.Chain(model, entity))
        {
            if (model.Get<Entity>(id)?.Key is { } key)
                return key;
        }

        return null;
    }

    private static ProcessState? FindState(IReadOnlyList<ProcessState> states, string id)
    {
        foreach (var state in states)
        {
            if (string.Equals(state.Id, id, StringComparison.Ordinal))
                return state;
            if (FindState(state.States, id) is { } found)
                return found;
        }

        return null;
    }

    /// <summary>A type as messages say it: a built-in keyword, a scalar type by name with its base, or the element that types it.</summary>
    private static string TypeName(ModelSnapshot model, TypeRef type)
    {
        if (type.Builtin is { Length: > 0 } builtin)
            return builtin;
        if (type.Ref is { } id && model.Get<ScalarType>(id) is { } scalar)
            return $"by scalar type '{scalar.Name}' ({scalar.Base})";
        return type.Ref is { } other ? "by " + Loading.ChangePlanner.Describe(model, other) : "nothing";
    }

    private static string Name(Dictionary<string, ModelAttribute> held, string id) => held.TryGetValue(id, out var attribute) ? attribute.Name : id;

    /// <summary>
    /// The entities with a <c>copy</c> block whose findings a change of <paramref name="element"/> can move without the block referencing it: for
    /// a relation or an entity, the blocks whose parts (worked out on <paramref name="model"/>) reach it, as an entity on the way down or a base
    /// of one, or a composition of the tree; for a scalar type (the type of a key or of an <c>original</c> attribute), a process (which
    /// lifecycles govern a part), a stereotype or a package (which entities carry a stereotype a lifecycle names), every block. The store asks it
    /// of the model before a change too, so a delete or a change that takes something off the way down checks the blocks it reached.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="element">The changed element.</param>
    /// <returns>The ids of the entities, in model order.</returns>
    internal static IEnumerable<string> Peers(ModelSnapshot model, Element element)
    {
        if (element is not (Relation or Entity or ScalarType or Process or Stereotype or Package))
            yield break;
        foreach (var writer in model.All<Entity>())
        {
            if (writer.Copy is null || string.Equals(writer.Id, element.Id, StringComparison.Ordinal))
                continue;
            if (element is not (Relation or Entity) || Reaches(model, writer, element.Id))
                yield return writer.Id;
        }
    }

    /// <summary>Whether an entity's parts reach an element: an entity on the way down (its own chain or a part's), or a composition of the tree.</summary>
    private static bool Reaches(ModelSnapshot model, Entity writer, string id)
    {
        if (SubjectCoverage.Chain(model, writer).Contains(id, StringComparer.Ordinal))
            return true;
        foreach (var node in PartTree.Of(model, writer).Nodes)
        {
            if (string.Equals(node.Relation.Id, id, StringComparison.Ordinal) || string.Equals(node.EntityId, id, StringComparison.Ordinal)
                || (model.Get<Entity>(node.EntityId) is { } part && SubjectCoverage.Chain(model, part).Contains(id, StringComparer.Ordinal)))
                return true;
        }

        return false;
    }
}
