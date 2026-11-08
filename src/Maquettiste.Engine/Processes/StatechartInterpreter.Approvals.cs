using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>A signature counted on an approval (signoff-design.md decision 26): who signed, as which actor, and for which named signature.</summary>
/// <param name="Signer">The person's identity.</param>
/// <param name="Actor">The actor signed as.</param>
/// <param name="Meaning">The meaning id.</param>
/// <param name="Reason">The reason.</param>
/// <param name="Signature">The named signature's id.</param>
internal sealed record CountedSignature(string Signer, string Actor, string? Meaning, string? Reason, string Signature)
{
    /// <summary>For a signature given on behalf of someone who delegated it, the delegator's identity (signoff-design.md decision 34); else null.</summary>
    public string? OnBehalfOf { get; init; }
}

/// <summary>
/// A signature handed to someone else (signoff-design.md decision 34): who handed it, as which actor, and to whom. The person it went to answers
/// once on the delegator's behalf, as the delegator's actor; a withdraw reopens it. A rejection or leaving the state ends it with the approval.
/// </summary>
/// <param name="Signature">The named signature's id.</param>
/// <param name="Delegator">The delegator's identity.</param>
/// <param name="Actor">The actor the delegator acted as: an answer on their behalf counts as it.</param>
/// <param name="Target">The identity it went to.</param>
internal sealed record Delegation(string Signature, string Delegator, string Actor, string Target)
{
    /// <summary>Whether the person it went to has answered on it, by a signature or an ignore that still stands.</summary>
    public bool Answered { get; set; }
}

/// <summary>
/// The scenario's participants a step names for a delegation (signoff-design.md decision 34), as the run reads them: the identity a <c>delegate</c>
/// goes to and which actors it meets, and the identity of the person an answer is given on behalf of.
/// </summary>
/// <param name="DelegateTo">The identity of the step's <c>delegateTo</c> participant, or <see langword="null"/>.</param>
/// <param name="DelegateMeets">Whether that participant meets an actor, through memberships; <see langword="null"/> when there is none.</param>
/// <param name="OnBehalfOf">The identity of the step's <c>onBehalfOf</c> participant, or <see langword="null"/>.</param>
internal sealed record StepParties(string? DelegateTo, Func<string, bool>? DelegateMeets, string? OnBehalfOf);

/// <summary>
/// An approval's progress on a gated move (signoff-design.md decisions 7 to 16): the signatures its round needs, the signatures counted, and each
/// person's answers. With nothing needed it is complete when it starts, and the gated event's next occurrence moves as if there were no gate.
/// </summary>
internal sealed class ApprovalProgress
{
    /// <summary>
    /// The ids of the named signatures the current round needs: those of its stages not skipped whose <c>when</c> was not false when it started.
    /// </summary>
    public HashSet<string> Needed { get; } = new(StringComparer.Ordinal);

    /// <summary>The signatures counted, in every round, in the order given.</summary>
    public List<CountedSignature> Counted { get; } = [];

    /// <summary>Each person's signatures, ignores and delegations, in the order given; a withdraw takes its signature back out.</summary>
    public List<(string Person, string Signature, SignatureResponse Kind)> Answers { get; } = [];

    /// <summary>The current round (signoff-design.md decision 33): the round of its stages; the last one once nothing is needed.</summary>
    public int Round { get; set; }

    /// <summary>The ids of the stages skipped in the rounds started, their <c>when</c> false (decision 6).</summary>
    public HashSet<string> Skipped { get; } = new(StringComparer.Ordinal);

    /// <summary>The ids of the named signatures not needed in the rounds started: their <c>when</c> was false, or their stage was skipped.</summary>
    public HashSet<string> Excused { get; } = new(StringComparer.Ordinal);

    /// <summary>The instant the current round started, which its reminders and escalations count from (decision 35).</summary>
    public DateTimeOffset Started { get; set; }

    /// <summary>The reminders and escalations that fell due since the current round started, each once (decision 35).</summary>
    public HashSet<(string Signature, NoticeKind Kind)> Noticed { get; } = [];

    /// <summary>The delegations given on the approval, in the order given (decision 34).</summary>
    public List<Delegation> Delegations { get; } = [];

