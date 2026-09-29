using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Localization;

/// <summary>A Markdown sidecar a translation entry references: its model path, its text (<see langword="null"/> when missing) and hash.</summary>
/// <param name="ModelPath">The model-relative path.</param>
/// <param name="Text">The Markdown, LF line ends; <see langword="null"/> when the file does not exist.</param>
/// <param name="Hash">The content hash, or <c>absent</c>.</param>
public sealed record LocaleSidecar(string ModelPath, string? Text, string Hash);

/// <summary>A loaded locale shard (reference-types-seeds-localization.md section 3.3).</summary>
/// <param name="Shard">The shard.</param>
/// <param name="Path">The repo-relative path.</param>
/// <param name="ModelPath">The model-relative path.</param>
/// <param name="Hash">The file's content hash (its ETag).</param>
/// <param name="DependencyHash">The content hash combined with its sidecars' hashes.</param>
/// <param name="Sidecars">The description sidecars by the <c>file</c> value that names them.</param>
public sealed record LocaleShardDocument(LocaleShard Shard, string Path, string ModelPath, string Hash, string DependencyHash,
    IReadOnlyDictionary<string, LocaleSidecar> Sidecars)
{
    /// <summary>The locale folder the file sits in (<c>model/locales/&lt;locale&gt;/</c>), or <c>""</c> outside it.</summary>
    public string FolderLocale => FolderLocaleOf(ModelPath);

    /// <summary>The locale folder of a model path, or <c>""</c>.</summary>
    /// <param name="modelPath">The path.</param>
    /// <returns>The folder locale.</returns>
    public static string FolderLocaleOf(string modelPath)
    {
        var segments = modelPath.Split('/');
        return segments.Length >= 4 && segments[0] == "model" && segments[1] == "locales" ? segments[2] : "";
    }

    /// <summary>Resolves a translated description's <c>file</c>: relative to <c>model/locales/&lt;locale&gt;/</c> (section 3.4), or
    /// to the shard's folder for a shard outside the locales folder.</summary>
    /// <param name="shardModelPath">The shard's model path.</param>
    /// <param name="file">The reference.</param>
    /// <returns>The sidecar's model path, or <see langword="null"/> when it leaves the model.</returns>
    public static string? ResolveSidecar(string shardModelPath, string file)
    {
        var locale = FolderLocaleOf(shardModelPath);
        return ModelPaths.ResolveSidecar(locale.Length > 0 ? "model/locales/" + locale + "/_.json" : shardModelPath, file);
    }
}

/// <summary>One localizable node (section 3.1) with its default-locale source texts.</summary>
/// <param name="Id">The node id.</param>
/// <param name="OwnerId">The element whose file holds it.</param>
/// <param name="Kind">The node kind a <c>require</c> entry names (<c>entity</c>, <c>attribute</c>, <c>reference-row</c>...).</param>
/// <param name="Scope">The shard scope: a package id, <c>root</c> or <c>reference-data</c>.</param>
/// <param name="DisplayName">The display name source (a row has none).</param>
/// <param name="PluralName">The plural name source, when the node has a plural.</param>
/// <param name="Label">The label source of a reference row.</param>
/// <param name="Description">The description source, when the default has one.</param>
/// <param name="HasPluralField">Whether a plural name is read for the node.</param>
/// <param name="ExplicitPluralOnToOne">A to-one end whose default file states a plural name (MQ7211).</param>
public sealed record LocalizableNode(string Id, string OwnerId, string Kind, string Scope, string? DisplayName, string? PluralName, string? Label,
    string? Description, bool HasPluralField, bool ExplicitPluralOnToOne)
{
    /// <summary>The source text of a field, or <see langword="null"/> when the node has no such field (or no default text).</summary>
    /// <param name="field">The field name.</param>
    /// <returns>The text.</returns>
    public string? Source(string field) => field switch
    {
        LocalizationIndex.DisplayNameField => DisplayName,
        LocalizationIndex.PluralNameField => PluralName,
        LocalizationIndex.LabelField => Label,
        LocalizationIndex.DescriptionField => Description,
        _ => null,
    };

    /// <summary>Whether a translation of the field may exist for the node.</summary>
    /// <param name="field">The field name.</param>
    /// <returns><see langword="true"/> when allowed.</returns>
    public bool Allows(string field) => field switch
    {
        LocalizationIndex.DisplayNameField => DisplayName is not null,
        LocalizationIndex.PluralNameField => HasPluralField,
        LocalizationIndex.LabelField => Kind == "reference-row",
        LocalizationIndex.DescriptionField => true,
        _ => false,
    };

    /// <summary>The fields completeness expects: display name, plural (where marked), label (rows), description (when the default has one).</summary>
    public IEnumerable<string> ExpectedFields()
    {
        if (DisplayName is not null)
            yield return LocalizationIndex.DisplayNameField;
        if (HasPluralField)
            yield return LocalizationIndex.PluralNameField;
        if (Label is not null)
            yield return LocalizationIndex.LabelField;
        if (Description is not null)
            yield return LocalizationIndex.DescriptionField;
    }
}

