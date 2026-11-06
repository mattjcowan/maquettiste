using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// A scenario's or a simulation's objects as the interpreter sees them (lifecycle-design.md decision 22): the subject's row, and the
/// checks of the values its objects give.
/// </summary>
internal static class ScenarioObjects
{
    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();
    private static readonly JsonElement NoObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>
    /// The subject's row as a run of a process sees it: <see cref="Row(ModelSnapshot, IReadOnlyList{ScenarioObject}, string?, int, IReadOnlySet{string}?)"/>
    /// nested as deep as the longest path under <c>subject</c> any guard condition of the process, or of a process it invokes, reads
    /// (<see cref="ExprSites.SubjectRowDepth"/>), so a row holds no links when no condition reads the object.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process the run is of.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <returns>The row.</returns>
    public static JsonElement Row(ModelSnapshot model, Process process, IReadOnlyList<ScenarioObject> objects, string? subject)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(objects);
        if (subject is null || objects.Count == 0)
            return NoObject;
        return Row(model, objects, subject, ExprSites.SubjectRowDepth(model, process), ExprSites.StereotypeAttributesRead(model, process));
    }

    /// <summary>
    /// The subject's row: the attributes of its entity and its bases by name (the most derived declaration of a name wins), each the value
    /// given, else the declaring attribute's default, else null; then, while <paramref name="depth"/> allows, each link through a to-one
    /// end nested by the end's navigation name (the first link wins). A link through a to-many end, which no condition can read, or whose
    /// end has no navigation, whose object is not one of the objects, or whose object is already on the path is left out. An empty object
    /// when there is no subject.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <param name="depth">How many links deep the row nests: 0 for the subject's attributes alone.</param>
    /// <param name="stereotypeAttributes">The ids of the stereotype attributes a guard reads on the object
    /// (<see cref="ExprSites.StereotypeAttributesRead"/>): the subject's own row gains those its entity holds, after its entity's attributes
    /// (stereotype-sets-design.md section 6); <see langword="null"/> or empty for none.</param>
    /// <returns>The row.</returns>
    internal static JsonElement Row(ModelSnapshot model, IReadOnlyList<ScenarioObject> objects, string? subject, int depth,
        IReadOnlySet<string>? stereotypeAttributes = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(objects);
        if (subject is null || objects.Count == 0)
            return NoObject;
        var byId = new Dictionary<string, ScenarioObject>(StringComparer.Ordinal);
        foreach (var obj in objects)
            byId.TryAdd(obj.Id, obj);
        return byId.TryGetValue(subject, out var root) ? RowOf(model, root, byId, new HashSet<string>(StringComparer.Ordinal), depth, stereotypeAttributes) : NoObject;
    }

    /// <summary>
    /// Why a run's objects are bad, with the pointer of the first problem, or <see langword="null"/>: a value an object gives that is not
    /// an attribute of its entity or its bases, or of a stereotype they carry (stereotype-sets-design.md decision 7), or that does not fit
    /// its attribute; a link through an end that does not lead from the
    /// object's entity (or a base) to the linked object's entity (or a base); or a subject whose entity the process does not govern (it, a
    /// base, or a stereotype it or a base carries is not listed; <see cref="SubjectCoverage.Governs"/>). An object, an end or a subject that is not in the model is left to MQ2001.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process the run is of.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <param name="fits">Why a value does not fit an attribute, or <see langword="null"/>.</param>
    /// <returns>The first problem: its message and its pointer.</returns>
    public static (string Message, string Pointer)? Check(ModelSnapshot model, Process process, IReadOnlyList<ScenarioObject> objects, string? subject,
        Func<ModelAttribute, JsonElement, string?> fits)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(objects);
        ArgumentNullException.ThrowIfNull(fits);
        var byId = new Dictionary<string, ScenarioObject>(StringComparer.Ordinal);
        foreach (var obj in objects)
            byId.TryAdd(obj.Id, obj);
        for (var i = 0; i < objects.Count; i++)
        {
            var obj = objects[i];
            if (model.Get<Entity>(obj.Entity) is not { } entity)
                continue;
            var attributes = new Dictionary<string, ModelAttribute>(StringComparer.Ordinal);
            foreach (var attribute in ExprSites.ProcessAttributesOf(model, entity))
                attributes.TryAdd(attribute.Id, attribute);
            var pointer = "/objects/" + i.ToString(CultureInfo.InvariantCulture);
            foreach (var (id, value) in obj.Values.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!attributes.TryGetValue(id, out var attribute))
                    return ($"Object '{obj.Name}' gives a value for '{id}', which is not an attribute of entity '{entity.Name}' or its bases; remove it or name one of the entity's attributes.", pointer + "/values");
                if (fits(attribute, value) is { } why)
                    return ($"Object '{obj.Name}': the value of '{attribute.Name}' does not fit its type ({why}); give a value of the attribute's type.", pointer + "/values");
            }

            var chain = Chain(model, entity);
            for (var k = 0; k < obj.Links.Count; k++)
            {
                var link = obj.Links[k];
                if (!model.TryGetEntry(link.End, out var entry) || model.Get<Relation>(entry.OwnerId) is not { } relation
                    || relation.Ends.FirstOrDefault(e => string.Equals(e.Id, link.End, StringComparison.Ordinal)) is not { } end)
                    continue;
                var target = byId.GetValueOrDefault(link.Object);
                var targetEntity = target is null ? null : model.Get<Entity>(target.Entity);
                var from = relation.Ends.Any(e => !string.Equals(e.Id, end.Id, StringComparison.Ordinal) && chain.Contains(e.Entity));
                if (!from || (targetEntity is not null && !Chain(model, targetEntity).Contains(end.Entity)))
                {
                    var endName = relation.Name + "." + (string.IsNullOrEmpty(end.Role) ? end.Id : end.Role);
                    return (from
                        ? $"Object '{obj.Name}' links to '{target!.Name}' through end '{endName}', which leads to entity '{model.Get<Entity>(end.Entity)?.Name ?? end.Entity}', not to entity '{targetEntity!.Name}' or one of its bases; link to an object of that entity, or through an end that leads to '{targetEntity.Name}'."
                        : $"Object '{obj.Name}' links through end '{endName}', which does not lead from entity '{entity.Name}' or one of its bases; use an end of a relation of '{entity.Name}'.",
                        pointer + "/links/" + k.ToString(CultureInfo.InvariantCulture) + "/end");
                }
            }

            if (obj.States.Count > 0 && StatesProblem(model, process, obj, entity, subject, pointer) is { } statesProblem)
                return statesProblem;
        }

        if (subject is not null && byId.GetValueOrDefault(subject) is { } root && model.Get<Entity>(root.Entity) is { } rootEntity
            && !SubjectCoverage.Governs(model, process, rootEntity))
        {
            var names = process.Subjects.Select(x => x.Stereotype is { } key ? "stereotype '" + key + "'" : model.Get<Entity>(x.Entity!)?.Name ?? x.Entity!)
                .Distinct(StringComparer.Ordinal).ToList();
            return (names.Count == 0
                ? $"The subject '{root.Name}' is an object of entity '{rootEntity.Name}', but process '{process.Name}' has no subject; leave subject and objects out, or give the process a subject."
                : $"The subject '{root.Name}' is an object of entity '{rootEntity.Name}', which is not a subject of process '{process.Name}' ({string.Join(", ", names)}) or derived from one; make an object of a subject's entity the subject.",
                "/subject");
        }

        return null;
    }

    // An object's states (object-effects-design.md decision 10, MQ9305): each a state an object can be in, of a lifecycle that governs the
    // object's entity, never the state of the process the run is of on its subject, and states of one lifecycle that can be active together
    // (one inside the other, or in different regions of a parallel state). An id that is no state is left to MQ2001.
    private static (string Message, string Pointer)? StatesProblem(ModelSnapshot model, Process process, ScenarioObject obj, Entity entity, string? subject,
        string pointer)
    {
        var given = new Dictionary<string, List<IReadOnlyList<ProcessState>>>(StringComparer.Ordinal);
        for (var k = 0; k < obj.States.Count; k++)
        {
            var at = pointer + "/states/" + k.ToString(CultureInfo.InvariantCulture);
            if (!model.TryGetEntry(obj.States[k], out var entry) || entry.Kind != "state" || model.Get<Process>(entry.OwnerId) is not { } lifecycle
                || PathTo(lifecycle.States, obj.States[k]) is not { } path)
                continue;
            var state = path[^1];
            if (string.Equals(obj.Id, subject, StringComparison.Ordinal) && string.Equals(lifecycle.Id, process.Id, StringComparison.Ordinal))
                return ($"Object '{obj.Name}' is the subject, whose state in process '{process.Name}' the run works out; remove '{state.Name}' from its states.", at);
            if (state.Type is StateType.History or StateType.Choice)
                return ($"Object '{obj.Name}' gives state '{state.Name}' of '{lifecycle.Name}', a history or choice state, which no object is in; give a state.", at);
            if (!SubjectCoverage.Governs(model, lifecycle, entity))
                return ($"Object '{obj.Name}' gives state '{state.Name}' of '{lifecycle.Name}', which does not govern entity '{entity.Name}'; give a state of a lifecycle of the entity.", at);
            if (!given.TryGetValue(lifecycle.Id, out var others))
                given[lifecycle.Id] = others = [];
            foreach (var other in others)
            {
                if (!Together(path, other))
                    return ($"Object '{obj.Name}' gives states '{other[^1].Name}' and '{state.Name}' of '{lifecycle.Name}', which cannot be active together; give one state per lifecycle, or one per region of a parallel state.", at);
            }

            others.Add(path);
        }

        return null;

        static bool Together(IReadOnlyList<ProcessState> a, IReadOnlyList<ProcessState> b)
        {
            var i = 0;
            while (i < a.Count && i < b.Count && ReferenceEquals(a[i], b[i]))
                i++;
            return i == a.Count || i == b.Count || (i > 0 && a[i - 1].Type == StateType.Parallel);
        }
    }

    // The states from the root's child down to the state with an id, or null.
    private static List<ProcessState>? PathTo(IReadOnlyList<ProcessState> level, string id)
    {
        foreach (var state in level)
        {
            if (string.Equals(state.Id, id, StringComparison.Ordinal))
                return [state];
            if (PathTo(state.States, id) is { } below)
            {
                below.Insert(0, state);
                return below;
            }
        }

        return null;
    }

    // The ids of an entity and its bases.
    private static HashSet<string> Chain(ModelSnapshot model, Entity entity)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && chain.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
        }

        return chain;
    }

    /// <summary>The attributes of an entity and its bases, most derived first, each base once.</summary>
    /// <param name="model">The model.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The attributes.</returns>
    internal static IEnumerable<ModelAttribute> Attributes(ModelSnapshot model, Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && seen.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
            foreach (var attribute in current.Attributes)
                yield return attribute;
        }
    }

    private static JsonElement RowOf(ModelSnapshot model, ScenarioObject obj, Dictionary<string, ScenarioObject> byId, HashSet<string> path, int depth,
        IReadOnlySet<string>? stereotypeAttributes = null)
    {
        path.Add(obj.Id);
        var row = new SortedDictionary<string, object>(StringComparer.Ordinal);
        if (model.Get<Entity>(obj.Entity) is { } entity)
        {
            foreach (var attribute in Attributes(model, entity))
                row.TryAdd(attribute.Name, obj.Values.TryGetValue(attribute.Id, out var given) ? given : attribute.Default ?? Null);
            if (stereotypeAttributes is { Count: > 0 })
            {
                // Only the object itself offers a stereotype's attributes, and only those a guard reads (stereotype-sets-design.md section 6).
                foreach (var attribute in ExprSites.ProcessAttributesOf(model, entity))
                {
                    if (stereotypeAttributes.Contains(attribute.Id))
                        row.TryAdd(attribute.Name, obj.Values.TryGetValue(attribute.Id, out var given) ? given : attribute.Default ?? Null);
                }
            }
        }

        foreach (var link in depth > 0 ? obj.Links : [])
        {
            if (End(model, link.End) is not { Navigation.Length: > 0, Max: MaxCardinality.One } end || row.ContainsKey(end.Navigation)
                || !byId.TryGetValue(link.Object, out var target) || path.Contains(target.Id))
                continue;
            row[end.Navigation] = RowOf(model, target, byId, path, depth - 1);
        }

        path.Remove(obj.Id);
        return JsonSerializer.SerializeToElement(row);
    }

    private static RelationEnd? End(ModelSnapshot model, string endId) =>
        model.TryGetEntry(endId, out var entry) && model.Get<Relation>(entry.OwnerId) is { } relation
            ? relation.Ends.FirstOrDefault(e => string.Equals(e.Id, endId, StringComparison.Ordinal))
            : null;
}
