using System.Globalization;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>
/// Whether one kind of caller, one who satisfies <see cref="Actor"/> and no other actor the grid names, may do <see cref="Verb"/> while the
/// object is in <see cref="State"/> (rights-design.md decision 31; <c>getRights</c> and <c>get_rights</c>).
/// </summary>
/// <param name="State">An atomic state of the lifecycle.</param>
/// <param name="Actor">The actor of the row; <see langword="null"/> for everyone else, who satisfies none of the actors the grid names.</param>
/// <param name="Verb">The verb's key.</param>
/// <param name="Entity">The entity the row holds for, when the subjects are governed by different other lifecycles and that changes the
/// answer; <see langword="null"/>, and left out of the JSON, when one row holds for every entity.</param>
/// <param name="Answer"><c>allowed</c>, <c>denied</c> or <c>depends</c>.</param>
/// <param name="ByDefault">Whether a lifecycle's default decides it, no right or closed list.</param>
/// <param name="By">The ids of what decides: rights (an inline right, or the named right), events, human tasks, gates, or a lifecycle.</param>
/// <param name="DependsOn">The ids of the other lifecycles that may say no; empty otherwise.</param>
public sealed record RightsRow(string State, string? Actor, string Verb,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Entity,
    string Answer, bool ByDefault, IReadOnlyList<string> By, IReadOnlyList<string> DependsOn);

/// <summary>One page of who can do what in a lifecycle (rights-design.md decision 31).</summary>
/// <param name="Lifecycle">The lifecycle's id.</param>
/// <param name="RightsDefault">Its default, <c>allow</c> when its file writes none.</param>
/// <param name="Rows">The rows of the page, in grid order: state, actor, verb, then entity.</param>
/// <param name="Next">The cursor of the next page; <see langword="null"/> on the last one.</param>
public sealed record RightsPage(string Lifecycle, string RightsDefault, IReadOnlyList<RightsRow> Rows, string? Next);

/// <summary>What a <c>get_rights</c> call narrows the grid to; each list empty means no filter.</summary>
public sealed record RightsFilter
{
    /// <summary>State ids: the rows of the atomic states that are, or are inside, one of them.</summary>
    public IReadOnlyList<string> States { get; init; } = [];

    /// <summary>Actor ids: only the rows of these actors (the "everyone else" row is left out).</summary>
    public IReadOnlyList<string> Actors { get; init; } = [];

    /// <summary>Verb keys: only the rows of these verbs.</summary>
    public IReadOnlyList<string> Verbs { get; init; } = [];

    /// <summary>An entity id, one of the lifecycle's subjects: only the rows that hold for it.</summary>
    public string? Entity { get; init; }
}

/// <summary>
/// "Who can do what" in a lifecycle (rights-design.md decision 31 and section 6): per atomic state, actor and verb, the answer of
/// <see cref="RightsEvaluator"/>, with the closed lists of the events that can be sent from the state, its human tasks for <c>complete</c> and
/// its gates' signers. Each lifecycle answers on its own and any no wins. Another lifecycle that governs the object answers only for object,
/// link and own verbs, and its state is not known in a cell: one that says no in every one of its states denies the cell; one that could
/// say no in only some, or under a condition, makes it depend (<c>dependsOn</c>). It reads the snapshot only, never the resolved model, so
/// <c>get_rights</c> and <c>process.access_grid</c> give the same rows.
/// </summary>
internal static class RightsGrid
{
    /// <summary>The answer of a cell's own lifecycle, or of one cell for one entity.</summary>
    private sealed record Cell(string Answer, bool ByDefault, IReadOnlyList<string> By, IReadOnlyList<string> DependsOn)
    {
        public bool SameAs(Cell other) => Answer == other.Answer && ByDefault == other.ByDefault && By.SequenceEqual(other.By, StringComparer.Ordinal)
            && DependsOn.SequenceEqual(other.DependsOn, StringComparer.Ordinal);
    }