/// <summary>The state of one translated field.</summary>
public enum TranslationState
{
    /// <summary>Translated from the current default text.</summary>
    Translated,

    /// <summary>No translation in the locale itself.</summary>
    Missing,

    /// <summary>Translated from a default text that has changed since (<c>src</c> differs).</summary>
    Stale,
}

/// <summary>Completeness of one (locale, shard) for the <c>require</c> kinds.</summary>
/// <param name="Locale">The locale.</param>
/// <param name="Scope">The shard scope.</param>
/// <param name="ShardPath">The model path the shard has (derived from the scope).</param>
/// <param name="Expected">Expected fields.</param>
/// <param name="Translated">Translated and current.</param>
/// <param name="Missing">Missing.</param>
/// <param name="Stale">Stale.</param>
public sealed record ShardCompleteness(string Locale, string Scope, string ShardPath, int Expected, int Translated, int Missing, int Stale);

/// <summary>
/// The localization view of a snapshot (reference-types-seeds-localization.md section 3): the settings, the loaded shards, the
/// effective entry of each (locale, node), the localizable nodes with their source texts, completeness and the <c>l:</c> hashes.
/// Built lazily, once per snapshot; every member is safe for concurrent reads.
/// </summary>
public sealed class LocalizationIndex
{
    /// <summary><c>displayName</c>.</summary>
    public const string DisplayNameField = "displayName";

    /// <summary><c>pluralName</c>.</summary>
    public const string PluralNameField = "pluralName";

    /// <summary><c>label</c>.</summary>
    public const string LabelField = "label";

    /// <summary><c>description</c>.</summary>
    public const string DescriptionField = "description";

    /// <summary>The translatable fields in canonical order.</summary>
    public static readonly IReadOnlyList<string> Fields = [DisplayNameField, PluralNameField, LabelField, DescriptionField];

    /// <summary>The scope of the elements with no package.</summary>
    public const string RootScope = "root";

    /// <summary>The scope of reference types, their seeds and rows.</summary>
    public const string ReferenceDataScope = "reference-data";

