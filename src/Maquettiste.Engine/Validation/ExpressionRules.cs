using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// One place an element holds an expression (next-phase-3-design.md section 3.1, "Schema and files"): its pointer, its tree (or
/// <see langword="null"/>), whether it also holds its raw form, and how its scope is built.
/// </summary>
/// <param name="Pointer">The pointer of the tree's key in the element's file.</param>
/// <param name="Tree">The tree, or <see langword="null"/>.</param>
/// <param name="Raw">Whether the site holds its raw form (SQL text, a derived value's string, a guard's JavaScript).</param>
/// <param name="SubId">The check, guard or attribute that holds the site, for diagnostics.</param>
/// <param name="Kind">The site's kind.</param>
/// <param name="Fill">Adds the site's members to its scope's root.</param>
internal sealed record ExprSite(string Pointer, Expr? Tree, bool Raw, string SubId, ExprSiteKind Kind, Action<ExprScope> Fill)
{
    private readonly string? _key;

    /// <summary>
    /// The key the site is found and lowered by: its <see cref="SubId"/>, but for a trigger point's <c>when</c>, which shares the point's id with
    /// its condition (<see cref="ExprSites.WhenKey"/>).
    /// </summary>
    public string Key { get => _key ?? SubId; init => _key = value; }

    /// <summary>The type a value site must have.</summary>
    public ExprType? ValueType { get; init; }

    /// <summary>Whether the site may hold neither form (a process guard without either is a stub).</summary>
    public bool MayBeEmpty { get; init; }

    /// <summary>The id a check must read (a column check's column, an entity check's attribute), for MQ3114.</summary>
    public string? Reads { get; init; }

    /// <summary>The constraint whose condition the site is.</summary>
    public Constraint? Constraint { get; init; }

    /// <summary>The ids of payload attributes, by the events of the process that carry them, for a process guard.</summary>
    public IReadOnlyList<HashSet<string>>? Payloads { get; init; }

    /// <summary>The ids of an operation subject's members, for MQ3111.</summary>
    public HashSet<string>? Subject { get; init; }

    /// <summary>What <c>in_state</c> reads at a guard, set or right site (object-effects-design.md decisions 7 and 8).</summary>
    public IExprLifecycles? Lifecycles { get; init; }

    /// <summary>
    /// The named right whose condition a lifecycle's use types here, against the lifecycle's subjects (rights-design.md decision 7): the tree
    /// lives in the named right's file, so nothing writes it here, and a finding is reported at the use. <see langword="null"/> otherwise.
    /// </summary>
    public Right? Borrowed { get; init; }

    /// <summary>
    /// Whether the site has no scope: a named right that no lifecycle uses (rights-design.md decision 7). Its text stays text, nothing types
    /// it, and MQ3108 is its only finding.
    /// </summary>
    public bool Unscoped { get; init; }
}

/// <summary>
/// The expression sites of the model and the scope each reads (next-phase-3-design.md section 3.1): a table's columns (with those its
/// stereotypes contribute, by keyed id, and on the overlay of an entity's table the entity's attributes) or a stereotype's at its
/// checks; an entity's attributes (its bases' too, value-object members as <c>attribute.member</c>) at its checks and derived values;
/// an operation's input and its subject's attributes (under <c>subject</c>) at its guards; a process's context, the payloads of the
/// events that use a guard (under <c>event</c>) and what its subjects hold (under <c>subject</c>, <see cref="SubjectMembers"/>) at the
/// guard; a constraint's parameters at its condition.
/// </summary>
internal static class ExprSites
{
    /// <summary>The sites of an element, in document order.</summary>
    public static IReadOnlyList<ExprSite> Of(ModelSnapshot model, Element element)
    {
        var sites = new List<ExprSite>();
        switch (element)
        {
            case Table table:
                AddChecks(sites, table.Checks, s => TableColumns(s, model, table));
                break;
            case Stereotype stereotype:
                AddChecks(sites, stereotype.Checks, s => Columns(s, model, stereotype.Columns));
                break;
            case Entity entity:
                for (var i = 0; i < entity.Checks.Count; i++)
                {
                    var check = entity.Checks[i];
                    sites.Add(new ExprSite(Ptr.At("/checks", i) + "/condition", check.Condition, false, check.Id, ExprSiteKind.Check, s => Attributes(s, model, entity))
                    {
                        Reads = check.Attribute,
                    });
                }

                for (var i = 0; i < entity.Attributes.Count; i++)
                {
                    var attribute = entity.Attributes[i];
                    if (attribute.Derived is not { } derived)
                        continue;
                    sites.Add(new ExprSite(Ptr.At("/attributes", i) + "/derived/value", derived.Value, derived.Expression.Length > 0, attribute.Id, ExprSiteKind.Value,
                        s => Attributes(s, model, entity))
                    {
                        ValueType = TypeOf(model, attribute.Type),
                    });
                }

                break;
            case Operation operation:
                var subject = operation.Guards.Count > 0 ? SubjectIds(model, operation) : null;
                for (var i = 0; i < operation.Guards.Count; i++)
                {
                    var guard = operation.Guards[i];
                    sites.Add(new ExprSite(Ptr.At("/guards", i) + "/condition", guard.Condition, false, guard.Id, ExprSiteKind.Guard, s => Operation(s, model, operation))
                    {
                        Subject = subject,
                        Lifecycles = new ModelLifecycles(model, null, operation),
                    });
                }

                break;
            case Process process:
                for (var i = 0; i < process.Guards.Count; i++)
                {
                    var guard = process.Guards[i];
                    var events = process.Transitions.Where(t => t.Guard == guard.Id && t.Event is not null).Select(t => t.Event!).Distinct(StringComparer.Ordinal)
                        .Select(id => process.Events.FirstOrDefault(e => e.Id == id)).OfType<ProcessEvent>().ToList();
                    sites.Add(new ExprSite(Ptr.At("/guards", i) + "/condition", guard.Condition, !string.IsNullOrEmpty(guard.Expression), guard.Id, ExprSiteKind.Guard,
                        s => Process(s, model, process, events))
                    {
                        MayBeEmpty = true,
                        Payloads = [.. events.Select(e => e.Payload.Select(a => a.Id).ToHashSet(StringComparer.Ordinal))],
                        Lifecycles = new ModelLifecycles(model, process, null),
                    });
                }

                AddSets(sites, model, process);
                AddRights(sites, model, process);
                AddTriggerPoints(sites, model, process);
                AddApprovals(sites, model, process);
                break;
            case Constraint constraint:
                sites.Add(new ExprSite("/condition", constraint.Condition, false, constraint.Id, ExprSiteKind.Check, _ => { }) { Constraint = constraint });
                break;
            case Right right:
                sites.Add(NamedRightSite(model, right, null));
                break;
            case TriggerPoint named:
                if (named.When is { } namedWhen)
                    sites.Add(NamedTriggerSite(model, named, "/when", namedWhen, named.Id, null) with { Key = WhenKey(named.Id) });
                if (named.Condition is { } namedCondition)
                    sites.Add(NamedTriggerSite(model, named, "/condition", namedCondition, named.Id, null));
                for (var j = 0; j < named.Sets.Count; j++)
                    sites.Add(NamedTriggerSite(model, named, Ptr.At("/sets", j) + "/value", named.Sets[j].Value, named.Sets[j].Id, named.Sets[j].Attribute));
                break;
        }

        return sites;

        // A lifecycle's trigger points (trigger-points-design.md decision 15): each one's when and a check's condition at a trigger site, each
        // value it sets at a trigger set site, all in the point's scope (TriggerScope).
        static void AddTriggerPoints(List<ExprSite> sites, ModelSnapshot model, Process process)
        {
            for (var i = 0; i < process.TriggerPoints.Count; i++)
            {
                var point = process.TriggerPoints[i];
                var at = Ptr.At("/triggerPoints", i);
                if (point.When is { } when)
                    sites.Add(TriggerSite(model, process, point, at + "/when", when, point.Id, null) with { Key = WhenKey(point.Id) });
                if (point.Condition is { } condition)
                    sites.Add(TriggerSite(model, process, point, at + "/condition", condition, point.Id, null));
                for (var j = 0; j < point.Sets.Count; j++)
                    sites.Add(TriggerSite(model, process, point, Ptr.At(at + "/sets", j) + "/value", point.Sets[j].Value, point.Sets[j].Id, point.Sets[j].Attribute));
            }
        }

        // Each named signature's when of an approval on one of the process's transitions (signoff-design.md decision 7), at an approval site: the
        // process's context and its subjects under subject, and no payload, since no event is given when a round starts. A gate policy's
        // signature takes no when (MQ8202), so a library has no such site.
        static void AddApprovals(List<ExprSite> sites, ModelSnapshot model, Process process)
        {
            for (var i = 0; i < process.Transitions.Count; i++)
            {
                if (process.Transitions[i].Gate is not { Stages: { } stages })
                    continue;
                for (var j = 0; j < stages.Count; j++)
                {
                    for (var k = 0; k < stages[j].Signatures.Count; k++)
                    {
                        var signature = stages[j].Signatures[k];
                        if (signature.When is not { } when)
                            continue;
                        var at = Ptr.At(Ptr.At(Ptr.At("/transitions", i) + "/gate/stages", j) + "/signatures", k) + "/when";
                        sites.Add(new ExprSite(at, when, false, signature.Id, ExprSiteKind.Approval, s => Process(s, model, process, []))
                        {
                            Lifecycles = new ModelLifecycles(model, process, null),
                        });
                    }
                }
            }
        }

        // A lifecycle's rights (rights-design.md decisions 6 and 7): an inline right's condition reads the lifecycle's subjects; a named
        // right's is typed at each use against the lifecycle's subjects, though its tree lives in the named right's file. Only lifecycles
        // hold rights (MQ9901).
        static void AddRights(List<ExprSite> sites, ModelSnapshot model, Process process)
        {
            if (process.Use != ProcessUse.Lifecycle)
                return;
            for (var i = 0; i < process.Rights.Count; i++)
            {
                var entry = process.Rights[i];
                if (entry.Right is null)
                    sites.Add(RightSite(model, process, Ptr.At("/rights", i) + "/condition", entry.Condition, entry.Id, null));
                else if (model.Get<Right>(entry.Right) is { Condition: { } condition } named)
                    sites.Add(RightSite(model, process, Ptr.At("/rights", i) + "/right", condition, entry.Id, named));
            }
        }

        // An action's sets (object-effects-design.md decision 3): typed against the attribute's type, reading what a guard of the action's
        // transitions reads. An action that runs on entering or leaving a state reads no payload, since no one event enters or leaves it.
        static void AddSets(List<ExprSite> sites, ModelSnapshot model, Process process)
        {
            Dictionary<string, ModelAttribute>? held = null;
            for (var i = 0; i < process.Actions.Count; i++)
            {
                var action = process.Actions[i];
                if (action.Sets.Count == 0)
                    continue;
                held ??= HeldAttributes(model, process);
                var (events, payloads) = SetEvents(process, action);
                for (var j = 0; j < action.Sets.Count; j++)
                {
                    var set = action.Sets[j];
                    sites.Add(SetSite(model, process, Ptr.At(Ptr.At("/actions", i) + "/sets", j) + "/value", set.Value, set.Id, set.Attribute, held, events, payloads));
                }
            }
        }

        static void AddChecks(List<ExprSite> sites, IReadOnlyList<CheckConstraint> checks, Action<ExprScope> fill)
        {
            for (var i = 0; i < checks.Count; i++)
            {
                var check = checks[i];
                sites.Add(new ExprSite(Ptr.At("/checks", i) + "/condition", check.Condition, check.Expression.Count > 0, check.Id, ExprSiteKind.Check, fill)
                {
                    Reads = check.Column,
                });
            }
        }
    }

