using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The process side of processes linked to operations (processes-operations-design.md sections 4 and 8): MQ3032 on a call and on a service
/// step, when the operation is record-level; MQ9024, only while <c>validation.rules</c> gives it a severity; MQ9025 and MQ9026 on calls and
/// steps. A library's call is MQ9020 (<see cref="CheckLibrary"/>). An operation that doesn't resolve is MQ2001 or MQ2002, so nothing more is
/// said of its call.
/// </summary>
internal static partial class ProcessRules
{
    private static void CheckOperationLinks(ValidationContext context, Process process, ProcessAnalysis analysis, Report report)
    {
        var model = context.Model;
        for (var i = 0; i < process.Actions.Count; i++)
        {
            if (process.Actions[i].Call is { } call)
                CheckCall(model, process, process.Actions[i], call, "/actions/" + N(i) + "/call", report);
        }

        foreach (var node in analysis.States)
        {
            for (var i = 0; i < node.State.Invoke.Count; i++)
            {
                var invoke = node.State.Invoke[i];
                if (invoke.Operation is not { } operationId)
                    continue;
                var at = node.Pointer + "/invoke/" + N(i) + "/operation";
                if (invoke.Type != InvokeType.Service)
                {
                    report.Add("MQ9025", $"Invoke '{invoke.Name}' of process '{process.Name}' names an operation, but only a service step runs one; remove operation, or make "
                        + "the invoke a service step.", at, invoke.Id);
                }
                else if (model.Get<Operation>(operationId) is { } operation)
                {
                    CheckSubjects(model, process, operation, $"Service step '{invoke.Name}' runs operation '{operation.Name}'", at, invoke.Id, report);
                }
            }
        }

        if (OperationLinks.UnfiredEventsOn(model))
            CheckUnfiredEvents(model, process, report);
    }

    // MQ3032 on a call, MQ9025 on its inputs, MQ9026 on the required members it leaves out.
    private static void CheckCall(ModelSnapshot model, Process process, ProcessAction action, ProcessCall call, string pointer, Report report)
    {
        if (model.Get<Operation>(call.Operation) is not { } operation)
            return;
        CheckSubjects(model, process, operation, $"Action '{action.Name}' calls operation '{operation.Name}'", pointer + "/operation", action.Id, report);
        var given = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < call.Inputs.Count; j++)
        {
            var input = call.Inputs[j];
            var at = pointer + "/inputs/" + N(j) + "/member";
            var member = operation.Input.FirstOrDefault(m => string.Equals(m.Id, input.Member, StringComparison.Ordinal));
            if (member is null)
            {
                report.Add("MQ9025", $"An input of action '{action.Name}' names '{input.Member}', which is not an input member of operation '{operation.Name}'; remove the input.",
                    at, input.Id);
            }
            else if (!given.Add(member.Id))
            {
                report.Add("MQ9025", $"Action '{action.Name}' gives input '{member.Name}' of operation '{operation.Name}' twice; remove the second input.", at, input.Id);
            }
            else if (Ungivable(model, member) is { } why)
            {
                report.Add("MQ9025", $"Action '{action.Name}' gives input '{member.Name}' of operation '{operation.Name}' a value, but it is {why}, so a call can't give it; "
                    + "remove the input, and let your system supply it.", at, input.Id);
            }
        }

        // Inputs not listed are the team's (decision 7): a required one without a default is a warning, unless a call can't give it at all
        // (a file, a list, the key of an entity with a composite key or none, a member with no type the expression language has).
        foreach (var member in operation.Input)
        {
            if (member.Required && member.Default is null && !given.Contains(member.Id) && Ungivable(model, member) is null)
            {
                report.Add("MQ9026", $"Action '{action.Name}' calls operation '{operation.Name}' without its required input '{member.Name}', which has no default; give it a value, "
                    + "or your system must supply it.", pointer, action.Id);
            }
        }
    }

    // Why a call can't give a member a value (decision 6), or null: a file, a list, the key of an entity whose key is composite or missing, or
    // a member with no type the expression language has values of (binary, float, a value object; ExprSites.MemberType). A type or entity
    // that doesn't resolve is MQ2001 or MQ2002, so nothing more is said of it here.
    private static string? Ungivable(ModelSnapshot model, OperationMember member)
    {
        if (member.File)
            return "a file";
        if (member.Collection)
            return "a list";
        if (member.KeyOf is { } entityId)
        {
            if (model.Get<Entity>(entityId) is not { } entity)
                return null;
            if (ExprSites.KeyAttribute(model, entity) is not { } key)
                return $"the key of '{entity.Name}', which has no one key attribute";
            return ExprSites.MemberType(model, member) is null
                ? $"the key of '{entity.Name}', whose key attribute is {OperationLinks.TypeText(model, key.Type)}, a type the expression language has no values of"
                : null;
        }

        if (member.Type is not { } type || (type.Ref is { } id && model.Get<Element>(id) is null))
            return null;
        return ExprSites.MemberType(model, member) is null ? $"of type {OperationLinks.TypeText(model, type)}, which the expression language has no values of" : null;
    }

    // MQ3032 on a call or a service step: only for a record-level operation, and not when either side has no subject (decision 2).
    private static void CheckSubjects(ModelSnapshot model, Process process, Operation operation, string what, string pointer, string elementId, Report report)
    {
        if (operation.Level != OperationLevel.Record || operation.Subject is not { } subjectId || model.Get<Entity>(subjectId) is not { } subject
            || process.Subjects.Count == 0 || OperationLinks.LinesUp(model, process, subject))
            return;
        report.Add("MQ3032", $"{what}, which acts on one '{subject.Name}', but process '{process.Name}' is about {OperationLinks.Subjects(model, process)}, and governs neither "
            + $"'{subject.Name}' nor an entity derived from it; the model can't say which record it acts on. Call an operation on the process's subject, or let your system "
            + "pick the record and turn the rule off.", pointer, elementId);
    }

    // MQ9024 (decision 4): an event some event transition uses, that no action of the process raises and that no operation fires.
    private static void CheckUnfiredEvents(ModelSnapshot model, Process process, Report report)
    {
        var used = process.Transitions.Where(t => t.Trigger == TransitionTrigger.Event && t.Event is not null).Select(t => t.Event!).ToHashSet(StringComparer.Ordinal);
        var raised = process.Actions.SelectMany(a => a.Raises).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < process.Events.Count; i++)
        {
            var e = process.Events[i];
            if (!used.Contains(e.Id) || raised.Contains(e.Id)
                || model.ReferencesTo(e.Id).Any(r => model.Get<Operation>(r.FromElementId) is { } operation && operation.Fires.Contains(e.Id, StringComparer.Ordinal)))
                continue;
            report.Add(OperationLinks.UnfiredEvent, $"Event '{e.Name}' of process '{process.Name}' makes a move, but no operation fires it; list it in the fires of the "
                + "operation that makes the move.", "/events/" + N(i), e.Id);
        }
    }
}
