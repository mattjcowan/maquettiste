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
/// <param name="Gate">Its gate, or <see langword="null"/>; also <see langword="null"/> for an approval that needs nothing, which moves as without a gate.</param>
/// <param name="Unsigned">The gate's required actors that no signature yet satisfies, in file order; empty on an approval.</param>
internal sealed record ActingMove(ProcessGate? Gate, IReadOnlyList<string> Unsigned)
{
    /// <summary>On an approval with something needed, its round's needed signatures in file order (signoff-design.md decision 8); else <see langword="null"/>.</summary>
    public IReadOnlyList<ActingSignature>? Signatures { get; init; }

    /// <summary>On an approval, the id of the person's latest signature in the round, which a withdraw takes back.</summary>
    public string? Latest { get; init; }

    /// <summary>On an approval, the actor the person gave their latest signature in the round as: a withdraw acts as it.</summary>
    public string? LatestActor { get; init; }
}

/// <summary>A needed signature of an approval's round, as the pick reads it for one person and one response (signoff-design.md decision 8).</summary>
/// <param name="Signature">The named signature.</param>
/// <param name="Complete">Whether it has its count and each of its required actors signed.</param>
/// <param name="Unsigned">Its required actors that no signature counted on it yet satisfies, in file order.</param>
/// <param name="Blocked">Whether rows 7c or 7d keep the person from giving the response on it.</param>
internal sealed record ActingSignature(NamedSignature Signature, bool Complete, IReadOnlyList<string> Unsigned, bool Blocked);

/// <summary>
/// The rule that picks the named signature a response answers when the step names none (signoff-design.md decision 8), the same way every time.
/// The engine's step and a participant's actor both use it, so the signature and the actor are picked together.
/// </summary>
internal static class ApprovalPick
{
    /// <summary>
    /// Picks the signature: for <c>withdraw</c>, the person's latest signature in the round; for <c>reject</c>, the first needed signature whose
    /// signers the person meets, complete or not; for <c>sign</c> and <c>ignore</c>, among the needed signatures not complete, the first the person
    /// may fill (rows 7a, 7c and 7d) where they meet a required actor not yet signed, else the first they may fill, else the first needed signature
    /// whose signers they meet, so that row 7 refuses on it: for <c>sign</c> complete or not (a complete one with <c>gate-repeat</c>), for
    /// <c>ignore</c> not complete only, as a complete signature leaves the person nothing to answer. The refusal is <c>gate-not-signed</c> or
    /// <c>gate-signer</c> when nothing can be picked.
    /// </summary>
    /// <param name="response">The response.</param>
    /// <param name="needed">The round's needed signatures, in file order.</param>
    /// <param name="latest">The id of the person's latest signature in the round, or <see langword="null"/>.</param>
    /// <param name="actorFor">The actor the person gives a signature as, or <see langword="null"/> when they meet none of its signers; and whether that actor is one of its required actors not yet signed.</param>
    /// <returns>The signature and the actor, or the refusal.</returns>
    public static (NamedSignature? Signature, string? Actor, string? Refusal) Choose(SignatureResponse response, IReadOnlyList<ActingSignature> needed, string? latest,
        Func<ActingSignature, (string? Actor, bool Required)> actorFor)
    {
        ArgumentNullException.ThrowIfNull(needed);
        ArgumentNullException.ThrowIfNull(actorFor);
        if (response == SignatureResponse.Withdraw)
        {
            var mine = latest is null ? null : needed.FirstOrDefault(s => string.Equals(s.Signature.Id, latest, StringComparison.Ordinal));
            return mine is null ? (null, null, Refusals.GateNotSigned) : (mine.Signature, actorFor(mine).Actor, null);
        }

        if (response == SignatureResponse.Reject)
        {
            foreach (var s in needed)
            {
                if (actorFor(s).Actor is { } actor)
                    return (s.Signature, actor, null);
            }

            return (null, null, Refusals.GateSigner);
        }

        (NamedSignature Signature, string Actor)? fill = null, met = null;
        foreach (var s in needed)
        {
            if ((s.Complete && response == SignatureResponse.Ignore) || actorFor(s) is not { Actor: { } actor } found)
                continue;
            met ??= (s.Signature, actor);
            if (s.Complete || s.Blocked)
                continue;
            if (found.Required)
                return (s.Signature, actor, null);
            fill ??= (s.Signature, actor);
        }

        return (fill ?? met) is { } picked ? (picked.Signature, picked.Actor, null) : (null, null, Refusals.GateSigner);
    }
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
    public static string? ActsAs(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioStep step, ActingChoice choice) =>
        Pick(model, process, cast, step, choice).Actor;

