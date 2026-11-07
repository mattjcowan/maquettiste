using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>What a check answers (rights-design.md decisions 11 and 31): the verb is allowed, denied, or depends on what is not known here.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RightAnswer>))]
internal enum RightAnswer
{
    /// <summary>Every answering lifecycle says yes (<c>allowed</c>).</summary>
    [JsonStringEnumMemberName("allowed")] Allowed,

    /// <summary>One answering lifecycle says no (<c>denied</c>).</summary>
    [JsonStringEnumMemberName("denied")] Denied,

    /// <summary>
    /// None says no for certain, and one might (<c>depends</c>): a condition, an actor from the object, or another lifecycle's state decides.
    /// Only a caller that cannot know a condition's result or an actor from the object gets it; a run always knows.
    /// </summary>
    [JsonStringEnumMemberName("depends")] Depends,
}

/// <summary>
/// One right a lifecycle states (rights-design.md decisions 3 and 7): an inline right of its <see cref="Process.Rights"/>, or a use of a
/// named right with the named right's body. Its <see cref="Id"/> is what a refusal names.
/// </summary>
/// <param name="Lifecycle">The lifecycle whose rights list it.</param>
/// <param name="Entry">The entry of the lifecycle's rights: the inline right, or the use.</param>
/// <param name="Named">The named right a use names; <see langword="null"/> for an inline right.</param>
internal sealed record RightRule(Process Lifecycle, ProcessRight Entry, Right? Named)
{
    /// <summary>The right's id: an inline right's own, or the named right's.</summary>
    public string Id => Named?.Id ?? Entry.Id;

    /// <summary>The right's name.</summary>
    public string Name => Named?.Name ?? Entry.Name ?? "";

    /// <summary>Whether it allows or denies.</summary>
    public RightAccess Access => Named?.Access ?? Entry.Access ?? RightAccess.Allow;

    /// <summary>The actors it applies to; empty means everyone.</summary>
    public IReadOnlyList<string> Actors => Named?.Actors ?? Entry.Actors;

    /// <summary>The actors it does not apply to.</summary>
    public IReadOnlyList<string> Except => Named?.Except ?? Entry.Except;

    /// <summary>The verbs it speaks of, by key.</summary>
    public IReadOnlyList<string> Verbs => Named?.Verbs ?? Entry.Verbs;

    /// <summary>The relation ends whose links it speaks of; empty for the object.</summary>
    public IReadOnlyList<string> Ends => Named?.Ends ?? Entry.Ends;

    /// <summary>The attributes whose reading or modifying it speaks of (decision 21); empty for the object as a whole.</summary>
    public IReadOnlyList<string> Attributes => Named?.Attributes ?? Entry.Attributes;

    /// <summary>Whether it holds only while the object, or with <see cref="Ends"/> the link, is frozen (decision 18).</summary>
    public bool WhileFrozen => Named?.WhileFrozen ?? Entry.WhileFrozen;

    /// <summary>Its condition, or <see langword="null"/>.</summary>
    public Expr? Condition => Named?.Condition ?? Entry.Condition;

    /// <summary>The states of the lifecycle where it holds (the use's, for a named right); empty for every state.</summary>
    public IReadOnlyList<string> States => Entry.States;
}

/// <summary>
/// A closed list that a check counts (rights-design.md decisions 13 and 14): an event's or a human task's actors, or a gate's signers.
/// </summary>
/// <param name="Owner">The id of the event, invoke or gate whose list it is: what refuses at the closed-list check, or what allows.</param>
/// <param name="Actors">Its actors; empty lets anyone through and allows no one.</param>
internal sealed record RightsList(string Owner, IReadOnlyList<string> Actors);

/// <summary>
/// Who asks (rights-design.md decision 24): the actor it acts as, which closed lists check as they do today (the actor or one of its
/// members at any depth), and whether it satisfies an actor a right names, which counts every actor it satisfies.
/// </summary>
internal sealed record RightsCaller
{
    /// <summary>The actor the caller acts as, or <see langword="null"/> for none.</summary>
    public string? ActsAs { get; init; }

    /// <summary>Whether the acting actor passes an actor a closed list names: it is that actor or one of its members at any depth.</summary>
    public required Func<string, bool> Accepts { get; init; }

