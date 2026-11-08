using System.Runtime.CompilerServices;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The approvals naming key, <c>ap:approvals</c> (signoff-design.md section 7): which processes use an approval, inline or through a gate
/// policy. Every output whose answer could change when a process gets its first approval keys on it, so that first approval renders and
/// replays them again. Its hash never changes in a project without approvals, as <c>rn:rights</c> does not in a project without rights.
/// </summary>
internal static class ApprovalNaming
{
    /// <summary>The key.</summary>
    public const string Key = "ap:approvals";

    private static readonly ConditionalWeakTable<ModelSnapshot, string> Hashes = new();

    /// <summary>
    /// The current hash of <see cref="Key"/>, worked out once per snapshot from the index rows: the id of each process that is no library
    /// and uses an approval, by id. A process uses one when a transition carries a gate with stages, or names a gate policy that a library
    /// row lists among its gates with stages. It changes when a process gets its first approval or loses its last, and when an unused gate
    /// policy with stages gets its first use; not when a stage or a signature changes.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <returns>The hash.</returns>
    public static string Hash(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return Hashes.GetValue(model, static m =>
        {
            var rows = m.Summaries();
            var policies = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.ApprovalGatePolicies is { } held)
                    policies.UnionWith(held);
            }

            var users = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!string.Equals(row.Kind, "process", StringComparison.Ordinal) || string.Equals(row.Use, "library", StringComparison.Ordinal))
                    continue;
                if (row.InlineApproval || (row.GatePolicyUses ?? []).Any(policies.Contains))
                    users.Add(row.Id);
            }

            using var hash = new Hashing.HashBuilder();
            hash.Add("ap");
            foreach (var id in users)
                hash.Add(id);
            return hash.Finish();
        });
    }
}
