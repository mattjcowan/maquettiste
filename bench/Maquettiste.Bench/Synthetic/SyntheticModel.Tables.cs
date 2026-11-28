using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;

namespace Maquettiste.Bench.Synthetic;

/// <summary>
/// The model's storage as designed tables (retire-projection-design.md section 8): the entities each database stores (every entity in
/// <c>main</c>, some packages' in <c>reporting</c> and <c>edge</c>) are stored with "Create tables…", the engine's materialize-tables, run in
/// memory on the model built so far: a designed table per entity (a table per type in each hierarchy, the conventions' shape), a
/// junction table per many-to-many relation, a binding per entity and a relation mapping per relation. Then some designed columns are
/// shaped as a team would design them (<see cref="ShapeColumns"/>): an enum stored by its code, a value object stored as JSON. The ids come
/// from the model's own id stream, so the result is as deterministic as the rest.
/// </summary>
internal sealed partial class SyntheticModel
{
    private void StoreTables()
    {
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
            var entities = EntitiesOf(database.Id);
            var listed = entities.ToHashSet(StringComparer.Ordinal); // the list keeps the order, the set answers membership
            // A hierarchy is stored whole.
            foreach (var id in entities.ToList())
            {
                for (var b = snapshot.Get<Entity>(id)?.Base; b is not null && snapshot.Get<Entity>(b) is { } baseEntity; b = baseEntity.Base)
                {
                    if (listed.Add(b))
                        entities.Add(b);
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
            ShapeColumns(database);
        }
    }

    /// <summary>
    /// Designs some of a database's columns by hand, as the model measured before 0.29.0 stored them through entity mappings: in <c>main</c>,
    /// for one entity in ten, its first enum attribute as a string column of the enum's codes; in <c>reporting</c>, for one entity in five
    /// whose package is one the database stores directly, its first value object attribute as one JSON column in place of a column per
    /// member. The binding's fields and provenance follow, so the binding reads as freshly synced.
    /// </summary>
    private void ShapeColumns(Database database)
    {
        var main = database.Name == "main";
        if (!main && database.Name != "reporting")
            return;
        var enums = _enums.ToDictionary(e => e.Id, StringComparer.Ordinal);
        var valueObjects = _valueObjects.Select(v => v.Id).ToHashSet(StringComparer.Ordinal);
        var direct = _databasePackages.GetValueOrDefault(database.Id) ?? [];
        var tableAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var entityAt = new Dictionary<Element, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _elements.Count; i++)
        {
            if (_elements[i] is Table t)
                tableAt[t.Id] = i;
            else if (_elements[i] is Entity e)
                entityAt[e] = i;
        }

        for (var i = 0; i < _entities.Count; i++)
        {
            var entity = _entities[i];
            if (main ? i % 10 != 3 : i % 5 != 1 || entity.Package is null || !direct.Contains(entity.Package))
                continue;
            var attribute = entity.Attributes.FirstOrDefault(a => a.Type.Ref is { } r && (main ? enums.ContainsKey(r) : valueObjects.Contains(r)));
            var at = entity.Bindings.ToList().FindIndex(b => b.Database == database.Id);
            if (attribute is null || at < 0 || !tableAt.TryGetValue(entity.Bindings[at].Source, out var tableIndex))
                continue;
            var binding = entity.Bindings[at];
            var table = (Table)_elements[tableIndex];
            var columns = table.Columns.ToList();
            var fields = binding.Fields.ToList();
            var recorded = new Dictionary<string, string>(binding.Provenance?.Columns ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            if (main)
            {
                var field = fields.Find(f => f.Attribute == attribute.Id);
                var c = field is null ? -1 : columns.FindIndex(x => x.Id == field.Column);
                if (c < 0)
                    continue;
                var codes = enums[attribute.Type.Ref!].Members.Select(m => (m.Code ?? m.Name).Length).DefaultIfEmpty(1).Max();
                columns[c] = columns[c] with { Type = "string", Length = codes };
                if (recorded.ContainsKey(columns[c].Id))
                    recorded[columns[c].Id] = ColumnShapeHash.Of(columns[c]);
            }
            else
            {
                var prefix = attribute.Id + ".";
                var members = fields.Where(f => f.Attribute.StartsWith(prefix, StringComparison.Ordinal)).Select(f => f.Column).ToHashSet(StringComparer.Ordinal);
                var first = columns.FindIndex(x => members.Contains(x.Id));
                if (first < 0)
                    continue;
                var json = new Column { Id = _ids.Next(), Name = Casing.Snake(attribute.Name), Type = "json" };
                columns.RemoveAll(x => members.Contains(x.Id));
                columns.Insert(first, json);
                var f = fields.FindIndex(x => members.Contains(x.Column));
                fields.RemoveAll(x => members.Contains(x.Column));
                fields.Insert(f, new BindingField { Attribute = attribute.Id, Column = json.Id });
                foreach (var id in members)
                    recorded.Remove(id);
                if (binding.Provenance is not null)
                    recorded[json.Id] = ColumnShapeHash.Of(json);
            }

            _elements[tableIndex] = table with { Columns = columns };
            var bindings = entity.Bindings.ToList();
            bindings[at] = binding with { Fields = fields, Provenance = binding.Provenance is null ? null : binding.Provenance with { Columns = recorded } };
            var shaped = entity with { Bindings = bindings };
            _entities[i] = shaped;
            if (entityAt.TryGetValue(entity, out var element))
                _elements[element] = shaped;
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