    /// <summary>
    /// Whether the caller satisfies an actor a right names in <c>actors</c> or <c>except</c>: <see langword="true"/>, <see langword="false"/>, or
    /// <see langword="null"/> when it cannot be known here (an actor from the object outside a run), which makes the answer depend.
    /// </summary>
    public required Func<string, bool?> Satisfies { get; init; }

    /// <summary>
    /// A caller that acts as an actor and holds others with no context (rights-design.md decision 29, <c>RightRequest.Held</c>): it satisfies
    /// an actor that it, or a held actor, is or is a member of at any depth (decision 24's "a step with only an actor"). An actor from the
    /// object is satisfied only when named exactly; otherwise it is <see langword="false"/>, or unknown when <paramref name="objectUnknown"/>.
    /// </summary>
    /// <param name="model">The model, for memberships.</param>
    /// <param name="actsAs">The actor it acts as, or <see langword="null"/>.</param>
    /// <param name="held">The actors it holds with no context, besides the one it acts as.</param>
    /// <param name="objectUnknown">Whether an actor from the object it does not name exactly is unknown (who can do what) rather than unmet.</param>
    /// <returns>The caller.</returns>
    public static RightsCaller Acting(ModelSnapshot model, string? actsAs, IReadOnlyList<string>? held = null, bool objectUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(model);
        var membership = ActorMembership.Of(model);
        var holds = held ?? [];
        bool Accepts(string listed) => actsAs is not null && membership.Accepts(listed, actsAs);
        return new RightsCaller
        {
            ActsAs = actsAs,
            Accepts = Accepts,
            Satisfies = listed =>
            {
                if (Accepts(listed) || holds.Any(h => membership.Accepts(listed, h)))
                    return true;
                return objectUnknown && model.Get<Actor>(listed) is { Relative: not null } ? null : false;
            },
        };
    }

    /// <summary>
    /// A scenario's participant (rights-design.md decision 24): it satisfies an actor as <see cref="Participants.Satisfies"/> says (its actor,
    /// its holdings, and actors from the object), or as the actor it acts as does.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="participant">The participant.</param>
    /// <param name="actsAs">The actor the step acts as.</param>
    /// <returns>The caller.</returns>
    public static RightsCaller Participant(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioParticipant participant, string? actsAs)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(participant);
        var membership = ActorMembership.Of(model);
        bool Accepts(string listed) => actsAs is not null && membership.Accepts(listed, actsAs);
        return new RightsCaller
        {
            ActsAs = actsAs,
            Accepts = Accepts,
            Satisfies = listed => Accepts(listed) || Participants.Satisfies(model, process, cast, participant, listed),
        };
    }
}

/// <summary>
/// One check (rights-design.md decisions 10 to 12): a verb, done by a caller, to an object in its states, from a process's move or outside
/// one. Its init members let later parts add more without changing a call.
/// </summary>
internal sealed record RightsQuery
{
    /// <summary>The verb's key; <see langword="null"/> for an event that names no verb, which only its closed list and its lifecycle's default
    /// answer (decision 13).</summary>
    public string? Verb { get; init; }

    /// <summary>
    /// The process whose move, signature or task it is, or that runs the step: a lifecycle answers first, and alone for move, signature and
    /// task verbs; an orchestration holds no rights; <see langword="null"/> outside a process (an edit through an API).
    /// </summary>
    public Process? Process { get; init; }

    /// <summary>
    /// The object's entity, which decides the other lifecycles that answer for object, link and own verbs; <see langword="null"/> when the
    /// run has no object, and the lifecycles that govern every subject of <see cref="Process"/> answer (decision 10).
    /// </summary>
    public Entity? Entity { get; init; }

    /// <summary>The caller.</summary>
    public required RightsCaller Caller { get; init; }

    /// <summary>
    /// The relation end of the subject when the verb acts on a link through it (decision 20); <see langword="null"/> when it acts on the object.
    /// </summary>
    public string? End { get; init; }

    /// <summary>
    /// The object's states, by lifecycle id: the active states, or any states given, each counted with its ancestors. A lifecycle missing
    /// here reads as its initial state (decision 27); so does every lifecycle for <c>create</c> (decision 6).
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> States { get; init; } = FrozenDictionary<string, IReadOnlyList<string>>.Empty;

    /// <summary>
    /// The closed lists of the closed-list check (decision 12's second row): the event's or the human task's actors. When one names actors,
    /// the caller must pass one of them, or is refused by the first; a list the caller passes counts as an allow of the acting lifecycle.
    /// </summary>
    public IReadOnlyList<RightsList> Closed { get; init; } = [];

