using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The promotion naming key, <c>pm:relation-mappings</c>: what of the relation mappings can promote a relation (add an entity and two
/// relations to <c>model.entities</c> and <c>model.relations</c>). The entity and relation lists record it instead of every relation mapping
/// file's own key: a model with a mapping per relation would otherwise put tens of thousands of keys on every entity's relation list, and
/// render those lists again on any edit to any mapping. Once a mapping promotes (or the conventions do), the lists also record every mapping
/// and database file (<c>ComputeEntityMembership</c>), as before.
/// </summary>
internal static class PromotionNaming
{
    /// <summary>The key.</summary>
    public const string Key = "pm:relation-mappings";

    private static readonly ConditionalWeakTable<ModelSnapshot, string> Hashes = new();

    /// <summary>
    /// The current hash of <see cref="Key"/>, once per snapshot: for each relation mapping by id, its id, relation, database, shape, ignore
    /// flag and promoted name. It changes when a relation mapping is added or removed or one of those changes; not when a mapping names
    /// another foreign key, junction table or column, which reaches the relation's and the tables' own keys.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The hash.</returns>
    public static string Hash(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Hashes.GetValue(model, static m =>
        {
            using var hash = new Hashing.HashBuilder();
            hash.Add("pm");
            foreach (var mapping in m.All<Mapping>().Where(x => x.Relation is not null).OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                hash.Add(mapping.Id).Add(mapping.Relation).Add(mapping.Database).Add(mapping.Shape is { } shape ? shape.ToString() : "-")
                    .Add(mapping.Ignore ? "ignore" : "-").Add(mapping.PromotedName).Add("/");
            }

            return hash.Finish();
        });
    }

    /// <summary>Whether a change between two snapshots moves the hash.</summary>
    /// <param name="before">The snapshot before.</param>
    /// <param name="after">The snapshot after.</param>
    /// <returns>Whether it differs.</returns>
    public static bool Differs(ModelSnapshot before, ModelSnapshot after) => !string.Equals(Hash(before), Hash(after), StringComparison.Ordinal);
}
