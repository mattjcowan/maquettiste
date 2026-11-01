using System.Collections.Frozen;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// The layers packages declare (next-phase-1-design.md section 4.3), built once per validation over the active documents: for each
/// package its chain (itself, then each parent), whether it is checked (it or a parent declares <c>dependsOn</c>, even as an empty
/// list) and, when it is, the packages it may reference besides its own subtree (the union of <c>dependsOn</c> over it and its
/// parents), plus the packages whose <c>dependsOn</c> edges form a cycle (MQ2021).
/// </summary>
internal sealed class PackageLayers
{
    private readonly FrozenDictionary<string, string[]> _chains;
    private readonly FrozenDictionary<string, FrozenSet<string>> _allowed;
    private readonly FrozenDictionary<string, (int Component, string Names)> _cycles;
    private readonly FrozenDictionary<string, string> _names;

    private PackageLayers(
        FrozenDictionary<string, string[]> chains,
        FrozenDictionary<string, FrozenSet<string>> allowed,
        FrozenDictionary<string, (int Component, string Names)> cycles,
        FrozenDictionary<string, string> names)
    {
        _chains = chains;
        _allowed = allowed;
        _cycles = cycles;
        _names = names;
    }

    /// <summary>Whether some package is checked; without one, layering judges nothing.</summary>
    public bool AnyChecked => _allowed.Count > 0;

    /// <summary>Whether a package is checked: it or a parent declares <c>dependsOn</c>.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns><see langword="true"/> when it is.</returns>
    public bool IsChecked(string packageId) => _allowed.ContainsKey(packageId);

    /// <summary>
    /// Whether an element of package <paramref name="from"/> may reference an element of package <paramref name="to"/>: always when
    /// <paramref name="from"/> is unchecked; else when <paramref name="to"/> is <paramref name="from"/> or one of its descendants, or
    /// when <paramref name="to"/> or an ancestor of it is in the <c>dependsOn</c> of <paramref name="from"/> or of a parent.
    /// </summary>
    /// <param name="from">The referring element's package.</param>
    /// <param name="to">The referenced element's package.</param>
    /// <returns><see langword="true"/> when the reference is allowed.</returns>
    public bool Allows(string from, string to)
    {
        if (!_allowed.TryGetValue(from, out var allowed))
            return true;
        foreach (var id in ChainOf(to))
        {
            if (string.Equals(id, from, StringComparison.Ordinal) || allowed.Contains(id))
                return true;
        }

        return false;
    }

    /// <summary>A package and its parents, nearest first (a parent cycle stops at the first repeat).</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The chain; a package the model does not have is its own chain.</returns>
    public IReadOnlyList<string> ChainOf(string packageId) => _chains.TryGetValue(packageId, out var chain) ? chain : [packageId];

    /// <summary>A package's qualified name (its parents' names and its own, dotted), or the id of a package the model does not have.</summary>
    /// <param name="packageId">The package id.</param>
    /// <returns>The name.</returns>
    public string NameOf(string packageId) => _names.GetValueOrDefault(packageId, packageId);

    /// <summary>Whether the <c>dependsOn</c> edge from one package to another is part of a cycle, and the quoted names of the cycle's packages.</summary>
    /// <param name="from">The package that declares the edge.</param>
    /// <param name="to">The package it names.</param>
    /// <param name="names">The quoted qualified names of the packages caught in the cycle, comma-separated.</param>
    /// <returns><see langword="true"/> when both are in one cycle.</returns>
    public bool InCycle(string from, string to, out string names)
    {
        names = "";
        if (!_cycles.TryGetValue(from, out var a) || !_cycles.TryGetValue(to, out var b) || a.Component != b.Component)
            return false;
        names = a.Names;
        return true;
    }

    /// <summary>Builds the layers over a context's active documents.</summary>
    /// <param name="context">The validation context.</param>
    /// <returns>The layers.</returns>
    public static PackageLayers Build(ValidationContext context)
    {
        var model = context.Model;
        var packages = context.Documents.Select(d => d.Element).OfType<Package>().ToList();
        var chains = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var allowed = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
        var edges = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var package in packages)
        {
            var chain = model.VocabularyChain(package.Id).OfType<string>().ToArray();
            chains[package.Id] = chain;
            names[package.Id] = string.Join('.', Enumerable.Reverse(chain).Select(id => model.Get<Package>(id)?.Name is { Length: > 0 } name ? name : id));
            HashSet<string>? union = null;
            foreach (var id in chain)
            {
                if (model.Get<Package>(id)?.DependsOn is { } dependsOn)
                    (union ??= new HashSet<string>(StringComparer.Ordinal)).UnionWith(dependsOn);
            }

            if (union is not null)
                allowed[package.Id] = union.ToFrozenSet(StringComparer.Ordinal);
            if (package.DependsOn is { Count: > 0 } edgesOut)
                edges[package.Id] = [.. edgesOut.Distinct(StringComparer.Ordinal).Where(id => model.Get<Package>(id) is not null)];
        }

