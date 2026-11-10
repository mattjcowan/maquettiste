using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Revisions;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>What the interpreter asks of a run's rights check for one input (rights-design.md section 7).</summary>
internal sealed record RunCheck
{
    /// <summary>The verb's key; <see langword="null"/> for an event that names none.</summary>
    public string? Verb { get; init; }

    /// <summary>The event's or the human task's actors, the closed list the second check row reads.</summary>
    public IReadOnlyList<RightsList> Closed { get; init; } = [];

    /// <summary>The gates whose signers count as an allow (decision 14).</summary>
    public IReadOnlyList<RightsList> Signers { get; init; } = [];

    /// <summary>The subject's relation end, for a verb on a link; <see langword="null"/> for the object.</summary>
    public string? End { get; init; }

    /// <summary>Whether the subject is frozen as the input runs (rights-design.md decisions 16 to 18).</summary>
    public bool Frozen { get; init; }

    /// <summary>Whether the link the verb acts on is frozen; with no end known, whether a link of the subject is.</summary>
    public bool LinkFrozen { get; init; }

    /// <summary>
    /// The attributes the verb reads or modifies (decision 21): a <c>modify</c> step's <c>values</c>, a <c>read</c> step's <c>attributes</c>;
    /// <see langword="null"/> when not known, such as for an event that names <c>modify</c>.
    /// </summary>
    public IReadOnlyList<string>? Attributes { get; init; }

    /// <summary>The step as it runs: its actor is the one it acts as.</summary>
    public required ScenarioStep Step { get; init; }

    /// <summary>The running chart's active states, which its rights and any <c>in_state</c> over it read (decision 6).</summary>
    public required IReadOnlyList<string> Running { get; init; }

    /// <summary>The identity <c>current_actor()</c> reads, or <see langword="null"/>.</summary>
    public string? Identity { get; init; }

    /// <summary>The simulated clock, which <c>now()</c> reads.</summary>
    public required string Clock { get; init; }

    /// <summary>The object's row as the run holds it, with the values the run set; read when the run has no object.</summary>
    public required JsonElement Subject { get; init; }

    /// <summary>The values the run set on the object so far, by attribute id.</summary>
    public required IReadOnlyDictionary<string, JsonElement> Values { get; init; }

    /// <summary>The seed of <c>Math.random</c>.</summary>
    public required string Seed { get; init; }
}

