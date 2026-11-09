using System.Globalization;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Text;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The binding operations (next-phase-2-design.md section 3.3): <c>map-by-name</c> maps the attributes a binding leaves unmapped to the
/// free columns of the same loose name, and <c>resync-binding</c> brings a binding to a table file into step with the table, both
/// recording the provenance of the columns they follow. They read the named entities, their bases and effective stereotypes, their
/// source files and the stereotypes those inherit from; they never resolve the model (decision 10). Like every expanded operation they
/// plan against the saved model.
/// </summary>
internal sealed partial class Materializer
{
    /// <summary>
    /// Something a binding field can fill (decision 7): an attribute (own, a base's or a stereotype's), a member of an embedded value object
    /// attribute (<c>attributeId.memberId</c>, named <c>attributeMember</c>) or the relation end a to-one navigation leads to (named
    /// the navigation plus <c>Id</c>, which also matches without <c>Id</c>).
    /// </summary>
    /// <param name="Ref">What the field's <c>attribute</c> holds.</param>
    /// <param name="Name">The name generation gives it.</param>
    /// <param name="Navigation">Whether it is a to-one navigation's key.</param>
    private sealed record FieldCandidate(string Ref, string Name, bool Navigation);

    /// <summary>A column of the source map by name compares: a table file's (its id) or a view's (its name).</summary>
    private sealed record ByNameSource(string Kind, string Name, IReadOnlyList<ShapeColumn> Columns, Table? File);

