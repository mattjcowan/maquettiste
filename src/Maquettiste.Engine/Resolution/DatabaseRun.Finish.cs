using System.Collections.Immutable;
using System.Globalization;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>Constraints, entity mappings, join paths, identifier limits and the published database.</summary>
internal sealed partial class DatabaseRun
{
    private readonly Dictionary<string, SortedDictionary<string, RJoinPath>> _joinsByEntity = new(StringComparer.Ordinal);
    private readonly Dictionary<string, REntityMapping> _mappingByEntity = new(StringComparer.Ordinal);

    private void FinishTables()
    {
        foreach (var t in _tableOrder)
        {
            // A table file's own package wins; a table without one is in its entity's or relation's (next-phase-1-design.md 4.3).
            t.Table.Package ??= t.Table.Entity?.Package ?? t.Table.Relation?.Package;
            AddOverlayExtraColumns(t);
            // Inherited members follow the own ones (next-phase-2-design.md section 3.1), before positions and keys.
            AddInheritedMembers(t);
            for (var i = 0; i < t.Columns.Count; i++)
                t.Columns[i].Position = i;
            FillComments(t);
            var pk = t.PrimaryKey.Select(t.Resolve).OfType<RColumn>().Distinct().ToList();
            if (pk.Count == 0)
                continue;
            foreach (var column in pk)
            {
                column.IsPrimaryKey = true;
                column.Nullable = false;
            }

            t.Table.PrimaryKey = new RPrimaryKey { Id = t.PrimaryKeyId, Name = t.PrimaryKeyName ?? Render(_conv.PrimaryKeyName, ("table", t.Table.Name)),
                Columns = pk, Clustered = t.PrimaryKeyClustered, WithoutOverlaps = t.PrimaryKeyWithoutOverlaps,
                MemberOrigin = t.PrimaryKeyOrigin ?? RMemberOrigin.Own };
        }

        foreach (var t in _tableOrder)
        {
            var foreignKeys = new List<RForeignKey>();
            foreach (var spec in t.ForeignKeys)
            {
                var target = spec.Target ?? (spec.TargetKey is null ? null : _tables.GetValueOrDefault(spec.TargetKey));
                spec.Target = target;
                var columns = spec.Columns.Select(t.Resolve).ToList();
                if (target is null)
                {
                    ReportUnresolved(t, spec, $"the referenced table '{spec.TargetKey}' is not in the database");
                    continue;
                }

                if (columns.Count == 0 || columns.Any(c => c is null))
                {
                    ReportUnresolved(t, spec, $"it names columns that are not in the table ({string.Join(", ", spec.Columns.Where(k => t.Resolve(k) is null))})");
                    continue;
                }

                var referenced = spec.TargetColumns.Count > 0
                    ? spec.TargetColumns.Select(target.Resolve).ToList()
                    : [.. target.Table.PrimaryKey?.Columns ?? []];
                if (referenced.Any(c => c is null))
                {
                    ReportUnresolved(t, spec, $"it references columns that are not in table '{target.Table.Name}' ({string.Join(", ", spec.TargetColumns.Where(k => target.Resolve(k) is null))})");
                    continue;
                }

                if (referenced.Count != columns.Count)
                {
                    ReportUnresolved(t, spec, referenced.Count == 0
                        ? $"the referenced table '{target.Table.Name}' has no primary key"
                        : string.Create(CultureInfo.InvariantCulture,
                            $"it has {columns.Count} column(s) but references {referenced.Count} column(s) of table '{target.Table.Name}'"));
                    continue;
                }
                var fk = spec.Result;
                fk.Id = spec.Id;
                fk.Columns = [.. columns!];
                fk.ReferencedTable = target.Table;
                fk.ReferencedColumns = [.. referenced!];
                // Only the key's own columns; MQ4060 reports the others.
                if (fk.OnDelete is "set-null" or "set-default")
                    fk.OnDeleteColumns = [.. spec.OnDeleteColumns.Select(t.Resolve).Where(c => c is not null && fk.Columns.Contains(c)).Distinct().Select(c => c!)];
                fk.Name = spec.Name ?? Render(_conv.ForeignKeyName, ("table", t.Table.Name), ("columns", Joined(fk.Columns)));
                foreach (var column in fk.Columns)
                    column.IsForeignKey = true;
                spec.Resolved = true;
                foreignKeys.Add(fk);
            }

            t.Table.ForeignKeys = foreignKeys;
            t.Table.Uniques = [.. t.Uniques.Select(u => (Spec: u, Columns: u.Columns.Select(t.Resolve).ToList()))
                .Where(x => x.Columns.Count > 0 && x.Columns.All(c => c is not null))
                .Select(x => new RUnique
                {
                    Id = x.Spec.Id,
                    Name = x.Spec.Name ?? RenderWithName(_conv.UniqueName, t, x.Columns!, x.Spec.NameToken),
                    Columns = [.. x.Columns!],
                    NullsNotDistinct = x.Spec.NullsNotDistinct,
                    WithoutOverlaps = x.Spec.WithoutOverlaps,
                    MemberOrigin = x.Spec.Origin ?? RMemberOrigin.Own,
                })];
            var indexes = t.Indexes.Select(i => (Spec: i, Columns: i.Columns.Select(c => (Column: c.Column is null ? null : t.Resolve(c.Column), c.Descending, c.Expression, c.Length, c.OperatorClass, c.Nulls)).ToList(),
                    Include: i.Include.Select(t.Resolve).ToList()))
                .Where(x => x.Columns.Count > 0 && x.Columns.All(c => c.Column is not null || c.Expression is not null) && x.Include.All(c => c is not null))
                .ToList();
            // An index a table file declares (overlay or designed table) overrides the index an attribute's `indexed` flag synthesizes on the
            // same columns, so a file can change its sort order, method or name without producing a second index of the same name. An
            // expression is named "expr" in a conventional index name.
            static string IndexColumnNames(IEnumerable<(RColumn? Column, bool Descending, string? Expression, int? Length, string? OperatorClass, string? Nulls)> columns) =>
                string.Join('_', columns.Select(c => c.Column?.Name ?? "expr"));
            var fileColumnSets = indexes.Where(x => x.Spec.FromFile && x.Columns.All(c => c.Column is not null)).Select(x => Joined(x.Columns.Select(c => c.Column!))).ToHashSet(StringComparer.Ordinal);
            indexes.RemoveAll(x => !x.Spec.FromFile && fileColumnSets.Contains(Joined(x.Columns.Select(c => c.Column!))));
            t.Table.Indexes = [.. indexes
                .Select(x => new RIndex
                {
                    Id = x.Spec.Id,
                    Name = x.Spec.Name ?? Render(_conv.IndexName, ("table", t.Table.Name), ("columns", IndexColumnNames(x.Columns))),
                    Columns = [.. x.Columns.Select(c => new RIndexColumn
                    {
                        Column = c.Column, Expression = c.Column is null ? c.Expression : null, Descending = c.Descending, Length = c.Length, OperatorClass = c.OperatorClass,
                        Nulls = c.Nulls,
                    })],
                    Include = [.. x.Include!],
                    Where = x.Spec.Where,
                    Unique = x.Spec.Unique,
                    Method = x.Spec.Method,
                    Storage = x.Spec.Storage ?? [],
                    MemberOrigin = x.Spec.Origin ?? RMemberOrigin.Own,
                })];
            // Partition columns the table does not have leave the partitioning out (validation's MQ4008 says so).
            if (t.PartitionBy is { } partitionBy && partitionBy.Columns.Select(t.Resolve).ToList() is var partitionColumns && partitionColumns.All(c => c is not null))
                t.Table.PartitionBy = new RPartitionBy { Strategy = ResolutionValues.Kebab(partitionBy.Strategy), Columns = [.. partitionColumns!] };
            // An exclusion constraint naming a column the table does not have is left out (validation's MQ4008 says so).
            t.Table.Exclusions = [.. t.Exclusions
                .Select(x => (Spec: x, Columns: x.Elements.Select(e => e.Column is null ? null : t.Resolve(e.Column)).ToList()))
                .Where(x => x.Spec.Elements.Select((e, i) => e.Column is null || x.Columns[i] is not null).All(ok => ok))
                .Select(x => new RExclusion
                {
                    Id = x.Spec.Id,
                    Name = x.Spec.Name ?? "ex_" + t.Table.Name + "_" + string.Join('_', x.Columns.Select(c => c?.Name ?? "expr")),
                    Method = ResolutionValues.Kebab(x.Spec.Method),
                    Elements = [.. x.Spec.Elements.Select((e, i) => new RExclusionElement
                    {
                        Column = x.Columns[i], Expression = e.Column is null ? e.Expression : null, OperatorClass = e.OperatorClass, Operator = e.Operator,
                    })],
                    Where = x.Spec.Where,
                    Deferrable = ResolutionValues.Kebab(x.Spec.Deferrable),
                })];
            t.Table.Checks = [.. t.Checks.Select(c => ResolveCheck(t, c)).OfType<RCheck>()];
        }

        // Each table's keys are frozen on their own (in parallel: a table's set shares only immutable arrays with others).
        _run.ForEach(_tableOrder.Count, i =>
        {
            var t = _tableOrder[i];
            var deps = t.Deps.ToList();
            t.Table.Dependencies = deps;
            t.Table.Columns = new RList<RColumn>(t.Columns, deps);
            foreach (var column in t.Columns)
                column.Dependencies = deps;
        });
    }

