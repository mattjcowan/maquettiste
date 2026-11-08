using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>A moment of a verb at which trigger points run (trigger-points-design.md decision 18, rows 8 to 12).</summary>
internal enum TriggerMoment
{
    /// <summary>Row 8: before actions, which fill the proposed values.</summary>
    Before,

    /// <summary>Row 9: checks, whose errors refuse and whose warnings are listed.</summary>
    Check,

    /// <summary>Row 10: a replace, in place of the verb's built-in effect.</summary>
    Replace,

    /// <summary>Row 11: with-the-change actions.</summary>
    WithChange,

    /// <summary>Row 12: after-commit actions, once the step's whole change is done.</summary>
    AfterCommit,
}

/// <summary>Which places a <see cref="TriggerQuery"/> leaves open, so the points narrowed by them are listed as if they matched.</summary>
[Flags]
internal enum TriggerOpenPlaces
{
    /// <summary>Every place is as the query gives it: what a run asks.</summary>
    None = 0,

    /// <summary>A lifecycle whose states the query doesn't give matches every <c>states</c> narrowing, instead of reading its initial state.</summary>
    States = 1,

    /// <summary>With no entity, a point narrowed by <c>entities</c> is listed, instead of running only when it covers every subject.</summary>
    Entity = 2,

    /// <summary>
    /// With no transition, a point narrowed by <c>targets</c> or <c>transitions</c>, or reading the payload, is listed, and so is a replace,
    /// since the verb may come from a move or from a verb step.
    /// </summary>
    Move = 4,
}

/// <summary>
/// Where and how a verb happens, for <see cref="TriggerSequences.For"/> (trigger-points-design.md decisions 10 to 13). Its init members let
/// later parts add more without changing a call.
/// </summary>
internal sealed record TriggerQuery
{
    /// <summary>The verb's key; <see langword="null"/> for a move with no verb, which runs no trigger points (decision 4).</summary>
    public string? Verb { get; init; }

    /// <summary>
    /// The process whose move, signature or task does the verb, or that runs the step: a lifecycle runs its points first, and alone for move,
    /// signature and task verbs; an orchestration holds none; <see langword="null"/> outside a process (the per-type runner).
    /// </summary>
    public Process? Process { get; init; }

    /// <summary>
    /// The object's entity, which decides the other lifecycles that run their points for object, link and own verbs and what <c>entities</c>
    /// matches; <see langword="null"/> when the run has no object, and the lifecycles that govern every subject of <see cref="Process"/> run
    /// theirs (decision 24).
    /// </summary>
    public Entity? Entity { get; init; }

    /// <summary>
    /// The object's states, by lifecycle id, as the step finds them before the verb: for a move, its source states; for <c>copy</c> and
    /// <c>revise</c>, the source object's states (decision 13). A lifecycle missing here reads as its initial state, and every lifecycle does
    /// for <c>create</c>.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> States { get; init; } = FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>
    /// The transition of <see cref="Process"/> whose move does the verb (an event's or an automatic move's), or, for <c>sign</c>, the transition
    /// whose gate is signed; <see langword="null"/> for a verb step, a task's end or the per-type runner. With it, no replace runs, since a
    /// move's effect is its move (decision 16).
    /// </summary>
    public string? Transition { get; init; }

    /// <summary>
    /// The states the move enters, default states included, as the run works them out; <see langword="null"/> to work them out from
    /// <see cref="Transition"/>'s targets and their default states.
    /// </summary>
    public IReadOnlyList<string>? Entered { get; init; }

    /// <summary>
    /// The attributes the step itself reads or changes, the actor's own change (decision 10): never what before actions add. <see langword="null"/>
    /// or empty when the step doesn't say, and a point narrowed by <c>attributes</c> then runs only if it is a check.
    /// </summary>
    public IReadOnlyList<string>? Attributes { get; init; }

    /// <summary>
    /// The relation end of the subject when the verb acts on a link through it; <see langword="null"/> when it acts on the object, or for
    /// <c>connect</c> and <c>disconnect</c> when the step doesn't say, and a point narrowed by <c>ends</c> then runs only if it is a check.
    /// </summary>
    public string? End { get; init; }

    /// <summary>The places left open; <see cref="TriggerOpenPlaces.None"/> in a run.</summary>
    public TriggerOpenPlaces Open { get; init; }
}

/// <summary>One trigger point that runs at one moment of a verb.</summary>
/// <param name="Point">The trigger point.</param>
/// <param name="Lifecycle">The lifecycle whose trigger points list it; <see langword="null"/> for a named trigger point (decision 33).</param>
/// <param name="Verb">The verb it runs on, by key.</param>
/// <param name="Moment">When it runs.</param>
/// <param name="Acting">Whether its lifecycle is the acting one, whose move, signature or task does the verb; never for a named trigger point.</param>
internal sealed record TriggerRun(ProcessTriggerPoint Point, Process? Lifecycle, string Verb, TriggerMoment Moment, bool Acting)
{
    /// <summary>
    /// The named trigger point it is, whose body <see cref="Point"/> is (<see cref="NamedTriggerPoints.Body"/>); <see langword="null"/> for a
    /// lifecycle's trigger point.
    /// </summary>
    public TriggerPoint? Named { get; init; }

    /// <summary>
    /// Why a check runs although the step doesn't say which attributes or link it speaks of (decision 10: checks fail closed), for the trace's
    /// note; <see langword="null"/> when its places matched.
    /// </summary>
    public string? Assumed { get; init; }
}

/// <summary>
/// One verb's trigger points at one place (trigger-points-design.md decisions 11, 18 and 19), by moment. Within a moment the acting lifecycle
/// comes first, then the others by name, each in its list's order.
/// </summary>
internal sealed record TriggerVerbRun
{
    /// <summary>The verb's key; empty for a move with no verb.</summary>
    public required string Verb { get; init; }

    /// <summary>Row 8: the before actions.</summary>
    public IReadOnlyList<TriggerRun> Before { get; init; } = [];

    /// <summary>Row 9: the checks.</summary>
    public IReadOnlyList<TriggerRun> Checks { get; init; } = [];

    /// <summary>Row 10: the replace that stands in for the built-in effect; <see langword="null"/> when the built-in effect runs.</summary>
    public TriggerRun? Replace { get; init; }

    /// <summary>Row 11: the with-the-change actions.</summary>
    public IReadOnlyList<TriggerRun> WithChange { get; init; } = [];

    /// <summary>Row 12: the after-commit actions.</summary>
    public IReadOnlyList<TriggerRun> AfterCommit { get; init; } = [];

    /// <summary>The lifecycles whose points were asked, in run order: the acting one, then the others by name then id.</summary>
    public IReadOnlyList<Process> Lifecycles { get; init; } = [];

    /// <summary>The named trigger points asked, in run order (decision 34), whatever their places; each runs before every lifecycle's points.</summary>
    public IReadOnlyList<TriggerPoint> Named { get; init; } = [];

