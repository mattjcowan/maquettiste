using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Revisions;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// The revisions of a run (revisions-design.md decisions 19, 22 and 37 to 39, section 9). Only the root revises, since an invoked sub-process
/// sees no object, and only when <see cref="InterpreterOptions.Revisions"/> is set, so a project without a <c>revisions</c> block selects,
/// moves and traces as before. A revise verb step makes the new revision in one macrostep; a revise move and a labelling move move the revision
/// after the exit actions; row 7b refuses what the sequence can't give.
/// </summary>
public sealed partial class StatechartInterpreter
{
    // A microstep's revision (decision 19): a revise when any transition taken is a revise move, else a label, at the most significant level
    // any of them names, and whether they named different levels.
    private readonly record struct RevisionStep(bool Revise, int Level, bool Differ);

    // Row 7b of a candidate move (decision 22): the entity that refuses it when the sequence can't give its revision, else null. An automatic
    // move refused so is named in a note, once per step. Only the running chart's moves revise; a step's chosen name applies to its own move.
    private string? RevisionRefused(Instance inst, ChartTransition t, bool own)
    {
        if (_revisions is not { Active: true } revisions || inst.Parent is not null)
            return null;
        var revise = MoveVerb(t) == "revise";
        if (!revise && t.Transition.NextRevision is null)
            return null;
        if (revisions.Next(t.Transition.NextRevision ?? 1, own ? Chosen() : null) is not null)
            return null;
        if (!own && _scope.RefusedMoves.Add("revision:" + t.Id))
            Note($"The revision sequence can't give the revision of the automatic move {Label(t)} from {RunRevisions.Text(revisions.Revision)}, so it was not taken.");
        return revisions.RefusedBy;
    }

    // The chosen name of the step's own event, while it is unused.
    private string? Chosen() =>
        _scope.Input is { Input: StepInput.Event, Revision.Name: { } name } && !_scope.ChoiceUsed ? name : null;

    // The scratch copy's start on a revise move (section 9): the move's proposed values, then the type's reset and cleared values and the bound
    // revision attribute at the next value, under any before action; null when the move doesn't revise the subject.
    private SortedDictionary<string, (string Name, JsonElement Value)>? ReviseStart(ChartTransition t, bool own,
        IReadOnlyDictionary<string, (string Name, JsonElement Value)>? proposed)
    {
        if (_revisions is not { Active: true } revisions || revisions.Next(t.Transition.NextRevision ?? 1, own ? Chosen() : null) is not { } next)
            return null;
        var start = new SortedDictionary<string, (string Name, JsonElement Value)>(StringComparer.Ordinal);
        foreach (var (id, name, value) in revisions.Resets)
            start[id] = (name, value);
        if (revisions.Bound is { } bound)
            start[bound.Id] = (bound.Name, JsonSerializer.SerializeToElement(next));
        foreach (var (id, value) in proposed ?? (IReadOnlyDictionary<string, (string Name, JsonElement Value)>)ImmutableSorted)
            start[id] = value;
        return start;
    }

    // A revise verb step after rows 1 to 9 (section 9, steps 5 to 17), in one macrostep: the reset and cleared values, then the before actions'
    // values, then a replace's sets, written as sets are; the revision moved and the subject thawed; each lifecycle as the type says; the
    // with-change actions on the new revision; then the automatic moves.
    private void Revise(Prepared? prepared, SortedDictionary<string, (string Name, JsonElement Value)> resets, string next, int? level)
    {
        Macrostep(() =>
        {
            if (prepared is not null)
                List(prepared, []);
            Apply(resets);
            if (prepared is not null)
            {
                Write(prepared, []);
                // A replace is a revise site after the before actions, so it reads the new revision's values (decision 38): the bound revision
                // attribute at the next value.
                BindRevision(next);
                Replace(prepared);
            }

            ReviseSubject(next, level, move: false, null);
            if (prepared is not null)
                RunLater(prepared, []);
            RunToCompletion(_root);
            Propagate(_root);
            return true;
        });
    }

    // Steps 7, 8, 9 to 14 and 15 (section 9): the revision moves, the subject thaws, the running lifecycle goes as the type says on a verb step
    // (where the move goes on a revise move), every other lifecycle as the type says, and a note says what each did.
    private void ReviseSubject(string next, int? level, bool move, IReadOnlyList<string>? leaving)
    {
        var revisions = _revisions!;
        var from = revisions.Revision;
        var parts = new List<string>();
        var earlier = new List<string>();
        if (revisions.Running is not null)
            earlier.Add($"{RunRevisions.Leaves(Chart, leaving ?? LiveStates())} of '{Chart.Process.Name}'");
        SetRevision(next, level);
        if (_frozen)
            (_frozen, _frozeAt) = (false, _index);
        if (revisions.Running is { } rule)
        {
            if (move)
            {
                parts.Add($"lifecycle '{Chart.Process.Name}' went where the move goes");
            }
            else if (rule.OnRevise == LifecycleOnRevise.Keep)
            {
                parts.Add($"lifecycle '{Chart.Process.Name}' kept its states");
            }
            else
            {
                var target = rule.OnRevise == LifecycleOnRevise.State && rule.State is { } id && Chart.ById.TryGetValue(id, out var state) ? state : null;
                RestartRoot(target);
                parts.Add(target is null ? $"lifecycle '{Chart.Process.Name}' started over" : $"lifecycle '{Chart.Process.Name}' went to '{target.Path}'");
            }
        }

        revisions.ReviseOthers(parts, earlier);
        _current = revisions.Apply(_current);
        Note($"The subject was revised from {RunRevisions.Text(from)} to {RunRevisions.Text(next)}"
            + (parts.Count > 0 ? ": " + string.Join("; ", parts) : "")
            + (earlier.Count > 0 ? ". The earlier revision stays in " + string.Join(", ", earlier) : "") + ".");
        foreach (var note in revisions.ResetNotes)
            Note(note);
        if (revisions.LinksNote() is { } links)
            Note(links);
    }

