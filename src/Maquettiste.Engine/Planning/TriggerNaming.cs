using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The trigger points naming key, <c>tn:trigger-points</c> (trigger-points-design.md section 7): which lifecycles have trigger points, the verbs
/// those points name and their replaces. Every output whose answer could change when a lifecycle gets its first trigger point keys on it, so
/// that first point renders and replays them again. Its hash never changes in a project without trigger points, as <c>rn:rights</c> does not in
/// a project without rights.
/// </summary>
internal static class TriggerNaming
{
    /// <summary>The key.</summary>
    public const string Key = "tn:trigger-points";

    private static readonly ConditionalWeakTable<ModelSnapshot, string> Hashes = new();

    /// <summary>
    /// The current hash of <see cref="Key"/>, worked out once per snapshot from the index rows: for each lifecycle that has
    /// <c>triggerPoints</c>, by id, its id, every verb its points name, and its replaces with their verbs and entities. It changes when a
    /// lifecycle gets its first trigger point or loses its last, when the verbs its points name change, or when a replace changes; not when a
    /// check's condition, a point's states or its sets change.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The hash.</returns>
    public static string Hash(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Hashes.GetValue(model, static m =>
        {
            var lifecycles = new SortedDictionary<string, ElementSummary>(StringComparer.Ordinal);
            foreach (var row in m.Summaries())
            {
                if (row.UsesTriggerPoints)
                    lifecycles[row.Id] = row;
            }

            using var hash = new Hashing.HashBuilder();
            hash.Add("tn");
            foreach (var (id, row) in lifecycles)
            {
                hash.Add(id).Add("verbs");
                foreach (var verb in row.TriggerVerbs ?? [])
                    hash.Add(verb);
                hash.Add("replaces");
                foreach (var scope in row.ReplaceScopes ?? [])
                    hash.Add(scope);
                hash.Add("/");
            }

            return hash.Finish();
        });
    }
}