    /// <summary>
    /// A site for an expression written as text and not stored anywhere yet (<c>preview_expression</c> with <c>text</c> and a site
    /// kind): the scope a site of <paramref name="kind"/> on the element reads. A check at a table, a stereotype or a constraint (its
    /// parameters); a check or a value, of no required type, at an entity; a guard at an operation, or at a process, where the payload
    /// of every event reads under <c>event</c>. <see langword="null"/> when the element has no site of that kind.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="element">The element the text is written on.</param>
    /// <param name="kind">The site's kind.</param>
    /// <returns>The site, with no tree.</returns>
    public static ExprSite? Free(ModelSnapshot model, Element element, ExprSiteKind kind) => (element, kind) switch
    {
        (Table table, ExprSiteKind.Check) => new ExprSite("", null, false, table.Id, kind, s => TableColumns(s, model, table)),
        (Stereotype stereotype, ExprSiteKind.Check) => new ExprSite("", null, false, stereotype.Id, kind, s => Columns(s, model, stereotype.Columns)),
        (Entity entity, ExprSiteKind.Check or ExprSiteKind.Value) => new ExprSite("", null, false, entity.Id, kind, s => Attributes(s, model, entity)),
        (Operation operation, ExprSiteKind.Guard) => new ExprSite("", null, false, operation.Id, kind, s => Operation(s, model, operation))
        {
            Subject = SubjectIds(model, operation),
            Lifecycles = new ModelLifecycles(model, null, operation),
        },
        (Process process, ExprSiteKind.Guard) => new ExprSite("", null, false, process.Id, kind, s => Process(s, model, process, process.Events))
        {
            Lifecycles = new ModelLifecycles(model, process, null),
        },
        (Constraint constraint, ExprSiteKind.Check) => new ExprSite("/condition", null, false, constraint.Id, kind, _ => { }) { Constraint = constraint },
        (Process { Use: ProcessUse.Lifecycle } lifecycle, ExprSiteKind.Right) => RightSite(model, lifecycle, "", null, lifecycle.Id, null),
        (Right right, ExprSiteKind.Right) => NamedRightSite(model, right, null) with { Tree = null },
        _ => null,
    };

    /// <summary>Whether a process holds a named signature with a <c>when</c> on one of its transitions' gates (signoff-design.md decision 7).</summary>
    /// <param name="process">The process.</param>
    /// <returns>Whether one does.</returns>
    internal static bool ApprovalData(Process process) =>
        process.Use != ProcessUse.Library && process.Transitions.Any(t => t.Gate is { UsesStages: true } gate && gate.AllSignatures.Any(s => s.When is not null));

    /// <summary>The key a trigger point's <c>when</c> is typed and lowered by: the point's id is its condition's.</summary>
    /// <param name="pointId">The trigger point's id.</param>
    /// <returns>The key.</returns>
    internal static string WhenKey(string pointId) => pointId + "_when";

    /// <summary>The name a trigger point's site reads the values before the verb under (trigger-points-design.md decision 14).</summary>
    internal const string OldPrefix = "old";

    /// <summary>
    /// A trigger point's site (trigger-points-design.md decisions 12 to 15): a <c>when</c> or a check's condition, or with
    /// <paramref name="attribute"/> the value of one of its sets, typed against that attribute.
    /// </summary>
    internal static ExprSite TriggerSite(ModelSnapshot model, Process process, ProcessTriggerPoint point, string pointer, Expr? tree, string subId, string? attribute)
    {
        var events = TriggerEvents(process, point);
        ExprType? type = null;
        if (attribute is not null && TriggerMembers(model, process, point, withBound: true).Attributes.FirstOrDefault(a => string.Equals(a.Id, attribute, StringComparison.Ordinal))
            is { Collection: false } held && TypeOf(model, held.Type) is { IsAvailable: true } found)
            type = found;
        return new ExprSite(pointer, tree, false, subId, attribute is null ? ExprSiteKind.Trigger : ExprSiteKind.TriggerSet,
            s => TriggerScope(s, model, process, point, events))
        {
            ValueType = type,
            Payloads = events.Count > 0 ? [.. events.Select(e => e?.Payload.Select(a => a.Id).ToHashSet(StringComparer.Ordinal) ?? [])] : null,
            Lifecycles = new ModelLifecycles(model, process, null),
        };
    }

    /// <summary>
    /// A site for text written for a trigger point not stored yet, or for a new one (<c>preview_expression</c> with <c>site</c> <c>trigger</c>
    /// or <c>trigger-set</c>): the point's scope, or with no point the lifecycle's subjects and no payload. <see langword="null"/> when the
    /// lifecycle has no such trigger point.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <param name="pointId">A trigger point's id, or <see langword="null"/>.</param>
    /// <param name="attributeId">For a set's value, the attribute's id; else <see langword="null"/>.</param>
    /// <returns>The site, with no tree.</returns>
    public static ExprSite? FreeTrigger(ModelSnapshot model, Process lifecycle, string? pointId, string? attributeId)
    {
        var point = pointId is null || string.Equals(pointId, lifecycle.Id, StringComparison.Ordinal)
            ? new ProcessTriggerPoint { Id = lifecycle.Id, Name = "", Type = TriggerPointType.Check, Verbs = [] }
            : lifecycle.TriggerPoints.FirstOrDefault(t => string.Equals(t.Id, pointId, StringComparison.Ordinal));
        return point is null ? null : TriggerSite(model, lifecycle, point, "", null, point.Id, attributeId);
    }

    /// <summary>
    /// The events whose payload a trigger point may read (trigger-points-design.md decisions 4 and 13): when every verb it names is a move verb
    /// or an own verb an event of its lifecycle names, the events of its lifecycle that send one of its verbs (on its transitions, when it
    /// names some), and a <see langword="null"/> entry for a transition with no event that does one, which carries no payload. Empty
    /// otherwise: then it reads no payload (MQ3102).
    /// </summary>
    internal static IReadOnlyList<ProcessEvent?> TriggerEvents(Process process, ProcessTriggerPoint point)
    {
        if (point.Verbs.Count == 0 || !point.Verbs.All(v => IsMoveVerb(v) || (!BuiltInVerbs.IsBuiltIn(v) && process.Events.Any(e => e.Verb == v))))
            return [];
        var transitions = process.Transitions.Where(t => point.Transitions.Count == 0 || point.Transitions.Contains(t.Id, StringComparer.Ordinal)).ToList();
        var events = new List<ProcessEvent?>();
        foreach (var e in process.Events.Where(e => e.Verb is { } v && point.Verbs.Contains(v, StringComparer.Ordinal)))
        {
            if (point.Transitions.Count == 0 || transitions.Any(t => t.Event == e.Id))
                events.Add(e);
        }

        if (transitions.Any(t => t.Event is null && t.Verb is { } v && point.Verbs.Contains(v, StringComparer.Ordinal)))
            events.Add(null);
        return events;
    }

    /// <summary>Whether a verb is a move verb (trigger-points-design.md decision 4): a built-in verb of group <c>move</c>.</summary>
    /// <param name="verb">The verb's key.</param>
    /// <returns>Whether it is.</returns>
    internal static bool IsMoveVerb(string verb) => BuiltInVerbs.Get(verb)?.Group == "move";