    /// <summary>
    /// The gates whose signers count as an allow of the acting lifecycle (decision 14): for the gated event, those on the transitions it can
    /// take from the active states; for <c>sign</c>, the gate signed. They never refuse here: the signatures step does.
    /// </summary>
    public IReadOnlyList<RightsList> Signers { get; init; } = [];

    /// <summary>
    /// The result of a right's condition for this object and caller (decision 6), through the process expression machinery:
    /// <see langword="true"/>, <see langword="false"/>, or <see langword="null"/> when it cannot be known here, which makes the answer depend.
    /// In a run an unknown result is <see langword="false"/>, since a right then says nothing. Absent, every condition is unknown.
    /// </summary>
    public Func<RightRule, bool?>? Condition { get; init; }

    /// <summary>
    /// Whether the object is frozen (decisions 16 to 18): <c>modify</c> and <c>delete</c> of it, and <c>connect</c> and <c>disconnect</c> of its
    /// links, are refused before any right, and rights with <c>whileFrozen</c> and no <c>ends</c> hold.
    /// </summary>
    public bool Frozen { get; init; }

    /// <summary>
    /// Whether the link the verb acts on is frozen (decisions 16 to 18): <c>disconnect</c>, and <c>modify</c> with <see cref="End"/>, are refused
    /// before any right, and rights with <c>whileFrozen</c> and <c>ends</c> hold. With no end known, whether a link of the object may be frozen.
    /// </summary>
    public bool LinkFrozen { get; init; }

    /// <summary>
    /// The attributes the verb reads or modifies (decision 21): each is checked on its own, against the rights that name it and those that
    /// name no attributes, and one refused attribute refuses the verb. <see langword="null"/> or empty when not known, such as for an event
    /// that names <c>modify</c>: then a deny that names attributes holds, and an allow that names attributes grants nothing.
    /// </summary>
    public IReadOnlyList<string>? Attributes { get; init; }

    /// <summary>
    /// Whether a right that names attributes makes the answer depend, and is named in it, instead of failing closed: for a cell of who can
    /// do what, which speaks of no attribute (decision 21). <see cref="Attributes"/> is then not read.
    /// </summary>
    public bool AttributesDepend { get; init; }
}

/// <summary>One answering lifecycle's own answer (rights-design.md decision 11).</summary>
/// <param name="Lifecycle">The lifecycle.</param>
/// <param name="Answer">Its answer.</param>
/// <param name="ByDefault">Whether its default decided.</param>
/// <param name="By">The ids that decide: the rights that deny, else those that allow and the closed lists the caller passes, else the
/// lifecycle (its default); for <see cref="RightAnswer.Depends"/>, the rights whose holding is not known.</param>
internal sealed record LifecycleAnswer(Process Lifecycle, RightAnswer Answer, bool ByDefault, IReadOnlyList<string> By);

/// <summary>The answer of a check (rights-design.md decisions 11, 12 and 26).</summary>
/// <param name="Answer">Allowed, denied, or depends.</param>
/// <param name="Refusal">For a denied answer, the value of <see cref="Refusals"/>: <c>frozen</c>, <c>actor</c>, <c>right</c> or
/// <c>default</c>.</param>
/// <param name="RefusedBy">For a denied answer, the id that refused it: the event or invoke, the right (inline, or the named right), or the
/// lifecycle whose default denies; <see langword="null"/> for <c>frozen</c>, since freezing is built in.</param>
/// <param name="Rights">The rights that held, each with its access, in check order: the denies as the third row reads them, then the allows,
/// lifecycle by lifecycle. Empty when the closed-list check refused.</param>
/// <param name="Lifecycles">The answering lifecycles that state rights, in check order, each with its own answer.</param>
/// <param name="Notes">What the check assumed: the lifecycles that answered in their initial state, or were not asked, for a run with no
/// object.</param>
internal sealed record RightsDecision(RightAnswer Answer, string? Refusal, string? RefusedBy, IReadOnlyList<RightEvaluation> Rights,
    IReadOnlyList<LifecycleAnswer> Lifecycles, IReadOnlyList<string> Notes)
{
    /// <summary>Whether the verb is allowed.</summary>
    public bool Allowed => Answer == RightAnswer.Allowed;
}