    // ---- map-by-name ------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plans <c>map-by-name</c> (decision 7): for each entity bound to the database, every attribute its binding does not map, matched to a
    /// free column of the same loose name, each column once in the source's column order. Fields hold column ids for a table file and
    /// names for a view; a table file's binding records the provenance of the columns it maps (every column when it has none). It writes
    /// no attribute and no column. Refused for an entity with no binding to the database, a source it cannot read (a query, a projected
    /// table, whose columns come from resolution), and when nothing matches for every entity named.
    /// </summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="entityIds">The entities.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public Task<MaterializeResult> MapByNameAsync(string databaseId, IReadOnlyList<string> entityIds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(MapByName(databaseId, entityIds, ct));
    }

    private MaterializeResult MapByName(string databaseId, IReadOnlyList<string> entityIds, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        var selected = entityIds.Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0)
            return Refused("Name at least one entity to map by name.");
        var work = new List<(Entity Entity, int Index, ByNameSource Source)>();
        foreach (var id in selected)
        {
            if (Bound(db, id, out var entity, out var index) is { } refusal)
                return Refused(refusal);
            var binding = entity!.Bindings[index];
            switch (snapshot.Get<Element>(binding.Source))
            {
                case Query query when query.Database == db.Id:
                    return Refused($"Entity '{entity.Name}' reads query '{query.Name}' in database '{db.Name}': a query's fields resolve from the query, so map them by hand.");
                case View view when view.Database == db.Id:
                    work.Add((entity, index, new ByNameSource("view", view.Name, [.. view.Columns.Select(c => new ShapeColumn(c.Name, c.Name, new Column { Id = c.Name, Name = c.Name }, false))], null)));
                    continue;
            }

            if (BindingDrift.SourceOf(snapshot, binding, out var table) != DriftSource.TableFile)
                return Refused($"Entity '{entity.Name}' reads '{binding.Source}' in database '{db.Name}', which is not a table file or a view: its columns come from resolution, so map them by hand.");
            work.Add((entity, index, new ByNameSource("table", table!.Name, BindingDrift.ColumnsOf(snapshot, table), table)));
        }

        var mapped = 0;
        foreach (var (entity, index, source) in work)
        {
            ct.ThrowIfCancellationRequested();
            var binding = entity.Bindings[index];
            var matches = MatchByName(entity, binding, source.Columns, null);
            if (matches.Count == 0)
            {
                _notes.Add($"No attribute the binding of entity '{entity.Name}' leaves unmapped has the name of a free column of {source.Kind} '{source.Name}': nothing to map.");
                continue;
            }

            mapped++;
            var node = Node(entity.Id, BatchOp.Update, $"maps {Plural(matches.Count, "attribute")} of entity {entity.Name} by name to columns of {source.Kind} {source.Name}");
            var bindingNode = (JsonObject)node["bindings"]![index]!;
            var fields = bindingNode["fields"] as JsonArray ?? [];
            bindingNode["fields"] = fields;
            foreach (var (candidate, column) in matches)
                fields.Add(new JsonObject { ["attribute"] = candidate.Ref, ["column"] = column.Id });
            if (source.File is not null)
                RecordProvenance(bindingNode, source.Columns, [.. matches.Select(m => m.Column.Id)]);
        }

        return mapped == 0
            ? Refused(work.Count == 1
                ? $"No attribute the binding of entity '{work[0].Entity.Name}' leaves unmapped has the name of a free column of {work[0].Source.Kind} '{work[0].Source.Name}': nothing to map."
                : "No attribute the bindings of the entities named leave unmapped has the name of a free column of their sources: nothing to map.")
            : Result();
    }

    // ---- resync-binding ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Plans <c>resync-binding</c> (decision 8) for each entity whose binding to the database reads a table file: renames an own attribute
    /// that followed its column's old name (unless <paramref name="keepNames"/>), retypes an own attribute whose column was retyped (it
    /// keeps an enum, reference type or scalar type the column still holds), removes the fields of removed columns (the attributes stay),
    /// maps by name (so an attribute whose column was dropped and re-added under its name follows the new column), adds an attribute per
    /// added column still free and records the provenance of every column. With
    /// <paramref name="columns"/> (one entity), it works on those columns only and updates only their entries. A binding without
    /// provenance (or with one that describes another source) re-syncs from scratch: map by name, the columns nothing accounts for, and
    /// provenance. A binding in step is skipped with a note; the operation is refused when every binding it names is in step.
    /// </summary>
    /// <param name="databaseId">The database.</param>
    /// <param name="entityIds">The entities.</param>
    /// <param name="columns">The columns, as keys or physical names (one entity only), or <see langword="null"/> for every column.</param>
    /// <param name="keepNames">Whether every attribute keeps its name.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The plan.</returns>
    public Task<MaterializeResult> ResyncAsync(string databaseId, IReadOnlyList<string> entityIds, IReadOnlyList<string>? columns, bool keepNames, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Resync(databaseId, entityIds, columns, keepNames, ct));
    }

    private MaterializeResult Resync(string databaseId, IReadOnlyList<string> entityIds, IReadOnlyList<string>? columns, bool keepNames, CancellationToken ct)
    {
        if (snapshot.Get<Database>(databaseId) is not { } db)
            return Refused($"'{databaseId}' is not a database.");
        var selected = entityIds.Distinct(StringComparer.Ordinal).ToList();
        if (selected.Count == 0)
            return Refused("Name at least one entity to re-sync.");
        if (columns is { Count: > 0 } && selected.Count > 1)
            return Refused("Name the columns for one entity at a time.");
        var work = new List<(Entity Entity, int Index, Table Table)>();
        foreach (var id in selected)
        {
            if (Bound(db, id, out var entity, out var index) is { } refusal)
                return Refused(refusal);
            if (BindingDrift.SourceOf(snapshot, entity!.Bindings[index], out var table) != DriftSource.TableFile)
                return Refused($"Entity '{entity.Name}' reads '{ReadableSource(entity.Bindings[index].Source)}' in database '{db.Name}', which is not a designed or imported table file: re-sync follows a table file's columns, which have ids.");
            work.Add((entity, index, table!));
        }

        var dialectMap = DialectTypeMaps.Effective(db.Dialect, snapshot.Settings);
        var changed = 0;
        var inStep = new List<string>();
        foreach (var (entity, index, table) in work)
        {
            ct.ThrowIfCancellationRequested();
            if (ResyncOne(db, entity, index, table, columns is { Count: > 0 } ? columns : null, keepNames, dialectMap, out var refusal) is { } planned)
            {
                Put(entity.Id, snapshot.GetDocument(entity.Id)!.Hash, planned, BatchOp.Update, $"re-syncs the binding of entity {entity.Name} with table {table.Name}");
                changed++;
            }
            else if (refusal is not null)
            {
                return Refused(refusal);
            }
            else
            {
                inStep.Add(entity.Name);
                _notes.Add($"The binding of entity '{entity.Name}' to database '{db.Name}' is in step with table '{table.Name}': nothing to re-sync.");
            }
        }

        if (changed == 0)
        {
            return Refused(inStep.Count == 1
                ? $"The binding of entity '{inStep[0]}' to database '{db.Name}' is in step with its table: nothing to re-sync."
                : $"Every binding named is in step with its table in database '{db.Name}': nothing to re-sync.");
        }

        return Result();
    }

    /// <summary>
    /// One binding's re-sync, on a working copy of the entity: the copy when it changes anything, else <see langword="null"/> (in step,
    /// or refused with <paramref name="refusal"/>).
    /// </summary>
    private JsonObject? ResyncOne(Database db, Entity entity, int index, Table table, IReadOnlyList<string>? only, bool keepNames,
        IReadOnlyDictionary<string, string> dialectMap, out string? refusal)
    {
        refusal = null;
        var binding = entity.Bindings[index];
        var drift = BindingDrift.Of(snapshot, entity, binding);
        var columns = drift.Columns;
        HashSet<string>? named = null;
        if (only is not null)
        {
            named = new HashSet<string>(StringComparer.Ordinal);
            foreach (var reference in only.Distinct(StringComparer.Ordinal))
            {
                var at = BindingDrift.Find(columns, reference);
                if (at < 0)
                {
                    refusal = $"'{reference}' is not a column of table '{table.Name}'.";
                    return null;
                }

                named.Add(columns[at].Id);
            }
        }

        bool In(string columnId) => named is null || named.Contains(columnId);
        var original = Raw(entity.Id)!;
        var node = (JsonObject)original.DeepClone();
        var attributes = node["attributes"] as JsonArray ?? [];
        var bindingNode = (JsonObject)node["bindings"]![index]!;
        var fields = bindingNode["fields"] as JsonArray ?? [];
        var taken = AttributeNames(entity);
        var notes = new List<string>();

        // 1. Renamed: an own attribute that followed its column's old name takes the new one, unless names are kept.
        foreach (var change in drift.Renamed.Where(c => drift.Reads && In(c.Column)))
        {
            if (Own(attributes, change.Attribute) is not { } attribute)
            {
                notes.Add($"Column '{change.Name}' of table '{table.Name}' was renamed; '{change.AttributeName}', which the binding of entity '{entity.Name}' maps to it, is not an attribute of the entity's own, so it keeps its name.");
                continue;
            }

            var name = attribute["name"]!.GetValue<string>();
            if (keepNames || !string.Equals(ColumnShapeHash.NamePart(name), ColumnShapeHash.NamePartOf(change.Recorded), StringComparison.Ordinal))
                continue;
            taken.Remove(name);
            var renamed = AttributeName(change.Name, taken);
            attribute["name"] = renamed;
            if (!string.Equals(renamed, name, StringComparison.Ordinal))
                notes.Add($"Attribute '{name}' of entity '{entity.Name}' followed column '{change.Name}' of table '{table.Name}' and is renamed '{renamed}' (its id stays).");
        }

        // 2. Retyped: an own attribute keeps an enum, reference type or scalar type the column still holds, else takes materialize's type;
        // its facets and required follow the column.
        foreach (var change in drift.Retyped.Where(c => drift.Reads && In(c.Column)))
        {
            if (Own(attributes, change.Attribute) is not { } attribute)
            {
                notes.Add($"Column '{change.Name}' of table '{table.Name}' was retyped; '{change.AttributeName}', which the binding of entity '{entity.Name}' maps to it, is not an attribute of the entity's own, so it keeps its type.");
                continue;
            }

            var column = columns.First(c => string.Equals(c.Id, change.Column, StringComparison.Ordinal));
            var source = Source(column.Entry, column.Id);
            if (attribute["type"] is JsonObject typed && typed["ref"]?.GetValue<string>() is { } typeId && Keeps(typeId, column.Entry))
            {
                foreach (var facet in new[] { "length", "precision", "scale" })
                    attribute.Remove(facet);
                Require(attribute, source);
            }
            else
            {
                TypeAttribute(attribute, source, dialectMap);
            }
        }

        // 3. Removed: a field whose column is gone goes; its attribute stays. This runs before map by name, which then sees the attribute
        // as free: a column dropped and re-added under the same name (a new id) maps the attribute that followed the old one instead of
        // getting a second attribute in step 5. With columns named, only the gone columns recorded under the name of a named one (the
        // column dropped and re-added): their fields go and their provenance entries with them.
        var kept = binding;
        HashSet<string> removed = [];
        if (drift.Reads && drift.Removed.Count > 0)
        {
            var recorded = binding.Provenance?.Columns;
            var namedParts = named is { } set
                ? columns.Where(c => set.Contains(c.Id)).Select(c => ColumnShapeHash.NamePartOf(c.Shape)).ToHashSet(StringComparer.Ordinal)
                : null;
            removed = drift.Removed.Where(id => namedParts is null
                || (recorded is not null && recorded.TryGetValue(id, out var was) && namedParts.Contains(ColumnShapeHash.NamePartOf(was))))
                .ToHashSet(StringComparer.Ordinal);
        }

        if (removed.Count > 0)
        {
            bool Gone(string column) => removed.Contains(column) && BindingDrift.Find(columns, column) < 0;
            foreach (var field in fields.OfType<JsonObject>().Where(f => f["column"]?.GetValue<string>() is { } c && Gone(c)).ToList())
            {
                fields.Remove(field);
                notes.Add($"Column '{field["column"]!.GetValue<string>()}' is gone from table '{table.Name}': the field that mapped '{BindingDrift.AttributeName(snapshot, entity, field["attribute"]?.GetValue<string>() ?? "")}' goes, and the attribute stays.");
            }

            kept = binding with { Fields = [.. binding.Fields.Where(f => !Gone(f.Column))] };
        }

        // 4. Map by name: the attributes the binding does not map (after step 3) and the free columns.
        var matches = MatchByName(entity, kept, columns, named);
        var matched = matches.Select(m => m.Column.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (candidate, column) in matches)
            fields.Add(new JsonObject { ["attribute"] = candidate.Ref, ["column"] = column.Id });

        // 5. Added: an attribute per added column still free, mapped to it.
        foreach (var added in drift.Added.Where(a => In(a.Column) && !matched.Contains(a.Column)))
        {
            var column = columns.First(c => string.Equals(c.Id, added.Column, StringComparison.Ordinal));
            var attribute = AttributeFor(Source(column.Entry, column.Id), dialectMap, taken);
            attributes.Add(attribute);
            fields.Add(new JsonObject { ["attribute"] = attribute["id"]!.GetValue<string>(), ["column"] = column.Id });
        }

        // 6. Provenance: every column of the source, or with columns the entries of those (every column while the binding has none).
        RecordProvenance(bindingNode, columns, named is not null && drift.Reads ? [.. named, .. removed] : null);

        if (attributes.Count > 0)
            node["attributes"] = attributes;
        if (fields.Count > 0)
            bindingNode["fields"] = fields;
        else
            bindingNode.Remove("fields");
        if (JsonNode.DeepEquals(original, node))
            return null;
        _notes.AddRange(notes);
        return node;
    }

    /// <summary>
    /// Whether an attribute typed by <paramref name="typeId"/> keeps its type over a retyped column: the column holds no other type
    /// (<c>typeRef</c>) and can hold the type's values (MQ4083's fit).
    /// </summary>
    private bool Keeps(string typeId, Column column) =>
        (column.TypeRef?.Ref is not { } held || string.Equals(held, typeId, StringComparison.Ordinal))
        && snapshot.Get<Element>(typeId) is { } type && ColumnTypeRules.Holds(type, column);

    // ---- shared -----------------------------------------------------------------------------------------------------------------

    /// <summary>The entity and the index of its binding to the database, or why the operation is refused.</summary>
    private string? Bound(Database db, string id, out Entity? entity, out int index)
    {
        index = -1;
        entity = snapshot.Get<Entity>(id);
        if (entity is null)
            return $"'{id}' is not an entity.";
        index = entity.Bindings.ToList().FindIndex(b => b.Database == db.Id);
        return index < 0
            ? $"Entity '{entity.Name}' has no binding to database '{db.Name}': add one first, or create its table (materialize-tables)."
            : null;
    }

    private string ReadableSource(string key) => snapshot.Get<Element>(key.Split('@')[0]) is { } element ? element.KindName + " " + element.Name : key;

    /// <summary>
    /// Map by name (decision 7): each free column, in the source's column order, matched to the first candidate the binding does not
    /// map whose loose name is the column's (a navigation key also without <c>Id</c>). A free column is one no field, constant, listing
    /// or soft delete names; with <paramref name="named"/>, only those columns.
    /// </summary>
    private List<(FieldCandidate Candidate, ShapeColumn Column)> MatchByName(Entity entity, EntityBinding binding, IReadOnlyList<ShapeColumn> columns,
        IReadOnlySet<string>? named)
    {
        var taken = new HashSet<int>();
        void Name(string reference)
        {
            if (BindingDrift.Find(columns, reference) is var at and >= 0)
                taken.Add(at);
        }

        foreach (var field in binding.Fields)
            Name(field.Column);
        foreach (var constant in binding.Constants)
            Name(constant.Column);
        foreach (var listed in binding.Columns)
            Name(listed.Column);
        if (binding.Delete?.Soft is { } soft)
            Name(soft.Column);

        var mapped = binding.Fields.Select(f => f.Attribute).ToHashSet(StringComparer.Ordinal);
        var open = Candidates(entity).Where(c => !mapped.Contains(c.Ref)).ToList();
        var matches = new List<(FieldCandidate, ShapeColumn)>();
        for (var i = 0; i < columns.Count && open.Count > 0; i++)
        {
            var column = columns[i];
            if (taken.Contains(i) || (named is not null && !named.Contains(column.Id)))
                continue;
            var loose = ColumnShapeHash.Loose(column.Name);
            if (loose.Length == 0)
                continue;
            var at = open.FindIndex(c => string.Equals(ColumnShapeHash.Loose(c.Name), loose, StringComparison.Ordinal)
                || (c.Navigation && c.Name.EndsWith("Id", StringComparison.Ordinal) && string.Equals(ColumnShapeHash.Loose(c.Name[..^2]), loose, StringComparison.Ordinal)));
            if (at < 0)
                continue;
            matches.Add((open[at], column));
            open.RemoveAt(at);
        }

        return matches;
    }

    /// <summary>
    /// What a binding of the entity can map, from files: root first, each level's own attributes then its stereotypes' ones (a value
    /// object attribute stands for its members, a collection has no column), then the to-one navigation keys, by relation id.
    /// </summary>
    private List<FieldCandidate> Candidates(Entity entity)
    {
        var chain = Chain(entity);
        var result = new List<FieldCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(ModelAttribute attribute)
        {
            if (!seen.Add(attribute.Id) || attribute.Collection)
                return;
            if (attribute.Type.Ref is { } typeId && snapshot.Get<ValueObject>(typeId) is { } valueObject)
            {
                foreach (var member in valueObject.Attributes.Where(m => !m.Collection && (m.Type.Ref is null || snapshot.Get<ValueObject>(m.Type.Ref) is null)))
                    result.Add(new FieldCandidate(attribute.Id + "." + member.Id, attribute.Name + Casing.Pascal(member.Name), false));
                return;
            }

            result.Add(new FieldCandidate(attribute.Id, attribute.Name, false));
        }

        foreach (var level in chain.AsEnumerable().Reverse())
        {
            foreach (var attribute in level.Attributes)
                Add(attribute);
            foreach (var applied in snapshot.EffectiveStereotypes(level))
            {
                foreach (var attribute in applied.Stereotype.Attributes)
                    Add(attribute);
            }
        }

        var mine = chain.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var relation in snapshot.All<Relation>().Where(r => r.Ends.Count == 2).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            for (var i = 0; i < 2; i++)
            {
                var (self, other) = (relation.Ends[i], relation.Ends[1 - i]);
                // Field candidates skip a relation with an end of several types (several-types-design.md decision 21): MQ4116 refuses such a field.
                // several-types-2: joins — map by name offers the new field forms for a holding entity (section 19.11).
                if (self.SingleEntity is not { } selfEntity || other.HasSeveralTypes || !mine.Contains(selfEntity) || other.Max != MaxCardinality.One
                    || !seen.Add(other.Id))
                    continue;
                result.Add(new FieldCandidate(other.Id, other.Navigation.Length > 0 ? other.Navigation + "Id" : Casing.Camel(other.Role + "Id"), true));
            }
        }

        return result;
    }

    /// <summary>The names of the entity's attributes: its own, its bases' and its stereotypes' (what an attribute's new name avoids).</summary>
    private HashSet<string> AttributeNames(Entity entity)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var level in Chain(entity))
        {
            foreach (var attribute in level.Attributes)
                names.Add(attribute.Name);
            foreach (var applied in snapshot.EffectiveStereotypes(level))
            {
                foreach (var attribute in applied.Stereotype.Attributes)
                    names.Add(attribute.Name);
            }
        }

        return names;
    }

    /// <summary>The entity and its bases, the entity first, stopping at a loop.</summary>
    private List<Entity> Chain(Entity entity)
    {
        var chain = new List<Entity>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base is { } b ? snapshot.Get<Entity>(b) : null)
            chain.Add(e);
        return chain;
    }

    /// <summary>The working copy's own attribute with the id, or <see langword="null"/> (a base's, a stereotype's, a member or an end).</summary>
    private static JsonObject? Own(JsonArray attributes, string id) =>
        attributes.OfType<JsonObject>().FirstOrDefault(a => string.Equals(a["id"]?.GetValue<string>(), id, StringComparison.Ordinal));

    private static string Plural(int n, string noun) => n.ToString(CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");
}