    private readonly ModelSnapshot _model;
    private readonly Dictionary<string, Dictionary<string, (TranslationEntry Entry, LocaleShardDocument Shard)>> _byLocale = new(StringComparer.Ordinal);
    private readonly List<(LocaleShardDocument Shard, string Reason)> _ignored = [];
    private readonly List<(string Locale, string Id, LocaleShardDocument Kept, LocaleShardDocument Other)> _duplicates = [];
    private readonly Lazy<Dictionary<string, LocalizableNode>> _nodes;
    private readonly Lazy<Dictionary<string, string>> _packageFiles;
    private readonly ConcurrentDictionary<string, Dictionary<string, List<string>>> _owners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, string), string> _ownerHashes = new();
    private readonly ConcurrentDictionary<(string Id, string Field), string> _sourceHashes = new();

    internal LocalizationIndex(ModelSnapshot model, IReadOnlyList<LocaleShardDocument> shards)
    {
        _model = model;
        Settings = model.Settings.Localization;
        Shards = [.. shards.OrderBy(s => s.ModelPath, StringComparer.Ordinal)];
        Locales = LocaleChains.Ordered(Settings);
        _nodes = new Lazy<Dictionary<string, LocalizableNode>>(BuildNodes, LazyThreadSafetyMode.ExecutionAndPublication);
        _packageFiles = new Lazy<Dictionary<string, string>>(BuildPackageFiles, LazyThreadSafetyMode.ExecutionAndPublication);
        if (Settings is null)
            return;
        foreach (var locale in Locales.Skip(1))
            _byLocale[locale] = new Dictionary<string, (TranslationEntry, LocaleShardDocument)>(StringComparer.Ordinal);
        foreach (var shard in Shards)
        {
            var folder = shard.FolderLocale;
            if (folder.Length > 0 && !IsTranslated(folder))
            {
                _ignored.Add((shard, string.Equals(folder, Settings.DefaultLocale, StringComparison.Ordinal)
                    ? $"The folder of the default locale '{folder}' holds no translations; its files are not loaded."
                    : $"The locale '{folder}' is not declared in the project's localization settings; its files are not loaded."));
                continue;
            }

            if (!_byLocale.TryGetValue(shard.Shard.Locale, out var map))
                continue; // an undeclared declared locale: MQ7210 reports the mismatch with the folder
            foreach (var (id, entry) in shard.Shard.Entries)
            {
                if (map.TryGetValue(id, out var kept))
                    _duplicates.Add((shard.Shard.Locale, id, kept.Shard, shard));
                else
                    map[id] = (entry, shard);
            }
        }
    }

    /// <summary>The settings, or <see langword="null"/> when the project declares no localization.</summary>
    public LocalizationSettings? Settings { get; }

    /// <summary>Whether the project declares localization.</summary>
    public bool Enabled => Settings is not null;

    /// <summary>The declared locales: the default first, then ordinal.</summary>
    public IReadOnlyList<string> Locales { get; }

    /// <summary>Every loaded shard file, ordinal by path (including ignored ones).</summary>
    public IReadOnlyList<LocaleShardDocument> Shards { get; }

    /// <summary>Shards that are not loaded, with the reason (MQ7202).</summary>
    internal IReadOnlyList<(LocaleShardDocument Shard, string Reason)> Ignored => _ignored;

    /// <summary>Ids held by two shards of one locale (MQ7209): the ordinally first path is kept.</summary>
    internal IReadOnlyList<(string Locale, string Id, LocaleShardDocument Kept, LocaleShardDocument Other)> Duplicates => _duplicates;

    /// <summary>The localizable nodes by id.</summary>
    public IReadOnlyDictionary<string, LocalizableNode> Nodes => _nodes.Value;

    /// <summary>Whether a locale is declared and is not the default (so it has shards).</summary>
    /// <param name="locale">The locale.</param>
    /// <returns><see langword="true"/> for a translated locale.</returns>
    public bool IsTranslated(string locale) =>
        Settings is { } s && !string.Equals(locale, s.DefaultLocale, StringComparison.Ordinal) && s.Locales.Contains(locale, StringComparer.Ordinal);

    /// <summary>The fallback chain of a locale (section 3.7), ending with the default; just the locale without settings.</summary>
    /// <param name="locale">The locale.</param>
    /// <returns>The chain.</returns>
    public IReadOnlyList<string> ChainOf(string locale) => Settings is { } s ? LocaleChains.Chain(s, locale) : [locale];

    /// <summary>The effective entry of a node in one locale (no fallback), with the shard that holds it.</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="id">The node id.</param>
    /// <param name="entry">The entry.</param>
    /// <param name="shard">The shard.</param>
    /// <returns><see langword="true"/> when the locale has an entry for the node.</returns>
    public bool TryGetEntry(string locale, string id, out TranslationEntry entry, out LocaleShardDocument shard)
    {
        if (_byLocale.TryGetValue(locale, out var map) && map.TryGetValue(id, out var found))
        {
            (entry, shard) = found;
            return true;
        }

        entry = null!;
        shard = null!;
        return false;
    }

    /// <summary>Every effective entry of a locale, ordinal by id.</summary>
    /// <param name="locale">The locale.</param>
    /// <returns>The entries.</returns>
    public IEnumerable<(string Id, TranslationEntry Entry, LocaleShardDocument Shard)> EntriesOf(string locale) =>
        _byLocale.TryGetValue(locale, out var map)
            ? map.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => (p.Key, p.Value.Entry, p.Value.Shard))
            : [];

    /// <summary>A node's translated text of one field in one locale (a sidecar description read), or <see langword="null"/>.</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="id">The node id.</param>
    /// <param name="field">The field.</param>
    /// <returns>The text.</returns>
    public string? Text(string locale, string id, string field)
    {
        if (!TryGetEntry(locale, id, out var entry, out var shard))
            return null;
        return field switch
        {
            DisplayNameField => entry.DisplayName,
            PluralNameField => entry.PluralName,
            LabelField => entry.Label,
            DescriptionField => entry.Description is { } d ? d.Text ?? (d.File is { } f && shard.Sidecars.TryGetValue(f, out var s) ? s.Text : null) : null,
            _ => null,
        };
    }

    /// <summary>Whether an entry states a field.</summary>
    internal static bool Has(TranslationEntry entry, string field) => field switch
    {
        DisplayNameField => entry.DisplayName is { Length: > 0 },
        PluralNameField => entry.PluralName is { Length: > 0 },
        LabelField => entry.Label is { Length: > 0 },
        DescriptionField => entry.Description is not null,
        _ => false,
    };

    /// <summary>The state of a node's field in a locale (section 3.3): missing, stale (its <c>src</c> hash differs) or translated.</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="node">The node.</param>
    /// <param name="field">The field.</param>
    /// <returns>The state.</returns>
    public TranslationState StateOf(string locale, LocalizableNode node, string field)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (!TryGetEntry(locale, node.Id, out var entry, out _) || !Has(entry, field))
            return TranslationState.Missing;
        return entry.Src.TryGetValue(field, out var src) && node.Source(field) is { } source && !string.Equals(src, SourceHashOf(node, field, source), StringComparison.Ordinal)
            ? TranslationState.Stale
            : TranslationState.Translated;
    }

    /// <summary>The source fingerprint: the first 8 hex digits of SHA-256 over the UTF-8 text, line ends normalized to LF.</summary>
    /// <param name="text">The default-locale text.</param>
    /// <returns>The fingerprint.</returns>
    public static string SourceHash(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..8];
    }

    /// <summary>The source fingerprint of a node's field, computed once per snapshot (every locale compares against the same one).</summary>
    private string SourceHashOf(LocalizableNode node, string field, string source) =>
        _nodes.IsValueCreated && _nodes.Value.TryGetValue(node.Id, out var known) && ReferenceEquals(known, node)
            ? _sourceHashes.GetOrAdd((node.Id, field), static (_, text) => SourceHash(text), source)
            : SourceHash(source);

    /// <summary>Completeness per (locale, shard) for the <c>require</c> kinds, locales in unit order, shards ordinal by path.</summary>
    /// <param name="locale">One locale, or <see langword="null"/> for every translated locale.</param>
    /// <returns>One row per (locale, shard) that expects at least one field.</returns>
    public IReadOnlyList<ShardCompleteness> Completeness(string? locale = null)
    {
        if (Settings is not { } settings)
            return [];
        var require = settings.Require.Count == 0 ? null : settings.Require.ToHashSet(StringComparer.Ordinal);
        var locales = Locales.Skip(1).Where(l => locale is null || string.Equals(l, locale, StringComparison.Ordinal)).ToList();
        var perLocale = new List<ShardCompleteness>[locales.Count];
        Parallel.For(0, locales.Count, i => perLocale[i] = CompletenessOf(locales[i], require));
        return [.. perLocale.SelectMany(r => r)];
    }

    private List<ShardCompleteness> CompletenessOf(string l, HashSet<string>? require)
    {
        var counts = new SortedDictionary<string, (string Scope, int Expected, int Translated, int Missing, int Stale)>(StringComparer.Ordinal);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in Nodes.Values)
        {
            if (require is not null && !require.Contains(node.Kind))
                continue;
            if (!paths.TryGetValue(node.Scope, out var path))
                paths[node.Scope] = path = ShardPath(l, node.Scope);
            var c = counts.TryGetValue(path, out var known) ? known : (Scope: node.Scope, Expected: 0, Translated: 0, Missing: 0, Stale: 0);
            foreach (var field in node.ExpectedFields())
            {
                c.Expected++;
                switch (StateOf(l, node, field))
                {
                    case TranslationState.Missing: c.Missing++; break;
                    case TranslationState.Stale: c.Stale++; break;
                    default: c.Translated++; break;
                }
            }

            counts[path] = c;
        }

        return [.. counts.Where(p => p.Value.Expected > 0)
            .Select(p => new ShardCompleteness(l, p.Value.Scope, p.Key, p.Value.Expected, p.Value.Translated, p.Value.Missing, p.Value.Stale))];
    }

    /// <summary>
    /// The <c>l:&lt;locale&gt;:&lt;owner&gt;</c> hash: the owner's entries in that locale (every field of the owner and its
    /// sub-elements, ordinal by id) and their description sidecars.
    /// </summary>
    /// <param name="locale">The locale.</param>
    /// <param name="ownerId">The owner element id.</param>
    /// <returns>The hash.</returns>
    public string OwnerHash(string locale, string ownerId) => _ownerHashes.GetOrAdd((locale, ownerId), key =>
    {
        using var hash = new HashBuilder();
        hash.Add(key.Item1).Add(key.Item2);
        foreach (var id in OwnersOf(key.Item1).GetValueOrDefault(key.Item2) ?? [])
        {
            var (entry, shard) = _byLocale[key.Item1][id];
            hash.Add(id).Add(entry.DisplayName).Add(entry.PluralName).Add(entry.Label).Add(entry.Description?.Text).Add(entry.Description?.File);
            if (entry.Description?.File is { } file)
                hash.Add(shard.Sidecars.TryGetValue(file, out var sidecar) ? sidecar.Hash : "absent");
            foreach (var field in Fields)
                hash.Add(entry.Src.GetValueOrDefault(field));
        }

        return hash.Finish();
    });

    private Dictionary<string, List<string>> OwnersOf(string locale) => _owners.GetOrAdd(locale, l =>
    {
        var owners = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        if (!_byLocale.TryGetValue(l, out var map))
            return owners;
        foreach (var id in map.Keys.Order(StringComparer.Ordinal))
        {
            if (!_model.TryGetEntry(id, out var indexEntry))
                continue;
            if (!owners.TryGetValue(indexEntry.OwnerId, out var list))
                owners[indexEntry.OwnerId] = list = [];
            list.Add(id);
        }

        return owners;
    });

    // ---- shards: scope and file name ----

    /// <summary>The shard scope of an element (section 3.1): a package's own id; <c>reference-data</c> for reference types and their
    /// seeds; a seed's target's package; the element's package; else <c>root</c>. A package that does not exist gives <c>root</c>.</summary>
    /// <param name="element">The owning element.</param>
    /// <returns>The scope.</returns>
    public string ScopeOf(Element element) => ScopeOf(element, _model.Get<Element>);

    /// <summary>The shard scope of an element, looking elements up through <paramref name="find"/> (a candidate model).</summary>
    /// <param name="element">The owning element.</param>
    /// <param name="find">Finds an element by id.</param>
    /// <returns>The scope.</returns>
    public static string ScopeOf(Element element, Func<string, Element?> find)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(find);
        string? package = element switch
        {
            Package p => p.Id,
            ReferenceType => ReferenceDataScope,
            Seed s => find(s.Target) switch
            {
                ReferenceType => ReferenceDataScope,
                Entity e => e.Package,
                Relation r => r.Package,
                _ => null,
            },
            Entity e => e.Package,
            ValueObject v => v.Package,
            ScalarType st => st.Package,
            EnumType en => en.Package,
            Relation r => r.Package,
            _ => null,
        };
        if (package == ReferenceDataScope)
            return package;
        return package is not null && find(package) is Package ? package : RootScope;
    }

    /// <summary>The shard file name of a scope without <c>.json</c>: the package's kebab path (sibling collisions suffixed with the
    /// last 6 characters of the id, all but the ordinally first id), <c>_root</c> or <c>_reference-data</c>; <see langword="null"/>
    /// for a package id the model does not have.</summary>
    /// <param name="scope">The scope.</param>
    /// <returns>The name.</returns>
    public string? ShardFileOf(string scope) => scope switch
    {
        RootScope => "_root",
        ReferenceDataScope => "_reference-data",
        _ => _packageFiles.Value.GetValueOrDefault(scope),
    };

    /// <summary>The model path of a (locale, scope) shard.</summary>
    /// <param name="locale">The locale.</param>
    /// <param name="scope">The scope.</param>
    /// <returns>The path; an unknown package falls back to <c>_root</c>.</returns>
    public string ShardPath(string locale, string scope) => PathOf(locale, ShardFileOf(scope) ?? "_root");

    /// <summary>The model path of a shard file name in a locale.</summary>
    public static string PathOf(string locale, string file) => "model/locales/" + locale + "/" + file + ".json";

    /// <summary>The kebab paths of every package, computed as <see cref="ShardFileOf"/> describes.</summary>
    internal static Dictionary<string, string> PackageFiles(IEnumerable<Package> packages)
    {
        var all = packages.ToDictionary(p => p.Id, StringComparer.Ordinal);
        var segment = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in all.Values.GroupBy(p => (p.Parent ?? "", Casing.Kebab(p.Name))))
        {
            var first = true;
            foreach (var package in group.OrderBy(p => p.Id, StringComparer.Ordinal))
            {
                segment[package.Id] = first ? group.Key.Item2 : group.Key.Item2 + ModelPaths.Suffix(package.Id);
                first = false;
            }
        }

        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var package in all.Values)
        {
            var parts = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var p = package; p is not null && seen.Add(p.Id); p = p.Parent is { } parent ? all.GetValueOrDefault(parent) : null)
                parts.Add(segment[p.Id]);
            parts.Reverse();
            paths[package.Id] = string.Join('/', parts);
        }

        return paths;
    }

    private Dictionary<string, string> BuildPackageFiles() => PackageFiles(_model.All<Package>());

    // ---- nodes ----

    private Dictionary<string, LocalizableNode> BuildNodes()
    {
        var nodes = new Dictionary<string, LocalizableNode>(StringComparer.Ordinal);
        if (Settings is null)
            return nodes;
        var scopes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var document in _model.Documents)
        {
            var element = document.Element;
            if (!LocaleChains.ElementKinds.Contains(element.Kind))
                continue;
            var scope = ScopeOf(element);
            scopes[element.Id] = scope;
            var display = element.DisplayName ?? element.Name;
            var description = element.Description is { } d ? d.Text ?? document.SidecarText ?? "file:" + d.File : null;
            nodes[element.Id] = new LocalizableNode(element.Id, element.Id, element.KindName, scope, display, element.PluralName ?? display, null,
                description, true, false);
        }

        var seeds = new Dictionary<string, (Seed Seed, int Label, int Description, Dictionary<string, SeedRow> Rows)?>(StringComparer.Ordinal);
        foreach (var entry in _model.Index.Entries.Values)
        {
            if (entry.OwnerId == entry.Id || !scopes.TryGetValue(entry.OwnerId, out var scope))
                continue;
            var document = _model.GetDocument(entry.OwnerId);
            if (document is null)
                continue;
            if (entry.Kind == "row")
            {
                if (!seeds.TryGetValue(entry.OwnerId, out var seed))
                    seeds[entry.OwnerId] = seed = document.Element is Seed s && _model.Get<ReferenceType>(s.Target) is not null
                        ? (s, IndexOf(s.Columns, "label"), IndexOf(s.Columns, "description"), s.Rows.ToDictionary(r => r.Id, StringComparer.Ordinal))
                        : null;
                if (seed is not { } rowSeed || !rowSeed.Rows.TryGetValue(entry.Id, out var row))
                    continue;
                nodes[entry.Id] = new LocalizableNode(entry.Id, entry.OwnerId, "reference-row", scope, null, null,
                    Cell(row, rowSeed.Label), Cell(row, rowSeed.Description), false, false);
                continue;
            }

            if (entry.Kind is not ("attribute" or "end" or "enum-member" or "reference-field" or "category"))
                continue;
            if (!Json.JsonPointer.TryGet(document.Json, entry.JsonPointer, out var node) || node.ValueKind != JsonValueKind.Object)
                continue;
            var name = String(node, "name") ?? String(node, "role");
            var display = String(node, "displayName") ?? name ?? (entry.Kind == "reference-field" ? DefaultFieldName(entry.JsonPointer) : null);
            var plural = String(node, "pluralName");
            var description = node.TryGetProperty("description", out var d) ? d.ValueKind switch
            {
                JsonValueKind.String => d.GetString(),
                JsonValueKind.Object when d.TryGetProperty("file", out var f) && f.ValueKind == JsonValueKind.String => "file:" + f.GetString(),
                _ => null,
            } : null;
            var toMany = entry.Kind != "end" || !node.TryGetProperty("max", out var max) || max.ValueKind != JsonValueKind.Number;
            var hasPlural = entry.Kind == "category" || (entry.Kind == "end" && toMany);
            nodes[entry.Id] = new LocalizableNode(entry.Id, entry.OwnerId, entry.Kind, scope, display, hasPlural ? plural ?? display : null, null,
                description, hasPlural, entry.Kind == "end" && !toMany && plural is not null);
        }

        return nodes;
    }

    private static string DefaultFieldName(string pointer) => pointer.EndsWith("/label", StringComparison.Ordinal) ? "Label" : "Code";

    private static int IndexOf(IReadOnlyList<string> columns, string name)
    {
        for (var i = 0; i < columns.Count; i++)
        {
            if (string.Equals(columns[i], name, StringComparison.Ordinal))
                return i;
        }

        return -1;
    }

    private static string? Cell(SeedRow row, int index) =>
        index >= 0 && index < row.Values.Count && row.Values[index].ValueKind == JsonValueKind.String ? row.Values[index].GetString() : null;

    private static string? String(JsonElement node, string property) =>
        node.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
