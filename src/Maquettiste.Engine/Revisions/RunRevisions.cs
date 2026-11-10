using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Revisions;

/// <summary>
/// The revisions of one simulation or replay (revisions-design.md decisions 37 to 40, section 9): the subject's revision, what its type's
/// merged block says a revise does, and the states the other lifecycles that govern it read after a revise. Created only in a project that
/// uses revisions (<see cref="RevisionCoverage.Any"/>) and only for a run whose subject's type has a block, or that has no subject object (whose
/// revises and labels then only say so in a note), so a project without a block runs exactly as before. Not thread-safe: one per run.
/// </summary>
internal sealed class RunRevisions
{
    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    private readonly ModelSnapshot _model;
    private readonly Process _process;
    private readonly ScenarioObject? _object;
    private readonly Entity? _entity;
    // The states each other lifecycle reads from the latest revise on, by lifecycle id; only those a revise changed.
    private readonly Dictionary<string, IReadOnlyList<string>> _revised = new(StringComparer.Ordinal);
    // Whether the running process is a lifecycle that governs a type with revisions, worked out for the first no-object note.
    private bool? _governsRevisions;

    private RunRevisions(ModelSnapshot model, Process process, ScenarioObject? obj, Entity? entity, MergedRevisions? merged, RevisionSequence? sequence)
    {
        _model = model;
        _process = process;
        _object = obj;
        _entity = entity;
        Merged = merged;
        Sequence = sequence;
        if (merged is null || sequence is null || entity is null || obj is null)
            return;
        var held = new Dictionary<string, ModelAttribute>(StringComparer.Ordinal);
        foreach (var attribute in ScenarioObjects.Attributes(model, entity).Concat(ExprSites.ProcessAttributesOf(model, entity)))
            held.TryAdd(attribute.Id, attribute);
        if (merged.BoundAttribute is { } boundId && held.TryGetValue(boundId, out var bound))
            Bound = (bound.Id, bound.Name);
        // The start (decision 40): the object's revision, else the bound attribute's value, else its default, else the sequence's start.
        Revision = obj.Revision
            ?? (Bound is { } b && obj.Values.TryGetValue(b.Id, out var given) && given.ValueKind == JsonValueKind.String ? given.GetString() : null)
            ?? (Bound is { } d && held[d.Id].Default is { ValueKind: JsonValueKind.String } fallback ? fallback.GetString() : null)
            ?? sequence.Start;
        Running = merged.Lifecycles.FirstOrDefault(l => string.Equals(l.Lifecycle.Id, process.Id, StringComparison.Ordinal));
        var resets = new List<(string, string, JsonElement)>();
        var notes = new List<string>();
        foreach (var rule in merged.Attributes)
        {
            if (!held.TryGetValue(rule.Attribute, out var attribute))
                continue;
            var value = rule.OnRevise == AttributeOnRevise.Reset && attribute.Default is { ValueKind: not (JsonValueKind.Undefined or JsonValueKind.Null) } byDefault
                ? byDefault.Clone() : Null;
            resets.Add((attribute.Id, attribute.Name, value));
            if (rule.OnRevise == AttributeOnRevise.Reset && attribute.Default is null && attribute.DefaultExpression is not null)
                notes.Add($"Attribute '{attribute.Name}' resets to the value of its default expression, which the team's system fills, so the run leaves it empty.");
            if (attribute.Required && value.ValueKind == JsonValueKind.Null)
                notes.Add($"Required attribute '{attribute.Name}' is empty on the new revision; the team's system gives it a value before it stores the revision.");
        }

        Resets = resets;
        ResetNotes = notes;
    }

    /// <summary>The subject type's merged block; <see langword="null"/> when the run has no subject object.</summary>
    public MergedRevisions? Merged { get; }

    /// <summary>The subject type's sequence; <see langword="null"/> when the run has no subject object.</summary>
    public RevisionSequence? Sequence { get; }

    /// <summary>Whether the run's subject has revisions: revises and labels move them. Otherwise the run has no object, and they only say so.</summary>
    public bool Active => Sequence is not null && _object is not null;

    /// <summary>The subject's current revision, which the run follows (decision 37); empty with no subject object.</summary>
    public string Revision { get; set; } = "";

    /// <summary>The id and name of the attribute that holds the revision, or <see langword="null"/>.</summary>
    public (string Id, string Name)? Bound { get; }

    /// <summary>What a revise does to the running lifecycle, or <see langword="null"/> when the running process doesn't govern the subject.</summary>
    public RevisionLifecycleRule? Running { get; }

    /// <summary>The values a revise gives the new revision: each <c>reset</c> or <c>clear</c> attribute, by id with its name, in rule order.</summary>
    public IReadOnlyList<(string Id, string Name, JsonElement Value)> Resets { get; } = [];

    /// <summary>The notes the reset values bring: a default expression the team's system fills, and an emptied required value.</summary>
    public IReadOnlyList<string> ResetNotes { get; } = [];

