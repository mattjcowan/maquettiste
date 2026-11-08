using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// MQ9305 on a verb step's fields and on an expected refusal (rights-design.md decisions 25 and 26), judged before the step runs, as a
/// payload is: an object, link or own verb (moves, signatures and tasks are event and invoke-done steps); attributes only beside read;
/// assume words only for checks the team writes (trigger-points-design.md decision 26);
/// values only beside modify on the subject or create, of attributes a set could set and of their types; a link step's end of the
/// subject's entity and an object linked through it, except for connect; no delete on a link; and a refusal only beside
/// <c>accepted: false</c>. A step that is no verb step and expects no refusal costs two tests.
/// </summary>
internal static class VerbStepFields
{
    /// <summary>The first problem of a step's verb fields or expected refusal, or <see langword="null"/>.</summary>
    /// <param name="runtime">The replay's runtime.</param>
    /// <param name="process">The process the scenario runs.</param>
    /// <param name="cast">The scenario's objects, subject and participants.</param>
    /// <param name="step">The step.</param>
    /// <returns>The message, without the step's number, and the field it points at.</returns>
    public static (string Message, string Field)? Check(ProcessRuntime runtime, Process process, ScenarioCast cast, ScenarioStep step)
    {
        if (step.Expect is { Accepted: true } expect && (expect.Refusal is not null || expect.RefusedBy is not null))
        {
            return ("expect gives " + (expect.Refusal is not null ? "refusal" : "refusedBy") + " beside accepted true, but only a refused step has a refusal; "
                + "set accepted to false, or remove them.", expect.Refusal is not null ? "/expect/refusal" : "/expect/refusedBy");
        }

        if (CheckAssume(runtime.Model, process, cast, step) is { } assume)
            return assume;
        if (step.Input != StepInput.Verb)
            return null;
        var model = runtime.Model;
        if (step.Verb is not { } verb)
            return ("the verb step names no verb; give the verb the participant does.", "/verb");
        var builtIn = BuiltInVerbs.Get(verb);
        if (builtIn is null && model.OwnVerb(verb) is null)
            return ($"the verb step names '{verb}', which is neither a built-in verb nor one of the project's own; name an object, link or own verb.", "/verb");
        if (builtIn is { Group: "move" or "signature" or "task" })
        {
            return ($"the verb step names '{verb}', a {builtIn.Group} verb; a verb step does an object, link or own verb, and moves, signatures and tasks are "
                + "event and invoke-done steps.", "/verb");
        }

        if (step.Attributes is { Count: > 0 } && !string.Equals(verb, "read", StringComparison.Ordinal))
            return ($"the step gives attributes beside verb '{verb}'; only read names the attributes it reads, and modify names them in values. Remove them.", "/attributes");

        if (step.Values is { Count: > 0 } values)
        {
            // A create step gives the new object's values (trigger-points-design.md decision 27).
            if (verb is not ("modify" or "create"))
                return ($"the step gives values beside verb '{verb}'; only modify and create set values. Remove them.", "/values");
            if (step.End is not null)
                return ("the step gives values on a link, which holds none in a scenario; remove them, or modify the subject.", "/values");
            var held = ExprSites.HeldAttributes(model, process);
            var bound = model.All<Process>().SelectMany(p => p.Subjects).Select(s => s.BoundAttribute).OfType<string>().ToHashSet(StringComparer.Ordinal);
            foreach (var (id, value) in values)
            {
                if (!held.TryGetValue(id, out var attribute) || attribute.Collection || attribute.Derived is not null || bound.Contains(id)
                    || ExprSites.TypeOf(model, attribute.Type).Kind == "value-object")
                {
                    return ($"values names '{id}', which a set could not set: an attribute every subject holds under one id, neither derived, a collection, a value object "
                        + "nor one that holds a lifecycle's state. Remove it.", "/values");
                }

                if (runtime.Fits(attribute, value) is { } why)
                    return ($"the value of '{attribute.Name}' does not fit its type ({why}); give a value of the attribute's type.", "/values");
            }
        }

        if (step.End is null && step.Object is null)
            return null;
        if (step.End is not { } end)
            return ("the step names an object but no end; a link step names the subject's end and the object at its other end.", "/object");
        if (step.Object is not { } objectId)
            return ("the step names an end but no object; a link step names the subject's end and the object at its other end.", "/end");
        if (string.Equals(verb, "delete", StringComparison.Ordinal))
            return ("delete removes the object, not a link; remove end and object, or disconnect the link.", "/end");
        if (cast.Objects.FirstOrDefault(o => string.Equals(o.Id, cast.Subject, StringComparison.Ordinal)) is not { } subject
            || model.Get<Entity>(subject.Entity) is not { } entity)
            return ("a link step needs the scenario's objects and its subject; give them, or remove end and object.", "/end");
        var chain = SubjectCoverage.Chain(model, entity);
        if (!model.TryGetEntry(end, out var entry) || model.Get<Relation>(entry.OwnerId) is not { } relation
            || !relation.Ends.Any(e => string.Equals(e.Id, end, StringComparison.Ordinal))
            // several-types: wave 2 — an end held through a covered type, and a connect step's type check (decision 18), read the end coverage helper.
            || !relation.Ends.Any(e => !string.Equals(e.Id, end, StringComparison.Ordinal) && e.SingleEntity is { } near && chain.Contains(near, StringComparer.Ordinal)))
            return ($"end '{end}' does not lead from the subject's entity '{entity.Name}' or one of its bases; name an end of a relation of '{entity.Name}'.", "/end");
        if (!cast.Objects.Any(o => string.Equals(o.Id, objectId, StringComparison.Ordinal)))
            return ($"object '{objectId}' is not one of the scenario's objects; name one of them.", "/object");
        if (!string.Equals(verb, "connect", StringComparison.Ordinal)
            && !subject.Links.Any(l => string.Equals(l.End, end, StringComparison.Ordinal) && string.Equals(l.Object, objectId, StringComparison.Ordinal)))
        {
            return ($"object '{cast.Objects.First(o => string.Equals(o.Id, objectId, StringComparison.Ordinal)).Name}' is not linked to the subject through end '{end}'; "
                + "link it in the subject's links, or connect it first.", "/object");
        }

        return null;
    }

