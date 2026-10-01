using System.Collections.Immutable;
using System.Globalization;
using Maquettiste.Engine.Model;

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
            AddOverlayExtraColumns(t);
            for (var i = 0; i < t.Columns.Count; i++)
                t.Columns[i].Position = i;
            var pk = t.PrimaryKey.Select(t.Resolve).OfType<RColumn>().Distinct().ToList();
            if (pk.Count == 0)
                continue;
            foreach (var column in pk)
            {
                column.IsPrimaryKey = true;
                column.Nullable = false;
            }

            t.Table.PrimaryKey = new RPrimaryKey { Name = t.PrimaryKeyName ?? Render(_conv.PrimaryKeyName, ("table", t.Table.Name)), Columns = pk,
                Clustered = t.PrimaryKeyClustered };
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
                })];
            var indexes = t.Indexes.Select(i => (Spec: i, Columns: i.Columns.Select(c => (Column: t.Resolve(c.Column), c.Descending)).ToList(),
                    Include: i.Include.Select(t.Resolve).ToList()))
                .Where(x => x.Columns.Count > 0 && x.Columns.All(c => c.Column is not null) && x.Include.All(c => c is not null))
                .ToList();
            // An index a table file declares (overlay or designed table) overrides the index an attribute's `indexed` flag synthesizes on the
            // same columns, so a file can change its sort order, method or name without producing a second index of the same name.
            var fileColumnSets = indexes.Where(x => x.Spec.FromFile).Select(x => Joined(x.Columns.Select(c => c.Column!))).ToHashSet(StringComparer.Ordinal);
            indexes.RemoveAll(x => !x.Spec.FromFile && fileColumnSets.Contains(Joined(x.Columns.Select(c => c.Column!))));
            t.Table.Indexes = [.. indexes
                .Select(x => new RIndex
                {
                    Id = x.Spec.Id,
                    Name = x.Spec.Name ?? Render(_conv.IndexName, ("table", t.Table.Name), ("columns", Joined(x.Columns.Select(c => c.Column!)))),
                    Columns = [.. x.Columns.Select(c => new RIndexColumn { Column = c.Column!, Descending = c.Descending })],
                    Include = [.. x.Include!],
                    Where = x.Spec.Where,
                    Unique = x.Spec.Unique,
                    Method = x.Spec.Method,
                })];
            t.Table.Checks = [.. t.Checks.Select(c => new RCheck
            {
                Id = c.Id,
                Name = c.Name ?? Render(_conv.CheckName, ("table", t.Table.Name), ("name", c.Ordinal.ToString(CultureInfo.InvariantCulture))),
                Expression = c.Expression,
            })];
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

    /// <summary>MQ4008: a foreign key that cannot be resolved is left out of its table, and said so.</summary>
    private void ReportUnresolved(TableBuild t, ForeignKeySpec spec, string reason)
    {
        var relation = spec.Result.Relation;
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
        foreach (var phys in _relationPhysical.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Value))
        {
            if (phys.Shape != RelationShape.ForeignKey || phys.Mapping is not { } mapping)
                continue;
            var dependentTable = phys.Dependent is { } dependent ? TableOf(dependent.Entity.Id) : null;
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
                if (phys.Principal is { } principal && _placements.TryGetValue(principal.Entity.Id, out var pp))
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
            var reason = target.StartsWith("entity:", StringComparison.Ordinal) && _placements.TryGetValue(target["entity:".Length..], out var p)
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
            if (p.Table is not null)
                columns[i] = ColumnMappings(TablesOf(p), p.Entity.Attributes);
        });

        for (var i = 0; i < _placementOrder.Count; i++)
        {
            var p = _placementOrder[i];
            if (p.Table is null)
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
            var dependentTable = TableOf(fromIsDependent ? fromEntityId : navigation.To.Entity.Id);
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
                Check("Column", column.Name, t.SourceElementId);
            if (t.Table.PrimaryKey is { } pk)
                Check("Primary key", pk.Name, t.SourceElementId);
            foreach (var fk in t.Table.ForeignKeys)
                Check("Foreign key", fk.Name, t.SourceElementId);
            foreach (var unique in t.Table.Uniques)
                Check("Unique constraint", unique.Name, t.SourceElementId);
            foreach (var index in t.Table.Indexes)
                Check("Index", index.Name, t.SourceElementId);
            foreach (var check in t.Table.Checks)
                Check("Check constraint", check.Name, t.SourceElementId);
        }

        foreach (var (sequence, sourceId, _) in _sequenceOrder)
            Check("Sequence", sequence.Name, sourceId);
        foreach (var (view, sourceId) in _views)
            Check("View", view.Name, sourceId);

        void Check(string what, string name, string elementId)
        {
            if (name.Length <= limit || !seen.Add((what, name, elementId)))
                return;
            _run.AddDiagnostic("MQ4001",
                string.Create(CultureInfo.InvariantCulture,
                    $"{what} name '{name}' in database '{_db.Name}' is {name.Length} characters long; the {_dialect} limit is {limit}."),
                elementId);
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

        var membership = new DependencySet(_run.Keys)
            .Add("k:entity").Add("k:relation").Add("k:enum").Add("k:table").Add("k:mapping").Add("k:value-object").Add("k:stereotype")
            .Add("k:sequence").Add("k:view").Add("k:package").Add(Conventions).Add(InflectionKey).Add(TypeMaps)
            .Element(_db.Id).Referrers(_db.Id)
            .AddRange(_run.ConceptualElementKeys);
        foreach (var t in _tableOrder)
            membership.AddRange(t.Deps.ToList());
        foreach (var (_, _, deps) in _sequenceOrder)
            membership.AddRange(deps.ToList());
        foreach (var (view, _) in _views)
            membership.AddRange(view.Dependencies);
        var keys = membership.ToList();

        var schemaNames = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var schema in _db.Schemas)
            schemaNames.Add(schema.Name);
        foreach (var name in tables.Select(t => t.Schema).Concat(views.Select(v => v.Schema)).Concat(sequences.Select(s => s.Schema)))
        {
            if (name is not null)
                schemaNames.Add(name);
        }

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
        _run.Register(_rdb);
        return _rdb;
    }
}
