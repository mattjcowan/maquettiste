using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// Deletes that resolve references (engine-design.md section 15.1): <c>remove-references</c> clears each reference whose referrer
/// stays valid without it and refuses the others; <c>delete-dependents</c> also resolves those. A required reference inside a
/// sub-element removes the smallest enclosing part of the referrer that leaves it valid (the reference's parent object, then its
/// parent's, outwards: an attribute whose type is gone, an end, a key, a foreign key); when only the whole referrer would do (a table
/// whose database is gone, a mapping whose entity is gone, a relation that loses an end), the referrer is deleted with its own
/// dependents, recursively. Everything is planned against the files as the index read them, for every delete of the batch, and
/// staged at once when the batch's operations have all run; the result is one change.
/// </summary>
internal sealed partial class ChangePlanner
{
    private readonly Dictionary<string, (int Index, string? Because)> _cascadeDeleted = new(StringComparer.Ordinal);
    private readonly List<string> _cascadeOrder = [];
    private readonly Dictionary<string, List<CascadeOp>> _cascadeOps = new(StringComparer.Ordinal);
    private readonly Queue<(int Index, string Target, DeleteResolution Mode)> _cascadeQueue = new();
    private readonly List<(int Index, string ElementId, ReferenceInfo Reference, string Why)> _cascadeRefused = [];
    private readonly Dictionary<string, bool> _cascadeReadable = new(StringComparer.Ordinal);
    private readonly List<DeletePlanSetting> _settingsRemovals = [];
    private readonly Dictionary<int, DeleteResolution> _cascadeModes = [];
    private readonly List<(int Index, PackParameterRef Value)> _packRemovals = [];
    private readonly List<DeletePlanRefusal> _packRefusals = [];
    private readonly HashSet<string> _packHandled = new(StringComparer.Ordinal);
    private readonly HashSet<string> _cascadeMembers = new(StringComparer.Ordinal);
    private bool _cascadePacksPlanned;
    private ProjectSettings? _candidateSettings;
    private string? _candidateSettingsHash;

    /// <summary>What removes a pack parameter value (next-phase-1-design.md section 4.1, "Pack parameters").</summary>
    private enum PackValueMode { Refuse, RemoveReferences, DeleteDependents, Save }

    /// <summary>One planned edit of a surviving referrer: a reference cleared, or a part of the referrer removed.</summary>
    /// <param name="Pointer">The reference (clear) or the part removed (remove), in the file as the index read it.</param>
    /// <param name="Remove">Whether the part at <paramref name="Pointer"/> is removed rather than the reference cleared.</param>
    /// <param name="Reference">The reference that caused it.</param>
    /// <param name="Index">The change it belongs to.</param>
    private sealed record CascadeOp(string Pointer, bool Remove, ReferenceInfo Reference, int Index);

    /// <summary>What the cascade of the planned deletes does, for the delete plan read (null when no delete resolved references).</summary>
    public DeletePlanReport? CascadeReport { get; private set; }

    private void CascadeDelete(int index, string id, DeleteResolution mode)
    {
        _cascadeModes[index] = mode;
        MarkCascadeDeleted(index, id, null, mode);
        while (_cascadeQueue.Count > 0)
        {
            var (at, target, how) = _cascadeQueue.Dequeue();
            foreach (var reference in _snapshot.ReferencesTo(target)
                         .OrderBy(r => r.FromElementId, StringComparer.Ordinal)
                         .ThenBy(r => r.JsonPointer, PointerOrder.Instance))
                Resolve(at, target, reference, how);
        }
    }

    private void MarkCascadeDeleted(int index, string id, string? because, DeleteResolution mode)
    {
        if (_cascadeDeleted.ContainsKey(id) || !TryGetCurrent(id, null, out var current))
            return;
        _cascadeDeleted[id] = (index, because);
        _cascadeOrder.Add(id);
        _cascadeOps.Remove(id);
        // A deleted stereotype's members are named by keyed id where tables inherit them (next-phase-2-design.md section 3.1).
        if (current.Element is Stereotype stereotype)
            _cascadeMembers.UnionWith(MemberIds(stereotype).Where(m => !string.IsNullOrEmpty(m)));
        _cascadeQueue.Enqueue((index, id, mode));
        foreach (var sub in SubElementIds(id, current.Json))
            _cascadeQueue.Enqueue((index, sub, mode));
    }