    /// <summary>
    /// MQ9305 on a step's <c>assume</c> words (trigger-points-design.md decision 26): each word names a check the team writes, a check with no
    /// condition, of a lifecycle the step reaches (<see cref="TriggerSequences.Reaching"/>). Boolean entries, for guards, are not checked. A step
    /// with no word costs one pass over its entries.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process the scenario runs.</param>
    /// <param name="cast">The scenario's objects, subject and participants.</param>
    /// <param name="step">The step.</param>
    /// <returns>The message, without the step's number, and the field it points at; or <see langword="null"/>.</returns>
    public static (string Message, string Field)? CheckAssume(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioStep step)
    {
        if (!step.Assume.Values.Any(a => a.Check is not null))
            return null;
        var obj = cast.Subject is { } subject ? cast.Objects.FirstOrDefault(o => string.Equals(o.Id, subject, StringComparison.Ordinal)) : null;
        var entity = obj is null ? null : model.Get<Entity>(obj.Entity);
        var verb = step.Input switch
        {
            StepInput.Verb => step.Verb,
            StepInput.Event => process.Events.FirstOrDefault(e => string.Equals(e.Id, step.Event, StringComparison.Ordinal))?.Verb,
            _ => null,
        };
        var sequences = TriggerSequences.Of(model);
        var reached = sequences.Reaching(process, entity, verb);
        // A named trigger point's check (part 2) is reached when the point covers the object's entity, or every subject with no object.
        var named = sequences.NamedReaching(process, entity);
        foreach (var (key, value) in step.Assume.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (value.Check is not { } word)
                continue;
            var point = reached.SelectMany(l => l.TriggerPoints).FirstOrDefault(p => string.Equals(p.Id, key, StringComparison.Ordinal))
                ?? (named.FirstOrDefault(p => string.Equals(p.Id, key, StringComparison.Ordinal)) is { } found ? sequences.BodyOf(found) : null);
            if (point is not { Type: TriggerPointType.Check })
            {
                return ($"assume gives {StepAssumption.Word(word)} for '{key}', which is not a check the team writes of a lifecycle this step reaches; "
                    + "give true or false for a guard, or name such a check.", "/assume");
            }

            if (point.Condition is not null)
                return ($"assume gives {StepAssumption.Word(word)} for check '{point.Name}', which has a condition the run works out; remove the entry.", "/assume");
        }

        return null;
    }
}
