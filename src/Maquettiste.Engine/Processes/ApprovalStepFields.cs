using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// MQ9305 on a step's approval fields (signoff-design.md decision 25), judged before the step runs, as a payload is: a <c>response</c> other
/// than sign, or a <c>signature</c>, only on an event step whose event is gated by an approval; a <c>signature</c> of such an approval; and
/// each key of <c>expect.signatures</c> a named signature of the approval the step answers. A step that gives none of them costs three tests.
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
        if (step.Response == SignatureResponse.Sign && step.Signature is null && expected is not { Count: > 0 })
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
}