    /// <summary>Whether nothing is needed: the approval is complete and waits for the gated event's next occurrence.</summary>
    public bool Waiting => Needed.Count == 0;

    /// <summary>Whether it holds any response, which leaving its source discards with an audit record.</summary>
    public bool Holds => Counted.Count > 0 || Answers.Count > 0;
}

/// <summary>
/// Approvals in a run (signoff-design.md section 8): a gate with stages starts its round when the object enters the gated move's source state,
/// takes the responses sign, reject, ignore and withdraw, checks row 7 of decision 14 on the signature picked, and keeps the record of acts that
/// <c>notBy</c> reads (decision 28). In part 2 its stages run in rounds, a signature may be delegated and answered on behalf, and reminders and
/// escalations fall due on time steps (decisions 33 to 35). A gate without stages never reaches this code, so it signs, audits and traces as before.
/// </summary>
public sealed partial class StatechartInterpreter
{
    // The named signature each gated candidate answers in the step, from row 7 to its admit, and the delegation an answer on behalf uses.
    private readonly Dictionary<ChartTransition, NamedSignature> _picks = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ChartTransition, Delegation> _behalf = new(ReferenceEqualityComparer.Instance);

    /// <summary>The progress of the root's approval on a gated transition, or <see langword="null"/> when its source is not active.</summary>
    /// <param name="transitionId">The gated transition's id.</param>
    /// <returns>The progress.</returns>
    internal ApprovalProgress? ApprovalAt(string transitionId) => _root.Approvals.TryGetValue(transitionId, out var approval) ? approval : null;

    // An act of a person since a state was entered (decision 28), by the step's sequence number: a verb, a move, or both.
    private sealed record Act(int Sequence, string Person, string? Verb, string? Transition);

    // Whether a chart keeps the record of acts: one of its gates, inline or through a gate policy, has a signature with a notBy.
    private static bool KeepsActs(StatechartModel chart) =>
        chart.Transitions.Any(t => t.Gate is { UsesStages: true } gate && gate.AllSignatures.Any(s => s.NotBy.Count > 0));

    // The person a step is (decision 15): its signer, else its participant's identity; null for a move nobody sends.
    private string? Person() => _scope.Input?.Signer is { Length: > 0 } signer ? signer : _scope.Identity;

    // Records an act of a person, only in a chart that keeps the record; nobody's act is never recorded.
    private void RecordAct(Instance inst, string? person, string? verb, string? transition)
    {
        if (inst.RecordsActs && person is not null && (verb is not null || transition is not null))
            inst.Acts.Add(new Act(_index, person, verb, transition));
    }

    // Drops the acts no waiting notBy can read (decision 28): those older than the earliest entry of an active state that is the source of a
    // gated move with a notBy; all of them when no such state is active.
    private void TrimActs()
    {
        foreach (var inst in AllInstances())
        {
            if (!inst.RecordsActs || inst.Acts.Count == 0)
                continue;
            int? earliest = null;
            foreach (var t in inst.Chart.Transitions)
            {
                if (t.Gate is { UsesStages: true } gate && gate.AllSignatures.Any(s => s.NotBy.Count > 0) && inst.Config.Contains(t.Source)
                    && inst.EnteredAt.TryGetValue(t.Source.Id, out var at) && (earliest is null || at < earliest))
                    earliest = at;
            }

            inst.Acts.RemoveAll(a => earliest is null || a.Sequence < earliest);
        }
    }

    // The approval of a gated move whose source is active; started now if its round has not started (it starts on entry, so this is a fallback).
    private ApprovalProgress ApprovalOf(Instance inst, ChartTransition t, ProcessGate gate) =>
        inst.Approvals.TryGetValue(t.Id, out var approval) ? approval : StartApproval(inst, t, gate);

    // Whether a gated move holds an open approval: one with something needed.
    private bool OpenApproval(Instance inst, ChartTransition t) => t.Gate is { UsesStages: true } gate && !ApprovalOf(inst, t, gate).Waiting;

    // Starts round 1 (decisions 7 and 33), with every later round that completes at once.
    private ApprovalProgress StartApproval(Instance inst, ChartTransition t, ProcessGate gate)
    {
        var approval = new ApprovalProgress();
        inst.Approvals[t.Id] = approval;
        StartRound(inst, gate, approval, gate.Stages!.Min(s => s.Round));
        return approval;
    }