/// <summary>
/// The engine's one implementation of the rights check (rights-design.md sections 3 and 7), for one snapshot (<see cref="Of"/>): the
/// simulation, <c>get_rights</c>, <c>process.access_grid</c> and the editor's API all ask it. Each answering lifecycle answers on its own,
/// its denies, then its allows (a closed list the caller passes counts as one), then its default; then any "no" wins. The first check that
/// fails names the refusal: the closed lists, then every lifecycle's denies, then the defaults. A lifecycle is evaluated only when it states
/// rights or a default. It reads model elements only; conditions and actors from the object come from the query. Immutable once built, so
/// it is safe to share across threads.
/// </summary>
internal sealed class RightsEvaluator
{
    private static readonly ConditionalWeakTable<ModelSnapshot, RightsEvaluator> ByModel = new();

    private readonly ModelSnapshot _model;
    private readonly FrozenDictionary<string, IReadOnlyList<RightRule>> _rules;
    // The lifecycles that state rights or a default, by name then id: the others that may answer for object, link and own verbs.
    private readonly IReadOnlyList<Process> _stating;
    // The lifecycles of _stating that govern an entity, by entity id, worked out once per entity.
    private readonly ConcurrentDictionary<string, IReadOnlyList<Process>> _governing = new(StringComparer.Ordinal);

    private RightsEvaluator(ModelSnapshot model)
    {
        _model = model;
        var rules = new Dictionary<string, IReadOnlyList<RightRule>>(StringComparer.Ordinal);
        var stating = new List<Process>();
        foreach (var process in model.All<Process>())
        {
            // Only lifecycles hold rights (decision 2): an orchestration that still writes them is MQ9901 and uses nothing, so a project whose
            // only rights sit on one writes no refusedBy and pays for no rights path, as generated code (model.rights_lifecycles) agrees.
            if (process.Use != ProcessUse.Lifecycle || (process.Rights.Count == 0 && process.RightsDefault is null))
                continue;
            Uses = true;
            stating.Add(process);
            var list = new List<RightRule>(process.Rights.Count);
            foreach (var entry in process.Rights)
            {
                if (entry.Right is { } id)
                {
                    if (model.Get<Right>(id) is { } named)
                        list.Add(new RightRule(process, entry, named));
                }
                else if (entry.Access is not null)
                {
                    list.Add(new RightRule(process, entry, null)); // an entry in neither form is MQ9901 and says nothing
                }
            }

            if (list.Any(r => r.Attributes.Count > 0))
                NamesAttributes = true;

            rules[process.Id] = list;
        }

        _rules = rules.ToFrozenDictionary(StringComparer.Ordinal);
        stating.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        _stating = stating;
    }

    /// <summary>Whether the project uses rights (decision 28): a lifecycle writes <c>rights</c> or <c>rightsDefault</c>.</summary>
    public bool Uses { get; }

    /// <summary>
    /// Whether the project uses attribute rights (decision 28): a lifecycle's right names <c>attributes</c>. Without them, a check is one
    /// check, whatever attributes the query gives, so its answers are part 1's.
    /// </summary>
    public bool NamesAttributes { get; }

    /// <summary>The evaluator of a snapshot, built on first use and kept as long as the snapshot.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The evaluator.</returns>
    public static RightsEvaluator Of(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return ByModel.GetValue(model, static m => new RightsEvaluator(m));
    }

    /// <summary>The rights a lifecycle states, in file order, each use with its named right; empty for a lifecycle that states none.</summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The rights.</returns>
    public IReadOnlyList<RightRule> RulesOf(Process lifecycle)
    {
        ArgumentNullException.ThrowIfNull(lifecycle);
        return _rules.GetValueOrDefault(lifecycle.Id) ?? [];
    }

    /// <summary>A lifecycle's default (decision 9): allow unless it writes deny.</summary>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The default.</returns>
    public static RightAccess DefaultOf(Process lifecycle) => lifecycle?.RightsDefault ?? RightAccess.Allow;

    /// <summary>
    /// The group a verb's key belongs to for rights (decision 5): a built-in verb's group (<c>move</c>, <c>signature</c>, <c>task</c>,
    /// <c>object</c>, <c>link</c>), else <see cref="BuiltInVerbs.ProjectGroup"/>; <see langword="null"/> for no verb.
    /// </summary>
    /// <param name="verb">The verb's key.</param>
    /// <returns>The group.</returns>
    public static string? GroupOf(string? verb) => verb is null ? null : BuiltInVerbs.Get(verb)?.Group ?? BuiltInVerbs.ProjectGroup;

