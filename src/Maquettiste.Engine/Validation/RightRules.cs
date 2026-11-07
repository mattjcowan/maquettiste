using System.Collections.Frozen;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The rights rules (rights-design.md section 5): MQ9901 to MQ9906 on a lifecycle's rights and default, and MQ9903 and MQ9906 on a named
/// right. A named right's condition, its ends and its actors from the object are judged at each use. Every rule needs rights, a default
/// or a named right, so a model without them gives the same findings as before.
/// </summary>
internal static class RightRules
{
    /// <summary>The verbs that act on a link, the only ones a right with <c>ends</c> may name (rights-design.md decision 20).</summary>
    private static readonly FrozenSet<string> LinkVerbs = new[] { "connect", "disconnect", "read", "modify", "freeze", "thaw" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>The verbs that read or modify attributes, the only ones a right with <c>attributes</c> may name (rights-design.md decision 21).</summary>
    private static readonly FrozenSet<string> AttributeVerbs = new[] { "read", "modify" }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Checks the rights rules of a document.</summary>
    /// <param name="context">The validation context.</param>
    /// <param name="element">The document's element.</param>
    /// <param name="report">The document's report.</param>
    public static void Check(ValidationContext context, Element element, Report report)
    {
        switch (element)
        {
            case Process process when process.Rights.Count > 0 || process.RightsDefault is not null:
                if (process.Use == ProcessUse.Lifecycle)
                    CheckLifecycle(context.Model, process, report);
                else
                    NotALifecycle(process, report);
                break;
            case Right right:
                CheckEnds(right.Verbs, right.Ends, "/ends", right.Id, $"Named right '{right.Name}'", report);
                CheckAttributes(right.Verbs, right.Ends, right.Attributes, "/attributes", right.Id, $"Named right '{right.Name}'", report);
                CheckExcept(context.Model, right.Actors, right.Except, "/except", right.Id, $"named right '{right.Name}'", report);
                break;
        }
    }

    // MQ9901: only lifecycles hold rights; an orchestration's and a library's events and tasks keep their closed lists (decision 2).
    private static void NotALifecycle(Process process, Report report)
    {
        var what = process.Use == ProcessUse.Library ? "a library" : "an orchestration";
        if (process.Rights.Count > 0)
            report.Add("MQ9901", $"Process '{process.Name}' is {what}, and only lifecycles hold rights; remove its rights, or make it a lifecycle.", "/rights");
        if (process.RightsDefault is not null)
            report.Add("MQ9901", $"Process '{process.Name}' is {what}, and only lifecycles have a rights default; remove rightsDefault, or make it a lifecycle.", "/rightsDefault");
    }

    private static void CheckLifecycle(ModelSnapshot model, Process process, Report report)
    {
        var states = new Dictionary<string, ProcessState>(StringComparer.Ordinal);
        Walk(process.States, "", (state, _) => states[state.Id] = state);
        var held = process.Subjects.Count > 0 ? ExprSites.RightMembers(model, process).Navigations.Select(e => e.Id).ToHashSet(StringComparer.Ordinal) : null;
        HashSet<string>? heldAttributes = null;
        var allowsByDefault = process.RightsDefault != RightAccess.Deny;
        var allowed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < process.Rights.Count; i++)
        {
            var entry = process.Rights[i];
            var at = Ptr.At("/rights", i);
            if (Form(model, process, entry, at, report) is not { } form)
                continue;
            var (named, label) = form;
            for (var k = 0; k < entry.States.Count; k++)
            {
                var id = entry.States[k];
                if (!model.TryGetEntry(id, out _))
                    continue; // MQ2001
                var why = !states.TryGetValue(id, out var state) ? "is not a state of this lifecycle"
                    : state.Type is StateType.History or StateType.Choice ? $"is {state.Name}, a {(state.Type == StateType.History ? "history" : "choice")} state, which no object is in"
                    : null;
                if (why is not null)
                    report.Add("MQ9902", $"{label} names state '{id}' in states, which {why}; remove it, or name a state of lifecycle '{process.Name}'.", Ptr.At(at + "/states", k), entry.Id);
            }

            var access = named?.Access ?? entry.Access!.Value;
            var verbs = named?.Verbs ?? entry.Verbs;
            if (access == RightAccess.Allow)
            {
                // An allow that names attributes grants nothing where the attributes are not known, as for an event (decision 21).
                if ((named?.Attributes ?? entry.Attributes).Count == 0)
                    allowed.UnionWith(verbs);
                if (allowsByDefault)
                {
                    report.Add("MQ9904", $"{label} allows, and lifecycle '{process.Name}' allows anything no right denies: each lifecycle answers on its own, so this right "
                        + "changes no answer. Remove it, or set the lifecycle's rightsDefault to deny.", at, entry.Id);
                }
            }

            if (named is null)
            {
                CheckEnds(entry.Verbs, entry.Ends, at + "/ends", entry.Id, label, report);
                CheckAttributes(entry.Verbs, entry.Ends, entry.Attributes, at + "/attributes", entry.Id, label, report);
                CheckExcept(model, entry.Actors, entry.Except, at + "/except", entry.Id, $"right '{entry.Name}'", report);
            }

            if (held is null)
                continue;
            var ends = named?.Ends ?? entry.Ends;
            for (var k = 0; k < ends.Count; k++)
            {
                if (!model.TryGetEntry(ends[k], out _) || held.Contains(ends[k]))
                    continue; // MQ2001, or an end every subject holds
                report.Add("MQ9903", $"{label} names relation end '{EndName(model, ends[k])}' in ends, which not every subject of lifecycle '{process.Name}' holds; "
                    + "remove it, or name an end of the subjects.", named is null ? Ptr.At(at + "/ends", k) : at + "/right", entry.Id);
            }

            var attributes = named?.Attributes ?? entry.Attributes;
            if (attributes.Count > 0)
                heldAttributes ??= ExprSites.RightAttributes(model, process);
            for (var k = 0; k < attributes.Count; k++)
            {
                if (!model.TryGetEntry(attributes[k], out _) || heldAttributes!.Contains(attributes[k]))
                    continue; // MQ2001, or an attribute every subject holds
                report.Add("MQ9903", $"{label} names attribute '{AttributeName(model, attributes[k])}' in attributes, which not every subject of "
                    + $"lifecycle '{process.Name}' holds; remove it, or name an attribute of the subjects.", named is null ? Ptr.At(at + "/attributes", k) : at + "/right", entry.Id);
            }
        }

        if (process.RightsDefault == RightAccess.Deny)
            CheckUnreachable(process, allowed, report);
    }

