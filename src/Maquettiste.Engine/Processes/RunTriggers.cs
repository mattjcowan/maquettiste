using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Revisions;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// The trigger points of one simulation or replay (trigger-points-design.md section 8): which points a verb runs, asked of the one evaluator
/// (<see cref="TriggerSequences"/>) with the run's object, its entity and its states in the other lifecycles (read from the scenario, as
/// <see cref="RunRights"/> reads them, rights-design.md decision 27); and what another lifecycle's expressions read, the object's row worked out
/// for each expression as a right's condition is. The running lifecycle's own expressions run in the interpreter's session. Created only in a
/// project that uses trigger points, so a run without them pays nothing. Not thread-safe: one per run.
/// </summary>
internal sealed class RunTriggers
{
    private readonly ProcessRuntime _runtime;
    private readonly ModelSnapshot _model;
    private readonly Process _process;
    private readonly ScenarioCast _cast;
    private readonly ScenarioObject? _object;
    // The object's states in each other lifecycle with trigger points that governs it, and the note when a state was assumed.
    private readonly Dictionary<string, (IReadOnlyList<string> States, string? Note)> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Prepared?> _prepared = new(StringComparer.Ordinal);

    /// <summary>Creates the trigger points of one run.</summary>
    /// <param name="runtime">The run's runtime, whose sessions compile other lifecycles' expressions.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's objects, subject and participants, as given.</param>
    public RunTriggers(ProcessRuntime runtime, Process process, ScenarioCast cast)
    {
        _runtime = runtime;
        _model = runtime.Model;
        _process = process;
        _cast = cast;
        Sequences = TriggerSequences.Of(_model);
        _object = cast.Subject is { } id ? cast.Objects.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal)) : null;
        Entity = _object is null ? null : _model.Get<Entity>(_object.Entity);
    }

    /// <summary>The trigger points of a run's model, or <see langword="null"/> when the project uses none (decision 29).</summary>
    /// <param name="runtime">The run's runtime.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <returns>The run's trigger points.</returns>
    /// <param name="revisions">The run's revisions, whose revise moves the other lifecycles (revisions-design.md section 9), or <see langword="null"/>.</param>
    public static RunTriggers? For(ProcessRuntime runtime, Process process, ScenarioCast cast, RunRevisions? revisions = null) =>
        TriggerSequences.Of(runtime.Model).Uses ? new RunTriggers(runtime, process, cast) { Revisions = revisions } : null;

    /// <summary>The run's revisions: after a revise, other lifecycles read the states it gave them, over this run's cache.</summary>
    public RunRevisions? Revisions { get; init; }

    /// <summary>The model's evaluator.</summary>
    public TriggerSequences Sequences { get; }

    /// <summary>The object's entity; <see langword="null"/> when the run has no object.</summary>
    public Entity? Entity { get; }

    /// <summary>
    /// Which trigger points run, by moment, when a verb happens in the run (decisions 10, 11, 18 and 19): the running lifecycle reads its live
    /// states, the others the object's states from the scenario, with a note for each that the run assumed.
    /// </summary>
    /// <param name="verb">The verb's key; <see langword="null"/> for a move with no verb.</param>
    /// <param name="transition">The transition whose move does the verb, or whose gate is signed; <see langword="null"/> for a verb step or a task.</param>
    /// <param name="acting">The running lifecycle's states as the step finds them (for a move, its source).</param>
    /// <param name="attributes">The attributes the step itself reads or changes, or <see langword="null"/>.</param>
    /// <param name="end">The relation end a link verb acts through, or <see langword="null"/>.</param>
    /// <param name="notes">Receives what was assumed.</param>
    /// <returns>The points by moment.</returns>
    public TriggerVerbRun Run(string? verb, string? transition, IReadOnlyList<string> acting, IReadOnlyList<string>? attributes, string? end, List<string> notes)
    {
        if (verb is null)
            return TriggerVerbRun.None(verb);
        var states = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [_process.Id] = acting };
        if (_object is not null && TriggerSequences.SpeaksOfObject(verb))
        {
            foreach (var lifecycle in Sequences.Lifecycles)
            {
                if (!string.Equals(lifecycle.Id, _process.Id, StringComparison.Ordinal) && StatesOf(lifecycle) is { } known)
                    states[lifecycle.Id] = known.States;
            }
        }

        var run = Sequences.For(new TriggerQuery
        {
            Verb = verb, Process = _process, Entity = Entity, States = states, Transition = transition, Attributes = attributes, End = end,
        });
        foreach (var lifecycle in run.Lifecycles)
        {
            if (!string.Equals(lifecycle.Id, _process.Id, StringComparison.Ordinal) && StatesOf(lifecycle) is { Note: { } note })
                notes.Add($"Trigger points of lifecycle '{lifecycle.Name}': {note}");
        }

        notes.AddRange(run.Notes);
        return run;
    }

    /// <summary>MQ9305 on a step's <c>assume</c> words (decision 26, <see cref="VerbStepFields.CheckAssume"/>), or <see langword="null"/>.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The message, without the step's number.</returns>
    public string? AssumeProblem(ScenarioStep step) => VerbStepFields.CheckAssume(_model, _process, _cast, step)?.Message;

    /// <summary>The expression session of another lifecycle, or <see langword="null"/> when none of its expressions compiled.</summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The session.</returns>
    public ProcessExpressionSession? Session(Process lifecycle) => _runtime.Session(ProcessLibraries.Chart(_model, lifecycle));

    /// <summary>The expression session of a named trigger point, keyed by the point, or <see langword="null"/> when none of its expressions compiled.</summary>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The session.</returns>
    public ProcessExpressionSession? Session(TriggerPoint point) => _runtime.Session(point);

    /// <summary>
    /// The object's row a named trigger point's expression reads under <c>subject</c> or <c>old</c> (decision 33): with an object, its row to
    /// the depth the expression reads, with the values given and its states, the running lifecycle's being the live ones; with no object, the
    /// row the run holds.
    /// </summary>
    /// <param name="point">The named trigger point.</param>
    /// <param name="key">The expression's key: a <c>when</c>'s, a condition's or a set's.</param>
    /// <param name="held">The row the run holds, with the same values.</param>
    /// <param name="values">The object's values, by attribute id, that the row holds.</param>
    /// <param name="running">The running lifecycle's live states.</param>
    /// <returns>The row.</returns>
    public JsonElement Row(TriggerPoint point, string key, JsonElement held, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyList<string> running) =>
        _object is null || Prepare(point, key) is not { } prepared ? held : RowOf(prepared, values, running);

    /// <summary>Another lifecycle's states that an <c>in_state</c> over it reads, as a JSON object of its id to its state ids.</summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The states.</returns>
    public JsonElement Running(Process lifecycle) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [lifecycle.Id] = StatesOf(lifecycle)?.States ?? ObjectStates.Of(_model, lifecycle, Entity, _object).States,
        });

    /// <summary>
    /// The object's row another lifecycle's expression reads under <c>subject</c> or <c>old</c>: with an object, its row to the depth the
    /// expression reads, with the values given and each object's states in the lifecycles its <c>in_state</c> reads, the running lifecycle's
    /// being the live ones; with no object, the row the run holds.
    /// </summary>
    /// <param name="lifecycle">The lifecycle whose trigger point it is.</param>
    /// <param name="key">The expression's key: a <c>when</c>'s, a condition's or a set's.</param>
    /// <param name="held">The row the run holds, with the same values.</param>
    /// <param name="values">The object's values, by attribute id, that the row holds.</param>
    /// <param name="running">The running lifecycle's live states.</param>
    /// <returns>The row.</returns>
    public JsonElement Row(Process lifecycle, string key, JsonElement held, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyList<string> running)
    {
        if (_object is null || Prepare(lifecycle, key) is not { } prepared)
            return held;
        return RowOf(prepared, values, running);
    }

    // The object's row to the depth an expression reads, with the values given and its states, the running lifecycle's being the live ones.
    private JsonElement RowOf(Prepared prepared, IReadOnlyDictionary<string, JsonElement> values, IReadOnlyList<string> running)
    {
        var cast = Participants.Current(_cast, values, Revisions);
        var obj = cast.Objects.First(o => string.Equals(o.Id, _object!.Id, StringComparison.Ordinal));
        var runningChart = ProcessLibraries.Chart(_model, _process);
        var live = obj with { States = [.. obj.States.Where(s => !runningChart.ById.ContainsKey(s)), .. running] };
        IReadOnlyList<ScenarioObject> objects = [.. cast.Objects.Select(o => ReferenceEquals(o, obj) ? live : o)];
        return ScenarioObjects.RowWithStates(_model, objects, obj.Id, prepared.Depth, prepared.Stereotype, prepared.Reads);
    }

    // The object's states in another lifecycle that governs it, worked out once; null when the run has no object or the lifecycle doesn't govern it.
    private (IReadOnlyList<string> States, string? Note)? StatesOf(Process lifecycle)
    {
        if (_object is null || Entity is null)
            return null;
        // A revise gave this lifecycle new states, which every later read takes over what was worked out before it.
        if (Revisions?.States(lifecycle.Id) is { } revised)
            return (revised, null);
        if (_states.TryGetValue(lifecycle.Id, out var known))
            return known;
        if (!SubjectCoverage.Governs(_model, lifecycle, Entity))
            return null;
        var found = ObjectStates.Of(_model, lifecycle, Entity, _object);
        _states[lifecycle.Id] = found;
        return found;
    }

    // What an expression reads on the object, typed once per run: how many links deep, the stereotype attributes, and its in_state reads.
    private Prepared? Prepare(Process lifecycle, string key)
    {
        var id = lifecycle.Id + "|" + key;
        if (_prepared.TryGetValue(id, out var known))
            return known;
        Prepared? prepared = null;
        if (ExprResolution.Type(_model, lifecycle, key) is { } typed)
        {
            var stereotype = ExprResolution.Paths(typed.Typed).Where(a => ExprSites.IsStereotypeAttribute(_model, a)).ToHashSet(StringComparer.Ordinal);
            prepared = new Prepared(ExprSites.SubjectPathDepth(lifecycle, typed.Typed) ?? 0, stereotype, ObjectStates.ReadsOf(typed.Typed));
        }

        _prepared[id] = prepared;
        return prepared;
    }

    // What a named trigger point's expression reads on the object, typed once per run in the point's own scope.
    private Prepared? Prepare(TriggerPoint point, string key)
    {
        var id = "named|" + point.Id + "|" + key;
        if (_prepared.TryGetValue(id, out var known))
            return known;
        Prepared? prepared = null;
        if (ExprResolution.Type(_model, point, key) is { } typed)
        {
            var stereotype = ExprResolution.Paths(typed.Typed).Where(a => ExprSites.IsStereotypeAttribute(_model, a)).ToHashSet(StringComparer.Ordinal);
            prepared = new Prepared(ExprSites.SubjectPathDepth(typed.Typed) ?? 0, stereotype, ObjectStates.ReadsOf(typed.Typed));
        }

        _prepared[id] = prepared;
        return prepared;
    }

    private sealed record Prepared(int Depth, IReadOnlySet<string> Stereotype, IReadOnlyList<StateRead> Reads);
}
