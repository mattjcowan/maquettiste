using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;
using Maquettiste.Engine.Revisions;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rules of revisions (revisions-design.md section 7): MQ8301 (a level's shape and values), MQ8303 (revisions that can't be read back),
/// MQ8305 (the bound attribute), MQ8308 (duplicated links where the entity's own end has max 1) and MQ8312 (an entry that can't stand) on
/// each entity whose family has a <c>revisions</c> block, and MQ8306 on a process with a <c>nextRevision</c>.
/// <para>
/// Each entity's merged block is checked (decision 9, <see cref="RevisionCoverage"/>). A finding goes on the file that holds an offending key;
/// when every key comes from bases, the nearest of those bases reports it when its own merged block has it too, and otherwise the entity does,
/// at its <c>base</c>, since the conflict comes only from merging. A project without a block or a <c>nextRevision</c> does no work here beyond
/// one look at the snapshot's coverage per entity and one pass over a process's transitions.
/// </para>
/// </summary>
internal static class RevisionRules
{
    private const string Letters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static readonly ConditionalWeakTable<ModelSnapshot, ConcurrentDictionary<string, IReadOnlyList<Finding>>> ByModel = new();

    private static readonly ConditionalWeakTable<ModelSnapshot, StrongBox<bool>> NextRevisions = new();

    /// <summary>Runs the rules that belong to one element file.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="report">The report of the document to validate.</param>
    public static void Validate(ValidationContext context, Report report)
    {
        switch (report.Document.Element)
        {
            case Entity entity:
                CheckEntity(context.Model, entity, report);
                break;
            case Process process:
                CheckNextRevision(context.Model, process, report);
                break;
        }
    }

    /// <summary>Whether some transition of the model names a <c>nextRevision</c>, once per snapshot.</summary>
    /// <param name="model">The model.</param>
    /// <returns>Whether one does.</returns>
    internal static bool AnyNextRevision(ModelSnapshot model) =>
        NextRevisions.GetValue(model, static m => new StrongBox<bool>(m.All<Process>().Any(p => p.Transitions.Any(t => t.NextRevision is not null)))).Value;

    /// <summary>One finding of an entity's merged block, with the keys it involves, each in the file of the entity that writes it.</summary>
    /// <param name="Rule">The rule id.</param>
    /// <param name="Key">What it is about, the same for the same item and case on every entity of the family.</param>
    /// <param name="Message">The message, naming the entity checked.</param>
    /// <param name="At">The keys involved, each with the entity whose block writes it and its pointer there, the first preferred.</param>
    private sealed record Finding(string Rule, string Key, string Message, IReadOnlyList<(Entity From, string Pointer)> At);

    private static void CheckEntity(ModelSnapshot model, Entity entity, Report report)
    {
        var coverage = RevisionCoverage.Of(model);
        if (!coverage.Any || coverage.For(entity) is null)
            return;
        List<string>? chain = null;
        foreach (var finding in FindingsOf(model, entity))
        {
            var own = finding.At.FirstOrDefault(a => string.Equals(a.From.Id, entity.Id, StringComparison.Ordinal));
            if (own.From is not null)
            {
                report.Add(finding.Rule, finding.Message, own.Pointer);
                continue;
            }

            // Every key comes from bases: the nearest of them, whose merged block shares them, reports it when it has it too.
            chain ??= SubjectCoverage.Chain(model, entity);
            var nearest = finding.At.Select(a => a.From).MinBy(e => chain.IndexOf(e.Id) is var i and >= 0 ? i : int.MaxValue)!;
            if (!FindingsOf(model, nearest).Any(f => f.Rule == finding.Rule && f.Key == finding.Key))
                report.Add(finding.Rule, finding.Message, "/base");
        }
    }

    private static IReadOnlyList<Finding> FindingsOf(ModelSnapshot model, Entity entity) =>
        ByModel.GetValue(model, static _ => new ConcurrentDictionary<string, IReadOnlyList<Finding>>(StringComparer.Ordinal))
            .GetOrAdd(entity.Id, static (_, state) => Compute(state.model, state.entity), (model, entity));

