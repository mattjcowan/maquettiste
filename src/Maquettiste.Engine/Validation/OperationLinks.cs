using Maquettiste.Engine.Model;
using Maquettiste.Engine.Processes;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// What the rules on processes linked to operations share (processes-operations-design.md sections 4 and 8): whether an operation's subject
/// lines up with a process's subjects (MQ3032), how a member or an attribute is named in messages, and whether MQ9024 is on. Pure functions of
/// the snapshot.
/// </summary>
internal static class OperationLinks
{
    /// <summary>The rule an event that no operation fires reports, off unless <c>validation.rules</c> gives it a severity.</summary>
    internal const string UnfiredEvent = "MQ9024";

    /// <summary>
    /// Whether MQ9024 is on: <c>validation.rules</c> gives it a severity (decision 4, as MQ7205). While it is off, the validator neither runs it
    /// nor adds the peers it needs.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>Whether it is on.</returns>
    internal static bool UnfiredEventsOn(ModelSnapshot model) =>
        model.Settings.Validation.Rules.TryGetValue(UnfiredEvent, out var setting) && setting is "error" or "warning" or "info";

    /// <summary>
    /// Whether an operation's subject lines up with a process (decision 2): the process governs it (<see cref="SubjectCoverage.Governs"/>,
    /// derived entities and stereotype entries included), or governs some entity derived from it. Entities have one base, so an entity entry
    /// derived from the subject, or a carrier of a stereotype entry derived from it, is the only other way.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="process">The process, which has subjects.</param>
    /// <param name="subject">The operation's subject.</param>
    /// <returns>Whether they line up.</returns>
    internal static bool LinesUp(ModelSnapshot model, Process process, Entity subject)
    {
        if (SubjectCoverage.Governs(model, process, subject))
            return true;
        foreach (var entry in process.Subjects)
        {
            if (entry.Entity is { } id && model.Get<Entity>(id) is { } listed && SubjectCoverage.Chain(model, listed).Contains(subject.Id, StringComparer.Ordinal))
                return true;
            if (entry.Stereotype is { } key && SubjectCoverage.Of(model).CarriersOf(key).Any(c => SubjectCoverage.Chain(model, c).Contains(subject.Id, StringComparer.Ordinal)))
                return true;
        }

        return false;
    }

    /// <summary>A process's subjects as messages write them: <c>'Invoice'</c>, <c>'Invoice' and 'Credit note'</c>, a stereotype entry by its key.</summary>
    internal static string Subjects(ModelSnapshot model, Process process)
    {
        var names = process.Subjects.Select(s => "'" + (s.Entity is { } id ? model.Get<Entity>(id)?.Name ?? id : s.Stereotype ?? s.Id) + "'").ToList();
        return names.Count switch
        {
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>A type reference as messages write it: its keyword, or the name of the element it names.</summary>
    internal static string TypeText(ModelSnapshot model, TypeRef? type) =>
        type?.Builtin ?? (type?.Ref is { } id ? model.Get<Element>(id)?.Name ?? id : "no type");

    /// <summary>Whether two type references name the same type: the same built-in keyword, or the same element (decision 3).</summary>
    internal static bool SameType(TypeRef? a, TypeRef? b) =>
        a?.Builtin is { } keyword ? string.Equals(keyword, b?.Builtin, StringComparison.Ordinal) : a?.Ref is { } id && string.Equals(id, b?.Ref, StringComparison.Ordinal);

    /// <summary>An operation member's form as messages write it: a file, the key of an entity, or its type, with "list of" for a collection.</summary>
    internal static string MemberText(ModelSnapshot model, OperationMember member)
    {
        var text = member.File ? "a file"
            : member.KeyOf is { } entityId ? "the key of '" + (model.Get<Entity>(entityId)?.Name ?? entityId) + "'"
            : TypeText(model, member.Type);
        return member.Collection ? "a list of " + text : text;
    }
}