    private void Resolve(int index, string target, ReferenceInfo reference, DeleteResolution mode)
    {
        var referrerId = reference.FromElementId;
        if (_cascadeDeleted.ContainsKey(referrerId) || _deleted.Contains(referrerId))
            return;
        // An element the batch names itself is left to its own operation; the candidate check judges what it still references.
        if (_batchIds.Contains(referrerId))
            return;
        // Seed columns naming a removed attribute or end are dropped with their cells once the plan is staged (section 2.7).
        if (reference.Field == "columns" && _snapshot.Get<Seed>(referrerId) is not null)
            return;
        var ops = _cascadeOps.GetValueOrDefault(referrerId) ?? [];
        if (ops.Any(o => o.Remove && (reference.JsonPointer == o.Pointer || IsUnder(reference.JsonPointer, o.Pointer))))
            return; // inside a part already removed
        if (ops.Any(o => !o.Remove && o.Pointer == reference.JsonPointer))
            return;
        if (reference.Owning)
        {
            MarkCascadeDeleted(index, referrerId, "belongs to " + Label(target), mode);
            return;
        }

        if (!TryGetCurrent(referrerId, null, out var referrer))
            return;
        if (!Readable(referrerId, referrer))
        {
            _cascadeRefused.Add((index, referrerId, reference, "its file is not valid, so it cannot be changed; fix it first."));
            return;
        }

        var narrow = mode == DeleteResolution.DeleteDependents;

        // A stereotype parameter's x-ref of (section 4.2) is never optional: without it the parameter would take the
        // members of any element, so remove-references refuses. delete-dependents removes the of key through the clear
        // below, which the plan lists.
        if (mode == DeleteResolution.RemoveReferences && reference.Field == "of" && referrer.Element is Stereotype
            && reference.JsonPointer.StartsWith("/parameters/", StringComparison.Ordinal))
        {
            var parameter = JsonPointer.TryParse(reference.JsonPointer, out var at) && at.Length > 2 ? at[2] : reference.JsonPointer;
            _cascadeRefused.Add((index, referrerId, reference,
                $"its parameter '{parameter}' takes its values from it (x-ref of), and without of it would take them from any element, so "
                + "remove-references cannot clear it."));
            return;
        }

        // A value an x-ref types in custom properties (section 4.1) is cleared, never its holder: an optional one in both resolutions, a
        // required one only by delete-dependents; either refuses when a schema declaring the property would then fail.
        if (reference.Schema is not null)
        {
            ResolveXRef(index, referrerId, referrer, reference, ops, mode);
            return;
        }

        // Links to a member of a deleted stereotype (next-phase-2-design.md section 3.1): a table's redefinition of it goes whole and an
        // exclusion of it is cleared, in both resolutions; any other keyed use of an inherited copy (an index, a key, a binding field, a
        // query) refuses remove-references, and delete-dependents removes it as it removes a deleted column's uses.
        if (_cascadeMembers.Contains(target))
        {
            if (referrer.Element is Table && reference.Field == "member" && RedefinesEntry(reference.JsonPointer) is { } entry)
            {
                List<CascadeOp> next = [.. ops.Where(o => o.Pointer != entry && !IsUnder(o.Pointer, entry)), new CascadeOp(entry, true, reference, index)];
                if (TryOps(referrer, next, narrow, out _) is null)
                {
                    Commit(referrerId, next);
                    return;
                }
            }
            else if (mode == DeleteResolution.RemoveReferences && !IsMemberLink(reference))
            {
                _cascadeRefused.Add((index, referrerId, reference,
                    $"it names {Label(target)} where a table inherits it (a keyed id), and remove-references does not clear such a use; delete with delete-dependents to remove it."));
                return;
            }
        }

        // A reference from an entity's binding (erratum E43) or a reference type's (2026-10-11) never deletes its owner, in either
        // resolution: the binding goes (a field, a constant or a listed column only that entry), and the owner stays with its other bindings.
        if (referrer.Element is Entity or ReferenceType && BindingPart(reference.JsonPointer) is { } bindingPart)
        {
            var drop = new CascadeOp(bindingPart, true, reference, index);
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != bindingPart && !IsUnder(o.Pointer, bindingPart)), drop];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == bindingPart || IsUnder(pointer, bindingPart)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        var clear = new CascadeOp(reference.JsonPointer, false, reference, index);
        var reason = TryOps(referrer, [.. ops, clear], narrow, out var widening);
        if (reason is null)
        {
            Commit(referrerId, [.. ops, clear]);
            return;
        }

        if (mode != DeleteResolution.DeleteDependents)
        {
            _cascadeRefused.Add((index, referrerId, reference, reason));
            return;
        }

        // A query whose required reference goes (a source, a column, a selected attribute) is deleted whole: its aliases tie its parts
        // together, so dropping a join or a field would leave references to it (engine-design.md section 7, "Queries").
        if (referrer.Element is Query)
        {
            MarkCascadeDeleted(index, referrerId, "needs " + Label(target), mode);
            return;
        }