    private static List<Finding> Compute(ModelSnapshot model, Entity entity)
    {
        var findings = new List<Finding>();
        if (RevisionCoverage.Of(model).For(entity) is not { } merged)
            return findings;
        var held = ExprSites.ProcessAttributesOf(model, entity).ToDictionary(a => a.Id, StringComparer.Ordinal);
        if (merged.LevelsFrom is not { } levelsFrom)
        {
            // Only an entity that writes a block reports it: a derived one without a block leaves it to the base, whose family names none either.
            if (entity.Revisions is not null)
                findings.Add(new("MQ8301", "levels", $"'{entity.Name}' has a revisions block, but neither it nor a base names levels; give the block levels.",
                    [(entity, "/revisions")]));
        }
        else
        {
            CheckLevels(entity, merged, levelsFrom, findings);
            CheckSeparator(entity, merged, levelsFrom, findings);
        }

        var sequence = merged.Levels.Count > 0 && RevisionSequence.TryCreate(merged.Levels, merged.Separator, out var built, out _) ? built : null;
        var nearest = NearestAttributeEntries(model, entity);
        CheckBound(model, entity, merged, sequence, held, nearest, findings);
        CheckOwnEntries(model, entity, merged, held, findings);
        foreach (var (id, entry) in nearest)
        {
            // A base's entry, checked again where the entity changes the answer: its key and the lifecycles that govern it.
            if (!string.Equals(entry.From.Id, entity.Id, StringComparison.Ordinal) && held.TryGetValue(id, out var attribute))
                CheckAttributeOnEntity(model, entity, merged, attribute, entry.OnRevise, entry.From, entry.Pointer, findings);
        }

        return findings;
    }

    // ---- MQ8301 ----