    /// <summary>
    /// MQ9901 on an entry's form: an inline right (a name, an access and at least one verb) or a use of a named right, never both or neither.
    /// Returns the named right a use names (<see langword="null"/> for an inline right) and how messages name the entry, or
    /// <see langword="null"/> when the entry cannot be read further.
    /// </summary>
    private static (Right? Named, string Label)? Form(ModelSnapshot model, Process process, ProcessRight entry, string at, Report report)
    {
        var inline = entry.Name is not null || entry.DisplayName is not null || entry.Description is not null || entry.Access is not null || entry.Actors.Count > 0
            || entry.Except.Count > 0 || entry.Verbs.Count > 0 || entry.Ends.Count > 0 || entry.Attributes.Count > 0 || entry.WhileFrozen || entry.Condition is not null;
        if (entry.Right is { } id)
        {
            if (inline)
            {
                report.Add("MQ9901", $"An entry of lifecycle '{process.Name}''s rights both uses a named right and says a right of its own; keep one: right, or the keys "
                    + "of an inline right.", at, entry.Id);
                return null;
            }

            if (!model.TryGetEntry(id, out var target))
                return null; // MQ2001
            if (model.Get<Right>(id) is not { } named)
            {
                report.Add("MQ9901", $"An entry of lifecycle '{process.Name}''s rights uses '{id}', {BuiltinRules.Article(target.Kind)} {target.Kind}, which is no named right; "
                    + "name a named right, or remove the entry.", at + "/right", entry.Id);
                return null;
            }

            return (named, $"The use of named right '{named.Name}'");
        }

        var missing = new List<string>();
        if (string.IsNullOrEmpty(entry.Name))
            missing.Add("name");
        if (entry.Access is null)
            missing.Add("access");
        if (entry.Verbs.Count == 0)
            missing.Add("verbs");
        if (missing.Count == 0)
            return (null, $"Right '{entry.Name}'");
        report.Add("MQ9901", inline
            ? $"An inline right of lifecycle '{process.Name}' needs a name, an access and at least one verb; it has no {Join(missing)}. Add them, or remove the entry."
            : $"An entry of lifecycle '{process.Name}''s rights is neither an inline right nor a use of a named right; give it right, or a name, an access and verbs, or remove it.",
            at, entry.Id);
        return null;
    }

    // MQ9903: ends speak of links, so they stand beside link verbs only (decision 20).
    private static void CheckEnds(IReadOnlyList<string> verbs, IReadOnlyList<string> ends, string pointer, string id, string label, Report report)
    {
        if (ends.Count == 0)
            return;
        var other = verbs.Where(v => !LinkVerbs.Contains(v)).ToList();
        if (other.Count > 0)
        {
            report.Add("MQ9903", $"{label} names ends beside {(other.Count == 1 ? "verb" : "verbs")} {string.Join(", ", other.Select(v => "'" + v + "'"))}, which can't act on a link; "
                + "ends go with connect, disconnect, read, modify, freeze and thaw. Remove the ends, or move those verbs to another right.", pointer, id);
        }
    }