        // Remove the smallest enclosing part that leaves the referrer valid; the list itself is skipped when emptying it would widen it.
        if (JsonPointer.TryParse(reference.JsonPointer, out var segments))
        {
            for (var k = segments.Length - (widening ? 2 : 1); k >= 1; k--)
            {
                var part = JsonPointer.Build(segments[..k]);
                var remove = new CascadeOp(part, true, reference, index);
                List<CascadeOp> next = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), remove];
                if (TryOps(referrer, next, narrow, out _) is not null)
                    continue;
                Commit(referrerId, next);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == part || IsUnder(pointer, part)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        MarkCascadeDeleted(index, referrerId, "needs " + Label(target), mode);
    }

    private void Commit(string referrerId, List<CascadeOp> ops) => _cascadeOps[referrerId] = ops;

    private void ResolveXRef(int index, string referrerId, Working referrer, ReferenceInfo reference, List<CascadeOp> ops, DeleteResolution mode)
    {
        if (!reference.Optional && mode == DeleteResolution.RemoveReferences)
        {
            _cascadeRefused.Add((index, referrerId, reference,
                $"its property '{reference.Field}' links to it, and {reference.Schema} types the link without optional, so remove-references cannot clear it."));
            return;
        }

        List<CascadeOp> next = [.. ops, new CascadeOp(reference.JsonPointer, false, reference, index)];
        var node = ApplyOps(referrer, next, mode == DeleteResolution.DeleteDependents, out var failure, out _);
        var reason = node is null
            ? failure
            : XRefRemovalProblem(referrer.Element, node, [reference.JsonPointer]) is { } problem
                ? problem + "."
                : Read(node, KindInfo.Get(referrer.Element.Kind), null, new ChangeOutcome()) is null ? "the element would not be valid without it." : null;
        if (reason is not null)
        {
            _cascadeRefused.Add((index, referrerId, reference, reason));
            return;
        }

        Commit(referrerId, next);
    }

    /// <summary>
    /// Clears, in every file the batch does not name otherwise (the saved one included), the optional values an <c>x-ref</c> types that
    /// name a sub-element a save removed (next-phase-1-design.md section 4.1, "Saves that remove a sub-element"). A required value
    /// stays, and the validation of the change reports it (MQ2001); an optional one whose removal a schema declaring the property
    /// refuses makes the save invalid.
    /// </summary>
    private void ClearOptionalReferences(int index, string savedId, IReadOnlyCollection<string> removedIds, ChangeOutcome outcome)
    {
        var schemas = _snapshot.PropertySchemas;
        if (!schemas.HasRefs || removedIds.Count == 0)
            return;
        var removed = removedIds.ToHashSet(StringComparer.Ordinal);
        var referrers = removedIds
            .SelectMany(sub => _snapshot.ReferencesTo(sub))
            .Where(r => r.Schema is not null)
            .Select(r => r.FromElementId)
            .Append(savedId)
            .Where(id => !_deleted.Contains(id) && (id == savedId || !_batchIds.Contains(id)))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
        foreach (var referrerId in referrers)
        {
            if (!TryGetCurrent(referrerId, null, out var current))
                continue;
            var cleared = schemas.Sites(current.Element, _snapshot.PropertyKeys(current.Element))
                .Where(site => Validation.BuiltinRules.XRefIds(site.Value) is { } ids && ids.Any(removed.Contains))
                .GroupBy(site => site.Pointer, StringComparer.Ordinal)
                .Where(group => group.All(site => site.Ref.Optional))
                .Select(group => group.Key)
                .OrderByDescending(pointer => pointer, PointerOrder.Instance)
                .ToList();
            if (cleared.Count == 0)
                continue;
            var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
            foreach (var pointer in cleared)
                _ = RemoveValue(node, pointer);
            var repoPath = _paths.ToRepoPath(current.ModelPath);
            if (XRefRemovalProblem(current.Element, node, cleared) is { } problem)
            {
                outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                    "MQ2001",
                    $"{current.Element.KindName} '{NameOf(current.Element)}' links to what the save removes, and {problem}: change it first.",
                    referrerId,
                    repoPath,
                    cleared[^1]));
                continue;
            }

            var info = KindInfo.Get(current.Element.Kind);
            if (Read(node, info, repoPath, outcome) is not { } element)
                continue;
            _changeByElement.TryAdd(referrerId, index);
            Touch(referrerId, current);
            Stage(referrerId, element, node, info, current.ModelPath, outcome, current);
        }
    }

    /// <summary>Removes the value at a pointer: an object member, or an array item. Whether it was there.</summary>
    private static bool RemoveValue(JsonNode root, string pointer)
    {
        if (!JsonPointer.TryGetParent(root, pointer, out var parent, out var last))
            return false;
        switch (parent)
        {
            case JsonObject obj:
                return obj.Remove(last);
            case JsonArray array when JsonPointer.TryIndex(last, out var i) && i < array.Count:
                array.RemoveAt(i);
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Why the edited holder fails a schema that declares the property an <c>x-ref</c> value was removed from (the <c>optional</c>
    /// decision of next-phase-1-design.md section 4.1): its <c>required</c> names the property (a stereotype default excepted, which
    /// need not be complete), an object it sits in requires the member, or an array drops below <c>minItems</c>. Naming the property
    /// and the schema; <see langword="null"/> when every declaring schema still holds.
    /// </summary>
    /// <param name="element">The holder's top-level element, as the index read it.</param>
    /// <param name="edited">The element's file after the removals.</param>
    /// <param name="pointers">The removed values' pointers (in the file as the index read it).</param>
    private string? XRefRemovalProblem(Element element, JsonObject edited, IEnumerable<string> pointers)
    {
        foreach (var pointer in pointers)
        {
            if (!JsonPointer.TryParse(pointer, out var segments))
                continue;
            var at = Array.FindIndex(segments, segment => segment == "properties");
            var isDefault = segments.Length > 1 && segments[0] == "defaultProperties";
            if (isDefault)
                at = 0;
            if (at < 0 || at + 1 >= segments.Length)
                continue;
            var hostPointer = JsonPointer.Build(segments[..at]);
            var field = segments[at + 1];
            var rest = segments[(at + 2)..];
            IEnumerable<PropertySchemaSource> sources;
            if (isDefault)
            {
                sources = element is Stereotype stereotype ? _snapshot.PropertySchemas.ForDefaults(stereotype.Key) : [];
            }
            else
            {
                var host = PropertyHost.Of(element, _snapshot.PropertyKeys(element)).FirstOrDefault(h => h.Pointer == hostPointer);
                if (host.Id is null)
                    continue;
                sources = _snapshot.PropertySchemas.For(host);
            }

            JsonNode? value = null;
            if (JsonPointer.TryGetParent(edited, JsonPointer.Build(segments[..(at + 2)]), out var holder, out var name) && holder is JsonObject properties)
                value = properties[name];
            foreach (var source in sources)
            {
                if (source.Properties.ValueKind != JsonValueKind.Object || !source.Properties.TryGetProperty(field, out var schema))
                    continue;
                var what = $"its property '{field}' ({source.Schema})";
                if (value is null)
                {
                    if (!isDefault && source.Required.Contains(field, StringComparer.Ordinal))
                        return what + " is required, so the link cannot be cleared";
                }
                else if (rest.Length > 0 && RemovalProblem(schema, value, rest, what) is { } problem)
                {
                    return problem;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Why removing the item at <paramref name="path"/> (from the value's root) leaves <paramref name="edited"/> failing
    /// <paramref name="schema"/>: the array it was in drops below <c>minItems</c>, or the object it was in requires it.
    /// </summary>
    private static string? RemovalProblem(JsonElement schema, JsonNode? edited, IReadOnlyList<string> path, string what)
    {
        var node = edited;
        var current = schema;
        for (var i = 0; i < path.Count - 1; i++)
        {
            switch (node)
            {
                case JsonObject obj:
                    current = current.ValueKind != JsonValueKind.Object ? default
                        : current.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                          && properties.TryGetProperty(path[i], out var member) ? member
                        : current.TryGetProperty("additionalProperties", out var additional) ? additional : default;
                    node = obj[path[i]];
                    break;
                case JsonArray array when JsonPointer.TryIndex(path[i], out var k) && k < array.Count:
                    current = current.ValueKind == JsonValueKind.Object && current.TryGetProperty("items", out var items) ? items : default;
                    node = array[k];
                    break;
                default:
                    return null;
            }

            if (current.ValueKind != JsonValueKind.Object)
                return null;
        }

        if (current.ValueKind != JsonValueKind.Object)
            return null;
        if (node is JsonArray list && current.TryGetProperty("minItems", out var min) && min.TryGetInt32(out var least) && list.Count < least)
            return $"{what} needs at least {least} item{(least == 1 ? "" : "s")}, so the link cannot be removed";
        if (node is JsonObject && current.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            && required.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.GetString() == path[^1]))
            return $"{what} requires its member '{path[^1]}', so the link cannot be removed";
        return null;
    }

    /// <summary>
    /// Judges the pack parameter values that name what a change removes (next-phase-1-design.md section 4.1, "Pack parameters"): a
    /// plain delete, a required value under <c>remove-references</c> and a required value a save would leave dangling refuse (an MQ2001
    /// diagnostic in <c>maquettiste.json</c> at the value, with no element id); otherwise the entry (or its array item) is removed in
    /// the same change, unless the parameter's schema then fails (a required key, an array below <c>minItems</c>), which refuses too.
    /// </summary>
    private void CheckPackValues(int index, IReadOnlyCollection<string> removedIds, PackValueMode mode, ChangeOutcome outcome)
    {
        if (_packParameters is null || removedIds.Count == 0)
            return;
        var refs = _packParameters.Value;
        if (refs.Values.Count == 0)
            return;
        var removed = removedIds.ToHashSet(StringComparer.Ordinal);
        foreach (var value in refs.Values)
        {
            if (Validation.BuiltinRules.XRefIds(value.Value) is not { } ids || ids.FirstOrDefault(removed.Contains) is not { } hit || !_packHandled.Add(value.Pointer))
                continue;
            var why = mode switch
            {
                PackValueMode.Refuse => "a delete that refuses never changes the settings; delete with remove-references (an optional link) or delete-dependents to remove the value in the same change",
                PackValueMode.RemoveReferences when !value.Ref.Optional => "the pack's parameterSchema types the link without optional, so remove-references cannot remove it",
                PackValueMode.Save when !value.Ref.Optional => "the pack's parameterSchema types the link without optional, so the save cannot remove it",
                _ => PackRemovalProblem(value),
            };
            if (why is null)
            {
                _packRemovals.Add((index, value));
                if (mode != PackValueMode.Save)
                {
                    var what = value.Pointer == value.EntryPointer ? $"parameter '{value.Key}' of pack '{value.Pack}'" : $"an item of parameter '{value.Key}' of pack '{value.Pack}'";
                    _settingsRemovals.Add(new DeletePlanSetting(value.Pointer, $"{what}, which names {Label(hit)}"));
                }

                continue;
            }

            var message = $"Parameter '{value.Key}' of pack '{value.Pack}' names {Label(hit)} ({value.Pointer}): {why}. Change the parameter first.";
            outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create("MQ2001", message, null, _paths.ToRepoPath(ModelPaths.SettingsFile), value.Pointer));
            if (mode != PackValueMode.Save)
                _packRefusals.Add(new DeletePlanRefusal(null, null, null, value.Pointer, $"parameter '{value.Key}' of pack '{value.Pack}' names {Label(hit)}: {why}.", "MQ2001"));
        }
    }

    /// <summary>Why removing a pack parameter value fails the parameter's schema, counting the removals already planned.</summary>
    private string? PackRemovalProblem(PackParameterRef value)
    {
        if (value.Pointer == value.EntryPointer)
            return value.Required ? $"the pack's parameterSchema requires the parameter '{value.Key}', so its entry cannot be removed" : null;
        if (!_snapshot.Settings.Packs.TryGetValue(value.Pack, out var settings) || !settings.Parameters.TryGetValue(value.Key, out var original)
            || !JsonPointer.TryParse(value.Pointer[value.EntryPointer.Length..], out var path) || path.Length == 0)
            return null;
        var edited = JsonNode.Parse(original.GetRawText());
        var root = new JsonObject { ["value"] = edited };
        foreach (var pointer in _packRemovals.Where(r => r.Value.Pack == value.Pack && r.Value.Key == value.Key).Select(r => r.Value.Pointer).Append(value.Pointer)
                     .Select(p => "/value" + p[value.EntryPointer.Length..]).OrderByDescending(p => p, PointerOrder.Instance))
            _ = RemoveValue(root, pointer);
        return RemovalProblem(value.Schema, root["value"], path, $"parameter '{value.Key}' of pack '{value.Pack}'");
    }

    /// <summary>The pack parameter values the cascades of the batch remove, judged once per change before the report is built.</summary>
    private void PlanCascadePackValues(IReadOnlyList<ChangeOutcome> outcomes)
    {
        if (_cascadePacksPlanned || _packParameters is null)
            return;
        _cascadePacksPlanned = true;
        var removed = new SortedDictionary<int, HashSet<string>>();
        void Add(int index, IEnumerable<string> ids)
        {
            if (!removed.TryGetValue(index, out var set))
                removed[index] = set = new HashSet<string>(StringComparer.Ordinal);
            set.UnionWith(ids);
        }

        foreach (var id in _cascadeOrder)
        {
            if (TryGetCurrent(id, null, out var current))
                Add(_cascadeDeleted[id].Index, SubElementIds(id, current.Json).Prepend(id));
        }

        foreach (var (referrerId, ops) in _cascadeOps)
        {
            if (!TryGetCurrent(referrerId, null, out var referrer))
                continue;
            foreach (var op in ops.Where(o => o.Remove))
                Add(op.Index, DocumentReader.Scan(referrer.Json).Ids.Where(x => x.Pointer.Length > 0 && (x.Pointer == op.Pointer || IsUnder(x.Pointer, op.Pointer))).Select(x => x.Id));
        }

        foreach (var (index, ids) in removed)
        {
            var mode = _cascadeModes.GetValueOrDefault(index) == DeleteResolution.DeleteDependents ? PackValueMode.DeleteDependents : PackValueMode.RemoveReferences;
            CheckPackValues(index, ids, mode, outcomes[index]);
        }
    }

    /// <summary>Removes the planned pack parameter values from <c>maquettiste.json</c> in this change, once every operation passed.</summary>
    private void RemovePackValues(IReadOnlyList<ChangeOutcome> outcomes)
    {
        if (_packRemovals.Count == 0 || outcomes.Any(o => o.Outcome != SaveOutcome.Saved))
            return;
        var pointers = _packRemovals.Select(r => r.Value.Pointer).Distinct(StringComparer.Ordinal).OrderByDescending(p => p, PointerOrder.Instance).ToList();
        var packs = _packRemovals.Select(r => r.Value.Pack).Distinct(StringComparer.Ordinal).ToList();
        var edited = EditSettings(
            node =>
            {
                var any = false;
                foreach (var pointer in pointers)
                    any |= RemoveValue(node, pointer);
                return any;
            },
            (settings, node) =>
            {
                var all = settings.Packs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                foreach (var pack in packs.Where(all.ContainsKey))
                {
                    var parameters = node["packs"]?[pack]?["parameters"] as JsonObject;
                    all[pack] = all[pack] with
                    {
                        Parameters = parameters is null
                            ? ImmutableDictionary<string, JsonElement>.Empty
                            : parameters.ToImmutableDictionary(kv => kv.Key, kv => JsonSerializer.SerializeToElement(kv.Value), StringComparer.Ordinal),
                    };
                }

                return settings with { Packs = all.ToImmutableDictionary(StringComparer.Ordinal) };
            });
        if (!edited)
        {
            var first = _packRemovals[0];
            outcomes[first.Index].Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                "MQ2001",
                $"Parameter '{first.Value.Key}' of pack '{first.Value.Pack}' names what the change removes, and maquettiste.json cannot be read to remove it.",
                null,
                _paths.ToRepoPath(ModelPaths.SettingsFile),
                first.Value.Pointer));
        }
    }

    /// <summary>
    /// Edits <c>maquettiste.json</c> in this change, on top of any edit the plan already staged: <paramref name="edit"/> changes the
    /// file's tree (returning whether it changed anything), and <paramref name="candidate"/> derives the candidate model's settings
    /// from the settings so far and the edited tree. False when the file cannot be read or nothing changed.
    /// </summary>
    private bool EditSettings(Func<JsonObject, bool> edit, Func<ProjectSettings, JsonObject, ProjectSettings> candidate)
    {
        JsonObject node;
        try
        {
            if (_writes.TryGetValue(ModelPaths.SettingsFile, out var planned))
            {
                node = JsonNode.Parse(planned, documentOptions: DocumentReader.ParseOptions)!.AsObject();
            }
            else
            {
                var full = _paths.FullPath(ModelPaths.SettingsFile);
                if (!File.Exists(full))
                    return false;
                node = JsonNode.Parse(DocumentReader.StripBom(File.ReadAllBytes(full)).Span, documentOptions: DocumentReader.ParseOptions)!.AsObject();
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or IOException)
        {
            return false; // an unreadable settings file is reported by the loader
        }

        if (!edit(node))
            return false;
        RetiredSettings.Strip(node);
        var bytes = _json.Write(node, ModelPaths.SettingsFile, ModelPaths.SettingsFile);
        _writes[ModelPaths.SettingsFile] = bytes;
        _touched[ModelPaths.SettingsFile] = _snapshot.SettingsHash;
        _candidateSettings = candidate(_candidateSettings ?? _snapshot.Settings, node);
        _candidateSettingsHash = ContentHash.Of(bytes);
        return true;
    }

    /// <summary>A table's <c>redefines</c> entry a reference at <c>/redefines/&lt;n&gt;/member</c> sits in, else <see langword="null"/>.</summary>
    private static string? RedefinesEntry(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments is ["redefines", _, "member"] ? JsonPointer.Build(segments[..2]) : null;

    /// <summary>
    /// The part of an entity's binding a reference at <paramref name="pointer"/> takes with it: the field, constant or listed column
    /// entry it sits in, else the whole binding; <see langword="null"/> outside <c>bindings</c>.
    /// </summary>
    private static string? BindingPart(string pointer)
    {
        if (!pointer.StartsWith("/bindings/", StringComparison.Ordinal) || !JsonPointer.TryParse(pointer, out var segments) || segments.Length < 3)
            return null;
        if (segments.Length >= 4 && segments[2] is "fields" or "constants" or "columns")
            return JsonPointer.Build(segments[..4]);
        return JsonPointer.Build(segments[..2]);
    }

    private bool Readable(string id, Working current)
    {
        if (_cascadeReadable.TryGetValue(id, out var readable))
            return readable;
        var node = JsonNode.Parse(current.Json.GetRawText())!.AsObject();
        readable = Read(node, KindInfo.Get(current.Element.Kind), null, new ChangeOutcome()) is not null;
        _cascadeReadable[id] = readable;
        return readable;
    }

    /// <summary>Applies the edits to a copy of the referrer; null when it is valid after them, else why not.</summary>
    private string? TryOps(Working referrer, IReadOnlyList<CascadeOp> ops, bool narrow, out bool widening)
    {
        widening = false;
        var node = ApplyOps(referrer, ops, narrow, out var failure, out widening);
        if (node is null)
            return failure;
        return Read(node, KindInfo.Get(referrer.Element.Kind), null, new ChangeOutcome()) is not null
            ? null
            : "the reference is required, so it cannot be cleared.";
    }

    private static JsonObject? ApplyOps(Working referrer, IReadOnlyList<CascadeOp> ops, bool narrow, out string? failure, out bool widening)
    {
        failure = null;
        widening = false;
        var node = JsonNode.Parse(referrer.Json.GetRawText())!.AsObject();
        foreach (var op in ops.OrderByDescending(o => o.Pointer, PointerOrder.Instance))
        {
            if (op.Remove)
            {
                if (!JsonPointer.TryGetParent(node, op.Pointer, out var parent, out var last))
                {
                    failure = "it is not where the index says.";
                    return null;
                }

                var removed = parent switch
                {
                    JsonObject obj => obj.Remove(last),
                    JsonArray array when JsonPointer.TryIndex(last, out var i) && i < array.Count => Remove(array, i),
                    _ => false,
                };
                if (!removed)
                {
                    failure = "it is not where the index says.";
                    return null;
                }

                continue;
            }

            if (RemoveAt(node, op.Pointer, referrer.Element.Kind, narrow, out var wide) is { } reason)
            {
                failure = reason;
                widening = wide;
                return null;
            }
        }

        return node;

        static bool Remove(JsonArray array, int i)
        {
            array.RemoveAt(i);
            return true;
        }
    }

    private static bool IsUnder(string pointer, string ancestor) =>
        pointer.Length > ancestor.Length && pointer[ancestor.Length] == '/' && pointer.StartsWith(ancestor, StringComparison.Ordinal);

    /// <summary>Stages what <see cref="CascadeDelete"/> planned, after every operation of the batch ran.</summary>
    private void FinishCascade(IReadOnlyList<ChangeOutcome> outcomes)
    {
        if (_cascadeOrder.Count == 0)
            return;
        PlanCascadePackValues(outcomes);
        CascadeReport = BuildReport();
        foreach (var group in _cascadeRefused.GroupBy(r => (r.Index, r.ElementId)).OrderBy(g => g.Key.Index).ThenBy(g => g.Key.ElementId, StringComparer.Ordinal))
        {
            var referrer = _snapshot.GetDocument(group.Key.ElementId)!;
            var pointers = group.Select(r => r.Reference.JsonPointer).Distinct(StringComparer.Ordinal).ToList();
            var targets = group.Select(r => Label(r.Reference.ToId)).Distinct(StringComparer.Ordinal);
            outcomes[group.Key.Index].Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                "MQ2001",
                $"{referrer.Element.KindName} '{NameOf(referrer.Element)}' cannot lose its reference to {string.Join(", ", targets)} "
                + $"({string.Join(", ", pointers)}): {group.First().Why} Change or delete it first, or delete with its dependents (resolution delete-dependents).",
                group.Key.ElementId,
                referrer.Path,
                pointers[0]));
        }

        if (outcomes.Any(o => o.Outcome != SaveOutcome.Saved))
            return;

        var removedSubIds = new Dictionary<int, List<string>>();
        foreach (var id in _cascadeOrder)
        {
            if (!TryGetCurrent(id, null, out var current))
                continue;
            var index = _cascadeDeleted[id].Index;
            _changeByElement.TryAdd(id, index);
            Touch(id, current);
            var subIds = StageDelete(index, id, current);
            Add(removedSubIds, index, subIds);
        }

        foreach (var (referrerId, ops) in _cascadeOps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (ops.Count == 0 || !TryGetCurrent(referrerId, null, out var referrer))
                continue;
            var index = ops.Min(o => o.Index);
            var outcome = outcomes[index];
            var node = ApplyOps(referrer, ops, narrow: true, out var failure, out _);
            var repoPath = _paths.ToRepoPath(referrer.ModelPath);
            var info = KindInfo.Get(referrer.Element.Kind);
            if (node is null || Read(node, info, repoPath, outcome) is not { } element)
            {
                outcome.Fail(SaveOutcome.Invalid, RuleCatalog.Create(
                    "MQ2001",
                    $"{referrer.Element.KindName} '{NameOf(referrer.Element)}' cannot be changed to drop its references: {failure ?? "it would not be valid."}",
                    referrerId,
                    repoPath,
                    ops[0].Pointer));
                continue;
            }

            outcome.Referrers.AddRange(ops.Select(o => o.Reference).Distinct());
            _changeByElement.TryAdd(referrerId, index);
            Touch(referrerId, referrer);
            Stage(referrerId, element, node, info, referrer.ModelPath, outcome, referrer);
            var removed = DocumentReader.Scan(referrer.Json).Ids
                .Where(x => x.Pointer.Length > 0 && ops.Any(o => o.Remove && (x.Pointer == o.Pointer || IsUnder(x.Pointer, o.Pointer))))
                .Select(x => x.Id)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            foreach (var subId in removed)
                _deleteChecks.Add((index, subId, []));
            _removedIds.UnionWith(removed);
            Add(removedSubIds, index, removed);
        }

        foreach (var (index, subIds) in removedSubIds.OrderBy(kv => kv.Key))
        {
            if (outcomes[index].Outcome == SaveOutcome.Saved && subIds.Count > 0)
                DropSeedColumns(index, subIds, outcomes[index], _cascadeMembers);
        }

        RemoveDatabaseConventions(outcomes);

        static void Add(Dictionary<int, List<string>> map, int index, IEnumerable<string> ids)
        {
            if (!map.TryGetValue(index, out var list))
                map[index] = list = [];
            list.AddRange(ids);
        }
    }

    /// <summary>
    /// Removes the <c>databases.&lt;name&gt;</c> convention entry of every deleted database from <c>maquettiste.json</c> in the same
    /// change, unless another database keeps the name: the entry is keyed by name, so it would otherwise apply to a later database
    /// that takes the name.
    /// </summary>
    private void RemoveDatabaseConventions(IReadOnlyList<ChangeOutcome> outcomes)
    {
        var names = _cascadeOrder
            .Select(id => _snapshot.Get<Database>(id))
            .OfType<Database>()
            .Select(d => d.Name)
            .Where(n => _snapshot.Settings.Databases.ContainsKey(n))
            .Distinct(StringComparer.Ordinal)
            .Where(n => !_snapshot.All<Database>().Any(d => d.Name == n && !_deleted.Contains(d.Id)))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (names.Count == 0)
            return;
        if (outcomes.Any(o => o.Outcome != SaveOutcome.Saved))
            return;
        // The recording of migration facts chooses nothing more for this settings change: the entries belong to deleted databases,
        // whose facts go with them, so no database is reshaped by it (next-phase-1-design.md section 3.2, "Write surfaces").
        _ = EditSettings(
            node =>
            {
                if (node["databases"] is not JsonObject databases)
                    return false;
                foreach (var name in names)
                    databases.Remove(name);
                if (databases.Count == 0)
                    node.Remove("databases");
                return true;
            },
            (settings, _) => settings with
            {
                Databases = settings.Databases.Where(kv => !names.Contains(kv.Key)).ToImmutableDictionary(StringComparer.Ordinal),
            });
    }

    private DeletePlanReport BuildReport()
    {
        var deletes = _cascadeOrder
            .Where(id => _cascadeDeleted[id].Because is not null)
            .Select(id =>
            {
                var document = _snapshot.GetDocument(id)!;
                return new DeletePlanDelete(id, document.Element.KindName, NameOf(document.Element), document.Path, _cascadeDeleted[id].Because!);
            })
            .ToList();
        var clears = new List<DeletePlanClear>();
        var removes = new List<DeletePlanRemove>();
        foreach (var (referrerId, ops) in _cascadeOps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var document = _snapshot.GetDocument(referrerId)!;
            foreach (var op in ops.OrderBy(o => o.Pointer, PointerOrder.Instance))
            {
                if (op.Remove)
                {
                    var sub = DocumentReader.Scan(document.Json).Ids.Where(x => x.Pointer == op.Pointer).Select(x => x.Id).FirstOrDefault();
                    var subKind = sub is not null && _snapshot.TryGetEntry(sub, out var entry) ? entry.Kind : null;
                    var because = document.Element.Kind is ElementKind.Entity or ElementKind.ReferenceType && op.Pointer.StartsWith("/bindings/", StringComparison.Ordinal)
                        ? (subKind == "binding" ? "the binding" : "this entry of its binding") + " needs " + Label(op.Reference.ToId)
                            + (document.Element.Kind == ElementKind.Entity ? "; the entity stays" : "; the reference type stays")
                        : "needs " + Label(op.Reference.ToId);
                    removes.Add(new DeletePlanRemove(referrerId, document.Element.KindName, NameOf(document.Element), op.Pointer,
                        Part(document.Json, op.Pointer, subKind, ShortName(op.Reference.ToId)), sub, subKind, because));
                }
                else if (document.Element.Kind == ElementKind.Diagram && op.Reference.Field == "element")
                {
                    var member = op.Pointer[..op.Pointer.LastIndexOf('/')];
                    removes.Add(new DeletePlanRemove(referrerId, document.Element.KindName, NameOf(document.Element), member,
                        "member " + NameOrId(op.Reference.ToId), null, "member", "shows " + Label(op.Reference.ToId)));
                }
                else
                {
                    clears.Add(new DeletePlanClear(referrerId, document.Element.KindName, NameOf(document.Element), op.Pointer, op.Reference.Field,
                        op.Reference.ToId, "pointed to " + Label(op.Reference.ToId)));
                }
            }
        }

        removes.AddRange(SeedColumnsOfMembers());
        removes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id) is var c and not 0 ? c : PointerOrder.Instance.Compare(a.Pointer, b.Pointer));
        var refused = _cascadeRefused
            .Select(r =>
            {
                var document = _snapshot.GetDocument(r.ElementId)!;
                return new DeletePlanRefusal(r.ElementId, document.Element.KindName, NameOf(document.Element), r.Reference.JsonPointer,
                    $"its reference to {Label(r.Reference.ToId)} at {r.Reference.JsonPointer}: {r.Why}", "MQ2001");
            })
            .ToList();
        var settings = _cascadeOrder
            .Select(id => _snapshot.Get<Database>(id))
            .OfType<Database>()
            .Where(d => _snapshot.Settings.Databases.ContainsKey(d.Name))
            .Select(d => new DeletePlanSetting("/databases/" + JsonPointer.Escape(d.Name), $"the conventions of database {d.Name}"))
            .Concat(_settingsRemovals)
            .DistinctBy(s => s.Pointer, StringComparer.Ordinal)
            .ToList();
        // Pack parameter refusals carry no element id; the delete plan reads them here, in every resolution (section 4.1).
        refused.AddRange(_packRefusals);
        var roots = _cascadeOrder.Where(id => _cascadeDeleted[id].Because is null).ToList();
        return new DeletePlanReport(roots, deletes, clears, removes, refused, settings);
    }

    /// <summary>
    /// The seed columns that name an inherited copy of a deleted stereotype's member by keyed id, which the plan drops with their cells
    /// (next-phase-2-design.md section 3.1); seeds deleted with the cascade or named by the batch are left out.
    /// </summary>
    private IEnumerable<DeletePlanRemove> SeedColumnsOfMembers()
    {
        foreach (var member in _cascadeMembers.Order(StringComparer.Ordinal))
        {
            foreach (var reference in _snapshot.ReferencesTo(member)
                         .Where(r => r.Field == "columns" && !_cascadeDeleted.ContainsKey(r.FromElementId) && !_deleted.Contains(r.FromElementId)
                             && !_batchIds.Contains(r.FromElementId))
                         .OrderBy(r => r.FromElementId, StringComparer.Ordinal).ThenBy(r => r.JsonPointer, PointerOrder.Instance))
            {
                if (_snapshot.GetDocument(reference.FromElementId) is not { Element: Seed seed } document
                    || !JsonPointer.TryGet(document.Json, reference.JsonPointer, out var value) || value.ValueKind != JsonValueKind.String)
                    continue;
                yield return new DeletePlanRemove(seed.Id, seed.KindName, NameOf(seed), reference.JsonPointer, "column " + value.GetString(), null, "column",
                    "names " + Label(member) + " where a table inherits it; its cells go with it");
            }
        }
    }

    private string NameOrId(string id) => _snapshot.GetDocument(id) is { } document && document.Element.Id == id ? NameOf(document.Element) : id;

    private string NameOf(Element element) => ReadableName(_snapshot, element);

    /// <summary>A readable name for an element: its name, else its display name, else (a synthesized table) its entity's name.</summary>
    /// <param name="snapshot">The model the element is in.</param>
    /// <param name="element">The element.</param>
    /// <returns>The name.</returns>
    internal static string ReadableName(ModelSnapshot snapshot, Element element)
    {
        if (element.Name.Length > 0)
            return element.Name;
        if (!string.IsNullOrEmpty(element.DisplayName))
            return element.DisplayName;
        if (element is Table { Entity: { } entity } && snapshot.Get<Element>(entity) is { } owner)
            return owner.Name + " table";
        return element.Id;
    }

    private string Label(string id) => Describe(_snapshot, id);

    /// <summary>"entity Invoice", or for a sub-element "attribute total of entity Invoice".</summary>
    /// <param name="snapshot">The model the id is in.</param>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns>The description, or the id when the model does not have it.</returns>
    internal static string Describe(ModelSnapshot snapshot, string id)
    {
        if (snapshot.GetDocument(id) is not { } document)
            return id;
        var owner = $"{document.Element.KindName} {ReadableName(snapshot, document.Element)}";
        if (document.Element.Id == id)
            return owner;
        if (!snapshot.TryGetEntry(id, out var entry))
            return id;
        return $"{entry.Kind} {SubName(document.Json, entry.JsonPointer) ?? id} of {owner}";
    }

    /// <summary>The name of an element or sub-element alone, without its owner.</summary>
    private string ShortName(string id)
    {
        if (_snapshot.GetDocument(id) is not { } document)
            return id;
        if (document.Element.Id == id)
            return NameOf(document.Element);
        return _snapshot.TryGetEntry(id, out var entry) ? SubName(document.Json, entry.JsonPointer) ?? id : id;
    }

    private static string? SubName(JsonElement json, string pointer)
    {
        if (!JsonPointer.TryGet(json, pointer, out var value) || value.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var property in (string[])["name", "role", "code", "key", "label"])
        {
            if (value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String && text.GetString() is { Length: > 0 } s)
                return s;
        }

        return null;
    }

    /// <summary>"attribute total", "end lines", "foreign key fk_invoice", "key"; an unnamed part takes the name of what it pointed to.</summary>
    private static string Part(JsonElement json, string pointer, string? subKind, string? fallback)
    {
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length == 0)
            return pointer;
        var last = segments[^1];
        var item = JsonPointer.TryIndex(last, out _) && segments.Length > 1;
        var noun = subKind ?? Noun(item ? segments[^2] : last);
        var name = SubName(json, pointer) ?? (item ? fallback : null);
        return name is null ? noun : noun + " " + name;
    }

    private static string Noun(string property) => property switch
    {
        "attributes" => "attribute",
        "redefines" => "redefinition",
        "ends" => "end",
        "members" => "member",
        "columns" => "column",
        "foreignKeys" => "foreign key",
        "indexes" => "index",
        "uniques" => "unique constraint",
        "checks" => "check",
        "rows" => "row",
        "fields" => "field",
        "constants" => "constant",
        "bindings" => "binding",
        "steps" => "step",
        "states" => "state",
        "transitions" => "transition",
        "values" => "value",
        _ => property,
    };
}

/// <summary>What the planned deletes of a change do (the planner's half of <see cref="DeletePlan"/>).</summary>
/// <param name="Roots">The ids the change deletes itself.</param>
/// <param name="Deletes">Every other element deleted.</param>
/// <param name="Clears">References cleared.</param>
/// <param name="Removes">Parts of surviving elements removed.</param>
/// <param name="Refused">References that cannot be resolved.</param>
/// <param name="Settings">Settings entries removed.</param>
internal sealed record DeletePlanReport(
    IReadOnlyList<string> Roots,
    IReadOnlyList<DeletePlanDelete> Deletes,
    IReadOnlyList<DeletePlanClear> Clears,
    IReadOnlyList<DeletePlanRemove> Removes,
    IReadOnlyList<DeletePlanRefusal> Refused,
    IReadOnlyList<DeletePlanSetting> Settings);