    /// <summary>What it assumed or left out, for the trace's notes, in the order found.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>Whether no trigger point runs.</summary>
    public bool IsEmpty => Before.Count == 0 && Checks.Count == 0 && Replace is null && WithChange.Count == 0 && AfterCommit.Count == 0;

    /// <summary>Every point that runs, in run order: before actions, checks, the replace, with the change, after commit.</summary>
    public IEnumerable<TriggerRun> InOrder => Before.Concat(Checks).Concat(Replace is null ? [] : [Replace]).Concat(WithChange).Concat(AfterCommit);

    /// <summary>Nothing runs.</summary>
    /// <param name="verb">The verb's key, or <see langword="null"/>.</param>
    /// <returns>An empty run.</returns>
    public static TriggerVerbRun None(string? verb) => new() { Verb = verb ?? "" };
}

/// <summary>
/// One candidate of a move with a gate (trigger-points-design.md decisions 18 and 19): <c>sign</c>'s points run on every signature, and on
/// the signature that completes the gate the move's verb runs too, as two verbs in one candidate.
/// </summary>
/// <param name="Sign"><c>sign</c>'s points, which run around the count.</param>
/// <param name="Move">The move's points, only on the completing signature; <see langword="null"/> otherwise.</param>
internal sealed record TriggerCandidate(TriggerVerbRun Sign, TriggerVerbRun? Move)
{
    /// <summary>
    /// Rows 8 and 9 of the candidate, in run order, on one scratch copy: <c>sign</c>'s before actions and checks first, then the move's, so
    /// the move's checks see what <c>sign</c> prepared.
    /// </summary>
    public IEnumerable<TriggerRun> Selection =>
        Sign.Before.Concat(Sign.Checks).Concat(Move?.Before ?? []).Concat(Move?.Checks ?? []);
}

/// <summary>One lifecycle's points on one verb, by moment, in list order, whatever their places (trigger-points-design.md section 7).</summary>
/// <param name="Verb">The verb's key.</param>
/// <param name="Before">The before actions.</param>
/// <param name="Checks">The checks.</param>
/// <param name="Replace">The replaces, on a verb that allows one (decision 17); at most one is valid (MQ8104).</param>
/// <param name="WithChange">The with-the-change actions.</param>
/// <param name="AfterCommit">The after-commit actions.</param>
internal sealed record TriggerVerbPoints(string Verb, IReadOnlyList<ProcessTriggerPoint> Before, IReadOnlyList<ProcessTriggerPoint> Checks,
    IReadOnlyList<ProcessTriggerPoint> Replace, IReadOnlyList<ProcessTriggerPoint> WithChange, IReadOnlyList<ProcessTriggerPoint> AfterCommit)
{
    /// <summary>Whether the lifecycle has no point on the verb.</summary>
    public bool IsEmpty => Before.Count == 0 && Checks.Count == 0 && Replace.Count == 0 && WithChange.Count == 0 && AfterCommit.Count == 0;
}

/// <summary>What a <c>get_trigger_sequence</c> call narrows the answer to; each member absent leaves that place open.</summary>
public sealed record TriggerSequenceFilter
{
    /// <summary>A state id of the lifecycle the object is in (for a move, its source).</summary>
    public string? State { get; init; }

    /// <summary>An entity id the lifecycle governs: only the points that run for it.</summary>
    public string? Entity { get; init; }

    /// <summary>The attribute ids the step reads or changes, for <c>read</c> and <c>modify</c>; empty when the request gives none.</summary>
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>A relation end id of the subject, for a verb on a link.</summary>
    public string? End { get; init; }

    /// <summary>A transition id of the lifecycle, for a move verb or <c>sign</c>.</summary>
    public string? Transition { get; init; }
}

/// <summary>What runs, in order, when a verb happens in a lifecycle (trigger-points-design.md decision 36): the answer of <c>get_trigger_sequence</c>.</summary>
/// <param name="Lifecycle">The id of the lifecycle asked about, the acting one.</param>
/// <param name="Verb">The verb, by key.</param>
/// <param name="Steps">The steps in run order. A built-in step has no trigger point; a trigger point's step follows the built-in step of its
/// moment, when that applies.</param>
public sealed record TriggerSequence(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Lifecycle, string Verb, IReadOnlyList<TriggerSequenceStep> Steps);

/// <summary>
/// One step of the fixed order (trigger-points-design.md decision 18): a built-in step that applies to the verb, or a trigger point that can
/// run at that moment, with its lifecycle and places. Every member but <see cref="Order"/> and <see cref="Step"/> is left out when it doesn't
/// apply.
/// </summary>
public sealed record TriggerSequenceStep
{
    /// <summary>The step's row in the fixed order, 1 to 12.</summary>
    public required int Order { get; init; }

    /// <summary>
    /// <c>frozen</c>, <c>closed-lists</c>, <c>denies</c>, <c>defaults</c>, <c>no-move</c>, <c>guards</c> and <c>signatures</c> (rows 1 to 7),
    /// <c>before</c>, <c>checks</c>, <c>effect</c>, <c>with-change</c> and <c>after-commit</c>.
    /// </summary>
    public required string Step { get; init; }

    /// <summary>The trigger point's id; absent for a built-in step.</summary>
    public string? TriggerPoint { get; init; }

    /// <summary>The id of the lifecycle whose trigger points list it.</summary>
    public string? Lifecycle { get; init; }

    /// <summary>The trigger point's type.</summary>
    public TriggerPointType? Type { get; init; }

    /// <summary>An action's moment.</summary>
    public TriggerTiming? Timing { get; init; }

    /// <summary>A check's severity, <c>error</c> when its file gives none.</summary>
    public TriggerSeverity? Severity { get; init; }

    /// <summary>The ids of the states it is narrowed to.</summary>
    public IReadOnlyList<string>? States { get; init; }

    /// <summary>The ids of the states a move must enter.</summary>
    public IReadOnlyList<string>? Targets { get; init; }

    /// <summary>The ids of the transitions it is narrowed to.</summary>
    public IReadOnlyList<string>? Transitions { get; init; }

    /// <summary>The ids of the entities it is narrowed to.</summary>
    public IReadOnlyList<string>? Entities { get; init; }

    /// <summary>The ids of the attributes it is narrowed to.</summary>
    public IReadOnlyList<string>? Attributes { get; init; }

    /// <summary>The ids of the relation ends it is narrowed to.</summary>
    public IReadOnlyList<string>? Ends { get; init; }

    /// <summary><see langword="true"/> when it runs only when its <c>when</c> holds.</summary>
    public bool? When { get; init; }

    /// <summary><see langword="true"/> when the team's code reaches outside systems.</summary>
    public bool? Outside { get; init; }

    /// <summary>A named trigger point's subjects in file order (decision 33): an entity's id or a stereotype's id each; absent otherwise.</summary>
    public IReadOnlyList<TriggerPointSubjectRecord>? Subjects { get; init; }