/// <summary>A run's rights check: the answer, the notes it adds to the trace, and the conditions that stopped (MQ9502, MQ9503).</summary>
/// <param name="Decision">The evaluator's answer.</param>
/// <param name="Notes">What the check assumed: another lifecycle's state, or a run with no object.</param>
/// <param name="Diagnostics">Conditions that threw or ran past a limit; each counts as false.</param>
internal sealed record RunCheckResult(RightsDecision Decision, IReadOnlyList<string> Notes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// The rights check of one simulation or replay (rights-design.md sections 3 and 7): it asks <see cref="RightsEvaluator"/> with the run's
/// participant, the object's entity, the running chart's live states and the other lifecycles' states from the scenario (decision 27), and
/// works out each right's condition through the lifecycle's compiled expressions at the right site. A condition reads the object with the
/// values the run set, and <c>in_state</c> on the running lifecycle reads the live states, from any answering lifecycle's right. Not
/// thread-safe: one per run.
/// </summary>
internal sealed class RunRights
{
    private static readonly JsonElement NoContext = JsonDocument.Parse("{}").RootElement.Clone();

    private readonly ProcessRuntime _runtime;
    private readonly ModelSnapshot _model;
    private readonly Process _process;
    private readonly ScenarioCast _cast;
    private readonly Dictionary<string, Prepared?> _prepared = new(StringComparer.Ordinal);
    private Dictionary<string, ModelAttribute>? _held;

    /// <summary>Creates the check of one run.</summary>
    /// <param name="runtime">The run's runtime, whose sessions compile the conditions.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's objects, subject and participants, as given.</param>
    public RunRights(ProcessRuntime runtime, Process process, ScenarioCast cast)
    {
        _runtime = runtime;
        _model = runtime.Model;
        _process = process;
        _cast = cast;
        Evaluator = RightsEvaluator.Of(_model);
    }

    /// <summary>The model's evaluator.</summary>
    public RightsEvaluator Evaluator { get; }

    /// <summary>The run's revisions: after a revise, other lifecycles answer in the states it gave them (revisions-design.md section 9).</summary>
    public RunRevisions? Revisions { get; init; }

    /// <summary>Whether the project uses rights (decision 28).</summary>
    public bool Uses => Evaluator.Uses;

    /// <summary>Whether the subject starts frozen (rights-design.md decision 16): the scenario's subject object gives <c>frozen</c>.</summary>
    public bool StartsFrozen => SubjectObject?.Frozen == true;

    /// <summary>The subject's links that start frozen (decision 16), each by its relation end and linked object, in document order.</summary>
    public IEnumerable<(string End, string Object)> FrozenLinks =>
        SubjectObject?.Links.Where(l => l.Frozen).Select(l => (l.End, l.Object)) ?? [];

    private ScenarioObject? SubjectObject =>
        _cast.Subject is { } id ? _cast.Objects.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal)) : null;

    /// <summary>MQ9305 on a verb step's fields (<see cref="VerbStepFields"/>), or <see langword="null"/>.</summary>
    /// <param name="step">The step.</param>
    /// <returns>The message, without the step's number, and the field.</returns>
    public (string Message, string Field)? Fields(ScenarioStep step) => VerbStepFields.Check(_runtime, _process, _cast, step);

    /// <summary>The name of an attribute a <c>modify</c> step sets on the object, or <see langword="null"/> when no set could set it.</summary>
    /// <param name="id">The attribute id.</param>
    /// <returns>The name.</returns>
    public string? AttributeName(string id) =>
        (_held ??= ExprSites.HeldAttributes(_model, _process)).TryGetValue(id, out var attribute) ? attribute.Name : null;

    /// <summary>Checks one input (decisions 10 to 12).</summary>
    /// <param name="check">What the interpreter asks.</param>
    /// <returns>The answer, with notes and diagnostics.</returns>
    public RunCheckResult Check(RunCheck check)
    {
        var cast = Participants.Current(_cast, check.Values, Revisions);
        var obj = cast.Subject is { } subjectId ? cast.Objects.FirstOrDefault(o => string.Equals(o.Id, subjectId, StringComparison.Ordinal)) : null;
        var entity = obj is null ? null : _model.Get<Entity>(obj.Entity);
        var caller = check.Step.Participant is { } participantId && cast.Participant(participantId) is { } participant
            ? RightsCaller.Participant(_model, _process, cast, participant, check.Step.Actor)
            : RightsCaller.Acting(_model, check.Step.Actor);
        var query = new RightsQuery
        {
            Verb = check.Verb, Process = _process, Entity = entity, Caller = caller, End = check.End, Closed = check.Closed, Signers = check.Signers,
            Frozen = check.Frozen, LinkFrozen = check.LinkFrozen, Attributes = check.Attributes,
        };

        // The states each answering lifecycle reads: the running chart's live states, else the object's from the scenario (decision 27). With no
        // object the evaluator reads other lifecycles in their initial states and says so.
        var notes = new List<string>();
        var states = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [_process.Id] = check.Running };
        var (answering, _) = Evaluator.Answering(query);
        foreach (var lifecycle in answering)
        {
            if (obj is null || states.ContainsKey(lifecycle.Id))
                continue;
            var (active, note) = ObjectStates.Of(_model, lifecycle, entity, obj);
            states[lifecycle.Id] = active;
            if (note is not null)
                notes.Add($"Object '{obj.Name}' gives no state in lifecycle '{lifecycle.Name}', so its rights answer in {Paths(lifecycle, active)}.");
        }

        var diagnostics = new List<Diagnostic>();
        var decision = Evaluator.Check(query with { States = states, Condition = rule => Condition(rule, check, cast, obj, entity, states, diagnostics) });
        notes.AddRange(decision.Notes);
        return new RunCheckResult(decision, notes, diagnostics);
    }

    // A right's condition in a run (decision 6): true or false, never unknown, since a run knows the object and the caller. One that did not
    // compile says nothing; validation says why. One that throws or runs past a limit counts as false, with its finding.
    private bool? Condition(RightRule rule, RunCheck check, ScenarioCast cast, ScenarioObject? obj, Entity? entity,
        Dictionary<string, IReadOnlyList<string>> states, List<Diagnostic> diagnostics)
    {
        var lifecycle = rule.Lifecycle;
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        if (_runtime.Session(chart) is not { } session || !session.Lowered.TryGetValue(rule.Entry.Id, out var lowered)
            || Prepare(lifecycle, rule.Entry.Id) is not { } prepared)
            return false;
        JsonElement? running = lowered.ReadsRunning
            ? JsonSerializer.SerializeToElement(new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [lifecycle.Id] = StatesIn(lifecycle) })
            : null;
        var subject = SubjectRow(prepared, check, cast, obj);
        var result = session.EvaluateCondition(rule.Entry.Id, $"Right '{rule.Name}'", NoContext, NoContext, subject, running, check.Clock, check.Identity, check.Seed,
            _runtime.CancellationToken);
        if (result.Diagnostic is { } d)
        {
            diagnostics.Add(d);
            return false;
        }

        return result.Value is true;

        IReadOnlyList<string> StatesIn(Process l) =>
            states.TryGetValue(l.Id, out var known) ? known : ObjectStates.Of(_model, l, entity, obj).States;
    }

    // The object a condition reads under subject: with an object, its row to the depth the condition reads, each object holding its states in
    // the lifecycles an in_state reads at its path, the subject's state in the running lifecycle being the live one. With no object, the row
    // the run holds (defaults and the values it set) with the states an in_state reads on it.
    private JsonElement SubjectRow(Prepared prepared, RunCheck check, ScenarioCast cast, ScenarioObject? obj)
    {
        var runningChart = ProcessLibraries.Chart(_model, _process);
        if (obj is not null)
        {
            var live = obj with { States = [.. obj.States.Where(s => !runningChart.ById.ContainsKey(s)), .. check.Running] };
            IReadOnlyList<ScenarioObject> objects = [.. cast.Objects.Select(o => ReferenceEquals(o, obj) ? live : o)];
            return ScenarioObjects.RowWithStates(_model, objects, obj.Id, prepared.Depth, prepared.Stereotype, prepared.Reads);
        }

        var own = prepared.Reads.Where(r => r.Path.Count == 0).ToList();
        if (own.Count == 0)
            return check.Subject;
        var row = JsonNode.Parse(check.Subject.ValueKind == JsonValueKind.Object ? check.Subject.GetRawText() : "{}")!.AsObject();
        var held = new JsonObject();
        foreach (var read in own)
        {
            if (_model.Get<Process>(read.Lifecycle) is not { } lifecycle || held.ContainsKey(lifecycle.Id))
                continue;
            var active = string.Equals(lifecycle.Id, _process.Id, StringComparison.Ordinal) ? check.Running : ObjectStates.Of(_model, lifecycle, null, null).States;
            held[lifecycle.Id] = new JsonArray([.. active.Select(s => (JsonNode?)JsonValue.Create(s))]);
        }

        row[ObjectStates.Member] = held;
        return JsonSerializer.SerializeToElement(row);
    }

    // What a right's condition reads on the object, typed once per run: how many links deep, the stereotype attributes, and its in_state reads.
    private Prepared? Prepare(Process lifecycle, string entryId)
    {
        var key = lifecycle.Id + "|" + entryId;
        if (_prepared.TryGetValue(key, out var known))
            return known;
        Prepared? prepared = null;
        if (ExprResolution.Type(_model, lifecycle, entryId) is { } typed)
        {
            var stereotype = ExprResolution.Paths(typed.Typed).Where(id => ExprSites.IsStereotypeAttribute(_model, id)).ToHashSet(StringComparer.Ordinal);
            prepared = new Prepared(ExprSites.SubjectPathDepth(lifecycle, typed.Typed) ?? 0, stereotype, ObjectStates.ReadsOf(typed.Typed));
        }

        _prepared[key] = prepared;
        return prepared;
    }

    private string Paths(Process lifecycle, IReadOnlyList<string> states)
    {
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        var leaves = states.Where(id => chart.ById.TryGetValue(id, out var s) && s.IsLeaf).Select(id => chart.ById[id].Path).ToList();
        return leaves.Count == 0 ? "no state" : string.Join(", ", leaves);
    }

    private sealed record Prepared(int Depth, IReadOnlySet<string> Stereotype, IReadOnlyList<StateRead> Reads);
}
