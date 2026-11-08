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
    /// and uses an approval, by id, each followed by a mark for each part 2 group it uses, inline or through a gate policy (signoff-design.md
    /// decision 32). A process uses one when a transition carries a gate with stages, or names a gate policy that a library row lists among
    /// its gates with stages. It changes when a process gets its first approval or loses its last, when an unused gate policy with stages
    /// gets its first use, and when a process starts or stops using a group; not when a stage or a signature changes otherwise.
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
            var policyGroups = new Dictionary<string, ApprovalGroups>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (row.ApprovalGatePolicies is { } held)
                    policies.UnionWith(held);
                foreach (var (policy, groups) in row.ApprovalPolicyGroups ?? [])
                    policyGroups[policy] = policyGroups.GetValueOrDefault(policy) | groups;
            }

            var users = new SortedDictionary<string, ApprovalGroups>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!string.Equals(row.Kind, "process", StringComparison.Ordinal) || string.Equals(row.Use, "library", StringComparison.Ordinal))
                    continue;
                var used = (row.GatePolicyUses ?? []).Where(policies.Contains).ToList();
                if (row.InlineApproval || used.Count > 0)
                    users[row.Id] = used.Aggregate(row.InlineApprovalGroups, (groups, policy) => groups | policyGroups.GetValueOrDefault(policy));
            }

            using var hash = new Hashing.HashBuilder();
            hash.Add("ap");
            foreach (var (id, groups) in users)
            {
                hash.Add(id);
                // A lifecycle that uses no part 2 group hashes as in part 1 (signoff-design.md decision 32).
                foreach (var mark in ApprovalGroupMarks.Marks(groups))
                    hash.Add(mark);
            }

            return hash.Finish();
        });
    }
}
