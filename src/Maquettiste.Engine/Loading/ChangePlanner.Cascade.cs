using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

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
            if (how == DeleteResolution.DeleteDependents)
                DropAnswers(at, target);
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
        // A deleted stereotype's members are named by keyed id where tables and operations inherit them (next-phase-2-design.md section 3.1).
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

        // An id inside an expression tree (next-phase-3-design.md section 3.1): a tree never loses a node, since it would mean something
        // else. remove-references refuses; delete-dependents removes what holds the tree: the check or guard, the derived value, an action's
        // set (object-effects-design.md section 8), a call's input (processes-operations-design.md section 11), or a constraint whole.
        // A named trigger point's when and condition need the point, and a set's value its set (trigger-points-design.md section 9, part 2).
        if ((referrer.Element is TriggerPoint ? NamedTriggerHolder(reference.JsonPointer, "value") : ExpressionHolder(reference.JsonPointer)) is { } holder)
        {
            if (mode != DeleteResolution.DeleteDependents)
            {
                var what = referrer.Element is TriggerPoint ? (holder.Length > 0 ? "the set" : "the trigger point")
                    : referrer.Element is Right || holder.StartsWith("/rights/", StringComparison.Ordinal) ? "the right"
                    : referrer.Element is Process && SetHolder(holder) ? "the set"
                    : referrer.Element is Process && CallInputHolder(holder) ? "the call's input"
                    : referrer.Element is Process && holder.StartsWith("/triggerPoints/", StringComparison.Ordinal) ? "the trigger point"
                    : referrer.Element is Process && SignatureCondition(holder) ? "the signature's condition (when)"
                    : referrer.Element is Process && StageCondition(holder) ? "the stage's condition (when)"
                    : "the check or guard";
                _cascadeRefused.Add((index, referrerId, reference,
                    $"an expression names {Label(target)}, and remove-references does not rewrite an expression; delete with delete-dependents to remove {what} that holds it."));
                return;
            }

            if (holder.Length > 0)
            {
                var drop = new CascadeOp(holder, true, reference, index);
                List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != holder && !IsUnder(o.Pointer, holder)), drop];
                if (TryOps(referrer, dropped, narrow, out _) is null)
                {
                    Commit(referrerId, dropped);
                    foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                    {
                        if (pointer.Length > 0 && (pointer == holder || IsUnder(pointer, holder)))
                            _cascadeQueue.Enqueue((index, subId, mode));
                    }

                    return;
                }
            }

            MarkCascadeDeleted(index, referrerId, "needs " + Label(target), mode);
            return;
        }

        // A right's actors, states, ends and attributes, and a use's named right (rights-design.md section 8), in either resolution: the entry
        // is dropped from its list, but the last one left takes the right with it, since an empty list would widen the right to everyone, every
        // state or the whole object; an inline right goes from its lifecycle's rights, a named right is deleted, and a use goes with its named right.
        // An actor in except is cleared as any reference is below, and the right stays.
        if (RightPart(referrer.Element, reference.JsonPointer) is { } rightPart)
        {
            var whole = rightPart.List is not { } list || Remaining(referrer.Json, list, ops) <= 1;
            if (whole && rightPart.Entry.Length == 0)
            {
                MarkCascadeDeleted(index, referrerId, "names no other " + Noun(rightPart.List![1..]) + " than " + Label(target), mode);
                return;
            }

            var part = whole ? rightPart.Entry : reference.JsonPointer;
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == part || IsUnder(pointer, part)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        // A trigger point's states, targets, transitions, entities, attributes and ends (trigger-points-design.md section 9), in either
        // resolution: the entry is dropped from its list, but the last one left takes the trigger point with it, since an empty list would
        // widen it to every state, every move, every type or the whole object.
        if (referrer.Element is Process && TriggerPointPart(reference.JsonPointer) is { } pointPart)
        {
            var part = Remaining(referrer.Json, pointPart.List, ops) <= 1 ? pointPart.Entry : reference.JsonPointer;
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == part || IsUnder(pointer, part)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        // A named trigger point's subjects, entities, attributes and ends (trigger-points-design.md section 9, part 2), in either resolution:
        // the entry is dropped, but the last one left takes the point with it, since an empty list would widen it to every type or the whole
        // object. An entry of exceptEntities is dropped, and the key with its last entry.
        if (referrer.Element is TriggerPoint && NamedTriggerPart(reference.JsonPointer) is { } namedPart)
        {
            var last = Remaining(referrer.Json, namedPart.List, ops) <= 1;
            if (last && namedPart.List != "/exceptEntities")
            {
                MarkCascadeDeleted(index, referrerId, "names no other " + Noun(namedPart.List[1..]) + " than " + Label(target), mode);
                return;
            }

            var part = last ? namedPart.List : namedPart.Item;
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                return;
            }
        }

        // A verb or a move a named signature's notBy names (signoff-design.md section 9), in either resolution: the entry goes, and the key
        // with its last entry. The verb or move can no longer happen, so dropping it widens nothing that could occur.
        if (referrer.Element is Process && NotByPart(reference.JsonPointer) is { } notBy)
        {
            var part = Remaining(referrer.Json, notBy.List, ops) <= 1 ? notBy.List : notBy.Entry;
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                return;
            }
        }

        // An actor a named signature's delegateTo or escalateTo names (signoff-design.md section 9, part 2), in either resolution: the entry goes,
        // and the key with its last entry, so the signature may no longer be delegated, or escalates to nobody; escalateAfter goes with the last
        // escalateTo, since one without the other is MQ8204.
        if (referrer.Element is Process && NoticeActorPart(reference.JsonPointer) is { } actorPart)
        {
            var last = Remaining(referrer.Json, actorPart.List, ops) <= 1;
            List<string> parts = last ? [actorPart.List] : [reference.JsonPointer];
            if (last && actorPart.List.EndsWith("/escalateTo", StringComparison.Ordinal))
            {
                var after = actorPart.List[..^"escalateTo".Length] + "escalateAfter";
                if (JsonPointer.TryGet(referrer.Json, after, out _))
                    parts.Add(after);
            }

            List<CascadeOp> dropped = [.. ops.Where(o => !parts.Any(part => o.Pointer == part || IsUnder(o.Pointer, part))), .. parts.Select(part => new CascadeOp(part, true, reference, index))];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                return;
            }
        }

        // A named signature a step's expect.notices names (signoff-design.md section 9, part 2), in either resolution: only the listed notices
        // are compared, so the entry goes, and the list with its last entry.
        if (referrer.Element is Scenario && ExpectedNotice(reference.JsonPointer) is { } notice)
        {
            var part = Remaining(referrer.Json, notice.List, ops) <= 1 ? notice.List : notice.Entry;
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                return;
            }
        }

        // A named signature a step's expect.signatures counts (signoff-design.md section 9), in either resolution: only the listed signatures
        // are compared, so its key is cleared, and the map with its last key.
        if (referrer.Element is Scenario && ExpectedSignature(reference.JsonPointer) is { } expected)
        {
            var part = Remaining(referrer.Json, expected.Map, ops) <= 1 ? expected.Map : reference.JsonPointer;
            List<CascadeOp> cleared = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, false, reference, index)];
            if (TryOps(referrer, cleared, narrow, out _) is null)
            {
                Commit(referrerId, cleared);
                return;
            }
        }

        // Links to a member of a deleted stereotype (next-phase-2-design.md sections 3.1 and 4.1): a table's or an operation's redefinition
        // of it goes whole and an exclusion of it is cleared, in both resolutions; any other keyed use of an inherited copy (an index, a key, a binding field, a
        // query) refuses remove-references, and delete-dependents removes it as it removes a deleted column's uses.
        if (_cascadeMembers.Contains(target))
        {
            if (referrer.Element is Table or Operation && reference.Field == "member" && RedefinesEntry(reference.JsonPointer) is { } entry)
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

        // An entity's revisions block (revisions-design.md section 10, "Deletes"), in either resolution: a bound attribute that goes clears
        // boundAttribute, and a lifecycle, state, attribute or end an entry names takes the entry, so that item falls back (the type then says
        // nothing about it). A list goes with its last entry, and the block with its last key, since an empty block is not valid.
        if (referrer.Element is Entity && RevisionsPart(reference.JsonPointer) is { } revisionsPart)
        {
            var next = RevisionsOps(referrer.Json, revisionsPart, reference, index, ops);
            if (TryOps(referrer, next, narrow, out _) is null)
            {
                Commit(referrerId, next);
                return;
            }
        }

        // An entity or a stereotype a process lists in subjects (lifecycle-design.md decision 5, stereotype-sets-design.md decision 11): its
        // entry goes in either resolution, the way an entity's binding does, and a list left empty goes whole, so a lifecycle is left with no
        // subject (MQ9201), as when its one subject goes.
        if (referrer.Element is Process && SubjectEntry(reference.JsonPointer) is { } subjectEntry)
        {
            var part = subjectEntry;
            var entries = referrer.Json.TryGetProperty("subjects", out var listed) && listed.ValueKind == JsonValueKind.Array ? listed.GetArrayLength() : 0;
            if (ops.Where(o => o.Remove && SubjectEntry(o.Pointer + "/entity") == o.Pointer).Select(o => o.Pointer).Append(subjectEntry).Distinct(StringComparer.Ordinal).Count() >= entries)
                part = "/subjects";
            var drop = new CascadeOp(part, true, reference, index);
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), drop];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == part || IsUnder(pointer, part)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        // An entity or a stereotype a relation end's types names (several-types-design.md decision 21): its entry goes in either resolution, as
        // a named trigger point's subject does, and one entity entry left is a valid one-type end. The last entry falls through to today's
        // "a relation that loses an end" below: delete-dependents deletes the relation, remove-references refuses.
        if (referrer.Element is Relation && EndTypeEntry(reference.JsonPointer) is { } endType && Remaining(referrer.Json, endType.List, ops) > 1)
        {
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != endType.Item && !IsUnder(o.Pointer, endType.Item)), new CascadeOp(endType.Item, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                return;
            }
        }

        // An ends entry of a relation mapping (several-types-design.md decision 38, section 19.6): the schema requires only its end, but an entry
        // needs a way to point at it. What a delete takes goes alone while the entry keeps a way: a foreignKey or idColumn, an item of
        // foreignKeys, and in either resolution the types item of an entity that goes. An entry left with no way, or losing its typeColumn or
        // referencesColumn (what its id means), goes whole with delete-dependents, and remove-references refuses with the words it had while
        // the schema required foreignKey; so a one-type mapping keeps today's plans. A types item's own key or column follows the generic rule.
        if (referrer.Element is Mapping && EndsEntryPart(reference.JsonPointer) is { } endsPart)
        {
            var losesMeaning = endsPart.Item is null && endsPart.Member is "typeColumn" or "referencesColumn";
            var part = endsPart.Item is null ? reference.JsonPointer : endsPart.ItemMember is null or "entity" || narrow ? endsPart.Item : null;
            if (!losesMeaning && part is not null)
            {
                List<CascadeOp> next = [.. ops.Where(o => o.Pointer != part && !IsUnder(o.Pointer, part)), new CascadeOp(part, endsPart.Item is not null, reference, index)];
                var list = endsPart.Entry + "/" + endsPart.Member;
                if (endsPart.Item is not null && Remaining(referrer.Json, list, next) <= 0)
                    next = [.. next.Where(o => o.Pointer != list && !IsUnder(o.Pointer, list)), new CascadeOp(list, true, reference, index)];
                if (!EntryKeepsWay(referrer.Json, endsPart.Entry, next))
                    losesMeaning = true;
                else if (TryOps(referrer, next, narrow, out _) is null)
                {
                    Commit(referrerId, next);
                    return;
                }
            }

            if (losesMeaning)
            {
                if (mode != DeleteResolution.DeleteDependents)
                {
                    _cascadeRefused.Add((index, referrerId, reference, "the reference is required, so it cannot be cleared."));
                    return;
                }

                List<CascadeOp> whole = [.. ops.Where(o => o.Pointer != endsPart.Entry && !IsUnder(o.Pointer, endsPart.Entry)), new CascadeOp(endsPart.Entry, true, reference, index)];
                if (TryOps(referrer, whole, narrow, out _) is null)
                {
                    Commit(referrerId, whole);
                    return;
                }
            }
        }

        // The attribute an action's set writes (object-effects-design.md section 8): a set means nothing without it, so in either resolution
        // the set goes, and the action keeps its other sets.
        if ((referrer.Element is TriggerPoint ? NamedTriggerHolder(reference.JsonPointer, "attribute") : referrer.Element is Process ? SetOfAttribute(reference.JsonPointer) : null)
            is { Length: > 0 } setEntry)
        {
            var drop = new CascadeOp(setEntry, true, reference, index);
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != setEntry && !IsUnder(o.Pointer, setEntry)), drop];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == setEntry || IsUnder(pointer, setEntry)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        // The operation an action calls, and the input member a call's input names (processes-operations-design.md section 11): a call means
        // nothing without its operation, nor an input without its member, so in either resolution the call or the input goes, and the action
        // keeps its other effects.
        if (referrer.Element is Process && CallPart(reference.JsonPointer) is { } callPart)
        {
            var drop = new CascadeOp(callPart, true, reference, index);
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != callPart && !IsUnder(o.Pointer, callPart)), drop];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == callPart || IsUnder(pointer, callPart)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        // A scenario's objects (lifecycle-design.md decision 22): in either resolution an object whose entity goes is dropped with its
        // links, and so is each link to an object that goes, and each participant's holding within it (actors-design.md section 7). A
        // subject left naming no object refuses remove-references (unless the scenario goes too, FinishCascade); delete-dependents deletes
        // the scenario. A participant's actor or object, an object's owner and a step's participant are optional and are cleared.
        // An actor from the object (actors-design.md section 7): an id on its path or within, or its membersOf, is what it means, so
        // remove-references refuses unless the actor goes too (Refusals), and delete-dependents deletes the actor.
        if (referrer.Element is Actor && IsRelativePart(reference.JsonPointer))
        {
            if (mode != DeleteResolution.DeleteDependents)
            {
                _cascadeRefused.Add((index, referrerId, reference,
                    $"it is worked out from the object through {Label(target)} (relative), and remove-references does not change what such an actor means; delete the actor too, or delete with delete-dependents."));
                return;
            }

            MarkCascadeDeleted(index, referrerId, "is worked out through " + Label(target), mode);
            return;
        }

        if (referrer.Element is Scenario && reference.JsonPointer == "/subject")
        {
            if (mode != DeleteResolution.DeleteDependents)
            {
                _cascadeRefused.Add((index, referrerId, reference,
                    $"{Label(target)} is the object it runs for (subject), and remove-references does not leave a scenario with objects but no subject; delete the scenario too, or delete with delete-dependents."));
                return;
            }

            MarkCascadeDeleted(index, referrerId, "runs for " + Label(target), mode);
            return;
        }

        if (referrer.Element is Scenario && ScenarioObjectPart(reference.JsonPointer) is { } objectPart)
        {
            var drop = new CascadeOp(objectPart, true, reference, index);
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != objectPart && !IsUnder(o.Pointer, objectPart)), drop];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                foreach (var (subId, pointer) in DocumentReader.Scan(referrer.Json).Ids)
                {
                    if (pointer.Length > 0 && (pointer == objectPart || IsUnder(pointer, objectPart)))
                        _cascadeQueue.Enqueue((index, subId, mode));
                }

                return;
            }
        }

        // An entry of a SQL text query's reads or writes (sql-queries-design.md decision 17): the text is the team's own work and the engine
        // cannot tell whether it still needs the object, so in either resolution the entry goes and the query stays, with a delete-plan warning.
        if (referrer.Element is Query { IsSqlText: true } && SqlTextEntry(reference.JsonPointer))
        {
            List<CascadeOp> dropped = [.. ops.Where(o => o.Pointer != reference.JsonPointer), new CascadeOp(reference.JsonPointer, true, reference, index)];
            if (TryOps(referrer, dropped, narrow, out _) is null)
            {
                Commit(referrerId, dropped);
                return;
            }
        }

        // An operation member's `type` or `keyOf` is its one form (MQ3024), as a returns' `entity` is (MQ3025), and the schema cannot
        // say so: clearing it would leave a part with neither. So the member or the returns goes with delete-dependents, and
        // remove-references refuses.
        var widening = false;
        if (referrer.Element is Operation && IsOperationForm(reference.JsonPointer))
        {
            if (mode != DeleteResolution.DeleteDependents)
            {
                _cascadeRefused.Add((index, referrerId, reference,
                    $"it types or keys an operation member, or is the entity an outcome returns, with {Label(target)}, and remove-references does not clear such a use; delete with delete-dependents to remove that member or return."));
                return;
            }
        }
        else
        {
            var clear = new CascadeOp(reference.JsonPointer, false, reference, index);
            var reason = TryOps(referrer, [.. ops, clear], narrow, out widening);
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

    /// <summary>
    /// An approval that delete-dependents removes (signoff-design.md section 9): a scenario step of a process whose gated transition's event it
    /// raises drops its <c>signature</c> when that named one of the approval's signatures, and its <c>response</c> when no other approval of the
    /// process is left on that event to answer. The gate is one on a transition, or a library's gate policy at each transition that uses it.
    /// </summary>
    private void DropAnswers(int index, string target)
    {
        if (!_snapshot.TryGetEntry(target, out var entry) || _snapshot.Get<Process>(entry.OwnerId) is not { } owner
            || !JsonPointer.TryParse(entry.JsonPointer, out var segments))
            return;
        List<(string Process, string Event)> moves = [];
        ProcessGate? removed = null;
        if (segments is ["transitions", var at, "gate"] && JsonPointer.TryIndex(at, out var t) && t < owner.Transitions.Count
            && owner.Transitions[t].Event is { } gated)
        {
            removed = owner.Transitions[t].Gate;
            moves.Add((owner.Id, gated));
        }
        else if (segments is ["gates", var g])
        {
            if (JsonPointer.TryIndex(g, out var gi) && gi < owner.Gates.Count)
                removed = owner.Gates[gi];
            foreach (var use in _snapshot.ReferencesTo(target).Where(r => r.Field == "gatePolicy"))
            {
                if (_snapshot.Get<Process>(use.FromElementId) is { } user && JsonPointer.TryParse(use.JsonPointer, out var parts)
                    && parts is ["transitions", var i, "gatePolicy"] && JsonPointer.TryIndex(i, out var u) && u < user.Transitions.Count
                    && user.Transitions[u].Event is { } named)
                    moves.Add((user.Id, named));
            }
        }

        var signatures = (removed?.AllSignatures ?? []).Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
        bool Gone(string id) => string.Equals(id, target, StringComparison.Ordinal) || _cascadeDeleted.ContainsKey(id) || _deleted.Contains(id);
        foreach (var (process, raised) in moves.Distinct())
        {
            // Another approval of the process on the same event, after the delete, still takes a response.
            var answered = _snapshot.Get<Process>(process) is { } p && p.Transitions.Any(tr => string.Equals(tr.Event, raised, StringComparison.Ordinal) && !Gone(tr.Id)
                && ApprovalRules.GateOf(_snapshot, tr) is { UsesStages: true } other && !Gone(other.Id));
            foreach (var scenarioId in _snapshot.ReferencesTo(process).Where(r => r.JsonPointer == "/process").Select(r => r.FromElementId).Distinct().Order(StringComparer.Ordinal))
            {
                if (_cascadeDeleted.ContainsKey(scenarioId) || _deleted.Contains(scenarioId) || _batchIds.Contains(scenarioId)
                    || _snapshot.Get<Scenario>(scenarioId) is not { } scenario || !TryGetCurrent(scenarioId, null, out var referrer))
                    continue;
                var ops = _cascadeOps.GetValueOrDefault(scenarioId) ?? [];
                List<CascadeOp> next = [.. ops];
                for (var k = 0; k < scenario.Steps.Count; k++)
                {
                    var step = scenario.Steps[k];
                    if (!string.Equals(step.Event, raised, StringComparison.Ordinal))
                        continue;
                    foreach (var key in new[] { "response", "signature" })
                    {
                        if (key == "response" ? answered : step.Signature is not { } named || !signatures.Contains(named))
                            continue;
                        var pointer = FormattableString.Invariant($"/steps/{k}/{key}");
                        if (!JsonPointer.TryGet(referrer.Json, pointer, out _) || next.Any(o => o.Pointer == pointer || (o.Remove && IsUnder(pointer, o.Pointer))))
                            continue;
                        next.Add(new CascadeOp(pointer, true, new ReferenceInfo(scenarioId, step.Id, pointer, key, target), index));
                    }
                }

                if (next.Count > ops.Count && TryOps(referrer, next, true, out _) is null)
                    Commit(scenarioId, next);
            }
        }
    }

    /// <summary>
    /// The refused references that still stand: a scenario's subject refuses only while the scenario stays, and a later delete of the
    /// change may delete it (its process going, lifecycle-design.md decision 22).
    /// </summary>
    private IEnumerable<(int Index, string ElementId, ReferenceInfo Reference, string Why)> Refusals() =>
        _cascadeRefused.Where(r => !(r.Reference.JsonPointer == "/subject" && _cascadeDeleted.ContainsKey(r.ElementId)
            && _snapshot.GetDocument(r.ElementId)?.Element is Scenario)
            && !(IsRelativePart(r.Reference.JsonPointer) && _cascadeDeleted.ContainsKey(r.ElementId) && _snapshot.GetDocument(r.ElementId)?.Element is Actor));

    /// <summary>Whether a pointer is inside an actor's <c>relative</c> (its path, within or membersOf).</summary>
    private static bool IsRelativePart(string pointer) => pointer.StartsWith("/relative/", StringComparison.Ordinal);

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
            // A cleared value its folder follows (an operation's subject) moves the file, as a save would.
            Stage(referrerId, element, node, info, Relocate(current, element), outcome, current);
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
    /// and the schema; <see langword="null"/> when every declaring schema still holds. A custom element's <c>content</c> is checked
    /// against its custom kind's content schema, the top-level content key standing for the property.
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
            // A custom element's content (next-phase-2-design.md section 4.2, decision 11) is typed by its custom kind's content schema,
            // whose top-level key is the field; a value in a member is reached through the member arrays' item schemas below it.
            var custom = segments.Length > 1 && segments[0] == "content" ? element as Custom : null;
            var at = Array.FindIndex(segments, segment => segment == "properties");
            var isDefault = segments.Length > 1 && segments[0] == "defaultProperties";
            if (isDefault || custom is not null)
                at = 0;
            if (at < 0 || at + 1 >= segments.Length)
                continue;
            var hostPointer = JsonPointer.Build(segments[..at]);
            var field = segments[at + 1];
            var rest = segments[(at + 2)..];
            IEnumerable<PropertySchemaSource> sources;
            if (custom is not null)
            {
                sources = _snapshot.PropertySchemas.ContentOf(custom.Type) is { } content ? [content] : [];
            }
            else if (isDefault)
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

    /// <summary>A table's or an operation's <c>redefines</c> entry a reference at <c>/redefines/&lt;n&gt;/member</c> sits in, else <see langword="null"/>.</summary>
    private static string? RedefinesEntry(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments is ["redefines", _, "member"] ? JsonPointer.Build(segments[..2]) : null;

    /// <summary>
    /// An operation member's form (<c>/input/&lt;n&gt;/type</c> or <c>keyOf</c>, the same under <c>/returns/members/</c>) or a
    /// returns' <c>entity</c>: the one form its part has, so clearing it is never enough.
    /// </summary>
    private static bool IsOperationForm(string pointer) =>
        pointer.EndsWith("/returns/entity", StringComparison.Ordinal)
        || ((pointer.EndsWith("/type", StringComparison.Ordinal) || pointer.EndsWith("/keyOf", StringComparison.Ordinal))
            && (pointer.StartsWith("/input/", StringComparison.Ordinal) || pointer.Contains("/returns/members/", StringComparison.Ordinal)));

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

    /// <summary>
    /// Where a pointer is in a relation mapping's <c>ends</c> entry (several-types-design.md decision 38): the entry (<c>/ends/0</c>), the entry's
    /// member (<c>foreignKey</c>, <c>idColumn</c>, <c>typeColumn</c>, <c>referencesColumn</c>, <c>foreignKeys</c> or <c>types</c>), the list item
    /// for the two lists (<c>/ends/0/types/1</c>), and a <c>types</c> item's member; <see langword="null"/> for any other pointer.
    /// </summary>
    private static (string Entry, string Member, string? Item, string? ItemMember)? EndsEntryPart(string pointer)
    {
        if (!pointer.StartsWith("/ends/", StringComparison.Ordinal) || !JsonPointer.TryParse(pointer, out var s) || s.Length < 3 || !JsonPointer.TryIndex(s[1], out _))
            return null;
        var entry = JsonPointer.Build(s[..2]);
        return s[2] switch
        {
            "foreignKey" or "idColumn" or "typeColumn" or "referencesColumn" when s.Length == 3 => (entry, s[2], null, null),
            "foreignKeys" when s.Length == 4 && JsonPointer.TryIndex(s[3], out _) => (entry, s[2], JsonPointer.Build(s[..4]), null),
            "types" when s.Length == 5 && JsonPointer.TryIndex(s[3], out _) => (entry, s[2], JsonPointer.Build(s[..4]), s[4]),
            _ => null,
        };
    }

    /// <summary>
    /// Whether a relation mapping's <c>ends</c> entry still names a way once the ops are applied (several-types-design.md decision 38): its
    /// <c>foreignKey</c>, an item of <c>foreignKeys</c>, a <c>types</c> item with its <c>foreignKey</c>, or its <c>idColumn</c>.
    /// </summary>
    private static bool EntryKeepsWay(JsonElement json, string entry, IReadOnlyList<CascadeOp> ops)
    {
        if (!JsonPointer.TryGet(json, entry, out var e) || e.ValueKind != JsonValueKind.Object)
            return false;
        bool Kept(string pointer) => !ops.Any(o => o.Pointer == pointer || IsUnder(pointer, o.Pointer));
        if (e.TryGetProperty("foreignKey", out _) && Kept(entry + "/foreignKey") || e.TryGetProperty("idColumn", out _) && Kept(entry + "/idColumn"))
            return true;
        if (e.TryGetProperty("foreignKeys", out var keys) && keys.ValueKind == JsonValueKind.Array
            && Enumerable.Range(0, keys.GetArrayLength()).Any(k => Kept(entry + "/foreignKeys/" + k.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            return true;
        return e.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array
            && types.EnumerateArray().Select((item, k) => (item, at: entry + "/types/" + k.ToString(System.Globalization.CultureInfo.InvariantCulture)))
                .Any(t => t.item.ValueKind == JsonValueKind.Object && t.item.TryGetProperty("foreignKey", out _) && Kept(t.at) && Kept(t.at + "/foreignKey"));
    }

    /// <summary>
    /// The <c>types</c> entry of a relation end (<c>/ends/1/types/0</c>) whose <c>entity</c> or <c>stereotype</c> a pointer is, with its list
    /// (<c>/ends/1/types</c>), several-types-design.md decision 21; <see langword="null"/> for any other pointer.
    /// </summary>
    private static (string List, string Item)? EndTypeEntry(string pointer) =>
        pointer.StartsWith("/ends/", StringComparison.Ordinal) && JsonPointer.TryParse(pointer, out var segments) && segments.Length == 5
            && segments[2] == "types" && segments[4] is "entity" or "stereotype" && JsonPointer.TryIndex(segments[1], out _) && JsonPointer.TryIndex(segments[3], out _)
            ? (JsonPointer.Build(segments[..3]), JsonPointer.Build(segments[..4]))
            : null;

    /// <summary>The pointer of an entity's revisions block.</summary>
    private const string RevisionsBlock = "/revisions";

    /// <summary>
    /// Where a reference sits in an entity's revisions block (revisions-design.md section 10, "Deletes"): for an entry's <c>lifecycle</c>, state
    /// (<c>onRevise</c>), <c>attribute</c> or <c>end</c>, its list (<c>/revisions/attributes</c>) and the entry (<c>/revisions/attributes/0</c>);
    /// for <c>boundAttribute</c>, both <see langword="null"/>. <see langword="null"/> for any other pointer.
    /// </summary>
    private static (string? List, string? Entry)? RevisionsPart(string pointer)
    {
        if (!pointer.StartsWith(RevisionsBlock + "/", StringComparison.Ordinal) || !JsonPointer.TryParse(pointer, out var segments))
            return null;
        if (segments is ["revisions", "boundAttribute"])
            return (null, null);
        return segments is ["revisions", "lifecycles" or "attributes" or "links", var at, "lifecycle" or "onRevise" or "attribute" or "end"]
            && JsonPointer.TryIndex(at, out _)
            ? (JsonPointer.Build(segments[..2]), JsonPointer.Build(segments[..3]))
            : null;
    }

    /// <summary>
    /// The edits a reference's loss makes to a revisions block, on top of those planned: <c>boundAttribute</c> is cleared; an entry goes, or its
    /// list with its last entry; and when no key of the block is left, the block goes instead.
    /// </summary>
    private static List<CascadeOp> RevisionsOps(JsonElement json, (string? List, string? Entry) part, ReferenceInfo reference, int index, List<CascadeOp> ops)
    {
        var (pointer, remove) = part is { List: { } list, Entry: { } entry }
            ? (Remaining(json, list, ops) <= 1 ? list : entry, true)
            : (reference.JsonPointer, false);
        List<CascadeOp> next = [.. ops.Where(o => o.Pointer != pointer && !IsUnder(o.Pointer, pointer)), new CascadeOp(pointer, remove, reference, index)];
        if (Remaining(json, RevisionsBlock, next) <= 0)
            next = [.. next.Where(o => o.Pointer != RevisionsBlock && !IsUnder(o.Pointer, RevisionsBlock)), new CascadeOp(RevisionsBlock, true, reference, index)];
        return next;
    }

    /// <summary>
    /// How a delete plan words a removed part of an entity's revisions block: the block, a list, or an entry by what it names (the lifecycle,
    /// attribute or end); <see langword="null"/> for any other pointer.
    /// </summary>
    private string? RevisionsWhat(JsonElement json, string pointer)
    {
        if (pointer == RevisionsBlock)
            return "revisions block";
        if (!pointer.StartsWith(RevisionsBlock + "/", StringComparison.Ordinal) || !JsonPointer.TryParse(pointer, out var segments))
            return null;
        var key = segments.Length > 1 ? segments[1] switch { "lifecycles" => "lifecycle", "attributes" => "attribute", "links" => "end", _ => null } : null;
        if (key is null)
            return null;
        if (segments.Length == 2)
            return "revisions " + segments[1];
        return segments.Length == 3 && JsonPointer.TryGet(json, pointer + "/" + key, out var named) && named.ValueKind == JsonValueKind.String
            ? $"revisions entry for {key} {ShortName(named.GetString()!)}"
            : null;
    }

    /// <summary>The <c>subjects</c> entry (<c>/subjects/2</c>) whose <c>entity</c> or <c>stereotype</c> (stereotype-sets-design.md decision 11) a
    /// pointer is; <see langword="null"/> for any other pointer.</summary>
    private static string? SubjectEntry(string pointer) =>
        pointer.StartsWith("/subjects/", StringComparison.Ordinal) && JsonPointer.TryParse(pointer, out var segments) && segments.Length == 3
            && segments[2] is "entity" or "stereotype" && JsonPointer.TryIndex(segments[1], out _)
            ? JsonPointer.Build(segments[..2])
            : null;

    /// <summary>
    /// The part of a scenario a reference's loss drops (lifecycle-design.md decision 22): the object (<c>/objects/2</c>) whose
    /// <c>entity</c> a pointer is, or the link (<c>/objects/2/links/0</c>) whose <c>object</c> or <c>end</c> (its relation going) it is;
    /// or a participant's holding (<c>/participants/0/holds/1</c>) whose <c>actor</c> or <c>within</c> it is (actors-design.md section 7:
    /// the holding goes as an actor goes from a list, and dropping only within would widen it to every context); <see langword="null"/> for
    /// any other pointer.
    /// </summary>
    private static string? ScenarioObjectPart(string pointer)
    {
        if (pointer.StartsWith("/participants/", StringComparison.Ordinal) && JsonPointer.TryParse(pointer, out var at) && at.Length == 5
            && JsonPointer.TryIndex(at[1], out _) && at[2] == "holds" && JsonPointer.TryIndex(at[3], out _) && at[4] is "actor" or "within")
            return JsonPointer.Build(at[..4]);
        if (!pointer.StartsWith("/objects/", StringComparison.Ordinal) || !JsonPointer.TryParse(pointer, out var segments)
            || !JsonPointer.TryIndex(segments[1], out _))
            return null;
        if (segments.Length == 3 && segments[2] == "entity")
            return JsonPointer.Build(segments[..2]);
        if (segments.Length == 5 && segments[2] == "links" && JsonPointer.TryIndex(segments[3], out _) && segments[4] is "object" or "end")
            return JsonPointer.Build(segments[..4]);
        return null;
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

    /// <summary>
    /// Where a reference sits in a right (rights-design.md section 8): <c>List</c> is the actors, states, ends or attributes list it is an item of
    /// (<c>/rights/0/actors</c> in a lifecycle, <c>/actors</c> in a named right), or <see langword="null"/> for a use's <c>right</c>; <c>Entry</c>
    /// is the lifecycle's entry (<c>/rights/0</c>), or <c>""</c> for the named right itself. <see langword="null"/> for any other pointer,
    /// <c>except</c> included.
    /// </summary>
    private static (string? List, string Entry)? RightPart(Element element, string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var segments))
            return null;
        if (element is Process && segments.Length >= 3 && segments[0] == "rights" && JsonPointer.TryIndex(segments[1], out _))
        {
            var entry = JsonPointer.Build(segments[..2]);
            if (segments.Length == 3 && segments[2] == "right")
                return (null, entry);
            if (segments.Length == 4 && segments[2] is "actors" or "states" or "ends" or "attributes" && JsonPointer.TryIndex(segments[3], out _))
                return (JsonPointer.Build(segments[..3]), entry);
        }

        if (element is Right && segments.Length == 2 && segments[0] is "actors" or "ends" or "attributes" && JsonPointer.TryIndex(segments[1], out _))
            return ("/" + segments[0], "");
        return null;
    }

    /// <summary>
    /// Where a reference sits in a lifecycle's trigger point (trigger-points-design.md section 9): <c>List</c> is the states, targets,
    /// transitions, entities, attributes or ends list it is an item of (<c>/triggerPoints/0/targets</c>), and <c>Entry</c> the trigger point
    /// (<c>/triggerPoints/0</c>). <see langword="null"/> for any other pointer: its verbs, its sets and its expressions go their own ways.
    /// </summary>
    private static (string List, string Entry)? TriggerPointPart(string pointer) =>
        pointer.StartsWith("/triggerPoints/", StringComparison.Ordinal) && JsonPointer.TryParse(pointer, out var segments) && segments.Length == 4
            && JsonPointer.TryIndex(segments[1], out _) && JsonPointer.TryIndex(segments[3], out _)
            && segments[2] is "states" or "targets" or "transitions" or "entities" or "attributes" or "ends"
            ? (JsonPointer.Build(segments[..3]), JsonPointer.Build(segments[..2]))
            : null;

    /// <summary>
    /// Where a reference sits in a named trigger point (trigger-points-design.md section 9, part 2): <c>List</c> is its subjects, exceptEntities,
    /// entities, attributes or ends (<c>/subjects</c>), and <c>Item</c> the entry (<c>/subjects/0</c>). <see langword="null"/> for any other
    /// pointer: its package, its verbs, its sets and its expressions go their own ways.
    /// </summary>
    private static (string List, string Item)? NamedTriggerPart(string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length < 2 || !JsonPointer.TryIndex(segments[1], out _))
            return null;
        if (segments.Length == 3 && segments[0] == "subjects" && segments[2] is "entity" or "stereotype")
            return ("/subjects", JsonPointer.Build(segments[..2]));
        return segments.Length == 2 && segments[0] is "exceptEntities" or "entities" or "attributes" or "ends"
            ? ("/" + segments[0], JsonPointer.Build(segments))
            : null;
    }

    /// <summary>
    /// What holds a pointer of a named trigger point: <c>""</c>, the point, for its <c>when</c> or condition; its set (<c>/sets/1</c>) for the
    /// set's <paramref name="member"/> (<c>value</c> or <c>attribute</c>); <see langword="null"/> otherwise.
    /// </summary>
    private static string? NamedTriggerHolder(string pointer, string member)
    {
        if (!JsonPointer.TryParse(pointer, out var segments) || segments.Length == 0)
            return null;
        if (member == "value" && segments.Length > 1 && segments[0] is "when" or "condition")
            return "";
        return segments.Length >= 3 && segments[0] == "sets" && JsonPointer.TryIndex(segments[1], out _) && segments[2] == member
            && (member == "value" ? segments.Length > 3 : segments.Length == 3)
            ? JsonPointer.Build(segments[..2])
            : null;
    }

    /// <summary>The items a list holds, or the keys a map holds, once the planned edits that drop one of them are applied.</summary>
    private static int Remaining(JsonElement json, string list, IReadOnlyList<CascadeOp> ops)
    {
        if (!JsonPointer.TryGet(json, list, out var items) || items.ValueKind is not (JsonValueKind.Array or JsonValueKind.Object))
            return 0;
        var dropped = ops.Select(o => o.Pointer).Where(p => IsUnder(p, list) && p.IndexOf('/', list.Length + 1) < 0).Distinct(StringComparer.Ordinal).Count();
        return (items.ValueKind == JsonValueKind.Array ? items.GetArrayLength() : items.EnumerateObject().Count()) - dropped;
    }

    /// <summary>
    /// The entry of a named signature's <c>notBy</c> a pointer names a verb or a transition in (<c>…/signatures/1/notBy/0/verb</c>), with its
    /// list (<c>…/signatures/1/notBy</c>), on an inline gate or a gate policy (signoff-design.md section 9); <see langword="null"/> for any
    /// other pointer.
    /// </summary>
    private static (string List, string Entry)? NotByPart(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments.Length >= 6 && segments[^1] is "verb" or "transition"
        && JsonPointer.TryIndex(segments[^2], out _) && segments[^3] == "notBy" && segments[^5] == "signatures"
            ? (JsonPointer.Build(segments[..^2]), JsonPointer.Build(segments[..^1]))
            : null;

    /// <summary>
    /// The map a key of a step's <c>expect.signatures</c> is in (<c>/steps/0/expect/signatures</c>, signoff-design.md section 9);
    /// <see langword="null"/> for any other pointer.
    /// </summary>
    private static (string Map, string Key)? ExpectedSignature(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments.Length == 5 && segments[0] == "steps" && JsonPointer.TryIndex(segments[1], out _)
        && segments[2] == "expect" && segments[3] == "signatures"
            ? (JsonPointer.Build(segments[..4]), pointer)
            : null;

    /// <summary>Whether a part is a named signature's <c>when</c> (<c>…/signatures/1/when</c>, signoff-design.md section 9).</summary>
    private static bool SignatureCondition(string part) =>
        JsonPointer.TryParse(part, out var segments) && segments.Length >= 3 && segments[^1] == "when" && segments[^3] == "signatures";

    /// <summary>Whether a part is a stage's <c>when</c> (<c>…/stages/1/when</c>, signoff-design.md section 9, part 2).</summary>
    private static bool StageCondition(string part) =>
        JsonPointer.TryParse(part, out var segments) && segments.Length >= 3 && segments[^1] == "when" && segments[^3] == "stages";

    /// <summary>
    /// The list of a named signature's <c>delegateTo</c> or <c>escalateTo</c> an actor entry is in (<c>…/signatures/1/delegateTo/0</c>, with its
    /// list <c>…/signatures/1/delegateTo</c>; signoff-design.md section 9, part 2); <see langword="null"/> for any other pointer.
    /// </summary>
    private static (string List, string Entry)? NoticeActorPart(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments.Length >= 5 && JsonPointer.TryIndex(segments[^1], out _)
        && segments[^2] is "delegateTo" or "escalateTo" && segments[^4] == "signatures"
            ? (JsonPointer.Build(segments[..^1]), pointer)
            : null;

    /// <summary>
    /// The entry of a step's <c>expect.notices</c> a pointer names a signature in (<c>/steps/0/expect/notices/1/signature</c>, with its list
    /// <c>/steps/0/expect/notices</c>; signoff-design.md section 9, part 2); <see langword="null"/> for any other pointer.
    /// </summary>
    private static (string List, string Entry)? ExpectedNotice(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments.Length == 6 && segments[0] == "steps" && JsonPointer.TryIndex(segments[1], out _)
        && segments[2] == "expect" && segments[3] == "notices" && JsonPointer.TryIndex(segments[4], out _) && segments[5] == "signature"
            ? (JsonPointer.Build(segments[..4]), JsonPointer.Build(segments[..5]))
            : null;

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

    /// <summary>
    /// The part that holds the expression tree a pointer is inside: a check, guard or inline right (<c>/checks/0</c>, <c>/guards/2</c>,
    /// <c>/rights/1</c>, rights-design.md section 8), a trigger point for its <c>when</c> or condition (<c>/triggerPoints/0</c>,
    /// trigger-points-design.md section 9), a named signature's <c>when</c> itself (<c>…/signatures/1/when</c>, signoff-design.md section 9),
    /// a derived value (<c>/attributes/1/derived</c>), an action's or a trigger point's set
    /// (<c>/actions/0/sets/1</c>, its value, object-effects-design.md section 8), or <c>""</c> for a constraint's condition, which needs the
    /// whole element; <see langword="null"/> when the pointer is in no tree.
    /// </summary>
    private static string? ExpressionHolder(string pointer)
    {
        if (!JsonPointer.TryParse(pointer, out var segments))
            return null;
        for (var k = 0; k < segments.Length - 1; k++)
        {
            if (segments[k] == "condition" && k == 0)
                return "";
            if (segments[k] == "condition" && k >= 2 && segments[k - 2] is "checks" or "guards" or "rights" or "triggerPoints")
                return JsonPointer.Build(segments[..k]);
            if (segments[k] == "when" && k == 2 && segments[0] == "triggerPoints")
                return JsonPointer.Build(segments[..k]);
            // A named signature's when goes alone (signoff-design.md section 9): the signature is then always needed, the safe choice decision
            // 7 makes when a condition cannot be judged. A stage's when goes alone too, and the stage then always runs (decision 6).
            if (segments[k] == "when" && k >= 2 && segments[k - 2] is "signatures" or "stages")
                return JsonPointer.Build(segments[..(k + 1)]);
            if (segments[k] == "value" && k >= 1 && segments[k - 1] == "derived")
                return JsonPointer.Build(segments[..k]);
            if (SetEntry(segments, k) is { } set)
                return set;
            if (CallInputEntry(segments, k) is { } input)
                return input;
        }

        return null;
    }

    /// <summary>
    /// The call's input (<c>/actions/0/call/inputs/1</c>) when the segment at <paramref name="k"/> is its <c>value</c>
    /// (processes-operations-design.md decision 6); <see langword="null"/> otherwise.
    /// </summary>
    private static string? CallInputEntry(string[] segments, int k) =>
        k == 5 && segments[k] == "value" && segments[0] == "actions" && JsonPointer.TryIndex(segments[1], out _) && segments[2] == "call"
            && segments[3] == "inputs" && JsonPointer.TryIndex(segments[4], out _)
            ? JsonPointer.Build(segments[..5])
            : null;

    /// <summary>Whether a part is a call's input (<c>/actions/0/call/inputs/1</c>).</summary>
    private static bool CallInputHolder(string part) =>
        JsonPointer.TryParse(part, out var segments) && segments.Length == 5 && CallInputEntry([.. segments, "value"], 5) is not null;

    /// <summary>
    /// What goes when a call's reference does (processes-operations-design.md section 11): the call (<c>/actions/0/call</c>) for its
    /// <c>operation</c>, the input (<c>/actions/0/call/inputs/1</c>) for an input's <c>member</c>; <see langword="null"/> for any other pointer.
    /// </summary>
    private static string? CallPart(string pointer)
    {
        if (!pointer.StartsWith("/actions/", StringComparison.Ordinal) || !JsonPointer.TryParse(pointer, out var segments)
            || segments.Length < 4 || !JsonPointer.TryIndex(segments[1], out _) || segments[2] != "call")
        {
            return null;
        }

        if (segments.Length == 4 && segments[3] == "operation")
            return JsonPointer.Build(segments[..3]);
        return segments.Length == 6 && segments[3] == "inputs" && JsonPointer.TryIndex(segments[4], out _) && segments[5] == "member"
            ? JsonPointer.Build(segments[..5])
            : null;
    }

    /// <summary>
    /// The set (<c>/actions/0/sets/1</c>, or a trigger point's <c>/triggerPoints/0/sets/1</c>) when the segment at <paramref name="k"/> is its
    /// <c>attribute</c> or <c>value</c> (object-effects-design.md decision 1, trigger-points-design.md decision 3); <see langword="null"/>
    /// otherwise.
    /// </summary>
    private static string? SetEntry(string[] segments, int k) =>
        k == 4 && segments[0] is "actions" or "triggerPoints" && JsonPointer.TryIndex(segments[1], out _) && segments[2] == "sets"
            && JsonPointer.TryIndex(segments[3], out _)
            ? JsonPointer.Build(segments[..4])
            : null;

    /// <summary>Whether a part is an action's or a trigger point's set (<c>/actions/0/sets/1</c>, <c>/triggerPoints/0/sets/1</c>).</summary>
    private static bool SetHolder(string part) =>
        JsonPointer.TryParse(part, out var segments) && segments.Length == 4 && SetEntry([.. segments, "value"], 4) is not null;

    /// <summary>The set whose <c>attribute</c> a pointer is (<c>/actions/0/sets/1/attribute</c>, <c>/triggerPoints/0/sets/1/attribute</c>);
    /// <see langword="null"/> for any other pointer.</summary>
    private static string? SetOfAttribute(string pointer) =>
        (pointer.StartsWith("/actions/", StringComparison.Ordinal) || pointer.StartsWith("/triggerPoints/", StringComparison.Ordinal))
            && JsonPointer.TryParse(pointer, out var segments) && segments.Length == 5
            && segments[4] == "attribute"
            ? SetEntry(segments, 4)
            : null;

    /// <summary>Whether a pointer is an entry of a query's <c>reads</c> or <c>writes</c> (<c>/reads/0</c>).</summary>
    private static bool SqlTextEntry(string pointer) =>
        JsonPointer.TryParse(pointer, out var segments) && segments is ["reads" or "writes", var at] && JsonPointer.TryIndex(at, out _);

    private static bool IsUnder(string pointer, string ancestor) =>
        pointer.Length > ancestor.Length && pointer[ancestor.Length] == '/' && pointer.StartsWith(ancestor, StringComparison.Ordinal);

    /// <summary>Stages what <see cref="CascadeDelete"/> planned, after every operation of the batch ran.</summary>
    private void FinishCascade(IReadOnlyList<ChangeOutcome> outcomes)
    {
        if (_cascadeOrder.Count == 0)
            return;
        PlanCascadePackValues(outcomes);
        CascadeReport = BuildReport();
        foreach (var group in Refusals().GroupBy(r => (r.Index, r.ElementId)).OrderBy(g => g.Key.Index).ThenBy(g => g.Key.ElementId, StringComparer.Ordinal))
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
            // A cleared value its folder follows (an operation's subject) moves the file, as a save would.
            Stage(referrerId, element, node, info, Relocate(referrer, element), outcome, referrer);
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
        var warnings = new List<DeletePlanWarning>();
        foreach (var (referrerId, ops) in _cascadeOps.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var document = _snapshot.GetDocument(referrerId)!;
            foreach (var op in ops.OrderBy(o => o.Pointer, PointerOrder.Instance))
            {
                if (op.Remove && document.Element is Query { IsSqlText: true } && SqlTextEntry(op.Pointer))
                {
                    // The query stays; its text may still name the object (sql-queries-design.md decision 17).
                    var list = op.Pointer[1..op.Pointer.IndexOf('/', 1)];
                    removes.Add(new DeletePlanRemove(referrerId, document.Element.KindName, NameOf(document.Element), op.Pointer,
                        list + " entry " + ShortName(op.Reference.ToId), null, null, "names " + Label(op.Reference.ToId) + "; the query and its SQL text stay"));
                    warnings.Add(new DeletePlanWarning(
                        $"Query {NameOf(document.Element)}'s SQL text may still name {Label(op.Reference.ToId)}; check the text.", null, null));
                }
                else if (op.Remove)
                {
                    var sub = DocumentReader.Scan(document.Json).Ids.Where(x => x.Pointer == op.Pointer).Select(x => x.Id).FirstOrDefault();
                    var subKind = sub is not null && _snapshot.TryGetEntry(sub, out var entry) ? entry.Kind : null;
                    var because = document.Element.Kind is ElementKind.Entity or ElementKind.ReferenceType && op.Pointer.StartsWith("/bindings/", StringComparison.Ordinal)
                        ? (subKind == "binding" ? "the binding" : "this entry of its binding") + " needs " + Label(op.Reference.ToId)
                            + (document.Element.Kind == ElementKind.Entity ? "; the entity stays" : "; the reference type stays")
                        : TriggerPointPart(op.Reference.JsonPointer) is { } point && point.Entry == op.Pointer
                            // The last entry of a narrowing list takes its trigger point with it (trigger-points-design.md section 9).
                            ? "names no other " + Noun(point.List[(point.List.LastIndexOf('/') + 1)..]) + " than " + Label(op.Reference.ToId)
                        // A revisions list or block left with nothing goes whole (revisions-design.md section 10, "Deletes").
                        : document.Element.Kind == ElementKind.Entity && (op.Pointer == RevisionsBlock
                            || (op.Pointer.StartsWith(RevisionsBlock + "/", StringComparison.Ordinal) && op.Pointer.Count(c => c == '/') == 2))
                            ? "is left empty without " + Label(op.Reference.ToId)
                            : "needs " + Label(op.Reference.ToId);
                    var what = document.Element.Kind == ElementKind.Entity ? RevisionsWhat(document.Json, op.Pointer) : null;
                    removes.Add(new DeletePlanRemove(referrerId, document.Element.KindName, NameOf(document.Element), op.Pointer,
                        what ?? Part(document.Json, op.Pointer, subKind, ShortName(op.Reference.ToId)), sub, subKind, because));
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
        var refused = Refusals()
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
        return new DeletePlanReport(roots, deletes, clears, removes, refused, settings, [.. warnings.Distinct()]);
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
        return $"{PartNoun(entry.Kind)} {SubName(document.Json, entry.JsonPointer) ?? id} of {owner}";
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
        var noun = subKind is not null ? PartNoun(subKind) : Noun(item ? segments[^2] : last);
        var name = SubName(json, pointer) ?? (item ? fallback : null);
        return name is null ? noun : noun + " " + name;
    }

    /// <summary>A part kind as a delete plan words it: an entry of a lifecycle's rights is a right (rights-design.md section 8), an entry
    /// of its trigger points a trigger point (trigger-points-design.md section 9), and an approval's parts a stage and a signature
    /// (signoff-design.md section 9).</summary>
    private static string PartNoun(string kind) => kind switch
    {
        "process-right" => "right",
        "process-trigger-point" => "trigger point",
        "gate-stage" => "stage",
        "named-signature" => "signature",
        "call-input" => "call input",
        _ => kind,
    };

    private static string Noun(string property) => property switch
    {
        "rights" => "right",
        "triggerPoints" => "trigger point",
        "stages" => "stage",
        "signatures" => "signature",
        "notBy" => "not by entry",
        "delegateTo" => "delegate to entry",
        "escalateTo" => "escalate to entry",
        "escalateAfter" => "escalation delay (escalateAfter)",
        "notices" => "expected notice",
        "targets" => "target",
        "entities" => "entity",
        "subjects" => "subject",
        "actors" => "actor",
        "verbs" => "verb",
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
        "objects" => "object",
        "links" => "link",
        "participants" => "participant",
        "holds" => "holding",
        "sets" => "set",
        "when" => "condition (when)",
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
/// <param name="Warnings">What the deletes leave that may stop working (a SQL text that may still name a deleted table).</param>
internal sealed record DeletePlanReport(
    IReadOnlyList<string> Roots,
    IReadOnlyList<DeletePlanDelete> Deletes,
    IReadOnlyList<DeletePlanClear> Clears,
    IReadOnlyList<DeletePlanRemove> Removes,
    IReadOnlyList<DeletePlanRefusal> Refused,
    IReadOnlyList<DeletePlanSetting> Settings,
    IReadOnlyList<DeletePlanWarning>? Warnings = null);
