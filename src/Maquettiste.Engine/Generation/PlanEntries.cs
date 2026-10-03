using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine.Generation;

/// <summary>
/// A plan's complete file list and its counts (Generation/README.md, Plans). The dry run's writer lists every file it decides,
/// identical ones included (<see cref="FileChangeKind.Unchanged"/>, no diff); this adds the outputs of the units the plan skipped,
/// from their stored state: an owned output (<c>o:</c>) as <see cref="FileChangeKind.Kept"/>, any other as
/// <see cref="FileChangeKind.NotRendered"/>. Entries carry no content and no diff. Apply ignores all three kinds: it is fed from the
/// plan's units, and a path it may touch is already in them.
/// </summary>
internal static class PlanEntries
{
    /// <summary>The JSON name of every kind, in declaration order (the keys of <see cref="GenerationPlan.Counts"/>).</summary>
    public static readonly IReadOnlyList<(FileChangeKind Kind, string Name)> Kinds =
    [
        (FileChangeKind.Added, "added"),
        (FileChangeKind.Modified, "modified"),
        (FileChangeKind.Deleted, "deleted"),
        (FileChangeKind.Unchanged, "unchanged"),
        (FileChangeKind.HandEdited, "hand-edited"),
        (FileChangeKind.Kept, "kept"),
        (FileChangeKind.OrphanedOwned, "orphaned-owned"),
        (FileChangeKind.Conflict, "conflict"),
        (FileChangeKind.NotRendered, "not-rendered"),
    ];

    /// <summary>The decided files plus the skipped units' outputs, sorted by path, then pack, then kind (as the writer sorts).</summary>
    /// <param name="decided">The writer's decisions (identical files included).</param>
    /// <param name="units">The plan's units.</param>
    /// <returns>The entries.</returns>
    public static IReadOnlyList<FileChange> Complete(IReadOnlyList<FileChange> decided, IReadOnlyList<PlanUnit> units)
    {
        var listed = new HashSet<string>(decided.Select(c => c.Path), StringComparer.Ordinal);
        var all = new List<FileChange>(decided.Count + units.Where(u => u.Skipped).Sum(u => u.Outputs.Count));
        all.AddRange(decided);
        foreach (var unit in units)
        {
            if (!unit.Skipped)
                continue;
            var pack = unit.Pack ?? unit.Key[..Math.Max(0, unit.Key.IndexOf('/', StringComparison.Ordinal))];
            foreach (var output in unit.Outputs)
            {
                if (!listed.Add(output.Path))
                    continue;
                var kind = ManifestHashes.IsOwned(output.ManifestHash) ? FileChangeKind.Kept : FileChangeKind.NotRendered;
                all.Add(new FileChange(output.Path, kind, pack, unit.Key, output.ManifestHash, output.ManifestHash, null));
            }
        }

        all.Sort(static (a, b) =>
        {
            var c = string.CompareOrdinal(a.Path, b.Path);
            if (c == 0)
                c = string.CompareOrdinal(a.Pack, b.Pack);
            return c != 0 ? c : a.Kind.CompareTo(b.Kind);
        });
        return all;
    }

    /// <summary>The number of entries of every kind, zero included, keyed by the kind's JSON name (ordinal).</summary>
    /// <param name="changes">The plan's entries.</param>
    /// <returns>The counts.</returns>
    public static SortedDictionary<string, int> Counts(IReadOnlyList<FileChange> changes)
    {
        var byKind = new int[Kinds.Count];
        foreach (var change in changes)
            byKind[(int)change.Kind]++;
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (kind, name) in Kinds)
            counts[name] = byKind[(int)kind];
        return counts;
    }

    /// <summary>The units counted by reason (<c>new</c>, <c>forced</c>, <c>inputs</c>, ...), rendered and skipped apart (ordinal keys).</summary>
    /// <param name="units">The plan's units.</param>
    /// <returns>Rendered and skipped units by reason; a unit without a reason counts under <c>unknown</c>.</returns>
    public static (SortedDictionary<string, int> Rendered, SortedDictionary<string, int> Skipped) UnitReasons(IReadOnlyList<PlanUnit> units)
    {
        var rendered = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var unit in units)
        {
            var into = unit.Skipped ? skipped : rendered;
            var reason = unit.Reason ?? "unknown";
            into[reason] = into.GetValueOrDefault(reason) + 1;
        }

        return (rendered, skipped);
    }
}
