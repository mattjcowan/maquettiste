using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Localization;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Model;

/// <summary>
/// An immutable, thread-safe snapshot of a loaded model with its id, name and reverse-reference indexes
/// (engine-design.md section 2.6). Built by <see cref="Create"/>, which is pure.
/// </summary>
public sealed class ModelSnapshot
{
    // Plain dictionaries rather than frozen ones: a snapshot is built on every load (every incremental run), and freezing a few
    // hundred thousand ids costs more than the faster lookups save. Never written after construction, so concurrent reads are safe.
    private readonly Dictionary<string, IndexEntry> _entries;
    private readonly Dictionary<string, ElementDocument> _documentsById;
    private readonly Dictionary<string, ReferenceInfo[]> _referencesTo;
    private readonly Dictionary<string, ReferenceInfo[]> _referencesFrom;
    private readonly FrozenDictionary<ElementKind, ImmutableArray<Element>> _byKind;
    private readonly FrozenDictionary<ElementKind, string> _kindSetHashes;
    private readonly FrozenDictionary<string, Stereotype> _stereotypes;
    private readonly ImmutableArray<ElementSummary> _summaries;

    private ModelSnapshot(
        long version,
        ProjectSettings settings,
        string settingsHash,
        ImmutableArray<ElementDocument> documents,
        ImmutableArray<Diagnostic> loadDiagnostics,
        ImmutableArray<ExtensionDocument> extensions,
        ImmutableArray<ScriptSource> ruleScripts,
        PropertySchemaSet propertySchemas,
        ModelIndexer.Result index,
        IReadOnlyList<LocaleShardDocument> shards,
        StereotypeDefaults stereotypeDefaults,
        ImmutableArray<ScriptSource> seedScripts,
        ImmutableArray<CustomKindDocument> customKinds,
        IReadOnlyList<ScriptLibrarySource> scriptLibraries,
        ImmutableArray<ScriptSource> calculationScripts,
        ImmutableArray<DeclaredRuleDocument> declaredRules)
    {
        ScriptLibraries = scriptLibraries;
        CalculationScripts = calculationScripts;
        DeclaredRules = declaredRules;
        Version = version;
        Settings = settings;
        SettingsHash = settingsHash;
        Documents = documents;
        LoadDiagnostics = loadDiagnostics;
        Extensions = extensions;
        RuleScripts = ruleScripts;
        SeedScripts = seedScripts;
        CustomKinds = customKinds;
        _customKindsByType = customKinds
            .Where(k => string.Equals(ModelPaths.StemOfPath(k.Path), k.Kind.Type, StringComparison.Ordinal))
            .GroupBy(k => k.Kind.Type, StringComparer.Ordinal)
            .ToFrozenDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        PropertySchemas = propertySchemas;
        _entries = index.Entries;
        _documentsById = index.DocumentsById;
        _referencesTo = index.ReferencesTo;
        _referencesFrom = index.ReferencesFrom;
        _byKind = index.ByKind;
        _kindSetHashes = index.KindSetHashes;
        _stereotypes = index.Stereotypes;
        _summaries = index.Summaries;
        Tags = index.Tags;
        Categories = index.Categories;
        Verbs = index.Verbs;
        _ownVerbs = index.OwnVerbs;
        _tagVocabularies = index.TagVocabularies;
        _categoryTrees = index.CategoryTrees;
        Index = index;
        StereotypeDefaults = stereotypeDefaults;
        LocaleShards = shards;
        _localization = new Lazy<LocalizationIndex>(() => new LocalizationIndex(this, shards), LazyThreadSafetyMode.ExecutionAndPublication);
        _names = new Lazy<NameIndexes>(() => new NameIndexes(this), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private readonly Lazy<LocalizationIndex> _localization;
    private readonly Lazy<NameIndexes> _names;

    /// <summary>The locale shards the loader read (reference-types-seeds-localization.md section 3.3), ordinal by path.</summary>
    public IReadOnlyList<LocaleShardDocument> LocaleShards { get; }

    /// <summary>The localization view: settings, effective translations, localizable nodes, completeness and the <c>l:</c> hashes.</summary>
    public LocalizationIndex Localization => _localization.Value;

    /// <summary>The indexes, kept so the next snapshot of a store can patch them (<see cref="ModelIndexer.Build"/>).</summary>
    internal ModelIndexer.Result Index { get; }

    /// <summary>The number of suppression entries the documents hold (next-phase-2-design.md section 5.6), counted by the index walk.</summary>
    internal int SuppressionCount => Index.SuppressionCount;

    /// <summary>
    /// The holders and layered effective sets of default stereotypes (next-phase-2-design.md section 3.1), built from the documents
    /// before the index.
    /// </summary>
    internal StereotypeDefaults StereotypeDefaults { get; }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, StereotypeHolder> _holders = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds a snapshot and its indexes. Pure: no I/O. On duplicate ids the document with the ordinally first path wins; a second
    /// tag vocabulary or category tree is ignored and reported (MQ1009) in <see cref="LoadDiagnostics"/>.
    /// </summary>
    /// <param name="documents">The loaded element files.</param>
    /// <param name="settings">The project settings.</param>
    /// <param name="settingsHash">The hash of <c>maquettiste.json</c>.</param>
    /// <param name="extensions">The loaded extension schemas.</param>
    /// <param name="ruleScripts">The JavaScript rule scripts from <c>extensions/rules/</c>.</param>
    /// <param name="version">A number that increases with every snapshot a store produces.</param>
    /// <param name="loadDiagnostics">Diagnostics of files that failed to load, or loaded with warnings.</param>
    /// <param name="localeShards">The locale shards (reference-types-seeds-localization.md section 3.3).</param>
    /// <param name="seedScripts">The rows scripts from <c>extensions/seeds/</c> (next-phase-2-design.md section 3.4).</param>
    /// <param name="customKinds">The custom kind files from <c>extensions/kinds/</c> (next-phase-2-design.md section 4.2).</param>
    /// <param name="scriptLibraries">The closure of <c>maquettiste.json</c>'s uses, in dependency order (library packs).</param>
    /// <param name="calculationScripts">The calculation files from <c>extensions/calculated/</c> (next-phase-2-design.md section 5.5).</param>
    /// <param name="declaredRules">The valid rule declaration files from <c>extensions/rules/*.json</c> (next-phase-2-design.md section 5.6).</param>
    /// <returns>The snapshot.</returns>
    public static ModelSnapshot Create(
        IEnumerable<ElementDocument> documents,
        ProjectSettings settings,
        string settingsHash,
        IReadOnlyList<ExtensionDocument> extensions,
        IReadOnlyList<ScriptSource> ruleScripts,
        long version,
        IReadOnlyList<Diagnostic>? loadDiagnostics = null,
        IReadOnlyList<LocaleShardDocument>? localeShards = null,
        IReadOnlyList<ScriptSource>? seedScripts = null,
        IReadOnlyList<CustomKindDocument>? customKinds = null,
        IReadOnlyList<ScriptLibrarySource>? scriptLibraries = null,
        IReadOnlyList<ScriptSource>? calculationScripts = null,
        IReadOnlyList<DeclaredRuleDocument>? declaredRules = null) =>
        CreateAfter(null, documents, settings, settingsHash, extensions, ruleScripts, version, loadDiagnostics, ModelIndexer.DefaultParallelism,
            CancellationToken.None, localeShards, seedScripts, customKinds, scriptLibraries, calculationScripts, declaredRules);

    /// <summary>
    /// <see cref="Create"/> after the store's previous snapshot, whose indexes are patched when few documents changed (the result
    /// is the same as without it), walking at most <paramref name="parallelism"/> documents at once and observing
    /// <paramref name="ct"/> (the loader and the store pass <see cref="EngineOptions.EffectiveParallelism"/> and their token). Without
    /// <paramref name="localeShards"/>, <paramref name="seedScripts"/>, <paramref name="customKinds"/>, <paramref name="scriptLibraries"/> or
    /// <paramref name="calculationScripts"/> or <paramref name="declaredRules"/> the previous snapshot's are kept.
    /// </summary>
    internal static ModelSnapshot CreateAfter(
        ModelSnapshot? previous,
        IEnumerable<ElementDocument> documents,
        ProjectSettings settings,
        string settingsHash,
        IReadOnlyList<ExtensionDocument> extensions,
        IReadOnlyList<ScriptSource> ruleScripts,
        long version,
        IReadOnlyList<Diagnostic>? loadDiagnostics,
        int parallelism,
        CancellationToken ct,
        IReadOnlyList<LocaleShardDocument>? localeShards = null,
        IReadOnlyList<ScriptSource>? seedScripts = null,
        IReadOnlyList<CustomKindDocument>? customKinds = null,
        IReadOnlyList<ScriptLibrarySource>? scriptLibraries = null,
        IReadOnlyList<ScriptSource>? calculationScripts = null,
        IReadOnlyList<DeclaredRuleDocument>? declaredRules = null)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(settings);
        var docs = documents.ToImmutableArray();
        var sorted = true;
        for (var i = 1; i < docs.Length && sorted; i++)
            sorted = string.CompareOrdinal(docs[i - 1].Path, docs[i].Path) <= 0;
        if (!sorted)
            docs = [.. docs.OrderBy(d => d.Path, StringComparer.Ordinal)];
        var sortedExtensions = extensions.OrderBy(e => e.Path, StringComparer.Ordinal).ToImmutableArray();
        ImmutableArray<CustomKindDocument> kinds =
            [.. (customKinds ?? previous?.CustomKinds ?? []).OrderBy(k => k.Kind.Type, StringComparer.Ordinal).ThenBy(k => k.Path, StringComparer.Ordinal)];
        var propertySchemas = PropertySchemaSet.Create(sortedExtensions, previous?.PropertySchemas, [.. docs.Where(d => d.Element is Stereotype)], kinds);
        var stereotypeDefaults = new StereotypeDefaults(docs);
        var index = ModelIndexer.Build(docs, propertySchemas, stereotypeDefaults, previous?.Index, parallelism, ct);
        var diagnostics = (loadDiagnostics ?? []).ToImmutableArray();
        if (!index.Diagnostics.IsEmpty)
            diagnostics = [.. diagnostics.AddRange(index.Diagnostics).Order(Diagnostic.Order)];
        return new ModelSnapshot(
            version,
            settings,
            settingsHash,
            docs,
            diagnostics,
            sortedExtensions,
            ruleScripts.OrderBy(s => s.Path, StringComparer.Ordinal).ToImmutableArray(),
            propertySchemas,
            index,
            localeShards is null ? previous?.LocaleShards ?? [] : [.. localeShards.OrderBy(s => s.ModelPath, StringComparer.Ordinal)],
            stereotypeDefaults,
            [.. (seedScripts ?? previous?.SeedScripts ?? []).OrderBy(s => s.Path, StringComparer.Ordinal)],
            kinds,
            scriptLibraries ?? previous?.ScriptLibraries ?? [],
            [.. (calculationScripts ?? previous?.CalculationScripts ?? []).OrderBy(s => s.Path, StringComparer.Ordinal)],
            [.. (declaredRules ?? previous?.DeclaredRules ?? []).OrderBy(d => d.Path, StringComparer.Ordinal)]);
    }

    /// <summary>The snapshot version.</summary>
    public long Version { get; }

    /// <summary>The project settings.</summary>
    public ProjectSettings Settings { get; }

    /// <summary>The hash of <c>maquettiste.json</c>.</summary>
    public string SettingsHash { get; }

    /// <summary>Every element document, ordinal by path.</summary>
    public IReadOnlyList<ElementDocument> Documents { get; }

    /// <summary>Diagnostics produced while loading.</summary>
    public IReadOnlyList<Diagnostic> LoadDiagnostics { get; }

    /// <summary>Extension schemas, ordinal by path.</summary>
    public IReadOnlyList<ExtensionDocument> Extensions { get; }

    /// <summary>JavaScript rule scripts, ordinal by path.</summary>
    public IReadOnlyList<ScriptSource> RuleScripts { get; }

    /// <summary>
    /// The closure of <c>maquettiste.json</c>'s uses, in dependency order; empty without uses (library packs, next-phase-2-design.md
    /// section 5.2). The project's rule scripts reach their helpers through <c>maquettiste.use</c>.
    /// </summary>
    public IReadOnlyList<ScriptLibrarySource> ScriptLibraries { get; }

    /// <summary>
    /// The rows scripts of <c>extensions/seeds/*.js</c> (next-phase-2-design.md section 3.4), ordinal by path: read with the model, run
    /// only when something reads the rows of a seed that names one (<c>rowsFrom.script</c>).
    /// </summary>
    public IReadOnlyList<ScriptSource> SeedScripts { get; }

    /// <summary>
    /// The calculation files of <c>extensions/calculated/*.js</c> (next-phase-2-design.md section 5.5), ordinal by path: read with the
    /// model, run only when something reads a calculated value. Empty for a project that declares none, which then pays nothing.
    /// </summary>
    public IReadOnlyList<ScriptSource> CalculationScripts { get; }

    /// <summary>
    /// The valid rule declaration files of <c>extensions/rules/*.json</c> (next-phase-2-design.md section 5.6), ordinal by path. Empty for a
    /// project that declares none, which then pays nothing.
    /// </summary>
    public IReadOnlyList<DeclaredRuleDocument> DeclaredRules { get; }

    /// <summary>
    /// The custom kind files of <c>extensions/kinds/*.json</c> that read (next-phase-2-design.md section 4.2), ordinal by type, then by
    /// path.
    /// </summary>
    public IReadOnlyList<CustomKindDocument> CustomKinds { get; }

    private readonly FrozenDictionary<string, CustomKindDocument> _customKindsByType;

    /// <summary>
    /// The custom kind file that declares a type: the file <c>extensions/kinds/&lt;type&gt;.json</c> whose <c>type</c> is its stem, or
    /// <see langword="null"/> when the project declares none (a file whose type differs from its name, MQ1017, declares nothing).
    /// </summary>
    /// <param name="type">The type key.</param>
    /// <returns>The custom kind file.</returns>
    public CustomKindDocument? CustomKindOf(string type) => _customKindsByType.GetValueOrDefault(type);

    /// <summary>The rows script a seed's <c>rowsFrom.script</c> names (a path under <c>extensions/</c>), or <see langword="null"/>.</summary>
    /// <param name="path">The path under <c>extensions/</c>, such as <c>seeds/audited-records.js</c>.</param>
    /// <returns>The script.</returns>
    internal ScriptSource? SeedScript(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var suffix = "extensions/" + path;
        foreach (var script in SeedScripts)
        {
            if (string.Equals(script.Path, suffix, StringComparison.Ordinal) || script.Path.EndsWith("/" + suffix, StringComparison.Ordinal))
                return script;
        }

        return null;
    }

    /// <summary>
    /// The custom property schemas (next-phase-1-design.md section 4.1): the extension files' property schemas with their
    /// <c>x-ref</c> sites and translatable names, reused from the previous snapshot when the files did not change.
    /// </summary>
    internal PropertySchemaSet PropertySchemas { get; }

    /// <summary>The global tag vocabulary (one with no <c>package</c>), when the model has one.</summary>
    public TagVocabulary? Tags { get; }

    /// <summary>The verb vocabulary in use (<c>model/vocabularies/verbs.json</c>, verbs-design.md decision 6), or <see langword="null"/>; a
    /// second one is MQ1009 and ignored.</summary>
    public VerbVocabulary? Verbs { get; }

    private readonly FrozenDictionary<string, OwnVerb> _ownVerbs;

    /// <summary>The project's own verb a use of <paramref name="key"/> names: the first own verb of that key in file order, never for a built-in
    /// verb's key (verbs-design.md decision 7).</summary>
    /// <param name="key">A verb key.</param>
    /// <returns>The own verb, or <see langword="null"/>.</returns>
    public OwnVerb? OwnVerb(string? key) => key is null ? null : _ownVerbs.GetValueOrDefault(key);

    /// <summary>The dependency key of which verbs the model names (verbs-design.md section 6): <c>model.verbs</c> and each verb key on it.</summary>
    internal const string VerbNamingKey = "vn:verbs";

    private string? _verbNamingHash;

    /// <summary>
    /// The current hash of <see cref="VerbNamingKey"/>: the verb vocabulary in use (its id and dependency hash), then the distinct verb keys
    /// the events, transitions and operations name, ordinal, read from the index rows. It changes when a key starts or stops being named
    /// anywhere, or when the vocabulary changes; not when a process or an operation changes otherwise. A model with no vocabulary and no
    /// verb key keeps one value. Worked out once per snapshot.
    /// </summary>
    /// <returns>The hash.</returns>
    internal string VerbNamingHash()
    {
        if (_verbNamingHash is { } known)
            return known;
        var named = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var row in _summaries)
        {
            if (row.Verb is { Length: > 0 } verb)
                named.Add(verb);
            foreach (var key in row.Verbs ?? [])
            {
                if (key.Length > 0)
                    named.Add(key);
            }
        }

        using var hash = new Hashing.HashBuilder();
        hash.Add("vn").Add(Verbs?.Id ?? "").Add(Verbs is { } vocabulary ? GetDocument(vocabulary.Id)?.DependencyHash ?? "" : "");
        foreach (var key in named)
            hash.Add(key);
        return _verbNamingHash = hash.Finish(); // a race computes equal values
    }