    /// <summary>
    /// The rows of a lifecycle's grid, in order: atomic states in document order, actors by (name, id) then everyone else, verbs by key, then
    /// entities by (name, id) where a cell has one row per entity.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="filter">What to narrow the grid to, or <see langword="null"/> for every row.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The rows.</returns>
    public static IReadOnlyList<RightsRow> Rows(ModelSnapshot model, Process lifecycle, RightsFilter? filter = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(lifecycle);
        filter ??= new RightsFilter();
        var evaluator = RightsEvaluator.Of(model);
        var chart = ProcessLibraries.Chart(model, lifecycle);
        var rules = evaluator.RulesOf(lifecycle);

        // The actors the grid names: in the lifecycle's rights (actors and except), events, human tasks and gates' signers.
        var named = new HashSet<string>(StringComparer.Ordinal);
        var verbKeys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rule in rules)
        {
            named.UnionWith(rule.Actors);
            named.UnionWith(rule.Except);
            verbKeys.UnionWith(rule.Verbs);
        }

        foreach (var ev in chart.Events.Values)
        {
            named.UnionWith(ev.Actors);
            if (ev.Verb is { Length: > 0 } key)
                verbKeys.Add(key);
        }

        foreach (var (invoke, _) in chart.Invokes.Values)
        {
            if (invoke.Type == InvokeType.HumanTask)
                named.UnionWith(invoke.Actors);
        }

        foreach (var t in chart.Transitions)
        {
            if (t.Gate is { } gate)
                named.UnionWith(gate.Signers);
        }

        var actors = named.Select(model.Get<Actor>).OfType<Actor>().OrderBy(a => a.Name, StringComparer.Ordinal).ThenBy(a => a.Id, StringComparer.Ordinal)
            .Select(a => (string?)a.Id).Append(null).ToList();
        var states = chart.States.Where(s => s.Parent is not null && s.Children.IsEmpty && s.Type is not (StateType.History or StateType.Choice)).ToList();

        // Other lifecycles that could answer: those that state rights or a default and govern a subject, per subject entity.
        var entities = Subjects(model, lifecycle).OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToList();
        var stating = model.All<Process>().Where(p => p.Use == ProcessUse.Lifecycle && !string.Equals(p.Id, lifecycle.Id, StringComparison.Ordinal)
                && (p.Rights.Count > 0 || p.RightsDefault is not null))
            .OrderBy(p => p.Name, StringComparer.Ordinal).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        var governing = entities.Select(e => stating.Where(o => SubjectCoverage.Governs(model, o, e)).ToList()).ToList();