        // One ordinal depth-first pass (Tarjan) finds the packages whose dependsOn edges form a cycle; a package that names itself is
        // reported apart, so a component of one is not a cycle here.
        var cycles = new Dictionary<string, (int, string)>(StringComparer.Ordinal);
        var component = 0;
        foreach (var members in DatabaseObjectIndex.StronglyConnected(edges))
        {
            if (members.Count < 2)
                continue;
            var text = string.Join(", ", members.Select(id => names.GetValueOrDefault(id, id)).Order(StringComparer.Ordinal).Select(n => "'" + n + "'"));
            foreach (var id in members)
                cycles[id] = (component, text);
            component++;
        }

        return new PackageLayers(
            chains.ToFrozenDictionary(StringComparer.Ordinal),
            allowed.ToFrozenDictionary(StringComparer.Ordinal),
            cycles.ToFrozenDictionary(StringComparer.Ordinal),
            names.ToFrozenDictionary(StringComparer.Ordinal));
    }
}

/// <summary>
/// MQ2020 and MQ2021 (next-phase-1-design.md section 4.3). MQ2020: a reference from an element of a checked package to an element of
/// a package it may not reference (<see cref="PackageLayers.Allows"/>), reported at the reference. The element's package is the one
/// its file states (a package judges its own references as its own); every index reference counts, <c>x-ref</c> values and stereotype
/// keys included, except a package's <c>parent</c> and <c>dependsOn</c>, every <c>package</c> field (membership, a vocabulary's scope,
/// a database's convention entries), references into category trees (MQ2008 governs them along the domain chain), references from
/// diagrams, and references from or to an element whose file states no package. MQ2021: a <c>dependsOn</c> entry that names the
/// package itself or an edge of a cycle, at the entry.
/// </summary>
internal static class PackageRules
{
    public static void Validate(ValidationContext context, Report report)
    {
        var element = report.Document.Element;
        var layers = context.Layers;
        if (element is Package package)
            CheckDependsOn(layers, package, report);
        if (!layers.AnyChecked || element is Diagram)
            return;
        if (BuiltinRules.VocabularyScope(element) is not { } from || !layers.IsChecked(from))
            return;
        var model = context.Model;
        foreach (var reference in model.ReferencesFrom(element.Id))
        {
            if (reference.Field is "package" || (element is Package && reference.Field is "parent" or "dependsOn"))
                continue;
            if (!model.TryGetEntry(reference.ToId, out var entry) || model.GetDocument(entry.OwnerId)?.Element is not { } owner || owner is CategoryTree)
                continue;
            if (BuiltinRules.VocabularyScope(owner) is not { } to || layers.Allows(from, to))
                continue;
            var target = layers.NameOf(to);
            report.Add(
                "MQ2020",
                $"'{reference.Field}' references {ChangePlanner.Describe(model, reference.ToId)} in package '{target}', which package '{layers.NameOf(from)}' does not depend on: add '{target}' (or a parent of it) to the dependsOn of '{layers.NameOf(from)}' or of a parent.",
                reference.JsonPointer,
                reference.FromId);
        }
    }

    private static void CheckDependsOn(PackageLayers layers, Package package, Report report)
    {
        if (package.DependsOn is not { } dependsOn)
            return;
        for (var i = 0; i < dependsOn.Count; i++)
        {
            var pointer = Ptr.At("/dependsOn", i);
            if (string.Equals(dependsOn[i], package.Id, StringComparison.Ordinal))
                report.Add("MQ2021", $"Package '{layers.NameOf(package.Id)}' names itself in dependsOn: its own elements are always in reach; remove the entry.", pointer);
            else if (layers.InCycle(package.Id, dependsOn[i], out var names))
                report.Add("MQ2021", $"dependsOn of package '{layers.NameOf(package.Id)}' forms a cycle with '{layers.NameOf(dependsOn[i])}' (packages {names}): layers depend one way; remove one of the dependencies.", pointer);
        }
    }
}
