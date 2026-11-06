using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Rules that compare a proposed save with the stored model, which a snapshot alone cannot see. <c>ModelStore</c> (W1) calls
/// <see cref="Check"/> for every created or updated element before touching disk; an error makes the save <c>Invalid</c>.
/// </summary>
internal static class SaveRules
{
    /// <summary>
    /// The first own verb whose key a save changes while something names it (MQ9707, verbs-design.md decision 9): matched by id, in use
    /// when the index records a reference to it from an element the save does not also rewrite. An unused verb's key may change freely.
    /// </summary>
    /// <param name="current">The stored model.</param>
    /// <param name="before">The stored vocabulary.</param>
    /// <param name="after">The proposed vocabulary.</param>
    /// <param name="rewritten">Whether the same change rewrites an element (a batch's own updates, as <c>rename-verb</c> plans them), whose uses
    /// then do not count; <see langword="null"/> counts every use.</param>
    /// <returns>The change, or <see langword="null"/>.</returns>
    internal static (string Before, string After, int Uses, string Pointer)? RenamedVerbInUse(ModelSnapshot current, VerbVocabulary before, VerbVocabulary after,
        Func<string, bool>? rewritten)
    {
        var stored = new Dictionary<string, OwnVerb>(StringComparer.Ordinal);
        foreach (var verb in before.Verbs)
            stored.TryAdd(verb.Id, verb);
        for (var i = 0; i < after.Verbs.Count; i++)
        {
            var verb = after.Verbs[i];
            if (!stored.TryGetValue(verb.Id, out var old) || string.Equals(old.Key, verb.Key, StringComparison.Ordinal))
                continue;
            var uses = current.ReferencesTo(old.Id).Count(r => rewritten is null || !rewritten(r.FromElementId));
            if (uses > 0)
                return (old.Key, verb.Key, uses, Ptr.At("/verbs", i) + "/key");
        }

        return null;
    }

    /// <summary>
    /// Checks a proposed element against the stored one: a stereotype whose immutable <c>key</c> changes is MQ3020 (D2), because
    /// every <c>stereotypes</c> list refers to the key.
    /// </summary>
    /// <param name="current">The stored model.</param>
    /// <param name="proposed">The element about to be saved.</param>
    /// <param name="filePath">The repo-relative path the element is saved to.</param>
    /// <returns>The diagnostics; empty when the save may proceed.</returns>
    public static IReadOnlyList<Diagnostic> Check(ModelSnapshot current, Element proposed, string filePath)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(proposed);
        if (proposed is Stereotype stereotype && current.Get<Stereotype>(stereotype.Id) is { } stored && stored.Key != stereotype.Key)
        {
            var referrers = current.ReferencesTo(stored.Id).Select(r => r.FromElementId).Distinct(StringComparer.Ordinal).Count();
            var suffix = referrers == 0 ? "" : $" ({Ptr.N(referrers)} element(s) apply it)";
            return
            [
                RuleCatalog.Create("MQ3020", $"The key of stereotype '{stored.Name}' is '{stored.Key}' and cannot change to '{stereotype.Key}'{suffix}; create a new stereotype instead.",
                    stereotype.Id, filePath, "/key"),
            ];
        }

        if (proposed is VerbVocabulary verbs && current.Get<VerbVocabulary>(verbs.Id) is { } storedVerbs && RenamedVerbInUse(current, storedVerbs, verbs, null) is { } renamed)
        {
            return
            [
                RuleCatalog.Create("MQ9707", $"The key of own verb '{renamed.Before}' cannot change to '{renamed.After}' while {Ptr.N(renamed.Uses)} use(s) name it; rename it with the batch operation rename-verb, which rewrites every use.",
                    verbs.Id, filePath, renamed.Pointer),
            ];
        }

        return [];
    }
}