    // Starts a round (decision 33): its stages' when are judged, then the when of each signature of the stages not skipped. A round in which
    // nothing is needed is complete at once and the next one starts; when the last one completes that way, nothing is needed.
    private void StartRound(Instance inst, ProcessGate gate, ApprovalProgress approval, int from)
    {
        var session = Session(inst.Chart);
        foreach (var round in gate.Stages!.Select(s => s.Round).Where(r => r >= from).Distinct().Order())
        {
            approval.Round = round;
            approval.Started = Clock;
            approval.Noticed.Clear();
            approval.Needed.Clear();
            var stages = gate.Stages!.Where(s => s.Round == round).ToList();
            var running = stages.Where(s => s.When is null || Holds(inst, session, s.Id, $"Stage '{s.Name}'")).ToList();
            foreach (var skipped in stages.Where(s => !running.Contains(s)))
            {
                approval.Skipped.Add(skipped.Id);
                approval.Excused.UnionWith(skipped.Signatures.Select(x => x.Id));
            }

            foreach (var signature in running.SelectMany(s => s.Signatures))
            {
                if (signature.When is null || Holds(inst, session, signature.Id, $"Signature '{signature.Name}'"))
                    approval.Needed.Add(signature.Id);
                else
                    approval.Excused.Add(signature.Id);
            }

            if (approval.Needed.Count > 0)
                return;
        }
    }

    // Whether a stage's or signature's when holds when its round starts (decisions 6 and 7), over the context and the object: only false
    // answers false, so one that didn't compile, threw or ran past a limit holds.
    private bool Holds(Instance inst, ProcessExpressionSession? session, string id, string label)
    {
        if (session is null || !session.Conditions.Contains(id))
            return true;
        var lowered = session.Lowered.TryGetValue(id, out var found) ? found : null;
        if (lowered is { EngineOnly: true })
            _ = Reads(inst, lowered);
        var result = session.EvaluateApproval(id, label, ContextObject(inst), SubjectOf(inst), lowered is { ReadsRunning: true } ? Running(inst) : null, ClockText(),
            Seed(inst), _options.CancellationToken);
        if (result.Diagnostic is { } d)
            _scope.Diagnostics.Add(d);
        return result.Diagnostic is not null || result.Value is not false;
    }

    // The round after the current one, or null when it is the last (decision 33).
    private static int? LaterRound(ProcessGate gate, ApprovalProgress approval) =>
        gate.Stages!.Where(s => s.Round > approval.Round).Select(s => (int?)s.Round).Min();

    // Whether a signature has its count and each of its required actors signed, among the signatures given.
    private bool Complete(NamedSignature signature, IEnumerable<CountedSignature> counted)
    {
        var own = counted.Where(c => string.Equals(c.Signature, signature.Id, StringComparison.Ordinal)).ToList();
        return own.Count >= Math.Max(1, signature.Required) && signature.RequiredActors.All(a => own.Any(c => Accepts(a, c.Actor)));
    }

    // Whether every needed signature of the round is complete with the signatures given (decision 4).
    private bool RoundDone(ProcessGate gate, ApprovalProgress approval, IReadOnlyList<CountedSignature> counted) =>
        gate.AllSignatures.Where(s => approval.Needed.Contains(s.Id)).All(s => Complete(s, counted));

    // A signature's required actors that no signature counted on it yet satisfies, in file order.
    private List<string> Unsigned(NamedSignature signature, ApprovalProgress approval) =>
        [.. signature.RequiredActors.Where(a => !approval.Counted.Any(c => string.Equals(c.Signature, signature.Id, StringComparison.Ordinal) && Accepts(a, c.Actor)))
            .Distinct(StringComparer.Ordinal)];

    // Row 7c (decision 15): the person did a verb or move the signature's notBy names since the object entered the gated move's source state,
    // the move that entered it included.
    private static bool Excluded(Instance inst, ChartTransition t, NamedSignature signature, string? person)
    {
        if (signature.NotBy.Count == 0 || person is null || inst.Acts.Count == 0)
            return false;
        var from = inst.EnteredAt.TryGetValue(t.Source.Id, out var at) ? at : int.MinValue;
        return inst.Acts.Any(a => a.Sequence >= from && string.Equals(a.Person, person, StringComparison.Ordinal)
            && signature.NotBy.Any(n => (n.Verb is not null && string.Equals(n.Verb, a.Verb, StringComparison.Ordinal))
                || (n.Transition is not null && string.Equals(n.Transition, a.Transition, StringComparison.Ordinal))));
    }