    /// <summary>The global category tree (one with no <c>package</c>), when the model has one.</summary>
    public CategoryTree? Categories { get; }

    private readonly FrozenDictionary<string, TagVocabulary> _tagVocabularies;
    private readonly FrozenDictionary<string, CategoryTree> _categoryTrees;

    /// <summary>Every tag vocabulary validation uses, one per scope (a second one in a scope is MQ1009 and ignored), ordinal by scope.</summary>
    public IEnumerable<TagVocabulary> TagVocabularies => _tagVocabularies.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value);

    /// <summary>Every category tree validation uses, one per scope (a second one in a scope is MQ1009 and ignored), ordinal by scope.</summary>
    public IEnumerable<CategoryTree> CategoryTrees => _categoryTrees.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value);

    /// <summary>The tag vocabulary of a scope: a package id, or <see langword="null"/> for the global one.</summary>
    /// <param name="scope">The package id, or <see langword="null"/>.</param>
    /// <returns>The vocabulary, or <see langword="null"/> when the scope has none.</returns>
    public TagVocabulary? TagVocabularyOf(string? scope) => _tagVocabularies.GetValueOrDefault(scope ?? "");

    /// <summary>The category tree of a scope: a package id, or <see langword="null"/> for the global one.</summary>
    /// <param name="scope">The package id, or <see langword="null"/>.</param>
    /// <returns>The tree, or <see langword="null"/> when the scope has none.</returns>
    public CategoryTree? CategoryTreeOf(string? scope) => _categoryTrees.GetValueOrDefault(scope ?? "");

    /// <summary>
    /// The vocabulary scopes an element in <paramref name="packageId"/> sees (explorer-redesign.md section 1.11): the package, each
    /// enclosing package, nearest first, then <see langword="null"/> for global. A package cycle (MQ3xxx) stops at the first repeat.
    /// </summary>
    /// <param name="packageId">The element's package (a package's own chain starts at itself), or <see langword="null"/>.</param>
    /// <returns>The scopes, nearest first, ending with <see langword="null"/>.</returns>
    public IReadOnlyList<string?> VocabularyChain(string? packageId)
    {
        var chain = new List<string?>();
        for (var id = packageId; id is not null && !chain.Contains(id); id = (GetDocument(id)?.Element as Package)?.Parent)
            chain.Add(id);
        chain.Add(null);
        return chain;
    }

    /// <summary>Looks up an element or sub-element id.</summary>
    /// <param name="id">The id.</param>
    /// <param name="entry">The index entry when found.</param>
    /// <returns><see langword="true"/> when the id exists.</returns>
    public bool TryGetEntry(string id, [MaybeNullWhen(false)] out IndexEntry entry) => _entries.TryGetValue(id, out entry);

    /// <summary>Returns the document holding an id; a sub-element id returns its owner's document.</summary>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns>The document, or <see langword="null"/>.</returns>
    public ElementDocument? GetDocument(string id) =>
        _entries.TryGetValue(id, out var entry) && _documentsById.TryGetValue(entry.OwnerId, out var doc) ? doc : null;

    /// <summary>Returns a top-level element of a given type.</summary>
    /// <typeparam name="T">The element type.</typeparam>
    /// <param name="id">The element id.</param>
    /// <returns>The element, or <see langword="null"/> when absent or of another type.</returns>
    public T? Get<T>(string id) where T : Element =>
        _documentsById.TryGetValue(id, out var doc) ? doc.Element as T : null;

    /// <summary>Returns every element of a type, ordinal by (name, id).</summary>
    /// <typeparam name="T">The element type (a concrete element record, or <see cref="Element"/> for all).</typeparam>
    /// <returns>The elements.</returns>
    public IReadOnlyList<T> All<T>() where T : Element
    {
        if (typeof(T) == typeof(Element))
            return (IReadOnlyList<T>)(object)_byKind.Values.SelectMany(v => v).OrderBy(e => e.Name, StringComparer.Ordinal)
                .ThenBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray();
        foreach (var kind in KindInfo.All)
        {
            if (kind.ClrType == typeof(T))
                return _byKind.TryGetValue(kind.Kind, out var list) ? list.Cast<T>().ToImmutableArray() : [];
        }

        return [];
    }

    /// <summary>Returns every reference to an id, in (path, pointer) order.</summary>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns>The references.</returns>
    public IReadOnlyList<ReferenceInfo> ReferencesTo(string id) => ReadOnly(_referencesTo, id);

    /// <summary>Returns every reference held in an element's file, in pointer order.</summary>
    /// <param name="elementId">A top-level element id.</param>
    /// <returns>The references.</returns>
    public IReadOnlyList<ReferenceInfo> ReferencesFrom(string elementId) => ReadOnly(_referencesFrom, elementId);

    /// <summary>
    /// A reference group as an <see cref="ImmutableArray{T}"/> over the stored array (no copy): the arrays are shared with the
    /// snapshots patched from this one, so a caller must not be able to write them through a downcast.
    /// </summary>
    private static IReadOnlyList<ReferenceInfo> ReadOnly(Dictionary<string, ReferenceInfo[]> groups, string id) =>
        groups.TryGetValue(id, out var refs) ? ImmutableCollectionsMarshal.AsImmutableArray(refs) : ImmutableArray<ReferenceInfo>.Empty;

    /// <summary>Returns the stereotype with a key.</summary>
    /// <param name="key">The stereotype's immutable <see cref="Stereotype.Key"/>.</param>
    /// <returns>The stereotype, or <see langword="null"/>.</returns>
    public Stereotype? GetStereotype(string key) => _stereotypes.TryGetValue(key, out var s) ? s : null;

    /// <summary>
    /// The effective stereotypes of a list of keys (next-phase-1-design.md section 4.2). Implied first, then own; an own entry
    /// replaces an implied copy of the same key; unknown keys skipped. Implication is one level, in entry then <c>implies</c> order,
    /// each key once. Rules judge this set, and resolution merges it in order, so an explicit choice beats an implied default. Sub-elements
    /// and process nodes use it; a top-level element's set, defaults included, is <see cref="EffectiveStereotypes(Element)"/>.
    /// </summary>
    /// <param name="keys">The element's stereotype keys, as its file lists them.</param>
    /// <returns>The effective set: own entries with <see cref="StereotypeSource.Own"/>, implied ones with <see cref="StereotypeSource.Implied"/>.</returns>
    public IReadOnlyList<AppliedStereotype> EffectiveStereotypes(IReadOnlyList<string> keys) => StereotypeDefaults.Layer(keys, GetStereotype);

    /// <summary>
    /// What a top-level element's effective set is built from (next-phase-2-design.md section 3.1): its own and excluded keys, the
    /// package whose chain gives package defaults (its own, else the phase 1 fallback: a table overlay's entity's or relation's, a
    /// seed's target's, a scenario's process's), and, for a table, view, sequence, routine, database type or SQL object, the database
    /// schema entry whose defaults reach it. Cached per element id for the snapshot's own elements.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The holder.</returns>
    public StereotypeHolder HolderOf(Element element)
    {
        ArgumentNullException.ThrowIfNull(element);
        // Only the snapshot's own instance is cached: a candidate element with the same id (a save being judged) gets its own holder.
        return ReferenceEquals(Get<Element>(element.Id), element)
            ? _holders.GetOrAdd(element.Id, static (_, state) => state.Defaults.HolderOf(state.Element), (Defaults: StereotypeDefaults, Element: element))
            : StereotypeDefaults.HolderOf(element);
    }

    /// <summary>
    /// The effective stereotypes of a top-level element (next-phase-2-design.md section 3.1): <see cref="EffectiveStereotypes(StereotypeHolder)"/>
    /// of its <see cref="HolderOf"/>.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The effective set.</returns>
    public IReadOnlyList<AppliedStereotype> EffectiveStereotypes(Element element) => EffectiveStereotypes(HolderOf(element));

    /// <summary>
    /// The effective stereotypes of a holder, in layers (next-phase-2-design.md section 3.1, decision 5): the schema's defaults, then the
    /// package defaults from the outermost package to the holder's own, then the own list. Within a layer the stereotypes its entries
    /// imply come first, then the entries, as <see cref="EffectiveStereotypes(IReadOnlyList{string})"/> orders the own list. A default
    /// that <c>excludeStereotypes</c> names, or that does not apply to the holder's kind, is left out with the copies it implies, so a
    /// default never makes MQ2004. A key listed twice keeps its last place: a later layer beats an earlier one, and within a layer an
    /// entry beats an implied copy. A default entry has <see cref="AppliedStereotype.Entry"/> -1, its source and its holder's id. With
    /// no defaults the set is the own list's.
    /// </summary>
    /// <param name="holder">The holder.</param>
    /// <returns>The effective set.</returns>
    public IReadOnlyList<AppliedStereotype> EffectiveStereotypes(StereotypeHolder holder) => StereotypeDefaults.Effective(holder, GetStereotype);

    /// <summary>
    /// The keys custom property schemas apply to a top-level element by (next-phase-2-design.md section 3.1): when the model declares a
    /// default stereotype, <see cref="PropertySchemaSet.HostKeys"/> of its effective set, defaults included; otherwise
    /// <see langword="null"/>, and the schemas apply by its own keys and the ones they imply, as in phase 1. Every walk over an
    /// element's <c>properties</c> passes it to <see cref="PropertySchemaSet.Sites"/> or <see cref="PropertyHost.Of"/>.
    /// </summary>
    /// <param name="element">The element.</param>
    /// <returns>The keys, or <see langword="null"/>.</returns>
    internal IReadOnlyList<string>? PropertyKeys(Element element) =>
        StereotypeDefaults.HasDefaults ? PropertySchemaSet.HostKeys(EffectiveStereotypes(element), element.Stereotypes) : null;

    /// <summary>
    /// Returns the current hash of the <c>r:&lt;id&gt;</c> dependency key (engine-design.md section 11): <c>H</c> over the sorted
    /// <c>(element id, DependencyHash)</c> pairs of every file that references <paramref name="id"/>, excluding the file that holds
    /// it and diagram files (never visible to templates). It changes when a file starts or stops referencing the id, or a referrer
    /// changes, so a new mapping or table overlay re-renders the units of the entity it binds.
    /// </summary>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns>The hash; for an id nothing references, the hash of no fields.</returns>
    public string ReferrersHash(string id)
    {
        var owner = _entries.TryGetValue(id, out var entry) ? entry.OwnerId : id;
        var referrers = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var reference in _referencesTo.GetValueOrDefault(id) ?? [])
        {
            if (reference.FromElementId != owner && _documentsById.TryGetValue(reference.FromElementId, out var doc) && doc.Element is not Diagram)
                referrers.Add(reference.FromElementId);
        }

        using var hash = new HashBuilder();
        foreach (var referrer in referrers)
            hash.Add(referrer).Add(_documentsById[referrer].DependencyHash);
        return hash.Finish();
    }

    /// <summary>Returns <c>H(sorted ids of the kind)</c>, the current hash of the <c>k:&lt;kind&gt;</c> dependency key.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The hash.</returns>
    public string KindSetHash(ElementKind kind) => _kindSetHashes.TryGetValue(kind, out var h) ? h : HashBuilder.Of();

    /// <summary>
    /// Whether an element or sub-element's <c>status</c> is <c>retired</c> (next-phase-1-design.md section 4.5; MQ2022 reads it).
    /// </summary>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns><see langword="true"/> when the model holds the id and it is retired.</returns>
    internal bool IsRetired(string id) => Index.Retired.Contains(id);

    /// <summary>
    /// The links to an element or sub-element that count as using it: every reference to it, except from diagrams, which only show it,
    /// and from the object's own parts. Declared rules and <c>referenced_by</c> read this.
    /// </summary>
    /// <param name="id">An element or sub-element id.</param>
    /// <returns>The references, in index order.</returns>
    internal IEnumerable<ReferenceInfo> LinksTo(string id)
    {
        if (!_entries.TryGetValue(id, out var entry))
            yield break;
        foreach (var reference in ReferencesTo(id))
        {
            if (TryGetEntry(reference.FromElementId, out var holder) && string.Equals(holder.Kind, "diagram", StringComparison.Ordinal))
                continue;
            if (string.Equals(reference.FromElementId, entry.OwnerId, StringComparison.Ordinal) && IsInside(reference.JsonPointer, entry.JsonPointer))
                continue;
            yield return reference;
        }
    }

    /// <summary>Whether a pointer is in the entry at another (the whole file for the empty pointer).</summary>
    internal static bool IsInside(string pointer, string entry) =>
        entry.Length == 0 || (pointer.Length > entry.Length && pointer[entry.Length] == '/' && pointer.StartsWith(entry, StringComparison.Ordinal));

    /// <summary>
    /// The forms of one kind that qualified names find (next-phase-1-design.md section 5.1), built from the typed elements on first
    /// use and cached; templates and rule engines read it in parallel. A batch layers its private overlay on it.
    /// </summary>
    /// <param name="kind">A kind of <see cref="NameIndex.NamedKinds"/>.</param>
    /// <returns>The immutable index.</returns>
    internal NameIndex Names(string kind) => _names.Value.Get(kind);

    /// <summary>The elements of one kind, in index order.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The elements.</returns>
    internal IEnumerable<Element> ElementsOf(ElementKind kind) => _byKind.TryGetValue(kind, out var list) ? list : [];

    /// <summary>
    /// The documents of one kind from the sorted kind index, ordinal by (name, id), without a scan of every document: the first
    /// document of each id (a duplicate id, MQ1004, is left out). A rule's <c>model.all(kind)</c> reads it (next-phase-1-design.md
    /// section 5.7).
    /// </summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The documents.</returns>
    internal IReadOnlyList<ElementDocument> DocumentsOf(ElementKind kind)
    {
        if (!_byKind.TryGetValue(kind, out var list))
            return [];
        var documents = new ElementDocument[list.Length];
        for (var i = 0; i < documents.Length; i++)
            documents[i] = _documentsById[list[i].Id];
        return documents;
    }

    /// <summary>Returns a summary of every element, ordinal by path.</summary>
    /// <returns>The summaries.</returns>
    public IReadOnlyList<ElementSummary> Summaries() => _rows ??= ModelIndexer.WithSubjectCoverage(this, _summaries);

    /// <summary>The rows with the process rows' subject coverage (stereotype-sets-design.md section 8), worked out on first read.</summary>
    private IReadOnlyList<ElementSummary>? _rows;
}
