using System.Collections.Frozen;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The order the seeds of one target merge in (next-phase-1-design.md section 4.3): by the qualified name of the package the seed's
/// file states (a seed in no package first), then by name, then by id, all ordinal. Without packages it is the (name, id) order. The
/// resolver concatenates a target's rows in this order (the first row of a code or key wins, the first table seed decides the row
/// key), and validation reports a duplicate on the later seed. Thread-safe: the package names are computed once, up front. No seed
/// file holds those names, so the resolver adds the packages of a seed's chain (<see cref="ChainOf"/>) to the seed's dependency keys:
/// renaming or moving one of them can reorder the seeds of a target.
/// </summary>
internal sealed class SeedOrder : IComparer<Seed>
{
    private readonly FrozenDictionary<string, string> _names;
    private readonly FrozenDictionary<string, string[]> _chains;

    /// <summary>Creates the order over a model's packages.</summary>
    /// <param name="model">The snapshot.</param>
    public SeedOrder(ModelSnapshot model)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var chains = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var package in model.All<Package>())
        {
            var parts = new List<string>();
            var chain = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var current = package;
            while (seen.Add(current.Id))
            {
                parts.Add(current.Name);
                chain.Add(current.Id);
                if (current.Parent is not { } parent)
                    break;
                if (model.Get<Package>(parent) is not { } next)
                {
                    chain.Add(parent); // an unknown parent: its creation would name the chain anew
                    break;
                }

                current = next;
            }

            parts.Reverse();
            names[package.Id] = string.Join('.', parts);
            chains[package.Id] = [.. chain];
        }

        _names = names.ToFrozenDictionary(StringComparer.Ordinal);
        _chains = chains.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>The ids of the packages whose names and parents make up the qualified name <see cref="PackageOf"/> returns: the
    /// seed's package, then its parents up to the root (an unknown id ends the chain, itself included), or none when the seed's file
    /// states no package.</summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The ids, from the seed's package up.</returns>
    public IReadOnlyList<string> ChainOf(Seed seed) =>
        seed.Package is not { } id ? [] : _chains.TryGetValue(id, out var chain) ? chain : [id];

    /// <summary>The qualified name of the package a seed's file states, or <see langword="null"/> when it states none (an unknown
    /// package id stands for itself).</summary>
    /// <param name="seed">The seed.</param>
    /// <returns>The name.</returns>
    public string? PackageOf(Seed seed) => seed.Package is { } id ? _names.GetValueOrDefault(id, id) : null;

    /// <inheritdoc/>
    public int Compare(Seed? x, Seed? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;
        var (a, b) = (PackageOf(x), PackageOf(y));
        var byPackage = a is null ? (b is null ? 0 : -1) : b is null ? 1 : string.CompareOrdinal(a, b);
        if (byPackage != 0)
            return byPackage;
        var byName = string.CompareOrdinal(x.Name, y.Name);
        return byName != 0 ? byName : string.CompareOrdinal(x.Id, y.Id);
    }
}