    /// <summary>
    /// The note a revise, or a move that revises or labels, gives in a run with no subject object (decision 38), which moves no revision. The
    /// reason depends on the process: a lifecycle that governs a type with revisions may govern several, and the run can't know which apply;
    /// an orchestration, a library, or a lifecycle that governs none has no revision to move.
    /// </summary>
    /// <param name="what">What moved no revision, such as "the move gave".</param>
    /// <returns>The note.</returns>
    public string NoObjectNote(string what)
    {
        _governsRevisions ??= _process.Use == ProcessUse.Lifecycle
            && _model.All<Entity>().Any(e => RevisionCoverage.Of(_model).For(e) is not null && SubjectCoverage.Governs(_model, _process, e));
        return _governsRevisions.Value
            ? $"The run has no object, so {what} no revision: one lifecycle may govern types with different revisions, and the run can't know which apply."
            : $"The run has no object, so {what} no revision: only an object whose type has revisions has one.";
    }

    /// <summary>The entity whose block holds the sequence, which a refusal <c>revision</c> names (decision 22).</summary>
    public string RefusedBy => (Merged!.LevelsFrom ?? Merged.Entity).Id;

    /// <summary>
    /// The revisions of a run (section 9): <see langword="null"/> in a project that uses no revisions, and for a subject object whose type has
    /// no block (a revise keeps today's handling and a move with <c>nextRevision</c> moves no revision, with no note) or a sequence that can't
    /// be built (MQ8301 says why).
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's objects and subject, as given.</param>
    /// <returns>The run's revisions, or <see langword="null"/>.</returns>
    public static RunRevisions? For(ModelSnapshot model, Process process, ScenarioCast cast)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(cast);
        var coverage = RevisionCoverage.Of(model);
        if (!coverage.Any)
            return null;
        var obj = cast.Subject is { } id ? cast.Objects.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.Ordinal)) : null;
        if (obj is null)
            return new RunRevisions(model, process, null, null, null, null);
        if (model.Get<Entity>(obj.Entity) is not { } entity || coverage.For(entity) is not { } merged
            || !RevisionSequence.TryCreate(merged.Levels, merged.Separator, out var sequence, out _))
            return null;
        return new RunRevisions(model, process, obj, entity, merged, sequence);
    }

    /// <summary>
    /// Row 7b (decision 22): the next revision at a level, or the chosen name; <see langword="null"/> when the sequence can't give it (a list at
    /// its end, a level the sequence lacks, a name that isn't later than the latest revision or isn't a value, or a current revision that isn't
    /// a value of the sequence).
    /// </summary>
    /// <param name="level">The level that moves, from 1.</param>
    /// <param name="chosen">The chosen name, or <see langword="null"/>.</param>
    /// <returns>The next revision, or <see langword="null"/>.</returns>
    public string? Next(int level, string? chosen)
    {
        if (Sequence is not { } sequence || !sequence.IsValue(Revision))
            return null;
        if (chosen is not null)
            return sequence.CheckChosen(Revision, chosen) == RevisionNameCheck.Ok ? chosen : null;
        return level >= 1 && level <= sequence.Levels.Count ? sequence.Next(Revision, level) : null;
    }

    /// <summary>The states a revise changed for another lifecycle, which every reader takes from then on; <see langword="null"/> when unchanged.</summary>
    /// <param name="lifecycleId">The lifecycle's id.</param>
    /// <returns>The active state ids, or <see langword="null"/>.</returns>
    public IReadOnlyList<string>? States(string lifecycleId) => _revised.Count == 0 ? null : _revised.GetValueOrDefault(lifecycleId);

    /// <summary>Whether a revise moved another lifecycle, so readers lay its states over the subject's given ones.</summary>
    public bool Changed => _revised.Count > 0;

    /// <summary>
    /// Moves every other lifecycle that governs the subject as its rule says (decision 33, section 9's other lifecycles): its initial
    /// configuration for <c>restart</c>, the named state's for a state, its states as before for <c>keep</c>. Says what each did, and what
    /// states the earlier revision stays in.
    /// </summary>
    /// <param name="parts">Receives what each lifecycle did, for the revise's note.</param>
    /// <param name="earlier">Receives the states the earlier revision stays in, by lifecycle, for the note.</param>
    public void ReviseOthers(List<string> parts, List<string> earlier)
    {
        foreach (var rule in Merged!.Lifecycles)
        {
            if (string.Equals(rule.Lifecycle.Id, _process.Id, StringComparison.Ordinal))
                continue;
            var chart = ProcessLibraries.Chart(_model, rule.Lifecycle);
            var before = States(rule.Lifecycle.Id) ?? ObjectStates.Of(_model, rule.Lifecycle, _entity, _object).States;
            earlier.Add($"{Leaves(chart, before)} of '{rule.Lifecycle.Name}'");
            switch (rule.OnRevise)
            {
                case LifecycleOnRevise.Keep:
                    parts.Add($"lifecycle '{rule.Lifecycle.Name}' kept its states");
                    break;
                case LifecycleOnRevise.State when rule.State is { } state:
                    _revised[rule.Lifecycle.Id] = ObjectStates.Configuration(chart, [state]);
                    parts.Add($"lifecycle '{rule.Lifecycle.Name}' went to {Leaves(chart, _revised[rule.Lifecycle.Id])}");
                    break;
                default:
                    _revised[rule.Lifecycle.Id] = ObjectStates.Configuration(chart, []);
                    parts.Add($"lifecycle '{rule.Lifecycle.Name}' started over");
                    break;
            }
        }
    }

    /// <summary>What the type's <c>links</c> say each of the subject's links would do (decision 28), for the revise's note; empty with no links.</summary>
    /// <returns>The text, or <see langword="null"/>.</returns>
    public string? LinksNote()
    {
        if (_object is null || _object.Links.Count == 0)
            return null;
        var said = new List<string>();
        foreach (var group in _object.Links.GroupBy(l => l.End, StringComparer.Ordinal))
        {
            var end = EndName(group.Key);
            var rule = Merged!.Links.FirstOrDefault(l => string.Equals(l.End, group.Key, StringComparison.Ordinal));
            said.Add(rule is null ? $"'{end}' not said" : $"'{end}' {Word(rule.OnRevise)}");
        }

        return "The run leaves the subject's links as they are; the type says its links would " + string.Join(", ", said) + ".";

        static string Word(LinkOnRevise onRevise) => onRevise switch
        {
            LinkOnRevise.Stay => "stay with the earlier revision",
            LinkOnRevise.Move => "move to the new revision",
            _ => "be duplicated on the new revision",
        };
    }

    /// <summary>The subject object as readers see it after a revise: its given states in each lifecycle a revise changed replaced (section 9).</summary>
    /// <param name="obj">The subject object, as given.</param>
    /// <returns>The object.</returns>
    public ScenarioObject Apply(ScenarioObject obj)
    {
        if (_revised.Count == 0)
            return obj;
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lifecycleId in _revised.Keys)
        {
            if (_model.Get<Process>(lifecycleId) is { } lifecycle)
                owned.UnionWith(ProcessLibraries.Chart(_model, lifecycle).ById.Keys);
        }

        return obj with { States = [.. obj.States.Where(s => !owned.Contains(s)), .. _revised.Values.SelectMany(s => s)] };
    }

    /// <summary>The run's row with its <c>$states</c> rewritten for each lifecycle a revise changed, where the row holds it (section 9).</summary>
    /// <param name="row">The row.</param>
    /// <returns>The row.</returns>
    public JsonElement Apply(JsonElement row)
    {
        if (_revised.Count == 0 || row.ValueKind != JsonValueKind.Object || !row.TryGetProperty(ObjectStates.Member, out var states)
            || states.ValueKind != JsonValueKind.Object || !_revised.Keys.Any(k => states.TryGetProperty(k, out _)))
            return row;
        var node = JsonNode.Parse(row.GetRawText())!.AsObject();
        var held = node[ObjectStates.Member]!.AsObject();
        foreach (var (id, active) in _revised)
        {
            if (held.ContainsKey(id))
                held[id] = new JsonArray([.. active.Select(s => (JsonNode?)JsonValue.Create(s))]);
        }

        return JsonSerializer.SerializeToElement(node);
    }

    /// <summary>A revision for messages: quoted, or "no revision" for <c>""</c>.</summary>
    /// <param name="revision">The revision.</param>
    /// <returns>The text.</returns>
    public static string Text(string revision) => revision.Length == 0 ? "no revision" : "'" + revision + "'";

    /// <summary>A configuration's atomic states by path, for notes.</summary>
    /// <param name="chart">The chart.</param>
    /// <param name="states">The state ids.</param>
    /// <returns>The text.</returns>
    public static string Leaves(StatechartModel chart, IEnumerable<string> states)
    {
        var leaves = states.Where(chart.ById.ContainsKey).Select(id => chart.ById[id]).Where(s => s.IsLeaf).Select(s => "'" + s.Path + "'").ToList();
        return leaves.Count == 0 ? "no state" : string.Join(" and ", leaves);
    }

    private string EndName(string endId)
    {
        if (!_model.TryGetEntry(endId, out var entry) || _model.Get<Relation>(entry.OwnerId) is not { } relation)
            return endId;
        // An end's navigation is empty when not written (never null), so its role names it then.
        var end = relation.Ends.FirstOrDefault(e => string.Equals(e.Id, endId, StringComparison.Ordinal));
        return end is null ? endId : end.Navigation.Length > 0 ? end.Navigation : !string.IsNullOrEmpty(end.Role) ? end.Role : relation.Name;
    }
}
