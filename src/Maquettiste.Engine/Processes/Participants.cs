using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Processes;

/// <summary>What a run of a process is given about who and what it runs for: its objects, its subject and its participants.</summary>
/// <param name="Objects">The objects (lifecycle-design.md decision 22).</param>
/// <param name="Subject">The id of the object the process runs for.</param>
/// <param name="Participants">The participants (actors-design.md decision 14).</param>
internal sealed record ScenarioCast(IReadOnlyList<ScenarioObject> Objects, string? Subject, IReadOnlyList<ScenarioParticipant> Participants)
{
    /// <summary>A scenario's cast.</summary>
    /// <param name="scenario">The scenario.</param>
    /// <returns>The cast.</returns>
    public static ScenarioCast Of(Scenario scenario) => new(scenario.Objects, scenario.Subject, scenario.Participants);

    /// <summary>The participant with an id, or <see langword="null"/>.</summary>
    /// <param name="id">The id.</param>
    /// <returns>The participant.</returns>
    public ScenarioParticipant? Participant(string? id) =>
        id is null ? null : Participants.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));
}

/// <summary>
/// A scenario's participants (actors-design.md decisions 14 to 17): which actors a participant satisfies, the actor a step acts as by the
/// fixed rule, the step the interpreter runs, and the checks of MQ9305. Signatures are untouched: a signature counts for a required
/// actor only through its actor and that actor's memberships, never through a participant.
/// </summary>
internal static class Participants
{
    /// <summary>
    /// Whether a participant satisfies an actor (decision 17). A plain actor: the participant's actor is it or one of its members, or the
    /// participant holds it or one of its members with no context. The owner: the subject entry's owner field holds the participant's
    /// identity or links to the participant's object; without an owner field, the subject object's <c>owner</c> names the participant. A
    /// path: it ends at the participant's identity or at the participant's object. Members of R within a path: the participant holds R, or
    /// one of its members, within the object the path reaches; a holding with no context does not count (decision 13). A value matches
    /// when its text equals the identity's text.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="participant">The participant.</param>
    /// <param name="actorId">The actor's id.</param>
    /// <returns><see langword="true"/> when the participant satisfies it.</returns>
    public static bool Satisfies(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioParticipant participant, string actorId)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(participant);
        if (model.Get<Actor>(actorId) is not { } actor)
            return false;
        var membership = ActorMembership.Of(model);
        if (actor.Relative is not { } relative)
        {
            return (participant.Actor is { } own && membership.Accepts(actorId, own))
                || participant.Holds.Any(h => h.Within is null && membership.Accepts(actorId, h.Actor));
        }