    // Step 7: the run's revision becomes the next value, and a bound revision attribute takes it in the row and the run's values directly, not
    // as a set, so the trace's values leave it out and its revision says it.
    private void SetRevision(string next, int? level)
    {
        var revisions = _revisions!;
        _scope.Revision = new RevisionChange(_scope.Revision?.From ?? revisions.Revision, next, level);
        revisions.Revision = next;
        BindRevision(next);
    }

    // A bound revision attribute takes the revision in the row and the run's values, written directly rather than as a set.
    private void BindRevision(string next)
    {
        if (_revisions!.Bound is not { } bound)
            return;
        var value = JsonSerializer.SerializeToElement(next);
        var row = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var member in _current.EnumerateObject())
            row[member.Name] = member.Value;
        row[bound.Name] = value;
        _values[bound.Id] = value;
        _current = JsonSerializer.SerializeToElement(row);
    }

    // The revision a microstep's transitions move (decision 19), or null when none revises or labels.
    private static RevisionStep? RevisionMove(IReadOnlyList<ChartTransition> transitions)
    {
        var any = false;
        var revise = false;
        var differ = false;
        var level = int.MaxValue;
        int? first = null;
        foreach (var t in transitions)
        {
            var isRevise = MoveVerb(t) == "revise";
            if (!isRevise && t.Transition.NextRevision is null)
                continue;
            var named = t.Transition.NextRevision ?? 1;
            any = true;
            revise |= isRevise;
            differ |= first is { } f && f != named;
            first ??= named;
            level = Math.Min(level, named);
        }

        return any ? new RevisionStep(revise, level, differ) : null;
    }

    // A microstep's revision after its exit actions (decision 19, section 9): with no subject object, a note only; a label moves the revision
    // and nothing else; a revise move also writes the type's reset and cleared values (except those a before action set), thaws, and moves the
    // other lifecycles, while the running one goes where the move goes.
    private void MoveRevision(RevisionStep step, List<Prepared>? taken, IReadOnlyList<string>? leaving)
    {
        var revisions = _revisions!;
        if (!revisions.Active)
        {
            Note(revisions.NoObjectNote("the move gave"));
            return;
        }

        var chosen = _scope.OwnMove ? Chosen() : null;
        if (revisions.Next(step.Level, chosen) is not { } next)
            return;
        if (chosen is not null)
            _scope.ChoiceUsed = true;
        if (step.Differ)
            Note($"Transitions taken together named different revision levels, so the revision moved once, at level {step.Level.ToString(CultureInfo.InvariantCulture)}.");
        if (!step.Revise)
        {
            SetRevision(next, chosen is null ? step.Level : null);
            return;
        }

        var filled = new HashSet<string>(taken?.SelectMany(p => p.Fills).SelectMany(f => f.Sets.Keys) ?? [], StringComparer.Ordinal);
        var resets = new SortedDictionary<string, (string Name, JsonElement Value)>(StringComparer.Ordinal);
        foreach (var (id, name, value) in revisions.Resets)
        {
            if (!filled.Contains(id))
                resets[id] = (name, value);
        }

        Apply(resets); // thawed: Microstep counts a revise move as thawing
        ReviseSubject(next, chosen is null ? step.Level : null, move: true, leaving);
    }

    // The start of the root chart again (section 9, steps 10 to 13): every active state left without its exit actions, deepest first, its run
    // state cleared, the context back to the run's start, then the initial state or the named one entered with its entry actions, as one
    // microstep with no transitions.
    private void RestartRoot(ChartState? target)
    {
        var exited = new List<string>();
        foreach (var s in _root.Config.Where(s => !s.IsRoot).OrderByDescending(s => s.Order).ToList())
        {
            Cancel(_root, s);
            _root.Config.Remove(s);
            exited.Add(s.Id);
        }

        _root.Config.Clear();
        _root.History.Clear();
        _root.Timers.Clear();
        _root.Pending.Clear();
        _root.Children.Clear();
        _root.Signatures.Clear();
        _root.Approvals.Clear();
        _root.Acts.Clear();
        _root.EnteredAt.Clear();
        _root.Queue.Clear();
        _root.Done = false;
        _root.Reported = false;
        InitContext(_root, _startContext);
        _root.Config.Add(_root.Chart.Root);
        if ((target ?? _root.Chart.Root.InitialChild) is { } entry)
            Microstep(_root, [], entry, exited);
        else
            _scope.Microsteps.Add(new MicrostepTrace([], exited, [], []));
    }
}