    // MQ9903: attributes speak of reading or modifying them, so they stand beside read and modify only, and never beside ends, which speak
    // of links (decision 21).
    private static void CheckAttributes(IReadOnlyList<string> verbs, IReadOnlyList<string> ends, IReadOnlyList<string> attributes, string pointer, string id,
        string label, Report report)
    {
        if (attributes.Count == 0)
            return;
        var other = verbs.Where(v => !AttributeVerbs.Contains(v)).ToList();
        if (other.Count > 0)
        {
            report.Add("MQ9903", $"{label} names attributes beside {(other.Count == 1 ? "verb" : "verbs")} {string.Join(", ", other.Select(v => "'" + v + "'"))}, which "
                + "don't read or modify attributes; attributes go with read and modify. Remove the attributes, or move those verbs to another right.", pointer, id);
        }

        if (ends.Count > 0)
        {
            report.Add("MQ9903", $"{label} names attributes beside ends: with ends it speaks of links, which hold none of the subject's attributes. Remove the "
                + "attributes or the ends, or split the right.", pointer, id);
        }
    }

    // MQ9906: an actor in both lists is one the right never applies to (decision 4).
    private static void CheckExcept(ModelSnapshot model, IReadOnlyList<string> actors, IReadOnlyList<string> except, string pointer, string id, string label, Report report)
    {
        for (var k = 0; k < except.Count; k++)
        {
            if (actors.Contains(except[k], StringComparer.Ordinal))
            {
                report.Add("MQ9906", $"Actor '{(model.Get<Actor>(except[k])?.Name ?? except[k])}' is in both the actors and the except of {label}, so the right never applies to it; "
                    + "remove it from except.", Ptr.At(pointer, k), id);
            }
        }
    }

    // MQ9905: under a deny default, an event or a human task no one can ever do. An event's actors, the signers of the gates on its
    // transitions and an allow right naming its verb let someone through (decisions 13 and 14); an event an action raises is the process's own.
    private static void CheckUnreachable(Process process, HashSet<string> allowed, Report report)
    {
        var gated = process.Transitions.Where(t => t.Event is not null && (t.Gate is not null || t.GatePolicy is not null)).Select(t => t.Event!).ToHashSet(StringComparer.Ordinal);
        var raised = process.Actions.SelectMany(a => a.Raises).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < process.Events.Count; i++)
        {
            var e = process.Events[i];
            if (e.Actors.Count > 0 || gated.Contains(e.Id) || raised.Contains(e.Id) || (e.Verb is { } verb && allowed.Contains(verb)))
                continue;
            report.Add("MQ9905", e.Verb is { } named
                ? $"Lifecycle '{process.Name}' denies by default, and no one can send event '{e.Name}': it lists no actors, its transitions have no gate signers, and no allow right "
                    + $"names its verb '{named}'. Add actors, or an allow right for '{named}'."
                : $"Lifecycle '{process.Name}' denies by default, and no one can send event '{e.Name}': it names no verb, lists no actors and its transitions have no gate signers. "
                    + "Add actors, or give it a verb and an allow right for it.", Ptr.At("/events", i), e.Id);
        }

        if (allowed.Contains("complete"))
            return;
        Walk(process.States, "", (state, pointer) =>
        {
            for (var j = 0; j < state.Invoke.Count; j++)
            {
                var invoke = state.Invoke[j];
                if (invoke.Type == InvokeType.HumanTask && invoke.Actors.Count == 0)
                {
                    report.Add("MQ9905", $"Lifecycle '{process.Name}' denies by default, and no one can complete human task '{invoke.Name}': it lists no actors, and no allow right "
                        + "names complete. Add actors, or an allow right for complete.", Ptr.At(pointer + "/invoke", j), invoke.Id);
                }
            }
        });
    }

    private static void Walk(IReadOnlyList<ProcessState> states, string pointer, Action<ProcessState, string> visit)
    {
        for (var i = 0; i < states.Count; i++)
        {
            var at = Ptr.At(pointer + "/states", i);
            visit(states[i], at);
            Walk(states[i].States, at, visit);
        }
    }

    private static string AttributeName(ModelSnapshot model, string id) =>
        model.TryGetEntry(id, out var entry) && model.Get<Entity>(entry.OwnerId)?.Attributes.FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal)) is { } attribute
            ? attribute.Name
            : id;

    private static string EndName(ModelSnapshot model, string id) =>
        model.TryGetEntry(id, out var entry) && model.Get<Relation>(entry.OwnerId)?.Ends.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.Ordinal)) is { } end
            ? end.Role is { Length: > 0 } role ? role : end.Navigation.Length > 0 ? end.Navigation : id
            : id;

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        1 => items[0],
        2 => items[0] + " and " + items[1],
        _ => string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1],
    };
}