    // Row 7d by response (decision 14): an earlier answer of the person on this approval blocks the response.
    private static bool Repeated(ProcessGate gate, ApprovalProgress approval, NamedSignature signature, SignatureResponse response, string? person)
    {
        var mine = approval.Answers.Where(a => string.Equals(a.Person, person, StringComparison.Ordinal)).ToList();
        var here = mine.Where(a => string.Equals(a.Signature, signature.Id, StringComparison.Ordinal)).ToList();
        var elsewhere = mine.Count > here.Count && !gate.AllowSignerAcrossSignatures;
        return response switch
        {
            SignatureResponse.Sign => (here.Any(a => a.Kind == SignatureResponse.Sign) && !signature.AllowRepeatSigner) || here.Any(a => Finished(a.Kind)) || elsewhere,
            SignatureResponse.Ignore or SignatureResponse.Delegate => here.Count > 0 || elsewhere,
            SignatureResponse.Reject => mine.Any(a => Finished(a.Kind) && (string.Equals(a.Signature, signature.Id, StringComparison.Ordinal) || !gate.AllowSignerAcrossSignatures)),
            _ => false,
        };

        // A delegation blocks as an ignore does (part 2).
        static bool Finished(SignatureResponse kind) => kind is SignatureResponse.Ignore or SignatureResponse.Delegate;
    }

    // The round's needed signatures in file order, as the pick reads them for a person and a response (decision 8).
    private List<ActingSignature> NeededSignatures(Instance inst, ChartTransition t, ProcessGate gate, ApprovalProgress approval, string? person, SignatureResponse response) =>
        [.. gate.AllSignatures.Where(s => approval.Needed.Contains(s.Id)).Select(s => new ActingSignature(s, Complete(s, approval.Counted), Unsigned(s, approval),
            Excluded(inst, t, s, person) || Repeated(gate, approval, s, response, person)))];

    // The person's latest signature in the current round, their own or on behalf of the delegator given, which a withdraw takes back (decisions
    // 13 and 34).
    private static CountedSignature? LatestSigned(ApprovalProgress approval, string? person, string? onBehalfOf = null) =>
        approval.Counted.LastOrDefault(c => string.Equals(c.Signer, person, StringComparison.Ordinal) && approval.Needed.Contains(c.Signature)
            && string.Equals(c.OnBehalfOf, onBehalfOf, StringComparison.Ordinal));

    private static string? Latest(ApprovalProgress approval, string? person, string? onBehalfOf = null) => LatestSigned(approval, person, onBehalfOf)?.Signature;

    // The delegations from a delegator to a person on the current round's needed signatures (decision 34), by signature id.
    private static Dictionary<string, Delegation> OpenTo(ApprovalProgress approval, string? delegator, string? person)
    {
        var open = new Dictionary<string, Delegation>(StringComparer.Ordinal);
        foreach (var d in approval.Delegations)
        {
            if (string.Equals(d.Delegator, delegator, StringComparison.Ordinal) && string.Equals(d.Target, person, StringComparison.Ordinal) && approval.Needed.Contains(d.Signature))
                open.TryAdd(d.Signature, d);
        }

        return open;
    }

    // The signatures an answer on behalf can take (decision 34): the round's needed signatures the delegator handed to the person, each counting
    // as the delegator's actor.
    private List<ActingSignature> BehalfSignatures(Instance inst, ChartTransition t, ProcessGate gate, ApprovalProgress approval, string? person, string? delegator,
        SignatureResponse response)
    {
        var open = OpenTo(approval, delegator, person);
        return [.. NeededSignatures(inst, t, gate, approval, person, response).Where(s => open.ContainsKey(s.Signature.Id))
            .Select(s => s with { CountsAs = open[s.Signature.Id].Actor })];
    }