    /// <summary>
    /// What a trigger point reads and sets on its object (trigger-points-design.md decision 12): what every subject of its lifecycle holds under
    /// one id, or with <c>entities</c> what every entity it names holds. Without <paramref name="withBound"/> the subjects' bound attributes are
    /// left out, as a guard reads the object.
    /// </summary>
    internal static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations) TriggerMembers(ModelSnapshot model, Process process,
        ProcessTriggerPoint point, bool withBound)
    {
        IEnumerable<ProcessSubject> entries = point.Entities.Count == 0 ? process.Subjects : point.Entities.Select(id => new ProcessSubject { Id = id, Entity = id });
        var bound = withBound ? new HashSet<string>(StringComparer.Ordinal) : process.Subjects.Select(s => s.BoundAttribute).OfType<string>().ToHashSet(StringComparer.Ordinal);
        return Held(model, entries, bound);
    }

    /// <summary>
    /// What a trigger point's site reads (trigger-points-design.md decision 13): the object under <c>subject</c>, the same members before the
    /// verb under <c>old</c>, and the payload of the events it may read; never the context, since a trigger point also runs outside a run.
    /// </summary>
    private static void TriggerScope(ExprScope scope, ModelSnapshot model, Process process, ProcessTriggerPoint point, IReadOnlyList<ProcessEvent?> events)
    {
        foreach (var e in events)
        {
            if (e is not null)
                AddAttributes(scope.Root, model, e.Payload, "event");
        }

        var (attributes, navigations) = TriggerMembers(model, process, point, withBound: false);
        AddAttributes(scope.Root, model, attributes, SubjectPrefix);
        AddNavigations(scope.Root, model, navigations, SubjectPrefix);
        if (scope.Old is { } old)
        {
            AddAttributes(old, model, attributes, OldPrefix);
            AddNavigations(old, model, navigations, OldPrefix);
        }
    }

    /// <summary>
    /// A named trigger point's site (trigger-points-design.md decisions 12 to 15 and 33): a <c>when</c> or a check's condition, or with
    /// <paramref name="attribute"/> the value of one of its sets. It reads what every entity it covers holds under one id, under
    /// <c>subject</c> and <c>old</c>, and no payload, since it has no lifecycle.
    /// </summary>
    internal static ExprSite NamedTriggerSite(ModelSnapshot model, TriggerPoint point, string pointer, Expr? tree, string subId, string? attribute)
    {
        ExprType? type = null;
        if (attribute is not null && NamedTriggerMembers(model, point).Attributes.FirstOrDefault(a => string.Equals(a.Id, attribute, StringComparison.Ordinal))
            is { Collection: false } held && TypeOf(model, held.Type) is { IsAvailable: true } found)
            type = found;
        return new ExprSite(pointer, tree, false, subId, attribute is null ? ExprSiteKind.Trigger : ExprSiteKind.TriggerSet, s =>
        {
            var (attributes, navigations) = NamedTriggerMembers(model, point);
            AddAttributes(s.Root, model, attributes, SubjectPrefix);
            AddNavigations(s.Root, model, navigations, SubjectPrefix);
            if (s.Old is { } old)
            {
                AddAttributes(old, model, attributes, OldPrefix);
                AddNavigations(old, model, navigations, OldPrefix);
            }
        })
        {
            ValueType = type,
            Lifecycles = new ModelLifecycles(model, null, null),
        };
    }

    /// <summary>
    /// What a named trigger point reads and sets on its object (trigger-points-design.md decisions 12 and 33): with <c>entities</c>, what every
    /// entity it names holds under one id; with <c>exceptEntities</c>, what every entity it covers holds; otherwise what its subjects hold, as a
    /// lifecycle's subjects do.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="point">The named trigger point.</param>
    /// <returns>The attributes and the navigations.</returns>
    internal static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations) NamedTriggerMembers(ModelSnapshot model, TriggerPoint point)
    {
        var covered = point.Entities.Count == 0 && point.ExceptEntities.Count > 0 ? Processes.NamedTriggerPoints.Of(model).CoveredEntities(point) : [];
        IEnumerable<ProcessSubject> entries = point.Entities.Count > 0 ? point.Entities.Select(id => new ProcessSubject { Id = id, Entity = id })
            : covered.Count > 0 ? covered.Select(e => new ProcessSubject { Id = e.Id, Entity = e.Id })
            : point.Subjects.Select((s, i) => new ProcessSubject { Id = point.Id + "/" + i, Entity = s.Entity, Stereotype = s.Stereotype });
        return Held(model, entries, new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>Whether a lifecycle's trigger points hold an expression or a set (trigger-points-design.md decision 3).</summary>
    /// <param name="process">The process.</param>
    /// <returns>Whether one does.</returns>
    internal static bool TriggerData(Process process) =>
        process.Use == ProcessUse.Lifecycle && process.TriggerPoints.Any(t => t.When is not null || t.Condition is not null || t.Sets.Count > 0);

    /// <summary>
    /// The site of a named right's condition (rights-design.md decision 7). Its scope is what every subject of every lifecycle that uses the
    /// right holds under one id, or with <paramref name="lifecycle"/> what that lifecycle's subjects hold, as if the right were used there.
    /// With no use and no lifecycle the site has no scope (<see cref="ExprSite.Unscoped"/>).
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="right">The named right.</param>
    /// <param name="lifecycle">A lifecycle to read the condition in, or <see langword="null"/> for the right's uses.</param>
    /// <returns>The site, with the right's condition.</returns>
    public static ExprSite NamedRightSite(ModelSnapshot model, Right right, Process? lifecycle)
    {
        IReadOnlyList<Process> uses = lifecycle is not null ? [lifecycle] : RightUses(model, right);
        return new ExprSite("/condition", right.Condition, false, right.Id, ExprSiteKind.Right, s => RightScope(s, model, uses))
        {
            MayBeEmpty = true,
            Unscoped = uses.Count == 0,
            Lifecycles = uses.Count == 0 ? null : new ModelLifecycles(model, uses.Count == 1 ? uses[0] : null, null) { ForRights = true, Uses = uses },
        };
    }

    /// <summary>The lifecycles that use a named right (an entry of their rights names it), by id; a process that is no lifecycle is left out.</summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="right">The named right.</param>
    /// <returns>The lifecycles.</returns>
    internal static IReadOnlyList<Process> RightUses(ModelSnapshot model, Right right) =>
        [.. model.ReferencesTo(right.Id).Select(r => model.Get<Process>(r.FromElementId)).OfType<Process>()
            .Where(p => p.Use == ProcessUse.Lifecycle && p.Rights.Any(e => string.Equals(e.Right, right.Id, StringComparison.Ordinal)))
            .DistinctBy(p => p.Id).OrderBy(p => p.Id, StringComparer.Ordinal)];

    private static ExprSite RightSite(ModelSnapshot model, Process lifecycle, string pointer, Expr? tree, string subId, Right? borrowed) =>
        new(pointer, tree, false, subId, ExprSiteKind.Right, s => RightScope(s, model, [lifecycle]))
        {
            MayBeEmpty = true,
            Lifecycles = new ModelLifecycles(model, lifecycle, null) { ForRights = true },
            Borrowed = borrowed,
        };

    /// <summary>
    /// What a right's condition reads (rights-design.md decisions 6 and 7): the object under <c>subject</c>, what every subject of every one
    /// of the lifecycles holds under one id (<see cref="RightMembers"/>), and nothing of the context or a payload.
    /// </summary>
    private static void RightScope(ExprScope scope, ModelSnapshot model, IReadOnlyList<Process> lifecycles)
    {
        List<ModelAttribute>? attributes = null;
        List<RelationEnd>? navigations = null;
        foreach (var lifecycle in lifecycles)
        {
            var (held, reached) = RightMembers(model, lifecycle);
            if (attributes is null || navigations is null)
            {
                attributes = [.. held];
                navigations = [.. reached];
                continue;
            }

            var heldIds = held.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            attributes.RemoveAll(a => !heldIds.Contains(a.Id));
            var reachedIds = reached.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            navigations.RemoveAll(e => !reachedIds.Contains(e.Id));
        }

        AddAttributes(scope.Root, model, attributes ?? [], SubjectPrefix);
        AddNavigations(scope.Root, model, navigations ?? [], SubjectPrefix);
    }

    /// <summary>
    /// What a right of a lifecycle reads on its object (rights-design.md decision 6): what every subject entry holds under one id, as a guard
    /// reads it under <c>subject</c>, less the subjects' bound attributes; a context attribute named <c>subject</c> hides nothing, since a
    /// right reads no context.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The attributes and the relation ends navigated to.</returns>
    internal static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations) RightMembers(ModelSnapshot model, Process lifecycle) =>
        Held(model, lifecycle, lifecycle.Subjects.Select(s => s.BoundAttribute).OfType<string>().ToHashSet(StringComparer.Ordinal));

    /// <summary>
    /// The attributes a right of a lifecycle may name in <c>attributes</c> (rights-design.md decision 21; MQ9903): every attribute each
    /// subject entry holds under one id, its bound attributes included, by id.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="lifecycle">The lifecycle.</param>
    /// <returns>The attribute ids.</returns>
    internal static HashSet<string> RightAttributes(ModelSnapshot model, Process lifecycle) =>
        Held(model, lifecycle, new HashSet<string>(StringComparer.Ordinal)).Attributes.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// A site for a value text writes for a set not stored yet (<c>preview_expression</c> with <c>site</c> <c>set</c>): the scope a set of
    /// the attribute on the action reads (object-effects-design.md section 8). <see langword="null"/> when the process has no such action.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="process">The process.</param>
    /// <param name="actionId">The action's id.</param>
    /// <param name="attributeId">The attribute's id.</param>
    /// <returns>The site, with no tree.</returns>
    public static ExprSite? FreeSet(ModelSnapshot model, Process process, string actionId, string attributeId)
    {
        if (process.Actions.FirstOrDefault(a => string.Equals(a.Id, actionId, StringComparison.Ordinal)) is not { } action)
            return null;
        var (events, payloads) = SetEvents(process, action);
        return SetSite(model, process, "", null, action.Id, attributeId, HeldAttributes(model, process), events, payloads);
    }

    private static ExprSite SetSite(ModelSnapshot model, Process process, string pointer, Expr? tree, string subId, string attributeId,
        Dictionary<string, ModelAttribute> held, IReadOnlyList<ProcessEvent> events, IReadOnlyList<HashSet<string>> payloads)
    {
        var type = held.TryGetValue(attributeId, out var attribute) && !attribute.Collection ? TypeOf(model, attribute.Type) : null;
        return new ExprSite(pointer, tree, false, subId, ExprSiteKind.Set, s => Process(s, model, process, events))
        {
            ValueType = type is { IsAvailable: true } ? type : null,
            Payloads = payloads,
            Lifecycles = new ModelLifecycles(model, process, null),
        };
    }

    /// <summary>
    /// The events whose transitions run an action, whose payloads a set's value reads, and the payloads a read is checked against: one per
    /// event, and an empty one when the action also runs on entering or leaving a state (MQ3102).
    /// </summary>
    private static (IReadOnlyList<ProcessEvent> Events, IReadOnlyList<HashSet<string>> Payloads) SetEvents(Process process, ProcessAction action)
    {
        var events = process.Transitions.Where(t => t.Actions.Contains(action.Id, StringComparer.Ordinal) && t.Event is not null).Select(t => t.Event!)
            .Distinct(StringComparer.Ordinal).Select(id => process.Events.FirstOrDefault(e => e.Id == id)).OfType<ProcessEvent>().ToList();
        var payloads = events.Select(e => e.Payload.Select(a => a.Id).ToHashSet(StringComparer.Ordinal)).ToList();
        if (RunsOnStates(process.States, action.Id))
        {
            payloads.Add(new HashSet<string>(StringComparer.Ordinal));
            return ([], payloads);
        }

        return (events, payloads);

        static bool RunsOnStates(IReadOnlyList<ProcessState> states, string id) =>
            states.Any(s => s.Entry.Contains(id, StringComparer.Ordinal) || s.Exit.Contains(id, StringComparer.Ordinal) || RunsOnStates(s.States, id));
    }

    /// <summary>
    /// A table's columns: its own and those its stereotypes contribute, by their keyed ids. On the overlay of an entity's table the
    /// entity's attributes and value object members come first, named as an entity check names them and held under the keys the
    /// resolver gives their columns (<c>attributeId</c>, <c>attributeId.memberId</c>): validation cannot know the physical names the
    /// naming conventions decide. An override entry of one of them reads as that member under the entry's id too, typed as the entry
    /// makes its column (<see cref="Overridden"/>), and its name, when it gives one, is another name text may write for the member, unless
    /// a member or column already has it; foreign key columns are not attributes, so only an entry that names one reads it.
    /// </summary>
    private static void TableColumns(ExprScope scope, ModelSnapshot model, Table table)
    {
        var entity = OverlaidEntity(model, table);
        if (entity is not null)
        {
            Dictionary<string, Column>? entries = null;
            foreach (var column in table.Columns)
            {
                if (column.Attribute is { } key)
                    (entries ??= new(StringComparer.Ordinal)).TryAdd(key, column);
            }

            AddAttributes(scope.Root, model, AttributesOf(model, entity), null,
                entries is null ? null : m => entries.TryGetValue(m.Id, out var entry) ? Overridden(m, entry) : m);
            foreach (var column in table.Columns)
            {
                if (column.Attribute is { } key && scope.Root.Member(key) is { } overridden)
                    scope.Root.Add(overridden with { Id = column.Id });
            }
        }

        Columns(scope, model, table.Columns);
        Columns(scope, model, [.. TableMembers.Of(model, table).Columns.Select(m => m.Member)]);
        if (entity is null)
            return;
        foreach (var column in table.Columns)
        {
            if (column is { Attribute: { } key, Name.Length: > 0 })
                scope.Root.Alias(key, column.Name);
        }
    }

    /// <summary>
    /// An attribute or value object member as an override entry makes its column: of the entry's <c>type</c> when it sets one (an enum
    /// or a code keeps its type, since the entry sets only how its column stores it), nullable as the entry says, fixed-length when the
    /// entry makes it so, with the entry's scale; as the resolver applies the entry. A <c>typeRef</c> on such an entry is MQ4082, and the
    /// resolver ignores it.
    /// </summary>
    private static ExprMember Overridden(ExprMember member, Column entry) => member with
    {
        Type = entry.Type is { } keyword && member.Type.Kind is not ("enum" or "code" or "value-object") ? ExprType.FromBuiltin(keyword) : member.Type,
        Nullable = entry.Nullable ?? member.Nullable,
        FixedLength = member.FixedLength || entry.FixedLength || (entry.NativeType?.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ?? false),
        Scale = entry.Scale ?? member.Scale,
    };

    /// <summary>The entity whose own table an overlay overrides (not one of its child tables), or <see langword="null"/>.</summary>
    internal static Entity? OverlaidEntity(ModelSnapshot model, Table table) =>
        table is { Origin: TableOrigin.Synthesized, Entity: { } id, Attribute: null } ? model.Get<Entity>(id) : null;

    /// <summary>
    /// The columns of an overlay of an entity's table (its own, and those its stereotypes contribute) whose name an attribute of the
    /// entity has in a check's scope, each with that attribute: text that writes the name reads the attribute, so it cannot name the
    /// column, and a check that reads the column prints as one that reads the attribute (MQ3102 where a check reads either). Empty on
    /// any other table.
    /// </summary>
    internal static IReadOnlyList<(ExprMember Attribute, Column Column)> Shadowed(ModelSnapshot model, Table table, ExprScope scope)
    {
        if (OverlaidEntity(model, table) is null)
            return [];
        List<(ExprMember, Column)>? shadowed = null;
        List<Column> columns = [.. table.Columns, .. TableMembers.Of(model, table).Columns.Select(m => m.Member)];
        var ids = columns.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            // An entry of an attribute reads as the attribute; any other column whose name a member that is no column has is shadowed.
            if (column.Name.Length == 0 || (column.Attribute is { } key && scope.Root.Member(key) is not null))
                continue;
            if (scope.Root.Find([column.Name]) is ExprMember named && !string.Equals(named.Id, column.Id, StringComparison.Ordinal) && !ids.Contains(named.Id))
                (shadowed ??= []).Add((named, column));
        }

        return shadowed ?? [];
    }

    /// <summary>The ids of the members an operation's subject offers a guard (its attributes and navigations), for MQ3111; none without one.</summary>
    private static HashSet<string>? SubjectIds(ModelSnapshot model, Operation operation) =>
        operation.Subject is { } subjectId && model.Get<Entity>(subjectId) is { } subject
            ? AttributesOf(model, subject).Select(a => a.Id).Concat(NavigationsOf(model, subject).Select(n => n.To.Id)).ToHashSet(StringComparer.Ordinal)
            : null;

    /// <summary>
    /// The scope of a site: its members, then the enums, reference types and constraints its tree names by id. When the tree, or a
    /// constraint it cites, holds text, or with <paramref name="text"/> (text is about to be parsed there), the names the text writes are
    /// looked up on first use (<see cref="ModelNames"/>): a parse reads only the enums, reference types, seeds and constraints it names,
    /// never every one of the model, which may hold hundreds of lookup types.
    /// </summary>
    public static ExprScope Scope(ModelSnapshot model, Element element, ElementDocument? document, ExprSite site, bool text)
    {
        var constraints = new Dictionary<string, Constraint>(StringComparer.Ordinal);
        var refs = new HashSet<string>(StringComparer.Ordinal);
        if (site.Constraint is { } own)
            constraints[own.Id] = own;
        var pending = new Stack<Expr>();
        if (site.Tree is { } tree)
            pending.Push(tree);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            text |= e.Text is not null;
            if (e.Ref is { } r)
                refs.Add(r);
            if (e.Satisfies is { } cited && !constraints.ContainsKey(cited) && model.Get<Constraint>(cited) is { } found)
            {
                constraints[cited] = found;
                pending.Push(found.Condition);
            }

            foreach (var child in Children(e))
                pending.Push(child);
        }

        var scope = new ExprScope(site.Kind)
        {
            ValueType = site.ValueType, ElementId = site.SubId, FilePath = document?.Path, Pointer = site.Pointer,
            Names = text ? new ModelNames(model) : null,
            Lifecycles = site.Lifecycles,
        };
        site.Fill(scope);
        AddRefs(scope, model, refs);
        foreach (var c in constraints.Values.OrderBy(c => c.Id, StringComparer.Ordinal))
            scope.AddConstraint(ToExpr(model, c));
        return site.Constraint is { } constraint ? scope.ForConstraint(scope.Constraint(constraint.Id)!) : scope;
    }

    /// <summary>The enums and reference types that hold the members and codes of some refs, each once.</summary>
    private static void AddRefs(ExprScope scope, ModelSnapshot model, IEnumerable<string> refs)
    {
        var sets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in refs)
        {
            if (!model.TryGetEntry(r, out var entry) || !sets.Add(entry.OwnerId))
                continue;
            if (model.Get<EnumType>(entry.OwnerId) is { } e)
                AddEnum(scope, e);
            else if (model.Get<Seed>(entry.OwnerId) is { } seed && model.Get<ReferenceType>(seed.Target) is { } type && sets.Add(type.Id))
                AddCodes(scope, model, type);
        }
    }

    /// <summary>
    /// The enums, reference types and constraints text names, found by name in the model's name indexes when a parse first asks: the
    /// enum of that name with the smallest id, else the reference type, and the constraint with the smallest id, as a scope that held every
    /// one of them would resolve the name; a constraint comes with what its condition names by id.
    /// </summary>
    private sealed class ModelNames(ModelSnapshot model) : IExprNames
    {
        public string? Set(ExprScope scope, string name)
        {
            if (Named<EnumType>(model, "enum", name) is { } e)
            {
                AddEnum(scope, e);
                return e.Id;
            }

            if (Named<ReferenceType>(model, "reference-type", name) is { } type)
            {
                AddCodes(scope, model, type);
                return type.Id;
            }

            return null;
        }

        public string? Constraint(ExprScope scope, string name)
        {
            if (Named<Constraint>(model, "constraint", name) is not { } found)
                return null;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            var added = new SortedDictionary<string, Constraint>(StringComparer.Ordinal);
            var pending = new Stack<Constraint>([found]);
            while (pending.Count > 0)
            {
                var c = pending.Pop();
                if (scope.Constraint(c.Id) is not null || !added.TryAdd(c.Id, c))
                    continue;
                foreach (var node in Nodes(c.Condition))
                {
                    if (node.Ref is { } r)
                        refs.Add(r);
                    if (node.Satisfies is { } cited && model.Get<Constraint>(cited) is { } next)
                        pending.Push(next);
                }
            }

            AddRefs(scope, model, refs);
            foreach (var c in added.Values)
                scope.AddConstraint(ToExpr(model, c));
            return found.Id;
        }

        /// <summary>The element of a kind with this exact name and the smallest id, through the kind's name index.</summary>
        internal static T? Named<T>(ModelSnapshot model, string kind, string name) where T : Element
        {
            T? named = null;
            foreach (var entry in model.Names(kind).Find(QualifiedName.Create(kind, [name]).Path))
            {
                if (model.Get<T>(entry.Id) is { } e && string.Equals(e.Name, name, StringComparison.Ordinal)
                    && (named is null || string.CompareOrdinal(e.Id, named.Id) < 0))
                    named = e;
            }

            return named;
        }
    }

    /// <summary>
    /// What <c>in_state</c> reads at a process's guard or set, or at an operation's guard (object-effects-design.md decisions 7 and 8): the
    /// states of the model's processes by id and by name, built once per process and model, and whether a lifecycle can govern the
    /// object a path reaches. Nothing is read until an <c>in_state</c> asks.
    /// </summary>
    private sealed class ModelLifecycles(ModelSnapshot model, Process? process, Operation? operation) : IExprLifecycles
    {
        private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<Process, Dictionary<string, ExprState>>> States = new();

        public string? Own => process?.Id;

        /// <summary>Whether the site is a right's condition, which reads no context, so a context attribute named <c>subject</c> hides nothing.</summary>
        public bool ForRights { get; init; }

        /// <summary>A named right's uses, read together when no one process is given: a lifecycle must be able to govern the object at each.</summary>
        public IReadOnlyList<Process>? Uses { get; init; }

        public ExprState? State(string id) =>
            model.TryGetEntry(id, out var entry) && entry.Kind == "state" && model.Get<Process>(entry.OwnerId) is { } owner
                ? StatesOf(owner).GetValueOrDefault(id)
                : null;

        public string? Find(IReadOnlyList<string> segments)
        {
            if (segments.Count < 2 || ModelNames.Named<Process>(model, "process", segments[0]) is not { } lifecycle)
                return null;
            var level = lifecycle.States;
            ProcessState? found = null;
            foreach (var name in segments.Skip(1))
            {
                found = level.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
                if (found is null)
                    return null;
                level = found.States;
            }

            return found?.Id;
        }

        public string? CannotGovern(string lifecycle, IReadOnlyList<ExprNavigation> through)
        {
            if (model.Get<Process>(lifecycle) is not { } governing)
                return "in_state names a state of '" + lifecycle + "', which is no process of the model.";
            if (through.Count > 0)
            {
                if (through[^1].Entity is not { } id || model.Get<Entity>(id) is not { } reached)
                    return "in_state's object reaches no entity of the model.";
                return CanGovern(governing, reached) ? null
                    : "Lifecycle " + governing.Name + " cannot govern " + reached.Name + ", the object in_state reaches: neither it, a base, a stereotype it carries "
                        + "nor an entity derived from it is one of the lifecycle's subjects.";
            }

            if (operation is not null)
            {
                return operation.Subject is { } subject && model.Get<Entity>(subject) is { } entity
                    ? CanGovern(governing, entity) ? null : "Lifecycle " + governing.Name + " cannot govern " + entity.Name + ", the operation's subject."
                    : "The operation is not on one record, so in_state has no object to read; read a lifecycle through a guard of a process.";
            }

            if (process is null && Uses is { Count: > 0 } uses)
            {
                foreach (var use in uses)
                {
                    if (!string.Equals(use.Id, lifecycle, StringComparison.Ordinal)
                        && new ModelLifecycles(model, use, null) { ForRights = true }.CannotGovern(lifecycle, through) is { } why)
                        return why;
                }

                return null;
            }

            if (process is null)
                return "in_state has no object to read here.";
            if (!ForRights && process.Context.Any(a => string.Equals(a.Name, SubjectPrefix, StringComparison.Ordinal)))
                return "A context attribute named subject hides the object, so in_state reads no object; rename the context attribute.";
            if (process.Subjects.Count == 0)
                return "The process has no subjects, so in_state reads no object; give the process a subject, or name a state of the process's own lifecycle.";
            foreach (var entry in process.Subjects)
            {
                if (entry.Entity is { } entityId && model.Get<Entity>(entityId) is { } entity && CanGovern(governing, entity))
                    return null;
                if (entry.Stereotype is { } key && (governing.Subjects.Any(g => string.Equals(g.Stereotype, key, StringComparison.Ordinal))
                    || Processes.SubjectCoverage.Of(model).CarriersOf(key).Any(c => CanGovern(governing, c))))
                    return null;
            }

            return "Lifecycle " + governing.Name + " cannot govern the process's subjects: neither they, a base, a stereotype they carry nor an entity "
                + "derived from them is one of its subjects.";
        }

        /// <summary>Whether a lifecycle can govern an entity's objects: it governs the entity or one derived from it (decision 8).</summary>
        private bool CanGovern(Process lifecycle, Entity entity)
        {
            if (Processes.SubjectCoverage.Governs(model, lifecycle, entity))
                return true;
            foreach (var other in model.All<Entity>())
            {
                if (!ReferenceEquals(other, entity) && Processes.SubjectCoverage.Chain(model, other).Contains(entity.Id) && Processes.SubjectCoverage.Governs(model, lifecycle, other))
                    return true;
            }

            return false;
        }

        private Dictionary<string, ExprState> StatesOf(Process owner) => States.GetValue(model, static _ => new()).GetValue(owner, p =>
        {
            var states = new Dictionary<string, ExprState>(StringComparer.Ordinal);
            Walk(p, p.States, [], states);
            return states;
        });

        // Each state with its path and the states inside it, in document order; a history or choice state is no state an object is in.
        private static List<string> Walk(Process p, IReadOnlyList<ProcessState> level, List<string> path, Dictionary<string, ExprState> states)
        {
            var all = new List<string>();
            foreach (var state in level)
            {
                var own = new List<string>(path) { state.Name };
                var isState = state.Type is not (StateType.History or StateType.Choice);
                var inside = isState ? new List<string> { state.Id } : [];
                inside.AddRange(Walk(p, state.States, own, states));
                states[state.Id] = new ExprState(state.Id, p.Id, p.Name, own, isState, inside);
                all.AddRange(inside);
            }

            return all;
        }
    }

    /// <summary>A constraint as the typer reads it.</summary>
    public static ExprConstraint ToExpr(ModelSnapshot model, Constraint c) =>
        new(c.Id, c.Name, [.. c.Parameters.Select(p => new ExprMember(p.Id, p.Name, TypeOf(model, p.Type), true))], c.Condition);

    /// <summary>The children of a node.</summary>
    public static IEnumerable<Expr> Children(Expr e)
    {
        foreach (var arg in e.Args ?? [])
            yield return arg;
        foreach (var when in e.Case ?? [])
        {
            yield return when.When;
            yield return when.Then;
        }

        if (e.Else is { } otherwise)
            yield return otherwise;
        foreach (var argument in e.With ?? [])
            yield return argument.Value;
    }

    /// <summary>Every node of a tree, depth first.</summary>
    public static IEnumerable<Expr> Nodes(Expr e)
    {
        yield return e;
        foreach (var child in Children(e))
        {
            foreach (var node in Nodes(child))
                yield return node;
        }
    }

    /// <summary>The ids each path of a tree names, every segment.</summary>
    public static IEnumerable<string> PathIds(Expr e) => Nodes(e).SelectMany(n => n.Path ?? []);

    /// <summary>
    /// The paths of a tree, each its ids: as the tree holds them, or, when it holds text, whose paths exist only once it is parsed, as its
    /// typed form holds them (none without one). A cited constraint's own condition is not walked: its paths name its parameters.
    /// </summary>
    /// <param name="tree">The tree as stored.</param>
    /// <param name="typed">The tree typed, or <see langword="null"/> when it does not type.</param>
    /// <returns>The paths.</returns>
    public static IEnumerable<IReadOnlyList<string>> PathsOf(Expr tree, TypedExpr? typed)
    {
        if (!HasText(tree))
            return Nodes(tree).Select(n => n.Path).OfType<IReadOnlyList<string>>();
        return typed is null ? [] : Typed(typed);

        static IEnumerable<IReadOnlyList<string>> Typed(TypedExpr t) =>
            (t.Node.Path is { } path ? [path] : Enumerable.Empty<IReadOnlyList<string>>()).Concat(t.Args.SelectMany(Typed));
    }

    /// <summary>
    /// The context attribute named <c>event</c> that a process guard's typed condition reads, or <see langword="null"/>. A condition's
    /// row holds the event's payload under <c>event</c>, so such a read would see the payload: validation reports it (MQ3102), and
    /// resolution and simulation leave the guard's condition out, so the guard is a stub rather than one that misreads.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="typed">The guard's typed condition.</param>
    /// <returns>The attribute, or <see langword="null"/>.</returns>
    public static ModelAttribute? EventContextRead(Process process, TypedExpr typed)
    {
        foreach (var attribute in process.Context)
        {
            if (!string.Equals(attribute.Name, "event", StringComparison.Ordinal))
                continue;
            var member = attribute.Id + ".";
            if (ExprResolution.Paths(typed).Any(id => string.Equals(id, attribute.Id, StringComparison.Ordinal) || id.StartsWith(member, StringComparison.Ordinal)))
                return attribute;
        }

        return null;
    }

    /// <summary>Whether a tree holds a <c>text</c> node anywhere.</summary>
    public static bool HasText(Expr? e) => e is not null && Nodes(e).Any(n => n.Text is not null);

    /// <summary>
    /// A tree with each text node, at its root or inside it, parsed in the scope: what a write and <c>text-to-trees</c> store, and what
    /// <c>materialize-tables</c> moves into a designed table. A text that does not parse stays text, and its problem (MQ3101, or the
    /// parser's own) goes to <paramref name="problems"/>.
    /// </summary>
    public static Expr ParseTexts(Expr e, ExprScope scope, List<Diagnostic> problems)
    {
        if (e.Text is { } text)
        {
            var parsed = ExprText.Parse(text, scope);
            if (parsed.Tree is { } tree)
                return tree;
            problems.Add(parsed.Problem ?? RuleCatalog.Create("MQ3101", "The expression '" + text + "' does not parse.", scope.ElementId, scope.FilePath, scope.Pointer));
            return e;
        }

        return e with
        {
            Args = e.Args is { } args ? [.. args.Select(a => ParseTexts(a, scope, problems))] : null,
            Case = e.Case is { } branches ? [.. branches.Select(w => new ExprWhen { When = ParseTexts(w.When, scope, problems), Then = ParseTexts(w.Then, scope, problems) })] : null,
            Else = e.Else is { } otherwise ? ParseTexts(otherwise, scope, problems) : null,
            With = e.With is { } with ? [.. with.Select(a => a with { Value = ParseTexts(a.Value, scope, problems) })] : null,
        };
    }

    /// <summary>The expression type of a type reference: a built-in, an enum, a reference type's code, a scalar type's base.</summary>
    public static ExprType TypeOf(ModelSnapshot model, TypeRef? type)
    {
        if (type?.Builtin is { } builtin)
            return ExprType.FromBuiltin(builtin);
        if (type?.Ref is not { } id)
            return new ExprType("unknown", null);
        if (model.Get<EnumType>(id) is not null)
            return ExprType.Enum(id);
        if (model.Get<ReferenceType>(id) is not null)
            return ExprType.Code(id);
        if (model.Get<ScalarType>(id) is { } scalar)
            return ExprType.FromBuiltin(scalar.Base);
        return new ExprType("value-object", id);
    }

    private static void AddEnum(ExprScope scope, EnumType e) => scope.AddEnum(e.Id, e.Name, e.Members.Select(m => (m.Id, m.Name)));

    /// <summary>
    /// A reference type's codes: the code cell of each row of each seed of the type (its <c>code</c> column), the seeds in (name, id)
    /// order, found through the references to the type rather than among every seed.
    /// </summary>
    private static void AddCodes(ExprScope scope, ModelSnapshot model, ReferenceType type)
    {
        var codes = new List<(string Id, string Code)>();
        var seeds = model.ReferencesTo(type.Id).Select(r => model.Get<Seed>(r.FromElementId)).OfType<Seed>()
            .Where(s => string.Equals(s.Target, type.Id, StringComparison.Ordinal)).DistinctBy(s => s.Id)
            .OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal);
        foreach (var seed in seeds)
        {
            var at = seed.Columns.ToList().IndexOf("code");
            if (at < 0)
                continue;
            foreach (var row in seed.Rows)
            {
                if (at < row.Values.Count && row.Values[at] is { ValueKind: JsonValueKind.String or JsonValueKind.Number } code)
                    codes.Add((row.Id, code.ValueKind == JsonValueKind.String ? code.GetString()! : code.GetRawText()));
            }
        }

        scope.AddReferenceType(type.Id, type.Name, codes);
    }

    /// <summary>A table's or stereotype's columns; one that states no type and whose attribute gives none is unknown (MQ3103 where read).</summary>
    private static void Columns(ExprScope scope, ModelSnapshot model, IReadOnlyList<Column> columns)
    {
        foreach (var column in columns)
        {
            var type = column.TypeRef is { } typeRef ? TypeOf(model, typeRef)
                : column.Type is { } keyword ? ExprType.FromBuiltin(keyword)
                : column.Attribute is { } attribute && AttributeOf(model, attribute) is { } a ? TypeOf(model, a.Type)
                : new ExprType("unknown", null);
            var fixedLength = column.FixedLength || (column.NativeType?.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ?? false);
            scope.Root.Add(new ExprMember(column.Id, column.Name, type, column.Nullable ?? true) { FixedLength = fixedLength, Scale = column.Scale });
        }
    }

    private static ModelAttribute? AttributeOf(ModelSnapshot model, string keyed)
    {
        var id = keyed.Split('.', '@')[^1];
        return model.TryGetEntry(id, out var entry) && model.Get<Entity>(entry.OwnerId) is { } owner ? owner.Attributes.FirstOrDefault(a => a.Id == id) : null;
    }

    /// <summary>An entity's attributes and its bases', nearest first.</summary>
    public static IEnumerable<ModelAttribute> AttributesOf(ModelSnapshot model, Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && seen.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
            foreach (var attribute in current.Attributes)
                yield return attribute;
        }
    }

    /// <summary>
    /// The attributes a process reads on an entity it governs (stereotype-sets-design.md decision 7): its own and inherited attributes
    /// (<see cref="AttributesOf"/>), then the attributes of the stereotypes in the effective sets of the entity and its bases, nearest entity
    /// first, each set in its order; each attribute once. Operation guards and entity checks keep <see cref="AttributesOf"/>.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="entity">The entity.</param>
    /// <returns>The attributes.</returns>
    internal static IEnumerable<ModelAttribute> ProcessAttributesOf(ModelSnapshot model, Entity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var attribute in AttributesOf(model, entity))
        {
            if (seen.Add(attribute.Id))
                yield return attribute;
        }

        var levels = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && levels.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
            foreach (var applied in model.EffectiveStereotypes(current))
            {
                foreach (var attribute in applied.Stereotype.Attributes)
                {
                    if (seen.Add(attribute.Id))
                        yield return attribute;
                }
            }
        }
    }

    /// <summary>
    /// What one subject entry offers a process under its ids (stereotype-sets-design.md decision 7): an entity entry, the attributes of
    /// <see cref="ProcessAttributesOf"/> and the entity's navigations; a stereotype entry, the stereotype's own attributes and no
    /// navigations. <see langword="null"/> when the entry's entity or stereotype is not in the model.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="entry">The entry.</param>
    /// <returns>The attributes and the relation ends navigated to, or <see langword="null"/>.</returns>
    internal static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations)? EntryMembers(ModelSnapshot model, ProcessSubject entry)
    {
        if (entry.Stereotype is { } key)
            return model.GetStereotype(key) is { } stereotype ? (stereotype.Attributes, []) : null;
        if (entry.Entity is not { } entityId || model.Get<Entity>(entityId) is not { } entity)
            return null;
        return (ProcessAttributesOf(model, entity).ToList(), NavigationsOf(model, entity).Select(n => n.To).ToList());
    }

    /// <summary>Whether an attribute id names an attribute a stereotype declares.</summary>
    /// <param name="model">The model.</param>
    /// <param name="attributeId">The attribute id.</param>
    /// <returns>Whether a stereotype declares it.</returns>
    internal static bool IsStereotypeAttribute(ModelSnapshot model, string attributeId) =>
        model.TryGetEntry(attributeId, out var entry) && entry.Kind == "attribute" && model.Get<Stereotype>(entry.OwnerId) is not null;

    private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<Process, IReadOnlySet<string>>> StereotypeReads = new();

    /// <summary>
    /// The ids of the stereotype attributes a process's guard conditions, and a lifecycle's trigger point conditions, <c>when</c> and set
    /// values, read on the object itself (under <c>subject</c>, through no navigation; stereotype-sets-design.md decision 7), and those
    /// their sets write, typed once per model; empty when none does. A scenario's subject row and the subject's defaults gain them only
    /// then, so a run's answers stay the same otherwise.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The attribute ids.</returns>
    internal static IReadOnlySet<string> StereotypeAttributesRead(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        if ((process.Guards.All(g => g.Condition is null) && !SetsData(process) && !TriggerPointsRead(process))
            || process.Context.Any(a => string.Equals(a.Name, SubjectPrefix, StringComparison.Ordinal)))
            return FrozenSet<string>.Empty;
        return StereotypeReads.GetValue(model, static _ => new()).GetValue(process, p =>
        {
            HashSet<string>? read = null;
            // A set writes its attribute into the object's row, so a stereotype's attribute it sets joins the row as one read does; a
            // trigger point's set too (trigger-points-design.md decision 25).
            var pointSets = p.Use != ProcessUse.Lifecycle ? [] : p.TriggerPoints.SelectMany(t => t.Sets);
            foreach (var set in p.Actions.SelectMany(a => a.Sets).Concat(pointSets))
            {
                if (IsStereotypeAttribute(model, set.Attribute))
                    (read ??= new HashSet<string>(StringComparer.Ordinal)).Add(set.Attribute);
            }

            foreach (var siteId in ConditionIds(p))
            {
                if (ExprResolution.Type(model, p, siteId) is not { } typed)
                    continue;
                var pending = new Stack<TypedExpr>();
                pending.Push(typed.Typed);
                while (pending.Count > 0)
                {
                    var t = pending.Pop();
                    if (t.Node.Path is not null && t.Through.Count == 0 && t.Member is { Prefix: { } prefix } member
                        && (prefix == SubjectPrefix || prefix.StartsWith(SubjectPrefix + ".", StringComparison.Ordinal)))
                    {
                        var id = member.Id.Split('.')[0];
                        if (IsStereotypeAttribute(model, id))
                            (read ??= new HashSet<string>(StringComparer.Ordinal)).Add(id);
                    }

                    foreach (var arg in t.Args)
                        pending.Push(arg);
                }
            }

            return read is null ? FrozenSet<string>.Empty : read.ToFrozenSet(StringComparer.Ordinal);
        });
    }

    private static void Attributes(ExprScope scope, ModelSnapshot model, Entity entity) => AddAttributes(scope.Root, model, AttributesOf(model, entity), null);

    /// <summary>
    /// Adds attributes, and the members of each value object one holds as <c>attribute.member</c>; <paramref name="overridden"/>, when
    /// given, changes each member before it is added (an overlay entry's type).
    /// </summary>
    private static void AddAttributes(ExprContainer container, ModelSnapshot model, IEnumerable<ModelAttribute> attributes, string? prefix,
        Func<ExprMember, ExprMember>? overridden = null)
    {
        foreach (var attribute in attributes)
        {
            var type = TypeOf(model, attribute.Type);
            var own = new ExprMember(attribute.Id, attribute.Name, type, !attribute.Required)
            {
                Prefix = prefix,
                Collection = attribute.Collection,
                Scale = attribute.Scale,
            };
            container.Add(overridden is null ? own : overridden(own));
            if (type.Kind != "value-object" || model.Get<ValueObject>(type.Of!) is not { } valueObject)
                continue;
            foreach (var member in valueObject.Attributes)
            {
                var inner = new ExprMember(attribute.Id + "." + member.Id, member.Name, TypeOf(model, member.Type), !attribute.Required || !member.Required)
                {
                    Prefix = prefix is null ? attribute.Name : prefix + "." + attribute.Name,
                    Collection = member.Collection,
                    Scale = member.Scale,
                };
                container.Add(overridden is null ? inner : overridden(inner));
            }
        }
    }

    private static void Operation(ExprScope scope, ModelSnapshot model, Operation operation)
    {
        foreach (var member in operation.Input)
        {
            var type = member.KeyOf is not null ? ExprType.Uuid : TypeOf(model, member.Type);
            scope.Root.Add(new ExprMember(member.Id, member.Name, type, !member.Required) { Collection = member.Collection, Scale = member.Scale });
        }

        if (operation.Subject is { } subject && model.Get<Entity>(subject) is { } entity)
        {
            AddAttributes(scope.Root, model, AttributesOf(model, entity), "subject");
            AddNavigations(scope.Root, model, entity, "subject");
        }
    }

    /// <summary>
    /// The navigations a guard follows from an entity (and its bases), as resolution builds them: one per relation of two ends whose far
    /// end names its <c>navigation</c>, held in a path by that end's id, to-one when its maximum is one. What each leads to is filled
    /// when an expression first reads through it.
    /// </summary>
    private static void AddNavigations(ExprContainer container, ModelSnapshot model, Entity entity, string? prefix) =>
        AddNavigations(container, model, NavigationsOf(model, entity).Select(n => n.To), prefix);

    private static void AddNavigations(ExprContainer container, ModelSnapshot model, IEnumerable<RelationEnd> ends, string? prefix)
    {
        foreach (var to in ends)
        {
            var target = model.Get<Entity>(to.Entity);
            container.Add(new ExprNavigation(to.Id, to.Navigation, new ExprContainer(c =>
            {
                if (target is null)
                    return;
                AddAttributes(c, model, AttributesOf(model, target), null);
                AddNavigations(c, model, target, null);
            }), to.Max == MaxCardinality.One, to.Min == 0) { Prefix = prefix, Entity = to.Entity });
        }
    }

    /// <summary>The relation ends an entity (or one of its bases) navigates to, with the relation, in model order.</summary>
    internal static IEnumerable<(RelationEnd To, Relation Relation)> NavigationsOf(ModelSnapshot model, Entity entity)
    {
        var chain = new HashSet<string>(StringComparer.Ordinal);
        for (var current = entity; current is not null && chain.Add(current.Id); current = current.Base is { } b ? model.Get<Entity>(b) : null)
        {
        }

        foreach (var relation in model.All<Relation>())
        {
            if (relation.Ends.Count != 2 || relation.RelationKind == RelationKind.NAry)
                continue;
            for (var k = 0; k < 2; k++)
            {
                var (from, to) = (relation.Ends[1 - k], relation.Ends[k]);
                if (to.Navigation.Length > 0 && chain.Contains(from.Entity))
                    yield return (to, relation);
            }
        }
    }

    private static void Process(ExprScope scope, ModelSnapshot model, Process process, IReadOnlyList<ProcessEvent> events)
    {
        AddAttributes(scope.Root, model, process.Context, null);
        foreach (var e in events)
            AddAttributes(scope.Root, model, e.Payload, "event");
        var (attributes, navigations) = SubjectMembers(model, process);
        AddAttributes(scope.Root, model, attributes, SubjectPrefix);
        AddNavigations(scope.Root, model, navigations, SubjectPrefix);
    }

    /// <summary>The name a process guard reads its object under (lifecycle-design.md decision 20).</summary>
    internal const string SubjectPrefix = "subject";

    /// <summary>
    /// What a process guard's condition reads on its object under <c>subject</c> (lifecycle-design.md decision 20, stereotype-sets-design.md
    /// decision 7): what every subject entry holds under one id (<see cref="EntryMembers"/>). With one entity entry, its own and inherited
    /// attributes, the attributes of the stereotypes it or a base carries, and to-one or to-many navigations (the typer refuses a path through
    /// a to-many one); with several, what they all hold under one id, so attributes that only share a name are not in it; a stereotype entry
    /// holds its stereotype's own attributes and no navigations. An entry whose entity or stereotype is not in the model is left out, as
    /// resolution leaves it out. Kept out: the subjects' bound attributes (the chart already knows its state), and everything when a context
    /// attribute is named <c>subject</c>, which keeps its meaning. Attributes nearest first, navigations in model order, each once.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="process">The process.</param>
    /// <returns>The attributes and the relation ends navigated to.</returns>
    internal static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations) SubjectMembers(ModelSnapshot model, Process process)
    {
        if (process.Context.Any(a => string.Equals(a.Name, SubjectPrefix, StringComparison.Ordinal)))
            return ([], []);
        return Held(model, process, process.Subjects.Select(s => s.BoundAttribute).OfType<string>().ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>
    /// The attributes every subject of a process holds under one id, its bound attributes included, by id: what a set may name before
    /// MQ9802 (object-effects-design.md decision 2). Empty without subjects, or when a context attribute named <c>subject</c> hides them.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The attributes by id.</returns>
    internal static Dictionary<string, ModelAttribute> HeldAttributes(ModelSnapshot model, Process process) =>
        process.Context.Any(a => string.Equals(a.Name, SubjectPrefix, StringComparison.Ordinal))
            ? new Dictionary<string, ModelAttribute>(StringComparer.Ordinal)
            : Held(model, process, new HashSet<string>(StringComparer.Ordinal)).Attributes.ToDictionary(a => a.Id, StringComparer.Ordinal);

    private static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations) Held(ModelSnapshot model, Process process, HashSet<string> bound) =>
        Held(model, process.Subjects, bound);

    private static (IReadOnlyList<ModelAttribute> Attributes, IReadOnlyList<RelationEnd> Navigations) Held(ModelSnapshot model, IEnumerable<ProcessSubject> entries,
        HashSet<string> bound)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        List<ModelAttribute>? attributes = null;
        List<RelationEnd>? navigations = null;
        foreach (var entry in entries)
        {
            if (!named.Add(entry.Entity is { } id ? "e:" + id : "s:" + entry.Stereotype) || EntryMembers(model, entry) is not { } members)
                continue;
            if (attributes is null || navigations is null)
            {
                attributes = members.Attributes.Where(a => !bound.Contains(a.Id)).DistinctBy(a => a.Id).ToList();
                navigations = members.Navigations.DistinctBy(e => e.Id).ToList();
                continue;
            }

            var held = members.Attributes.Select(a => a.Id).ToHashSet(StringComparer.Ordinal);
            attributes.RemoveAll(a => !held.Contains(a.Id));
            var reached = members.Navigations.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
            navigations.RemoveAll(e => !reached.Contains(e.Id));
        }

        return (attributes ?? [], navigations ?? []);
    }

    /// <summary>
    /// Whether a process guard's typed condition reads its object (<see cref="SubjectMembers"/>): a path whose first segment is under
    /// <c>subject</c>. Never when a context attribute is named <c>subject</c>, whose own members print under that name.
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="typed">The guard's typed condition.</param>
    /// <returns>Whether it reads the object.</returns>
    internal static bool ReadsSubject(Process process, TypedExpr typed) => SubjectPathDepth(process, typed) is not null;

    /// <summary>
    /// The most navigations a path of a typed condition follows under <c>subject</c>: 0 for a path to the object's own attribute,
    /// <see langword="null"/> when the condition does not read the object (<see cref="ReadsSubject"/>).
    /// </summary>
    /// <param name="process">The process.</param>
    /// <param name="typed">The guard's typed condition.</param>
    /// <returns>The depth, or <see langword="null"/>.</returns>
    internal static int? SubjectPathDepth(Process process, TypedExpr typed) =>
        process.Context.Any(a => string.Equals(a.Name, SubjectPrefix, StringComparison.Ordinal)) ? null : SubjectPathDepth(typed);

    /// <summary>
    /// The most navigations a path of a typed expression follows under <c>subject</c>, where no context attribute takes that name: a named
    /// trigger point's (trigger-points-design.md decision 33), which has no context. <see langword="null"/> when it does not read the object.
    /// </summary>
    /// <param name="typed">The typed expression.</param>
    /// <returns>The depth, or <see langword="null"/>.</returns>
    internal static int? SubjectPathDepth(TypedExpr typed)
    {
        int? depth = null;
        var pending = new Stack<TypedExpr>();
        pending.Push(typed);
        while (pending.Count > 0)
        {
            var t = pending.Pop();
            if (t.Node.Path is not null && (t.Through.Count > 0 ? t.Through[0].Prefix : t.Member?.Prefix) is { } prefix
                && (prefix == SubjectPrefix || prefix.StartsWith(SubjectPrefix + ".", StringComparison.Ordinal)))
                depth = Math.Max(depth ?? 0, t.Through.Count);
            // An in_state reads the object's states, or those of an object its path reaches, unless it reads the running chart.
            if (t.Node.InState is not null && !t.Running)
                depth = Math.Max(depth ?? 0, t.Through.Count);
            foreach (var arg in t.Args)
                pending.Push(arg);
        }

        return depth;
    }

    private static readonly ConditionalWeakTable<ModelSnapshot, ConditionalWeakTable<Process, StrongBox<int?>>> SubjectDepths = new();

    /// <summary>
    /// The longest path under <c>subject</c> any guard condition of a process reads (<see cref="SubjectPathDepth(Process, TypedExpr)"/>): <see langword="null"/>
    /// when no condition that types reads the object, as <see cref="Processes.ProcessExpressions"/> then has no subject defaults. Typed once
    /// per model.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The depth, or <see langword="null"/>.</returns>
    internal static int? SubjectDepth(ModelSnapshot model, Process process)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        if (process.Guards.All(g => g.Condition is null) && !SetsData(process) && !TriggerData(process) && !ApprovalData(process))
            return null;
        return SubjectDepths.GetValue(model, static _ => new()).GetValue(process, p =>
        {
            int? depth = null;
            foreach (var id in ConditionIds(p))
            {
                if (ExprResolution.Type(model, p, id) is { } typed && SubjectPathDepth(p, typed.Typed) is { } d)
                    depth = Math.Max(depth ?? 0, d);
            }

            // A set writes into the object's own row (object-effects-design.md section 7), so a process that sets data needs the row.
            if (SetsData(p) || (p.Use == ProcessUse.Lifecycle && p.TriggerPoints.Any(t => t.Sets.Count > 0)))
                depth ??= 0;

            return new StrongBox<int?>(depth);
        }).Value;
    }

    /// <summary>Whether an action of a process sets the object's data (object-effects-design.md decision 1).</summary>
    /// <param name="process">The process.</param>
    /// <returns>Whether one does.</returns>
    internal static bool SetsData(Process process) => process.Actions.Any(a => a.Sets.Count > 0);

    // Whether a lifecycle's trigger points hold what reads or writes the object: a when, a condition or a set.
    private static bool TriggerPointsRead(Process process) =>
        process.Use == ProcessUse.Lifecycle && process.TriggerPoints.Any(t => t.When is not null || t.Condition is not null || t.Sets.Count > 0);

    /// <summary>The ids of a process's guards that hold a condition, then of its actions' sets, in document order: what reads the object.</summary>
    /// <param name="process">The process.</param>
    /// <returns>The guard and set ids.</returns>
    internal static IEnumerable<string> ConditionIds(Process process) =>
        process.Guards.Where(g => g.Condition is not null).Select(g => g.Id).Concat(process.Actions.SelectMany(a => a.Sets).Select(s => s.Id))
            .Concat(process.Use != ProcessUse.Lifecycle ? [] : process.TriggerPoints.SelectMany(t =>
                (t.When is null ? Enumerable.Empty<string>() : [WhenKey(t.Id)]).Concat(t.Condition is null ? [] : [t.Id]).Concat(t.Sets.Select(s => s.Id))))
            .Concat(process.Use == ProcessUse.Library ? [] : process.Transitions.SelectMany(t => t.Gate is { UsesStages: true } gate
                ? gate.AllSignatures.Where(s => s.When is not null).Select(s => s.Id) : []));

    /// <summary>The processes a process invokes, directly or through the processes it invokes, each once and never itself, in walk order.</summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The invoked processes found in the model.</returns>
    internal static IEnumerable<Process> InvokedProcesses(ModelSnapshot model, Process process)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { process.Id };
        var pending = new Stack<Process>();
        pending.Push(process);
        while (pending.Count > 0)
        {
            foreach (var id in ProcessRules.Callees(pending.Pop()))
            {
                if (seen.Add(id) && model.Get<Process>(id) is { } callee)
                {
                    yield return callee;
                    pending.Push(callee);
                }
            }
        }
    }

    /// <summary>
    /// How deep a scenario's subject row nests its links (lifecycle-design.md decision 22): the longest path under <c>subject</c> any guard
    /// condition of the process, or of a process it invokes, reads; 0, attributes only, when none reads the object.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <returns>The depth.</returns>
    internal static int SubjectRowDepth(ModelSnapshot model, Process process) =>
        InvokedProcesses(model, process).Prepend(process).Max(p => SubjectDepth(model, p) ?? 0);
}

