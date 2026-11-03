using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The <c>uses</c> graph of the packs under <c>templates/</c> (scope 5.1, library packs; next-phase-2-design.md section 5.2): a light
/// read of every <c>pack.json</c>'s <c>uses</c> (a broken file reads as no uses; it is reported only when its pack loads), the closure
/// of the run packs, the checks of each entry (MQ6042, MQ6043, MQ6044, MQ6045), the failure of a pack whose closure holds a failed pack, the
/// dependency order and the used-by lists. Every list and walk is ordinal or in file order, so the result is deterministic.
/// </summary>
internal sealed class PackUses
{
    /// <summary>A uses entry names a missing, twice listed or failed pack.</summary>
    internal const string MissingUse = "MQ6042";

    /// <summary>A used pack's version misses the range, or the range does not parse.</summary>
    internal const string RangeMissed = "MQ6043";

    /// <summary>A pack uses itself or is in a cycle.</summary>
    internal const string Cycle = "MQ6044";

    /// <summary>A pack without units that nothing uses.</summary>
    internal const string UnusedLibrary = "MQ6046";

    /// <summary>A used pack's helper namespace collides with a builtin, a keyword or a variable.</summary>
    internal const string NamespaceCollision = "MQ6045";

    /// <summary>The template language's keywords and special names, which no helper namespace may take.</summary>
    internal static readonly System.Collections.Frozen.FrozenSet<string> Keywords = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal,
        "if", "else", "end", "for", "in", "while", "tablerow", "case", "when", "break", "continue", "func", "ret", "import", "readonly",
        "with", "wrap", "capture", "include", "do", "this", "empty", "null", "true", "false");

    /// <summary>The members of the template builtin object (the language's own and the Maquettiste helpers), read once.</summary>
    private static readonly Lazy<System.Collections.Frozen.FrozenSet<string>> BuiltinMembers =
        new(() => System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, [.. Rendering.BuiltinHelpers.CreateBuiltins().GetMembers()]));

    private readonly Dictionary<string, (byte[] Bytes, IReadOnlyList<string> Uses)> packs;
    private readonly Dictionary<string, HashSet<string>> reach = new(StringComparer.Ordinal);
    private readonly ModelPaths paths;

    private PackUses(ModelPaths paths, Dictionary<string, (byte[] Bytes, IReadOnlyList<string> Uses)> packs)
    {
        this.paths = paths;
        this.packs = packs;
    }

    /// <summary>Reads the <c>uses</c> of every pack folder.</summary>
    /// <param name="paths">The model paths.</param>
    /// <param name="templates">The absolute templates folder.</param>
    /// <param name="folders">The pack folders (those with a <c>pack.json</c>), ordinal.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The graph.</returns>
    public static async Task<PackUses> ReadAsync(ModelPaths paths, string templates, IReadOnlyList<string> folders, CancellationToken ct)
    {
        var packs = new Dictionary<string, (byte[], IReadOnlyList<string>)>(StringComparer.Ordinal);
        foreach (var name in folders)
        {
            ct.ThrowIfCancellationRequested();
            byte[] bytes;
            try
            {
                bytes = await File.ReadAllBytesAsync(Path.Combine(templates, name, "pack.json"), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                bytes = [];
            }

            packs[name] = (bytes, LightUses(bytes));
        }

        return new PackUses(paths, packs);
    }

    /// <summary>Whether a pack folder exists.</summary>
    /// <param name="name">The pack name.</param>
    /// <returns>Whether it has a <c>pack.json</c>.</returns>
    public bool Exists(string name) => packs.ContainsKey(name);

    /// <summary>The run packs and every pack their uses reach, ordinal.</summary>
    /// <param name="roots">The run packs.</param>
    /// <returns>The packs to load.</returns>
    public IReadOnlyList<string> Closure(IEnumerable<string> roots)
    {
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (all.Add(root))
                all.UnionWith(Reach(root));
        }

        return [.. all];
    }

    /// <summary>Every existing pack reachable from a pack through uses; the pack itself only when it is in a cycle.</summary>
    /// <param name="name">The pack name.</param>
    /// <returns>The reachable packs.</returns>
    public IReadOnlySet<string> Reach(string name)
    {
        if (reach.TryGetValue(name, out var known))
            return known;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(Direct(name).Reverse());
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next))
                continue;
            foreach (var used in Direct(next).Reverse())
                stack.Push(used);
        }

        reach[name] = seen;
        return seen;
    }

    /// <summary>The packs whose uses name a pack, ordinal, then <c>maquettiste.json</c> when the project's uses does.</summary>
    /// <param name="name">The pack name.</param>
    /// <param name="project">The project's uses.</param>
    /// <returns>The users.</returns>
    public IReadOnlyList<string> UsedBy(string name, IReadOnlyList<PackUse> project)
    {
        var users = packs.Where(p => p.Value.Uses.Contains(name, StringComparer.Ordinal)).Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        if (project.Any(u => string.Equals(u.Pack, name, StringComparison.Ordinal)))
            users.Add("maquettiste.json");
        return users;
    }

    /// <summary>
    /// Checks a loaded pack's uses entries: a name with no folder or listed twice (MQ6042), the pack itself or a cycle back into it
    /// (MQ6044, at the first entry that leads back) and a range the used pack's version misses or that does not parse (MQ6043).
    /// </summary>
    /// <param name="pack">The loaded pack.</param>
    /// <param name="loaded">The loaded packs by name.</param>
    /// <returns>The errors; any fails the pack.</returns>
    public IReadOnlyList<Diagnostic> Check(LoadedPack pack, IReadOnlyDictionary<string, LoadedPack> loaded)
    {
        var errors = new List<Diagnostic>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var cycle = false;
        var uses = pack.Manifest.Uses;
        for (var i = 0; i < uses.Count; i++)
        {
            var use = uses[i];
            var pointer = "/uses/" + i.ToString(CultureInfo.InvariantCulture);
            if (!names.Add(use.Pack))
                errors.Add(Error(pack.Name, MissingUse, $"Pack '{pack.Name}' lists '{use.Pack}' more than once in its uses.", pointer + "/pack"));
            else if (!Exists(use.Pack))
                errors.Add(Error(pack.Name, MissingUse,
                    $"Pack '{pack.Name}' uses '{use.Pack}', which does not exist: there is no templates/{use.Pack}/pack.json.", pointer + "/pack"));
            else if (string.Equals(use.Pack, pack.Name, StringComparison.Ordinal) || Reach(use.Pack).Contains(pack.Name))
            {
                if (!cycle)
                    errors.Add(Error(pack.Name, Cycle, string.Equals(use.Pack, pack.Name, StringComparison.Ordinal)
                        ? $"Pack '{pack.Name}' uses itself; remove the entry."
                        : $"Pack '{pack.Name}' is in a uses cycle: {string.Join(" -> ", CyclePath(use.Pack, pack.Name).Prepend(pack.Name))}; remove one entry.",
                        pointer + "/pack"));
                cycle = true;
            }
            else if (loaded.TryGetValue(use.Pack, out var used) && !EngineRange.Satisfies(use.Version, used.Manifest.Version, out var valid))
            {
                errors.Add(Error(pack.Name, RangeMissed, valid
                    ? $"Pack '{pack.Name}' uses '{use.Pack}' {use.Version}, but '{use.Pack}' is version {used.Manifest.Version}."
                    : $"Pack '{pack.Name}' has a uses range for '{use.Pack}' that cannot be parsed: '{use.Version}'.", pointer + "/version"));
            }
            else if (used is { Scripts.Count: > 0 } && Namespace(use.Pack) is var space && Reserved(space))
            {
                errors.Add(Error(pack.Name, NamespaceCollision,
                    $"Pack '{use.Pack}' has scripts, so its helpers are the namespace '{space}', which is a builtin, a template keyword or a template variable; rename the pack.",
                    pointer + "/pack"));
            }
        }

        return errors;
    }

    /// <summary>
    /// The MQ6042 of a pack whose closure holds a failed pack, at the first entry that leads to one; <see langword="null"/> when its
    /// closure loaded.
    /// </summary>
    /// <param name="pack">The loaded pack.</param>
    /// <param name="failed">The packs that failed.</param>
    /// <returns>The error, or <see langword="null"/>.</returns>
    public Diagnostic? ClosureFailed(LoadedPack pack, IReadOnlySet<string> failed)
    {
        var uses = pack.Manifest.Uses;
        for (var i = 0; i < uses.Count; i++)
        {
            var target = uses[i].Pack;
            var pointer = "/uses/" + i.ToString(CultureInfo.InvariantCulture) + "/pack";
            if (failed.Contains(target))
                return Error(pack.Name, MissingUse, $"Pack '{pack.Name}' uses '{target}', which did not load; fix '{target}' first.", pointer);
            if (Exists(target) && Reach(target).Where(failed.Contains).Order(StringComparer.Ordinal).FirstOrDefault() is { } broken)
                return Error(pack.Name, MissingUse,
                    $"Pack '{pack.Name}' uses '{target}', whose uses reach '{broken}', which did not load; fix '{broken}' first.", pointer);
        }

        return null;
    }

    /// <summary>The MQ6046 warning of a pack without units that nothing uses.</summary>
    /// <param name="name">The pack name.</param>
    /// <returns>The warning.</returns>
    public Diagnostic Unused(string name)
    {
        var d = Error(name, UnusedLibrary, $"Pack '{name}' has no units, and no pack and no project uses lists it; add a unit or a uses entry.", "/name");
        return d with { Severity = DiagnosticSeverity.Warning };
    }

    /// <summary>
    /// The dependency order of packs whose uses are acyclic: Kahn's order, the earliest ordinal name first among the packs that are
    /// ready; a pack's uses outside the list are ignored.
    /// </summary>
    /// <param name="names">The packs.</param>
    /// <returns>The packs in dependency order.</returns>
    public IReadOnlyList<string> Order(IEnumerable<string> names)
    {
        var set = new HashSet<string>(names, StringComparer.Ordinal);
        var waiting = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var name in set)
        {
            var deps = Direct(name).Where(d => set.Contains(d) && !string.Equals(d, name, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToList();
            waiting[name] = deps.Count;
            foreach (var dep in deps)
            {
                if (!dependents.TryGetValue(dep, out var list))
                    dependents[dep] = list = [];
                list.Add(name);
            }
        }

        var ready = new SortedSet<string>(waiting.Where(w => w.Value == 0).Select(w => w.Key), StringComparer.Ordinal);
        var order = new List<string>(set.Count);
        while (ready.Count > 0)
        {
            var next = ready.Min!;
            ready.Remove(next);
            order.Add(next);
            foreach (var dependent in dependents.GetValueOrDefault(next) ?? [])
            {
                if (--waiting[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        // A cycle is failed before ordering; any pack left (none in practice) keeps ordinal order at the end.
        order.AddRange(set.Except(order, StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return order;
    }

    /// <summary>The helper namespace of a pack: its name with <c>-</c> replaced by <c>_</c>.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The namespace.</returns>
    internal static string Namespace(string pack) => pack.Replace('-', '_');

    /// <summary>Whether a namespace is a builtin member, a template keyword or a template variable (MQ6045).</summary>
    /// <param name="space">The namespace.</param>
    /// <returns>Whether it is taken.</returns>
    internal static bool Reserved(string space) =>
        Keywords.Contains(space) || Rendering.BuiltinHelpers.Variables.Contains(space) || BuiltinMembers.Value.Contains(space);

    /// <summary>
    /// A pack's uses closure read from the <c>pack.json</c> files of that closure alone, without loading any pack (the preview
    /// session's stamp): the existing packs its uses reach, ordinal, the pack itself left out.
    /// </summary>
    /// <param name="templates">The absolute templates folder.</param>
    /// <param name="name">The pack name.</param>
    /// <returns>The used packs.</returns>
    internal static IReadOnlyList<string> ReachFromFiles(string templates, string name)
    {
        if (!Directory.Exists(templates))
            return [];
        var folders = Directory.EnumerateDirectories(templates).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var seen = new SortedSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([name]);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next))
                continue;
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(Path.Combine(templates, next, "pack.json"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var used in LightUses(bytes).Where(folders.Contains))
                stack.Push(used);
        }

        seen.Remove(name);
        return [.. seen];
    }

    private IEnumerable<string> Direct(string name) =>
        packs.TryGetValue(name, out var pack) ? pack.Uses.Where(packs.ContainsKey) : [];

    /// <summary>The shortest uses path from one pack to another, both included (breadth first, entries in file order).</summary>
    private List<string> CyclePath(string from, string to)
    {
        var previous = new Dictionary<string, string?>(StringComparer.Ordinal) { [from] = null };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var next = queue.Dequeue();
            if (string.Equals(next, to, StringComparison.Ordinal))
                break;
            foreach (var used in Direct(next))
            {
                if (previous.TryAdd(used, next))
                    queue.Enqueue(used);
            }
        }

        var path = new List<string>();
        for (string? at = to; at is not null && previous.ContainsKey(at); at = previous[at])
            path.Add(at);
        path.Reverse();
        return path;
    }

    private Diagnostic Error(string pack, string rule, string message, string pointer)
    {
        var bytes = packs.TryGetValue(pack, out var p) ? p.Bytes : [];
        var position = bytes.Length == 0 ? null : new JsonPositionLocator().Locate(bytes, pointer);
        return new Diagnostic(rule, DiagnosticSeverity.Error, message, null, paths.ToRepoPath("templates/" + pack + "/pack.json"), pointer,
            position?.Line, position?.Column);
    }

    internal static IReadOnlyList<string> LightUses(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("uses", out var uses)
                || uses.ValueKind != JsonValueKind.Array)
                return [];
            var names = new List<string>();
            foreach (var entry in uses.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("pack", out var pack) && pack.ValueKind == JsonValueKind.String)
                    names.Add(pack.GetString()!);
            }

            return names;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