    /// <summary>
    /// The actor a step acts as (<see cref="ActsAs"/>) and, on an approval, the named signature it answers, picked together (signoff-design.md
    /// decision 8): the step's own signature, else the one the response's rule picks (<see cref="ApprovalPick"/>), each with the actor the
    /// participant gives it as by the same rule a gate uses, within that signature's signers and required actors. An approval whose signatures
    /// the participant can't give is passed over, as a gate is. The signature is <see langword="null"/> when the step names an actor or no
    /// approval was picked; the step then picks inside, once.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="process">The process that runs.</param>
    /// <param name="cast">The run's cast.</param>
    /// <param name="step">The step.</param>
    /// <param name="choice">What the step can do now.</param>
    /// <returns>The actor id and the named signature id.</returns>
    public static (string? Actor, string? Signature) Pick(ModelSnapshot model, Process process, ScenarioCast cast, ScenarioStep step, ActingChoice choice)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(choice);
        if (step.Participant is null || step.Actor is not null)
            return (step.Actor, null);
        if (cast.Participant(step.Participant) is not { } participant)
            return (null, null);
        bool Satisfied(string id) => Satisfies(model, process, cast, participant, id);
        var membership = ActorMembership.Of(model);
        // Decision 15 within a list of signers and its unsigned required actors: (a), else (b), else (c).
        (string? Actor, bool Required) ActorFor(IReadOnlyList<string> listed, IReadOnlyList<string> unsigned)
        {
            if (unsigned.FirstOrDefault(Satisfied) is { } required)
                return (required, true);
            var signers = listed.Distinct(StringComparer.Ordinal).Where(Satisfied).ToList();
            if (signers.Count == 0)
                return (null, false);
            if (choice.Actors.Count > 0 && signers.FirstOrDefault(s => choice.Actors.Any(a => membership.Accepts(a, s))) is { } accepted)
                return (accepted, false);
            return (signers[0], false);
        }

        foreach (var move in choice.Moves)
        {
            if (move.Gate is not { } gate)
                break;
            if (move.Signatures is { } needed)
            {
                (string? Actor, bool Required) For(ActingSignature s) => ActorFor(s.Signature.Signers, s.Unsigned);
                NamedSignature? signature;
                string? actor;
                if (step.Signature is { } own && gate.AllSignatures.Any(s => string.Equals(s.Id, own, StringComparison.Ordinal)))
                {
                    var found = needed.FirstOrDefault(s => string.Equals(s.Signature.Id, own, StringComparison.Ordinal));
                    (signature, actor) = found is null ? (null, null) : (found.Signature, For(found).Actor);
                }
                else
                {
                    var picked = ApprovalPick.Choose(step.Response, needed, move.Latest, For);
                    (signature, actor) = (picked.Signature, picked.Actor);
                    // A withdraw takes back the person's latest signature, as the actor they gave it as.
                    if (step.Response == SignatureResponse.Withdraw && picked.Signature is not null && move.LatestActor is { } signedAs)
                        actor = signedAs;
                }

                if (signature is not null && actor is not null)
                    return (actor, signature.Id);
                continue;
            }

            if (ActorFor(gate.Signers, move.Unsigned).Actor is { } signer)
                return (signer, null);
        }

        if (choice.Actors.Count == 0)
            return (participant.Actor, null);
        return (choice.Actors.Distinct(StringComparer.Ordinal).FirstOrDefault(Satisfied), null);
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
        // current_actor() reads the signer, else the participant's identity (object-effects-design.md decision 5).
        choice = interpreter.Acting(step, step.Signer ?? participant.IdentityText);
        var (actsAs, signature) = Pick(model, process, cast, step, choice);
        var effective = step with
        {
            Actor = actsAs,
            Signer = step.Signer ?? (step.Input == StepInput.Event ? participant.IdentityText : null),
        };
        return interpreter.Step(effective, participant.IdentityText, signature) with { ActsAs = actsAs };
    }

    /// <summary>
    /// The run's objects as a step's actor lookup reads them (object-effects-design.md decision 4, actors decision 27): the subject's values
    /// with what the run's sets gave it so far, by attribute id. The objects as given when nothing was set.
    /// </summary>
    /// <param name="cast">The run's cast.</param>
    /// <param name="values">The values the run set, by attribute id (<see cref="StatechartInterpreter.Values"/>).</param>
    /// <returns>The cast the next step reads.</returns>
    public static ScenarioCast Current(ScenarioCast cast, IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Count == 0 || cast.Subject is not { } subject || !cast.Objects.Any(o => string.Equals(o.Id, subject, StringComparison.Ordinal)))
            return cast;
        return cast with
        {
            Objects = [.. cast.Objects.Select(o => !string.Equals(o.Id, subject, StringComparison.Ordinal) ? o
                : o with { Values = o.Values.Concat(values).GroupBy(p => p.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal) })],
        };
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
        if (refusal == Refusals.GateSigner && choice.Moves.Where(m => m.Gate is not null).DistinctBy(m => m.Gate!.Id).ToList() is { Count: > 0 } gated)
        {
            // An approval's list is the signers of the signatures its round needs (signoff-design.md decision 8).
            var lists = string.Join(" or ", gated.Select(m => m.Signatures is { } needed
                ? $"the signers of approval '{m.Gate!.Name}' ({Names(model, needed.SelectMany(s => s.Signature.Signers).Distinct(StringComparer.Ordinal))})"
                : $"the signers of gate '{m.Gate!.Name}' ({Names(model, m.Gate.Signers.Distinct(StringComparer.Ordinal))})"));
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