/// <summary>
/// The expression rules (next-phase-3-design.md section 3.1, "Rules"): each tree is typed in its site's scope (MQ3101 to MQ3106,
/// MQ3108, MQ3109, MQ3113 come from the typer), and the sites, bindings and guards are checked around it (MQ3107, MQ3110, MQ3111,
/// MQ3114, MQ3115). An element without expression sites costs one type test.
/// </summary>
internal static class ExpressionRules
{
    /// <summary>Checks the expression sites of the report's element.</summary>
    public static void Validate(ValidationContext context, Report report)
    {
        var element = report.Document.Element;
        if (element is not (Table or Stereotype or Entity or Operation or Process or Constraint or Right))
            return;
        var model = context.Model;
        if (element is Right named)
        {
            CheckNamedRight(model, named, report);
            return;
        }

        var sites = ExprSites.Of(model, element);
        if (sites.Count == 0 && !(element is Entity { Bindings.Count: > 0 } bound && bound.Bindings.Any(b => b.Checks.Count > 0)))
            return;
        var trees = new Dictionary<string, Expr>(StringComparer.Ordinal);
        var typedTrees = new Dictionary<string, TypedExpr>(StringComparer.Ordinal);
        Dictionary<string, Column>? columns = null;
        IReadOnlyList<(ExprMember Attribute, Column Column)>? clashes = null;
        foreach (var site in sites)
        {
            if (site.Raw && site.Tree is not null)
            {
                report.Add("MQ3107", "This site holds both its raw form and an expression tree; keep one: the tree, or the text it replaces.", site.Pointer, site.SubId);
            }
            else if (!site.Raw && site.Tree is null && !site.MayBeEmpty)
            {
                report.Add("MQ3107", "This site holds neither its raw form nor an expression tree; write a condition or value.", site.Pointer, site.SubId);
            }

            if (site.Tree is not { } tree)
                continue;
            trees[site.SubId] = tree;
            var scope = ExprSites.Scope(model, element, report.Document, site, text: false);
            var problems = new List<Diagnostic>();
            var typed = ExprTyper.Type(tree, scope, problems);
            if (site.Borrowed is { } used)
            {
                // A named right's condition typed at a use (rights-design.md decision 7): its findings belong to the use, and MQ3108 to the
                // named right's own file.
                foreach (var problem in problems.Where(p => p.Rule != "MQ3108"))
                    report.Add(problem with { Message = $"Named right '{used.Name}': " + problem.Message, JsonPointer = site.Pointer, ElementId = site.SubId });
                continue;
            }

            foreach (var problem in problems)
                report.Add(problem);
            // Read from the stored paths, so a check that does not type because the name read the attribute still says why.
            if (element is Table overlay && (clashes ??= ExprSites.Shadowed(model, overlay, scope)).Count > 0)
                ReportShadowed(model, overlay, site, ExprSites.PathsOf(tree, typed).SelectMany(p => p), clashes, report);
            if (typed is null)
                continue;
            if (problems.All(p => p.Severity != DiagnosticSeverity.Error))
            {
                typedTrees[site.Key] = typed;
                if (element is Table or Stereotype)
                {
                    var known = columns ??= ColumnsOf(model, element);
                    foreach (var misfit in CodeMisfits(model, typed, id => known.GetValueOrDefault(id), site.SubId, report.Document.Path))
                        report.Add(misfit with { Message = misfit.Message + " Type the column by the code's type, or compare it with a code of the column's type." });
                }
            }

            if (element is Process process && ExprSites.EventContextRead(process, typed) is { } shadowed)
            {
                report.Add("MQ3102", site.Kind == ExprSiteKind.Set
                    ? $"The set's value reads the context attribute '{shadowed.Name}', but a value reads the event's payload under that name, so the set is left out; "
                        + "rename the context attribute."
                    : site.Kind == ExprSiteKind.Approval
                    ? $"The named signature's when reads the context attribute '{shadowed.Name}', but a condition's row holds the event under that name, so the "
                        + "when is left out; rename the context attribute."
                    : $"The guard reads the context attribute '{shadowed.Name}', but a condition reads the event's payload under that name, so "
                        + "the guard is left a stub; rename the context attribute.", site.Pointer, site.SubId);
            }

            var read = ExprSites.PathsOf(tree, typed).SelectMany(p => p).ToHashSet(StringComparer.Ordinal);
            if (site.Reads is { } wanted && !read.Contains(wanted) && !read.Contains(wanted.Split('.', '@', ':')[^1])
                && !(element is Table table && ReadsThroughOverride(table, read, wanted)))
            {
                report.Add("MQ3114", element is Entity
                    ? "The entity check does not read its attribute, so a failure points at a member the condition does not judge; point attribute at a member it reads."
                    : "The column check does not read its column; move it to the table's checks or point column at a member it reads.", site.Pointer, site.SubId);
            }

            if (site.Payloads is { Count: > 0 } payloads)
            {
                foreach (var id in read.Where(id => payloads.Any(p => p.Contains(id))).Order(StringComparer.Ordinal))
                {
                    if (payloads.Any(p => !p.Contains(id)))
                    {
                        report.Add("MQ3102", site.Kind switch
                        {
                            ExprSiteKind.Set => $"The set's value reads the payload attribute '{id}', which some event whose transitions run the action does not carry, or the action also "
                                + "runs on entering or leaving a state, where no one event is fixed; read a context attribute, or run the action only on transitions of events that carry it.",
                            ExprSiteKind.Trigger => $"The trigger point reads the payload attribute '{id}', which some event or verb step it runs on does not carry; read a value of "
                                + "the object, or narrow the trigger point with transitions to moves whose events carry it.",
                            ExprSiteKind.TriggerSet => $"The set's value reads the payload attribute '{id}', which some event or verb step its trigger point runs on does not carry; "
                                + "read a value of the object, or narrow the trigger point with transitions to moves whose events carry it.",
                            _ => $"The guard reads the payload attribute '{id}', which some event that uses the guard does not carry; read a context attribute, "
                                + "or use the guard only on transitions of events that carry it.",
                        }, site.Pointer, site.SubId);
                    }
                }
            }

            if (element is Operation operation)
                CheckOperationGuard(model, operation, site, read, report);
        }

        if (element is Entity entity)
            CheckBindings(model, entity, trees, typedTrees, report);
        if (element is Process { Use: not ProcessUse.Library } owner && (ExprSites.SetsData(owner) || owner.TriggerPoints.Any(t => t.Sets.Count > 0)))
            CheckSets(model, owner, report);
        if (element is TriggerPoint { Sets.Count: > 0 } namedPoint)
            CheckNamedSets(model, namedPoint, report);
    }