    /// <summary>The ids of the entities a named trigger point skips; absent when none.</summary>
    public IReadOnlyList<string>? ExceptEntities { get; init; }

    /// <summary><see langword="true"/> when no bulk load may ever skip it (decision 20).</summary>
    public bool? NoBypass { get; init; }
}

/// <summary>
/// The engine's one evaluator of trigger points (trigger-points-design.md section 8, "One evaluator"; decisions 4, 10 to 13, 16 to 19 and
/// 36), for one snapshot (<see cref="Of"/>): the interpreter, <c>get_trigger_sequence</c>, <c>process.trigger_verbs</c> and the packs all ask
/// it, so they never disagree. For a verb, the acting lifecycle, the object's entity and states and the place, it says which points run at
/// each moment and in what order. It reads the lifecycles and the entities they govern; it never resolves the model, never judges a
/// <c>when</c>, a condition or a set (the caller does), and is immutable once built, so it is safe to share across threads. A project without
/// trigger points costs one pass over its processes.
/// </summary>
internal sealed class TriggerSequences
{
    private static readonly ConditionalWeakTable<ModelSnapshot, TriggerSequences> ByModel = new();

    /// <summary>The verbs that allow a replace (decision 17). Adding one later breaks no file; taking one away would.</summary>
    public static readonly FrozenSet<string> ReplaceVerbs = FrozenSet.Create(StringComparer.Ordinal, "delete", "disconnect", "copy", "revise");

    // A built-in verb's place in the table, which orders a lifecycle's verbs.
    private static readonly FrozenDictionary<string, int> BuiltInRank =
        BuiltInVerbs.All.Select((v, i) => (v.Key, i)).ToFrozenDictionary(p => p.Key, p => p.i, StringComparer.Ordinal);

    private static readonly RightsCaller Nobody = new() { Accepts = static _ => false, Satisfies = static _ => false };

    private readonly ModelSnapshot _model;
    // The lifecycles with trigger points, by name then id.
    private readonly IReadOnlyList<Process> _lifecycles;
    private readonly FrozenDictionary<string, Process> _byId;
    // Per lifecycle id and verb key: its points on the verb, by moment, worked out on first use.
    private readonly ConcurrentDictionary<(string Lifecycle, string Verb), Points> _points = new();
    // Per entity id: the lifecycles of _lifecycles that govern it.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Process>> _governing = new(StringComparer.Ordinal);
    // Per lifecycle id: the entities it governs, by name then id.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Entity>> _governed = new(StringComparer.Ordinal);
    // The named trigger points (decision 33).
    private readonly NamedTriggerPoints _named;

    private TriggerSequences(ModelSnapshot model)
    {
        _model = model;
        _named = NamedTriggerPoints.Of(model);
        var lifecycles = new List<Process>();
        foreach (var process in model.All<Process>())
        {
            // Only lifecycles hold trigger points (decision 2): an orchestration or a library that still writes them is MQ8101 and runs none.
            if (process.Use == ProcessUse.Lifecycle && process.TriggerPoints.Count > 0)
                lifecycles.Add(process);
        }

        lifecycles.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        _lifecycles = lifecycles;
        _byId = lifecycles.ToFrozenDictionary(p => p.Id, StringComparer.Ordinal);
    }

    /// <summary>Whether the project uses trigger points (decision 29): a lifecycle has <c>triggerPoints</c>, or a named trigger point exists.</summary>
    public bool Uses => _lifecycles.Count > 0 || _named.Any;

    /// <summary>The named trigger points, by name then id (<c>model.trigger_points</c>, decision 33).</summary>
    public IReadOnlyList<TriggerPoint> Named => _named.All;

    /// <summary>The named trigger points that cover an entity, in run order (decision 34, <c>entity.trigger_points</c>), whatever their verbs.</summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The points.</returns>
    public IReadOnlyList<TriggerPoint> NamedFor(Entity entity) => _named.For(entity);

    /// <summary>The entities a named trigger point runs for, by qualified name then id (<c>trigger_point.covers</c>).</summary>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The entities.</returns>
    public IReadOnlyList<Entity> Covers(TriggerPoint point) => _named.Covers(point);

    /// <summary>The lifecycles with trigger points, by name then id (<c>model.trigger_lifecycles</c>).</summary>
    public IReadOnlyList<Process> Lifecycles => _lifecycles;

