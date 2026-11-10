using System.Collections;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Reflection;
using System.Text.Json.Serialization.Metadata;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.Model;

/// <summary>
/// Builds the indexes of a <see cref="ModelSnapshot"/>: ids (elements and sub-elements), references (from
/// <see cref="ElementRefAttribute"/>, keyed references split into their id segments, and <c>stereotypes</c> entries resolved
/// through the stereotype keys), kinds, stereotypes and summaries. Property metadata comes from
/// <see cref="EngineJson.Options"/>, so JSON names and getters match serialization exactly. Holds no static state.
/// </summary>
internal static class ModelIndexer
{
    private const string ModelNamespace = "Maquettiste.Engine.Model";

    internal sealed record Result(
        Dictionary<string, IndexEntry> Entries,
        Dictionary<string, ElementDocument> DocumentsById,
        Dictionary<string, ReferenceInfo[]> ReferencesTo,
        Dictionary<string, ReferenceInfo[]> ReferencesFrom,
        FrozenDictionary<ElementKind, ImmutableArray<Element>> ByKind,
        FrozenDictionary<ElementKind, string> KindSetHashes,
        FrozenDictionary<string, Stereotype> Stereotypes,
        ImmutableArray<ElementSummary> Summaries,
        TagVocabulary? Tags,
        CategoryTree? Categories,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        /// <summary>The tag vocabulary of each scope (package id, <c>""</c> for global) that validation uses.</summary>
        internal FrozenDictionary<string, TagVocabulary> TagVocabularies { get; init; } = FrozenDictionary<string, TagVocabulary>.Empty;

        /// <summary>The category tree of each scope (package id, <c>""</c> for global) that validation uses.</summary>
        internal FrozenDictionary<string, CategoryTree> CategoryTrees { get; init; } = FrozenDictionary<string, CategoryTree>.Empty;

        /// <summary>The verb vocabulary in use (verbs-design.md decision 6): the ordinally first file; a second one is MQ1009.</summary>
        internal VerbVocabulary? Verbs { get; init; }

        /// <summary>
        /// The project's own verbs by key that uses resolve to (verbs-design.md decision 7): the first own verb of each key in file order, a
        /// built-in verb's key left out, since a use of it is the built-in (MQ9702 reports the own verb).
        /// </summary>
        internal FrozenDictionary<string, OwnVerb> OwnVerbs { get; init; } = FrozenDictionary<string, OwnVerb>.Empty;

        /// <summary>The documents indexed, in path order (aligned with <see cref="Walks"/>).</summary>
        internal ImmutableArray<ElementDocument> Documents { get; init; }

        /// <summary>Each document's walk (sub-element entries and references), for <see cref="Build"/> to reuse on the next snapshot.</summary>
        internal DocumentWalk[]? Walks { get; init; }

        /// <summary>Whether no id was registered twice and no singleton kind repeated: the precondition for patching this index.</summary>
        internal bool Clean { get; init; }

        /// <summary>Whether this index was patched from the previous one rather than built in full (tests).</summary>
        internal bool Patched { get; init; }

        /// <summary>The reference-data facts the walks used (kept by a patch, which rebuilds in full when they could change).</summary>
        internal ReferenceDataIndex? ReferenceData { get; init; }

        /// <summary>
        /// The ids of the elements and sub-elements whose <c>status</c> is <c>retired</c> (next-phase-1-design.md section 4.5), from
        /// the walks, so a patch keeps them. Never written after the build.
        /// </summary>
        internal HashSet<string> Retired { get; init; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The <see cref="PropertySchemaSet.Hash"/> the walks used (next-phase-1-design.md section 4.1): a patch whose set differs
        /// rebuilds in full, since every document's <c>x-ref</c> sites may change.
        /// </summary>
        internal string PropertySchemasHash { get; init; } = "";

        /// <summary>
        /// Whether the indexed model declares a default stereotype (next-phase-2-design.md section 3.1): the walks then typed each
        /// element's properties by its effective keys, defaults included, so a patch from or to such a model rebuilds in full when a
        /// change can move the holder of a document it would keep.
        /// </summary>
        internal bool HasDefaults { get; init; }

        /// <summary>
        /// The number of suppression entries the indexed documents hold (next-phase-2-design.md section 5.6), counted by the walk and kept
        /// by a patch, so a model without any never builds the suppression lookup.
        /// </summary>
        internal int SuppressionCount { get; init; }
    }

    internal enum PropertyRole { Reference, KeyedReference, StereotypeKeys, VerbKey, TypeRef, Record, RecordList }

    /// <summary>One walked property: its JSON name, getter and role; for a reference, whether it is owning and the words it may hold instead of
    /// an id (<see cref="ElementRefAttribute.Words"/>; <see langword="null"/> when none, as for almost every reference).</summary>
    internal sealed record PropertyMeta(string Name, Func<object, object?> Get, PropertyRole Role, bool Owning = false, string[]? Words = null)
    {
        /// <summary>Whether a value is one of the property's words, so no reference.</summary>
        /// <param name="value">The value.</param>
        /// <returns>Whether it is a word.</returns>
        public bool IsWord(string value) => Words is { } words && Array.IndexOf(words, value) >= 0;
    }

    /// <summary>Returns the index kind of a sub-element CLR type, or <see langword="null"/> when the type is not a sub-element.</summary>
    /// <param name="type">A model record type.</param>
    /// <returns>The <see cref="IndexEntry.Kind"/> value.</returns>
    internal static string? SubElementKind(Type type) => type switch
    {
        _ when type == typeof(ModelAttribute) => "attribute",
        _ when type == typeof(EnumMember) => "enum-member",
        _ when type == typeof(RelationEnd) => "end",
        _ when type == typeof(Column) => "column",
        _ when type == typeof(Category) => "category",
        _ when type == typeof(OwnVerb) => "verb",
        _ when type == typeof(DbSchema) => "schema",
        _ when type == typeof(ReferenceCode) || type == typeof(ReferenceLabel) => "reference-field",
        _ when type == typeof(SeedRow) => "row",
        _ when type == typeof(ProcessState) => "state",
        _ when type == typeof(ProcessTransition) => "transition",
        _ when type == typeof(ProcessEvent) => "event",
        _ when type == typeof(ProcessGuard) => "guard",
        _ when type == typeof(ProcessAction) => "action",
        _ when type == typeof(ProcessSet) => "set",
        _ when type == typeof(ProcessInvoke) => "invoke",
        _ when type == typeof(ProcessGate) => "gate",
        _ when type == typeof(GateMeaning) => "meaning",
        _ when type == typeof(GateStage) => "gate-stage",
        _ when type == typeof(NamedSignature) => "named-signature",
        _ when type == typeof(ProcessSubject) => "subject",
        _ when type == typeof(ProcessRight) => "process-right",
        _ when type == typeof(ProcessTriggerPoint) => "process-trigger-point",
        _ when type == typeof(ScenarioStep) => "step",
        _ when type == typeof(ScenarioObject) => "object",
        _ when type == typeof(ScenarioParticipant) => "participant",
        _ when type == typeof(ScenarioHolding) => "holding",
        _ when type == typeof(OperationMember) => "operation-member",
        _ when type == typeof(OperationOutcome) => "outcome",
        _ when type == typeof(OperationError) => "error",
        _ when type == typeof(OperationGuard) => "guard",
        _ when type == typeof(ConstraintParameter) => "parameter",
        _ when type == typeof(EntityBinding) || type == typeof(ReferenceTypeBinding) => "binding",
        _ when type == typeof(AlternateKey) || type == typeof(PrimaryKey) || type == typeof(UniqueConstraint) || type == typeof(ForeignKey)
            || type == typeof(MemberForeignKey) || type == typeof(CheckConstraint) || type == typeof(TableIndex) || type == typeof(EntityCheck) => "key",
        _ => null,
    };

    /// <summary>Every <see cref="IndexEntry.Kind"/> of a sub-element (<see cref="SubElementKind"/>'s values).</summary>
    internal static readonly FrozenSet<string> SubElementKinds = new[]
    {
        "attribute", "enum-member", "end", "column", "category", "schema", "reference-field", "row", "state", "transition", "event",
        "guard", "action", "invoke", "gate", "meaning", "step", "binding", "key", "operation-member", "outcome", "error", "member",
        "parameter", "subject", "object", "participant", "holding", "verb", "set", "process-right", "process-trigger-point", "gate-stage",
        "named-signature",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Builds the indexes of the documents (sorted by path). With the previous snapshot's index, and when the change is small and
    /// touches no stereotype, tag vocabulary or category tree (nor, while either model declares a default stereotype, a document that
    /// can move another one's stereotype holder), the previous indexes are patched instead of rebuilt; the result is the same as a full
    /// build (<c>ModelIndexerTests</c> compares them on random edits), which runs whenever the patch cannot be sure.
    /// </summary>
    /// <param name="documents">The documents, in path order.</param>
    /// <param name="schemas">The custom property schemas, whose <c>x-ref</c> sites become references.</param>
    /// <param name="defaults">The holders of default stereotypes of the documents: an element's properties are typed by its effective
    /// keys, defaults included (next-phase-2-design.md section 3.1).</param>
    /// <param name="previous">The previous snapshot's index, or <see langword="null"/>.</param>
    /// <param name="parallelism">The most documents walked at once by a full build (the caller's
    /// <see cref="EngineOptions.EffectiveParallelism"/>; <see cref="DefaultParallelism"/> without one).</param>
    /// <param name="ct">Cancellation, observed between documents.</param>
    /// <returns>The index.</returns>
    public static Result Build(ImmutableArray<ElementDocument> documents, PropertySchemaSet schemas, StereotypeDefaults defaults, Result? previous,
        int parallelism, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(schemas);
        ArgumentNullException.ThrowIfNull(defaults);
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        ct.ThrowIfCancellationRequested();
        if (previous is { Clean: true, Walks: not null } && string.Equals(previous.PropertySchemasHash, schemas.Hash, StringComparison.Ordinal)
            && TryPatch(documents, schemas, defaults, previous) is { } patched)
        {
            return patched;
        }

        return BuildFull(documents, schemas, defaults, parallelism, ct) with { Patched = false };
    }

    /// <summary>The walk parallelism of a build whose caller has no engine options (the public <see cref="ModelSnapshot.Create"/>).</summary>
    internal static int DefaultParallelism => Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    private static Result BuildFull(ImmutableArray<ElementDocument> documents, PropertySchemaSet schemas, StereotypeDefaults defaults, int parallelism,
        CancellationToken ct)
    {
        // Stereotype keys first, so every element's stereotypes[i] can be indexed as a reference to the stereotype's id.
        var stereotypes = new Dictionary<string, Stereotype>(StringComparer.Ordinal);
        var firstIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in documents)
        {
            if (firstIds.Add(doc.Element.Id) && doc.Element is Stereotype s)
                stereotypes.TryAdd(s.Key, s); // a duplicate key keeps the ordinally first path; validation reports it
        }

        // The verb vocabulary in use, the ordinally first (MQ1009 reports another), so every use of an own verb's key is a reference to it.
        var verbVocabulary = FirstVerbVocabulary(documents);
        var ownVerbs = OwnVerbsOf(verbVocabulary);

        // Each document is walked on its own (in parallel: the walk reads only the element and the stereotype keys); the results are
        // then merged in document order, so the first registration of an id wins exactly as in one sequential walk.
        var metadata = new MetadataCache();
        var referenceData = ReferenceDataIndex.Build(documents.Select(d => d.Element));
        var walks = new DocumentWalk[documents.Length];
        Parallel.For(0, documents.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
        {
            var walk = new DocumentWalk(stereotypes, metadata, referenceData, schemas, defaults, ownVerbs);
            walk.WalkElement(documents[i].Element);
            walks[i] = walk;
        });

        var entries = new Dictionary<string, IndexEntry>(Math.Max(16, walks.Sum(w => w.Entries.Count) + documents.Length), StringComparer.Ordinal);
        var references = new List<ReferenceInfo>(walks.Sum(w => w.References.Count));
        var docsById = new Dictionary<string, ElementDocument>(documents.Length, StringComparer.Ordinal);
        var byKind = new Dictionary<ElementKind, List<Element>>();
        var summaries = ImmutableArray.CreateBuilder<ElementSummary>(documents.Length);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        // One vocabulary of each kind per scope (explorer-redesign.md section 1.11): the scope is the package id, "" for global.
        var tagScopes = new Dictionary<string, (TagVocabulary Vocabulary, string Path)>(StringComparer.Ordinal);
        var categoryScopes = new Dictionary<string, (CategoryTree Tree, string Path)>(StringComparer.Ordinal);
        string? verbPath = null;
        var retired = new HashSet<string>(StringComparer.Ordinal);
        var clean = true;
        var suppressionCount = 0;
        var processRows = new List<(int Row, DocumentWalk Walk)>(); // filled with their actors once every id is known

        for (var d = 0; d < documents.Length; d++)
        {
            var doc = documents[d];
            var element = doc.Element;
            suppressionCount += element.Suppressions.Count; // every document, as the lookup reads them, a duplicate id included
            if (!entries.TryAdd(element.Id, new IndexEntry(element.Id, element.Id, element.KindName, "")))
            {
                clean = false;
                continue; // duplicate id: the ordinally first path wins; the loader reports MQ1004
            }

            docsById[element.Id] = doc;
            if (!byKind.TryGetValue(element.Kind, out var list))
                byKind[element.Kind] = list = [];
            list.Add(element);

            switch (element)
            {
                case TagVocabulary t when tagScopes.TryAdd(t.Package ?? "", (t, doc.Path)):
                    break;
                case CategoryTree c when categoryScopes.TryAdd(c.Package ?? "", (c, doc.Path)):
                    break;
                case TagVocabulary or CategoryTree:
                    // One per scope: the ordinally first file wins, the others are reported and ignored by validation (MQ1009).
                    clean = false;
                    var scope = element is TagVocabulary tv ? tv.Package : ((CategoryTree)element).Package;
                    var firstPath = element is TagVocabulary ? tagScopes[scope ?? ""].Path : categoryScopes[scope ?? ""].Path;
                    diagnostics.Add(RuleCatalog.Create(
                        "MQ1009",
                        $"The {(scope is null ? "model" : $"domain {scope}")} already has a {element.KindName} in {firstPath}; this second one is ignored.",
                        element.Id,
                        doc.Path,
                        ""));
                    break;
                case VerbVocabulary when verbPath is null:
                    verbPath = doc.Path;
                    break;
                case VerbVocabulary:
                    // One per project (verbs-design.md decision 6): the ordinally first file wins, the others are reported and ignored.
                    clean = false;
                    diagnostics.Add(RuleCatalog.Create(
                        "MQ1009",
                        $"The model already has a verb-vocabulary in {verbPath}; this second one is ignored.",
                        element.Id,
                        doc.Path,
                        ""));
                    break;
            }

            var walk = walks[d];
            foreach (var (id, entry) in walk.Entries)
                clean &= entries.TryAdd(id, entry);
            retired.UnionWith(walk.Retired);
            references.AddRange(walk.References);
            if (element is Process)
                processRows.Add((summaries.Count, walk));
            summaries.Add(Summarize(element, doc.Hash, doc.Path, schemas, null));
        }

        foreach (var (row, walk) in processRows)
        {
            if (ActorsOf(walk.References, docsById) is { } actors)
                summaries[row] = summaries[row] with { Actors = actors };
        }

        var referencesTo = Group(references, static r => r.ToId);
        var referencesFrom = Group(references, static r => r.FromElementId);
        var sortedByKind = byKind.ToFrozenDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray());
        var kindSetHashes = byKind.ToFrozenDictionary(
            kv => kv.Key,
            kv => KindSetHash(kv.Value));

        return new Result(
            entries,
            docsById,
            referencesTo,
            referencesFrom,
            sortedByKind,
            kindSetHashes,
            stereotypes.ToFrozenDictionary(StringComparer.Ordinal),
            summaries.ToImmutable(),
            tagScopes.GetValueOrDefault("").Vocabulary,
            categoryScopes.GetValueOrDefault("").Tree,
            diagnostics.ToImmutable())
        {
            TagVocabularies = tagScopes.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Vocabulary, StringComparer.Ordinal),
            CategoryTrees = categoryScopes.ToFrozenDictionary(kv => kv.Key, kv => kv.Value.Tree, StringComparer.Ordinal),
            Verbs = verbVocabulary,
            OwnVerbs = ownVerbs,
            Documents = documents,
            Walks = walks,
            Clean = clean,
            ReferenceData = referenceData,
            Retired = retired,
            PropertySchemasHash = schemas.Hash,
            HasDefaults = defaults.HasDefaults,
            SuppressionCount = suppressionCount,
        };
    }

    /// <summary>The largest number of added plus removed documents a patch handles; more rebuild in full.</summary>
    private const int PatchLimit = 256;

    /// <summary>
    /// Patches the previous index for a small change, or returns <see langword="null"/> when a full build is needed: a changed,
    /// added or removed stereotype, tag vocabulary or category tree (they change how other files are indexed), too many changes, or
    /// an id that would now be registered twice. The caller has checked that the custom property schemas are the ones the previous
    /// walks used (a changed set rebuilds in full, even when no document changed). Unchanged documents (the same <see cref="ElementDocument"/> instance at the same
    /// path) keep their walks; every map ends up with exactly the content and order a full build gives.
    /// </summary>
    private static Result? TryPatch(ImmutableArray<ElementDocument> documents, PropertySchemaSet schemas, StereotypeDefaults defaults, Result previous)
    {
        var old = previous.Documents;
        var oldWalks = previous.Walks!;
        var walks = new DocumentWalk[documents.Length];
        var removed = new List<int>(); // indexes into old
        var added = new List<int>(); // indexes into documents
        int i = 0, j = 0;
        while (i < old.Length || j < documents.Length)
        {
            var c = i >= old.Length ? 1 : j >= documents.Length ? -1 : string.CompareOrdinal(old[i].Path, documents[j].Path);
            if (c == 0 && ReferenceEquals(old[i], documents[j]))
            {
                walks[j++] = oldWalks[i++];
                continue;
            }

            if (c <= 0)
                removed.Add(i++);
            if (c >= 0)
                added.Add(j++);
            if (removed.Count + added.Count > PatchLimit)
                return null;
        }

        if (removed.Count == 0 && added.Count == 0)
            return previous with { Documents = documents, Walks = walks, Patched = true, HasDefaults = defaults.HasDefaults };
        foreach (var r in removed)
        {
            if (old[r].Element is Stereotype or TagVocabulary or CategoryTree or VerbVocabulary)
                return null;
        }

        foreach (var a in added)
        {
            if (documents[a].Element is Stereotype or TagVocabulary or CategoryTree or VerbVocabulary)
                return null;
        }

        // While either model declares a default stereotype, a kept document's walk typed its properties by a holder that a change to a
        // package, database or mapping, or to the element its holder falls back to, can move (next-phase-2-design.md section 3.1).
        if ((previous.HasDefaults || defaults.HasDefaults) && MovesHolders([.. removed.Select(r => old[r].Element)], [.. added.Select(a => documents[a].Element)]))
            return null;

        // Seed cells become references through model-wide facts (reference-typed attributes, ends, codes): a change that could alter
        // them for a document the patch keeps needs the full build.
        if (previous.ReferenceData is not { } referenceData
            || removed.Any(r => AffectsReferenceData(old[r].Element, referenceData))
            || added.Any(a => AffectsReferenceData(documents[a].Element, referenceData)))
        {
            return null;
        }

        // A state code under a reference binding becomes a reference to its row through the bound attribute's type, which may sit in another
        // file and which only a full build refreshes (lifecycle-design.md decision 18): a process that adds, removes or changes a binding,
        // and a document that retypes, removes or adds a bound attribute, need the full build. Neither happens in a model that binds nothing.
        if (MovesBindings([.. removed.Select(r => old[r].Element)], [.. added.Select(a => documents[a].Element)], referenceData))
            return null;

        var metadata = new MetadataCache();
        foreach (var a in added)
        {
            var walk = new DocumentWalk(previous.Stereotypes, metadata, referenceData, schemas, defaults, previous.OwnVerbs);
            walk.WalkElement(documents[a].Element);
            walks[a] = walk;
        }

        // Ids: the previous maps without the removed documents' ids, then the added documents' ids; an id taken twice needs the
        // full build (the first path wins there, and the loader reports it).
        var entries = new Dictionary<string, IndexEntry>(previous.Entries, StringComparer.Ordinal);
        var docsById = new Dictionary<string, ElementDocument>(previous.DocumentsById, StringComparer.Ordinal);
        var removedOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in removed)
        {
            var element = old[r].Element;
            removedOwners.Add(element.Id);
            entries.Remove(element.Id);
            docsById.Remove(element.Id);
            foreach (var (id, _) in oldWalks[r].Entries)
                entries.Remove(id);
        }

        foreach (var a in added)
        {
            var element = documents[a].Element;
            if (!entries.TryAdd(element.Id, new IndexEntry(element.Id, element.Id, element.KindName, "")))
                return null;
            docsById[element.Id] = documents[a];
            foreach (var (id, entry) in walks[a].Entries)
            {
                if (!entries.TryAdd(id, entry))
                    return null;
            }
        }

        // Retired ids: the previous set without the removed walks' ids, with the added walks' ids (unchanged when neither has any).
        var retired = previous.Retired;
        if (removed.Any(r => oldWalks[r].Retired.Count > 0) || added.Any(a => walks[a].Retired.Count > 0))
        {
            retired = new HashSet<string>(previous.Retired, StringComparer.Ordinal);
            foreach (var r in removed)
                retired.ExceptWith(oldWalks[r].Retired);
            foreach (var a in added)
                retired.UnionWith(walks[a].Retired);
        }

        // Suppression entries: the previous count without the removed documents', with the added documents'.
        var suppressionCount = previous.SuppressionCount;
        foreach (var r in removed)
            suppressionCount -= old[r].Element.Suppressions.Count;
        foreach (var a in added)
            suppressionCount += documents[a].Element.Suppressions.Count;

        // References from a file: one group per document.
        var referencesFrom = new Dictionary<string, ReferenceInfo[]>(previous.ReferencesFrom, StringComparer.Ordinal);
        foreach (var owner in removedOwners)
            referencesFrom.Remove(owner);
        foreach (var a in added)
        {
            if (walks[a].References.Count > 0)
                referencesFrom[documents[a].Element.Id] = [.. walks[a].References];
        }

        // References to an id: in document (path) order, then walk order. Groups the change touches are rebuilt: the previous group
        // without the removed documents' references, with the added documents' references merged in by path.
        var referencesTo = new Dictionary<string, ReferenceInfo[]>(previous.ReferencesTo, StringComparer.Ordinal);
        var dirty = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var r in removed)
        {
            foreach (var reference in oldWalks[r].References)
                dirty.Add(reference.ToId);
        }

        var incoming = new Dictionary<string, List<(string Path, ReferenceInfo Reference)>>(StringComparer.Ordinal);
        foreach (var a in added) // in path order
        {
            foreach (var reference in walks[a].References)
            {
                dirty.Add(reference.ToId);
                if (!incoming.TryGetValue(reference.ToId, out var list))
                    incoming[reference.ToId] = list = [];
                list.Add((documents[a].Path, reference));
            }
        }

        foreach (var key in dirty)
        {
            var kept = previous.ReferencesTo.TryGetValue(key, out var existing)
                ? existing.Where(r => !removedOwners.Contains(r.FromElementId)).ToList()
                : [];
            if (incoming.TryGetValue(key, out var news))
            {
                var merged = new List<ReferenceInfo>(kept.Count + news.Count);
                var n = 0;
                foreach (var reference in kept)
                {
                    var path = docsById[reference.FromElementId].Path;
                    while (n < news.Count && string.CompareOrdinal(news[n].Path, path) < 0)
                        merged.Add(news[n++].Reference);
                    merged.Add(reference);
                }

                while (n < news.Count)
                    merged.Add(news[n++].Reference);
                kept = merged;
            }

            if (kept.Count == 0)
                referencesTo.Remove(key);
            else
                referencesTo[key] = [.. kept];
        }

        // Kinds the change touches: their sorted lists and id-set hashes again.
        var kinds = new HashSet<ElementKind>();
        foreach (var r in removed)
            kinds.Add(old[r].Element.Kind);
        foreach (var a in added)
            kinds.Add(documents[a].Element.Kind);
        var byKind = previous.ByKind.ToDictionary(kv => kv.Key, kv => kv.Value);
        var kindSetHashes = previous.KindSetHashes.ToDictionary(kv => kv.Key, kv => kv.Value);
        var removedElements = new HashSet<Element>(removed.Select(r => old[r].Element), ReferenceEqualityComparer.Instance);
        foreach (var kind in kinds)
        {
            var list = (byKind.TryGetValue(kind, out var current) ? current : []).Where(e => !removedElements.Contains(e)).ToList();
            foreach (var a in added)
            {
                if (documents[a].Element.Kind == kind)
                    list.Add(documents[a].Element);
            }

            if (list.Count == 0)
            {
                byKind.Remove(kind);
                kindSetHashes.Remove(kind);
                continue;
            }

            byKind[kind] = [.. list.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal)];
            kindSetHashes[kind] = KindSetHash(list);
        }

        // A process row lists the ids it names whose element is an actor: an id that becomes or stops being an actor recounts the kept
        // processes that name it. Only an added or removed actor document can do that.
        HashSet<string>? recount = null;
        foreach (var id in removed.Select(r => old[r].Element).Concat(added.Select(a => documents[a].Element)).OfType<Actor>().Select(e => e.Id))
        {
            var was = previous.DocumentsById.TryGetValue(id, out var before) && before.Element is Actor;
            var now = docsById.TryGetValue(id, out var after) && after.Element is Actor;
            if (was != now && referencesTo.TryGetValue(id, out var naming))
            {
                foreach (var reference in naming)
                    (recount ??= new(StringComparer.Ordinal)).Add(reference.FromElementId);
            }
        }

        // Summaries follow the documents.
        var summaries = ImmutableArray.CreateBuilder<ElementSummary>(documents.Length);
        var previousSummaries = previous.Summaries;
        var addedSet = new HashSet<int>(added);
        var o = 0;
        for (var d = 0; d < documents.Length; d++)
        {
            var element = documents[d].Element;
            if (addedSet.Contains(d))
            {
                summaries.Add(Summarize(element, documents[d].Hash, documents[d].Path, schemas,
                    element is Process ? ActorsOf(walks[d].References, docsById) : null));
                continue;
            }

            while (!ReferenceEquals(old[o], documents[d]))
                o++; // skip removed documents; the unchanged one is further on
            var row = previousSummaries[o++];
            if (element is Process && recount is not null && recount.Contains(element.Id))
                row = row with { Actors = ActorsOf(walks[d].References, docsById) };
            summaries.Add(row);
        }

        return previous with
        {
            Entries = entries,
            DocumentsById = docsById,
            ReferencesTo = referencesTo,
            ReferencesFrom = referencesFrom,
            ByKind = byKind.ToFrozenDictionary(),
            KindSetHashes = kindSetHashes.ToFrozenDictionary(),
            Summaries = summaries.MoveToImmutable(),
            Documents = documents,
            Walks = walks,
            HasDefaults = defaults.HasDefaults,
            Patched = true,
            Retired = retired,
            SuppressionCount = suppressionCount,
        };
    }

    /// <summary>The first verb vocabulary in path order whose id is registered first (a duplicate id is MQ1004), or <see langword="null"/>.</summary>
    private static VerbVocabulary? FirstVerbVocabulary(ImmutableArray<ElementDocument> documents)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in documents)
        {
            if (ids.Add(doc.Element.Id) && doc.Element is VerbVocabulary verbs)
                return verbs;
        }

        return null;
    }

    /// <summary>The own verbs uses resolve to: the first of each key in file order, built-in keys left out.</summary>
    private static FrozenDictionary<string, OwnVerb> OwnVerbsOf(VerbVocabulary? vocabulary)
    {
        if (vocabulary is null || vocabulary.Verbs.Count == 0)
            return FrozenDictionary<string, OwnVerb>.Empty;
        var map = new Dictionary<string, OwnVerb>(StringComparer.Ordinal);
        foreach (var verb in vocabulary.Verbs)
        {
            if (!BuiltInVerbs.IsBuiltIn(verb.Key))
                map.TryAdd(verb.Key, verb);
        }

        return map.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// The distinct verb keys a process's events, transitions, inline rights (rights-design.md section 8) and trigger points
    /// (trigger-points-design.md section 9) name, sorted ordinally; <see langword="null"/> when none. A use of a named right adds nothing here:
    /// the named right's row lists its verbs.
    /// </summary>
    internal static IReadOnlyList<string>? VerbsOf(Process process)
    {
        SortedSet<string>? keys = null;
        foreach (var e in process.Events)
        {
            if (e.Verb is { } verb)
                (keys ??= new(StringComparer.Ordinal)).Add(verb);
        }

        foreach (var t in process.Transitions)
        {
            if (t.Verb is { } verb)
                (keys ??= new(StringComparer.Ordinal)).Add(verb);
        }

        foreach (var right in process.Rights)
        {
            foreach (var verb in right.Verbs)
                (keys ??= new(StringComparer.Ordinal)).Add(verb);
        }

        foreach (var point in process.TriggerPoints)
        {
            foreach (var verb in point.Verbs)
                (keys ??= new(StringComparer.Ordinal)).Add(verb);
        }

        return keys is null ? null : [.. keys];
    }

    /// <summary>The distinct verb keys a list names, sorted ordinally; <see langword="null"/> when none.</summary>
    private static IReadOnlyList<string>? SortedKeys(IEnumerable<string> keys)
    {
        var sorted = new SortedSet<string>(keys, StringComparer.Ordinal);
        return sorted.Count == 0 ? null : [.. sorted];
    }

    /// <summary>A right's access as its file writes it.</summary>
    internal static string AccessName(RightAccess access) => access == RightAccess.Deny ? "deny" : "allow";

    /// <summary>
    /// A process row's members for the rights naming key (rights-design.md section 6), each <see langword="null"/> unless the process writes
    /// <c>rights</c> or <c>rightsDefault</c>: its default as written, the verbs its inline deny rights name, and the named rights its uses name;
    /// and, for the freezing naming key, whether an inline right (or a named right's own row) has <c>whileFrozen</c>.
    /// </summary>
    private static ElementSummary WithRights(ElementSummary row, Element element)
    {
        if (element is Right { WhileFrozen: true })
            return row with { WhileFrozen = true };
        if (element is not Process process || (process.Rights.Count == 0 && process.RightsDefault is null))
            return row;
        return row with
        {
            RightsDefault = process.RightsDefault is { } written ? AccessName(written) : null,
            DenyVerbs = SortedKeys(process.Rights.Where(r => !r.IsUse && r.Access == RightAccess.Deny).SelectMany(r => r.Verbs)),
            RightUses = SortedKeys(process.Rights.Select(r => r.Right).OfType<string>()),
            WhileFrozen = process.Rights.Any(r => !r.IsUse && r.WhileFrozen),
        };
    }

    /// <summary>
    /// A process row's trigger point members (trigger-points-design.md sections 7 and 9), each <see langword="null"/> unless the process has
    /// <c>triggerPoints</c>: the count, the verbs its points name, the verbs its replaces name, and for the trigger points naming key each
    /// replace's verbs and entities.
    /// </summary>
    private static ElementSummary WithTriggerPoints(ElementSummary row, Element element)
    {
        if (element is not Process { TriggerPoints.Count: > 0 } process)
            return row;
        var replaces = process.TriggerPoints.Where(p => p.Type == TriggerPointType.Replace).ToList();
        return row with
        {
            TriggerPoints = process.TriggerPoints.Count,
            TriggerVerbs = SortedKeys(process.TriggerPoints.SelectMany(p => p.Verbs)),
            Replaces = SortedKeys(replaces.SelectMany(p => p.Verbs)),
            ReplaceScopes = replaces.Count == 0
                ? null
                : [.. replaces.Select(p => string.Join(',', p.Verbs.Order(StringComparer.Ordinal)) + "|" + string.Join(',', p.Entities.Order(StringComparer.Ordinal)))
                    .Order(StringComparer.Ordinal)],
        };
    }

    /// <summary>
    /// A process row's members for the approvals naming key (signoff-design.md section 7), each left at its default unless they apply:
    /// whether a gate on its transitions has stages, the gate policies its transitions use, and for a library the gates with stages it holds;
    /// and the part 2 groups its inline gates use and, for a library, each gate policy uses (decision 32).
    /// </summary>
    private static ElementSummary WithApprovals(ElementSummary row, Element element)
    {
        if (element is not Process process)
            return row;
        var inline = false;
        var groups = ApprovalGroups.None;
        SortedSet<string>? policies = null;
        foreach (var transition in process.Transitions)
        {
            if (transition.Gate is { UsesStages: true } gate)
            {
                inline = true;
                groups |= ApprovalGroupMarks.Of(gate);
            }

            if (transition.GatePolicy is { } policy)
                (policies ??= new(StringComparer.Ordinal)).Add(policy);
        }

        var approvals = SortedKeys(process.Gates.Where(static g => g.UsesStages).Select(static g => g.Id));
        // The part 2 groups each gate policy with stages uses (signoff-design.md decision 32), only for those that use one.
        var policyGroups = process.Gates.Select(static g => (Policy: g.Id, Groups: ApprovalGroupMarks.Of(g))).Where(static p => p.Groups != ApprovalGroups.None)
            .OrderBy(static p => p.Policy, StringComparer.Ordinal).ToList();
        if (!inline && policies is null && approvals is null)
            return row;
        return row with
        {
            InlineApproval = inline, GatePolicyUses = policies is null ? null : [.. policies], ApprovalGatePolicies = approvals,
            InlineApprovalGroups = groups, ApprovalPolicyGroups = policyGroups.Count == 0 ? null : policyGroups,
        };
    }

    /// <summary>
    /// The hash of a kind's id set. A library counts apart from itself as a process that runs, since <c>model.processes</c> and
    /// <c>model.process_libraries</c> list them apart; a model with no library hashes as ids alone.
    /// </summary>
    private static string KindSetHash(IEnumerable<Element> elements) =>
        HashBuilder.Of([.. elements.Select(static e => e is Process { Use: ProcessUse.Library } ? e.Id + "|library" : e.Id).Order(StringComparer.Ordinal)]);

    /// <summary>Whether a change to this element could change how other documents' seed cells and codes are indexed.</summary>
    private static bool AffectsReferenceData(Element element, ReferenceDataIndex referenceData)
    {
        if (element is Seed or ReferenceType)
            return true;
        foreach (var attribute in ReferenceDataIndex.AttributesOf(element))
        {
            if (referenceData.SeedColumns.Contains(attribute.Id))
                return true;
        }

        return element is Relation relation && relation.Ends.Any(e => referenceData.SeedColumns.Contains(e.Id));
    }

    /// <summary>
    /// Whether a patch's change moves what a lifecycle's state codes are indexed through (lifecycle-design.md decision 18): a process whose
    /// bindings (or, with bindings, its use) differ before and after, or a bound attribute retyped, gone from the documents that held it, or
    /// new in a changed document.
    /// </summary>
    /// <param name="removed">The elements of the removed documents (the versions before).</param>
    /// <param name="added">The elements of the added documents (the versions after).</param>
    /// <param name="referenceData">The model-wide facts the patch keeps.</param>
    /// <returns><see langword="true"/> when the patch must build in full.</returns>
    private static bool MovesBindings(IReadOnlyList<Element> removed, IReadOnlyList<Element> added, ReferenceDataIndex referenceData)
    {
        static string Bindings(Process process)
        {
            var bound = process.Subjects.Where(s => s.BoundAttribute is not null).Select(s => (s.Entity ?? "@" + s.Stereotype) + "|" + s.BoundAttribute).ToList();
            return bound.Count == 0 ? "" : process.Use + ";" + string.Join(";", bound);
        }

        var before = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var element in removed)
        {
            if (element is Process process)
                before.TryAdd(process.Id, Bindings(process));
        }

        var after = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var element in added)
        {
            after.TryAdd(element.Id, element);
            if (element is Process process && !string.Equals(Bindings(process), before.GetValueOrDefault(process.Id, ""), StringComparison.Ordinal))
                return true;
        }

        foreach (var (id, bindings) in before)
        {
            if (bindings.Length > 0 && after.GetValueOrDefault(id) is not Process)
                return true; // a bound process removed
        }

        if (referenceData.BoundAttributes.Count == 0)
            return false;

        // Both ways: a bound attribute retyped or gone, and one that comes back (or arrives) in a changed document, since the kept
        // attribute types know only the attributes the full build saw.
        var was = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        foreach (var element in removed)
        {
            foreach (var attribute in ReferenceDataIndex.AttributesOf(element))
            {
                if (referenceData.BoundAttributes.Contains(attribute.Id))
                    was.TryAdd(attribute.Id, attribute.Type);
            }
        }

        var @is = new Dictionary<string, TypeRef>(StringComparer.Ordinal);
        foreach (var element in added)
        {
            foreach (var attribute in ReferenceDataIndex.AttributesOf(element))
            {
                if (referenceData.BoundAttributes.Contains(attribute.Id))
                    @is.TryAdd(attribute.Id, attribute.Type);
            }
        }

        foreach (var (id, type) in was)
        {
            if (!@is.TryGetValue(id, out var now) || !Equals(now, type))
                return true;
        }

        foreach (var (id, type) in @is)
        {
            if (!was.TryGetValue(id, out var then) || !Equals(then, type))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a patch's change can move the stereotype holder of a document it does not walk again (next-phase-2-design.md section
    /// 3.1): a package, database or mapping changed, added or removed (package chains, schema entries, routing); or an element a holder
    /// falls back to (an entity, relation, process, reference type or table) added, removed, or changed in its package, or a table in
    /// its schema, entity, relation, attribute or origin (an overlay's schema routes its child tables, a table is a seed's target).
    /// </summary>
    /// <param name="removed">The elements of the removed documents.</param>
    /// <param name="added">The elements of the added documents.</param>
    /// <returns><see langword="true"/> when the patch must build in full.</returns>
    private static bool MovesHolders(IReadOnlyList<Element> removed, IReadOnlyList<Element> added)
    {
        var before = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var element in removed)
        {
            if (element is Package or Database or Mapping)
                return true;
            if (element is Entity or Relation or Process or ReferenceType or Table)
                before.TryAdd(element.Id, element);
        }

        var after = new Dictionary<string, Element>(StringComparer.Ordinal);
        foreach (var element in added)
        {
            if (element is Package or Database or Mapping)
                return true;
            if (element is Entity or Relation or Process or ReferenceType or Table)
                after.TryAdd(element.Id, element);
        }

        if (before.Count != after.Count)
            return true;
        foreach (var (id, old) in before)
        {
            if (!after.TryGetValue(id, out var now) || old.Kind != now.Kind || !string.Equals(PackageOf(old), PackageOf(now), StringComparison.Ordinal))
                return true;
            if (old is Table t && now is Table u && (t.Origin != u.Origin || !string.Equals(t.Schema, u.Schema, StringComparison.Ordinal)
                || !string.Equals(t.Entity, u.Entity, StringComparison.Ordinal) || !string.Equals(t.Relation, u.Relation, StringComparison.Ordinal)
                || !string.Equals(t.Attribute, u.Attribute, StringComparison.Ordinal) || !string.Equals(t.Database, u.Database, StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The index row of an element (E4 and E5 members included; a custom element's type and member count,
    /// next-phase-2-design.md section 4.2).</summary>
    private static ElementSummary Summarize(Element element, string hash, string path, PropertySchemaSet schemas, IReadOnlyList<string>? actors) =>
        WithApprovals(WithTriggerPoints(WithRights(new(element.Id, element.KindName, element.Name, PackageOf(element), element.Tags, hash, path, element.Category, element.Stereotypes,
            DisplayName: element.DisplayName,
            Database: element switch
            {
                Table t => t.Database,
                View v => v.Database,
                Sequence q => q.Database,
                Routine r => r.Database,
                DatabaseType t => t.Database,
                SqlObject o => o.Database,
                Query q => q.Database,
                Mapping m => m.Database,
                Migration f => f.Database,
                _ => null,
            },
            Entity: element switch
            {
                Mapping m => m.Entity,
                Table t => t.Entity,
                _ => null,
            },
            MemberCount: element switch
            {
                Diagram diagram => diagram.Members.Count,
                Custom custom => MembersOf(custom, schemas).Count,
                _ => null,
            },
            Ends: element is Relation relation ? EndsOf(relation) : null,
            Target: element is Seed seed ? seed.Target : null,
            RowCount: element is Seed rows ? rows.Rows.Count : null,
            FieldCount: element is ReferenceType referenceType ? referenceType.Attributes.Count : null,
            Base: element is Entity entity ? entity.Base : null,
            Use: element is Process use ? (use.Use switch { ProcessUse.Lifecycle => "lifecycle", ProcessUse.Library => "library", _ => "orchestration" }) : null,
            Subject: element switch
            {
                Process { Subjects: [var one] } => one.Entity, // exactly one subject, in either form (lifecycle-design.md decision 23)
                Operation operation => operation.Subject,
                _ => null,
            },
            StateCount: element is Process states ? CountStates(states.States) : null,
            ActorType: element is Actor actor ? actor.Type switch
            {
                Model.ActorType.Person => "person",
                Model.ActorType.Role => "role",
                Model.ActorType.Group => "group",
                Model.ActorType.Organization => "organization",
                Model.ActorType.System => "system",
                _ => "external-system",
            } : null,
            Process: element switch
            {
                Scenario scenario => scenario.Process,
                Diagram statechart => statechart.Process,
                _ => null,
            },
            StepCount: element is Scenario steps ? steps.Steps.Count : null,
            Effect: element is Operation effect ? (effect.Effect == OperationEffect.Change ? "change" : "read") : null,
            Type: element switch
            {
                Custom typed => typed.Type,
                TriggerPoint { Type: var pointType } => pointType switch
                {
                    TriggerPointType.Check => "check",
                    TriggerPointType.Action => "action",
                    _ => "replace",
                },
                _ => null,
            },
            Subjects: element switch
            {
                Process { Subjects.Count: > 1 } several when EntityEntries(several) is { Count: > 1 } named => named,
                TriggerPoint point when point.Subjects.Select(s => s.Entity).OfType<string>().ToList() is { Count: > 0 } covering => covering,
                _ => null,
            },
            Actors: actors,
            MemberOf: element is Actor { MemberOf.Count: > 0 } member ? [.. member.MemberOf] : null,
            Relative: element is Actor { Relative: { } relative } ? relative.Form : null,
            Participants: element is Scenario { Participants.Count: > 0 } cast ? cast.Participants.Count : null,
            Verb: element is Operation { Verb: { } operationVerb } ? operationVerb : null,
            Verbs: element switch
            {
                Process verbProcess => VerbsOf(verbProcess),
                Right namedRight => SortedKeys(namedRight.Verbs),
                TriggerPoint namedPoint => SortedKeys(namedPoint.Verbs),
                _ => null,
            },
            Rights: element is Process { Rights.Count: > 0 } withRights ? withRights.Rights.Count : null,
            Access: element is Right access ? AccessName(access.Access) : null,
            Approvals: element is Process approvalProcess && ApprovalGates(approvalProcess).Count() is > 0 and var approvals ? approvals : null,
            Signatures: element is Process signatureProcess && ApprovalGates(signatureProcess).Sum(static g => g.AllSignatures.Count()) is > 0 and var signatures
                ? signatures
                : null), element), element), element);

    /// <summary>
    /// A process's gates that have stages (signoff-design.md section 9): each transition's inline gate in transition order, then a library's gate
    /// policies in file order. A gate policy another process uses counts on its library's row only.
    /// </summary>
    private static IEnumerable<ProcessGate> ApprovalGates(Process process) =>
        process.Transitions.Select(static t => t.Gate).Concat(process.Gates).OfType<ProcessGate>().Where(static g => g.UsesStages);

    /// <summary>Whether a relation end's <c>types</c> holds a stereotype entry.</summary>
    private static bool EndNamesStereotype(RelationEnd end) => end.Types is { } types && types.Any(t => t.Stereotype is not null);

    /// <summary>An end's summary with the stereotypes its entries name, by id, and their carriers (several-types-design.md section 9).</summary>
    private static RelationEndSummary WithStereotypes(ModelSnapshot model, RelationEndSummary summary, RelationEnd end)
    {
        var stereotypes = new List<string>();
        var covers = new List<string>();
        foreach (var entry in end.Types ?? [])
        {
            if (entry.Stereotype is not { } key)
                continue;
            if (model.GetStereotype(key) is { } stereotype)
                stereotypes.Add(stereotype.Id);
            foreach (var carrier in EndCoverage.CarriersOf(model, key))
            {
                if (!covers.Contains(carrier.Id, StringComparer.Ordinal))
                    covers.Add(carrier.Id);
            }
        }

        return summary with { Stereotypes = stereotypes.Count > 0 ? stereotypes : null, Covers = covers.Count > 0 ? covers : null };
    }

    /// <summary>The entities a process's entity entries name, in entry order (a stereotype entry is not counted).</summary>
    private static List<string> EntityEntries(Process process) => [.. process.Subjects.Select(s => s.Entity).OfType<string>()];

    /// <summary>
    /// The index rows of a snapshot with each process row that has a stereotype entry completed (stereotype-sets-design.md section 8): its
    /// <see cref="ElementSummary.SubjectStereotypes"/> and <see cref="ElementSummary.Covers"/>, its <see cref="ElementSummary.Subjects"/>
    /// from one entity entry on, and no <see cref="ElementSummary.Subject"/>.
    /// Carriers follow package defaults, implied stereotypes and bases, which other files decide, so this runs per snapshot, and only while
    /// some entry names a stereotype: otherwise the rows are returned as they are.
    /// </summary>
    /// <param name="model">The snapshot.</param>
    /// <param name="rows">Its rows, as the index built them.</param>
    /// <returns>The rows.</returns>
    internal static IReadOnlyList<ElementSummary> WithSubjectCoverage(ModelSnapshot model, ImmutableArray<ElementSummary> rows)
    {
        var namedPoints = Processes.NamedTriggerPoints.Of(model);
        var endsNameStereotypes = EndCoverage.Of(model).Any && model.All<Relation>().Any(r => r.Ends.Any(EndNamesStereotype));
        if (!Processes.SubjectCoverage.Of(model).Any && !namedPoints.All.Any(p => p.Subjects.Any(s => s.Stereotype is not null)) && !endsNameStereotypes)
            return rows;
        var result = rows.ToBuilder();
        for (var i = 0; i < result.Count; i++)
        {
            var row = result[i];
            // A relation end that names stereotypes lists them by id (an unknown key is left out) and their carriers, each entry's by qualified
            // name, then id, in entry order, without repeats (several-types-design.md section 9).
            if (row.Kind == "relation")
            {
                if (endsNameStereotypes && row.Ends is { } summaries && model.Get<Relation>(row.Id) is { } relation && relation.Ends.Any(EndNamesStereotype))
                    result[i] = row with { Ends = [.. summaries.Select((s, n) => n < relation.Ends.Count && EndNamesStereotype(relation.Ends[n]) ? WithStereotypes(model, s, relation.Ends[n]) : s)] };
                continue;
            }

            // A named trigger point's row names the stereotypes its subjects name, by id; an unknown key is left out (section 9, part 2).
            if (row.Kind == "trigger-point" && model.Get<TriggerPoint>(row.Id) is { } point)
            {
                var ids = point.Subjects.Select(s => s.Stereotype is { } key ? model.GetStereotype(key)?.Id : null).OfType<string>().ToList();
                if (ids.Count > 0)
                    result[i] = row with { SubjectStereotypes = ids };
                continue;
            }

            if (row.Kind != "process" || model.Get<Process>(row.Id) is not { } process || !Processes.SubjectCoverage.NamesStereotype(process))
                continue;
            var stereotypes = new List<string>();
            var covers = new List<string>();
            foreach (var entry in process.Subjects)
            {
                if (entry.Stereotype is not { } key)
                    continue;
                if (model.GetStereotype(key) is { } stereotype)
                    stereotypes.Add(stereotype.Id);
                foreach (var carrier in Processes.SubjectCoverage.Governed(model, process, entry))
                {
                    if (!covers.Contains(carrier.Id, StringComparer.Ordinal))
                        covers.Add(carrier.Id);
                }
            }

            // The entity entries, even one: beside a stereotype entry the row has no one subject to name (section 8).
            var named = EntityEntries(process);
            result[i] = row with
            {
                Subject = null,
                Subjects = named.Count > 0 ? named : null,
                SubjectStereotypes = stereotypes.Count > 0 ? stereotypes : null,
                Covers = covers.Count > 0 ? covers : null,
            };
        }

        return result.ToImmutable();
    }

    /// <summary>
    /// The actors a process's document names: the targets of its references (every field, custom properties included) whose element is
    /// an actor, distinct and sorted ordinally; <see langword="null"/> when there are none, so the row leaves the member out.
    /// </summary>
    /// <param name="references">The references the document's walk collected.</param>
    /// <param name="documentsById">The model's top-level documents by id, complete for the snapshot being built.</param>
    /// <returns>The actor ids, or <see langword="null"/>.</returns>
    private static string[]? ActorsOf(List<ReferenceInfo> references, IReadOnlyDictionary<string, ElementDocument> documentsById)
    {
        SortedSet<string>? actors = null;
        foreach (var reference in references)
        {
            if (documentsById.TryGetValue(reference.ToId, out var target) && target.Element is Actor)
                (actors ??= new(StringComparer.Ordinal)).Add(reference.ToId);
        }

        return actors is null ? null : [.. actors];
    }

    /// <summary>
    /// The members of a custom element (next-phase-2-design.md section 4.2), in walk order, through its custom kind's member tree; none
    /// when the type is undeclared or the custom kind has no <c>x-member</c> array.
    /// </summary>
    /// <param name="custom">The custom element.</param>
    /// <param name="schemas">The snapshot's property schema set.</param>
    /// <returns>The members.</returns>
    internal static List<MemberSite> MembersOf(Custom custom, PropertySchemaSet schemas) =>
        schemas.ContentOf(custom.Type) is { Members.Count: > 0 } source ? XMemberNode.Walk(source.Members, custom.Content) : [];

    private static int CountStates(IReadOnlyList<ProcessState> states)
    {
        var count = states.Count;
        foreach (var state in states)
            count += CountStates(state.States);
        return count;
    }

    /// <summary>
    /// A relation's ends as its row states them, from its own file (several-types-design.md section 9): the one entity of a one-type end, the
    /// entity entries of an end that writes <c>types</c>, and <c>anyEntity</c>. The stereotypes an end names, by id, and their carriers
    /// (<c>covers</c>) need other files, so <see cref="WithSubjectCoverage"/> fills them per snapshot.
    /// </summary>
    private static RelationEndSummary[] EndsOf(Relation relation)
    {
        var ends = new RelationEndSummary[relation.Ends.Count];
        for (var i = 0; i < ends.Length; i++)
        {
            var end = relation.Ends[i];
            ends[i] = new RelationEndSummary(end.SingleEntity, end.Role)
            {
                Entities = end.Types is { } types && types.Any(t => t.Entity is not null) ? [.. types.Select(t => t.Entity).OfType<string>()] : null,
                AnyEntity = end.AnyEntity == true,
            };
        }

        return ends;
    }

    /// <summary>The package the file states: a package's parent, any other kind's <c>package</c> (a vocabulary's scope); null for
    /// a migration fact. No fallback to an entity's or a target's package: layering and the index read only the file.</summary>
    internal static string? PackageOf(Element element) => element switch
    {
        Package p => p.Parent,
        IPackaged x => x.Package,
        _ => null,
    };

    private static string Escape(string name) => name.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>Groups references by a key, keeping their order within each group (as <c>GroupBy</c> does).</summary>
    private static Dictionary<string, ReferenceInfo[]> Group(List<ReferenceInfo> references, Func<ReferenceInfo, string> key)
    {
        var groups = new Dictionary<string, List<ReferenceInfo>>(StringComparer.Ordinal);
        foreach (var reference in references)
        {
            var k = key(reference);
            if (!groups.TryGetValue(k, out var list))
                groups[k] = list = new List<ReferenceInfo>(2);
            list.Add(reference);
        }

        var result = new Dictionary<string, ReferenceInfo[]>(groups.Count, StringComparer.Ordinal);
        foreach (var (k, list) in groups)
            result[k] = [.. list];
        return result;
    }

    /// <summary>Property metadata by type, shared by the parallel walks of one build (computed once per type; lookups take no lock).</summary>
    internal sealed class MetadataCache
    {
        // Read once per object walked, from every walk thread: lookups take no lock (a lock here was contended by the parallel walks
        // of a full build). Two threads that miss the same type may both compute it; the result is a pure function of the type and
        // the first stored array is the one every caller gets.
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyMeta[]> _meta = new();

        public PropertyMeta[] Get(Type type) => _meta.TryGetValue(type, out var meta) ? meta : _meta.GetOrAdd(type, Compute(type));

        private static PropertyMeta[] Compute(Type type)
        {
            var list = new List<PropertyMeta>();
            var info = EngineJson.Options.GetTypeInfo(type);
            if (info.Kind == JsonTypeInfoKind.Object)
            {
                foreach (var property in info.Properties)
                {
                    if (property.Get is null)
                        continue;
                    var role = DocumentWalk.RoleOf(property);
                    var reference = property.AttributeProvider is PropertyInfo pi ? pi.GetCustomAttribute<ElementRefAttribute>(inherit: true) : null;
                    if (role is not null)
                        list.Add(new PropertyMeta(property.Name, property.Get, role.Value, reference is { Owning: true }, reference is { Words.Length: > 0 } ? reference.Words : null));
                }
            }

            return [.. list];
        }
    }

    /// <summary>The walk of one document: its sub-element entries (in walk order) and its references.</summary>
    internal sealed class DocumentWalk(IReadOnlyDictionary<string, Stereotype> stereotypes, MetadataCache metadata, ReferenceDataIndex referenceData,
        PropertySchemaSet schemas, StereotypeDefaults defaults, IReadOnlyDictionary<string, OwnVerb> ownVerbs)
    {
        public List<(string Id, IndexEntry Entry)> Entries { get; } = [];

        public List<ReferenceInfo> References { get; } = [];

        /// <summary>The ids of the element and sub-elements of this document whose status is retired, in walk order.</summary>
        public List<string> Retired { get; } = [];

        public void WalkElement(Element element)
        {
            // The element itself does not pass through WalkObject, so its own status is recorded here.
            if (element.Status == ElementStatus.Retired)
                Retired.Add(element.Id);
            WalkProperties(element, "", element.Id, element.Id);
            if (element is Custom custom)
                WalkMembers(custom);
            WalkReferenceData(element);
        }

        /// <summary>Registers each member of a custom element that has an id as a sub-element of kind <c>member</c> (next-phase-2-design.md
        /// section 4.2), owned by the custom element, in walk order.</summary>
        private void WalkMembers(Custom custom)
        {
            foreach (var member in MembersOf(custom, schemas))
            {
                if (member.Id is { } id)
                    Entries.Add((id, new IndexEntry(id, custom.Id, "member", member.Pointer)));
            }
        }

        /// <summary>
        /// References that no property attribute declares: a reference type's <c>storage</c> keys (database ids), the codes that
        /// reference-typed defaults and <c>allowedValues</c> hold (to the row with that code), seed cells (codes to rows, end
        /// cells to the row ids they hold), and the ids at the <c>x-ref</c> sites of custom properties.
        /// </summary>
        private void WalkReferenceData(Element element)
        {
            WalkPropertyReferences(element);

            if (element is ReferenceType type)
            {
                foreach (var key in type.Storage.Keys.Order(StringComparer.Ordinal))
                {
                    if (key != "*")
                        References.Add(new ReferenceInfo(type.Id, type.Id, "/storage/" + Escape(key), "storage", key));
                }
            }

            var attributes = ReferenceDataIndex.AttributesOf(element);
            for (var i = 0; i < attributes.Count; i++)
            {
                var attribute = attributes[i];
                if (attribute.Type.Ref is not { } typeId || !referenceData.ReferenceTypeIds.Contains(typeId))
                    continue;
                var pointer = "/attributes/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (attribute.Default is { } literal)
                    AddCodes(element.Id, attribute.Id, pointer + "/default", "default", typeId, literal);
                if (attribute.Validation is { } validation)
                {
                    for (var j = 0; j < validation.AllowedValues.Count; j++)
                        AddCode(element.Id, attribute.Id, pointer + "/validation/allowedValues/" + j.ToString(System.Globalization.CultureInfo.InvariantCulture), "allowedValues", typeId, validation.AllowedValues[j]);
                }
            }

            if (element is Process { Use: ProcessUse.Lifecycle } lifecycle)
                WalkStateCodes(lifecycle);
            if (element is not Seed seed)
                return;
            for (var k = 0; k < seed.Columns.Count; k++)
            {
                var column = seed.Columns[k];
                var isEnd = referenceData.EndIds.Contains(column);
                referenceData.AttributeTypes.TryGetValue(column, out var columnType);
                if (!isEnd && columnType is null)
                    continue;
                var suffix = "/values/" + k.ToString(System.Globalization.CultureInfo.InvariantCulture);
                for (var r = 0; r < seed.Rows.Count; r++)
                {
                    var row = seed.Rows[r];
                    if (k >= row.Values.Count)
                        continue;
                    var cell = row.Values[k];
                    var pointer = "/rows/" + r.ToString(System.Globalization.CultureInfo.InvariantCulture) + suffix;
                    if (isEnd)
                    {
                        if (cell.ValueKind == System.Text.Json.JsonValueKind.String && cell.GetString() is { } rowId && IdFormat.IsValid(rowId))
                            References.Add(new ReferenceInfo(seed.Id, row.Id, pointer, "values", rowId));
                    }
                    else
                    {
                        AddCodes(seed.Id, row.Id, pointer, "values", columnType!, cell);
                    }
                }
            }
        }

        /// <summary>
        /// The ids at the <c>x-ref</c> sites of the document's custom properties (next-phase-1-design.md section 4.1): the holder is
        /// <see cref="ReferenceInfo.FromId"/> and the top-level property the field. A value is an id, or ids joined by <c>.</c> (the
        /// keyed id of an inherited member), each a reference; text is data, which validation reports. One value several schemas
        /// type is one reference, naming the first schema, optional only when every one of them says so.
        /// </summary>
        private void WalkPropertyReferences(Element element)
        {
            if (element is Stereotype { Parameters: { } parameters })
                WalkParameterScopes(element.Id, parameters);
            if (!schemas.HasRefs)
                return;
            Dictionary<(string Pointer, string To), int>? seen = null;
            // The element's properties are typed by its effective keys, defaults included (next-phase-2-design.md section 3.1), as
            // ModelSnapshot.PropertyKeys gives them once the snapshot exists.
            var keys = defaults.HasDefaults
                ? PropertySchemaSet.HostKeys(defaults.Effective(defaults.HolderOf(element), key => stereotypes.GetValueOrDefault(key)), element.Stereotypes)
                : null;
            foreach (var site in schemas.Sites(element, keys))
            {
                foreach (var id in IdsOf(site.Value))
                {
                    seen ??= [];
                    if (seen.TryGetValue((site.Pointer, id), out var at))
                    {
                        if (References[at].Optional && !site.Ref.Optional)
                            References[at] = References[at] with { Optional = false };
                        continue;
                    }

                    seen[(site.Pointer, id)] = References.Count;
                    References.Add(new ReferenceInfo(element.Id, site.Host.Id, site.Pointer, site.Field, id, false, site.Ref.Optional, site.Source.Schema));
                }
            }
        }

        /// <summary>
        /// The element ids a stereotype's <c>parameters</c> name in an <c>x-ref</c> <c>of</c> (next-phase-1-design.md section 4.2),
        /// each a reference under the field <c>of</c>, so the list it scopes to cannot be deleted plainly. Malformed keywords are no
        /// reference (validation reports them, MQ2009).
        /// </summary>
        private void WalkParameterScopes(string stereotypeId, StereotypeParameters parameters)
        {
            var (_, keywords, _) = PropertySchemaSet.Read(parameters.Properties, "/parameters/properties", static (_, _) => { });
            foreach (var (pointer, xref) in keywords)
            {
                if (xref.Of is { } of && of != XRef.ElementScope)
                    References.Add(new ReferenceInfo(stereotypeId, stereotypeId, pointer + "/of", "of", of));
            }
        }

        private static IEnumerable<string> IdsOf(string value)
        {
            if (IdFormat.IsValid(value))
            {
                yield return value;
                yield break;
            }

            if (!value.Contains('.', StringComparison.Ordinal))
                yield break;
            var segments = value.Split('.');
            if (!segments.All(IdFormat.IsValid))
                yield break;
            string? previous = null;
            foreach (var segment in segments)
            {
                if (segment != previous)
                    yield return segment;
                previous = segment;
            }
        }

        private void AddCodes(string ownerId, string fromId, string pointer, string field, string typeId, System.Text.Json.JsonElement value)
        {
            if (value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                AddCode(ownerId, fromId, pointer, field, typeId, value);
                return;
            }

            var i = 0;
            foreach (var item in value.EnumerateArray())
                AddCode(ownerId, fromId, pointer + "/" + (i++).ToString(System.Globalization.CultureInfo.InvariantCulture), field, typeId, item);
        }

        /// <summary>
        /// A lifecycle's state codes under its reference bindings (lifecycle-design.md decision 18): each code is a use of the row of each
        /// bound reference type that holds it, field <c>code</c>, from the state, so <c>get_references</c> sees it, deleting the row is
        /// refused, and the code refactors rewrite it. A lifecycle with no reference binding adds nothing.
        /// </summary>
        private void WalkStateCodes(Process process)
        {
            List<string>? types = null;
            foreach (var entry in process.Subjects)
            {
                if (entry.BoundAttribute is { } bound && referenceData.AttributeTypes.TryGetValue(bound, out var typeId) && !(types ??= []).Contains(typeId, StringComparer.Ordinal))
                    types.Add(typeId);
            }

            if (types is not null)
                WalkStates(process.Id, process.States, "", types);
        }

        private void WalkStates(string processId, IReadOnlyList<ProcessState> states, string pointer, List<string> types)
        {
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var at = pointer + "/states/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (state.Code is { } code)
                {
                    foreach (var typeId in types)
                    {
                        if (referenceData.RowOf(typeId, code) is { } rowId) // each type at most once, and rows of two types never share an id
                            References.Add(new ReferenceInfo(processId, state.Id, at + "/code", "code", rowId));
                    }
                }

                WalkStates(processId, state.States, at, types);
            }
        }

        private void AddCode(string ownerId, string fromId, string pointer, string field, string typeId, System.Text.Json.JsonElement code)
        {
            if (referenceData.RowOf(typeId, code) is { } rowId)
                References.Add(new ReferenceInfo(ownerId, fromId, pointer, field, rowId));
        }

        private void WalkObject(object value, string pointer, string ownerId, string fromId)
        {
            var subKind = SubElementKind(value.GetType());
            if (subKind is not null && IdOf(value) is { } id)
            {
                Entries.Add((id, new IndexEntry(id, ownerId, subKind, pointer)));
                if (value is ElementBase { Status: ElementStatus.Retired })
                    Retired.Add(id);
                fromId = id;
            }

            WalkProperties(value, pointer, ownerId, fromId);
        }

        private void WalkProperties(object value, string pointer, string ownerId, string fromId)
        {
            foreach (var property in metadata.Get(value.GetType()))
            {
                var child = property.Get(value);
                if (child is null)
                    continue;
                var childPointer = pointer + "/" + Escape(property.Name);
                switch (property.Role)
                {
                    case PropertyRole.Reference when child is string id:
                        if (!property.IsWord(id))
                            References.Add(new ReferenceInfo(ownerId, fromId, childPointer, property.Name, id, property.Owning));
                        break;
                    case PropertyRole.Reference when child is IDictionary map:
                        // A map keyed by ids (a scenario's context and payload values, guard assumptions): each key is a reference.
                        foreach (var key in map.Keys.Cast<string>().Order(StringComparer.Ordinal))
                            References.Add(new ReferenceInfo(ownerId, fromId, childPointer + "/" + Escape(key), property.Name, key));
                        break;
                    case PropertyRole.Reference when child is IEnumerable<string> ids:
                        var i = 0;
                        foreach (var item in ids)
                        {
                            if (!property.IsWord(item))
                                References.Add(new ReferenceInfo(ownerId, fromId, childPointer + "/" + i, property.Name, item));
                            i++;
                        }

                        break;
                    case PropertyRole.KeyedReference when child is string key:
                        AddKeyed(ownerId, fromId, childPointer, property.Name, key);
                        break;
                    case PropertyRole.KeyedReference when child is IEnumerable<string> keys:
                        var k = 0;
                        foreach (var item in keys)
                            AddKeyed(ownerId, fromId, childPointer + "/" + k++, property.Name, item);
                        break;
                    case PropertyRole.StereotypeKeys when child is string single:
                        // One key (a subject entry's stereotype, stereotype-sets-design.md decision 2); an unknown key is no reference (MQ2003).
                        if (stereotypes.TryGetValue(single, out var named))
                            References.Add(new ReferenceInfo(ownerId, fromId, childPointer, property.Name, named.Id));
                        break;
                    case PropertyRole.StereotypeKeys when child is IEnumerable<string> stereotypeKeys:
                        var j = 0;
                        foreach (var item in stereotypeKeys)
                        {
                            // An unknown key is no reference; validation reports it (MQ2003).
                            if (stereotypes.TryGetValue(item, out var stereotype))
                                References.Add(new ReferenceInfo(ownerId, fromId, childPointer + "/" + j, property.Name, stereotype.Id));
                            j++;
                        }

                        break;
                    case PropertyRole.VerbKey when child is string verbKey:
                        // An own verb's key is a reference to its id (verbs-design.md decision 7); a built-in or unknown key is none (MQ9701).
                        if (ownVerbs.TryGetValue(verbKey, out var ownVerb))
                            References.Add(new ReferenceInfo(ownerId, fromId, childPointer, property.Name, ownVerb.Id));
                        break;
                    case PropertyRole.VerbKey when child is IEnumerable<string> verbKeys:
                        // A list of verb keys (a right's verbs, rights-design.md decision 5): each own verb's key is a reference, as above.
                        var v = 0;
                        foreach (var item in verbKeys)
                        {
                            if (ownVerbs.TryGetValue(item, out var listed))
                                References.Add(new ReferenceInfo(ownerId, fromId, childPointer + "/" + v, property.Name, listed.Id));
                            v++;
                        }

                        break;
                    case PropertyRole.TypeRef when child is TypeRef { Ref: { } typeId }:
                        References.Add(new ReferenceInfo(ownerId, fromId, childPointer + "/ref", property.Name, typeId));
                        break;
                    case PropertyRole.Record:
                        WalkObject(child, childPointer, ownerId, fromId);
                        break;
                    case PropertyRole.RecordList when child is IEnumerable items:
                        var n = 0;
                        foreach (var item in items)
                        {
                            if (item is not null)
                                WalkObject(item, childPointer + "/" + n, ownerId, fromId);
                            n++;
                        }

                        break;
                }
            }
        }

        private void AddKeyed(string ownerId, string fromId, string pointer, string field, string key)
        {
            string? previous = null;
            foreach (var segment in key.Split(['@', '.']))
            {
                if (IdFormat.IsValid(segment) && segment != previous)
                    References.Add(new ReferenceInfo(ownerId, fromId, pointer, field, segment));
                previous = segment;
            }
        }

        private static string? IdOf(object value) => value switch
        {
            ElementBase b => b.Id,
            AlternateKey k => k.Id,
            PrimaryKey p => p.Id, // optional: a primary key without an id is no sub-element
            UniqueConstraint u => u.Id,
            ForeignKey f => f.Id,
            MemberForeignKey f => f.Id,
            CheckConstraint c => c.Id,
            TableIndex x => x.Id,
            RelationEnd e => e.Id,
            SeedRow r => r.Id,
            ReferenceCode c => c.Id,
            ReferenceLabel l => l.Id,
            IProcessNode n => n.Id,
            EntityBinding b => b.Id,
            ReferenceTypeBinding b => b.Id,
            OwnVerb v => v.Id,
            _ => null,
        };

        internal static PropertyRole? RoleOf(JsonPropertyInfo property)
        {
            if (property.AttributeProvider is PropertyInfo pi)
            {
                if (pi.GetCustomAttribute<ElementRefAttribute>(inherit: true) is { } reference)
                    return reference.Keyed ? PropertyRole.KeyedReference : PropertyRole.Reference;
                if (pi.GetCustomAttribute<StereotypeKeysAttribute>(inherit: true) is not null)
                    return PropertyRole.StereotypeKeys;
                if (pi.GetCustomAttribute<VerbKeyAttribute>(inherit: true) is not null)
                    return PropertyRole.VerbKey;
            }

            var type = property.PropertyType;
            if (type == typeof(TypeRef))
                return PropertyRole.TypeRef;
            if (IsModelRecord(type))
                return PropertyRole.Record;
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) && IsModelRecord(type.GetGenericArguments()[0]))
                return PropertyRole.RecordList;
            return null;
        }

        private static bool IsModelRecord(Type type) =>
            type.IsClass && type != typeof(string) && type.Namespace == ModelNamespace && type != typeof(Description)
            && !typeof(IEnumerable).IsAssignableFrom(type);
    }
}
