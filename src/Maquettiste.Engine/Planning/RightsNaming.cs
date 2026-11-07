using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The rights naming key, <c>rn:rights</c> (rights-design.md section 6): which rights the lifecycles state. Every output whose answer could
/// change when a lifecycle gets its first right keys on it, so that first right renders and replays them again. Its hash never changes in a
/// project without rights, as <c>sn:subjects</c> does not in a model with no stereotype entry.
/// </summary>
internal static class RightsNaming
{
    /// <summary>The key.</summary>
    public const string Key = "rn:rights";

    private static readonly ConditionalWeakTable<ModelSnapshot, string> Hashes = new();

    /// <summary>
    /// The current hash of <see cref="Key"/>, worked out once per snapshot from the index rows: for each process that writes <c>rights</c> or
    /// <c>rightsDefault</c>, by id, its id, its default as written, the verbs its deny rights name (its own, and those of the deny named
    /// rights it uses), and the named rights it uses; then the id of every named right. It changes when a lifecycle gets its first right or
    /// loses its last, changes its default, or changes which verbs it can deny; not when a right's states, actors or condition change.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The hash.</returns>
    public static string Hash(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Hashes.GetValue(model, static m =>
        {
            var rows = m.Summaries();
            var named = new SortedDictionary<string, ElementSummary>(StringComparer.Ordinal);
            var processes = new SortedDictionary<string, ElementSummary>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (string.Equals(row.Kind, "right", StringComparison.Ordinal))
                    named[row.Id] = row;
                else if (row.UsesRights)
                    processes[row.Id] = row;
            }

            using var hash = new Hashing.HashBuilder();
            hash.Add("rn");
            foreach (var (id, row) in processes)
            {
                hash.Add(id).Add(row.RightsDefault ?? "");
                var denies = new SortedSet<string>(row.DenyVerbs ?? [], StringComparer.Ordinal);
                foreach (var use in row.RightUses ?? [])
                {
                    if (named.TryGetValue(use, out var right) && string.Equals(right.Access, "deny", StringComparison.Ordinal))
                        denies.UnionWith(right.Verbs ?? []);
                }

                hash.Add("deny");
                foreach (var verb in denies)
                    hash.Add(verb);
                hash.Add("uses");
                foreach (var use in row.RightUses ?? [])
                    hash.Add(use);
                hash.Add("/");
            }

            hash.Add("named");
            foreach (var id in named.Keys)
                hash.Add(id);
            return hash.Finish();
        });
    }
}
