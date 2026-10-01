using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>Attribute columns: scalars, enums (int, string), value objects (embedded, table, json) and collections.</summary>
internal sealed partial class DatabaseRun
{
    /// <summary>Adds the columns of one attribute (section 7.5) to a table.</summary>
    /// <param name="t">The table.</param>
    /// <param name="a">The attribute, as seen by the entity (or relation) the table stores.</param>
    /// <param name="am">The attribute's mapping in this database.</param>
    /// <param name="isKey">Whether the attribute is part of the hierarchy root's key (named by <c>keyColumn</c>, never nullable).</param>
    /// <param name="forceNullable">Whether the column must be nullable whatever the attribute says (TPH derived attributes).</param>
    /// <param name="entityName">The <c>{entity}</c> token for the key column pattern.</param>
    private void AddAttributeColumns(TableBuild t, RAttribute a, AttributeMapping? am, bool isKey, bool forceNullable, string entityName)
    {
        if (am is { Ignore: true })
            return;
        if (a.Derived is { Stored: false } && am is null && !t.OverlayColumns.ContainsKey(a.Id))
            return; // derived: not stored unless mapped (S6) or marked stored
        var nullable = !isKey && (forceNullable || !a.Required);
        var name = isKey
            ? Render(_conv.KeyColumn, ("attribute", a.Name), ("entity", entityName), ("table", t.Table.Name))
            : ColumnName(a.Name);
        AddValueColumns(t, a, a.Id, name, nullable, am?.Storage, am?.Prefix ?? a.Name, 0);
    }

    private void AddValueColumns(TableBuild t, RAttribute a, string path, string name, bool nullable, StorageKind? storage, string prefix, int depth)
    {
        if (a.Type.ReferenceType is { } reference)
        {
            // Reference-typed (reference-types-seeds-localization.md section 1.4): a single value is one column typed "reference"
            // whose code facets ride along; a collection gets no column and no table (the mapping lists it as template-defined).
            if (a.Collection)
                return;
            t.Deps.Element(reference.Id).Add(ResolveRun.ReferenceDataKey);
            var c = AddColumn(t, path, name, "reference", a.Length, null, null, nullable, a.Default, a, path, reference);
            c.Strategy = _run.StorageChoice(reference.Id, _db.Id)?.Strategy;
            return;
        }

        if (a.Type.ValueObject is { } vo)
        {
            t.Deps.Element(vo.Id).AddRange(_run.DepsOf(vo).ToList());
            if (a.Collection)
            {
                if (CollectionStorage(storage) == StorageKind.Json)
                    AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
                else
                    DeferChildTable(t, a, path, true);
                return;
            }

            var kind = storage is StorageKind.Embedded or StorageKind.Table or StorageKind.Json ? storage.Value
                : _conv.ValueObjectStorage is StorageKind.Embedded or StorageKind.Table or StorageKind.Json ? _conv.ValueObjectStorage : StorageKind.Embedded;
            switch (kind)
            {
                case StorageKind.Json:
                    AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
                    break;
                case StorageKind.Table:
                    DeferChildTable(t, a, path, false);
                    break;
                default:
                    Embed(t, vo, path, prefix, nullable, depth, null);
                    break;
            }

            return;
        }

        if (a.Collection)
        {
            if (CollectionStorage(storage) == StorageKind.Json)
                AddColumn(t, path, name, "json", null, null, null, nullable, null, a, path);
            else
                DeferChildTable(t, a, path, true);
            return;
        }

        if (a.Type.Enum is { } e)
        {
            AddEnumColumn(t, a, e, path, name, nullable, storage);
            return;
        }

        AddColumn(t, path, name, a.Type.Builtin ?? "string", a.Length, a.Precision, a.Scale, nullable, a.Default, a, path, scalar: a.Type.Scalar);
    }

    private StorageKind CollectionStorage(StorageKind? storage) =>
        storage is StorageKind.Table or StorageKind.Json ? storage.Value
        : _conv.ValueObjectCollectionStorage == StorageKind.Json ? StorageKind.Json : StorageKind.Table;