    // Whether the person a delegation goes to meets one of the signature's delegateTo actors (decision 34).
    private bool DelegatesTo(NamedSignature signature) =>
        _scope.Parties is { DelegateTo: not null, DelegateMeets: { } meets } && signature.DelegateTo.Any(meets);

    // The refusal for a named signature the current round doesn't need (decisions 8 and 33): a withdraw of an earlier round's finds nothing to
    // take back; otherwise no one meets its signers now.
    private static string Outside(ProcessGate gate, ApprovalProgress approval, NamedSignature signature, SignatureResponse response) =>
        response == SignatureResponse.Withdraw && StageOf(gate, signature).Round < approval.Round ? Refusals.GateNotSigned : Refusals.GateSigner;

    private static GateStage StageOf(ProcessGate gate, NamedSignature signature) =>
        gate.Stages!.First(s => s.Signatures.Any(x => string.Equals(x.Id, signature.Id, StringComparison.Ordinal)));

    /// <summary>
    /// Row 7 of decision 14 on an approval with something needed: the signature the step answers (the one picked with the actor, else the
    /// step's own, else picked here by the response's rule for the step's actor), then its signers, the lifecycle's denies on the response's verb,
    /// <c>notBy</c>, an earlier answer, nothing to withdraw and a reason, in that order; a delegation then needs the person it goes to among the
    /// signature's <c>delegateTo</c>. An answer on behalf of a delegator has the delegation in place of the signers (signoff-design.md decision
    /// 34). A refused attempt is audited <c>refused</c> with the signature when one was picked. <see langword="null"/> when the response may go
    /// on, its signature kept for the admit.
    /// </summary>
    private (string Refusal, string? By)? CheckApproval(Instance inst, ChartTransition t, ProcessGate gate, ApprovalProgress approval)
    {
        var input = _scope.Input!;
        var response = input.Response;
        _picks.Remove(t);
        _behalf.Remove(t);
        string? refusal = null;
        var by = gate.Id;
        NamedSignature? signature;
        Delegation? behalf = null;
        var named = new[] { _scope.Pick, input.Signature }.OfType<string>()
            .Select(id => gate.AllSignatures.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal))).OfType<NamedSignature>().FirstOrDefault();
        if (input.OnBehalfOf is not null)
        {
            // An answer on behalf of whoever delegated the signature to the person: only on a delegation open to them, and never passed on.
            var delegator = _scope.Parties?.OnBehalfOf;
            var open = OpenTo(approval, delegator, input.Signer);
            if (response == SignatureResponse.Delegate || delegator is null || string.IsNullOrEmpty(input.Signer))
            {
                (signature, refusal) = (named, Refusals.GateSigner);
            }
            else if (named is not null)
            {
                signature = named;
                behalf = open.GetValueOrDefault(named.Id);
                refusal = !approval.Needed.Contains(named.Id) ? Outside(gate, approval, named, response) : behalf is null ? Refusals.GateSigner : null;
            }
            else
            {
                var picked = ApprovalPick.Choose(response, BehalfSignatures(inst, t, gate, approval, input.Signer, delegator, response),
                    Latest(approval, input.Signer, delegator), s => (s.CountsAs, s.Unsigned.Any(a => Accepts(a, s.CountsAs!))));
                (signature, refusal) = (picked.Signature, picked.Refusal);
                behalf = signature is null ? null : open[signature.Id];
            }
        }
        else if (named is not null)
        {
            // A signature the round doesn't need asks nobody to sign: no one meets its signers now.
            signature = named;
            if (!approval.Needed.Contains(named.Id))
                refusal = Outside(gate, approval, named, response);
        }
        else
        {
            var actor = input.Actor;
            var met = actor is not null && !string.IsNullOrEmpty(input.Signer);
            var picked = ApprovalPick.Choose(response, NeededSignatures(inst, t, gate, approval, input.Signer, response), Latest(approval, input.Signer),
                s => met && Allows(s.Signature.Signers, actor!) ? (actor, s.Unsigned.Any(a => Accepts(a, actor!))) : (null, false));
            (signature, refusal) = (picked.Signature, picked.Refusal);
        }

        if (refusal is null && signature is not null)
        {
            if (behalf is null && (input.Actor is null || string.IsNullOrEmpty(input.Signer) || !Allows(signature.Signers, input.Actor)))
                refusal = Refusals.GateSigner;
            else if (ReferenceEquals(inst, _root) && _options.Rights is { Uses: true } rights
                && CheckRights(rights, VerbRules.ResponseKey(response), [], [new RightsList(gate.Id, behalf is null ? signature.Signers : signature.DelegateTo)], null)
                    is { Refusal: Refusals.Right } denied)
                (refusal, by) = (Refusals.Right, denied.RefusedBy ?? gate.Id);
            else if (Excluded(inst, t, signature, input.Signer))
                refusal = Refusals.GateExcluded;
            else if (Repeated(gate, approval, signature, response, input.Signer) || (response == SignatureResponse.Sign && Complete(signature, approval.Counted))
                || (behalf is { Answered: true } && response is SignatureResponse.Sign or SignatureResponse.Ignore))
                refusal = Refusals.GateRepeat;
            else if (response == SignatureResponse.Withdraw
                && !approval.Counted.Any(c => string.Equals(c.Signer, input.Signer, StringComparison.Ordinal) && string.Equals(c.Signature, signature.Id, StringComparison.Ordinal)
                    && string.Equals(c.OnBehalfOf, behalf?.Delegator, StringComparison.Ordinal)))
                refusal = Refusals.GateNotSigned;
            else if (gate.ReasonRequired && string.IsNullOrWhiteSpace(input.Reason))
                refusal = Refusals.GateReason;
            else if (response == SignatureResponse.Delegate && !DelegatesTo(signature))
                refusal = Refusals.GateSigner;
        }

        if (signature is not null)
            _picks[t] = signature;
        if (behalf is not null)
            _behalf[t] = behalf;
        if (refusal is null)
            return null;
        var (meaning, attributes) = AuditFields(input, gate);
        Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Refused, attributes, signature, behalf?.Delegator);
        return (refusal, by);
    }

    /// <summary>
    /// The response's effect on an approval (decisions 7, 10 to 13, 33 and 34), already checked by row 7: with nothing needed, the occurrence
    /// completes it; a signature is counted, and when every needed signature of the round is complete it starts the next round, or completes the
    /// approval in the last; an ignore finishes the person on the signature; a withdraw takes their latest signature on it back; a delegation
    /// hands the signature to the person it goes to; a rejection clears the approval and starts round 1 again. An answer on behalf counts as the
    /// delegator's actor. Whether the move follows.
    /// </summary>
    private bool Respond(Instance inst, ChartTransition t, ProcessGate gate)
    {
        var input = _scope.Input!;
        var approval = ApprovalOf(inst, t, gate);
        var (meaning, attributes) = AuditFields(input, gate);
        _scope.Answered.Add(gate);
        if (approval.Waiting)
        {
            // Nothing is needed: the move runs as if there were no gate, done by whoever sent it and by whoever signed an earlier round.
            Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Completed, attributes);
            RecordAct(inst, Person(), MoveVerb(t), t.Id);
            foreach (var counted in approval.Counted)
                RecordAct(inst, counted.Signer, MoveVerb(t), t.Id);
            inst.Approvals.Remove(t.Id);
            return true;
        }

        var signature = _picks[t];
        var behalf = _behalf.GetValueOrDefault(t);
        var person = input.Signer!;
        var actor = behalf?.Actor ?? input.Actor!;
        RecordAct(inst, person, VerbRules.ResponseKey(input.Response), null);
        switch (input.Response)
        {
            case SignatureResponse.Sign:
                approval.Counted.Add(new CountedSignature(person, actor, meaning, input.Reason, signature.Id) { OnBehalfOf = behalf?.Delegator });
                approval.Answers.Add((person, signature.Id, SignatureResponse.Sign));
                behalf?.Answered = true;
                Audit(inst, gate, t, person, actor, meaning, input.Reason, GateOutcome.Signed, attributes, signature, behalf?.Delegator);
                if (!RoundDone(gate, approval, approval.Counted))
                    return false;
                // A round other than the last starts the next and moves nothing; the step is consumed in the admit (decision 33).
                if (LaterRound(gate, approval) is { } next)
                {
                    StartRound(inst, gate, approval, next);
                    return false;
                }

                Audit(inst, gate, t, person, actor, meaning, input.Reason, GateOutcome.Completed, attributes);
                // A gated move is done by everyone whose signature counted toward the approval that made it (decision 15).
                foreach (var counted in approval.Counted)
                    RecordAct(inst, counted.Signer, MoveVerb(t), t.Id);
                inst.Approvals.Remove(t.Id);
                return true;
            case SignatureResponse.Ignore:
                approval.Answers.Add((person, signature.Id, SignatureResponse.Ignore));
                behalf?.Answered = true;
                Audit(inst, gate, t, person, actor, meaning, input.Reason, GateOutcome.Ignored, attributes, signature, behalf?.Delegator);
                return false;
            case SignatureResponse.Withdraw:
                var at = approval.Counted.FindLastIndex(c => string.Equals(c.Signer, person, StringComparison.Ordinal) && string.Equals(c.Signature, signature.Id, StringComparison.Ordinal)
                    && string.Equals(c.OnBehalfOf, behalf?.Delegator, StringComparison.Ordinal));
                if (at >= 0)
                    approval.Counted.RemoveAt(at);
                var answer = approval.Answers.FindLastIndex(a => string.Equals(a.Person, person, StringComparison.Ordinal)
                    && string.Equals(a.Signature, signature.Id, StringComparison.Ordinal) && a.Kind == SignatureResponse.Sign);
                if (answer >= 0)
                    approval.Answers.RemoveAt(answer);
                behalf?.Answered = false;
                Audit(inst, gate, t, person, actor, meaning, input.Reason, GateOutcome.Withdrawn, attributes, signature, behalf?.Delegator);
                return false;
            case SignatureResponse.Delegate:
                // The delegator is finished on the signature; the person it goes to may answer once on their behalf (decision 34).
                var target = _scope.Parties!.DelegateTo!;
                approval.Answers.Add((person, signature.Id, SignatureResponse.Delegate));
                approval.Delegations.Add(new Delegation(signature.Id, person, actor, target));
                Audit(inst, gate, t, person, actor, meaning, input.Reason, GateOutcome.Delegated, attributes, signature, delegateTo: target);
                return false;
            default:
                // A veto: it clears every signature and delegation of this approval, in every round, and moves nothing; round 1 starts again
                // at once (decisions 10 and 33).
                Audit(inst, gate, t, person, actor, meaning, input.Reason, GateOutcome.Rejected, attributes, signature, behalf?.Delegator);
                StartApproval(inst, t, gate);
                return false;
        }
    }

    // Round 1 starts when the object enters a gated move's source state, after the entering move's actions and sets, and again when a gated move
    // completed without leaving its source (decision 7).
    private void StartApprovals(Instance inst, IReadOnlyList<ChartTransition> transitions, List<ChartState> arrived)
    {
        foreach (var s in arrived)
        {
            foreach (var t in s.Transitions)
            {
                if (t.Trigger == TransitionTrigger.Event && t.Gate is { UsesStages: true } gate)
                    StartApproval(inst, t, gate);
            }
        }

        foreach (var t in transitions)
        {
            if (t.Gate is { UsesStages: true } gate && inst.Config.Contains(t.Source) && !arrived.Contains(t.Source))
                StartApproval(inst, t, gate);
        }
    }

    // The signatures counted after the step on each approval of the step's event, by named signature id (0 where the approval isn't open), for
    // expect.signatures (decision 27); and those of the approvals the step answered, which a recording writes. Null when there are none.
    private (IReadOnlyDictionary<string, int>? Counts, IReadOnlyDictionary<string, int>? Answered) SignatureCounts()
    {
        if (_scope.Input is not { Input: StepInput.Event, Event: { } eventId } || !AllInstances().Any(i => i.HasApprovals))
            return (null, null);
        SortedDictionary<string, int>? counts = null;
        foreach (var inst in AllInstances())
        {
            foreach (var t in inst.Chart.Transitions)
            {
                if (t.Trigger != TransitionTrigger.Event || !string.Equals(t.Transition.Event, eventId, StringComparison.Ordinal) || t.Gate is not { UsesStages: true } gate)
                    continue;
                var approval = inst.Approvals.TryGetValue(t.Id, out var found) && inst.Config.Contains(t.Source) ? found : null;
                foreach (var signature in gate.AllSignatures)
                {
                    counts ??= new(StringComparer.Ordinal);
                    counts[signature.Id] = (counts.TryGetValue(signature.Id, out var had) ? had : 0)
                        + (approval?.Counted.Count(c => string.Equals(c.Signature, signature.Id, StringComparison.Ordinal)) ?? 0);
                }
            }
        }

        if (counts is null || _scope.Refusal is not null || _scope.Answered.Count == 0)
            return (counts, null);
        var answered = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var signature in _scope.Answered.SelectMany(g => g.AllSignatures))
            answered[signature.Id] = counts.TryGetValue(signature.Id, out var n) ? n : 0;
        return (counts, answered);
    }

    // The signers of an approval's needed signatures, in file order, each once: the allow for the gated event (decision 18); for an answer on
    // behalf of a delegator, also their delegateTo actors (decision 34).
    private List<string> NeededSigners(ProcessGate gate, ApprovalProgress approval) =>
        [.. gate.AllSignatures.Where(s => approval.Needed.Contains(s.Id))
            .SelectMany(s => _scope.Input?.OnBehalfOf is null ? s.Signers : s.Signers.Concat(s.DelegateTo)).Distinct(StringComparer.Ordinal)];

    // The reminders and escalations due by an instant (decision 35): on each approval's current round, each needed signature not complete whose
    // delay has run out, once per round start, kept with its file order for the trace.
    private void DueNotices(DateTimeOffset until)
    {
        var instances = AllInstances().ToList();
        for (var n = 0; n < instances.Count; n++)
        {
            var inst = instances[n];
            if (!inst.HasApprovals)
                continue;
            for (var i = 0; i < inst.Chart.Transitions.Length; i++)
            {
                var t = inst.Chart.Transitions[i];
                if (t.Gate is not { UsesStages: true } gate || !inst.Approvals.TryGetValue(t.Id, out var approval) || approval.Waiting)
                    continue;
                var position = 0;
                foreach (var signature in gate.AllSignatures)
                {
                    position++;
                    if (!approval.Needed.Contains(signature.Id) || Complete(signature, approval.Counted))
                        continue;
                    Due(approval, signature, signature.RemindAfter, NoticeKind.Remind, signature.Signers, (n, i, position));
                    Due(approval, signature, signature.EscalateAfter, NoticeKind.Escalate, signature.EscalateTo, (n, i, position));
                }
            }
        }

        void Due(ApprovalProgress approval, NamedSignature signature, string? after, NoticeKind kind, IReadOnlyList<string> recipients, (int, int, int) at)
        {
            if (StatechartModel.ParseDuration(after) is { } span && span > TimeSpan.Zero && approval.Started + span <= until && approval.Noticed.Add((signature.Id, kind)))
                _scope.Notices.Add(((at.Item1, at.Item2, at.Item3, kind), new ApprovalNotice(signature.Id, kind, [.. recipients])));
        }
    }

    private void Audit(Instance inst, ProcessGate gate, ChartTransition t, string? signer, string? actor, string? meaning, string? reason, GateOutcome outcome,
        IReadOnlyDictionary<string, JsonElement> attributes, NamedSignature? signature, string? onBehalfOf = null, string? delegateTo = null)
    {
        if (signature is null)
        {
            Audit(inst, gate, t, signer, actor, meaning, reason, outcome, attributes);
            return;
        }

        _scope.Audit.Add(new GateAuditRecord(_options.Instance, inst.Chart.Process.Id, gate.Id, t.Id, ++_auditSequence, signer, actor, meaning, reason,
            ClockText(), outcome, attributes) { Signature = signature.Id, Stage = StageOf(gate, signature).Id, OnBehalfOf = onBehalfOf, DelegateTo = delegateTo });
    }

    private static readonly ImmutableArray<string> ResponseVerbs = ["sign", "reject", "ignore", "withdraw"];

    // The verbs an open approval takes: the responses, and delegate where one of its signatures has delegateTo (decision 34).
    private static IEnumerable<string> ResponseVerbsOf(ProcessGate gate) =>
        gate.AllSignatures.Any(s => s.DelegateTo.Count > 0) ? [.. ResponseVerbs, "delegate"] : ResponseVerbs;
}