    /// <summary>Whether a verb is answered by the lifecycles that govern the object, not only the acting one: an object, link or own verb.</summary>
    /// <param name="verb">The verb's key.</param>
    /// <returns><see langword="true"/> for an object, link or own verb.</returns>
    public static bool SpeaksOfObject(string? verb) => GroupOf(verb) is "object" or "link" or BuiltInVerbs.ProjectGroup;

    /// <summary>
    /// Whether freezing refuses a verb (decision 17), the first check, which no allow lifts: a frozen object can't be modified or deleted,
    /// and its links can't be connected or disconnected; a frozen link can't be disconnected or modified. Freezing stops nothing else
    /// (decision 18).
    /// </summary>
    /// <param name="verb">The verb's key.</param>
    /// <param name="end">The relation end of the link the verb acts on, or <see langword="null"/> for the object.</param>
    /// <param name="objectFrozen">Whether the object is frozen.</param>
    /// <param name="linkFrozen">Whether the link is frozen; with no end known, whether a link of the object may be.</param>
    /// <returns>Whether the verb is refused as frozen.</returns>
    public static bool RefusedByFreezing(string? verb, string? end, bool objectFrozen, bool linkFrozen) => verb switch
    {
        "modify" => end is null ? objectFrozen : linkFrozen,
        "delete" => end is null && objectFrozen,
        "connect" => objectFrozen,
        "disconnect" => objectFrozen || linkFrozen,
        _ => false,
    };

    /// <summary>
    /// The lifecycles that answer a verb (decision 10), in check order, and the notes of a run with no object: the acting lifecycle; and for
    /// object, link and own verbs, every other lifecycle that governs the object, by name. Only lifecycles that state rights or a default
    /// are listed, since the others always say yes.
    /// </summary>
    /// <param name="query">The check.</param>
    /// <returns>The lifecycles and the notes.</returns>
    public (IReadOnlyList<Process> Lifecycles, IReadOnlyList<string> Notes) Answering(RightsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var acting = query.Process is { Use: ProcessUse.Lifecycle } lifecycle ? lifecycle : null;
        var answering = new List<Process>();
        if (acting is not null && _rules.ContainsKey(acting.Id))
            answering.Add(acting);
        if (!SpeaksOfObject(query.Verb))
            return (answering, []);
        var notes = new List<string>();
        IReadOnlyList<Entity>? subjects = null;
        if (query.Entity is null)
        {
            subjects = query.Process is { } process ? SubjectsOf(process) : [];
            if (subjects.Count == 0)
                return (answering, []);
        }

        if (query.Entity is { } entity)
        {
            foreach (var other in Governing(entity))
            {
                if (acting is null || !string.Equals(other.Id, acting.Id, StringComparison.Ordinal))
                    answering.Add(other);
            }

            return (answering, notes);
        }

        foreach (var other in _stating)
        {
            if (acting is not null && string.Equals(other.Id, acting.Id, StringComparison.Ordinal))
                continue;
            var governed = subjects!.Count(s => SubjectCoverage.Governs(_model, other, s));
            if (governed == subjects!.Count)
            {
                answering.Add(other);
                if (!query.States.ContainsKey(other.Id))
                    notes.Add($"The run has no object, so lifecycle '{other.Name}' answers in its initial state.");
            }
            else if (governed > 0)
            {
                notes.Add($"Lifecycle '{other.Name}' governs only some subjects of '{query.Process!.Name}', so it is not asked.");
            }
        }

        return (answering, notes);
    }

    /// <summary>
    /// The lifecycles that state rights or a default and govern an entity, by name then id: those that answer for its object, link and own
    /// verbs. Worked out once per entity and snapshot.
    /// </summary>
    /// <param name="entity">The entity.</param>
    /// <returns>The lifecycles.</returns>
    public IReadOnlyList<Process> Governing(Entity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return _governing.GetOrAdd(entity.Id, _ => [.. _stating.Where(l => SubjectCoverage.Governs(_model, l, entity))]);
    }