    /// <summary>
    /// Embedded value object: prefixed columns, recursively (<c>valueObjectColumn</c>, keys <c>&lt;attr&gt;.&lt;member&gt;</c>). A
    /// member whose value object already contains it (<paramref name="outer"/>) is not embedded again: that is a containment cycle
    /// (MQ3015), which validation rejects, so only a speculative resolution of an invalid snapshot (engine-design.md section 7) meets
    /// it, and there it must end quickly instead of expanding the cycle up to the depth limit. Cancellation is observed per call.
    /// </summary>
    private void Embed(TableBuild t, RValueObject vo, string path, string prefix, bool nullable, int depth, Containment? outer)
    {
        _run.Ct.ThrowIfCancellationRequested();
        var containment = new Containment(vo, outer);
        foreach (var m in vo.Attributes)
        {
            var memberPath = path + "." + m.Id;
            var memberName = Render(_conv.ValueObjectColumn, ("attribute", prefix), ("member", m.Name));
            var memberNullable = nullable || !m.Required;
            if (m.Type.ValueObject is { } nested && !m.Collection && depth < 16 && !containment.Contains(nested))
            {
                t.Deps.Element(nested.Id).AddRange(_run.DepsOf(nested).ToList());
                Embed(t, nested, memberPath, memberName, memberNullable, depth + 1, containment);
            }
            else if (m.Collection || m.Type.ValueObject is not null)
            {
                AddColumn(t, memberPath, memberName, "json", null, null, null, memberNullable, null, m, memberPath);
            }
            else if (m.Type.Enum is { } e)
            {
                AddEnumColumn(t, m, e, memberPath, memberName, memberNullable, null);
            }
            else
            {
                AddColumn(t, memberPath, memberName, m.Type.Builtin ?? "string", m.Length, m.Precision, m.Scale, memberNullable, m.Default, m, memberPath,
                    scalar: m.Type.Scalar);
            }
        }
    }

    /// <summary>The value objects an embedding is inside, innermost first.</summary>
    private sealed record Containment(RValueObject ValueObject, Containment? Outer)
    {
        public bool Contains(RValueObject vo)
        {
            for (var c = this; c is not null; c = c.Outer)
            {
                if (ReferenceEquals(c.ValueObject, vo))
                    return true;
            }

            return false;
        }
    }