    /// <summary>MQ9802 and MQ9803 on a named trigger point's sets, as a lifecycle's trigger point's (trigger-points-design.md decision 33).</summary>
    private static void CheckNamedSets(ModelSnapshot model, TriggerPoint point, Report report)
    {
        var bound = BoundAttributes(model);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = ExprSites.NamedTriggerMembers(model, point).Attributes.ToDictionary(a => a.Id, StringComparer.Ordinal);
        for (var j = 0; j < point.Sets.Count; j++)
        {
            var set = point.Sets[j];
            var at = Ptr.At("/sets", j) + "/attribute";
            if (!seen.Add(set.Attribute))
                report.Add("MQ9803", $"Trigger point '{point.Name}' sets '{AttributeName(model, set.Attribute)}' twice; remove the second set.", at, set.Id);
            else if (members.TryGetValue(set.Attribute, out var attribute) && attribute.Derived is not null)
                report.Add("MQ9802", $"The set's attribute '{attribute.Name}' is derived, worked out from others; remove the set.", at, set.Id);
            else if (attribute is not null && bound.Contains(attribute.Id))
                report.Add("MQ9802", $"The set's attribute '{attribute.Name}' holds a lifecycle's state, which only its chart moves; remove the set.", at, set.Id);
        }
    }

    /// <summary>
    /// A named right's condition (rights-design.md decision 7): it is typed at each use, against that lifecycle's subjects, where a finding is
    /// reported; in its own file only text is reported (MQ3108). With no use there is no scope, so text stays text until a lifecycle uses the
    /// right.
    /// </summary>
    private static void CheckNamedRight(ModelSnapshot model, Right right, Report report)
    {
        if (!ExprSites.HasText(right.Condition))
            return;
        report.Add("MQ3108", ExprSites.RightUses(model, right).Count == 0
            ? $"Named right '{right.Name}' holds its condition as text, so renames will not follow it; no lifecycle uses the right, so the text stays until one does, then text-to-trees converts it."
            : $"Named right '{right.Name}' holds its condition as text, so renames will not follow it; convert it with text-to-trees.", "/condition", right.Id);
    }