    /// <summary>
    /// The <c>comments</c> convention: with <c>descriptions</c> (the default), a table without an explicit comment takes its own
    /// description, else the description of what it stores (a child table's attribute, an entity table's entity, a junction's
    /// relation), and a column without one takes its own entry's description, else its attribute's. Those elements are already
    /// among the table's dependencies (the entity, value object or relation that produced the table and its columns, and the
    /// conventions key), so a changed description replans the table.
    /// </summary>
    private void FillComments(TableBuild t)
    {
        if (_conv.Comments != CommentSource.Descriptions)
            return;
        var table = t.Table;
        table.Comment ??= Text(table.Description)
            ?? (table.Attribute is { } attribute ? Text(attribute.Description) : Text(table.Entity?.Description) ?? Text(table.Relation?.Description));
        foreach (var column in t.Columns)
            column.Comment ??= Text(column.Description) ?? Text(column.Attribute?.Description);

        static string? Text(string? description) => string.IsNullOrWhiteSpace(description) ? null : description.Trim();
    }

    /// <summary>
    /// MQ4005 over the resolved columns: each foreign key column has the referenced column's type, length, precision, scale and native type,
    /// so the generated key is valid DDL. An attribute's logical type is never compared with its column's (the attribute's length is
    /// validation, the column's is storage). A mismatch is reported on the file that pins it: the foreign key column's overlay entry, its
    /// designed or imported table, the referenced column's overlay entry or table, else the relation end (or the attribute's entity) that
    /// produced the column. Validation's file-level MQ4005 already covers a column whose table and referenced table are both files that
    /// declare both types; such a column is not reported again.
    /// </summary>
    private void CheckForeignKeyColumns()
    {
        foreach (var t in _tableOrder)
        {
            foreach (var spec in t.ForeignKeys)
            {
                if (!spec.Resolved || spec.Target is not { } target)
                    continue;
                var fk = spec.Result;
                for (var i = 0; i < fk.Columns.Count && i < fk.ReferencedColumns.Count; i++)
                {
                    var column = fk.Columns[i];
                    var referenced = fk.ReferencedColumns[i];
                    if (SamePhysicalType(column, referenced) || ReportedByValidation(t, target, column, referenced))
                        continue;
                    var (elementId, pointer) = PinOf(t, column) ?? PinOf(target, referenced) ?? Origin(t, fk, column);
                    _run.AddDiagnostic("MQ4005",
                        $"Foreign key column '{t.Table.Name}.{column.Name}' is {Describe(column)} but references '{target.Table.Name}.{referenced.Name}', which is {Describe(referenced)} (database '{_db.Name}').",
                        elementId, pointer);
                }
            }
        }
    }

