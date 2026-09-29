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
        /// <summary>The documents indexed, in path order (aligned with <see cref="Walks"/>).</summary>
        internal ImmutableArray<ElementDocument> Documents { get; init; }

        /// <summary>Each document's walk (sub-element entries and references), for <see cref="Build"/> to reuse on the next snapshot.</summary>
        internal DocumentWalk[]? Walks { get; init; }

        /// <summary>Whether no id was registered twice and no singleton kind repeated: the precondition for patching this index.</summary>
        internal bool Clean { get; init; }

        /// <summary>Whether this index was patched from the previous one rather than built in full (tests).</summary>
        internal bool Patched { get; init; }
    }

    internal enum PropertyRole { Reference, KeyedReference, StereotypeKeys, TypeRef, Record, RecordList }

    internal sealed record PropertyMeta(string Name, Func<object, object?> Get, PropertyRole Role);

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
        _ when type == typeof(DbSchema) => "schema",
        _ when type == typeof(AlternateKey) || type == typeof(UniqueConstraint) || type == typeof(ForeignKey)
            || type == typeof(CheckConstraint) || type == typeof(TableIndex) => "key",
        _ => null,
    };

    /// <summary>
    /// Builds the indexes of the documents (sorted by path). With the previous snapshot's index, and when the change is small and
    /// touches no stereotype, tag vocabulary or category tree, the previous indexes are patched instead of rebuilt; the result is
    /// the same as a full build (<c>ModelIndexerTests</c> compares them on random edits), which runs whenever the patch cannot be sure.
    /// </summary>
    /// <param name="documents">The documents, in path order.</param>
    /// <param name="previous">The previous snapshot's index, or <see langword="null"/>.</param>
    /// <param name="parallelism">The most documents walked at once by a full build (the caller's
    /// <see cref="EngineOptions.EffectiveParallelism"/>; <see cref="DefaultParallelism"/> without one).</param>
    /// <param name="ct">Cancellation, observed between documents.</param>
    /// <returns>The index.</returns>
    public static Result Build(ImmutableArray<ElementDocument> documents, Result? previous, int parallelism, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(parallelism, 1);
        ct.ThrowIfCancellationRequested();
        if (previous is { Clean: true, Walks: not null } && TryPatch(documents, previous) is { } patched)
            return patched;
        return BuildFull(documents, parallelism, ct) with { Patched = false };
    }

    /// <summary>The walk parallelism of a build whose caller has no engine options (the public <see cref="ModelSnapshot.Create"/>).</summary>
    internal static int DefaultParallelism => Math.Max(1, Math.Min(Environment.ProcessorCount, 8));

    private static Result BuildFull(ImmutableArray<ElementDocument> documents, int parallelism, CancellationToken ct)
    {
        // Stereotype keys first, so every element's stereotypes[i] can be indexed as a reference to the stereotype's id.
        var stereotypes = new Dictionary<string, Stereotype>(StringComparer.Ordinal);
        var firstIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in documents)
        {
            if (firstIds.Add(doc.Element.Id) && doc.Element is Stereotype s)
                stereotypes.TryAdd(s.Key, s); // a duplicate key keeps the ordinally first path; validation reports it
        }

        // Each document is walked on its own (in parallel: the walk reads only the element and the stereotype keys); the results are
        // then merged in document order, so the first registration of an id wins exactly as in one sequential walk.
        var metadata = new MetadataCache();
        var walks = new DocumentWalk[documents.Length];
        Parallel.For(0, documents.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i =>
        {
            var walk = new DocumentWalk(stereotypes, metadata);
            walk.WalkElement(documents[i].Element);
            walks[i] = walk;
        });

        var entries = new Dictionary<string, IndexEntry>(Math.Max(16, walks.Sum(w => w.Entries.Count) + documents.Length), StringComparer.Ordinal);
        var references = new List<ReferenceInfo>(walks.Sum(w => w.References.Count));
        var docsById = new Dictionary<string, ElementDocument>(documents.Length, StringComparer.Ordinal);
        var byKind = new Dictionary<ElementKind, List<Element>>();
        var summaries = ImmutableArray.CreateBuilder<ElementSummary>(documents.Length);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        TagVocabulary? tags = null;
        CategoryTree? categories = null;
        string? tagsPath = null, categoriesPath = null;
        var clean = true;

        for (var d = 0; d < documents.Length; d++)
        {
            var doc = documents[d];
            var element = doc.Element;
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
                case TagVocabulary t when tags is null:
                    (tags, tagsPath) = (t, doc.Path);
                    break;
                case CategoryTree c when categories is null:
                    (categories, categoriesPath) = (c, doc.Path);
                    break;
                case TagVocabulary or CategoryTree:
                    // A singleton kind: the ordinally first file wins, the others are reported and ignored by validation (MQ1009).
                    clean = false;
                    diagnostics.Add(RuleCatalog.Create(
                        "MQ1009",
                        $"The model already has a {element.KindName} in {(element is TagVocabulary ? tagsPath : categoriesPath)}; this second one is ignored.",
                        element.Id,
                        doc.Path,
                        ""));
                    break;
            }

            var walk = walks[d];
            foreach (var (id, entry) in walk.Entries)
                clean &= entries.TryAdd(id, entry);
            references.AddRange(walk.References);
            summaries.Add(Summarize(element, doc.Hash, doc.Path));
        }

        var referencesTo = Group(references, static r => r.ToId);
        var referencesFrom = Group(references, static r => r.FromElementId);
        var sortedByKind = byKind.ToFrozenDictionary(
            kv => kv.Key,
            kv => kv.Value.OrderBy(e => e.Name, StringComparer.Ordinal).ThenBy(e => e.Id, StringComparer.Ordinal).ToImmutableArray());
        var kindSetHashes = byKind.ToFrozenDictionary(
            kv => kv.Key,
            kv => HashBuilder.Of([.. kv.Value.Select(e => e.Id).Order(StringComparer.Ordinal)]));

        return new Result(
            entries,
            docsById,
            referencesTo,
            referencesFrom,
            sortedByKind,
            kindSetHashes,
            stereotypes.ToFrozenDictionary(StringComparer.Ordinal),
            summaries.ToImmutable(),
            tags,
            categories,
            diagnostics.ToImmutable())
        {
            Documents = documents,
            Walks = walks,
            Clean = clean,
        };
    }

    /// <summary>The largest number of added plus removed documents a patch handles; more rebuild in full.</summary>
    private const int PatchLimit = 256;

    /// <summary>
    /// Patches the previous index for a small change, or returns <see langword="null"/> when a full build is needed: a changed,
    /// added or removed stereotype, tag vocabulary or category tree (they change how other files are indexed), too many changes, or
    /// an id that would now be registered twice. Unchanged documents (the same <see cref="ElementDocument"/> instance at the same
    /// path) keep their walks; every map ends up with exactly the content and order a full build gives.
    /// </summary>
    private static Result? TryPatch(ImmutableArray<ElementDocument> documents, Result previous)
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
            return previous with { Documents = documents, Walks = walks, Patched = true };
        foreach (var r in removed)
        {
            if (old[r].Element is Stereotype or TagVocabulary or CategoryTree)
                return null;
        }

        foreach (var a in added)
        {
            if (documents[a].Element is Stereotype or TagVocabulary or CategoryTree)
                return null;
        }

        var metadata = new MetadataCache();
        foreach (var a in added)
        {
            var walk = new DocumentWalk(previous.Stereotypes, metadata);
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
            kindSetHashes[kind] = HashBuilder.Of([.. list.Select(e => e.Id).Order(StringComparer.Ordinal)]);
        }

        // Summaries follow the documents.
        var summaries = ImmutableArray.CreateBuilder<ElementSummary>(documents.Length);
        var previousSummaries = previous.Summaries;
        var addedSet = new HashSet<int>(added);
        var o = 0;
        for (var d = 0; d < documents.Length; d++)
        {
            if (addedSet.Contains(d))
            {
                var element = documents[d].Element;
                summaries.Add(Summarize(element, documents[d].Hash, documents[d].Path));
                continue;
            }

            while (!ReferenceEquals(old[o], documents[d]))
                o++; // skip removed documents; the unchanged one is further on
            summaries.Add(previousSummaries[o++]);
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
            Patched = true,
        };
    }

    /// <summary>The index row of an element (E4 and E5 members included).</summary>
    private static ElementSummary Summarize(Element element, string hash, string path) =>
        new(element.Id, element.KindName, element.Name, PackageOf(element), element.Tags, hash, path, element.Category, element.Stereotypes,
            DisplayName: element.DisplayName,
            Database: element switch
            {
                Table t => t.Database,
                View v => v.Database,
                Sequence q => q.Database,
                Mapping m => m.Database,
                _ => null,
            },
            Entity: element switch
            {
                Mapping m => m.Entity,
                Table t => t.Entity,
                _ => null,
            },
            MemberCount: element is Diagram diagram ? diagram.Members.Count : null,
            Ends: element is Relation relation ? EndsOf(relation) : null);

    private static RelationEndSummary[] EndsOf(Relation relation)
    {
        var ends = new RelationEndSummary[relation.Ends.Count];
        for (var i = 0; i < ends.Length; i++)
            ends[i] = new RelationEndSummary(relation.Ends[i].Entity, relation.Ends[i].Role);
        return ends;
    }

    private static string? PackageOf(Element element) => element switch
    {
        Package p => p.Parent,
        Entity e => e.Package,
        ValueObject v => v.Package,
        ScalarType s => s.Package,
        EnumType e => e.Package,
        Relation r => r.Package,
        Diagram d => d.Package,
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
                    if (role is not null)
                        list.Add(new PropertyMeta(property.Name, property.Get, role.Value));
                }
            }

            return [.. list];
        }
    }

    /// <summary>The walk of one document: its sub-element entries (in walk order) and its references.</summary>
    internal sealed class DocumentWalk(IReadOnlyDictionary<string, Stereotype> stereotypes, MetadataCache metadata)
    {
        public List<(string Id, IndexEntry Entry)> Entries { get; } = [];

        public List<ReferenceInfo> References { get; } = [];

        public void WalkElement(Element element) => WalkProperties(element, "", element.Id, element.Id);

        private void WalkObject(object value, string pointer, string ownerId, string fromId)
        {
            var subKind = SubElementKind(value.GetType());
            if (subKind is not null && IdOf(value) is { } id)
            {
                Entries.Add((id, new IndexEntry(id, ownerId, subKind, pointer)));
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
                        References.Add(new ReferenceInfo(ownerId, fromId, childPointer, property.Name, id));
                        break;
                    case PropertyRole.Reference when child is IEnumerable<string> ids:
                        var i = 0;
                        foreach (var item in ids)
                            References.Add(new ReferenceInfo(ownerId, fromId, childPointer + "/" + i++, property.Name, item));
                        break;
                    case PropertyRole.KeyedReference when child is string key:
                        AddKeyed(ownerId, fromId, childPointer, property.Name, key);
                        break;
                    case PropertyRole.KeyedReference when child is IEnumerable<string> keys:
                        var k = 0;
                        foreach (var item in keys)
                            AddKeyed(ownerId, fromId, childPointer + "/" + k++, property.Name, item);
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
            UniqueConstraint u => u.Id,
            ForeignKey f => f.Id,
            CheckConstraint c => c.Id,
            TableIndex x => x.Id,
            RelationEnd e => e.Id,
            _ => null,
        };

        internal static PropertyRole? RoleOf(JsonPropertyInfo property)
        {
            if (property.AttributeProvider is PropertyInfo pi)
            {
                if (pi.GetCustomAttribute<ElementRefAttribute>(inherit: true) is { } reference)
                    return reference.Keyed ? PropertyRole.KeyedReference : PropertyRole.Reference;
                if (pi.DeclaringType == typeof(ElementBase) && pi.Name == nameof(ElementBase.Stereotypes))
                    return PropertyRole.StereotypeKeys;
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
