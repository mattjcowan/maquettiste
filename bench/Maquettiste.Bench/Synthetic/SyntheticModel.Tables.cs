using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// The model's storage as designed tables (retire-projection-design.md section 8): the entities each database takes (every entity in
/// <c>main</c>, the packages of <c>reporting</c> and <c>edge</c>) are stored with "Create tables…", the engine's materialize-tables, run in
/// memory on the model built so far: a designed table per entity (a table per type in each hierarchy, the conventions' shape), a
/// junction table per many-to-many relation, a binding per entity and a relation mapping per relation. The entity mappings built
/// before it (an enum stored by its code, a value object stored as JSON) are the shapes the designed columns take; the operation
/// deletes them. The databases then take nothing by convention. The ids come from the model's own id stream, so the result is as
/// deterministic as the rest.
/// </summary>
internal sealed partial class SyntheticModel
{
    private void StoreTables()
    {
        if (_options.Projected)
            return;
        var ids = new StreamIds(_ids);
        var options = new EngineOptions { RepoRoot = Path.GetTempPath(), CacheDirectory = Path.Combine(Path.GetTempPath(), "maquettiste-bench-tables"), IdGenerator = ids, MaxDegreeOfParallelism = Environment.ProcessorCount };
        var resolver = new ModelResolver(options);
        var json = new CanonicalJson(new SchemaRegistry());
        var settingsHash = ContentHash.Of(json.Serialize(Settings, "maquettiste.json", ".maquettiste/" + ModelPaths.SettingsFile));
        // The documents of the last snapshot by element instance, kept for this step only: held by the model, they kept every element's
        // parsed JSON alive through the benchmark's timed runs.
        var known = new Dictionary<Element, ElementDocument>(ReferenceEqualityComparer.Instance);
        foreach (var database in _elements.OfType<Database>().ToList())
        {
            var snapshot = Snapshot(json, settingsHash, known);
            var resolved = resolver.ResolveAsync(snapshot, null, CancellationToken.None).GetAwaiter().GetResult();
            var rdb = resolved.Databases.First(d => d.Id == database.Id);
            var designed = rdb.Tables.Where(t => t.Origin != "synthesized").Select(t => t.Key).ToHashSet(StringComparer.Ordinal);
            var entities = resolved.Entities
                .Where(e => !e.IsPromoted && e.Mappings.TryGetValue(rdb.Name, out var m) && !designed.Contains(m.Table.Key))
                .Select(e => e.Id)
                .ToList();
            var listed = entities.ToHashSet(StringComparer.Ordinal); // the list keeps the order, the set answers membership
            // A hierarchy is stored whole.
            foreach (var e in resolved.Entities.Where(e => listed.Contains(e.Id)).ToList())
            {
                for (var b = e.Base; b is not null; b = b.Base)
                {
                    if (listed.Add(b.Id))
                        entities.Add(b.Id);
                }
            }

            if (entities.Count == 0)
                continue;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = new Materializer(snapshot, resolver, ids).TablesAsync(database.Id, entities, null, null, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            CreateTablesTime += clock.Elapsed;
            if (result.Refusal is { } refusal)
                throw new InvalidOperationException($"Create tables refused the synthetic model's database '{database.Name}': {refusal}");
            Apply(result.Ops);
        }

        // Every entity is bound: the databases take nothing by convention.
        for (var i = 0; i < _elements.Count; i++)
        {
            if (_elements[i] is Database db)
                _elements[i] = db with { ByConvention = ConventionMapping.None, Packages = [] };
        }
    }

    /// <summary>The time "Create tables…" took over every database (tests: it stays roughly linear in the model).</summary>
    internal TimeSpan CreateTablesTime { get; private set; }

    /// <summary>
    /// The model built so far as a snapshot: each element written canonically at the path the repo writer gives it. An element that neither
    /// changed nor moved since the last snapshot keeps its document from <paramref name="known"/>, so a database after the first serializes
    /// only what the previous database's tables changed.
    /// </summary>
    private ModelSnapshot Snapshot(CanonicalJson json, string settingsHash, Dictionary<Element, ElementDocument> known)
    {
        var paths = RepoWriter.PathsOf(_elements);
        var documents = new List<ElementDocument>(_elements.Count);
        for (var i = 0; i < _elements.Count; i++)
        {
            var element = _elements[i];
            var path = ".maquettiste/" + paths[i];
            if (known.TryGetValue(element, out var kept) && string.Equals(kept.Path, path, StringComparison.Ordinal))
            {
                documents.Add(kept);
                continue;
            }

            var bytes = json.Serialize(element, KindInfo.Get(element.Kind).SchemaFile, path);
            var hash = ContentHash.Of(bytes);
            using var document = JsonDocument.Parse(bytes);
            var made = new ElementDocument(element, path, hash, HashBuilder.Of(hash, null), document.RootElement.Clone(), null);
            known[element] = made;
            documents.Add(made);
        }

        return ModelSnapshot.Create(documents, Settings, settingsHash, [], [], 1);
    }

    /// <summary>Applies a materialize plan to the elements: creates go last, in plan order; updates replace in place; deletes go.</summary>
    private void Apply(IReadOnlyList<MaterializeOp> ops)
    {
        var index = _elements.Select((e, i) => (e.Id, i)).ToDictionary(x => x.Id, x => x.i, StringComparer.Ordinal);
        var entityIndex = new Dictionary<string, int>(_entities.Count, StringComparer.Ordinal);
        for (var i = 0; i < _entities.Count; i++)
            entityIndex.TryAdd(_entities[i].Id, i); // the first of an id, as FindIndex found
        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var created = new List<Element>();
        foreach (var op in ops)
        {
            if (op.Op == BatchOp.Delete)
            {
                deleted.Add(op.Id);
                continue;
            }

            var element = ElementReader.ReadElement(JsonElement.Parse(op.Node!.ToJsonString(), DocumentReader.ParseOptions));
            if (op.Op == BatchOp.Create)
            {
                created.Add(element);
                continue;
            }

            _elements[index[op.Id]] = element;
            if (element is Entity entity)
            {
                if (entityIndex.TryGetValue(entity.Id, out var at))
                    _entities[at] = entity;
            }
        }

        _elements.RemoveAll(e => deleted.Contains(e.Id));
        _elements.AddRange(created);
    }

    /// <summary>The engine's id generator over the model's id stream.</summary>
    private sealed class StreamIds(SyntheticIds ids) : IIdGenerator
    {
        private readonly Lock _gate = new();

        public string NewId()
        {
            lock (_gate)
                return ids.Next();
        }
    }
}
