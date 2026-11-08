using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// MQ9305 on a step's approval fields (signoff-design.md decision 25), judged before the step runs, as a payload is: a <c>response</c> other
/// than sign, or a <c>signature</c>, only on an event step whose event is gated by an approval; a <c>signature</c> of such an approval; and
/// each key of <c>expect.signatures</c> a named signature of the approval the step answers; and a delegation that fits (decision 34):
/// <c>delegateTo</c> only with <c>response: delegate</c>, which needs it; <c>delegate</c> or <c>onBehalfOf</c> only where a signature has
/// <c>delegateTo</c>, and a named <c>signature</c> then one that has it; never <c>delegate</c> with <c>onBehalfOf</c>. Whether an earlier step
/// delegated to the person is left to replay. A step that gives none of them costs five tests.
/// </summary>
internal static class ApprovalStepFields
{
    /// <summary>The first problem of a step's approval fields, or <see langword="null"/>.</summary>
    /// <param name="model">The model.</param>
    /// <param name="chart">The chart the step runs on.</param>
    /// <param name="step">The step.</param>
    /// <returns>The message, without the step's number, and the field it points at.</returns>
    public static (string Message, string Field)? Check(ModelSnapshot model, StatechartModel chart, ScenarioStep step)
    {
        var expected = step.Expect?.Signatures;
        if (step.Response == SignatureResponse.Sign && step.Signature is null && expected is not { Count: > 0 } && step.DelegateTo is null && step.OnBehalfOf is null)
            return null;

        // The approvals the step's event is gated by, on any source: which one it answers depends on the state, which only the run knows.
        var approvals = step.Input == StepInput.Event && step.Event is { } eventId
            ? chart.Transitions.Where(t => t.Trigger == TransitionTrigger.Event && string.Equals(t.Transition.Event, eventId, StringComparison.Ordinal))
                .Select(t => ApprovalRules.GateOf(model, t.Transition)).OfType<ProcessGate>().Where(g => g.UsesStages).ToList()
            : [];
        var eventName = step.Event is not null && chart.Events.TryGetValue(step.Event, out var ev) ? $"event '{ev.Name}'" : "its event";
        if (approvals.Count == 0)
        {
            if (step.Response != SignatureResponse.Sign)
            {
                return ($"the step gives response '{VerbRules.ResponseKey(step.Response)}', but only an event step whose event is gated by an approval takes a response; "
                    + "remove it, or give the gate stages.", "/response");
            }

            if (step.Signature is not null)
            {
                return ("the step names a signature, but only an event step whose event is gated by an approval answers one; remove it, or give the gate stages.",
                    "/signature");
            }
        }

        var signatures = approvals.SelectMany(g => g.AllSignatures).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        if (step.Signature is { } signature && !signatures.Contains(signature))
            return ($"the signature '{signature}' is not a named signature of an approval on {eventName}; pick one of its signatures, or remove it.", "/signature");
        if (Delegation(approvals, step, eventName) is { } delegation)
            return delegation;
        if (expected is not null)
        {
            foreach (var key in expected.Keys.Order(StringComparer.Ordinal))
            {
                if (!signatures.Contains(key))
                {
                    return ($"expect.signatures names '{key}', which is not a named signature of the approval the step answers; remove the entry.",
                        "/expect/signatures");
                }
            }
        }

        return null;
    }

    // A step's delegation (signoff-design.md decision 34): who it goes to only with delegate, and on whose behalf only where a signature may be
    // delegated; a delegation can't be passed on.
    private static (string Message, string Field)? Delegation(List<ProcessGate> approvals, ScenarioStep step, string eventName)
    {
        var delegating = step.Response == SignatureResponse.Delegate;
        if (step.DelegateTo is not null && !delegating)
        {
            return ("the step names delegateTo, but only a delegate response hands a signature to someone; remove it, or give response 'delegate'.",
                "/delegateTo");
        }

        if (delegating && step.DelegateTo is null)
            return ("the step gives response 'delegate', but names no delegateTo, the participant the signature goes to; add it.", "/response");
        if (delegating && step.OnBehalfOf is not null)
            return ("the step delegates on behalf of another person, but a delegation can't be passed on; remove onBehalfOf.", "/onBehalfOf");
        if (!delegating && step.OnBehalfOf is null)
            return null;
        var delegable = approvals.SelectMany(g => g.AllSignatures).Where(s => s.DelegateTo.Count > 0).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var what = delegating ? "gives response 'delegate'" : "answers on behalf of another person";
        if (delegable.Count == 0)
        {
            return ($"the step {what}, but no named signature of an approval on {eventName} has delegateTo; remove it, or let a signature be delegated.",
                delegating ? "/response" : "/onBehalfOf");
        }

        return step.Signature is { } named && !delegable.Contains(named)
            ? ($"the step {what}, but signature '{named}' has no delegateTo, so it can't be delegated; pick a signature with delegateTo, or remove signature.", "/signature")
            : null;
    }
}
