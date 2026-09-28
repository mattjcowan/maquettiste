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

        return [];
    }
}