    private static void CheckLevels(Entity entity, MergedRevisions merged, Entity from, List<Finding> findings)
    {
        var levels = merged.Levels;
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < levels.Count; i++)
        {
            var level = levels[i];
            var at = Ptr.At("/revisions/levels", i);
            var label = Label(level, i, true);
            void Add(string key, string message, string pointer) => findings.Add(new("MQ8301", $"level:{i}:{key}", message, [(from, pointer)]));

            if (level.Format is not null && level.Values is not null)
                Add("shape", $"{label} of '{entity.Name}' has both format and values; keep one.", at);
            else if (level.Format is null && level.Values is null)
                Add("shape", $"{label} of '{entity.Name}' has neither format nor values; give it a format, letters or numbers, or a list of values.", at);
            else if (level.Values is { } values)
            {
                var extra = new List<string>();
                if (level.First is not null)
                    extra.Add("first");
                if (level.Skip is not null)
                    extra.Add("skip");
                if (level.Width is not null)
                    extra.Add("width");
                if (extra.Count > 0)
                    Add("list", $"{label} of '{entity.Name}' is a list of values, so first, skip and width don't apply; remove {string.Join(" and ", extra)}.",
                        Ptr.Prop(at, extra[0]));
                if (merged.Separator.Length > 0 && values.Select((v, k) => (v, k)).FirstOrDefault(p => p.v.Contains(merged.Separator, StringComparison.Ordinal)) is { v: not null } holding)
                {
                    var involved = new List<(Entity, string)> { (from, Ptr.At(Ptr.Prop(at, "values"), holding.k)) };
                    if (merged.SeparatorFrom is { } separatorFrom)
                        involved.Add((separatorFrom, "/revisions/separator"));
                    findings.Add(new("MQ8301", $"level:{i}:separator",
                        $"Value '{holding.v}' of {Label(level, i, false)} of '{entity.Name}' holds the separator '{merged.Separator}', so its revisions can't be read back; change the value or the separator.",
                        involved));
                }
            }
            else if (level.Format == RevisionFormat.Letters)
            {
                if (level.Width is not null)
                    Add("width", $"{label} of '{entity.Name}' is letters, and width is for a numbers level only; remove it.", Ptr.Prop(at, "width"));
                var alphabet = string.Concat(Letters.Where(c => level.Skip?.Any(s => s.Length == 1 && s[0] == c) != true));
                if (alphabet.Length == 0)
                    Add("first", $"{label} of '{entity.Name}' skips every letter, so it has no values; skip fewer letters.", Ptr.Prop(at, "skip"));
                else if (level.First is { } first && FirstLetterProblem(first, alphabet) is { } problem)
                    Add("first", $"{label} of '{entity.Name}' starts at {Shown(first)}, which is not a value of its letters: {problem}; give a run of the letters it uses, such as '{alphabet[0]}'.",
                        Ptr.Prop(at, "first"));
            }
            else
            {
                if (level.Skip is not null)
                    Add("skip", $"{label} of '{entity.Name}' is numbers, and skip is for a letters level only; remove it.", Ptr.Prop(at, "skip"));
                if (level.First is { ValueKind: not JsonValueKind.Number } first)
                    Add("first", $"{label} of '{entity.Name}' starts at {Shown(first)}, which is not a whole number; give a number such as 1, without quotes.", Ptr.Prop(at, "first"));
            }

            if (level.Name is { } name)
            {
                if (names.TryGetValue(name, out var earlier))
                    Add("name", $"Levels {earlier + 1} and {i + 1} of '{entity.Name}' are both named '{name}'; give each its own name.", Ptr.Prop(at, "name"));
                else
                    names[name] = i;
            }

            if (i > 0 && levels[i - 1].StartEmpty == true && level.StartEmpty != true)
                Add("empty", $"{label} of '{entity.Name}' doesn't start empty, below level {i}, which does; give it startEmpty, since empty levels only come at the end.", at);
        }
    }

    /// <summary>Why a letters level's <c>first</c> is not a run of its letters, or <see langword="null"/> when it is one.</summary>
    private static string? FirstLetterProblem(JsonElement first, string alphabet)
    {
        if (first.ValueKind != JsonValueKind.String)
            return "a letters level starts at letters, written as a string";
        var text = first.GetString()!;
        if (text.Length == 0)
            return "it is empty";
        if (text.FirstOrDefault(c => c is < 'A' or > 'Z') is var other and not '\0')
            return char.IsAsciiLetterLower(other) ? "its letters are upper case" : $"'{other}' is not a letter";
        if (text.FirstOrDefault(c => !alphabet.Contains(c, StringComparison.Ordinal)) is var skipped and not '\0')
            return $"'{skipped}' is skipped";
        return null;
    }

    // ---- MQ8303 ----

    private static void CheckSeparator(Entity entity, MergedRevisions merged, Entity levelsFrom, List<Finding> findings)
    {
        if (merged.SeparatorFrom is not { } separatorFrom)
            return;
        var separator = merged.Separator;
        if (separator.Any(char.IsAsciiLetterOrDigit))
        {
            findings.Add(new("MQ8303", "separator", $"The separator '{separator}' of '{entity.Name}' holds a letter or a digit, so its revisions can't be read back; give a separator such as '.' or '-'.",
                [(separatorFrom, "/revisions/separator")]));
            return;
        }

        if (separator.Length > 0)
            return;
        var levels = merged.Levels;
        for (var i = 1; i < levels.Count; i++)
        {
            if (KindOf(levels[i - 1]) is not { } above || KindOf(levels[i]) is not { } below)
                continue;
            if ((above, below) is ("letters", "numbers") or ("numbers", "letters"))
                continue;
            var message = (above, below) switch
            {
                ("numbers", "numbers") => $"'{entity.Name}' writes two numbers levels with no separator, so '111' could be 1.11 or 11.1; give a separator.",
                ("letters", "letters") => $"'{entity.Name}' writes two letters levels with no separator, so 'AAA' could be A.AA or AA.A; give a separator.",
                _ => $"'{entity.Name}' writes a list level beside another level with no separator, so its revisions can't be split into levels again; give a separator.",
            };
            findings.Add(new("MQ8303", $"separator:{i}", message, [(separatorFrom, "/revisions/separator"), (levelsFrom, Ptr.At("/revisions/levels", i))]));
        }
    }

    private static string? KindOf(RevisionLevel level) => (level.Format, level.Values) switch
    {
        (RevisionFormat.Letters, null) => "letters",
        (RevisionFormat.Numbers, null) => "numbers",
        (null, not null) => "list",
        _ => null,
    };

    // ---- MQ8305 ----

    private static void CheckBound(ModelSnapshot model, Entity entity, MergedRevisions merged, RevisionSequence? sequence, Dictionary<string, ModelAttribute> held,
        Dictionary<string, NearestEntry> nearest, List<Finding> findings)
    {
        if (merged.BoundAttribute is not { } id || merged.BoundAttributeFrom is not { } from || !model.TryGetEntry(id, out _))
            return;
        var at = (from, "/revisions/boundAttribute");
        if (!held.TryGetValue(id, out var attribute))
        {
            findings.Add(new("MQ8305", "bound", $"Attribute '{AttributeName(model, id)}', which holds the revision of '{entity.Name}', is not one of its attributes (its own, an "
                + "inherited one or one from a stereotype it carries); name one of them.", [at]));
            return;
        }

        if (attribute.Derived is not null)
            findings.Add(new("MQ8305", "bound", $"Attribute '{attribute.Name}' of '{entity.Name}' is derived, worked out from others, so it can't hold the revision; hold the revision in a plain string attribute.", [at]));
        else if (attribute.Collection)
            findings.Add(new("MQ8305", "bound", $"Attribute '{attribute.Name}' of '{entity.Name}' holds a collection, so it can't hold the revision; hold the revision in a single string attribute.", [at]));
        else if (!IsText(model, attribute.Type))
            findings.Add(new("MQ8305", "bound", $"Attribute '{attribute.Name}' of '{entity.Name}' is {TypeName(model, attribute.Type)}, and its revisions are {Kinds(merged.Levels)}; hold the revision in a string attribute.", [at]));

        if (StateBinder(model, entity, merged, id) is { } lifecycle)
            findings.Add(new("MQ8305", "bound:state", $"Attribute '{attribute.Name}' of '{entity.Name}' holds the state of lifecycle '{lifecycle.Name}', which only its chart moves, so it can't hold the revision too; hold the revision in its own attribute.", [at]));

        if (sequence is not null && attribute.Default is { ValueKind: JsonValueKind.String } value && value.GetString() is { } text && !sequence.IsValue(text))
        {
            var involved = new List<(Entity, string)> { at };
            if (merged.LevelsFrom is { } levelsFrom)
                involved.Add((levelsFrom, "/revisions/levels"));
            if (merged.SeparatorFrom is { } separatorFrom)
                involved.Add((separatorFrom, "/revisions/separator"));
            var fix = sequence.Start.Length > 0 ? $"give a default such as '{sequence.Start}', or none" : "remove the default, since a new object has no revision";
            findings.Add(new("MQ8305", "bound:default", $"The default '{text}' of attribute '{attribute.Name}' of '{entity.Name}' is not a revision of its sequence, and a new object starts from it; {fix}.", involved));
        }

        if (nearest.TryGetValue(id, out var entry) && entry.OnRevise != AttributeOnRevise.Copy)
            findings.Add(new("MQ8305", "bound:entry", $"Attribute '{attribute.Name}' of '{entity.Name}' holds the revision, which only revising moves, so the block's attributes can't {Word(entry.OnRevise)} it; remove the entry.",
                [at, (entry.From, entry.Pointer)]));
    }

    private static bool IsText(ModelSnapshot model, TypeRef type)
    {
        var builtin = type.Builtin ?? (type.Ref is { } id && model.Get<ScalarType>(id) is { } scalar ? scalar.Base : null);
        // A type the model doesn't hold is MQ2001's.
        return builtin is "string" or "text" || (builtin is null && type.Ref is { } dangling && !model.TryGetEntry(dangling, out _));
    }

    private static string TypeName(ModelSnapshot model, TypeRef type)
    {
        static string Article(string word) => ("aeiou".Contains(word[0], StringComparison.Ordinal) ? "an " : "a ") + word;
        if (type.Builtin is { Length: > 0 } builtin)
            return Article(builtin);
        if (type.Ref is { } id && model.Get<ScalarType>(id) is { } scalar)
            return $"typed by scalar type '{scalar.Name}', {Article(scalar.Base)}";
        return type.Ref is { } other ? $"typed by {Loading.ChangePlanner.Describe(model, other)}" : "untyped";
    }

    private static string Kinds(IReadOnlyList<RevisionLevel> levels)
    {
        var kinds = levels.Select(KindOf).OfType<string>().Distinct(StringComparer.Ordinal).Select(k => k == "list" ? "values from a list" : k).ToList();
        return kinds.Count == 0 ? "text" : string.Join(" and ", kinds);
    }

    // ---- MQ8312 and MQ8308 ----

    private static void CheckOwnEntries(ModelSnapshot model, Entity entity, MergedRevisions merged, Dictionary<string, ModelAttribute> held, List<Finding> findings)
    {
        if (entity.Revisions is not { } block)
            return;
        List<Entity>? derived = null;
        List<Entity> Derived() => derived ??= DerivedFrom(model, entity);
        void Add(string rule, string key, string message, string pointer) => findings.Add(new(rule, key, message, [(entity, pointer)]));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var lifecycles = block.Lifecycles ?? [];
        for (var j = 0; j < lifecycles.Count; j++)
        {
            var entry = lifecycles[j];
            var at = Ptr.At("/revisions/lifecycles", j);
            if (!seen.Add(entry.Lifecycle))
            {
                Add("MQ8312", $"lifecycle:{j}", $"'{entity.Name}' has two entries for lifecycle '{ProcessName(model, entry.Lifecycle)}'; remove the second.", at + "/lifecycle");
                continue;
            }

            if (model.Get<Process>(entry.Lifecycle) is not { } process)
                continue;
            if (process.Use != ProcessUse.Lifecycle)
                Add("MQ8312", $"lifecycle:{j}", $"Process '{process.Name}' is {(process.Use == ProcessUse.Library ? "a library" : "an orchestration")}, not a lifecycle, so it governs no entity; remove the entry.", at + "/lifecycle");
            else if (!SubjectCoverage.Governs(model, process, entity) && !Derived().Exists(d => SubjectCoverage.Governs(model, process, d)))
                Add("MQ8312", $"lifecycle:{j}", $"Lifecycle '{process.Name}' governs neither '{entity.Name}' nor an entity derived from it; remove the entry.", at + "/lifecycle");

            // An id that isn't a state at all is the reference walker's (MQ2001, MQ2002).
            if (entry.OnRevise is RevisionLifecycleEntry.Restart or RevisionLifecycleEntry.Keep || !model.TryGetEntry(entry.OnRevise, out var target) || target.Kind != "state")
                continue;
            if (!string.Equals(target.OwnerId, process.Id, StringComparison.Ordinal))
            {
                var owner = model.Get<Process>(target.OwnerId);
                var name = owner is null ? entry.OnRevise : FindState(owner.States, entry.OnRevise)?.Name ?? entry.OnRevise;
                Add("MQ8312", $"lifecycle:{j}:state", $"State '{name}' belongs to {(owner?.Use == ProcessUse.Lifecycle ? "lifecycle" : "process")} '{owner?.Name}', not '{process.Name}'; name one of its states, restart or keep.",
                    at + "/onRevise");
            }
            else if (FindState(process.States, entry.OnRevise) is { Type: StateType.History or StateType.Choice } pseudo)
                Add("MQ8312", $"lifecycle:{j}:state", $"State '{pseudo.Name}' of lifecycle '{process.Name}' is a {(pseudo.Type == StateType.History ? "history" : "choice")} state, which a revise can't enter; name another state, restart or keep.",
                    at + "/onRevise");
        }

        seen.Clear();
        var attributes = block.Attributes ?? [];
        for (var j = 0; j < attributes.Count; j++)
        {
            var entry = attributes[j];
            var at = Ptr.At("/revisions/attributes", j);
            if (!seen.Add(entry.Attribute))
            {
                Add("MQ8312", $"attribute:{j}", $"'{entity.Name}' has two entries for attribute '{AttributeName(model, entry.Attribute)}'; remove the second.", at + "/attribute");
                continue;
            }

            if (!model.TryGetEntry(entry.Attribute, out _))
                continue;
            if (!held.TryGetValue(entry.Attribute, out var attribute))
                Add("MQ8312", $"attribute:{j}", $"Attribute '{AttributeName(model, entry.Attribute)}' is not one of the attributes of '{entity.Name}' (its own, an inherited one or one from a stereotype it carries); remove the entry.",
                    at + "/attribute");
            else if (attribute.Derived is not null)
                Add("MQ8312", $"attribute:{j}", $"Attribute '{attribute.Name}' of '{entity.Name}' is derived, worked out from others, so a revise doesn't carry it over; remove the entry.", at + "/attribute");
            else
                CheckAttributeOnEntity(model, entity, merged, attribute, entry.OnRevise, entity, at + "/attribute", findings);
        }

        seen.Clear();
        var links = block.Links ?? [];
        for (var j = 0; j < links.Count; j++)
        {
            var entry = links[j];
            var at = Ptr.At("/revisions/links", j);
            if (!seen.Add(entry.End))
            {
                Add("MQ8312", $"end:{j}", $"'{entity.Name}' has two entries for end '{EndRole(model, entry.End)}'; remove the second.", at + "/end");
                continue;
            }

            if (!model.TryGetEntry(entry.End, out var end) || end.Kind != "end" || model.Get<Relation>(end.OwnerId) is not { } relation)
                continue;
            var named = relation.Ends.First(e => string.Equals(e.Id, entry.End, StringComparison.Ordinal));
            if (relation.Ends.Count != 2)
            {
                Add("MQ8312", $"end:{j}", $"End '{named.Role}' belongs to relation '{relation.Name}', which has more than two ends, so a revise can't say what its links do; remove the entry.", at + "/end");
                continue;
            }

            var own = relation.Ends[0] == named ? relation.Ends[1] : relation.Ends[0];
            if (!EndCoverage.Covers(model, own, entity.Id) && !Derived().Exists(d => EndCoverage.Covers(model, own, d.Id)))
                Add("MQ8312", $"end:{j}", $"Neither '{entity.Name}' nor an entity derived from it reaches end '{named.Role}' of relation '{relation.Name}'; remove the entry.", at + "/end");
            if (entry.OnRevise == LinkOnRevise.Duplicate && own.Max == MaxCardinality.One)
                Add("MQ8308", $"end:{j}", $"Revising '{entity.Name}' can't duplicate its '{named.Role}' links: its own end '{own.Role}' has max 1, so each linked object may link to one '{entity.Name}', and would link to two; use move or stay.",
                    at + "/onRevise");
        }
    }

    /// <summary>
    /// MQ8312 on an attribute entry where the entity decides the answer: <c>reset</c> or <c>clear</c> on one of its key attributes, or on the
    /// bound state attribute of a lifecycle that governs it. <c>copy</c>, what any attribute not listed gets, is never refused here, so a derived
    /// entity can undo a base's entry.
    /// </summary>
    private static void CheckAttributeOnEntity(ModelSnapshot model, Entity entity, MergedRevisions merged, ModelAttribute attribute, AttributeOnRevise onRevise, Entity from,
        string pointer, List<Finding> findings)
    {
        if (onRevise == AttributeOnRevise.Copy)
            return;
        if (KeyOf(model, entity)?.Attributes.Contains(attribute.Id, StringComparer.Ordinal) == true)
            findings.Add(new("MQ8312", $"attribute:{attribute.Id}:key", $"Attribute '{attribute.Name}' is part of the key of '{entity.Name}', so a revision can't {Word(onRevise)} it; remove the entry.",
                [(from, pointer)]));
        if (StateBinder(model, entity, merged, attribute.Id) is { } lifecycle)
            findings.Add(new("MQ8312", $"attribute:{attribute.Id}:state", $"Attribute '{attribute.Name}' of '{entity.Name}' holds the state of lifecycle '{lifecycle.Name}' and follows the state, so a revise can't {Word(onRevise)} it; remove the entry.",
                [(from, pointer)]));
    }

    // ---- MQ8306 ----

    private static void CheckNextRevision(ModelSnapshot model, Process process, Report report)
    {
        List<Entity>? governed = null;
        for (var i = 0; i < process.Transitions.Count; i++)
        {
            var transition = process.Transitions[i];
            if (transition.NextRevision is not { } level)
                continue;
            var at = Ptr.At("/transitions", i) + "/nextRevision";
            var label = $"Transition {TransitionLabel(process, transition)} names revision level {level}";
            if (process.Use != ProcessUse.Lifecycle)
            {
                report.Add("MQ8306", $"{label}, but '{process.Name}' is {(process.Use == ProcessUse.Library ? "a library" : "an orchestration")}, which holds no revisions; remove nextRevision.", at, transition.Id);
                continue;
            }

            governed ??= Governed(model, process);
            if (governed.Count == 0)
            {
                report.Add("MQ8306", $"{label}, but lifecycle '{process.Name}' governs no entity with revisions; give one of its entities a revisions block, or remove nextRevision.", at, transition.Id);
                continue;
            }

            foreach (var entity in governed)
            {
                var count = RevisionCoverage.Of(model).For(entity)!.Levels.Count;
                if (count > 0 && count < level)
                    report.Add("MQ8306", $"{label}, but entity '{entity.Name}' has {Count(count, "level")}; name level {count}, or add a level.", at, transition.Id);
            }
        }
    }

    /// <summary>The entities with revisions a lifecycle governs, by name then id.</summary>
    private static List<Entity> Governed(ModelSnapshot model, Process process)
    {
        var coverage = RevisionCoverage.Of(model);
        if (!coverage.Any)
            return [];
        var governed = model.All<Entity>().Where(e => coverage.For(e) is not null && SubjectCoverage.Governs(model, process, e)).ToList();
        governed.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name) is var c and not 0 ? c : string.CompareOrdinal(a.Id, b.Id));
        return governed;
    }

    private static string TransitionLabel(Process process, ProcessTransition transition)
    {
        var source = FindState(process.States, transition.Source)?.Name ?? transition.Source;
        if (transition.Targets.Count == 0)
            return $"from {source}";
        return $"{source} → {string.Join(", ", transition.Targets.Select(t => FindState(process.States, t)?.Name ?? t))}";
    }

    // ---- helpers ----

    /// <summary>An attribute entry as the merge sees it: the nearest block's first entry for the attribute.</summary>
    private sealed record NearestEntry(AttributeOnRevise OnRevise, Entity From, string Pointer);

    /// <summary>For each attribute an entry of the family names, the entry that counts (decision 9), farthest base first.</summary>
    private static Dictionary<string, NearestEntry> NearestAttributeEntries(ModelSnapshot model, Entity entity)
    {
        var nearest = new Dictionary<string, NearestEntry>(StringComparer.Ordinal);
        var chain = SubjectCoverage.Chain(model, entity);
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (model.Get<Entity>(chain[i]) is not { Revisions.Attributes: { } entries } level)
                continue;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var j = 0; j < entries.Count; j++)
            {
                if (seen.Add(entries[j].Attribute))
                    nearest[entries[j].Attribute] = new(entries[j].OnRevise, level, Ptr.At("/revisions/attributes", j) + "/attribute");
            }
        }

        return nearest;
    }

    /// <summary>The first lifecycle that governs the entity and binds its state to the attribute, by name then id.</summary>
    private static Process? StateBinder(ModelSnapshot model, Entity entity, MergedRevisions merged, string attributeId)
    {
        foreach (var rule in merged.Lifecycles)
        {
            if (string.Equals(SubjectCoverage.EntryOf(model, rule.Lifecycle, entity)?.BoundAttribute, attributeId, StringComparison.Ordinal))
                return rule.Lifecycle;
        }

        return null;
    }

    /// <summary>The key of an entity: its own, else the nearest base's.</summary>
    private static EntityKey? KeyOf(ModelSnapshot model, Entity entity)
    {
        foreach (var id in SubjectCoverage.Chain(model, entity))
        {
            if (model.Get<Entity>(id)?.Key is { } key)
                return key;
        }

        return null;
    }

    /// <summary>The entities derived from an entity, directly or not.</summary>
    internal static List<Entity> DerivedFrom(ModelSnapshot model, Entity entity) =>
        [.. model.All<Entity>().Where(e => !string.Equals(e.Id, entity.Id, StringComparison.Ordinal) && e.Base is not null
            && SubjectCoverage.Chain(model, e).Contains(entity.Id, StringComparer.Ordinal))];

    private static ProcessState? FindState(IReadOnlyList<ProcessState> states, string id)
    {
        foreach (var state in states)
        {
            if (string.Equals(state.Id, id, StringComparison.Ordinal))
                return state;
            if (FindState(state.States, id) is { } found)
                return found;
        }

        return null;
    }

    private static string Label(RevisionLevel level, int index, bool capital) =>
        (capital ? "Level " : "level ") + (index + 1).ToString(CultureInfo.InvariantCulture) + (level.Name is { } name ? $" '{name}'" : "");

    private static string Shown(JsonElement value) => value.ValueKind == JsonValueKind.String ? $"'{value.GetString()}'" : value.GetRawText();

    private static string Word(AttributeOnRevise onRevise) => onRevise == AttributeOnRevise.Clear ? "clear" : "reset";

    private static string Count(int n, string noun) => (n switch { 1 => "one", 2 => "two", 3 => "three", _ => n.ToString(CultureInfo.InvariantCulture) }) + " " + noun + (n == 1 ? "" : "s");

    private static string ProcessName(ModelSnapshot model, string id) => model.Get<Process>(id)?.Name ?? id;

    private static string AttributeName(ModelSnapshot model, string id) =>
        model.TryGetEntry(id, out var entry) && model.GetDocument(entry.OwnerId)?.Element is { } owner
            && ReferenceDataIndex.AttributesOf(owner).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)) is { } attribute
            ? attribute.Name : id;

    private static string EndRole(ModelSnapshot model, string id) =>
        model.TryGetEntry(id, out var entry) && model.Get<Relation>(entry.OwnerId) is { } relation
            && relation.Ends.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is { } end ? end.Role : id;

}