    /// <summary>The evaluator of a snapshot, built on first use and kept as long as the snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The evaluator.</returns>
    public static TriggerSequences Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m => new TriggerSequences(m));
    }

    /// <summary>Whether a verb is a move verb: a built-in verb of group <c>move</c> (decision 4).</summary>
    /// <param name="verb">The verb's key.</param>
    /// <returns>Whether it is.</returns>
    public static bool IsMoveVerb(string? verb) => BuiltInVerbs.Get(verb)?.Group == "move";

    /// <summary>
    /// Whether every lifecycle that governs the object runs its points on a verb, not only the acting one (decision 11): an object, link or
    /// own verb. Move, signature and task verbs run only the acting lifecycle's.
    /// </summary>
    /// <param name="verb">The verb's key.</param>
    /// <returns>Whether it does.</returns>
    public static bool SpeaksOfObject(string? verb) => RightsEvaluator.SpeaksOfObject(verb);

    /// <summary>The moment of a trigger point on a verb; <see langword="null"/> for an action with no timing or a replace on a verb that allows none.</summary>
    /// <param name="point">The trigger point.</param>
    /// <param name="verb">The verb's key.</param>
    /// <returns>The moment.</returns>
    public static TriggerMoment? MomentOf(ProcessTriggerPoint point, string verb)
    {
        ArgumentNullException.ThrowIfNull(point);
        return point.Type switch
        {
            TriggerPointType.Check => TriggerMoment.Check,
            TriggerPointType.Replace => ReplaceVerbs.Contains(verb) ? TriggerMoment.Replace : null,
            _ => point.Timing switch
            {
                TriggerTiming.Before => TriggerMoment.Before,
                TriggerTiming.WithChange => TriggerMoment.WithChange,
                TriggerTiming.AfterCommit => TriggerMoment.AfterCommit,
                _ => null,
            },
        };
    }

    /// <summary>
    /// A lifecycle's points on a verb, by moment, in list order, whatever their places (<c>process.trigger_verbs</c>, the step methods). Worked
    /// out once per lifecycle and verb.
    /// </summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="verb">The verb's key.</param>
    /// <returns>The points; empty for a lifecycle with none on the verb.</returns>
    public TriggerVerbPoints PointsOf(Process lifecycle, string verb)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(verb);
        return Lookup(lifecycle, verb).ByMoment;
    }

    /// <summary>
    /// A lifecycle's points per verb (<c>process.trigger_verbs</c>): one entry for each verb its points name, built-in verbs in their table's
    /// order, then the project's own verbs in the vocabulary's order, then any other key ordinally.
    /// </summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The entries; empty for a lifecycle without trigger points.</returns>
    public IReadOnlyList<TriggerVerbPoints> VerbsOf(Process lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (!_byId.ContainsKey(lifecycle.Id))
            return [];
        var named = lifecycle.TriggerPoints.SelectMany(p => p.Verbs).Distinct(StringComparer.Ordinal).ToList();
        var own = _model.Verbs?.Verbs.Select(v => v.Key).ToList() ?? [];
        int Rank(string key) => BuiltInRank.TryGetValue(key, out var builtIn)
            ? builtIn
            : own.IndexOf(key) is >= 0 and var at ? BuiltInRank.Count + at : int.MaxValue;
        return [.. named.OrderBy(Rank).ThenBy(k => k, StringComparer.Ordinal).Select(k => PointsOf(lifecycle, k)).Where(p => !p.IsEmpty)];
    }

    /// <summary>
    /// The lifecycles with points on object, link or own verbs that govern an entity, by name then id (<c>entity.trigger_lifecycles</c>, the
    /// per-type runner), whatever those points' <c>entities</c>: the runner tests them at run time.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The lifecycles.</returns>
    public IReadOnlyList<Process> LifecyclesFor(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return [.. Governing(entity).Where(l => l.TriggerPoints.Any(p => p.Verbs.Any(SpeaksOfObject)))];
    }

    /// <summary>
    /// The entities a lifecycle's trigger point runs for (decision 12, <c>trigger_point.covers</c>), by name then id: every entity the lifecycle
    /// governs (its subjects, their derived entities and a stereotype's carriers), narrowed by <c>entities</c> to those derived from one.
    /// </summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="point">One of its trigger points.</param>
    /// <returns>The entities.</returns>
    public IReadOnlyList<Entity> Covers(Process lifecycle, ProcessTriggerPoint point)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(point);
        var governed = _governed.GetOrAdd(lifecycle.Id, _ =>
        {
            var names = _model.Names("entity");
            return [.. _model.All<Entity>().Where(e => SubjectCoverage.Governs(_model, lifecycle, e))
                .OrderBy(e => names.Get(e.Id)?.Longest ?? e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];
        });
        return point.Entities.Count == 0 ? governed : [.. governed.Where(e => Narrowed(point, e))];
    }

    /// <summary>
    /// Which trigger points run, at each moment and in what order, when a verb happens at a place (decisions 10, 11, 18 and 19). The acting
    /// lifecycle comes first, then for object, link and own verbs every other lifecycle that governs the object, by name then id, each reading
    /// its own states; within a moment, each lifecycle's points in its list's order. It never judges a <c>when</c>, which the caller does.
    /// </summary>
    /// <param name="query">The verb and its place.</param>
    /// <returns>The points by moment, with the notes of what was assumed.</returns>
    public TriggerVerbRun For(TriggerQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Verb is not { Length: > 0 } verb || !Uses)
            return TriggerVerbRun.None(query.Verb);
        var notes = new List<string>();
        var named = NamedAsked(query, verb, notes);
        var asked = Asked(query, verb, notes);
        if (asked.Count == 0 && named.Count == 0)
            return new TriggerVerbRun { Verb = verb, Notes = notes };
        var before = new List<TriggerRun>();
        var checks = new List<TriggerRun>();
        var replaces = new List<TriggerRun>();
        var withChange = new List<TriggerRun>();
        var afterCommit = new List<TriggerRun>();
        var acting = query.Process is { Use: ProcessUse.Lifecycle } p ? p : null;
        List<Entity>? subjects = null;
        // Named trigger points first, at each moment (decision 34). They have no states, targets or transitions, and read no payload.
        foreach (var namedPoint in named)
        {
            var point = _named.Body(namedPoint);
            if (MomentOf(point, verb) is not { } moment)
                continue;
            if (moment == TriggerMoment.Replace && query.Transition is not null)
            {
                notes.Add($"An event that names '{verb}' still does its move, so replace '{point.Name}' does not run.");
                continue;
            }

            Place(point, moment, null, namedPoint, false);
        }

        foreach (var lifecycle in asked)
        {
            var isActing = acting is not null && string.Equals(acting.Id, lifecycle.Id, StringComparison.Ordinal);
            var points = Lookup(lifecycle, verb);
            IReadOnlySet<string>? configuration = null;
            var configured = false;
            IReadOnlyList<string>? entered = null;
            foreach (var info in points.All)
            {
                var point = info.Point;
                var moment = info.Moment;
                if (moment == TriggerMoment.Replace && query.Transition is not null)
                {
                    notes.Add($"An event that names '{verb}' still does its move, so replace '{point.Name}' does not run.");
                    continue;
                }

                // Points narrowed to a move run only when a move of their own lifecycle does the verb (decision 4).
                if (info.MoveOnly && !(isActing && query.Transition is not null) && !OpenMove(query))
                    continue;
                if (point.Transitions.Count > 0 && query.Transition is { } transition && !point.Transitions.Contains(transition, StringComparer.Ordinal))
                    continue;
                if (point.Targets.Count > 0 && query.Transition is not null)
                {
                    entered ??= query.Entered ?? Entered(lifecycle, query.Transition);
                    if (!Enters(lifecycle, entered, point.Targets))
                        continue;
                }

                if (point.States.Count > 0)
                {
                    if (!configured)
                    {
                        configuration = Configuration(query, lifecycle, verb);
                        configured = true;
                    }

                    if (configuration is not null && !point.States.Any(configuration.Contains))
                        continue;
                }

                Place(point, moment, lifecycle, null, isActing);
            }
        }

        // At most one replace takes the place of the effect (MQ8104 refuses a second); the first in run order does.
        foreach (var extra in replaces.Skip(1))
            notes.Add($"Replace '{replaces[0].Point.Name}' already takes the place of '{verb}', so replace '{extra.Point.Name}' does not run.");
        var replace = replaces.Count > 0 ? replaces[0] : null;
        // After a verb step's built-in delete there is no object to set (decision 16). An event that names delete still does its move and the
        // object stays, so its with-change sets run.
        if (replace is null && query.Transition is null && string.Equals(verb, "delete", StringComparison.Ordinal))
        {
            foreach (var run in withChange.Where(r => r.Point.Sets.Count > 0))
                notes.Add($"No replace on 'delete' runs, so the sets of '{run.Point.Name}' are skipped: after a built-in delete there is no object to set.");
        }

        return new TriggerVerbRun
        {
            Verb = verb,
            Before = before,
            Checks = checks,
            Replace = replace,
            WithChange = withChange,
            AfterCommit = afterCommit,
            Lifecycles = asked,
            Named = named,
            Notes = notes,
        };

        // A point's entities, attributes and ends (decisions 10 and 12), alike for both kinds; then its place at its moment.
        void Place(ProcessTriggerPoint point, TriggerMoment moment, Process? lifecycle, TriggerPoint? namedPoint, bool isActing)
        {
            if (point.Entities.Count > 0)
            {
                if (query.Entity is { } entity)
                {
                    if (!Narrowed(point, entity))
                        return;
                }
                else if ((query.Open & TriggerOpenPlaces.Entity) == 0)
                {
                    subjects ??= query.Process is { } process ? SubjectsOf(process) : [];
                    if (subjects.Count == 0 || !subjects.All(s => Narrowed(point, s)))
                    {
                        notes.Add($"The run has no object, and trigger point '{point.Name}' runs only for some of the subjects' entities, so it does not run.");
                        return;
                    }
                }
            }

            var assumed = new List<string>(2);
            if (point.Attributes.Count > 0)
            {
                if (query.Attributes is { Count: > 0 } given)
                {
                    if (!point.Attributes.Any(a => given.Contains(a, StringComparer.Ordinal)))
                        return;
                }
                else
                {
                    assumed.Add("which attributes it reads or changes");
                }
            }

            switch (Speaks(point, verb, query.End))
            {
                case false:
                    return;
                case null:
                    assumed.Add("which link it speaks of");
                    break;
            }

            string? why = null;
            if (assumed.Count > 0)
            {
                var unknown = string.Join(" or ", assumed);
                if (point.Type != TriggerPointType.Check)
                {
                    notes.Add($"The step does not say {unknown}, so {Noun(point)} '{point.Name}', which is narrowed to some of them, does not run.");
                    return;
                }

                why = $"The step does not say {unknown}, so check '{point.Name}', which is narrowed to some of them, runs: a check fails closed.";
                notes.Add(why);
            }

            var run = new TriggerRun(point, lifecycle, verb, moment, isActing) { Assumed = why, Named = namedPoint };
            (moment switch
            {
                TriggerMoment.Before => before,
                TriggerMoment.Check => checks,
                TriggerMoment.Replace => replaces,
                TriggerMoment.WithChange => withChange,
                _ => afterCommit,
            }).Add(run);
        }
    }

    /// <summary>
    /// The named trigger points asked for a verb (decisions 33 and 34), in run order, whatever their places: those that cover the object's
    /// entity; for an object, link or own verb whichever process does it, or none; for a move, signature or task verb only in the lifecycle
    /// whose move, signature or task does it, never in an orchestration. With no object, those that cover every subject
    /// of the process, in the run order of its first subject; with the entity left open, those that cover any subject. Only points on the verb.
    /// </summary>
    private List<TriggerPoint> NamedAsked(TriggerQuery query, string verb, List<string> notes)
    {
        if (!_named.Any)
            return [];
        // A move, signature or task verb happens in one lifecycle: its named points run only there, never in an orchestration (A5).
        if (!SpeaksOfObject(verb) && query.Process is not { Use: ProcessUse.Lifecycle })
            return [];
        bool OnVerb(TriggerPoint p) => p.Verbs.Contains(verb, StringComparer.Ordinal);
        if (query.Entity is { } entity)
            return [.. _named.For(entity).Where(OnVerb)];
        var subjects = query.Process is { } process ? SubjectsOf(process) : [];
        var found = new List<TriggerPoint>();
        foreach (var subject in subjects)
        {
            foreach (var point in _named.For(subject))
            {
                if (OnVerb(point) && !found.Contains(point))
                    found.Add(point);
            }
        }

        if ((query.Open & TriggerOpenPlaces.Entity) != 0)
            return found;
        var all = new List<TriggerPoint>(found.Count);
        foreach (var point in found)
        {
            if (subjects.All(s => _named.Covered(point, s.Id)))
                all.Add(point);
            else
                notes.Add($"Named trigger point '{point.Name}' covers only some subjects of '{query.Process!.Name}', so it does not run.");
        }

        return all;
    }

    /// <summary>
    /// A signature on a gated move (decisions 18 and 19): <c>sign</c>'s points, narrowed by the gated transition; and on the signature that
    /// completes the gate, the move's own verb's points too, which run after <c>sign</c>'s at rows 8 and 9.
    /// </summary>
    /// <param name="move">The move: its verb (which may be none), its lifecycle, its transition and the place.</param>
    /// <param name="completes">Whether this signature completes the gate.</param>
    /// <returns>The candidate.</returns>
    public TriggerCandidate Signature(TriggerQuery move, bool completes)
    {
        ArgumentNullException.ThrowIfNull(move);
        var sign = For(move with { Verb = "sign" });
        return new TriggerCandidate(sign, completes ? For(move) : null);
    }

    /// <summary>
    /// What runs, in order, when a verb happens in a lifecycle (decision 36, <c>get_trigger_sequence</c>): the built-in steps of decision 18
    /// that apply to the verb, and each trigger point that can run, as <see cref="For"/> picks them. A place the filter doesn't give is left
    /// open, but for attributes and ends, where a run fails closed: only checks narrowed by them are listed. <c>reject</c>, <c>ignore</c> and
    /// <c>withdraw</c> at a transition whose gate has stages, or with no transition in a lifecycle that has such a gate, list the signatures row
    /// and the response's effect, as <c>sign</c> does (signoff-design.md section 9).
    /// </summary>
    /// <param name="lifecycle">The acting lifecycle.</param>
    /// <param name="verb">The verb's key.</param>
    /// <param name="filter">The place.</param>
    /// <returns>The sequence.</returns>
    public TriggerSequence Describe(Process lifecycle, string verb, TriggerSequenceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(filter);
        var entity = filter.Entity is { } entityId ? _model.Get<Entity>(entityId) : null;
        var query = new TriggerQuery
        {
            Verb = verb,
            Process = lifecycle,
            Entity = entity,
            States = filter.State is { } state
                ? new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [lifecycle.Id] = [state] }
                : FrozenDictionary<string, IReadOnlyList<string>>.Empty,
            Transition = filter.Transition,
            Attributes = filter.Attributes,
            End = filter.End,
            Open = TriggerOpenPlaces.States | (entity is null ? TriggerOpenPlaces.Entity : TriggerOpenPlaces.None)
                | (filter.Transition is null ? TriggerOpenPlaces.Move : TriggerOpenPlaces.None),
        };
        var run = For(query);
        var group = RightsEvaluator.GroupOf(verb);
        var byEvent = group is "move" or "signature" || filter.Transition is not null;
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        var steps = new List<TriggerSequenceStep>();
        void Builtin(int order, string step) => steps.Add(new TriggerSequenceStep { Order = order, Step = step });

        if (verb is "modify" or "delete" or "connect" or "disconnect")
            Builtin(1, "frozen");
        if (byEvent || group == "task")
            Builtin(2, "closed-lists");
        var rights = RightsEvaluator.Of(_model);
        if (rights.Uses)
        {
            var (answering, _) = rights.Answering(new RightsQuery { Verb = verb, Process = lifecycle, Entity = entity, Caller = Nobody });
            if (answering.Any(l => rights.RulesOf(l).Any(r => r.Access == RightAccess.Deny && r.Verbs.Contains(verb, StringComparer.Ordinal))))
                Builtin(3, "denies");
            if (answering.Any(l => RightsEvaluator.DefaultOf(l) == RightAccess.Deny))
                Builtin(4, "defaults");
        }

        if (byEvent)
        {
            Builtin(5, "no-move");
            Builtin(6, "guards");
        }

        // A response at an approval (signoff-design.md sections 9 and 17): reject, ignore and withdraw answer a transition whose gate has stages,
        // inline or through a gate policy, so the signatures row (7a to 7f) applies there, and the effect is the response's. A transition given
        // names its gate below; with none, any approval of the lifecycle may take the response. A plain gate answers as before.
        var responds = filter.Transition is null && verb is "reject" or "ignore" or "withdraw"
            && chart.Transitions.Any(t => Validation.ApprovalRules.GateOf(_model, t.Transition) is { UsesStages: true });
        var gated = verb == "sign" || responds || chart.Transitions.Any(t =>
            (filter.Transition is { } id ? string.Equals(t.Transition.Id, id, StringComparison.Ordinal)
                : string.Equals(t.Transition.Verb ?? t.Event?.Verb, verb, StringComparison.Ordinal))
            && (t.Transition.Gate is not null || t.Transition.GatePolicy is not null));
        if (gated && byEvent)
            Builtin(7, "signatures");
        steps.AddRange(run.Before.Select(r => Point(8, "before", r)));
        steps.AddRange(run.Checks.Select(r => Point(9, "checks", r)));
        if (run.Replace is { } replace)
            steps.Add(Point(10, "effect", replace));
        else if (group != BuiltInVerbs.ProjectGroup || filter.Transition is not null)
            Builtin(10, "effect");
        steps.AddRange(run.WithChange.Select(r => Point(11, "with-change", r)));
        steps.AddRange(run.AfterCommit.Select(r => Point(12, "after-commit", r)));
        return new TriggerSequence(lifecycle.Id, verb, steps);

        TriggerSequenceStep Point(int order, string step, TriggerRun run)
        {
            var point = run.Point;
            static IReadOnlyList<string>? Listed(IReadOnlyList<string> ids) => ids.Count > 0 ? ids : null;
            return new TriggerSequenceStep
            {
                Order = order,
                Step = step,
                TriggerPoint = point.Id,
                Lifecycle = run.Lifecycle?.Id,
                Type = point.Type,
                Timing = point.Type == TriggerPointType.Action ? point.Timing : null,
                Severity = point.Type == TriggerPointType.Check ? point.Severity ?? TriggerSeverity.Error : null,
                States = Listed(point.States),
                Targets = Listed(point.Targets),
                Transitions = Listed(point.Transitions),
                Entities = Listed(point.Entities),
                Attributes = Listed(point.Attributes),
                Ends = Listed(point.Ends),
                When = point.When is not null ? true : null,
                Outside = point.Outside ? true : null,
                Subjects = run.Named is { } named ? SubjectRecords(_model, named) : null,
                ExceptEntities = run.Named is { ExceptEntities.Count: > 0 } except ? except.ExceptEntities : null,
                NoBypass = point.NoBypass ? true : null,
            };
        }
    }

    /// <summary>
    /// What runs, in order, when an object, link or own verb happens to an object of an entity with no lifecycle acting (decisions 33, 34 and
    /// 36, <c>getEntityTriggerSequence</c>): the built-in steps that apply, then at each moment the named trigger points that cover the entity,
    /// then every lifecycle that governs it, by name, each reading every state. A place the filter doesn't give is left open, but for
    /// attributes and ends, where a run fails closed. <see langword="null"/> for a move, signature or task verb, which happens in one lifecycle.
    /// </summary>
    /// <param name="entity">The object's entity.</param>
    /// <param name="verb">The verb's key.</param>
    /// <param name="filter">The place: its attributes and end; its state, entity and transition are not read.</param>
    /// <returns>The sequence, with no lifecycle; or <see langword="null"/>.</returns>
    public TriggerSequence? Describe(Entity entity, string verb, TriggerSequenceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(verb);
        ArgumentNullException.ThrowIfNull(filter);
        if (!SpeaksOfObject(verb))
            return null;
        var run = For(new TriggerQuery { Verb = verb, Entity = entity, Attributes = filter.Attributes, End = filter.End, Open = TriggerOpenPlaces.States });
        var steps = new List<TriggerSequenceStep>();
        if (verb is "modify" or "delete" or "connect" or "disconnect")
            steps.Add(new TriggerSequenceStep { Order = 1, Step = "frozen" });
        var rights = RightsEvaluator.Of(_model);
        if (rights.Uses)
        {
            var (answering, _) = rights.Answering(new RightsQuery { Verb = verb, Entity = entity, Caller = Nobody });
            if (answering.Any(l => rights.RulesOf(l).Any(r => r.Access == RightAccess.Deny && r.Verbs.Contains(verb, StringComparer.Ordinal))))
                steps.Add(new TriggerSequenceStep { Order = 3, Step = "denies" });
            if (answering.Any(l => RightsEvaluator.DefaultOf(l) == RightAccess.Deny))
                steps.Add(new TriggerSequenceStep { Order = 4, Step = "defaults" });
        }

        var model = _model;
        steps.AddRange(run.Before.Select(r => Step(8, "before", r)));
        steps.AddRange(run.Checks.Select(r => Step(9, "checks", r)));
        if (run.Replace is { } replace)
            steps.Add(Step(10, "effect", replace));
        else if (RightsEvaluator.GroupOf(verb) != BuiltInVerbs.ProjectGroup)
            steps.Add(new TriggerSequenceStep { Order = 10, Step = "effect" });
        steps.AddRange(run.WithChange.Select(r => Step(11, "with-change", r)));
        steps.AddRange(run.AfterCommit.Select(r => Step(12, "after-commit", r)));
        return new TriggerSequence(null, verb, steps);

        TriggerSequenceStep Step(int order, string step, TriggerRun r)
        {
            var point = r.Point;
            return new TriggerSequenceStep
            {
                Order = order,
                Step = step,
                TriggerPoint = point.Id,
                Lifecycle = r.Lifecycle?.Id,
                Type = point.Type,
                Timing = point.Type == TriggerPointType.Action ? point.Timing : null,
                Severity = point.Type == TriggerPointType.Check ? point.Severity ?? TriggerSeverity.Error : null,
                Entities = point.Entities.Count > 0 ? point.Entities : null,
                Attributes = point.Attributes.Count > 0 ? point.Attributes : null,
                Ends = point.Ends.Count > 0 ? point.Ends : null,
                When = point.When is not null ? true : null,
                Outside = point.Outside ? true : null,
                Subjects = r.Named is { } named ? SubjectRecords(model, named) : null,
                ExceptEntities = r.Named is { ExceptEntities.Count: > 0 } except ? except.ExceptEntities : null,
                NoBypass = point.NoBypass ? true : null,
            };
        }
    }

    /// <summary>A named trigger point's subjects as records, in file order: an entity's id, or a known stereotype's id; an unknown key is left out.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The records.</returns>
    internal static IReadOnlyList<TriggerPointSubjectRecord> SubjectRecords(ModelSnapshot model, TriggerPoint point) =>
        [.. point.Subjects.Select(s => s.Entity is { } id ? new TriggerPointSubjectRecord(id)
                : s.Stereotype is { } key && model.GetStereotype(key) is { } stereotype ? new TriggerPointSubjectRecord(Stereotype: stereotype.Id) : null)
            .OfType<TriggerPointSubjectRecord>()];

    /// <summary>
    /// The states a move enters (decision 10's <c>targets</c>): each target of the transition and its default states, down from it (a compound
    /// state's initial child, every region of a parallel state, a history state's default). Empty for a targetless transition or an unknown id.
    /// </summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="transition">The transition's id.</param>
    /// <returns>The state ids, in document order.</returns>
    public IReadOnlyList<string> Entered(Process lifecycle, string transition)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        ArgumentNullException.ThrowIfNull(transition);
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        var found = chart.Transitions.FirstOrDefault(t => string.Equals(t.Transition.Id, transition, StringComparison.Ordinal));
        if (found is null)
            return [];
        var entered = new HashSet<ChartState>(ReferenceEqualityComparer.Instance);
        foreach (var target in found.Targets)
            Enter(target, 0);
        return [.. entered.OrderBy(s => s.Order).Select(s => s.Id)];

        void Enter(ChartState state, int depth)
        {
            if (depth > 64 || !entered.Add(state))
                return;
            switch (state.Type)
            {
                case StateType.History:
                    if ((state.DefaultTarget ?? state.Parent?.InitialChild) is { } restored && !ReferenceEquals(restored, state))
                        Enter(restored, depth + 1);
                    return;
                case StateType.Parallel:
                    foreach (var region in state.Children.Where(r => r.Type is not (StateType.History or StateType.Choice)))
                        Enter(region, depth + 1);
                    return;
                default:
                    if (state.InitialChild is { } initial)
                        Enter(initial, depth + 1);
                    return;
            }
        }
    }

    /// <summary>A lifecycle's points on a verb, worked out once.</summary>
    private Points Lookup(Process lifecycle, string verb) =>
        _points.GetOrAdd((lifecycle.Id, verb), static (key, state) => state.Self.Build(state.Lifecycle, key.Verb), (Self: this, Lifecycle: lifecycle));

    private Points Build(Process lifecycle, string verb)
    {
        if (lifecycle.Use != ProcessUse.Lifecycle)
            return Points.Empty(verb);
        HashSet<string>? payload = null;
        var all = new List<PointInfo>();
        foreach (var point in lifecycle.TriggerPoints)
        {
            if (!point.Verbs.Contains(verb, StringComparer.Ordinal) || MomentOf(point, verb) is not { } moment)
                continue;
            payload ??= lifecycle.Events.SelectMany(e => e.Payload).Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            var moveOnly = point.Targets.Count > 0 || point.Transitions.Count > 0 || ReadsPayload(point, payload);
            all.Add(new PointInfo(point, moment, moveOnly));
        }

        List<ProcessTriggerPoint> Of(TriggerMoment m) => [.. all.Where(i => i.Moment == m).Select(i => i.Point)];
        return new Points(all, new TriggerVerbPoints(verb, Of(TriggerMoment.Before), Of(TriggerMoment.Check), Of(TriggerMoment.Replace),
            Of(TriggerMoment.WithChange), Of(TriggerMoment.AfterCommit)));
    }

    /// <summary>
    /// The lifecycles whose points are asked (decision 11), in run order: the acting lifecycle; and for object, link and own verbs every other
    /// lifecycle that governs the object, by name then id, or with no object those that govern every subject of the process (decision 24).
    /// Only lifecycles with points on the verb are listed.
    /// </summary>
    private List<Process> Asked(TriggerQuery query, string verb, List<string> notes)
    {
        var acting = query.Process is { Use: ProcessUse.Lifecycle } lifecycle ? lifecycle : null;
        var asked = new List<Process>();
        if (acting is not null && !Lookup(acting, verb).ByMoment.IsEmpty)
            asked.Add(acting);
        if (!SpeaksOfObject(verb))
            return asked;
        if (query.Entity is { } entity)
        {
            foreach (var other in Governing(entity))
            {
                if ((acting is null || !string.Equals(other.Id, acting.Id, StringComparison.Ordinal)) && !Lookup(other, verb).ByMoment.IsEmpty)
                    asked.Add(other);
            }

            return asked;
        }

        var subjects = query.Process is { } process ? SubjectsOf(process) : [];
        if (subjects.Count == 0)
            return asked;
        foreach (var other in _lifecycles)
        {
            if ((acting is not null && string.Equals(other.Id, acting.Id, StringComparison.Ordinal)) || Lookup(other, verb).ByMoment.IsEmpty)
                continue;
            var governed = subjects.Count(s => SubjectCoverage.Governs(_model, other, s));
            if (governed == subjects.Count)
            {
                asked.Add(other);
                if (!query.States.ContainsKey(other.Id) && (query.Open & TriggerOpenPlaces.States) == 0)
                    notes.Add($"The run has no object, so lifecycle '{other.Name}' runs its trigger points in its initial state.");
            }
            else if (governed > 0)
            {
                notes.Add($"Lifecycle '{other.Name}' governs only some subjects of '{query.Process!.Name}', so its trigger points do not run.");
            }
        }

        return asked;
    }

    /// <summary>
    /// The lifecycles with trigger points a step can reach, whatever its places (decisions 11 and 26): the running lifecycle; and when the
    /// step's verb is an object, link or own verb, every other lifecycle that governs the object, or with no object those that govern every
    /// subject of the process. A scenario step's <c>assume</c> words may name team-code checks of these only (MQ9305).
    /// </summary>
    /// <param name="process">The process the step runs in.</param>
    /// <param name="entity">The object's entity; <see langword="null"/> when the run has no object.</param>
    /// <param name="verb">The step's verb (a verb step's, or the verb its event names); <see langword="null"/> for none.</param>
    /// <returns>The lifecycles, the running one first, then the others by name then id.</returns>
    public IReadOnlyList<Process> Reaching(Process process, Entity? entity, string? verb)
    {
        ArgumentNullException.ThrowIfNull(process);
        var reached = new List<Process>();
        if (_byId.ContainsKey(process.Id))
            reached.Add(process);
        if (!SpeaksOfObject(verb))
            return reached;
        var subjects = entity is null ? SubjectsOf(process) : null;
        foreach (var other in entity is null ? _lifecycles : Governing(entity))
        {
            if (string.Equals(other.Id, process.Id, StringComparison.Ordinal))
                continue;
            if (entity is not null || (subjects!.Count > 0 && subjects.All(s => SubjectCoverage.Governs(_model, other, s))))
                reached.Add(other);
        }

        return reached;
    }

    /// <summary>
    /// The named trigger points a step can reach (decisions 26 and 33), whatever its verb and places: those that cover the object's entity, in
    /// run order; with no object, those that cover every subject of the process. A step's <c>assume</c> words may name their team-code checks.
    /// </summary>
    /// <param name="process">The process the step runs in.</param>
    /// <param name="entity">The object's entity; <see langword="null"/> when the run has no object.</param>
    /// <returns>The named trigger points; empty in a project without them.</returns>
    public IReadOnlyList<TriggerPoint> NamedReaching(Process process, Entity? entity)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!_named.Any)
            return [];
        if (entity is not null)
            return _named.For(entity);
        var subjects = SubjectsOf(process);
        return subjects.Count == 0 ? [] : [.. _named.For(subjects[0]).Where(p => subjects.All(s => _named.Covered(p, s.Id)))];
    }

    /// <summary>A named trigger point's body as a lifecycle's trigger point reads it (<see cref="NamedTriggerPoints.Body"/>).</summary>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The body.</returns>
    public ProcessTriggerPoint BodyOf(TriggerPoint point) => _named.Body(point);

    /// <summary>The lifecycles with trigger points that govern an entity, by name then id, worked out once per entity.</summary>
    private IReadOnlyList<Process> Governing(Entity entity) =>
        _governing.GetOrAdd(entity.Id, _ => [.. _lifecycles.Where(l => SubjectCoverage.Governs(_model, l, entity))]);

    /// <summary>
    /// The states a lifecycle's <c>states</c> place matches (decision 10): the states the query gives with the compound states that hold them,
    /// so a compound state counts when a state inside it is active, and nothing else. For a move the states given are its source and the active
    /// states inside it, so another region of a parallel state never counts and is never filled with its default state. The initial
    /// configuration only when the lifecycle's states aren't given, and always on <c>create</c> (decision 13). <see langword="null"/> when the
    /// query leaves the lifecycle's states open.
    /// </summary>
    private HashSet<string>? Configuration(TriggerQuery query, Process lifecycle, string verb)
    {
        var create = string.Equals(verb, "create", StringComparison.Ordinal);
        var given = query.States.TryGetValue(lifecycle.Id, out var states);
        if (!create && !given && (query.Open & TriggerOpenPlaces.States) != 0)
            return null;
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        if (!create && given)
        {
            var held = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in states!)
            {
                if (!chart.ById.TryGetValue(id, out var state) || state.Type is StateType.History or StateType.Choice)
                    continue;
                for (var s = state; s is { IsRoot: false }; s = s.Parent)
                    held.Add(s.Id);
            }

            if (held.Count > 0)
                return held;
        }

        return ObjectStates.Configuration(chart, []).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Whether a move enters one of some states: a compound state counts when the move enters it or any state inside it.</summary>
    private bool Enters(Process lifecycle, IReadOnlyList<string> entered, IReadOnlyList<string> targets)
    {
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        foreach (var id in entered)
        {
            if (targets.Contains(id, StringComparer.Ordinal))
                return true;
            if (chart.ById.TryGetValue(id, out var state) && state.Ancestors.Any(a => targets.Contains(a.Id, StringComparer.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>Whether an entity is one a point's <c>entities</c> names, or derived from one (decision 12).</summary>
    private bool Narrowed(ProcessTriggerPoint point, Entity entity) =>
        SubjectCoverage.Chain(_model, entity).Any(id => point.Entities.Contains(id, StringComparer.Ordinal));

    private static bool OpenMove(TriggerQuery query) => query.Transition is null && (query.Open & TriggerOpenPlaces.Move) != 0;

    /// <summary>
    /// Whether a point speaks of what the verb acts on (decision 10, as rights-design.md decision 20): with <c>ends</c>, only links through one
    /// of them; without, the object, and for <c>connect</c> and <c>disconnect</c> every link too. <see langword="null"/> when a link verb doesn't
    /// say which end and the point names ends: it then runs only if it is a check.
    /// </summary>
    private static bool? Speaks(ProcessTriggerPoint point, string verb, string? end)
    {
        var link = RightsEvaluator.GroupOf(verb) == "link";
        if (point.Ends.Count > 0)
            return end is null ? (link ? null : false) : point.Ends.Contains(end, StringComparer.Ordinal);
        return end is null || link;
    }

    /// <summary>Whether a point reads the event's payload in its <c>when</c>, its condition or its set values.</summary>
    private static bool ReadsPayload(ProcessTriggerPoint point, HashSet<string> payload)
    {
        if (payload.Count == 0)
            return false;
        return Reads(point.When) || Reads(point.Condition) || point.Sets.Any(s => Reads(s.Value));

        bool Reads(Expr? expr)
        {
            if (expr is null)
                return false;
            if (expr.Path is [var first, ..] && expr.Old != true && payload.Contains(first))
                return true;
            return (expr.Args?.Any(Reads) ?? false) || (expr.Case?.Any(c => Reads(c.When) || Reads(c.Then)) ?? false) || Reads(expr.Else)
                || (expr.With?.Any(w => Reads(w.Value)) ?? false);
        }
    }

    private static string Noun(ProcessTriggerPoint point) => point.Type == TriggerPointType.Replace ? "replace" : "action";

    /// <summary>The entities a process's subject entries govern: each entity entry's entity, and each stereotype entry's carriers.</summary>
    private List<Entity> SubjectsOf(Process process)
    {
        var subjects = new List<Entity>();
        foreach (var entry in process.Subjects)
        {
            if (entry.Entity is { } id)
            {
                if (_model.Get<Entity>(id) is { } entity && !subjects.Contains(entity))
                    subjects.Add(entity);
                continue;
            }

            foreach (var carrier in SubjectCoverage.Governed(_model, process, entry))
            {
                if (!subjects.Contains(carrier))
                    subjects.Add(carrier);
            }
        }

        return subjects;
    }

    /// <summary>One point on one verb: its moment, and whether it runs only when a move of its lifecycle does the verb.</summary>
    private sealed record PointInfo(ProcessTriggerPoint Point, TriggerMoment Moment, bool MoveOnly);

    /// <summary>A lifecycle's points on one verb: in list order with what matching needs, and by moment.</summary>
    private sealed record Points(IReadOnlyList<PointInfo> All, TriggerVerbPoints ByMoment)
    {
        public static Points Empty(string verb) => new([], new TriggerVerbPoints(verb, [], [], [], [], []));
    }
}
