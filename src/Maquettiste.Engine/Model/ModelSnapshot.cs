using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
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
        ModelIndexer.Result index)
    {
        Version = version;
        Settings = settings;
        SettingsHash = settingsHash;
        Documents = documents;
        LoadDiagnostics = loadDiagnostics;
        Extensions = extensions;
        RuleScripts = ruleScripts;
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
        Index = index;
    }

    /// <summary>The indexes, kept so the next snapshot of a store can patch them (<see cref="ModelIndexer.Build"/>).</summary>
    internal ModelIndexer.Result Index { get; }

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
    /// <returns>The snapshot.</returns>
    public static ModelSnapshot Create(
        IEnumerable<ElementDocument> documents,
        ProjectSettings settings,
        string settingsHash,
        IReadOnlyList<ExtensionDocument> extensions,
        IReadOnlyList<ScriptSource> ruleScripts,
        long version,
        IReadOnlyList<Diagnostic>? loadDiagnostics = null) =>
        CreateAfter(null, documents, settings, settingsHash, extensions, ruleScripts, version, loadDiagnostics, ModelIndexer.DefaultParallelism,
            CancellationToken.None);

    /// <summary>
    /// <see cref="Create"/> after the store's previous snapshot, whose indexes are patched when few documents changed (the result
    /// is the same as without it), walking at most <paramref name="parallelism"/> documents at once and observing
    /// <paramref name="ct"/> (the loader and the store pass <see cref="EngineOptions.EffectiveParallelism"/> and their token).
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(settings);
        var docs = documents.ToImmutableArray();
        var sorted = true;
        for (var i = 1; i < docs.Length && sorted; i++)
            sorted = string.CompareOrdinal(docs[i - 1].Path, docs[i].Path) <= 0;
        if (!sorted)
            docs = [.. docs.OrderBy(d => d.Path, StringComparer.Ordinal)];
        var index = ModelIndexer.Build(docs, previous?.Index, parallelism, ct);
        var diagnostics = (loadDiagnostics ?? []).ToImmutableArray();
        if (!index.Diagnostics.IsEmpty)
            diagnostics = [.. diagnostics.AddRange(index.Diagnostics).Order(Diagnostic.Order)];
        return new ModelSnapshot(
            version,
            settings,
            settingsHash,
            docs,
            diagnostics,
            extensions.OrderBy(e => e.Path, StringComparer.Ordinal).ToImmutableArray(),
            ruleScripts.OrderBy(s => s.Path, StringComparer.Ordinal).ToImmutableArray(),
            index);
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

    /// <summary>The tag vocabulary, when the model has one.</summary>
    public TagVocabulary? Tags { get; }

    /// <summary>The category tree, when the model has one.</summary>
    public CategoryTree? Categories { get; }

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

    /// <summary>Returns a summary of every element, ordinal by path.</summary>
    /// <returns>The summaries.</returns>
    public IReadOnlyList<ElementSummary> Summaries() => _summaries;
}
