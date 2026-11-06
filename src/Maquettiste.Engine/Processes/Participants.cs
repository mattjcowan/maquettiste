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

/// <summary>What a step can do now, for the actor its participant acts as (actors-design.md decision 15).</summary>
/// <param name="Source"><c>event</c> or <c>human task</c>; <see langword="null"/> when the step names neither one that can run.</param>
/// <param name="Name">The event's or human task's name.</param>
/// <param name="Actors">The event's or human task's actors.</param>
/// <param name="Moves">For an event, the transitions it enables now, in the order the interpreter tries them.</param>
internal sealed record ActingChoice(string? Source, string? Name, IReadOnlyList<string> Actors, IReadOnlyList<ActingMove> Moves);

/// <summary>One transition an event enables now.</summary>
/// <param name="Gate">Its gate, or <see langword="null"/>.</param>
/// <param name="Unsigned">The gate's required actors that no signature yet satisfies, in file order.</param>
internal sealed record ActingMove(ProcessGate? Gate, IReadOnlyList<string> Unsigned);

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
    /// The actor a step acts as (decision 15, <c>RStep.ActsAs</c>): the step's actor when given (and always for a step with no
    /// participant). Otherwise it is read from the moves the step can make now (<see cref="StatechartInterpreter.Acting"/>), in the order
    /// the interpreter tries them, stopping at the first move the participant can act for. A gated move: (a) a required actor of the gate
    /// not yet signed that the participant satisfies; else (b) a signer it satisfies that the event's actors also accept, when the event
    /// lists actors; else (c) the first signer it satisfies. A gated move it satisfies no signer of is passed over, as the interpreter
    /// passes over a gate the actor cannot sign. A move with no gate, or no move at all: the first of the event's or human task's actors
    /// it satisfies, or the participant's actor when that list is empty. <see langword="null"/> when the participant satisfies none, or
    /// is not one of the cast's.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="step">The step.</param>
    /// <param name="choice">What the step can do now.</param>
    /// <returns>The actor id.</returns>
    public static string? ActsAs(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioStep step, ActingChoice choice)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(choice);
        if (step.Participant is null || step.Actor is not null)
            return step.Actor;
        if (cast.Participant(step.Participant) is not { } participant)
            return null;
        bool Satisfied(string id) => Satisfies(model, process, cast, participant, id);
        var membership = ActorMembership.Of(model);
        foreach (var move in choice.Moves)
        {
            if (move.Gate is not { } gate)
                break;
            if (move.Unsigned.FirstOrDefault(Satisfied) is { } required)
                return required;
            var signers = gate.Signers.Distinct(StringComparer.Ordinal).Where(Satisfied).ToList();
            if (signers.Count == 0)
                continue;
            if (choice.Actors.Count > 0 && signers.FirstOrDefault(s => choice.Actors.Any(a => membership.Accepts(a, s))) is { } accepted)
                return accepted;
            return signers[0];
        }

        if (choice.Actors.Count == 0)
            return participant.Actor;
        return choice.Actors.Distinct(StringComparer.Ordinal).FirstOrDefault(Satisfied);
    }

    /// <summary>
    /// Runs a step. One with a participant runs as the actor it acts as (<see cref="ActsAs"/>, read from what the step can do now), an
    /// event step's signer defaulting to the participant's identity; its trace echoes that input and names the actor in <c>actsAs</c>.
    /// A step with no participant, or one the cast does not hold, runs as given and its trace has no <c>actsAs</c>.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="interpreter">The interpreter the step runs in.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="step">The step.</param>
    /// <param name="choice">What the step could do when it ran, for a step with a participant; else <see langword="null"/>.</param>
    /// <returns>The trace.</returns>
    public static StepTrace Run(ModelSnapshot model, StatechartInterpreter interpreter, Process process, ScenarioCast cast, ScenarioStep step, out ActingChoice? choice)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(step);
        choice = null;
        if (step.Participant is null || cast.Participant(step.Participant) is not { } participant)
            return interpreter.Step(step);
        choice = interpreter.Acting(step);
        var actsAs = ActsAs(model, process, cast, step, choice);
        var effective = step with
        {
            Actor = actsAs,
            Signer = step.Signer ?? (step.Input == StepInput.Event ? participant.IdentityText : null),
        };
        return interpreter.Step(effective) with { ActsAs = actsAs };
    }

    /// <summary>
    /// What a participant failed when its step was refused by actor (MQ9301), or <see langword="null"/> for another refusal: the list it
    /// satisfied none of (the signers of the gates it could sign, or the event's or human task's actors), or the list that did not accept
    /// the actor it acted as.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="participant">The participant.</param>
    /// <param name="choice">What the step could do when it ran.</param>
    /// <param name="refusal">The refusal.</param>
    /// <param name="actsAs">The actor the step acted as.</param>
    /// <returns>The text, starting with the participant.</returns>
    public static string? Failed(ModelSnapshot model, ScenarioParticipant participant, ActingChoice choice, string? refusal, string? actsAs)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(choice);
        var who = $"participant '{participant.Name}'";
        if (refusal == Refusals.GateSigner && choice.Moves.Select(m => m.Gate).OfType<ProcessGate>().DistinctBy(g => g.Id).ToList() is { Count: > 0 } gates)
        {
            var lists = string.Join(" or ", gates.Select(g => $"the signers of gate '{g.Name}' ({Names(model, g.Signers.Distinct(StringComparer.Ordinal))})"));
            return actsAs is null
                ? $"{who} satisfies none of {lists}"
                : $"{who} acts as '{Names(model, [actsAs])}', which is not one of {lists}";
        }

        if (refusal != Refusals.Actor || choice.Source is null)
            return null;
        var list = $"the actors of {choice.Source} '{choice.Name}' ({Names(model, choice.Actors.Distinct(StringComparer.Ordinal))})";
        return actsAs is null
            ? $"{who} satisfies none of {list}"
            : $"{who} acts as '{Names(model, [actsAs])}', a signer of the gate, which is not one of {list}";
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
        {
            if (WithinOnly(model, cast, participant, actorId, actor) is { } within)
                return (within, "/actor");
            return ($"participant '{participant.Name}' does not satisfy actor '{actor.Name}'; leave actor out to let the engine work it out, or name an actor the participant stands for or holds.", "/actor");
        }

        return null;
    }

    /// <summary>
    /// The plain message for a participant that holds the actor's role only within a context while the step needs it held without one,
    /// or <see langword="null"/> when that is not the case.
    /// </summary>
    private static string? WithinOnly(ModelSnapshot model, ScenarioCast cast, ScenarioParticipant participant, string actorId, Actor actor)
    {
        if (actor.Relative is not null)
            return null;
        var membership = ActorMembership.Of(model);
        if (participant.Holds.FirstOrDefault(h => h.Within is not null && membership.Accepts(actorId, h.Actor)) is not { } held)
            return null;
        var context = cast.Objects.FirstOrDefault(o => string.Equals(o.Id, held.Within, StringComparison.Ordinal))?.Name ?? held.Within;
        var role = model.Get<Actor>(held.Actor)?.Name ?? actor.Name;
        var named = model.All<Actor>().FirstOrDefault(a => a.Relative is { Form: "members-of" } r && string.Equals(r.MembersOf, held.Actor, StringComparison.Ordinal));
        var instead = named is null ? $"name an actor taken from the object for the members of {role} within {context}" : $"name the actor for {named.Name} within {context}";
        return $"participant '{participant.Name}' holds {role} only within {context}; this step needs {actor.Name} held without a context. Hold it without a context too, or {instead}.";
    }

    /// <summary>The names of a list's actors, for a refusal's message (MQ9301).</summary>
    /// <param name="model">The model.</param>
    /// <param name="list">The actor ids.</param>
    /// <returns>The names, comma-separated.</returns>
    public static string Names(ModelSnapshot model, IEnumerable<string> list) =>
        string.Join(", ", list.Select(id => model.Get<Actor>(id)?.Name ?? id));

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
        ExprSites.ProcessAttributesOf(model, entity).FirstOrDefault(a => string.Equals(a.Id, id, StringComparison.Ordinal))?.Name
            ?? (RelativeActors.OwnerEnd(model, entity, id) is { } end ? (end.Navigation.Length > 0 ? end.Navigation : end.Id) : id);

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);
}