        var rows = new List<RightsRow>();
        var others = new Dictionary<(string? Actor, string Verb, string Lifecycle), Cell?>();
        foreach (var state in states)
        {
            ct.ThrowIfCancellationRequested();
            var active = state.Ancestors.Select(a => a.Id).Prepend(state.Id).ToHashSet(StringComparer.Ordinal);
            if (filter.States.Count > 0 && !filter.States.Any(active.Contains))
                continue;
            var path = state.Ancestors.Prepend(state).ToList();
            var moves = path.SelectMany(s => s.Transitions).Where(t => t.Event is not null).ToList();
            var tasks = path.SelectMany(s => s.Invokes).Where(i => i.Type == InvokeType.HumanTask).ToList();
            foreach (var actor in actors)
            {
                if (filter.Actors.Count > 0 && (actor is null || !filter.Actors.Contains(actor, StringComparer.Ordinal)))
                    continue;
                var caller = actor is null
                    ? new RightsCaller { Accepts = static _ => false, Satisfies = static _ => false }
                    : RightsCaller.Acting(model, actor, objectUnknown: true);
                foreach (var verb in verbKeys)
                {
                    if (filter.Verbs.Count > 0 && !filter.Verbs.Contains(verb, StringComparer.Ordinal))
                        continue;
                    var own = Own(evaluator, lifecycle, state.Id, moves, tasks, verb, caller);
                    var cells = new List<(Entity? Entity, Cell Cell)>();
                    if (own.Answer == "denied" || !RightsEvaluator.SpeaksOfObject(verb) || entities.Count == 0)
                    {
                        cells.Add((null, own));
                    }
                    else
                    {
                        for (var i = 0; i < entities.Count; i++)
                        {
                            var answers = governing[i].Select(o =>
                            {
                                var key = (actor, verb, o.Id);
                                if (!others.TryGetValue(key, out var known))
                                    others[key] = known = Other(model, evaluator, o, verb, caller);
                                return (Lifecycle: o, Cell: known);
                            }).Where(a => a.Cell is not null).ToList();
                            cells.Add((entities[i], Combine(own, answers!)));
                        }

                        if (cells.All(c => c.Cell.SameAs(cells[0].Cell)))
                            cells = [(null, cells[0].Cell)];
                    }

                    foreach (var (entity, cell) in cells)
                    {
                        if (filter.Entity is { } wanted && entity is not null && !string.Equals(entity.Id, wanted, StringComparison.Ordinal))
                            continue;
                        rows.Add(new RightsRow(state.Id, actor, verb, entity?.Id, cell.Answer, cell.ByDefault, cell.By, cell.DependsOn));
                    }
                }
            }
        }