    /// <summary>
    /// MQ4061 over the resolved columns: a foreign key a table file declares with on-delete set-null, or set-default, sets columns that
    /// can take NULL (set-default: or have a default), so deleting a referenced row does not fail on a not-null column. Nullability
    /// is the resolved one (an overlay column, or the attribute behind it). A relation's set-null on a required end is MQ3011.
    /// </summary>
    private void CheckOnDeleteSets()
    {
        foreach (var t in _tableOrder)
        {
            var file = t.Source ?? t.Overlay;
            foreach (var spec in t.ForeignKeys)
            {
                var fk = spec.Result;
                if (!spec.Resolved || file is null || spec.FileId != file.Id || fk.OnDelete is not ("set-null" or "set-default"))
                    continue;
                var setDefault = fk.OnDelete == "set-default";
                var refused = (fk.OnDeleteColumns.Count > 0 ? fk.OnDeleteColumns : fk.Columns)
                    .Where(c => !c.Nullable && (!setDefault || (c.Default is null && c.DefaultSql is null)))
                    .ToList();
                if (refused.Count == 0)
                    continue;
                var at = IndexOf(file.ForeignKeys, f => f.Id == spec.Id);
                var names = string.Join(", ", refused.Select(c => c.Name));
                var action = setDefault ? "sets its columns to their defaults" : "sets its columns to NULL";
                var but = setDefault ? "not nullable and without a default" : "not nullable";
                var fix = fk.OnDeleteColumns.Count > 0 || fk.Columns.Count < 2
                    ? "make the column nullable or pick another action"
                    : "list the columns to set in onDeleteColumns (PostgreSQL), make the column nullable, or pick another action";
                _run.AddDiagnostic("MQ4061",
                    $"Foreign key '{fk.Name}' of table '{t.Table.Name}' {action} on delete, but {names} {(refused.Count == 1 ? "is" : "are")} {but}: deleting a referenced row in '{fk.ReferencedTable.Name}' fails; {fix}.",
                    file.Id, at < 0 ? "/foreignKeys" : "/foreignKeys/" + at.ToString(CultureInfo.InvariantCulture) + "/onDelete");
            }
        }
    }

    private static bool SamePhysicalType(RColumn a, RColumn b)
    {
        if (!string.Equals(a.Type, b.Type, StringComparison.Ordinal) || !string.Equals(a.NativeType, b.NativeType, StringComparison.OrdinalIgnoreCase))
            return false;
        // Only the facets the type has (as MQ3013 allows them) count: a key's length means nothing to an int64.
        var type = PhysicalType(a);
        return (type is not ("string" or "text" or "binary") || a.Length == b.Length)
            && (type is not ("decimal" or "time" or "datetime" or "datetimeoffset") || a.Precision == b.Precision)
            && (type is not "decimal" || a.Scale == b.Scale);
    }

    /// <summary>Whether validation's file-level MQ4005 reports the pair: both tables are files and both columns declare differing types.</summary>
    private static bool ReportedByValidation(TableBuild t, TableBuild target, RColumn column, RColumn referenced) =>
        t.Source?.Columns.FirstOrDefault(c => c.Id == column.Key)?.Type is { } a
        && target.Source?.Columns.FirstOrDefault(c => c.Id == referenced.Key)?.Type is { } b
        && !string.Equals(a, b, StringComparison.Ordinal);

    /// <summary>The file and pointer that pin a column's physical type: its overlay entry, or its entry in a designed or imported table.</summary>
    private static (string ElementId, string Pointer)? PinOf(TableBuild t, RColumn column)
    {
        if (t.Source is { } source)
        {
            var at = IndexOf(source.Columns, c => c.Id == column.Key);
            return at < 0 ? (source.Id, "/columns") : (source.Id, ColumnPointer(at));
        }

        if (t.Overlay is { } overlay)
        {
            var at = IndexOf(overlay.Columns, c => c.Attribute == column.Key || (c.Attribute is null && c.Id == column.Key));
            if (at >= 0 && (overlay.Columns[at].Attribute is null || PinsType(overlay.Columns[at])))
                return (overlay.Id, ColumnPointer(at));
        }

        return null;
    }

    /// <summary>Where a synthesized foreign key column comes from: the relation end of its key, else the entity that declares its attribute, else the table's element.</summary>
    private (string? ElementId, string? Pointer) Origin(TableBuild t, RForeignKey fk, RColumn column)
    {
        if (fk.Relation is { } relation && _run.RelationSource(relation.Id) is { } source)
        {
            var at = fk.End is { } end ? IndexOf(source.Ends, e => e.Id == end.Id) : -1;
            return (source.Id, at < 0 ? "/ends" : "/ends/" + at.ToString(CultureInfo.InvariantCulture));
        }

        if (column.Attribute is { DeclaringEntity: { } declaring } attribute)
        {
            var at = _run.Model.Get<Entity>(declaring.Id) is { } entity ? IndexOf(entity.Attributes, a => a.Id == attribute.Id) : -1;
            return (declaring.Id, at < 0 ? null : "/attributes/" + at.ToString(CultureInfo.InvariantCulture));
        }

        return (t.SourceElementId, null);
    }

    private static string ColumnPointer(int index) => "/columns/" + index.ToString(CultureInfo.InvariantCulture);

