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
    public static JsonElement Row(ModelSnapshot model, Process process, IReadOnlyList<ScenarioObject> objects, string? subject) =>
        Row(model, process, objects, subject, out _);

    /// <summary>
    /// The subject's row as a run of a process sees it (<see cref="Row(ModelSnapshot, Process, IReadOnlyList{ScenarioObject}, string?)"/>),
    /// each object in it holding its states in the lifecycles an <c>in_state</c> of the process reads at its path (object-effects-design.md
    /// decision 10, <see cref="ObjectStates"/>); with no subject, a row of those states alone, the lifecycles' initial states. A process
    /// with no such read has the row it always had.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process the run is of.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <param name="notes">The notes of the states assumed, by <see cref="StateRead.Key"/>.</param>
    /// <returns>The row.</returns>
    internal static JsonElement Row(ModelSnapshot model, Process process, IReadOnlyList<ScenarioObject> objects, string? subject,
        out IReadOnlyDictionary<string, string> notes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(objects);
        var reads = ObjectStates.Reads(model, process);
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        notes = found;
        var root = subject is null ? null : objects.FirstOrDefault(o => string.Equals(o.Id, subject, StringComparison.Ordinal));
        if (root is null)
        {
            // No object: a lifecycle read on the subject itself reads its initial state (decision 10.3); deeper reads find no object.
            if (!reads.Any(r => r.Path.Count == 0))
                return NoObject;
            var entity = process.Subjects.Select(s => s.Entity).OfType<string>().Select(model.Get<Entity>).FirstOrDefault(e => e is not null);
            var row = new SortedDictionary<string, object>(StringComparer.Ordinal) { [ObjectStates.Member] = States(model, entity, null, [], reads, found) };
            return JsonSerializer.SerializeToElement(row);
        }

        return Row(model, objects, subject, ExprSites.SubjectRowDepth(model, process), ExprSites.StereotypeAttributesRead(model, process), reads, found);
    }

    // An object's states in each lifecycle read at its path that can govern it, by lifecycle id; each assumed state's note goes to notes.
    private static SortedDictionary<string, IReadOnlyList<string>> States(ModelSnapshot model, Entity? entity, ScenarioObject? obj, IReadOnlyList<string> path,
        IReadOnlyList<StateRead> reads, IDictionary<string, string> notes)
    {
        var states = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var read in reads)
        {
            if (!read.Path.SequenceEqual(path, StringComparer.Ordinal) || model.Get<Process>(read.Lifecycle) is not { } lifecycle
                || (entity is not null && !SubjectCoverage.Governs(model, lifecycle, entity)))
                continue;
            var (active, note) = ObjectStates.Of(model, lifecycle, entity, obj);
            states[lifecycle.Id] = active;
            if (note is not null)
                notes[read.Key] = note;
        }

        return states;
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
        IReadOnlySet<string>? stereotypeAttributes = null) => Row(model, objects, subject, depth, stereotypeAttributes, [], null);

    /// <summary>
    /// The subject's row (<see cref="Row(ModelSnapshot, IReadOnlyList{ScenarioObject}, string?, int, IReadOnlySet{string}?)"/>), each object in
    /// it holding its states in the lifecycles that <paramref name="reads"/> read at its path: the row a right's condition reads
    /// (rights-design.md decision 6).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="objects">The objects.</param>
    /// <param name="subject">The id of the subject among them.</param>
    /// <param name="depth">How many links deep the row nests.</param>
    /// <param name="stereotypeAttributes">The stereotype attributes read on the object.</param>
    /// <param name="reads">The lifecycles an <c>in_state</c> reads, by path.</param>
    /// <returns>The row.</returns>
    internal static JsonElement RowWithStates(ModelSnapshot model, IReadOnlyList<ScenarioObject> objects, string? subject, int depth,
        IReadOnlySet<string>? stereotypeAttributes, IReadOnlyList<StateRead> reads) => Row(model, objects, subject, depth, stereotypeAttributes, reads, null);

    private static JsonElement Row(ModelSnapshot model, IReadOnlyList<ScenarioObject> objects, string? subject, int depth,
        IReadOnlySet<string>? stereotypeAttributes, IReadOnlyList<StateRead> reads, IDictionary<string, string>? notes)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(objects);
        if (subject is null || objects.Count == 0)
            return NoObject;
        var byId = new Dictionary<string, ScenarioObject>(StringComparer.Ordinal);
        foreach (var obj in objects)
            byId.TryAdd(obj.Id, obj);
        return byId.TryGetValue(subject, out var root)
            ? RowOf(model, root, byId, new HashSet<string>(StringComparer.Ordinal), depth, stereotypeAttributes, reads, [], notes)
            : NoObject;
    }

    /// <summary>
    /// Why a run's objects are bad, with the pointer of the first problem, or <see langword="null"/>: a value an object gives that is not
    /// an attribute of its entity or its bases, or of a stereotype they carry (stereotype-sets-design.md decision 7), or that does not fit
    /// its attribute; a frozen link of an object other than the subject; a link through an end that does not lead from the
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
                // Only the subject's links freeze until scenarios span objects (rights-design.md section 5).
                if (link.Frozen && !string.Equals(obj.Id, subject, StringComparison.Ordinal))
                {
                    return ($"Object '{obj.Name}' has a frozen link, but only the subject's links can be frozen in a scenario; remove frozen, or link from the subject.",
                        pointer + "/links/" + k.ToString(CultureInfo.InvariantCulture) + "/frozen");
                }

                if (!model.TryGetEntry(link.End, out var entry) || model.Get<Relation>(entry.OwnerId) is not { } relation
                    || relation.Ends.FirstOrDefault(e => string.Equals(e.Id, link.End, StringComparison.Ordinal)) is not { } end)
                    continue;
                // several-types: wave 2 — a link through a relation with an end of several types fits by the end coverage helper (decision 18,
                // MQ9305's new text); not checked yet.
                if (EndCoverage.HasSeveralTypes(relation))
                    continue;
                var target = byId.GetValueOrDefault(link.Object);
                var targetEntity = target is null ? null : model.Get<Entity>(target.Entity);
                var from = relation.Ends.Any(e => !string.Equals(e.Id, end.Id, StringComparison.Ordinal) && e.SingleEntity is { } near && chain.Contains(near));
                if (!from || (targetEntity is not null && (end.SingleEntity is not { } far || !Chain(model, targetEntity).Contains(far))))
                {
                    var endName = relation.Name + "." + (string.IsNullOrEmpty(end.Role) ? end.Id : end.Role);
                    return (from
                        ? $"Object '{obj.Name}' links to '{target!.Name}' through end '{endName}', which leads to entity '{model.Get<Entity>(end.SingleEntity!)?.Name ?? end.SingleEntity}', not to entity '{targetEntity!.Name}' or one of its bases; link to an object of that entity, or through an end that leads to '{targetEntity.Name}'."
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

        // A state that disagrees with the bound value the object gives: the state the value maps to must be active in what the states give.
        foreach (var (lifecycleId, paths) in given)
        {
            if (model.Get<Process>(lifecycleId) is not { } lifecycle || ObjectStates.BoundAttribute(model, lifecycle, entity) is not { } bound
                || !obj.Values.TryGetValue(bound.Id, out var value) || ObjectStates.Mapped(model, lifecycle, bound, value) is not { } mapped)
                continue;
            var active = ObjectStates.Configuration(ProcessLibraries.Chart(model, lifecycle), paths.Select(p => p[^1].Id));
            if (!active.Contains(mapped.Id, StringComparer.Ordinal))
            {
                var at = pointer + "/states/" + obj.States.ToList().FindIndex(id => paths.Any(p => string.Equals(p[^1].Id, id, StringComparison.Ordinal))).ToString(CultureInfo.InvariantCulture);
                return ($"Object '{obj.Name}' gives state '{paths[0][^1].Name}' of '{lifecycle.Name}', but its '{bound.Name}' {value.GetRawText()} maps to state '{mapped.Name}'; give the state its value maps to, or leave one of them out.", at);
            }
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
        IReadOnlySet<string>? stereotypeAttributes, IReadOnlyList<StateRead> reads, List<string> navigation, IDictionary<string, string>? notes)
    {
        path.Add(obj.Id);
        var row = new SortedDictionary<string, object>(StringComparer.Ordinal);
        var objectEntity = model.Get<Entity>(obj.Entity);
        // The object's states in the lifecycles an in_state reads at its path (object-effects-design.md section 7); none without such a read.
        if (reads.Any(r => r.Path.SequenceEqual(navigation, StringComparer.Ordinal)))
            row[ObjectStates.Member] = States(model, objectEntity, obj, navigation, reads, notes ?? new Dictionary<string, string>(StringComparer.Ordinal));
        if (objectEntity is { } entity)
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
            navigation.Add(end.Navigation);
            row[end.Navigation] = RowOf(model, target, byId, path, depth - 1, null, reads, navigation, notes);
            navigation.RemoveAt(navigation.Count - 1);
        }

        path.Remove(obj.Id);
        return JsonSerializer.SerializeToElement(row);
    }

    private static RelationEnd? End(ModelSnapshot model, string endId) =>
        model.TryGetEntry(endId, out var entry) && model.Get<Relation>(entry.OwnerId) is { } relation
            ? relation.Ends.FirstOrDefault(e => string.Equals(e.Id, endId, StringComparison.Ordinal))
            : null;
}
