using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>One step of an actor's path once worked out: a relation end followed, or the attribute it ends at.</summary>
/// <param name="Id">The id the file names.</param>
/// <param name="End">The to-one relation end, or <see langword="null"/>.</param>
/// <param name="Attribute">The attribute the path ends at (the declaring attribute, a base's for a shared one), or <see langword="null"/>.</param>
internal sealed record RelativeStep(string Id, RelationEnd? End, ModelAttribute? Attribute);

/// <summary>What an actor from the object reaches on one object (<see cref="RelativeActors.Reach"/>).</summary>
/// <param name="Value">The value of the attribute the owner field or the path ends at (the value given, else the attribute's default, else
/// null), or <see langword="null"/> when it ends at an object.</param>
/// <param name="Object">The id of the scenario object the owner end, the path or the context reaches, or <see langword="null"/>.</param>
internal sealed record RelativeTarget(JsonElement? Value, string? Object);

/// <summary>
/// Actors worked out from the object (actors-design.md decisions 8 to 13): how their paths resolve on a process's subjects (decision 11,
/// lifecycle-design.md decision 20) and what they reach on a scenario's objects. Paths follow the relation ends a guard can navigate
/// (<see cref="ExprSites.NavigationsOf"/>), by id.
/// </summary>
internal static class RelativeActors
{
    /// <summary>
    /// The steps of an actor's path (<see cref="ActorRelative.Steps"/>) on a process's subjects, or why they cannot be worked out (MQ9604):
    /// the process has no subjects; a step is not held under one id by every subject (their own and inherited members, a shared base's for
    /// several); a step before the last, or any <c>within</c> step, is not a to-one relation end; or the path ends at a many-valued
    /// attribute, a value object or a to-many end. An id the model does not hold, a subject whose entity is missing, or a relative with no
    /// form or several (MQ2001, MQ9603) gives no reason and no steps.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that uses the actor.</param>
    /// <param name="relative">The actor's relative.</param>
    /// <returns>The steps, or the reason (a sentence part after the actor's name).</returns>
    public static (IReadOnlyList<RelativeStep> Steps, string? Why) Resolve(ModelSnapshot model, Process process, ActorRelative relative)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(relative);
        if (relative.Forms.Count != 1)
            return ([], null);
        if (process.Subjects.Count == 0)
            return ([], $"is worked out from the object, but process '{process.Name}' has no subjects");
        var current = process.Subjects.Select(s => model.Get<Entity>(s.Entity)).OfType<Entity>().DistinctBy(e => e.Id).ToList();
        if (current.Count < process.Subjects.Select(s => s.Entity).Distinct(StringComparer.Ordinal).Count())
            return ([], null);
        var endsOnly = relative.Form == "members-of";
        var steps = relative.Steps;
        var resolved = new List<RelativeStep>();
        for (var k = 0; k < steps.Count; k++)
        {
            var id = steps[k];
            var last = k == steps.Count - 1;
            if (!model.TryGetEntry(id, out _))
                return ([], null);
            var ends = current.Select(e => ExprSites.NavigationsOf(model, e).Select(n => n.To).FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal))).ToList();
            if (ends.All(e => e is not null))
            {
                var end = ends[0]!;
                if (end.Max != MaxCardinality.One)
                    return ([], $"follows end '{EndName(model, end)}', which leads to many objects; a path follows only to-one ends");
                resolved.Add(new RelativeStep(id, end, null));
                if (model.Get<Entity>(end.Entity) is not { } next)
                    return ([], null);
                current = [next];
                continue;
            }

            var attributes = current.Select(e => ExprSites.AttributesOf(model, e).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))).ToList();
            if (attributes.All(a => a is not null))
            {
                var attribute = attributes[0]!;
                if (endsOnly)
                    return ([], $"names attribute '{attribute.Name}' in within, which leads to its context only through to-one relation ends");
                if (!last)
                    return ([], $"goes on after attribute '{attribute.Name}'; a path goes on only through to-one relation ends");
                if (attribute.Collection)
                    return ([], $"ends at attribute '{attribute.Name}', which holds many values; end at a single-valued attribute or a to-one end");
                if (attribute.Type.Ref is { } typeId && model.Get<ValueObject>(typeId) is { } valueObject)
                    return ([], $"ends at attribute '{attribute.Name}', which holds value object '{valueObject.Name}'; end at a single-valued attribute or a to-one end");
                resolved.Add(new RelativeStep(id, null, attribute));
                continue;
            }

            var names = string.Join(", ", current.Select(e => "'" + e.Name + "'"));
            return ([], current.Count == 1
                ? $"names '{IdName(model, id)}', which is not an attribute or a navigable relation end of entity {names} or its bases"
                : $"names '{IdName(model, id)}', which entities {names} do not all hold under one id (a shared base's attribute or end)");
        }

        return (resolved, null);
    }

    /// <summary>
    /// Whether a subject entry's <c>owner</c> names a to-one relation end the entity (or a base) navigates to (decision 10); attributes are
    /// judged on the flattened attributes by the rule (MQ9606).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="entity">The subject's entity.</param>
    /// <param name="id">The owner id.</param>
    /// <returns>The end, when the entity navigates to it; whether it is to-one is the caller's.</returns>
    public static RelationEnd? OwnerEnd(ModelSnapshot model, Entity entity, string id) =>
        ExprSites.NavigationsOf(model, entity).Select(n => n.To).FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal));

    /// <summary>
    /// What an actor from the object reaches on a scenario's subject object, for the participants that satisfy it (decision 17): for the
    /// owner, the value of the subject entry's owner attribute or the object its owner end links to; for a path, the value it ends at or
    /// the object it reaches; for members-of, the context object (the subject itself for an empty <c>within</c>). Links are followed by
    /// end id, the first link of an end winning, as the subject's row takes them. <see langword="null"/> when the owner is held by the
    /// team's system (no owner field), when a link is missing, or when the path cannot be worked out (<see cref="Resolve"/>).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that uses the actor.</param>
    /// <param name="objects">The scenario's objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <param name="actor">The actor from the object.</param>
    /// <returns>What it reaches, or <see langword="null"/>.</returns>
    public static RelativeTarget? Reach(ModelSnapshot model, Process process, IReadOnlyList<ScenarioObject> objects, string? subject, Actor actor)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(actor);
        if (actor.Relative is not { } relative || subject is null)
            return null;
        var byId = new Dictionary<string, ScenarioObject>(StringComparer.Ordinal);
        foreach (var obj in objects)
            byId.TryAdd(obj.Id, obj);
        if (!byId.TryGetValue(subject, out var root) || model.Get<Entity>(root.Entity) is not { } rootEntity)
            return null;
        if (relative.Form == "owner")
        {
            if (EntryOf(model, process, rootEntity)?.Owner is not { } owner)
                return null;
            if (ExprSites.AttributesOf(model, rootEntity).FirstOrDefault(a => string.Equals(a.Id, owner, StringComparison.Ordinal)) is { } attribute)
                return new RelativeTarget(ValueOf(root, attribute), null);
            return Linked(root, owner, byId) is { } linked ? new RelativeTarget(null, linked.Id) : null;
        }

        var (steps, why) = Resolve(model, process, relative);
        if (why is not null || (steps.Count == 0 && relative.Form != "members-of") || steps.Count != relative.Steps.Count)
            return null;
        var current = root;
        foreach (var step in steps)
        {
            if (step.Attribute is { } attribute)
                return new RelativeTarget(ValueOf(current, attribute), null);
            if (Linked(current, step.Id, byId) is not { } next)
                return null;
            current = next;
        }

        return new RelativeTarget(null, current.Id);
    }

    /// <summary>The subject entry of a process that governs an entity: its own, else its nearest base's; <see langword="null"/> when none
    /// does.</summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The entry.</returns>
    public static ProcessSubject? EntryOf(ModelSnapshot model, Process process, Entity entity)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(entity);
        var chain = Chain(model, entity);
        return process.Subjects.Where(s => chain.Contains(s.Entity)).OrderBy(s => chain.IndexOf(s.Entity)).FirstOrDefault();
    }

    private static JsonElement ValueOf(ScenarioObject obj, ModelAttribute attribute) =>
        obj.Values.TryGetValue(attribute.Id, out var given) ? given : attribute.Default ?? JsonDocument.Parse("null").RootElement.Clone();

    private static ScenarioObject? Linked(ScenarioObject obj, string end, Dictionary<string, ScenarioObject> byId) =>
        obj.Links.FirstOrDefault(l => string.Equals(l.End, end, StringComparison.Ordinal)) is { } link ? byId.GetValueOrDefault(link.Object) : null;

    // The ids of an entity and its bases, nearest first.
    private static List<string> Chain(ModelSnapshot model, Entity entity)
    {
        var chain = new List<string>();
        for (var current = entity; current is not null && !chain.Contains(current.Id, StringComparer.Ordinal); current = current.Base is { } b ? model.Get<Entity>(b) : null)
            chain.Add(current.Id);
        return chain;
    }

    private static string EndName(ModelSnapshot model, RelationEnd end) =>
        end.Navigation.Length > 0 ? end.Navigation : !string.IsNullOrEmpty(end.Role) ? end.Role : end.Id;

    private static string IdName(ModelSnapshot model, string id)
    {
        if (!model.TryGetEntry(id, out var entry))
            return id;
        if (model.Get<Entity>(entry.OwnerId) is { } entity && entity.Attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)) is { } attribute)
            return entity.Name + "." + attribute.Name;
        if (model.Get<Relation>(entry.OwnerId) is { } relation && relation.Ends.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is { } end)
            return relation.Name + "." + EndName(model, end);
        return id;
    }
}
