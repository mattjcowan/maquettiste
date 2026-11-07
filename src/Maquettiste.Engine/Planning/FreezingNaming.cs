using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The freezing naming key, <c>fn:freezing</c> (rights-design.md decision 28): whether the project uses freezing. An output that is written
/// only in a project that freezes (the interpreter's frozen check, the frozen state hook, a scenario test's frozen flags) keys on it, and on
/// nothing else for that answer, so an edit that leaves the answer alone renders none of them again. Its hash never changes in a project
/// that doesn't freeze, as <c>rn:rights</c> does not in a project without rights.
/// </summary>
internal static class FreezingNaming
{
    /// <summary>The key.</summary>
    public const string Key = "fn:freezing";

    private static readonly ConditionalWeakTable<ModelSnapshot, StrongBox<bool>> Answers = new();

    /// <summary>
    /// Whether the project uses freezing, worked out once per snapshot from the index rows: a process's events, transitions or inline rights
    /// name <c>freeze</c> or <c>thaw</c>; in a project that uses rights, a named right names one; a named right has <c>whileFrozen</c>; or a
    /// lifecycle that states rights has an inline right with it.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>Whether it freezes.</returns>
    public static bool Uses(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Answers.GetValue(model, static m =>
        {
            var rows = m.Summaries();
            var usesRights = false;
            foreach (var row in rows)
                usesRights |= row.UsesRights;
            foreach (var row in rows)
            {
                var right = string.Equals(row.Kind, "right", StringComparison.Ordinal);
                if (right && row.WhileFrozen)
                    return new StrongBox<bool>(true);
                if (!right && row.WhileFrozen && row.UsesRights)
                    return new StrongBox<bool>(true);
                if ((!right || usesRights) && row.Verbs is { } verbs && (verbs.Contains("freeze", StringComparer.Ordinal) || verbs.Contains("thaw", StringComparer.Ordinal))
                    && (right || string.Equals(row.Kind, "process", StringComparison.Ordinal)))
                    return new StrongBox<bool>(true);
            }

            return new StrongBox<bool>(false);
        }).Value;
    }

    /// <summary>The current hash of <see cref="Key"/>: of the flag alone.</summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The hash.</returns>
    public static string Hash(ModelSnapshot model) => Hashing.HashBuilder.Of(["fn", Uses(model) ? "freezes" : "-"]);
}
