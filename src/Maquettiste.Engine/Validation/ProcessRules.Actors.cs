using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>The rules on actors worked out from the object and subject owners (actors-design.md decisions 8 to 12, section 5).</summary>
internal static partial class ProcessRules
{
    // MQ9603: what an actor's relative gets wrong by itself.
    private static void CheckRelative(ModelSnapshot model, Actor actor, Report report)
    {
        if (actor.Relative is not { } relative)
            return;
        var forms = relative.Forms;
        if (forms.Count == 0)
            report.Add("MQ9603", $"Actor '{actor.Name}' is worked out from the object but its relative gives no form; give one of owner, path, or membersOf (with within).", "/relative");
        else if (forms.Count > 1)
            report.Add("MQ9603", $"Actor '{actor.Name}' gives more than one form in its relative ({Names(forms)}); keep exactly one.", "/relative");
        if (relative.Within.Count > 0 && relative.MembersOf is null)
            report.Add("MQ9603", $"Actor '{actor.Name}' gives within without membersOf; within says where the members of membersOf are held. Add membersOf, or remove within.", "/relative/within");
        if (relative.MembersOf is { } holderId && model.Get<Actor>(holderId) is { } holder) // else MQ2001, MQ2002
        {
            if (holder.Relative is not null)
                report.Add("MQ9603", $"Actor '{actor.Name}' is the members of '{holder.Name}', which is itself worked out from the object; name a role, group or organization that stands for itself.", "/relative/membersOf");
            else if (!ActorTypes.HoldsMembers(holder.Type))
                report.Add("MQ9603", $"Actor '{actor.Name}' is the members of '{holder.Name}', which is {TypeText(holder.Type)} and has no members; name a role, group or organization.", "/relative/membersOf");
        }

        if (actor.MemberOf.Count > 0)
            report.Add("MQ9603", $"Actor '{actor.Name}' is worked out from the object, so it cannot be a member: who it is changes with each object. Remove memberOf.", "/memberOf");
        if (actor.Type == ActorType.System)
            report.Add("MQ9603", $"Actor '{actor.Name}' is the system itself (type system), which is never worked out from the object; remove relative, or change the type.", "/relative");
    }

    // MQ9606: a subject entry's owner names a single-valued attribute (own or inherited) or a to-one end of its entity. An attribute from a
    // stereotype waits for the stereotype as a set of types (actors-design.md decision 19), as guard conditions do.
    private static void CheckSubjectOwners(ValidationContext context, Process process, Report report)
    {
        var model = context.Model;
        var entries = process.SubjectList ?? [];
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Owner is not { } owner || model.Get<Entity>(entries[i].Entity) is not { } entity || !model.TryGetEntry(owner, out _))
                continue; // MQ2001, MQ2002
            var pointer = "/subjects/" + N(i) + "/owner";
            string? why = null;
            if (context.Flatten(entity).FirstOrDefault(f => string.Equals(f.Attribute.Id, owner, StringComparison.Ordinal)) is { } flat)
            {
                if (flat.Source == AttributeSource.Virtual)
                    why = $"is attribute '{flat.Attribute.Name}', which comes from stereotype '{flat.From}'; an owner field from a stereotype can't be read until a stereotype can stand for a set of types";
                else if (flat.Attribute.Collection)
                    why = $"is attribute '{flat.Attribute.Name}', which holds many values";
                else if (flat.Attribute.Type.Ref is { } typeId && model.Get<ValueObject>(typeId) is { } valueObject)
                    why = $"is attribute '{flat.Attribute.Name}', which holds value object '{valueObject.Name}'";
            }
            else if (RelativeActors.OwnerEnd(model, entity, owner) is { } end)
            {
                if (end.Max != MaxCardinality.One)
                    why = $"is relation end '{(end.Navigation.Length > 0 ? end.Navigation : end.Id)}', which leads to many objects";
            }
            else
            {
                why = "is not an attribute or a navigable relation end of the entity or its bases";
            }

            if (why is not null)
                report.Add("MQ9606", $"The owner of subject {SubjectLabel(model, entries[i])} of process '{process.Name}' {why}; name a single-valued attribute or a to-one end that holds one actor, or clear the owner.", pointer, entries[i].Id);
        }
    }

    // MQ9604: each use of an actor from the object in a process that runs (a library's gates are checked at each process that uses them).
    private static void CheckRelativeUses(ValidationContext context, Process process, ProcessAnalysis analysis, Report report)
    {
        var model = context.Model;
        if (!model.All<Actor>().Any(a => a.Relative is not null))
            return;
        var reasons = new Dictionary<string, string?>(StringComparer.Ordinal);
        string? Why(string id)
        {
            if (!reasons.TryGetValue(id, out var why))
                reasons[id] = why = model.Get<Actor>(id) is { Relative: { } relative } ? RelativeActors.Resolve(model, process, relative).Why : null;
            return why;
        }

        void Check(IReadOnlyList<string> list, string pointer, string nodeId, string where, bool indexed = true)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var k = 0; k < list.Count; k++)
            {
                if (seen.Add(list[k]) && Why(list[k]) is { } why)
                {
                    report.Add("MQ9604", $"Actor '{model.Get<Actor>(list[k])!.Name}', named by {where}, {why}; fix the actor's path, or name another actor here.",
                        indexed ? pointer + "/" + N(k) : pointer, nodeId);
                }
            }
        }

        for (var i = 0; i < process.Events.Count; i++)
            Check(process.Events[i].Actors, "/events/" + N(i) + "/actors", process.Events[i].Id, $"event '{process.Events[i].Name}' of process '{process.Name}'");
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var t = process.Transitions[i];
            if (t.Gate is { } gate)
            {
                var where = $"gate '{gate.Name}' of process '{process.Name}'";
                Check(gate.Signers, "/transitions/" + N(i) + "/gate/signers", gate.Id, where);
                Check(gate.RequiredActors, "/transitions/" + N(i) + "/gate/requiredActors", gate.Id, where);
            }

            if (t.GatePolicy is { } policy && ProcessLibraries.Owner(model, policy, "gate") is { } library)
            {
                var used = library.Gates.First(g => string.Equals(g.Id, policy, StringComparison.Ordinal));
                Check([.. used.Signers.Concat(used.RequiredActors)], "/transitions/" + N(i) + "/gatePolicy", t.Id,
                    $"gate '{used.Name}' of library '{library.Name}', used by process '{process.Name}'", indexed: false);
            }
        }

        foreach (var node in analysis.States)
        {
            for (var j = 0; j < node.State.Invoke.Count; j++)
            {
                var invoke = node.State.Invoke[j];
                Check(invoke.Actors, node.Pointer + "/invoke/" + N(j) + "/actors", invoke.Id, $"invoke '{invoke.Name}' of process '{process.Name}'");
            }
        }
    }

    // Whether an actor from the object stands among a list's actors or is the actor, so no model rule can judge the list (MQ9105).
    private static bool IsRelative(ModelSnapshot model, string id) => model.Get<Actor>(id) is { Relative: not null };
}