    /// <summary>
    /// One lifecycle's own answer to a check (decision 11), as <see cref="Check"/> gives it among its lifecycles, without asking any other:
    /// its denies, then its allows (a closed list the caller passes counts when it is the acting lifecycle), then its default.
    /// <see langword="null"/> when it states no rights and no default, and so always says yes.
    /// </summary>
    /// <param name="query">The check.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>Its answer, or <see langword="null"/>.</returns>
    public LifecycleAnswer? AnswerOf(RightsQuery query, Process lifecycle)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(lifecycle);
        if (!_rules.ContainsKey(lifecycle.Id))
            return null;
        var acting = query.Process is { Use: ProcessUse.Lifecycle } p && string.Equals(p.Id, lifecycle.Id, StringComparison.Ordinal);
        return Answer(query, lifecycle, acting, out _);
    }

    /// <summary>
    /// Checks a verb (decisions 10 to 12): freezing, then the closed lists, then every answering lifecycle's denies, then their defaults.
    /// With attributes known and attribute rights in the project, each lifecycle answers per attribute, and any no wins (decision 21).
    /// </summary>
    /// <param name="query">The check.</param>
    /// <returns>The answer.</returns>
    public RightsDecision Check(RightsQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var caller = query.Caller;

        // Freezing (decision 12, first row): built in, so no element refused it.
        if (RefusedByFreezing(query.Verb, query.End, query.Frozen, query.LinkFrozen))
            return new RightsDecision(RightAnswer.Denied, Refusals.Frozen, null, [], [], []);

        // The closed lists (decision 12, second row): the caller must pass one that names actors; the event's or task's list refuses.
        var named = query.Closed.Where(l => l.Actors.Count > 0).ToList();
        if (named.Count > 0 && !named.Any(l => l.Actors.Any(caller.Accepts)))
            return new RightsDecision(RightAnswer.Denied, Refusals.Actor, named[0].Owner, [], [], []);

        var (lifecycles, notes) = Answering(query);
        var acting = query.Process is { Use: ProcessUse.Lifecycle } p ? p : null;
        var answers = new List<LifecycleAnswer>(lifecycles.Count);
        var denies = new List<RightEvaluation>();
        var allows = new List<RightEvaluation>();
        string? deniedBy = null;
        foreach (var lifecycle in lifecycles)
        {
            var isActing = acting is not null && string.Equals(lifecycle.Id, acting.Id, StringComparison.Ordinal);
            var answer = Answer(query, lifecycle, isActing, out var holding);
            answers.Add(answer);
            foreach (var (rule, holds) in holding)
            {
                if (holds != true)
                    continue;
                var evaluation = new RightEvaluation(rule.Id, lifecycle.Id, rule.Access);
                if (rule.Access == RightAccess.Deny)
                {
                    denies.Add(evaluation);
                    deniedBy ??= rule.Id; // the third row: the acting lifecycle's first, in file order, then the others by name
                }
                else
                {
                    allows.Add(evaluation);
                }
            }
        }

        IReadOnlyList<RightEvaluation> rights = [.. denies, .. allows];
        if (deniedBy is not null)
            return new RightsDecision(RightAnswer.Denied, Refusals.Right, deniedBy, rights, answers, notes);
        // The fourth row: the first lifecycle, in the same order, where no allow holds and the default is deny.
        if (answers.FirstOrDefault(a => a is { Answer: RightAnswer.Denied, ByDefault: true }) is { } byDefault)
            return new RightsDecision(RightAnswer.Denied, Refusals.Default, byDefault.Lifecycle.Id, rights, answers, notes);
        var overall = answers.Any(a => a.Answer == RightAnswer.Depends) ? RightAnswer.Depends : RightAnswer.Allowed;
        return new RightsDecision(overall, null, null, rights, answers, notes);
    }

    /// <summary>
    /// One lifecycle's answer (decision 11), and whether each of its rights held. With attributes known and attribute rights in the project,
    /// it answers once per attribute (decision 21), and the answers join: denied if one is, by a right if one is, else by its default; else
    /// depends if one does; else allowed. A right held if it held for one attribute.
    /// </summary>
    private LifecycleAnswer Answer(RightsQuery query, Process lifecycle, bool acting, out List<(RightRule Rule, bool? Holds)> holding)
    {
        if (!NamesAttributes || query.AttributesDepend || query.Attributes is not { Count: > 1 } known)
        {
            var attribute = NamesAttributes && !query.AttributesDepend && query.Attributes is { Count: 1 } one ? one[0] : null;
            return Answer(query, lifecycle, acting, attribute, out holding);
        }

        var answers = new List<LifecycleAnswer>(known.Count);
        var held = new Dictionary<RightRule, bool?>(ReferenceEqualityComparer.Instance);
        foreach (var attribute in known.Distinct(StringComparer.Ordinal))
        {
            answers.Add(Answer(query, lifecycle, acting, attribute, out var each));
            foreach (var (rule, holds) in each)
            {
                if (!held.TryGetValue(rule, out var before) || (before != true && holds == true))
                    held[rule] = holds;
            }
        }

        holding = [.. RulesOf(lifecycle).Where(held.ContainsKey).Select(r => (r, held[r]))];
        static List<string> By(IEnumerable<LifecycleAnswer> some) => [.. some.SelectMany(a => a.By).Distinct(StringComparer.Ordinal)];
        if (answers.Any(a => a.Answer == RightAnswer.Denied))
        {
            var byRight = answers.Where(a => a is { Answer: RightAnswer.Denied, ByDefault: false }).ToList();
            return byRight.Count > 0 ? new(lifecycle, RightAnswer.Denied, false, By(byRight)) : new(lifecycle, RightAnswer.Denied, true, [lifecycle.Id]);
        }

        if (answers.Any(a => a.Answer == RightAnswer.Depends))
            return new(lifecycle, RightAnswer.Depends, false, By(answers.Where(a => a.Answer == RightAnswer.Depends)));
        return new(lifecycle, RightAnswer.Allowed, answers.All(a => a.ByDefault), By(answers));
    }

    /// <summary>
    /// One lifecycle's answer for one attribute, or for the verb as a whole (<paramref name="attribute"/> <see langword="null"/>): its denies,
    /// then its allows, then its default, over the rights that apply (<see cref="Applies"/>); and whether each of them held.
    /// </summary>
    private LifecycleAnswer Answer(RightsQuery query, Process lifecycle, bool acting, string? attribute, out List<(RightRule Rule, bool? Holds)> holding)
    {
        var configuration = Configuration(query, lifecycle);
        holding = [];
        foreach (var rule in RulesOf(lifecycle))
        {
            if (!Applies(rule, query, attribute))
                continue;
            var holds = Holds(query, rule, configuration);
            // In a cell of who can do what, a right that names attributes holds only for some of them, so the cell depends on it.
            if (holds == true && query.AttributesDepend && rule.Attributes.Count > 0)
                holds = null;
            if (holds != false)
                holding.Add((rule, holds));
        }

        var deny = holding.Where(h => h.Rule.Access == RightAccess.Deny).ToList();
        var allow = holding.Where(h => h.Rule.Access == RightAccess.Allow).ToList();
        if (deny.Any(h => h.Holds == true))
            return new(lifecycle, RightAnswer.Denied, false, [.. deny.Where(h => h.Holds == true).Select(h => h.Rule.Id).Distinct(StringComparer.Ordinal)]);

        // A closed list the caller passes is an allow of its own lifecycle (decisions 13 and 14); it can't widen one that refuses it.
        List<string> allowedBy = [.. allow.Where(h => h.Holds == true).Select(h => h.Rule.Id)];
        if (acting)
        {
            foreach (var list in query.Closed.Concat(query.Signers))
            {
                if (list.Actors.Any(query.Caller.Accepts))
                    allowedBy.Add(list.Owner);
            }
        }

        var maybeDenies = deny.Select(h => h.Rule.Id).Distinct(StringComparer.Ordinal).ToList();
        var defaultDenies = DefaultOf(lifecycle) == RightAccess.Deny;
        if (allowedBy.Count > 0)
        {
            return maybeDenies.Count > 0
                ? new(lifecycle, RightAnswer.Depends, false, maybeDenies)
                : new(lifecycle, RightAnswer.Allowed, false, [.. allowedBy.Distinct(StringComparer.Ordinal)]);
        }

        if (!defaultDenies)
        {
            return maybeDenies.Count > 0
                ? new(lifecycle, RightAnswer.Depends, false, maybeDenies)
                : new(lifecycle, RightAnswer.Allowed, true, [lifecycle.Id]);
        }

        // The default denies: an allow that may hold makes it depend, and a deny that may hold changes nothing.
        var maybeAllows = allow.Select(h => h.Rule.Id).Distinct(StringComparer.Ordinal).ToList();
        return maybeAllows.Count > 0
            ? new(lifecycle, RightAnswer.Depends, false, maybeAllows)
            : new(lifecycle, RightAnswer.Denied, true, [lifecycle.Id]);
    }

    /// <summary>
    /// Whether a right that names attributes applies to a check (decision 21): a right that names none always does; for one attribute, a
    /// right that names it; with the attributes not known, a deny (it might be one of its attributes) but no allow; in a cell of who can do
    /// what, every right, and those that name attributes make it depend.
    /// </summary>
    private static bool Applies(RightRule rule, RightsQuery query, string? attribute)
    {
        if (rule.Attributes.Count == 0 || query.AttributesDepend)
            return true;
        return attribute is null ? rule.Access == RightAccess.Deny : rule.Attributes.Contains(attribute, StringComparer.Ordinal);
    }

    /// <summary>
    /// Whether a right holds (decisions 4 to 6, 18 and 20): it names the verb, one of its states is active (empty: every state), it speaks of
    /// what the verb acts on, with <c>whileFrozen</c> the object (with <c>ends</c>, the link) is frozen, the caller satisfies one of its actors
    /// (empty: everyone) and none of its exceptions, and its condition is true.
    /// <see langword="null"/> when an actor from the object or the condition can't be known here.
    /// </summary>
    private static bool? Holds(RightsQuery query, RightRule rule, IReadOnlySet<string> configuration)
    {
        if (query.Verb is not { } verb || !rule.Verbs.Contains(verb, StringComparer.Ordinal))
            return false;
        if (rule.States.Count > 0 && !rule.States.Any(configuration.Contains))
            return false;
        if (!SpeaksOf(rule, verb, query.End))
            return false;
        if (rule.WhileFrozen && !(rule.Ends.Count > 0 ? query.LinkFrozen : query.Frozen))
            return false;
        bool? result = true;
        if (rule.Actors.Count > 0)
        {
            result = Any(rule.Actors, query.Caller.Satisfies);
            if (result == false)
                return false;
        }

        // An exception the caller satisfies takes it out of this right; other rights and the default still answer (decision 4).
        if (rule.Except.Count > 0)
        {
            var excepted = Any(rule.Except, query.Caller.Satisfies);
            if (excepted == true)
                return false;
            if (excepted is null)
                result = null;
        }

        if (rule.Condition is not null)
        {
            var condition = query.Condition?.Invoke(rule);
            if (condition == false)
                return false;
            if (condition is null)
                result = null;
        }

        return result;
    }

    /// <summary>
    /// Whether a right speaks of what the verb acts on (decision 20): with <c>ends</c>, only links through one of them; without, the object,
    /// and for a link verb (<c>connect</c>, <c>disconnect</c>) every link too. A link verb with no end known (an event that names
    /// <c>connect</c>, a cell of who can do what) fails closed: a deny with <c>ends</c> holds, since it might be one of its links, and an allow
    /// with <c>ends</c> grants nothing.
    /// </summary>
    private static bool SpeaksOf(RightRule rule, string verb, string? end)
    {
        if (rule.Ends.Count > 0)
            return end is null ? rule.Access == RightAccess.Deny && GroupOf(verb) == "link" : rule.Ends.Contains(end, StringComparer.Ordinal);
        return end is null || GroupOf(verb) == "link";
    }

    private static bool? Any(IReadOnlyList<string> actors, Func<string, bool?> satisfies)
    {
        bool? any = false;
        foreach (var actor in actors)
        {
            var satisfied = satisfies(actor);
            if (satisfied == true)
                return true;
            if (satisfied is null)
                any = null;
        }

        return any;
    }

    /// <summary>
    /// The active states of a lifecycle with their ancestors, so a compound state counts when a state inside it is active (decision 6): the
    /// states the query gives, else the initial configuration (decision 27), which <c>create</c> always reads.
    /// </summary>
    private IReadOnlySet<string> Configuration(RightsQuery query, Process lifecycle)
    {
        var chart = ProcessLibraries.Chart(_model, lifecycle);
        var given = !string.Equals(query.Verb, "create", StringComparison.Ordinal) && query.States.TryGetValue(lifecycle.Id, out var states)
            ? states.Where(chart.ById.ContainsKey)
            : [];
        return ObjectStates.Configuration(chart, given).ToHashSet(StringComparer.Ordinal);
    }

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
}