    /// <summary>
    /// MQ9801 to MQ9803 on a process's sets (object-effects-design.md decision 2): each names an attribute every subject holds under one id,
    /// neither a collection nor a value object, neither derived nor holding a lifecycle's state, once per action. A library's sets are MQ9020.
    /// </summary>
    private static void CheckSets(ModelSnapshot model, Process process, Report report)
    {
        var hidden = process.Context.Any(a => string.Equals(a.Name, ExprSites.SubjectPrefix, StringComparison.Ordinal));
        var held = hidden || process.Subjects.Count == 0 ? null : ExprSites.HeldAttributes(model, process);
        var bound = BoundAttributes(model);
        for (var i = 0; i < process.Actions.Count; i++)
        {
            var action = process.Actions[i];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < action.Sets.Count; j++)
            {
                var set = action.Sets[j];
                var at = Ptr.At(Ptr.At("/actions", i) + "/sets", j) + "/attribute";
                if (!seen.Add(set.Attribute))
                {
                    report.Add("MQ9803", $"Action '{action.Name}' sets '{AttributeName(model, set.Attribute)}' twice; remove the second set.", at, set.Id);
                    continue;
                }

                if (held is null)
                {
                    report.Add("MQ9801", hidden
                        ? "A context attribute named subject hides the object, so the action has nothing to set; rename the context attribute, or remove the set."
                        : "The process has no subjects, so the action has no object to set; give the process a subject, or remove the set.", at, set.Id);
                    continue;
                }

                if (!held.TryGetValue(set.Attribute, out var attribute))
                {
                    var what = model.TryGetEntry(set.Attribute, out var entry) && entry.Kind == "end" ? "is a relation end; setting a link is not supported"
                        : set.Attribute.Contains('.', StringComparison.Ordinal) ? "is a member of a value object; set an attribute of the object itself"
                        : "is not an attribute every subject of the process holds under one id";
                    report.Add("MQ9801", $"The set's attribute '{AttributeName(model, set.Attribute)}' {what}; remove the set.", at, set.Id);
                    continue;
                }

                if (attribute.Collection || ExprSites.TypeOf(model, attribute.Type).Kind == "value-object")
                {
                    report.Add("MQ9801", $"The set's attribute '{attribute.Name}' holds {(attribute.Collection ? "a collection" : "a value object")}, which a set does not write; "
                        + "remove the set.", at, set.Id);
                    continue;
                }

                if (attribute.Derived is not null)
                    report.Add("MQ9802", $"The set's attribute '{attribute.Name}' is derived, worked out from others; remove the set.", at, set.Id);
                else if (bound.Contains(attribute.Id))
                    report.Add("MQ9802", $"The set's attribute '{attribute.Name}' holds a lifecycle's state, which only its chart moves; remove the set.", at, set.Id);
            }
        }