        if (relative.Form == "owner" && SubjectObject(model, cast) is { } subject && model.Get<Entity>(subject.Entity) is { } entity
            && RelativeActors.EntryOf(model, process, entity)?.Owner is null)
            return string.Equals(subject.Owner, participant.Id, StringComparison.Ordinal);
        var target = RelativeActors.Reach(model, process, cast.Objects, cast.Subject, actor);
        return relative.Form switch
        {
            "owner" or "path" => target is not null && (target.Object is { } obj
                ? string.Equals(obj, participant.Object, StringComparison.Ordinal)
                : Matches(target.Value, participant.IdentityText)),
            "members-of" => target?.Object is { } context && relative.MembersOf is { } holder
                && participant.Holds.Any(h => string.Equals(h.Within, context, StringComparison.Ordinal) && membership.Accepts(holder, h.Actor)),
            _ => false,
        };
    }

    /// <summary>
    /// The first list of the fixed rule (decision 15): for an event step with a gated transition, the signers of its gates in transition
    /// order; else the event's actors; for a completed or failed human task, its actors; else empty. The event or task is looked up in the
    /// process, then in the processes it invokes.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="chart">The chart that runs.</param>
    /// <param name="step">The step.</param>
    /// <returns>The actor ids, each once.</returns>
    public static IReadOnlyList<string> FirstList(ModelSnapshot model, StatechartModel chart, ScenarioStep step)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(step);
        foreach (var c in Charts(model, chart))
        {
            if (step.Input == StepInput.Event && step.Event is { } eventId && c.Events.TryGetValue(eventId, out var ev))
            {
                var signers = c.Transitions.Where(t => t.Trigger == TransitionTrigger.Event && string.Equals(t.Transition.Event, eventId, StringComparison.Ordinal))
                    .Select(t => t.Gate).OfType<ProcessGate>().SelectMany(g => g.Signers).Distinct(StringComparer.Ordinal).ToList();
                return signers.Count > 0 ? signers : [.. ev.Actors.Distinct(StringComparer.Ordinal)];
            }

            if (step.Input is StepInput.InvokeDone or StepInput.InvokeError && step.Invoke is { } invokeId && c.Invokes.TryGetValue(invokeId, out var found))
                return found.Invoke.Type == InvokeType.HumanTask ? [.. found.Invoke.Actors.Distinct(StringComparer.Ordinal)] : [];
        }

        return [];
    }

    /// <summary>
    /// The actor a step acts as (decision 15, <c>RStep.ActsAs</c>): the step's actor when given (and always for a step with no
    /// participant); else the first actor of <see cref="FirstList"/> the participant satisfies, or the participant's actor when that
    /// list is empty; <see langword="null"/> when the participant satisfies none, or is not one of the cast's.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="chart">The chart that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="step">The step.</param>
    /// <returns>The actor id.</returns>
    public static string? ActsAs(ModelSnapshot model, StatechartModel chart, ScenarioCast cast, ScenarioStep step)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(chart);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(step);
        if (step.Participant is null || step.Actor is not null)
            return step.Actor;
        if (cast.Participant(step.Participant) is not { } participant)
            return null;
        var list = FirstList(model, chart, step);
        if (list.Count == 0)
            return participant.Actor;
        foreach (var listed in list)
        {
            if (Satisfies(model, chart.Process, cast, participant, listed))
                return listed;
        }

        return null;
    }

    /// <summary>
    /// The step the interpreter runs for a step with a participant: its actor is the one it acts as (<see cref="ActsAs"/>), and an event
    /// step's signer defaults to the participant's identity. A step with no participant, or one the cast does not hold, runs as given.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="chart">The chart that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="step">The step.</param>
    /// <returns>The step to run.</returns>
    public static ScenarioStep Effective(ModelSnapshot model, StatechartModel chart, ScenarioCast cast, ScenarioStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Participant is null || cast.Participant(step.Participant) is not { } participant)
            return step;
        return step with
        {
            Actor = ActsAs(model, chart, cast, step),
            Signer = step.Signer ?? (step.Input == StepInput.Event ? participant.IdentityText : null),
        };
    }

    /// <summary>
    /// Why a scenario's participants are bad (MQ9305), with the pointer of the first problem, or <see langword="null"/>: a participant's
    /// actor that is a role, group, organization or actor from the object; a holding that is not a plain role, group or organization; a
    /// <c>within</c> or <c>object</c> that is not one of the scenario's objects; an object's <c>owner</c> that is not one of the
    /// participants, or that sits beside its subject entry's owner field. An id the model does not hold is left to MQ2001.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="cast">The cast.</param>
    /// <returns>The first problem: its message and its pointer.</returns>
    public static (string Message, string Pointer)? Check(ModelSnapshot model, Process process, ScenarioCast cast)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(cast);
        var objects = new HashSet<string>(cast.Objects.Select(o => o.Id), StringComparer.Ordinal);
        for (var i = 0; i < cast.Participants.Count; i++)
        {
            var participant = cast.Participants[i];
            var pointer = "/participants/" + N(i);
            if (participant.Actor is { } actorId && model.Get<Actor>(actorId) is { } actor && (ActorTypes.HoldsMembers(actor.Type) || actor.Relative is not null))
            {
                return ($"Participant '{participant.Name}' stands for '{actor.Name}', {Describe(actor)}; a participant stands for a person, an external system or the system actor, and holds roles, groups and organizations in holds.",
                    pointer + "/actor");
            }

            for (var k = 0; k < participant.Holds.Count; k++)
            {
                var holding = participant.Holds[k];
                var at = pointer + "/holds/" + N(k);
                if (model.Get<Actor>(holding.Actor) is { } held && (!ActorTypes.HoldsMembers(held.Type) || held.Relative is not null))
                    return ($"Participant '{participant.Name}' holds '{held.Name}', {Describe(held)}; a participant holds only plain roles, groups and organizations.", at + "/actor");
                if (holding.Within is { } within && !objects.Contains(within))
                    return ($"Participant '{participant.Name}' holds a role within '{within}', which is not one of the scenario's objects; name one of its objects.", at + "/within");
            }

            if (participant.Object is { } record && !objects.Contains(record))
                return ($"Participant '{participant.Name}' names '{record}' as its object, which is not one of the scenario's objects; name one of its objects.", pointer + "/object");
        }

        for (var i = 0; i < cast.Objects.Count; i++)
        {
            var obj = cast.Objects[i];
            if (obj.Owner is not { } owner)
                continue;
            var pointer = "/objects/" + N(i) + "/owner";
            if (cast.Participant(owner) is null)
                return ($"Object '{obj.Name}' names '{owner}' as its owner, which is not one of the scenario's participants; name one of them.", pointer);
            if (model.Get<Entity>(obj.Entity) is { } entity && RelativeActors.EntryOf(model, process, entity) is { Owner: { } field })
            {
                return ($"Object '{obj.Name}' names its owner, but process '{process.Name}' holds the owner of entity '{entity.Name}' in '{FieldName(model, entity, field)}'; give the owner in that field, or remove the object's owner.",
                    pointer);
            }
        }

        return null;
    }

    /// <summary>
    /// Why a step's participant fields are bad (MQ9305), with the field, or <see langword="null"/>: a participant that is not one of the
    /// cast's, or an actor the participant does not satisfy.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process.</param>
    /// <param name="cast">The cast.</param>
    /// <param name="step">The step.</param>
    /// <returns>The problem's message (without the step prefix) and field.</returns>
    public static (string Message, string Field)? CheckStep(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioStep step)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(step);
        if (step.Participant is not { } id)
            return null;
        if (cast.Participant(id) is not { } participant)
            return ($"the participant '{id}' is not one of the scenario's participants; name one of them.", "/participant");
        if (step.Actor is { } actorId && model.Get<Actor>(actorId) is { } actor && !Satisfies(model, process, cast, participant, actorId))
            return ($"participant '{participant.Name}' does not satisfy actor '{actor.Name}'; leave actor out to let the engine work it out, or name an actor the participant stands for or holds.", "/actor");
        return null;
    }

    /// <summary>The names of a list's actors, for a refusal's message (MQ9301).</summary>
    /// <param name="model">The model.</param>
    /// <param name="list">The actor ids.</param>
    /// <returns>The names, comma-separated.</returns>
    public static string Names(ModelSnapshot model, IEnumerable<string> list) =>
        string.Join(", ", list.Select(id => model.Get<Actor>(id)?.Name ?? id));

    private static IEnumerable<StatechartModel> Charts(ModelSnapshot model, StatechartModel chart)
    {
        yield return chart;
        foreach (var process in ExprSites.InvokedProcesses(model, chart.Process))
        {
            if (!string.Equals(process.Id, chart.Process.Id, StringComparison.Ordinal))
                yield return ProcessLibraries.Chart(model, process);
        }
    }

    private static ScenarioObject? SubjectObject(ModelSnapshot model, ScenarioCast cast) =>
        cast.Subject is { } subject ? cast.Objects.FirstOrDefault(o => string.Equals(o.Id, subject, StringComparison.Ordinal)) : null;

    private static bool Matches(JsonElement? value, string identity) => value switch
    {
        { ValueKind: JsonValueKind.String } text => string.Equals(text.GetString(), identity, StringComparison.Ordinal),
        { ValueKind: JsonValueKind.Number } number => string.Equals(number.GetRawText(), identity, StringComparison.Ordinal),
        _ => false,
    };

    private static string Describe(Actor actor) => actor.Relative is not null
        ? "an actor worked out from the object"
        : actor.Type switch
        {
            ActorType.Role => "a role",
            ActorType.Group => "a group",
            ActorType.Organization => "an organization",
            ActorType.ExternalSystem => "an external system",
            ActorType.System => "the system actor",
            _ => "a person",
        };

    private static string FieldName(ModelSnapshot model, Entity entity, string id) =>
        ScenarioObjects.Attributes(model, entity).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))?.Name
            ?? (RelativeActors.OwnerEnd(model, entity, id) is { } end ? (end.Navigation.Length > 0 ? end.Navigation : end.Id) : id);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