    /// <summary>Enum storage: <c>int</c> (value, else ordinal) or <c>string</c> (code, else name). The lookup-table option is retired (MQ7012): a reference type replaces it, and its template decides the lookup table.</summary>
    private void AddEnumColumn(TableBuild t, RAttribute a, REnum e, string path, string name, bool nullable, StorageKind? storage)
    {
        t.Deps.AddRange(_run.DepsOf(e).ToList());
        var kind = storage is StorageKind.Int or StorageKind.String ? storage.Value
            : _conv.EnumStorage is StorageKind.Int or StorageKind.String ? _conv.EnumStorage : StorageKind.Int;
        var member = a.Default is string text
            ? e.Members.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.Ordinal) || string.Equals(m.Code, text, StringComparison.Ordinal))
            : null;
        switch (kind)
        {
            case StorageKind.String:
                AddColumn(t, path, name, "string", StringLength(e), null, null, nullable, member is null ? a.Default : member.Code ?? member.Name, a, path);
                break;
            default:
                AddColumn(t, path, name, IntegerType(e), null, null, null, nullable, member is null ? a.Default : ValueOf(e, member), a, path);
                break;
        }
    }

    private static long ValueOf(REnum e, REnumMember member)
    {
        for (var i = 0; i < e.Members.Count; i++)
        {
            if (ReferenceEquals(e.Members[i], member))
                return member.Value ?? i;
        }

        return member.Value ?? 0;
    }

    private static string IntegerType(REnum e) =>
        e.Members.Any(m => m.Value is < int.MinValue or > int.MaxValue) ? "int64" : "int32";

    private static int StringLength(REnum e) => Math.Max(1, e.Members.Select(m => (m.Code ?? m.Name).Length).DefaultIfEmpty(1).Max());

    private void DeferChildTable(TableBuild owner, RAttribute a, string path, bool collection) =>
        owner.Deferred.Add(() => CreateChildTable(owner, a, path, collection));

    /// <summary>
    /// A child table (value object stored as <c>table</c>, or a collection): the owner's key as a foreign key, <c>position</c> for
    /// collections, then the value columns; key <c>&lt;ownerId&gt;.&lt;attributeId&gt;@&lt;databaseId&gt;</c>.
    /// </summary>
    private void CreateChildTable(TableBuild owner, RAttribute a, string path, bool collection)
    {
        var ownerId = owner.Table.Entity?.Id ?? owner.Table.Relation?.Id ?? owner.Table.Key;
        var ownerName = owner.Table.Entity?.Name ?? owner.Table.Relation?.Name ?? owner.Table.Name;
        var overlay = _overlays.GetValueOrDefault("child:" + ownerId + "." + a.Id);
        var key = ownerId + "." + a.Id + "@" + _db.Id;
        var r = new RTable
        {
            Id = key,
            Key = key,
            Name = overlay is { Name.Length: > 0 } ? overlay.Name
                : NamePattern.Render(_conv.ChildTable, new Dictionary<string, string>(StringComparer.Ordinal) { ["entity"] = ownerName, ["attribute"] = a.Name },
                    _conv.TableCase, _conv.PluralTables ? _run.Inflector : null, owner.Table.Entity is { } ownerEntity ? ExplicitPlural(ownerEntity.Id) : null),
            Schema = overlay?.Schema is not null ? SchemaName(overlay.Schema) : owner.Table.Schema,
            Database = _rdb,
            Origin = "synthesized",
            Entity = owner.Table.Entity,
            Relation = owner.Table.Relation,
            Attribute = a,
            Comment = overlay?.Comment,
        };
        var t = new TableBuild(_run.Keys, r, null, overlay, owner.SourceElementId);
        t.Deps.AddRange(owner.Deps.ToList());
        if (overlay is not null)
        {
            t.Deps.Element(overlay.Id);
            AddConstraintFiles(t, overlay);
        }

        AddTable(t);
        owner.Children.Add(t);
        var ownerKeys = new List<string>();
        foreach (var pkKey in owner.PrimaryKey)
        {
            if (owner.Resolve(pkKey) is not { } ownerColumn)
                continue;
            var columnName = Render(_conv.ForeignKeyColumn, ("role", ownerName), ("key", ownerColumn.Attribute?.Name ?? ownerColumn.Name),
                ("entity", ownerName), ("table", r.Name));
            AddColumn(t, ownerColumn.Key, columnName, ownerColumn.Type, ownerColumn.Length, ownerColumn.Precision, ownerColumn.Scale, false, null, null, null,
                scalar: ScalarOf(ownerColumn));
            ownerKeys.Add(ownerColumn.Key);
        }

        if (collection)
            AddColumn(t, "position", Render(_conv.OrderColumn), "int32", null, null, null, false, null, null, null);
        if (a.Type.ValueObject is { } vo)
        {
            foreach (var m in vo.Attributes)
            {
                var memberPath = path + "." + m.Id;
                if (m.Type.ValueObject is { } nested && !m.Collection)
                    Embed(t, nested, memberPath, m.Name, !m.Required, 1, new Containment(vo, null));
                else
                    AddValueColumns(t, m, memberPath, ColumnName(m.Name), !m.Required, m.Collection ? StorageKind.Json : null, m.Name, 1);
            }
        }
        else
        {
            t.Deps.Add(InflectionKey);
            AddScalarValue(t, a, path, ColumnName(_run.Inflector.Singularize(a.Name)));
        }

        if (t.PrimaryKey.Count == 0)
        {
            t.PrimaryKey.AddRange(ownerKeys);
            if (collection)
                t.PrimaryKey.Add("position");
        }

        if (ownerKeys.Count > 0)
            t.ForeignKeys.Add(new ForeignKeySpec(t, ownerKeys, owner, null, [], "cascade", "no-action", null));
        foreach (var action in t.Deferred.ToList())
            action();
        t.Deferred.Clear();
    }

    /// <summary>The value column of a collection of scalars or enums in its child table.</summary>
    private void AddScalarValue(TableBuild t, RAttribute a, string path, string name)
    {
        if (a.Type.Enum is { } e)
            AddEnumColumn(t, a, e, path, name, false, null);
        else
            AddColumn(t, path, name, a.Type.Builtin ?? "string", a.Length, a.Precision, a.Scale, false, null, a, path, scalar: a.Type.Scalar);
    }

    /// <summary>
    /// Adds a synthesized column (or returns the existing one with that key), with its overlay applied. A column that stores a custom
    /// type (<paramref name="scalar"/>: an attribute's value, a child table's value, or a key column copied from one) takes the
    /// custom type's native type for the dialect, unless an overlay sets the native type or changes the type away from the custom
    /// type's base; this is the one place the custom type's native types apply, so entity, child and junction tables agree.
    /// </summary>
    private RColumn AddColumn(TableBuild t, string key, string name, string type, int? length, int? precision, int? scale, bool nullable,
        object? defaultValue, RAttribute? attribute, string? path, RReferenceType? reference = null, RScalarType? scalar = null)
    {
        if (t.ByKey.TryGetValue(key, out var existing))
            return existing;
        var c = new RColumn
        {
            Id = t.Table.Key + "/" + key,
            Key = key,
            Name = name,
            Table = t.Table,
            Type = type,
            Length = length,
            Precision = precision,
            Scale = scale,
            Nullable = nullable,
            Default = defaultValue,
            Attribute = attribute,
            AttributePath = path,
            ReferenceType = reference,
            CodeType = reference?.Code.Type,
        };
        if (t.OverlayColumns.TryGetValue(key, out var overlay))
        {
            if (overlay.Name.Length > 0)
                c.Name = overlay.Name;
            if (overlay.Type is not null)
                c.Type = overlay.Type;
            c.Length = overlay.Length ?? c.Length;
            c.Precision = overlay.Precision ?? c.Precision;
            c.Scale = overlay.Scale ?? c.Scale;
            c.Nullable = overlay.Nullable ?? c.Nullable;
            ApplyColumnFile(t, c, overlay);
            t.ByKey.TryAdd(overlay.Id, c);
        }

        ApplyFacetDefaults(c);
        if (scalar is not null)
        {
            t.Deps.AddRange(_run.DepsOf(scalar).ToList());
            if (string.Equals(PhysicalType(c), scalar.Base, StringComparison.Ordinal))
                _columnScalars[c] = scalar;
            else
                scalar = null;
        }

        c.NativeType = overlay?.NativeType ?? NativeType(PhysicalType(c), c.Length, c.Precision, c.Scale, scalar);
        t.Columns.Add(c);
        t.ByKey[key] = c;
        _run.Register(c);
        return c;
    }

    /// <summary>Overlay columns that override no synthesized column (<c>attribute</c> null): extra columns, appended in file order.</summary>
    private void AddOverlayExtraColumns(TableBuild t)
    {
        if (t.Overlay is null)
            return;
        foreach (var column in t.Overlay.Columns)
        {
            if (column.Attribute is not null || t.ByKey.ContainsKey(column.Id))
                continue;
            var c = new RColumn
            {
                Id = column.Id,
                Key = column.Id,
                Name = column.Name,
                Table = t.Table,
                Type = column.Type ?? "string",
                Length = column.Length,
                Precision = column.Precision,
                Scale = column.Scale,
                Nullable = column.Nullable ?? true,
            };
            ApplyColumnFile(t, c, column);
            ApplyFacetDefaults(c);
            c.NativeType = column.NativeType ?? NativeType(c.Type, c.Length, c.Precision, c.Scale);
            t.Columns.Add(c);
            t.ByKey[column.Id] = c;
            _run.Register(c);
        }
    }
}