    private static int IndexOf<T>(IReadOnlyList<T> items, Func<T, bool> match)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (match(items[i]))
                return i;
        }

        return -1;
    }

    /// <summary>A column's physical type for a message: the type with its facets, then the native type, such as <c>string(26) (varchar(26))</c>.</summary>
    private static string Describe(RColumn c)
    {
        var type = PhysicalType(c);
        var facets = type switch
        {
            "string" or "text" or "binary" when c.Length is { } length => "(" + length.ToString(CultureInfo.InvariantCulture) + ")",
            "decimal" when c.Precision is { } p => "(" + p.ToString(CultureInfo.InvariantCulture) + (c.Scale is { } s ? "," + s.ToString(CultureInfo.InvariantCulture) : "") + ")",
            "time" or "datetime" or "datetimeoffset" when c.Precision is { } p => "(" + p.ToString(CultureInfo.InvariantCulture) + ")",
            _ => "",
        };
        return c.Type + facets + " (" + c.NativeType + ")";
    }

    /// <summary>MQ4008: a foreign key that cannot be resolved is left out of its table, and said so.</summary>
    private void ReportUnresolved(TableBuild t, ForeignKeySpec spec, string reason)
    {
        var relation = spec.Result.Relation;
        if (spec.Inherited is { } inherited)
        {
            // A key a stereotype contributes is placed like any finding about an inherited member (next-phase-2-design.md section 3.1).
            var (pinId, pointer) = MemberPin(t, inherited.MemberId, inherited.From);
            _run.AddDiagnostic("MQ4008",
                $"Foreign key '{inherited.MemberId}' that stereotype '{inherited.From.Stereotype.Key}' gives table '{t.Table.Name}' of database '{_db.Name}' is left out: {reason}.",
                pinId, pointer);
            return;
        }

        var elementId = spec.FileId ?? (relation is not null && _run.RelationSource(relation.Id) is not null ? relation.Id : t.SourceElementId);
        var what = spec.Name is { } name ? $"Foreign key '{name}'"
            : relation is not null ? $"The foreign key of relation '{relation.Name}'"
            : "A foreign key";
        _run.AddDiagnostic("MQ4008", $"{what} in table '{t.Table.Name}' of database '{_db.Name}' is left out: {reason}.", elementId);
    }

    /// <summary>
    /// Once foreign keys are resolved: each foreign-key relation mapping takes its resolved key (or none), and the relation lists
    /// what that key's name and actions come from (<see cref="RForeignKey"/> is not a tracked object, section 11 and D38).
    /// </summary>
    private void FinishRelationMappings()
    {
        // several-types-2: storage — FinishTypeStorage runs beside this for a relation with an end of several types, which this skips: its
        // OneEntity reads throw on such an end (section 19.8, step 2). Today ResolveRelation records no physical shape for one.
        foreach (var phys in _relationPhysical.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
        {
            if (phys.Shape != RelationShape.ForeignKey || phys.Mapping is not { } mapping)
                continue;
            var dependentTable = phys.Dependent is { } dependent ? TableOf(dependent.OneEntity.Id) : null;
            var primary = phys.Foreign.FirstOrDefault(s => s.Resolved && ReferenceEquals(s.Host, dependentTable)) ?? phys.Foreign.FirstOrDefault(s => s.Resolved);
            mapping.ForeignKey = primary?.Result;
            if (phys.Relation is not { } relation)
                continue;
            var deps = _run.DepsOf(relation);
            foreach (var spec in phys.Foreign.Where(s => s.Resolved))
            {
                var host = spec.Host;
                deps.Add(Conventions).Add(InflectionKey).Element(_db.Id);
                if (host.Source is { } source)
                    deps.Element(source.Id);
                if (host.Overlay is { } overlay)
                    deps.Element(overlay.Id);
                if (host.Table.Entity is { } hostEntity)
                    deps.AddRange(_run.DepsOf(hostEntity).ToList());
                if (phys.Principal is { } principal && _placements.TryGetValue(principal.OneEntity.Id, out var pp))
                    deps.AddRange(KeyDeps(pp));
            }
        }
    }

    /// <summary>MQ4009: a table overlay that targets nothing this database synthesizes (for example a TPH-derived entity) is unused.</summary>
    private void ReportUnusedOverlays()
    {
        var used = new HashSet<string>(_tableOrder.Where(t => t.Overlay is not null).Select(t => t.Overlay!.Id), StringComparer.Ordinal);
        foreach (var (target, overlay) in _overlays.OrderBy(p => p.Value.Id, StringComparer.Ordinal))
        {
            if (used.Contains(overlay.Id))
                continue;
            var reason = target.StartsWith("entity:", StringComparison.Ordinal) && _bindingByEntity.ContainsKey(target["entity:".Length..])
                ? "the entity has a binding to this database, which wins over projection: materialize its table"
                : target.StartsWith("entity:", StringComparison.Ordinal) && _placements.TryGetValue(target["entity:".Length..], out var p)
                ? p.Bound ? "the entity is bound to a designed or imported table"
                : p.Strategy == InheritanceStrategy.Tph ? "the entity is TPH-derived and its rows live in the root entity's table; put the overlay on the root entity"
                : "the entity has no table of its own (TPC abstract)"
                : "its target has no synthesized table in this database";
            _run.AddDiagnostic("MQ4009", $"Table overlay '{overlay.Name}' in database '{_db.Name}' is not applied: {reason}.", overlay.Id);
        }
    }

    private string RenderWithName(string pattern, TableBuild t, IEnumerable<RColumn?> columns, string? name) => name is null
        ? Render(pattern, ("table", t.Table.Name), ("columns", Joined(columns.OfType<RColumn>())))
        : Render(pattern, ("table", t.Table.Name), ("columns", Joined(columns.OfType<RColumn>())), ("name", name));

    private static string Joined(IEnumerable<RColumn> columns) => string.Join('_', columns.Select(c => c.Name));

    private void BuildEntityMappings()
    {
        // Column mappings read finished tables only: computed in parallel, committed in placement order.
        var columns = new List<RColumnMapping>?[_placementOrder.Count];
        _run.ForEach(_placementOrder.Count, i =>
        {
            var p = _placementOrder[i];
            if (p.Table is not null && !p.ViaBinding)
                columns[i] = ColumnMappings(TablesOf(p), p.Entity.Attributes);
        });

        for (var i = 0; i < _placementOrder.Count; i++)
        {
            var p = _placementOrder[i];
            if (p.Table is null || p.ViaBinding)
                continue;
            var tph = p.Strategy == InheritanceStrategy.Tph && !p.Bound;
            var mapping = new REntityMapping
            {
                Database = _rdb,
                Table = p.Table.Table,
                Inheritance = p.Strategy is { } strategy ? ResolutionValues.Kebab(strategy) : null,
                DiscriminatorColumn = tph ? _discriminators.GetValueOrDefault(p.Table.Table.Key) : null,
                DiscriminatorValue = tph ? ResolutionValues.Plain(p.Mapping?.DiscriminatorValue) ?? p.Entity.Name : null,
                Columns = columns[i]!,
            };
            _mappingByEntity[p.Entity.Id] = mapping;
            _run.SetEntityMapping(p.Entity, _db.Name, mapping);
        }

        foreach (var (entity, t) in _promotedTables)
        {
            var mapping = new REntityMapping { Database = _rdb, Table = t.Table, Columns = ColumnMappings([t], entity.Attributes) };
            _mappingByEntity[entity.Id] = mapping;
            _run.SetEntityMapping(entity, _db.Name, mapping);
        }
    }

    /// <summary>The tables of a placement's rows: its table, or for TPT the tables of its chain, root first.</summary>
    private static List<TableBuild> TablesOf(Placement p)
    {
        var tables = new List<TableBuild>();
        if (p.Strategy == InheritanceStrategy.Tpt && !p.Bound)
        {
            var chain = new List<Placement>();
            for (var x = p; x is not null && !chain.Contains(x); x = x.Base)
                chain.Add(x);
            chain.Reverse();
            tables.AddRange(chain.Select(x => x.Table).OfType<TableBuild>().Distinct());
        }
        else
        {
            tables.Add(p.Table!);
        }

        return tables;
    }

    private static List<RColumnMapping> ColumnMappings(List<TableBuild> tables, IReadOnlyList<RAttribute> attributes)
    {
        var ids = new HashSet<string>(attributes.Select(a => a.Id), StringComparer.Ordinal);
        var all = new List<TableBuild>();
        foreach (var t in tables)
            AddWithChildren(t);
        var result = new List<RColumnMapping>();
        foreach (var t in all)
        {
            foreach (var column in t.Columns)
            {
                if (column.AttributePath is not { } path)
                    continue;
                var top = path.IndexOf('.', StringComparison.Ordinal) is var dot and >= 0 ? path[..dot] : path;
                if (ids.Contains(top))
                    result.Add(new RColumnMapping { AttributePath = path, Column = column });
            }
        }

        return result;

        void AddWithChildren(TableBuild t)
        {
            if (all.Contains(t))
                return;
            all.Add(t);
            foreach (var child in t.Children)
                AddWithChildren(child);
        }
    }

    private TableBuild? TableOf(string entityId) =>
        _placements.TryGetValue(entityId, out var p) ? p.Table : _promotedTables.FirstOrDefault(x => string.Equals(x.Entity.Id, entityId, StringComparison.Ordinal)).Table;

    private void BuildJoins()
    {
        // Join paths read finished tables and foreign keys only: computed per entity in parallel, recorded in entity order.
        // several-types-2: joins — the pairs come from RelationPhysical.TypeLinks, with conditions and type_joins (decision 48).
        var owners = _placementOrder.Select(p => p.Entity).Concat(_promotedTables.Select(x => x.Entity)).ToList();
        var paths = new List<(RNavigation Navigation, RJoinPath Path)>[owners.Count];
        _run.ForEach(owners.Count, i =>
        {
            var list = new List<(RNavigation, RJoinPath)>();
            foreach (var navigation in _run.InheritedNavigations(owners[i].Id))
            {
                if (_relationPhysical.TryGetValue(navigation.Relation.Id, out var phys) && JoinFor(navigation, phys, owners[i].Id) is { } path)
                    list.Add((navigation, path));
            }

            paths[i] = list;
        });

        for (var i = 0; i < owners.Count; i++)
        {
            var entity = owners[i];
            foreach (var (navigation, path) in paths[i])
            {
                if (ReferenceEquals(navigation.From.Entity, entity))
                    _run.SetJoin(navigation, _db.Name, path);
                if (!_joinsByEntity.TryGetValue(entity.Id, out var joins))
                {
                    joins = new SortedDictionary<string, RJoinPath>(StringComparer.Ordinal);
                    _joinsByEntity[entity.Id] = joins;
                }

                joins.TryAdd(navigation.Name, path);
            }
        }

        foreach (var (entityId, mapping) in _mappingByEntity)
        {
            mapping.Joins = _joinsByEntity.TryGetValue(entityId, out var joins)
                ? joins.ToImmutableSortedDictionary(StringComparer.Ordinal)
                : ImmutableSortedDictionary<string, RJoinPath>.Empty;
        }
    }

    /// <summary>The join path of a navigation read from an entity (the declaring entity or one that inherits the navigation).</summary>
    /// <remarks>
    /// An entity without a table of its own (TPC abstract) has no path: its rows live in several tables, and each concrete entity's
    /// <see cref="REntityMapping.Joins"/> holds its own. Likewise a path towards a TPC abstract dependent's several tables is none.
    /// </remarks>
    private RJoinPath? JoinFor(RNavigation navigation, RelationPhysical phys, string fromEntityId)
    {
        if (TableOf(fromEntityId) is null)
            return null;
        if (phys.Shape == RelationShape.ForeignKey)
        {
            var fromIsDependent = ReferenceEquals(navigation.From, phys.Dependent);
            var dependentTable = TableOf(fromIsDependent ? fromEntityId : navigation.To.OneEntity.Id);
            var spec = dependentTable is null ? null : phys.Foreign.FirstOrDefault(s => s.Resolved && ReferenceEquals(s.Host, dependentTable));
            if (spec is null)
                return null;
            var fk = spec.Result;
            var step = fromIsDependent
                ? new RJoinStep { FromTable = spec.Host.Table, FromColumns = fk.Columns, ToTable = fk.ReferencedTable, ToColumns = fk.ReferencedColumns }
                : new RJoinStep { FromTable = fk.ReferencedTable, FromColumns = fk.ReferencedColumns, ToTable = spec.Host.Table, ToColumns = fk.Columns };
            return new RJoinPath { Steps = [step] };
        }

        if (phys.Junction is not { } junction
            || !phys.EndKeys.TryGetValue(navigation.From.Id, out var from) || !from.Resolved
            || !phys.EndKeys.TryGetValue(navigation.To.Id, out var to) || !to.Resolved)
            return null;
        return new RJoinPath
        {
            Steps =
            [
                new RJoinStep
                {
                    FromTable = from.Result.ReferencedTable, FromColumns = from.Result.ReferencedColumns,
                    ToTable = junction.Table, ToColumns = from.Result.Columns, ViaJunction = true,
                },
                new RJoinStep
                {
                    FromTable = junction.Table, FromColumns = to.Result.Columns,
                    ToTable = to.Result.ReferencedTable, ToColumns = to.Result.ReferencedColumns, ViaJunction = true,
                },
            ],
        };
    }

    /// <summary>MQ4001: every identifier against the dialect's (or the database's) limit; never truncated.</summary>
    private void CheckIdentifiers()
    {
        if (_rdb.MaxIdentifierLength is not { } limit)
            return;
        var seen = new HashSet<(string, string, string)>();
        foreach (var t in _tableOrder)
        {
            Check("Table", t.Table.Name, t.SourceElementId);
            foreach (var column in t.Columns)
                Check("Column", column.Name, t.SourceElementId, Inherited(column.MemberOrigin, column.Id));
            if (t.Table.PrimaryKey is { } pk)
                Check("Primary key", pk.Name, t.SourceElementId, Inherited(pk.MemberOrigin, pk.Id));
            foreach (var fk in t.Table.ForeignKeys)
                Check("Foreign key", fk.Name, t.SourceElementId, Inherited(fk.MemberOrigin, fk.Id));
            foreach (var unique in t.Table.Uniques)
                Check("Unique constraint", unique.Name, t.SourceElementId, Inherited(unique.MemberOrigin, unique.Id));
            foreach (var index in t.Table.Indexes)
                Check("Index", index.Name, t.SourceElementId, Inherited(index.MemberOrigin, index.Id));
            foreach (var check in t.Table.Checks)
                Check("Check constraint", check.Name, t.SourceElementId, Inherited(check.MemberOrigin, check.Id));
        }

        foreach (var (sequence, sourceId, _) in _sequenceOrder)
            Check("Sequence", sequence.Name, sourceId);
        foreach (var (view, sourceId) in _views)
            Check("View", view.Name, sourceId);

        // An inherited member's finding keeps its keyed id, on its table's file, so a suppression on the table names it.
        static string? Inherited(RMemberOrigin origin, string? id) => origin.MemberId is null ? null : id;

        void Check(string what, string name, string elementId, string? memberId = null)
        {
            var length = DialectInfo.IdentifierLength(_db.Dialect, name);
            if (length <= limit || !seen.Add((what, name, elementId)))
                return;
            var message = string.Create(CultureInfo.InvariantCulture,
                $"{what} name '{name}' in database '{_db.Name}' is {length} {DialectInfo.IdentifierUnit(_db.Dialect)} long; the {_dialect} limit is {limit}.");
            if (memberId is null)
                _run.AddDiagnostic("MQ4001", message, elementId);
            else
                _run.AddMemberDiagnostic("MQ4001", message, elementId, memberId);
        }
    }

    /// <summary>An index, constraint or schema object that holds a name in a namespace MQ4068 checks.</summary>
    /// <param name="Holder">What kind of index or constraint it is; <see cref="NameHolders.None"/> for a table, view, sequence or routine.</param>
    /// <param name="What">What it is, for messages ("unique key", "table").</param>
    /// <param name="Name">Its name.</param>
    /// <param name="Table">The table it belongs to, or <see langword="null"/> for a schema object.</param>
    /// <param name="ElementId">The element the diagnostic is reported on.</param>
    /// <param name="Pointer">The pointer in that element's file, or <see langword="null"/>.</param>
    /// <param name="Schema">A schema object's schema, for messages.</param>
    private sealed record NameHolder(NameHolders Holder, string What, string Name, RTable? Table, string ElementId, string? Pointer, string? Schema = null);

    /// <summary>
    /// MQ4068 (next-phase-1-design.md section 3.5): two index or constraint names collide in a namespace the dialect keeps
    /// (<see cref="DialectInfo.NameSpaces"/>). Names compare as the database keeps them: cut to the identifier limit in the dialect's
    /// unit, then upper-cased invariantly. The schema namespaces that share object names first take every table, view and sequence
    /// (and, on SQL Server, routine) of the database, which only bar names (MQ4002 checks them against each other). Tables are then
    /// visited in published order (schema, name); within a table the primary key, uniques, foreign keys, checks, exclusions, default
    /// constraints (their <c>defaultName</c>) and indexes, in file order. The first holder keeps a name; each later one reports once,
    /// on its table file at its pointer, or on the table's source element without a pointer when no file declares it.
    /// </summary>
    private void CheckNames()
    {
        var spaces = DialectInfo.NameSpaces(_db.Dialect);
        var limit = _rdb.MaxIdentifierLength;
        var taken = new Dictionary<(int Space, string Instance, string Key), NameHolder>();
        var tables = _tableOrder
            .OrderBy(t => t.Table.Schema ?? "", StringComparer.Ordinal).ThenBy(t => t.Table.Name, StringComparer.Ordinal)
            .ThenBy(t => t.Table.Key, StringComparer.Ordinal).ToList();

        var objects = new List<(SchemaObjects Kind, string? Schema, NameHolder Holder)>();
        foreach (var t in tables)
            objects.Add((SchemaObjects.Tables, t.Table.Schema, new NameHolder(NameHolders.None, "table", t.Table.Name, null, t.SourceElementId, null, t.Table.Schema)));
        foreach (var (view, sourceId) in _views)
            objects.Add((SchemaObjects.Views, view.Schema, new NameHolder(NameHolders.None, "view", view.Name, null, sourceId, null, view.Schema)));
        foreach (var (sequence, sourceId, _) in _sequenceOrder)
            objects.Add((SchemaObjects.Sequences, sequence.Schema, new NameHolder(NameHolders.None, "sequence", sequence.Name, null, sourceId, null, sequence.Schema)));
        foreach (var (routine, file, _) in _routines)
            objects.Add((SchemaObjects.Routines, routine.Schema, new NameHolder(NameHolders.None, "routine", routine.Name, null, file.Id, null, routine.Schema)));
        for (var i = 0; i < spaces.Length; i++)
        {
            foreach (var (kind, schema, holder) in objects)
            {
                if ((spaces[i].Objects & kind) != 0)
                    taken.TryAdd((i, schema ?? "", Key(holder.Name)), holder);
            }
        }

        foreach (var t in tables)
        {
            foreach (var holder in HoldersOf(t))
            {
                NameHolder? first = null;
                var at = -1;
                for (var i = 0; i < spaces.Length; i++)
                {
                    if ((spaces[i].Holders & holder.Holder) == 0)
                        continue;
                    var instance = spaces[i].Scope == NameScope.Table ? t.Table.Key : t.Table.Schema ?? "";
                    var key = (i, spaces[i].Scope == NameScope.Table ? "t:" + instance : instance, Key(holder.Name));
                    if (taken.TryAdd(key, holder) || first is not null)
                        continue;
                    first = taken[key];
                    at = i;
                }

                if (first is not null)
                    ReportName(holder, first, spaces[at], limit);
            }
        }

        string Key(string name) => DialectInfo.CutIdentifier(_db.Dialect, name, limit).ToUpperInvariant();
    }

    private void ReportName(NameHolder holder, NameHolder first, NameSpace space, int? limit)
    {
        var cut = !string.Equals(holder.Name.ToUpperInvariant(), first.Name.ToUpperInvariant(), StringComparison.Ordinal)
            ? string.Create(CultureInfo.InvariantCulture,
                $"; it keeps only the first {limit} {DialectInfo.IdentifierUnit(_db.Dialect)} of a name, and both names start with '{DialectInfo.CutIdentifier(_db.Dialect, holder.Name, limit)}'")
            : "";
        var described = Describe(holder);
        _run.AddDiagnostic("MQ4068",
            $"{char.ToUpperInvariant(described[0])}{described[1..]} in database '{_db.Name}' has the name of {Describe(first)}: {_dialect} keeps {space.Text}{cut}; rename one of them.",
            holder.ElementId, holder.Pointer);

        static string Describe(NameHolder h) => h.Table is { } table
            ? $"{h.What} '{h.Name}' of table '{Qualified(table.Schema, table.Name)}'"
            : $"{h.What} '{Qualified(h.Schema, h.Name)}'";
    }

    /// <summary>
    /// A table's index and constraint names in the order MQ4068 visits them, each with where to report it: the table file (designed
    /// table or overlay) that declares it, at <c>/&lt;list&gt;/&lt;i&gt;/name</c> when the file names it, else at
    /// <c>/&lt;list&gt;/&lt;i&gt;</c>; a key or index no file declares (a synthesized key, an index an attribute's <c>indexed</c> flag
    /// made) is reported on the table's source element without a pointer.
    /// </summary>
    private IEnumerable<NameHolder> HoldersOf(TableBuild t)
    {
        var file = t.Source ?? t.Overlay;
        var table = t.Table;
        if (table.PrimaryKey is { } pk)
        {
            yield return file?.PrimaryKey is { } filePk
                ? new NameHolder(NameHolders.PrimaryKey, "primary key", pk.Name, table, file.Id, filePk.Name is null ? "/primaryKey" : "/primaryKey/name")
                : new NameHolder(NameHolders.PrimaryKey, "primary key", pk.Name, table, t.SourceElementId, null);
        }

        foreach (var unique in table.Uniques)
            yield return Holder(NameHolders.UniqueKey, "unique key", unique.Name, unique.Id, "uniques", file?.Uniques.Select(u => (u.Id, u.Name)));
        foreach (var fk in table.ForeignKeys)
            yield return Holder(NameHolders.ForeignKey, "foreign key", fk.Name, fk.Id, "foreignKeys", file?.ForeignKeys.Select(f => (f.Id, f.Name)));
        foreach (var check in table.Checks)
            yield return Holder(NameHolders.Check, "check constraint", check.Name, check.Id, "checks", file?.Checks.Select(c => (c.Id, c.Name)));
        foreach (var exclusion in table.Exclusions)
            yield return Holder(NameHolders.Exclusion, "exclusion constraint", exclusion.Name, exclusion.Id, "exclusions", file?.Exclusions.Select(e => (e.Id, e.Name)));
        if (_db.Dialect == Dialect.SqlServer)
        {
            foreach (var column in t.Columns)
            {
                if (column.DefaultName is not { } name)
                    continue;
                var at = file is null ? -1 : IndexOf(file.Columns.Select(c => (c.Id, c.DefaultName)), column.Id);
                yield return new NameHolder(NameHolders.DefaultConstraint, $"default constraint of column '{column.Name}'", name, table,
                    at < 0 ? t.SourceElementId : file!.Id,
                    at < 0 ? null : "/columns/" + at.ToString(CultureInfo.InvariantCulture) + "/defaultName");
            }
        }

        foreach (var index in table.Indexes)
            yield return Holder(NameHolders.Index, "index", index.Name, index.Id, "indexes", file?.Indexes.Select(i => (i.Id, i.Name)));

        NameHolder Holder(NameHolders kind, string what, string name, string? id, string list, IEnumerable<(string Id, string? Name)>? entries)
        {
            var items = entries?.ToList();
            var at = id is null || items is null ? -1 : IndexOf(items, id);
            if (at < 0)
                return new NameHolder(kind, what, name, table, t.SourceElementId, null);
            var pointer = "/" + list + "/" + at.ToString(CultureInfo.InvariantCulture);
            return new NameHolder(kind, what, name, table, file!.Id, items![at].Name is null ? pointer : pointer + "/name");
        }

        static int IndexOf(IEnumerable<(string Id, string? Name)> entries, string? id)
        {
            var i = 0;
            foreach (var (entryId, _) in entries)
            {
                if (string.Equals(entryId, id, StringComparison.Ordinal))
                    return i;
                i++;
            }

            return -1;
        }
    }

    private RDatabase Publish()
    {
        var tables = _tableOrder.Select(t => t.Table)
            .OrderBy(t => t.Schema ?? "", StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Key, StringComparer.Ordinal).ToList();
        var views = _views.Select(v => v.View)
            .OrderBy(v => v.Schema ?? "", StringComparer.Ordinal).ThenBy(v => v.Name, StringComparer.Ordinal).ThenBy(v => v.Id, StringComparer.Ordinal).ToList();
        foreach (var (sequence, _, deps) in _sequenceOrder)
            sequence.Dependencies = deps.ToList();
        var sequences = _sequenceOrder.Select(s => s.Sequence)
            .OrderBy(s => s.Schema ?? "", StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        FinishDatabaseObjects(views, sequences);
        var types = _typeOrder.Select(t => t.Type)
            .OrderBy(t => t.Schema ?? "", StringComparer.Ordinal).ThenBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal).ToList();
        var routines = _routines.Select(r => r.Routine)
            .OrderBy(r => r.Schema ?? "", StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList();
        var objects = _objects.Select(o => o.Object)
            .OrderBy(o => o.Schema ?? "", StringComparer.Ordinal).ThenBy(o => o.Name, StringComparer.Ordinal).ThenBy(o => o.Id, StringComparer.Ordinal).ToList();
        ResolveQueries(views);
        ResolveBindings(views);
        var queries = _queries.Select(q => q.Query).OrderBy(q => q.Name, StringComparer.Ordinal).ThenBy(q => q.Id, StringComparer.Ordinal).ToList();

        var membership = new DependencySet(_run.Keys)
            .Add("k:entity").Add("k:relation").Add("k:enum").Add("k:table").Add("k:mapping").Add("k:value-object").Add("k:stereotype")
            .Add("k:sequence").Add("k:view").Add("k:package").Add(Conventions).Add(InflectionKey).Add(TypeMaps)
            .Element(_db.Id).Referrers(_db.Id)
            .AddRange(_run.ConceptualElementKeys);
        if (LedgerContract.Supports(_db.Dialect))
        {
            // The ledger's schema reaches the scripts that write ledger rows (next-phase-1-design.md section 3.3).
            _rdb.LedgerSchema = _run.Settings.Migrations.LedgerSchema;
            membership.Add(MigrationsKey);
        }

        foreach (var t in _tableOrder)
            membership.AddRange(t.Deps.ToList());
        foreach (var (_, _, deps) in _sequenceOrder)
            membership.AddRange(deps.ToList());
        foreach (var (view, _) in _views)
            membership.AddRange(view.Dependencies);
        membership.Add("k:routine").Add("k:database-type").Add("k:sql-object");
        foreach (var type in types)
            membership.AddRange(type.Dependencies);
        foreach (var routine in routines)
            membership.AddRange(routine.Dependencies);
        foreach (var obj in objects)
            membership.AddRange(obj.Dependencies);
        membership.Add("k:query");
        foreach (var query in queries)
            membership.AddRange(query.Dependencies);

        var keys = membership.ToList();

        var schemaNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var schema in _db.Schemas)
            schemaNames.Add(schema.Name);
        foreach (var name in tables.Select(t => t.Schema).Concat(views.Select(v => v.Schema)).Concat(sequences.Select(s => s.Schema))
            .Concat(types.Select(t => t.Schema)).Concat(routines.Select(r => r.Schema)).Concat(objects.Select(o => o.Schema)))
        {
            if (name is not null)
                schemaNames.Add(name);
        }

        CheckLedgerSchema(schemaNames, tables, views, sequences, types, routines, objects);

        var schemas = new List<RSchema>();
        foreach (var name in schemaNames)
        {
            var declared = _db.Schemas.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            var schemaDeps = new DependencySet(_run.Keys).Element(_db.Id).Referrers(_db.Id);
            var schema = new RSchema
            {
                Id = declared?.Id ?? _db.Id + "/" + name,
                Name = name,
                IsDefault = string.Equals(name, _rdb.DefaultSchema, StringComparison.Ordinal),
                IsDeclared = declared is not null,
                Tables = new RList<RTable>(tables.Where(t => string.Equals(t.Schema, name, StringComparison.Ordinal)), keys),
                Views = new RList<RView>(views.Where(v => string.Equals(v.Schema, name, StringComparison.Ordinal)), keys),
                Sequences = new RList<RSequence>(sequences.Where(s => string.Equals(s.Schema, name, StringComparison.Ordinal)), keys),
                Routines = new RList<RRoutine>(routines.Where(r => string.Equals(r.Schema, name, StringComparison.Ordinal)), keys),
                Types = new RList<RDatabaseType>(types.Where(t => string.Equals(t.Schema, name, StringComparison.Ordinal)), keys),
                Objects = new RList<RSqlObject>(objects.Where(o => string.Equals(o.Schema, name, StringComparison.Ordinal)), keys),
            };
            _run.FillPhysicalAnnotations(schema, declared, null, schemaDeps);
            schema.Dependencies = schemaDeps.ToList();
            schemas.Add(schema);
            _run.Register(schema);
        }

        _rdb.Schemas = new RList<RSchema>(schemas, keys);
        _rdb.Tables = new RList<RTable>(tables, keys);
        _rdb.Views = new RList<RView>(views, keys);
        _rdb.Sequences = new RList<RSequence>(sequences, keys);
        _rdb.Routines = new RList<RRoutine>(routines, keys);
        _rdb.Types = new RList<RDatabaseType>(types, keys);
        _rdb.Objects = new RList<RSqlObject>(objects, keys);
        _rdb.Queries = new RList<RQuery>(queries, keys);
        _run.Register(_rdb);
        return _rdb;
    }

    /// <summary>
    /// MQ4067 (next-phase-1-design.md section 3.3): the ledger's schema is also a schema of this database's modeled objects, declared
    /// (reported at <c>/schemas/&lt;i&gt;/name</c> of the database file) or named by an object (reported on the database file, naming
    /// the first object in published order that uses it; an object without a schema is in the default schema). Unquoted names fold
    /// to lower case on PostgreSQL, so the names compare without case unless the database quotes every identifier.
    /// </summary>
    private void CheckLedgerSchema(SortedSet<string> schemaNames, List<RTable> tables, List<RView> views, List<RSequence> sequences,
        List<RDatabaseType> types, List<RRoutine> routines, List<RSqlObject> objects)
    {
        if (_rdb.LedgerSchema is not { } ledger)
            return;
        var comparison = _db.Quoting == Quoting.Always ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        bool Same(string? schema) => (schema ?? _rdb.DefaultSchema) is { } name && string.Equals(name, ledger, comparison);

        for (var i = 0; i < _db.Schemas.Count; i++)
        {
            if (!Same(_db.Schemas[i].Name))
                continue;
            _run.AddDiagnostic("MQ4067",
                $"Schema '{_db.Schemas[i].Name}' of database '{_db.Name}' is also the migration ledger's schema (migrations.ledgerSchema '{ledger}'): the bookkeeping tables would share it with modeled objects; choose another ledger schema.",
                _db.Id, "/schemas/" + i.ToString(CultureInfo.InvariantCulture) + "/name");
            return;
        }

        if (!schemaNames.Any(n => Same(n)) && !(_rdb.DefaultSchema is { } fallback && Same(fallback)))
            return;
        var user = tables.Where(t => Same(t.Schema)).Select(t => ("table", t.Schema, t.Name))
            .Concat(views.Where(v => Same(v.Schema)).Select(v => ("view", v.Schema, v.Name)))
            .Concat(sequences.Where(q => Same(q.Schema)).Select(q => ("sequence", q.Schema, q.Name)))
            .Concat(types.Where(t => Same(t.Schema)).Select(t => ("database type", t.Schema, t.Name)))
            .Concat(routines.Where(r => Same(r.Schema)).Select(r => ("routine", r.Schema, r.Name)))
            .Concat(objects.Where(o => Same(o.Schema)).Select(o => ("SQL object", o.Schema, o.Name)))
            .Select(x => ((string What, string? Schema, string Name)?)x)
            .FirstOrDefault();
        if (user is not { } first)
            return;
        var schema = first.Schema ?? _rdb.DefaultSchema;
        _run.AddDiagnostic("MQ4067",
            $"Schema '{schema}' of database '{_db.Name}', which {first.What} '{schema}.{first.Name}' uses, is also the migration ledger's schema (migrations.ledgerSchema '{ledger}'): the bookkeeping tables would share it with modeled objects; choose another ledger schema.",
            _db.Id);
    }
}
