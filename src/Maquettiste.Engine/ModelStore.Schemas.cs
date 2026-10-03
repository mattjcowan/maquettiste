using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>
/// The database schema operations of a batch (erratum E26): <c>add-schema</c>, <c>rename-schema</c>, <c>remove-schema</c> and
/// <c>set-default-schema</c>. Each expands into updates of the database and, for a remove with a target, of the tables, views,
/// sequences and mappings that live in the removed schema; the expanded updates run with the batch's other operations, all or
/// nothing. Tables, views, sequences, convention entries and mappings name a schema by id, so a rename changes the schema entry and,
/// when it is the default, <c>defaultSchema</c> (a name).
/// </summary>
public sealed partial class ModelStore
{
    /// <summary>Whether an operation is a schema operation.</summary>
    /// <param name="op">The operation kind.</param>
    public static bool IsSchemaOperation(BatchOp op) => op is BatchOp.AddSchema or BatchOp.RenameSchema or BatchOp.RemoveSchema or BatchOp.SetDefaultSchema;

    private async Task<BatchResult> ApplyWithSchemasAsync(ModelBatch batch, List<PlannedChange> changes, SaveResult?[] invalid, ChangeSource source, CancellationToken ct)
    {
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var work = new SchemaWork(snapshot, _options.EffectiveIdGenerator);
        var processWork = new ProcessWork(snapshot, _options.EffectiveIdGenerator, changes);
        var materialized = new List<PlannedChange>();
        var materializeResults = new List<MaterializeResult>();
        for (var i = 0; i < batch.Operations.Count; i++)
        {
            var o = batch.Operations[i];
            if (!(IsSchemaOperation(o.Op) || IsProcessOperation(o.Op) || IsMaterializeOperation(o.Op)) || invalid[i] is not null)
                continue;
            var (rule, refusal) = IsSchemaOperation(o.Op) ? ("MQ4015", work.Apply(o))
                : IsMaterializeOperation(o.Op) ? ("MQ4055", await MaterializeAsync(snapshot, o, materialized, materializeResults, ct).ConfigureAwait(false))
                : ("MQ9019", processWork.Apply(o));
            if (refusal is not null)
            {
                var pointer = "/operations/" + i.ToString(CultureInfo.InvariantCulture);
                invalid[i] = new SaveResult(SaveOutcome.Invalid, o.Id, null, null, [RuleCatalog.Create(rule, refusal, o.Id, null, pointer)], [], null);
            }
        }

        if (invalid.Any(r => r is not null))
        {
            var items = invalid.Select((r, i) => r ?? new SaveResult(SaveOutcome.Saved, batch.Operations[i].Id, null, null, [], [], null)).ToList();
            return new BatchResult(SaveOutcome.Invalid, items, null);
        }

        foreach (var (id, expectedHash, node) in work.Changes().Concat(processWork.Changes()))
        {
            if (!TryParseRequest(Encoding.UTF8.GetBytes(node.ToJsonString()), id, out var parsed, out var failure))
                return new BatchResult(SaveOutcome.Invalid, [failure], null);
            changes.Add(new PlannedChange(BatchOp.Update, id, expectedHash, parsed, DeleteResolution.Refuse));
        }

        changes.AddRange(materialized);
        if (changes.Count == 0)
            return new BatchResult(SaveOutcome.Saved, [], ChangeSet.Empty(source));
        var result = await ExecuteAsync(changes, source, ct).ConfigureAwait(false);
        if (result.Outcome == SaveOutcome.Saved && materializeResults.Count > 0)
            await RekeySnapshotsAsync(materializeResults, ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>The documents a batch's schema operations change, as working copies.</summary>
    private sealed class SchemaWork(ModelSnapshot snapshot, IIdGenerator ids)
    {
        private readonly Dictionary<string, (string Hash, JsonObject Node)> _nodes = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        public IEnumerable<(string Id, string Hash, JsonObject Node)> Changes() => _order.Select(id => (id, _nodes[id].Hash, _nodes[id].Node));

        /// <summary>Applies one operation; returns why it is refused, or <see langword="null"/>.</summary>
        public string? Apply(BatchOperation o)
        {
            if (o.Id is null || snapshot.GetDocument(o.Id) is not { Element: Database } document)
                return $"'{o.Id}' is not a database.";
            var db = Node(o.Id, o.ExpectedHash ?? document.Hash);
            var schemas = db["schemas"] as JsonArray ?? [];
            db["schemas"] = schemas;
            var defaultName = db["defaultSchema"]?.GetValue<string>();
            var dbName = db["name"]?.GetValue<string>() ?? o.Id;
            JsonObject? Find(string? id) => id is null ? null : schemas.OfType<JsonObject>().FirstOrDefault(s => s["id"]?.GetValue<string>() == id);
            string NameOf(JsonObject s) => s["name"]?.GetValue<string>() ?? "";
            bool Taken(string name, string? except) => schemas.OfType<JsonObject>()
                .Any(s => s["id"]?.GetValue<string>() != except && string.Equals(NameOf(s), name, StringComparison.OrdinalIgnoreCase));

            switch (o.Op)
            {
                case BatchOp.AddSchema:
                {
                    if (string.IsNullOrWhiteSpace(o.Name))
                        return "A new schema needs a name.";
                    if (Taken(o.Name, null))
                        return $"Database '{dbName}' already has a schema named '{o.Name}'.";
                    var id = o.Schema ?? ids.NewId();
                    if (snapshot.TryGetEntry(id, out _) || _nodes.Values.Any(n => n.Node["schemas"] is JsonArray a && a.OfType<JsonObject>().Any(s => s["id"]?.GetValue<string>() == id)))
                        return $"The id '{id}' is already used.";
                    schemas.Add(new JsonObject { ["id"] = id, ["name"] = o.Name });
                    return null;
                }

                case BatchOp.RenameSchema:
                {
                    if (Find(o.Schema) is not { } schema)
                        return $"Database '{dbName}' has no schema '{o.Schema}'.";
                    if (string.IsNullOrWhiteSpace(o.Name))
                        return "A schema needs a name.";
                    if (Taken(o.Name, o.Schema))
                        return $"Database '{dbName}' already has a schema named '{o.Name}'.";
                    if (string.Equals(defaultName, NameOf(schema), StringComparison.Ordinal))
                        db["defaultSchema"] = o.Name;
                    schema["name"] = o.Name;
                    return null;
                }

                case BatchOp.SetDefaultSchema:
                {
                    if (Find(o.Schema) is not { } schema)
                        return $"Database '{dbName}' has no schema '{o.Schema}'.";
                    db["defaultSchema"] = NameOf(schema);
                    return null;
                }

                default:
                    return Remove(o, db, schemas, defaultName, dbName, Find, NameOf);
            }
        }

        private string? Remove(BatchOperation o, JsonObject db, JsonArray schemas, string? defaultName, string dbName,
            Func<string?, JsonObject?> find, Func<JsonObject, string> nameOf)
        {
            if (find(o.Schema) is not { } schema)
                return $"Database '{dbName}' has no schema '{o.Schema}'.";
            var name = nameOf(schema);
            JsonObject? newDefault = null;
            if (string.Equals(defaultName, name, StringComparison.Ordinal))
            {
                newDefault = find(o.Default);
                if (newDefault is null || ReferenceEquals(newDefault, schema))
                    return $"Schema '{name}' is the default of database '{dbName}'; name another schema as the default to remove it.";
            }

            JsonObject? target = null;
            if (o.Target is not null && ((target = find(o.Target)) is null || ReferenceEquals(target, schema)))
                return $"The target schema '{o.Target}' is not another schema of database '{dbName}'.";

            // What lives in the schema: tables, views, sequences, routines, database types and SQL objects of the database, convention entries and entity mappings.
            var occupants = new List<(string Label, Action<string> Move)>();
            foreach (var element in snapshot.All<Table>().Cast<Element>().Concat(snapshot.All<View>()).Concat(snapshot.All<Sequence>()).Concat(snapshot.All<Routine>())
                .Concat(snapshot.All<DatabaseType>()).Concat(snapshot.All<SqlObject>()).Concat(snapshot.All<Mapping>()))
            {
                var (database, schemaId) = _nodes.TryGetValue(element.Id, out var working)
                    ? (working.Node["database"]?.GetValue<string>(), working.Node["schema"]?.GetValue<string>())
                    : element switch
                    {
                        Table t => (t.Database, t.Schema),
                        View v => (v.Database, v.Schema),
                        Sequence q => (q.Database, q.Schema),
                        Routine r => (r.Database, r.Schema),
                        DatabaseType t => (t.Database, t.Schema),
                        SqlObject x => (x.Database, x.Schema),
                        Mapping m => (m.Database, m.Schema),
                        _ => (null, null),
                    };
                if (database != o.Id || schemaId != o.Schema)
                    continue;
                var id = element.Id;
                var kind = element.KindName;
                occupants.Add(($"{kind} '{element.Name}'", to => Node(id, snapshot.GetDocument(id)!.Hash)["schema"] = to));
            }

            if (db["packages"] is JsonArray entries)
            {
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] is JsonObject entry && entry["schema"]?.GetValue<string>() == o.Schema)
                    {
                        var package = entry["package"]?.GetValue<string>() ?? "";
                        var label = snapshot.GetDocument(package)?.Element.Name ?? package;
                        occupants.Add(($"convention entry '{label}'", to => entry["schema"] = to));
                    }
                }
            }

            if (occupants.Count > 0 && target is null)
            {
                var shown = string.Join(", ", occupants.Take(20).Select(x => x.Label));
                var more = occupants.Count > 20 ? string.Create(CultureInfo.InvariantCulture, $" and {occupants.Count - 20} more") : "";
                return $"Schema '{name}' of database '{dbName}' still holds {shown}{more}; move them or name a target schema.";
            }

            var targetId = target?["id"]?.GetValue<string>() ?? "";
            foreach (var (_, move) in occupants)
                move(targetId);
            if (newDefault is not null)
                db["defaultSchema"] = nameOf(newDefault);
            schemas.Remove(schema);
            return null;
        }

        private JsonObject Node(string id, string hash)
        {
            if (_nodes.TryGetValue(id, out var working))
                return working.Node;
            var node = (JsonNode.Parse(snapshot.GetDocument(id)!.Json.GetRawText()) as JsonObject)!;
            _nodes[id] = (hash, node);
            _order.Add(id);
            return node;
        }
    }
}