        return rows;
    }

    /// <summary>
    /// One page of a lifecycle's grid: <paramref name="cursor"/> is the offset the previous page gave (none for the first page).
    /// <see langword="null"/> when the cursor is not one a page gave.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="cursor">The cursor, or <see langword="null"/>.</param>
    /// <param name="limit">The page size.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page, or <see langword="null"/> for a bad cursor.</returns>
    public static RightsPage? Page(ModelSnapshot model, Process lifecycle, RightsFilter? filter, string? cursor, int limit, CancellationToken ct = default)
    {
        var start = 0;
        if (!string.IsNullOrEmpty(cursor) && (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0))
            return null;
        var rows = Rows(model, lifecycle, filter, ct);
        if (start > rows.Count)
            return null;
        var page = rows.Skip(start).Take(limit).ToList();
        var next = start + page.Count < rows.Count ? (start + page.Count).ToString(CultureInfo.InvariantCulture) : null;
        return new RightsPage(lifecycle.Id, RightsEvaluator.DefaultOf(lifecycle) == RightAccess.Deny ? "deny" : "allow", page, next);
    }

    /// <summary>
    /// The entities a lifecycle governs (its subjects), each once: an entity entry's entity, a stereotype entry's carriers.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="process">The process.</param>
    /// <returns>The entities, in entry order.</returns>
    public static IReadOnlyList<Entity> Subjects(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        var subjects = new List<Entity>();
        foreach (var entry in process.Subjects)
        {
            var found = entry.Entity is { } id ? model.Get<Entity>(id) is { } entity ? [entity] : [] : SubjectCoverage.Governed(model, process, entry);
            foreach (var e in found)
            {
                if (!subjects.Contains(e))
                    subjects.Add(e);
            }
        }

        return subjects;
    }

    /// <summary>
    /// The lifecycle's own answer in a state (decisions 11, 13, 14 and 31): the closed lists of the events naming the verb that can be sent
    /// from it (the caller passes when any one lets it through), its human tasks for <c>complete</c>, and its gates' signers as allows; then
    /// its rights and default.
    /// </summary>
    private static Cell Own(RightsEvaluator evaluator, Process lifecycle, string state, List<ChartTransition> moves, List<ProcessInvoke> tasks, string verb,
        RightsCaller caller)
    {
        var closed = new List<RightsList>();
        var signers = new List<RightsList>();
        foreach (var t in moves)
        {
            if (string.Equals(t.Event!.Verb, verb, StringComparison.Ordinal) && !closed.Any(l => string.Equals(l.Owner, t.Event.Id, StringComparison.Ordinal)))
                closed.Add(new RightsList(t.Event.Id, t.Event.Actors));
            if (t.Gate is { Signers.Count: > 0 } gate && (verb == "sign" || string.Equals(t.Event.Verb, verb, StringComparison.Ordinal))
                && !signers.Any(l => string.Equals(l.Owner, gate.Id, StringComparison.Ordinal)))
                signers.Add(new RightsList(gate.Id, gate.Signers));
        }

        if (verb == "complete")
            closed.AddRange(tasks.Select(i => new RightsList(i.Id, i.Actors)));
        if (closed.Count > 0 && !closed.Any(l => l.Actors.Count == 0 || l.Actors.Any(caller.Accepts)))
            return new Cell("denied", false, [.. closed.Select(l => l.Owner)], []);

        var passing = closed.Where(l => l.Actors.Count > 0 && l.Actors.Any(caller.Accepts)).ToList();
        var decision = evaluator.Check(new RightsQuery
        {
            Verb = verb, Process = lifecycle, Caller = caller, Closed = passing, Signers = signers,
            States = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [lifecycle.Id] = [state] },
        });
        if (decision.Lifecycles.FirstOrDefault(a => ReferenceEquals(a.Lifecycle, lifecycle)) is { } answer)
            return FromAnswer(answer);

        // A lifecycle that states no rights and no default allows; a closed list the caller passes is what lets it through.
        var allowedBy = passing.Concat(signers.Where(l => l.Actors.Any(caller.Accepts))).Select(l => l.Owner).Distinct(StringComparer.Ordinal).ToList();
        return allowedBy.Count > 0 ? new Cell("allowed", false, allowedBy, []) : new Cell("allowed", true, [lifecycle.Id], []);
    }

    /// <summary>
    /// Another lifecycle's answer across its atomic states (decision 31): denied when it says no in every one of them (by the first state's
    /// rights or default), <see langword="null"/> when it says yes in all, otherwise depends.
    /// </summary>
    private static Cell? Other(ModelSnapshot model, RightsEvaluator evaluator, Process other, string verb, RightsCaller caller)
    {
        var chart = ProcessLibraries.Chart(model, other);
        var answers = new List<LifecycleAnswer>();
        foreach (var state in chart.States.Where(s => s.Parent is not null && s.Children.IsEmpty && s.Type is not (StateType.History or StateType.Choice)))
        {
            var decision = evaluator.Check(new RightsQuery
            {
                Verb = verb, Process = other, Caller = caller,
                States = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { [other.Id] = [state.Id] },
            });
            if (decision.Lifecycles.FirstOrDefault(a => ReferenceEquals(a.Lifecycle, other)) is { } answer)
                answers.Add(answer);
        }

        if (answers.Count == 0 || answers.All(a => a.Answer == RightAnswer.Allowed))
            return null;
        return answers.All(a => a.Answer == RightAnswer.Denied) ? FromAnswer(answers[0]) : new Cell("depends", false, [], [other.Id]);
    }

    /// <summary>A cell's own answer joined with the other lifecycles that govern one entity, in name order: any no wins.</summary>
    private static Cell Combine(Cell own, List<(Process Lifecycle, Cell Cell)> others)
    {
        if (others.FirstOrDefault(o => o.Cell.Answer == "denied") is { Lifecycle: not null } denied)
            return denied.Cell;
        var dependsOn = others.Where(o => o.Cell.Answer == "depends").Select(o => o.Lifecycle.Id).ToList();
        if (own.Answer == "depends" || dependsOn.Count > 0)
            return new Cell("depends", false, own.By, dependsOn);
        return own;
    }

    private static Cell FromAnswer(LifecycleAnswer answer) => new(answer.Answer switch
    {
        RightAnswer.Denied => "denied",
        RightAnswer.Depends => "depends",
        _ => "allowed",
    }, answer.ByDefault, answer.By, []);
}
