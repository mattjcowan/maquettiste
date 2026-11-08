using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// The trigger points of a run (trigger-points-design.md decisions 16 to 19 and 24 to 28, section 8). Only the root runs them, since an invoked
/// sub-process sees no object, and only when <see cref="InterpreterOptions.Triggers"/> is set, so a project without trigger points selects,
/// signs and traces as before. For a move, rows 8 and 9 (before actions, then checks) run on a scratch copy of the object's values while the
/// move is picked, and a refusing check leaves the candidate out as a false guard does; rows 10 to 12 follow in the microstep. A verb step runs
/// rows 8 to 12 in order after rights.
/// </summary>
public sealed partial class StatechartInterpreter
{
    private static readonly JsonElement NoEvent =
        JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["name"] = null, ["actor"] = null, ["payload"] = new Dictionary<string, object?>() });

    private static readonly IReadOnlyDictionary<string, JsonElement> NoValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

    // The candidates whose rows 8 and 9 passed, by transition, from selection to their admit and microstep.
    private readonly Dictionary<ChartTransition, Candidate> _candidates = new(ReferenceEqualityComparer.Instance);

    // A candidate's prepared verbs: sign's on a gated move, and the move's own (on a gated move, only on the occurrence that completes the gate).
    private sealed record Candidate(Prepared? Sign, Prepared? Move);

    // One verb's trigger points in one candidate: what rows 8 and 9 did on the scratch copy, and what waits for rows 10 to 12.
    private sealed class Prepared(TriggerVerbRun run, JsonElement old, IReadOnlyDictionary<string, JsonElement> oldValues)
    {
        public TriggerVerbRun Run { get; } = run;

        // The object's values before the verb, which old. reads: its row, or JSON null on create; and by attribute id for other lifecycles' rows.
        public JsonElement Old { get; } = old;

        public IReadOnlyDictionary<string, JsonElement> OldValues { get; } = oldValues;

        // The proposed values: the step's own values, then each before action's sets on top.
        public SortedDictionary<string, (string Name, JsonElement Value)> Proposed { get; } = new(StringComparer.Ordinal);

        // Each before action's sets, by point, written once the candidate is taken.
        public List<(string Point, SortedDictionary<string, (string Name, JsonElement Value)> Sets)> Fills { get; } = [];

        // Rows 8 and 9 in run order, listed only when the candidate is taken.
        public List<TriggerPointRun> Runs { get; } = [];

        public TriggerPointRun? Refused { get; set; }

        public string? RefusedName { get; set; }

        public bool Listed { get; set; }

        // Whether a replace took the place of the verb's effect (a verb step's only, decision 16).
        public bool Replaced { get; set; }

        // Whether a verb step or a task's end prepared it, rather than a move or a signature.
        public bool VerbStep { get; set; }
    }

    /// <summary>
    /// The team-code checks the moves of some transitions reach (<c>checksUnknown</c>, trigger-points-design.md section 9), whose outcome a step
    /// takes from its <c>assume</c>: the checks of each move's verb and, on a gated move, of <c>sign</c>, in the order found, each once. On a
    /// gated move the move's own checks are listed only when the next signature can complete the gate, as only that one runs them (decision 18).
    /// </summary>
    /// <param name="transitions">The candidate transitions' ids.</param>
    /// <returns>The trigger point ids; empty in a project without trigger points.</returns>
    internal IReadOnlyList<string> ChecksUnknown(IEnumerable<string> transitions)
    {
        ArgumentNullException.ThrowIfNull(transitions);
        if (_options.Triggers is not { } triggers || _root.Done)
            return [];
        var found = new List<string>();
        var notes = new List<string>();
        foreach (var id in transitions)
        {
            if (_root.Chart.Transitions.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.Ordinal)) is not { } t)
                continue;
            // An open approval takes every response; one that needs nothing moves as a move without a gate (signoff-design.md decision 17).
            var gate = t.Gate is { UsesStages: true } && !OpenApproval(_root, t) ? null : t.Gate;
            IEnumerable<string?> verbs = gate is { UsesStages: true } ? [.. ResponseVerbs] : [gate is not null ? "sign" : null];
            foreach (var verb in verbs.Append(gate is null || CouldComplete(_root, t, gate) ? MoveVerb(t) : null))
            {
                if (verb is null)
                    continue;
                foreach (var check in triggers.Run(verb, t.Id, SourceStates(t), null, null, notes).Checks)
                {
                    if (check.Point.TeamCode && !found.Contains(check.Point.Id, StringComparer.Ordinal))
                        found.Add(check.Point.Id);
                }
            }
        }

        return found;
    }

    // The verb a move does: its transition's own, else its event's (decision 4); null runs no trigger points.
    private static string? MoveVerb(ChartTransition t) => t.Transition.Verb ?? t.Event?.Verb;

    // The running lifecycle's active states, which a verb step and a task's end read.
    private IReadOnlyList<string> LiveStates() => [.. _root.Config.Where(s => !s.IsRoot).OrderBy(s => s.Order).Select(s => s.Id)];

    // The states a move leaves (decision 10's states): its source, with the active states inside it.
    private IReadOnlyList<string> SourceStates(ChartTransition t)
    {
        var inside = _root.Config.Where(s => s.IsLeaf && (ReferenceEquals(s, t.Source) || s.IsDescendantOf(t.Source))).OrderBy(s => s.Order).Select(s => s.Id).ToList();
        return inside.Count > 0 ? inside : [t.Source.Id];
    }

    /// <summary>
    /// Rows 8 and 9 of a verb step or a task's end (decision 18): its trigger points, then its before actions and checks on a scratch copy of the
    /// object's values. <see langword="null"/> when no trigger point matches.
    /// </summary>
    private Prepared? PrepareVerb(string verb, SortedDictionary<string, (string Name, JsonElement Value)>? own, IReadOnlyList<string>? attributes, string? end, bool create)
    {
        if (_options.Triggers is not { } triggers)
            return null;
        var notes = new List<string>();
        var run = triggers.Run(verb, null, LiveStates(), attributes, end, notes);
        foreach (var note in notes)
            Note(note);
        if (run.IsEmpty)
            return null;
        var prepared = Prepare(run, own, create ? Null : _current, create ? NoValues : new Dictionary<string, JsonElement>(_values, StringComparer.Ordinal));
        prepared.VerbStep = true;
        return prepared;
    }

    /// <summary>
    /// Rows 8 and 9 of a candidate move (decision 18), after its guard and, on a gated move, after who signs: on a gated move, <c>sign</c>'s
    /// before actions and checks, and on the occurrence that would complete the gate the move's own after them, on the same scratch copy; else
    /// the move's verb's. <see langword="null"/> when the candidate passes, its rows kept for its admit and microstep; otherwise the check that
    /// refused it. A refused signature is audited as refused and not counted; a refused automatic move is named in a note.
    /// </summary>
    private TriggerPointRun? TriggerCandidate(Instance inst, ChartTransition t, bool external)
    {
        _candidates.Remove(t);
        if (_options.Triggers is not { } triggers || inst.Parent is not null)
            return null;
        // On an approval the response's verb runs where sign's run (signoff-design.md decision 17); one that needs nothing moves as a move
        // without a gate.
        var gate = external && !(t.Gate is { UsesStages: true } && !OpenApproval(inst, t)) ? t.Gate : null;
        var response = gate is { UsesStages: true } ? _scope.Input!.Response : SignatureResponse.Sign;
        var verb = MoveVerb(t);
        if (verb is null && gate is null)
            return null;
        var notes = new List<string>();
        var source = SourceStates(t);
        var signRun = gate is not null ? triggers.Run(VerbRules.ResponseKey(response), t.Id, source, null, null, notes) : null;
        var moveRun = verb is not null && (gate is null || (response == SignatureResponse.Sign && WouldComplete(inst, t, gate)))
            ? triggers.Run(verb, t.Id, source, null, null, notes) : null;
        foreach (var note in notes)
            Note(note);
        if ((signRun is null || signRun.IsEmpty) && (moveRun is null || moveRun.IsEmpty))
            return null;
        var oldValues = new Dictionary<string, JsonElement>(_values, StringComparer.Ordinal);
        var sign = signRun is { IsEmpty: false } ? Prepare(signRun, null, _current, oldValues) : null;
        var move = sign?.Refused is null && moveRun is { IsEmpty: false } ? Prepare(moveRun, sign?.Proposed, _current, oldValues) : null;
        var refused = sign?.Refused is not null ? sign : move?.Refused is not null ? move : null;
        if (refused is not null)
        {
            if (gate is not null)
            {
                var input = _scope.Input!;
                var (meaning, attributes) = AuditFields(input, gate);
                Audit(inst, gate, t, input.Signer, input.Actor, meaning, input.Reason, GateOutcome.Refused, attributes, gate.UsesStages ? _picks.GetValueOrDefault(t) : null);
            }

            // Each refused automatic move is noted once per step, though RunToCompletion tries it again after every microstep.
            if (!external && _scope.RefusedMoves.Add(t.Id))
                Note($"Check '{refused.RefusedName}' refused the automatic move {Label(t)}, so it was not taken; the step that led to it is still accepted.");
            return refused.Refused;
        }

        _candidates[t] = new Candidate(sign, move);
        return null;
    }

    // Whether the next signature, by whoever signs, brings the gate to its count; who signs it may still leave a required actor missing. On an
    // approval: at most one signature is missing across the round's needed signatures, or nothing is needed.
    private bool CouldComplete(Instance inst, ChartTransition t, ProcessGate gate)
    {
        if (gate.UsesStages)
        {
            var approval = ApprovalOf(inst, t, gate);
            return approval.Waiting || gate.AllSignatures.Where(s => approval.Needed.Contains(s.Id) && !Complete(s, approval.Counted))
                .Sum(s => Math.Max(1, s.Required) - approval.Counted.Count(c => string.Equals(c.Signature, s.Id, StringComparison.Ordinal))) <= 1;
        }

        return (inst.Signatures.TryGetValue(t.Id, out var existing) ? existing.Count : 0) + 1 >= Math.Max(1, gate.Required);
    }

    // Whether this occurrence would complete the gate, worked out without counting it, as Sign counts. On an approval: a signature on the
    // picked signature that completes the round.
    private bool WouldComplete(Instance inst, ChartTransition t, ProcessGate gate)
    {
        var input = _scope.Input!;
        if (gate.UsesStages)
        {
            var approval = ApprovalOf(inst, t, gate);
            return approval.Waiting || (input.Response == SignatureResponse.Sign && _picks.TryGetValue(t, out var picked)
                && RoundDone(gate, approval, [.. approval.Counted, new CountedSignature(input.Signer!, input.Actor!, null, null, picked.Id)]));
        }

        var signatures = inst.Signatures.TryGetValue(t.Id, out var existing) ? existing : [];
        return signatures.Count + 1 >= Math.Max(1, gate.Required)
            && gate.RequiredActors.All(a => signatures.Any(s => Accepts(a, s.Actor)) || Accepts(a, input.Actor!));
    }

    /// <summary>
    /// The admit of a gated candidate (decision 18): <c>sign</c> as a verb of its own around the count, on every signature: its before values
    /// written, the count made, then on a completing occurrence the move's before actions and checks listed, then <c>sign</c>'s with-change
    /// points and its queued after-commit points. Whether the gate completes.
    /// </summary>
    private bool Admit(Instance inst, ChartTransition t, ProcessGate gate)
    {
        if (!_candidates.TryGetValue(t, out var candidate))
            return gate.UsesStages ? Respond(inst, t, gate) : Sign(inst, t, gate);
        if (candidate.Sign is { } sign)
        {
            List(sign, []);
            Write(sign, []);
        }

        var complete = gate.UsesStages ? Respond(inst, t, gate) : Sign(inst, t, gate);
        if (complete && candidate.Move is { } move)
            List(move, []);
        if (candidate.Sign is { } signed)
            RunLater(signed, []);
        return complete;
    }

    // The prepared moves of the transitions a microstep takes, in selection order; null when none has trigger points.
    private List<Prepared>? TakenCandidates(Instance inst, IReadOnlyList<ChartTransition> transitions)
    {
        if (_candidates.Count == 0 || inst.Parent is not null)
            return null;
        List<Prepared>? taken = null;
        foreach (var t in transitions)
        {
            if (_candidates.Remove(t, out var candidate) && candidate.Move is { } move)
                (taken ??= []).Add(move);
        }

        return taken;
    }

    // Rows 8 and 9 on a scratch copy: before actions fill the proposed values in turn, then the checks run; the first refusing check ends it.
    private Prepared Prepare(TriggerVerbRun run, IReadOnlyDictionary<string, (string Name, JsonElement Value)>? start, JsonElement old,
        IReadOnlyDictionary<string, JsonElement> oldValues)
    {
        var prepared = new Prepared(run, old, oldValues);
        foreach (var (id, value) in start ?? ImmutableSorted)
            prepared.Proposed[id] = value;
        foreach (var before in run.Before)
        {
            var (row, values) = Scratch(prepared);
            if (!When(before, row, values, prepared))
            {
                prepared.Runs.Add(Entry(before, TriggerOutcome.Skipped));
                continue;
            }

            var sets = TriggerSets(before, row, values, prepared);
            foreach (var (id, value) in sets)
                prepared.Proposed[id] = value;
            if (sets.Count > 0)
                prepared.Fills.Add((before.Point.Id, sets));
            prepared.Runs.Add(Entry(before, TriggerOutcome.Ran, sets));
        }

        var assume = _scope.Input?.Assume;
        foreach (var check in run.Checks)
        {
            var (row, values) = Scratch(prepared);
            if (!When(check, row, values, prepared))
            {
                prepared.Runs.Add(Entry(check, TriggerOutcome.Skipped));
                continue;
            }

            var point = check.Point;
            var warning = point.Severity == TriggerSeverity.Warning;
            TriggerOutcome outcome;
            var assumed = false;
            if (point.Condition is not null)
            {
                outcome = TriggerCondition(check, point.Id, $"Check '{point.Name}'", row, values, prepared) == true ? TriggerOutcome.Passed
                    : warning ? TriggerOutcome.Warned : TriggerOutcome.Refused;
            }
            else
            {
                // A check the team writes takes the word the step assumes; without one it passes. A warning check never refuses.
                assumed = true;
                outcome = (assume is not null && assume.TryGetValue(point.Id, out var entry) ? entry.Check : null) switch
                {
                    AssumedCheck.Warning => TriggerOutcome.Warned,
                    AssumedCheck.Error => warning ? TriggerOutcome.Warned : TriggerOutcome.Refused,
                    _ => TriggerOutcome.Passed,
                };
            }

            var listed = Entry(check, outcome) with { Assumed = assumed };
            prepared.Runs.Add(listed);
            if (outcome == TriggerOutcome.Refused)
            {
                prepared.Refused = listed;
                prepared.RefusedName = point.Name;
                break;
            }
        }

        return prepared;
    }

    private static readonly SortedDictionary<string, (string Name, JsonElement Value)> ImmutableSorted = new(StringComparer.Ordinal);

    // The scratch copy's row and values: the object's row with the proposed values on top.
    private (JsonElement Row, IReadOnlyDictionary<string, JsonElement> Values) Scratch(Prepared prepared)
    {
        if (prepared.Proposed.Count == 0)
            return (_current, _values);
        var row = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in _current.EnumerateObject())
            row[member.Name] = member.Value;
        var values = new Dictionary<string, JsonElement>(_values, StringComparer.Ordinal);
        foreach (var (id, (name, value)) in prepared.Proposed)
        {
            row[name] = value;
            values[id] = value;
        }

        return (JsonSerializer.SerializeToElement(row), values);
    }

    // A trace entry of a trigger point (decision 28).
    private static TriggerPointRun Entry(TriggerRun run, TriggerOutcome outcome, SortedDictionary<string, (string Name, JsonElement Value)>? sets = null) =>
        new(run.Point.Id, run.Lifecycle?.Id, run.Verb, run.Point.Type, run.Point.Type == TriggerPointType.Action ? run.Point.Timing : null, outcome)
        {
            Outside = run.Point.Outside,
            Sets = sets is { Count: > 0 } ? sets.ToDictionary(p => p.Key, p => p.Value.Value, StringComparer.Ordinal) : null,
        };

    // Lists a taken candidate's before actions and checks, each point once per microstep, with its warnings (decision 28).
    private void List(Prepared prepared, HashSet<string> listed)
    {
        if (prepared.Listed)
            return;
        prepared.Listed = true;
        foreach (var entry in prepared.Runs)
        {
            if (!listed.Add(entry.TriggerPoint))
                continue;
            _scope.TriggerRuns.Add(entry);
            if (entry.Outcome == TriggerOutcome.Warned && !_scope.CheckWarnings.Contains(entry.TriggerPoint, StringComparer.Ordinal))
                _scope.CheckWarnings.Add(entry.TriggerPoint);
        }
    }

    // Writes a taken candidate's before values once the step is accepted, each point once per microstep; a later point wins an attribute.
    private void Write(Prepared prepared, HashSet<string> written)
    {
        var sets = new SortedDictionary<string, (string Name, JsonElement Value)>(StringComparer.Ordinal);
        foreach (var (point, fill) in prepared.Fills)
        {
            if (!written.Add(point))
                continue;
            foreach (var (id, value) in fill)
                sets[id] = value;
        }

        ApplyTrigger(sets);
    }

    // What a trigger point sets is part of its verb (decision 25): freezing never stops it, and a note says so.
    private void ApplyTrigger(SortedDictionary<string, (string Name, JsonElement Value)> sets)
    {
        if (sets.Count == 0)
            return;
        if (_frozen && !_scope.Thaws)
            Note("The object is frozen, but a trigger point's sets applied: what a trigger point sets is part of its verb, so freezing does not stop it.");
        Apply(sets);
    }

    /// <summary>Row 10 on a verb step: the replace that takes the place of its effect, when its <c>when</c> holds (decision 16).</summary>
    private bool Replace(Prepared prepared)
    {
        if (prepared.Run.Replace is not { } replace)
            return false;
        if (!When(replace, _current, _values, prepared))
        {
            _scope.TriggerRuns.Add(Entry(replace, TriggerOutcome.Skipped));
            return false;
        }

        var sets = TriggerSets(replace, _current, _values, prepared);
        // Copy and revise make a new object the run doesn't hold: the trace shows the sets, and nothing is written.
        if (replace.Verb is not ("copy" or "revise"))
            ApplyTrigger(sets);
        _scope.TriggerRuns.Add(Entry(replace, TriggerOutcome.Replaced, sets));
        prepared.Replaced = true;
        return true;
    }

    // Rows 11 and 12: with-the-change actions now, on the object after the verb; after-commit ones queued for the end of the step. Each point
    // once per microstep. After a verb step's built-in delete there is no object to set (decision 16), so a with-change action with sets is
    // skipped with one note: the evaluator's when no replace matched, this one when a replace matched but its when didn't hold. An event that
    // names delete still does its move and the object stays, so its with-change actions run with their sets.
    private void RunLater(Prepared prepared, HashSet<string> seen)
    {
        var gone = prepared.VerbStep && string.Equals(prepared.Run.Verb, "delete", StringComparison.Ordinal) && !prepared.Replaced;
        foreach (var later in prepared.Run.WithChange)
        {
            if (!seen.Add("with-change:" + later.Point.Id))
                continue;
            if (!When(later, _current, _values, prepared))
            {
                _scope.TriggerRuns.Add(Entry(later, TriggerOutcome.Skipped));
                continue;
            }

            if (gone && later.Point.Sets.Count > 0)
            {
                if (prepared.Run.Replace is { } unused)
                    Note($"Replace '{unused.Point.Name}' did not run, so the sets of '{later.Point.Name}' are skipped: after a built-in delete there is no object to set.");
                _scope.TriggerRuns.Add(Entry(later, TriggerOutcome.Skipped));
                continue;
            }

            var sets = TriggerSets(later, _current, _values, prepared);
            ApplyTrigger(sets);
            _scope.TriggerRuns.Add(Entry(later, TriggerOutcome.Ran, sets));
        }

        foreach (var later in prepared.Run.AfterCommit)
        {
            if (!seen.Add("after-commit:" + later.Point.Id))
                continue;
            if (When(later, _current, _values, prepared))
                _scope.Queued.Add(Entry(later, TriggerOutcome.Queued));
            else
                _scope.TriggerRuns.Add(Entry(later, TriggerOutcome.Skipped));
        }
    }

    // A trigger point's when: only true runs it; false, unknown or one that didn't compile skips it (decision 15).
    private bool When(TriggerRun run, JsonElement row, IReadOnlyDictionary<string, JsonElement> values, Prepared prepared) =>
        run.Point.When is null || TriggerCondition(run, ExprSites.WhenKey(run.Point.Id), $"Trigger point '{run.Point.Name}'", row, values, prepared) == true;

    // A when or a check's condition at a trigger site, over the object as the moment reads it and its values before the verb: true or false;
    // null when it didn't compile (validation says why). One that throws or runs past a limit counts as false, with its finding.
    private bool? TriggerCondition(TriggerRun run, string key, string label, JsonElement row, IReadOnlyDictionary<string, JsonElement> values, Prepared prepared)
    {
        if (TriggerSite(run, key, row, values, prepared) is not { } site)
            return null;
        var result = site.Session.EvaluateCondition(key, label, NoObject, site.Event, site.Subject, site.Running, ClockText(), site.Identity, Seed(_root),
            _options.CancellationToken, site.Old);
        if (result.Diagnostic is { } d)
        {
            _scope.Diagnostics.Add(d);
            return false;
        }

        return result.Value is true;
    }

    // A trigger point's sets: every value worked out first, then all applied by the caller (decision 25). A set that didn't compile is left out.
    private SortedDictionary<string, (string Name, JsonElement Value)> TriggerSets(TriggerRun run, JsonElement row, IReadOnlyDictionary<string, JsonElement> values,
        Prepared prepared)
    {
        var sets = new SortedDictionary<string, (string Name, JsonElement Value)>(StringComparer.Ordinal);
        foreach (var set in run.Point.Sets)
        {
            if (TriggerSite(run, set.Id, row, values, prepared) is not { } site || site.Lowered.Set is not { } target)
                continue;
            var result = site.Session.EvaluateSet(set.Id, $"Trigger point '{run.Point.Name}'", NoObject, site.Event, site.Subject, site.Running, ClockText(),
                site.Identity, Seed(_root), _options.CancellationToken, site.Old);
            if (result.Diagnostic is { } d)
                _scope.Diagnostics.Add(d);
            else if (result.Value is JsonElement value)
                sets[target.Attribute] = (target.Name, value);
        }

        return sets;
    }

    private sealed record Site(ProcessExpressionSession Session, ProcessExpressions.LoweredCondition Lowered, JsonElement Event, JsonElement Subject,
        JsonElement? Running, string? Identity, JsonElement Old);

    // What an expression of a trigger point reads (decision 13): the running lifecycle's in the root's session, over the row the run holds,
    // the event and the live states; another lifecycle's in its own session, over the row worked out for it and its states from the scenario,
    // as a right's condition reads them (rights-design.md decision 6). A named trigger point (decision 33) reads in its own session, keyed by the point, over the object's row as another lifecycle's point
    // reads it, with no event, since it reads no payload.
    private Site? TriggerSite(TriggerRun run, string key, JsonElement row, IReadOnlyDictionary<string, JsonElement> values, Prepared prepared)
    {
        var triggers = _options.Triggers!;
        if (run.Lifecycle is not { } lifecycle)
        {
            if (run.Named is not { } named || triggers.Session(named) is not { } own || !own.Lowered.TryGetValue(key, out var reads))
                return null;
            var live = LiveStates();
            var before = prepared.Old.ValueKind == JsonValueKind.Null ? prepared.Old : triggers.Row(named, key, prepared.Old, prepared.OldValues, live);
            return new Site(own, reads, NoEvent, triggers.Row(named, key, row, values, live), null, reads.EngineOnly ? Reads(_root, reads) : Identity(), before);
        }

        var acting = string.Equals(lifecycle.Id, _root.Chart.Process.Id, StringComparison.Ordinal);
        var session = acting ? Session(_root.Chart) : triggers.Session(lifecycle);
        if (session is null || !session.Lowered.TryGetValue(key, out var lowered))
            return null;
        if (acting)
        {
            return new Site(session, lowered, _scope.Event, row, lowered.ReadsRunning ? Running(_root) : null, lowered.EngineOnly ? Reads(_root, lowered) : Identity(),
                prepared.Old);
        }

        var running = LiveStates();
        var old = prepared.Old.ValueKind == JsonValueKind.Null ? prepared.Old : triggers.Row(lifecycle, key, prepared.Old, prepared.OldValues, running);
        return new Site(session, lowered, NoEvent, triggers.Row(lifecycle, key, row, values, running),
            lowered.ReadsRunning ? triggers.Running(lifecycle) : null, Identity(), old);
    }
}