        // A trigger point's sets, as an action's (trigger-points-design.md decision 3): once each, neither derived nor a lifecycle's state.
        for (var i = 0; i < process.TriggerPoints.Count; i++)
        {
            var point = process.TriggerPoints[i];
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var members = point.Sets.Count == 0 ? null : ExprSites.TriggerMembers(model, process, point, withBound: true).Attributes.ToDictionary(a => a.Id, StringComparer.Ordinal);
            for (var j = 0; j < point.Sets.Count; j++)
            {
                var set = point.Sets[j];
                var at = Ptr.At(Ptr.At("/triggerPoints", i) + "/sets", j) + "/attribute";
                if (!seen.Add(set.Attribute))
                    report.Add("MQ9803", $"Trigger point '{point.Name}' sets '{AttributeName(model, set.Attribute)}' twice; remove the second set.", at, set.Id);
                else if (members!.TryGetValue(set.Attribute, out var attribute) && attribute.Derived is not null)
                    report.Add("MQ9802", $"The set's attribute '{attribute.Name}' is derived, worked out from others; remove the set.", at, set.Id);
                else if (attribute is not null && bound.Contains(attribute.Id))
                    report.Add("MQ9802", $"The set's attribute '{attribute.Name}' holds a lifecycle's state, which only its chart moves; remove the set.", at, set.Id);
            }
        }
    }

    private static readonly ConditionalWeakTable<ModelSnapshot, FrozenSet<string>> Bound = new();

    /// <summary>The attributes any process's subject entry binds its state to, once per model.</summary>
    private static FrozenSet<string> BoundAttributes(ModelSnapshot model) => Bound.GetValue(model, static m =>
        m.All<Process>().SelectMany(p => p.Subjects).Select(s => s.BoundAttribute).OfType<string>().ToFrozenSet(StringComparer.Ordinal));

    private static string AttributeName(ModelSnapshot model, string id)
    {
        var last = id.Split('.', '@')[^1];
        return model.TryGetEntry(last, out var entry) && model.Get<Entity>(entry.OwnerId) is { } owner
            && owner.Attributes.FirstOrDefault(a => a.Id == last) is { } attribute ? attribute.Name : id;
    }

    /// <summary>
    /// MQ3102 at an overlay's check that reads an attribute, or a column of the attribute's name (<see cref="ExprSites.Shadowed"/>): text
    /// that writes the name reads the attribute, so it cannot name the column, and a check that reads the column prints as one that
    /// reads the attribute. The name keeps reading the attribute; renaming the column ends the clash.
    /// </summary>
    private static void ReportShadowed(ModelSnapshot model, Table overlay, ExprSite site, IEnumerable<string> paths,
        IReadOnlyList<(ExprMember Attribute, Column Column)> shadowed, Report report)
    {
        var read = paths.ToHashSet(StringComparer.Ordinal);
        foreach (var (attribute, column) in shadowed)
        {
            var readsAttribute = read.Contains(attribute.Id)
                || overlay.Columns.Any(c => string.Equals(c.Attribute, attribute.Id, StringComparison.Ordinal) && read.Contains(c.Id));
            if (!readsAttribute && !read.Contains(column.Id))
                continue;
            var own = overlay.Columns.Any(c => string.Equals(c.Id, column.Id, StringComparison.Ordinal));
            var entity = ExprSites.OverlaidEntity(model, overlay)?.Name;
            report.Add("MQ3102", $"The check reads '{column.Name}', a name both attribute '{attribute.Name}' of entity '{entity}' and "
                + (own ? $"the overlay's column '{column.Name}' have" : $"column '{column.Name}', which a stereotype gives the table, have")
                + ": text that writes it reads the attribute, so it cannot name the column. Rename the column" + (own ? "." : " in its stereotype."),
                site.Pointer, site.SubId);
        }
    }

    /// <summary>
    /// Whether an overlay's column check reads its column through the other id of an override entry: the key the entry overrides where
    /// <c>column</c> names the entry, or the entry where it names the key. Both read the same column.
    /// </summary>
    private static bool ReadsThroughOverride(Table table, HashSet<string> read, string column) =>
        table.Origin == TableOrigin.Synthesized && table.Columns.Any(c => c.Attribute is { } key
            && ((string.Equals(c.Id, column, StringComparison.Ordinal) && read.Contains(key))
                || (string.Equals(key, column, StringComparison.Ordinal) && read.Contains(c.Id))));

    /// <summary>The columns a table's or stereotype's checks read, by the id their paths hold (a contributed column's keyed id).</summary>
    private static Dictionary<string, Column> ColumnsOf(ModelSnapshot model, Element element)
    {
        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        IEnumerable<Column> all = element switch
        {
            Table table => [.. table.Columns, .. TableMembers.Of(model, table).Columns.Select(m => m.Member)],
            Stereotype stereotype => stereotype.Columns,
            _ => [],
        };
        foreach (var column in all)
            columns.TryAdd(column.Id, column);
        return columns;
    }

    /// <summary>
    /// What the SQL lowering of a check finds about the codes it holds (MQ3103): a reference-type code that is not an integer where the
    /// column it meets stores integers (<paramref name="columnOf"/> gives the column each path reads). Lowered for PostgreSQL whatever
    /// the database's dialect, since the column stores its codes so on every one; a tree that holds no code is not lowered.
    /// </summary>
    private static IEnumerable<Diagnostic> CodeMisfits(ModelSnapshot model, TypedExpr typed, Func<string, Column?> columnOf, string elementId, string? filePath)
    {
        if (!HasCode(typed))
            return [];
        var problems = new List<Diagnostic>();
        ExprLowering.ToPostgreSql(typed, new ExprColumns
        {
            Column = t => t.Member is { } m && columnOf(m.Id) is { } c ? new ExprColumn(c.Name) { Form = FormOf(c) } : null,
            Ref = (id, form) => Resolution.ExprResolution.RefLiteral(model, id, form),
            ElementId = elementId,
            FilePath = filePath,
        }, ExprSiteKind.Check, problems);
        return problems.Where(p => p.Rule == "MQ3103");

        static bool HasCode(TypedExpr t) => (t.Node.Ref is not null && t.Type.Kind == "code") || (t.Body is { } body && HasCode(body)) || t.Args.Any(HasCode);
    }

    /// <summary>How a column file stores an enum member or code, by its physical type, as the resolver's <c>ExprResolution.FormOf</c> does.</summary>
    private static ExprStoredForm FormOf(Column column) => column.Type switch
    {
        "int16" or "int32" or "int64" => ExprStoredForm.Integer,
        "uuid" or "ulid" => ExprStoredForm.Uuid,
        _ => ExprStoredForm.Text,
    };

    private static void CheckOperationGuard(ModelSnapshot model, Operation operation, ExprSite site, HashSet<string> read, Report report)
    {
        var index = operation.Guards.ToList().FindIndex(g => g.Id == site.SubId);
        var guard = operation.Guards[index];
        if (guard.Error is { } error)
        {
            // The errors as resolution links them: its own by id, then the inherited ones less what it excludes, by keyed or stereotype id.
            var own = error.Split(':', '.')[^1];
            var errors = operation.Errors.Select(e => e.Id).Concat(OperationErrors.Of(model, operation).Select(e => e.Key));
            if (!errors.Any(e => string.Equals(e, error, StringComparison.Ordinal) || string.Equals(e.Split('.')[^1], own, StringComparison.Ordinal)))
            {
                report.Add("MQ3111", $"The guard's error '{error}' is not an error of the operation; name one of its own or inherited errors.",
                    Ptr.At("/guards", index) + "/error", guard.Id);
            }
        }

        if (operation.Level != OperationLevel.Record && site.Subject is { } subject && read.Any(subject.Contains))
        {
            report.Add("MQ3111", "The guard reads the subject, but the operation is not on one record, so there is no subject to read; read its input instead.",
                site.Pointer, guard.Id);
        }
    }

    /// <summary>
    /// MQ3110 on the checks a binding lists, which may be the entity's own or, by id, its bases'; MQ3115 on an entity check no binding
    /// lists though one could enforce it.
    /// </summary>
    private static void CheckBindings(ModelSnapshot model, Entity entity, Dictionary<string, Expr> trees, Dictionary<string, TypedExpr> typedTrees, Report report)
    {
        if (entity.Checks.Count == 0 && entity.Bindings.All(b => b.Checks.Count == 0))
            return;
        var inherited = new Dictionary<string, (Expr Tree, Entity Declaring)>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
        for (var b = entity.Base is { } first ? model.Get<Entity>(first) : null; b is not null && seen.Add(b.Id); b = b.Base is { } next ? model.Get<Entity>(next) : null)
        {
            foreach (var check in b.Checks)
                inherited.TryAdd(check.Id, (check.Condition, b));
        }

        // A base's check is typed in the scope of the entity that declares it, as the resolver lowers it.
        TypedExpr? TypedOf(string id) => typedTrees.TryGetValue(id, out var own) ? own
            : inherited.TryGetValue(id, out var from) ? Resolution.ExprResolution.Type(model, from.Declaring, id)?.Typed : null;

        var listed = new HashSet<string>(StringComparer.Ordinal);
        var enforceable = new HashSet<string>(StringComparer.Ordinal);
        for (var j = 0; j < entity.Bindings.Count; j++)
        {
            var binding = entity.Bindings[j];
            var table = WriteTable(model, binding);
            foreach (var check in entity.Checks)
            {
                if (table is not null && trees.TryGetValue(check.Id, out var tree) && Unenforceable(model, binding, table, tree, TypedOf(check.Id)) is null)
                    enforceable.Add(check.Id);
            }

            for (var k = 0; k < binding.Checks.Count; k++)
            {
                var id = binding.Checks[k];
                var at = Ptr.At(Ptr.At("/bindings", j) + "/checks", k);
                if (entity.Checks.All(c => c.Id != id) && !inherited.ContainsKey(id))
                {
                    report.Add("MQ3110", $"The binding lists '{id}', which is not a check of this entity or of its bases; list one of the entity's checks.", at, binding.Id);
                    continue;
                }

                if (table is null)
                {
                    report.Add("MQ3110", "The binding lists a check but writes no table, so no database could enforce it; give the binding a write table or remove the check.", at, binding.Id);
                    continue;
                }

                listed.Add(id);
                if ((trees.GetValueOrDefault(id) ?? inherited.GetValueOrDefault(id).Tree) is { } tree && Unenforceable(model, binding, table, tree, TypedOf(id)) is { } problem)
                    report.Add("MQ3110", problem, at, binding.Id);
            }
        }

        for (var i = 0; i < entity.Checks.Count; i++)
        {
            var check = entity.Checks[i];
            if (!listed.Contains(check.Id) && enforceable.Contains(check.Id))
            {
                report.Add("MQ3115", "No binding lists this check, though a binding with a write table maps every attribute it reads, so no database enforces it; "
                    + "list it in that binding's checks.", Ptr.At("/checks", i), check.Id);
            }
        }
    }

    /// <summary>The table a binding writes: its write table, else its source when that is a table; none when it never writes.</summary>
    private static Table? WriteTable(ModelSnapshot model, EntityBinding binding)
    {
        if (binding.Write is { None: true })
            return null;
        var id = binding.Write?.Table ?? binding.Source;
        return model.Get<Table>(id);
    }

    /// <summary>
    /// Why a binding cannot enforce a check: an attribute it reads that the binding does not map to a varying column of the table (a
    /// value object member needs its own field, <c>attributeId.memberId</c>, as the resolver lowers it), or a code the check holds that
    /// the column it meets cannot store (<see cref="CodeMisfits"/>); <see langword="null"/> when it can.
    /// </summary>
    private static string? Unenforceable(ModelSnapshot model, EntityBinding binding, Table table, Expr tree, TypedExpr? typed)
    {
        var columns = new Dictionary<string, Column>(StringComparer.Ordinal);
        foreach (var path in ExprSites.PathsOf(tree, typed).Where(p => p.Count > 0).Select(p => p[0]).Distinct(StringComparer.Ordinal))
        {
            var field = binding.Fields.FirstOrDefault(f => f.Attribute == path);
            if (field is null)
                return $"The binding lists a check that reads '{path}', which the binding does not map to its write table; map the attribute, or remove the check from the binding.";
            if (FieldColumn(model, table, field) is not { } column)
                continue;
            if (column.FixedLength || (column.NativeType?.StartsWith("char(", StringComparison.OrdinalIgnoreCase) ?? false))
                return $"The binding lists a check that reads '{path}', which it maps to the fixed-length column '{column.Name}'; map the attribute to a varying column, or remove the check from the binding.";
            columns[path] = column;
        }

        if (typed is not null && CodeMisfits(model, typed, id => columns.GetValueOrDefault(id), binding.Id, null).FirstOrDefault() is { } misfit)
        {
            return "The binding lists a check its write table cannot hold. " + misfit.Message
                + " Map the attribute to a column of the code's type, or remove the check from the binding.";
        }

        return null;
    }

    /// <summary>The column of a write table a binding field names: by its id (or the last segment of a keyed one), a contributed column's keyed id, or its name.</summary>
    private static Column? FieldColumn(ModelSnapshot model, Table table, BindingField field)
    {
        var columnId = field.Column.Split('.', '@')[^1];
        return table.Columns.FirstOrDefault(c => c.Id == columnId || c.Id == field.Column)
            ?? TableMembers.Of(model, table).Columns.Select(m => m.Member).FirstOrDefault(c => c.Id == field.Column)
            ?? table.Columns.FirstOrDefault(c => string.Equals(c.Name, field.Column, StringComparison.Ordinal));
    }

    /// <summary>The position text of an index, for messages.</summary>
    internal static string Index(int i) => i.ToString(CultureInfo.InvariantCulture);
}
