using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The physical resolution of one database (engine-design.md sections 7.3 to 7.6): its designed and imported tables, views, sequences and
/// other objects, constraints named, the bindings of the entities bound to it and the relations its relation mappings store, with their
/// join paths. Since 0.29.0 the engine lays out no table from an entity (retire-projection-design.md).
/// </summary>
internal sealed partial class DatabaseRun
{
    private const string Conventions = "s:conventions";
    private const string TypeMaps = "s:typeMaps";
    private const string InflectionKey = "s:inflection";
    private const string MigrationsKey = "s:migrations";

    private readonly ResolveRun _run;
    private readonly Database _db;
    private readonly EffectiveConventions _conv;
    private readonly IReadOnlyDictionary<string, string> _typeMap;
    private readonly string _dialect;
    private readonly RDatabase _rdb;
    private readonly Dictionary<string, TableBuild> _tables = new(StringComparer.Ordinal);
    private readonly List<TableBuild> _tableOrder = [];
    private readonly Dictionary<string, Placement> _placements = new(StringComparer.Ordinal);
    private readonly List<Placement> _placementOrder = [];
    private readonly Dictionary<string, RSequence> _sequences = new(StringComparer.Ordinal);
    private readonly List<(RSequence Sequence, string SourceId, DependencySet Deps)> _sequenceOrder = [];
    private readonly List<(RView View, string SourceId)> _views = [];
    private readonly Dictionary<string, string> _schemaNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ForeignKeySpec> _foreignKeysById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelationPhysical> _relationPhysical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string? Token, IReadOnlyList<string>? Literal)>> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rendered = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _renderKey = new();
    private readonly List<string> _renderWords = new(16);
    private readonly Dictionary<(string, int?, int?, int?, string?), string> _nativeTypes = [];

    public DatabaseRun(ResolveRun run, Database db)
    {
        _run = run;
        _db = db;
        _conv = EffectiveConventions.For(run.Settings, db.Name);
        _typeMap = DialectTypeMaps.Effective(db.Dialect, run.Settings);
        _dialect = DialectTypeMaps.Name(db.Dialect);
        _rdb = new RDatabase
        {
            Id = db.Id,
            Name = db.Name,
            Dialect = _dialect,
            Version = db.Version,
            DefaultSchema = db.DefaultSchema ?? db.Dialect switch
            {
                Dialect.PostgreSql => "public",
                Dialect.SqlServer => "dbo",
                _ => null,
            },
            Quoting = ResolutionValues.Kebab(db.Quoting),
            MaxIdentifierLength = db.MaxIdentifierLength ?? db.Dialect switch
            {
                Dialect.PostgreSql => 63,
                Dialect.SqlServer => 128,
                Dialect.MySql => 64,
                Dialect.Oracle => 128,
                _ => null,
            },
        };
        // The database's own values come from its file. What refers to it (its tables, views, queries, and every binding to it) reaches
        // its lists, which carry their own keys, the referrers among them: a unit that reads only the database's name or dialect does
        // not render again when a bound entity changes.
        var deps = new DependencySet(run.Keys).Element(db.Id);
        run.FillPhysicalAnnotations(_rdb, db, deps);
        _rdb.Dependencies = deps.ToList();
    }

    /// <summary>When runs go side by side: finished once the run before this one has added its relation mappings' keys.</summary>
    internal Task? RelationsAfter { get; set; }

    /// <summary>When runs go side by side: set once this run has added its relation mappings' keys.</summary>
    internal TaskCompletionSource? RelationsDone { get; set; }

    /// <summary>Resolves the database.</summary>
    public RDatabase Run()
    {
        foreach (var schema in _db.Schemas)
            _schemaNames.TryAdd(schema.Id, schema.Name);
        CollectBindings();
        CollectPhysicalFiles();
        ComputePlacements();
        // Beside the other databases' runs: the relations copy the keys earlier runs' relation mappings add, so wait for those.
        RelationsAfter?.Wait(_run.Ct);
        foreach (var relation in _run.RelationOrder)
        {
            _run.Ct.ThrowIfCancellationRequested();
            ResolveRelation(relation);
        }

        FinishTables();
        CheckForeignKeyColumns();
        CheckOnDeleteSets();
        FinishRelationMappings();
        RelationsDone?.TrySetResult();
        BuildJoins();
        // Names are judged once the bindings, which Publish resolves, have added the entity checks they list to their write tables.
        var database = Publish();
        CheckIdentifiers();
        CheckNames();
        return database;
    }

    // ---- designed and imported tables, views, sequences -----------------------------------------------------------------------

    private void CollectPhysicalFiles()
    {
        CollectDatabaseObjects();
        foreach (var sequence in _run.Model.All<Sequence>().Where(s => s.Database == _db.Id).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var r = new RSequence
            {
                Id = sequence.Id,
                Name = sequence.Name,
                Schema = SchemaName(sequence.Schema),
                Database = _rdb,
                Type = sequence.Type,
                NativeType = DialectTypeMaps.Render(_typeMap, sequence.Type, null, null, null, _conv),
                Start = sequence.Start,
                Increment = sequence.Increment,
                Min = sequence.Min,
                Max = sequence.Max,
                Cycle = sequence.Cycle,
                Cache = sequence.Cache,
            };
            var deps = new DependencySet(_run.Keys).Element(sequence.Id).Referrers(sequence.Id).Element(_db.Id).Add(TypeMaps);
            _run.FillPhysicalAnnotations(r, sequence, deps);
            AddSequence(r, sequence.Id, deps);
        }

        foreach (var view in _run.Model.All<View>().Where(v => v.Database == _db.Id))
        {
            var r = new RView
            {
                Id = view.Id,
                Name = view.Name,
                Schema = SchemaName(view.Schema),
                Database = _rdb,
                Body = ForDialect(view.Body),
                Columns = [.. view.Columns.Select(c => new RViewColumn
                {
                    Name = c.Name,
                    Type = c.Type,
                    NativeType = c.Type is null ? null : DialectTypeMaps.Render(_typeMap, c.Type, null, null, null, _conv),
                    Nullable = c.Nullable,
                })],
                ColumnList = view.ColumnList,
                WithCheckOption = view.WithCheckOption,
                Materialized = view.Materialized,
                SecurityInvoker = view.SecurityInvoker,
                SecurityBarrier = view.SecurityBarrier,
                Comment = view.Comment,
            };
            var deps = new DependencySet(_run.Keys).Element(view.Id).Element(_db.Id).Referrers(view.Id).Add(TypeMaps);
            AddBinderKeys(deps, view.Id);
            _run.FillPhysicalAnnotations(r, view, deps);
            r.Dependencies = deps.ToList();
            _views.Add((r, view.Id));
            _viewFiles.Add((r, view, deps));
            _run.Register(r);
        }

        foreach (var table in _run.Model.All<Table>().Where(t => t.Database == _db.Id).OrderBy(t => t.Id, StringComparer.Ordinal))
            BuildDesignedTable(table);
    }

    private void BuildDesignedTable(Table table)
    {
        var r = new RTable
        {
            Id = table.Id,
            Key = table.Id,
            Name = table.Name,
            Schema = SchemaName(table.Schema),
            Database = _rdb,
            Comment = table.Comment,
        };
        var t = new TableBuild(_run.Keys, r, table);
        t.Deps.Element(table.Id).Referrers(table.Id).Element(_db.Id).Add(TypeMaps).Add(Conventions);
        AddTable(t);
        foreach (var column in table.Columns)
        {
            var c = new RColumn
            {
                Id = column.Id,
                Key = column.Id,
                Name = column.Name,
                Table = r,
                Type = column.Type ?? "string",
                Subtype = BuiltinTypes.IsRange(column.Type) ? column.Subtype : null,
                Length = column.Length,
                Precision = column.Precision,
                Scale = column.Scale,
                Nullable = column.Nullable ?? true,
            };
            ApplyColumnFile(t, c, column);
            TypeColumn(t, c);
            ApplyFacetDefaults(c);
            ApplyNativeType(t, c, column.NativeType);
            t.Columns.Add(c);
            t.ByKey.TryAdd(column.Id, c);
            _run.Register(c);
        }

        AddConstraintFiles(t, table);
    }

    /// <summary>Adds the constraints and indexes of a table file as pending specs.</summary>
    private void AddConstraintFiles(TableBuild t, Table table)
    {
        if (table.PrimaryKey is { } pk)
        {
            if (pk.Columns.Count > 0)
            {
                t.PrimaryKey.Clear();
                t.PrimaryKey.AddRange(pk.Columns);
            }

            if (pk.Name is not null)
                t.PrimaryKeyName = pk.Name;
            if (pk.Clustered is not null)
                t.PrimaryKeyClustered = pk.Clustered;
            t.PrimaryKeyWithoutOverlaps = pk.WithoutOverlaps;
            t.PrimaryKeyId = pk.Id;
        }

        foreach (var unique in table.Uniques)
            t.Uniques.Add(new UniqueSpec(unique.Columns, unique.Name, null, unique.Id, unique.NullsNotDistinct, unique.WithoutOverlaps));
        foreach (var fk in table.ForeignKeys)
        {
            var spec = new ForeignKeySpec(t, fk.Columns, null, fk.ReferencesTable, fk.ReferencesColumns, ResolutionValues.Kebab(fk.OnDelete),
                ResolutionValues.Kebab(fk.OnUpdate), fk.Name) { FileId = table.Id, Id = fk.Id, OnDeleteColumns = fk.OnDeleteColumns };
            spec.Result.Deferrable = ResolutionValues.Kebab(fk.Deferrable);
            spec.Result.Period = fk.Period;
            t.ForeignKeys.Add(spec);
            _foreignKeysById.TryAdd(fk.Id, spec);
        }

        var ordinal = t.Checks.Count;
        for (var i = 0; i < table.Checks.Count; i++)
        {
            var check = table.Checks[i];
            if (check.Expression.Count == 0 && check.Condition is not null)
            {
                // A check in the expression language is typed now and lowered once the columns are final; on another dialect it is left out.
                if (TypeCheck(t, table, check.Id, check.Name, null, table.Id, "/checks/" + i.ToString(CultureInfo.InvariantCulture) + "/condition") is { } tree)
                    t.Checks.Add(new CheckSpec("", check.Name, ++ordinal, check.Id, check.Column) { Tree = tree, Properties = check.Properties });
                continue;
            }

            var expression = ForDialect(check.Expression);
            if (expression is not null)
                t.Checks.Add(new CheckSpec(expression, check.Name, ++ordinal, check.Id, check.Column) { Properties = check.Properties });
        }

        t.Exclusions.AddRange(table.Exclusions);
        if (table.PartitionBy is not null)
            t.PartitionBy = table.PartitionBy;
        if (table.Partitions.Count > 0)
            t.Table.Partitions = [.. table.Partitions.Select(p => new RPartition { Id = p.Id, Name = p.Name, Bounds = p.Default ? null : p.Bounds, IsDefault = p.Default })];
        foreach (var index in table.Indexes)
        {
            // An expression without a text for the dialect leaves the index out, as a check without one is left out.
            var columns = index.Columns.Select(c => c.Column is null
                ? new IndexColumnSpec(null, c.Descending, c.Expression is null ? null : ForDialect(c.Expression), c.Length, c.OperatorClass, NullsOrder(c))
                : new IndexColumnSpec(c.Column, c.Descending, null, c.Length, c.OperatorClass, NullsOrder(c))).ToList();
            if (columns.Any(c => c.Column is null && c.Expression is null))
                continue;
            t.Indexes.Add(new IndexSpec(columns, index.Include, index.Where, index.Unique,
                ResolutionValues.Kebab(index.Method), index.Name, FromFile: true, Id: index.Id, Storage: Storage([index.Storage])));
        }
    }

    /// <summary>
    /// Where an index column's nulls sort as the DDL writes it: <c>first</c> or <c>last</c> on PostgreSQL, and only when it differs
    /// from the direction's default (last ascending, first descending), so the DDL, the snapshot and the index key change only
    /// when the order does. Other dialects have no such clause (validation's MQ4056 says so).
    /// </summary>
    private string? NullsOrder(IndexColumn column) =>
        _db.Dialect != Dialect.PostgreSql || column.Nulls is not { } nulls ? null
        : nulls == IndexNulls.First && !column.Descending ? "first"
        : nulls == IndexNulls.Last && column.Descending ? "last"
        : null;

    /// <summary>
    /// Applies the physical properties and the annotations a column file sets to a
    /// column; the annotations' stereotype and category keys join the table's dependencies.
    /// </summary>
    private void ApplyColumnFile(TableBuild t, RColumn c, Column column)
    {
        _run.FillPhysicalAnnotations(c, column, null, t.Deps);
        if (column.Default is { } value)
            c.Default = ResolutionValues.Plain(value);
        if (ForDialect(column.DefaultSql) is { } sql)
            c.DefaultSql = sql;
        switch (column.Generated)
        {
            case ColumnGeneration.Identity:
                c.Identity = true;
                c.Sequence = null;
                c.IdentitySeed = column.Identity?.Seed;
                c.IdentityIncrement = column.Identity?.Increment;
                c.IdentityAlways = column.Identity?.Always ?? false;
                break;
            case ColumnGeneration.Sequence:
                c.Identity = false;
                if (column.Sequence is { } sequenceId)
                    c.Sequence = _sequences.GetValueOrDefault(sequenceId);

                break;
        }

        if (column.Computed is not null)
        {
            c.Computed = column.Computed;
            c.ComputedStored = column.ComputedStored;
        }

        c.StableId |= column.StableId; // next-phase-2-design.md section 3.4
        c.Collation = column.Collation ?? c.Collation;
        c.Comment = column.Comment ?? c.Comment;
        if (column.Sensitive is { } sensitive)
            c.Sensitive = c.OwnSensitive = ResolutionValues.Kebab(sensitive);
        c.Unicode = column.Unicode ?? c.Unicode;
        c.FixedLength |= column.FixedLength;
        c.DefaultName = column.DefaultName ?? c.DefaultName;
    }

    /// <summary>
    /// Gives a column of a designed or imported table, own or inherited, its bound type (0.28.0): the enum, reference type or scalar type
    /// of the attribute the entities' bindings map to it, when they all agree (<see cref="BoundColumnTypes"/>). Sets
    /// <see cref="RColumn.BoundType"/> only, as information: a table carries its own definition and binding an entity never changes
    /// storage, so the bound type sets no pack key or CHECK, no captured reference state and no value map, and fills no facet and no
    /// native type. The table depends on the bound entities, the elements that declare the mapped
    /// attributes, and the type.
    /// </summary>
    private void TypeColumn(TableBuild t, RColumn c)
    {
        if (t.Source is not { } file)
            return;
        var bound = BoundColumnTypes.Of(_run.Model);
        if (bound.TypeOf(file.Id, c.Id) is not { } typeId)
            return;
        foreach (var use in bound.UsesOf(file.Id, c.Id))
            t.Deps.Element(use.EntityId).Element(use.OwnerId);
        var type = _run.TypeOf(new TypeRef { Ref = typeId }, t.Deps, out _);
        switch (type)
        {
            case { Enum: { } e }:
                t.Deps.AddRange(_run.DepsOf(e).ToList());
                c.BoundType = type;
                break;
            case { ReferenceType: not null }:
                c.BoundType = type;
                break;
            case { Scalar: { } scalar }:
                t.Deps.AddRange(_run.DepsOf(scalar).ToList());
                c.BoundType = type;
                break;
        }
    }

    /// <summary>The logical type that decides a column's native type.</summary>
    private static string PhysicalType(RColumn c) => c.Type;

    /// <summary>
    /// Fills a column's missing facets with the conventions' defaults; a range or multirange column takes no facets, so it gets none
    /// (no length 255).
    /// </summary>
    private void ApplyFacetDefaults(RColumn c)
    {
        switch (PhysicalType(c))
        {
            case "string":
                c.Length ??= _conv.DefaultStringLength;
                break;
            case "decimal":
                c.Precision ??= _conv.DecimalPrecision;
                c.Scale ??= _conv.DecimalScale;
                break;
            case "time":
            case "datetime":
            case "datetimeoffset":
                c.Precision ??= _conv.DatetimePrecision;
                break;
        }
    }

    /// <summary>
    /// The storage parameters for the database's dialect from storage maps in increasing precedence (a table's stereotypes', then its
    /// own), by name in ordinal order, each value as the dialect writes it.
    /// </summary>
    private IReadOnlyList<RStorageParameter> Storage(IEnumerable<IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonElement>>> layers)
    {
        var merged = new SortedDictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var layer in layers)
        {
            if (layer.TryGetValue(_dialect, out var parameters))
            {
                foreach (var (name, value) in parameters)
                    merged[name] = value;
            }
        }

        return merged.Count == 0 ? [] : [.. merged.Select(p => new RStorageParameter { Name = p.Key, Value = ResolutionValues.StorageValue(p.Value, _dialect) })];
    }

    private string? ForDialect(IReadOnlyDictionary<string, string> values) =>
        values.TryGetValue(_dialect, out var value) ? value : values.TryGetValue("*", out var any) ? any : null;

    // ---- checks in the expression language (next-phase-3-design.md section 3.1, "Where lowering runs") ----------------------------

    /// <summary>
    /// Types a table's or stereotype's check written in the expression language, in the scope of the file that holds it, and
    /// adds what its condition names (an enum, a seed, a constraint) to the table's dependency keys. <see langword="null"/> when it does
    /// not type (validation reports why) or the database is not PostgreSQL (MQ4056: the DDL leaves it out).
    /// </summary>
    /// <param name="t">The table.</param>
    /// <param name="holder">The table file or stereotype that holds the check.</param>
    /// <param name="checkId">The check's id in the holder.</param>
    /// <param name="name">The check's name, or <see langword="null"/>.</param>
    /// <param name="keyPrefix">For a stereotype's check, the table key its columns are keyed under.</param>
    /// <param name="elementId">The element a finding goes on.</param>
    /// <param name="pointer">The finding's pointer there.</param>
    /// <returns>The typed check.</returns>
    private TreeCheck? TypeCheck(TableBuild t, Element holder, string checkId, string? name, string? keyPrefix, string elementId, string? pointer)
    {
        if (ExprResolution.Type(_run.Model, holder, checkId) is not { } typed)
            return null;
        if (_db.Dialect != Dialect.PostgreSql)
        {
            var problems = new List<Diagnostic>();
            ExprLowering.ToPostgreSql(typed.Typed, new ExprColumns { Dialect = _dialect }, ExprSiteKind.Check, problems);
            ReportLowering(problems, $"Check '{name ?? checkId}' of table '{t.Table.Name}' in database '{_db.Name}'", elementId, pointer);
            return null;
        }

        ExprResolution.AddNames(_run.Model, typed.Typed, t.Deps);
        return new TreeCheck(new ExprRoot(typed.Tree, typed.Typed, typed.Scope, ExprSiteKind.Check, ExprColumns.None), keyPrefix, elementId, pointer);
    }

    /// <summary>
    /// A table's resolved check: a SQL text check as its spec says; a check in the expression language lowered to PostgreSQL through the
    /// table's final columns (a stereotype's check reads its columns under their keyed ids), with its condition and shape.
    /// <see langword="null"/> leaves a tree check out when it reads a column the table does not have (validation says so).
    /// </summary>
    private RCheck? ResolveCheck(TableBuild t, CheckSpec c)
    {
        var check = new RCheck
        {
            Id = c.Id,
            Name = c.Name ?? Render(_conv.CheckName, ("table", t.Table.Name), ("name", c.Ordinal.ToString(CultureInfo.InvariantCulture))),
            Expression = c.Expression,
            Column = c.Column is null ? null : t.Resolve(c.Column),
            MemberOrigin = c.Origin ?? RMemberOrigin.Own,
        };
        if (c.Properties.Count > 0)
            check.Properties = ResolutionValues.PlainMap(c.Properties);
        if (c.Tree is not { } tree)
            return check;
        var columns = new Dictionary<string, RColumn>(StringComparer.Ordinal);
        foreach (var id in ExprResolution.Paths(tree.Source.Typed))
        {
            if (((tree.KeyPrefix is { } prefix ? t.Resolve(ContributedMembers.KeyOf(prefix, id)) : null) ?? t.Resolve(id)) is not { } column)
                return null;
            columns[id] = column;
        }

        if (LowerCheck(tree.Source, columns, $"Check '{check.Name}' of table '{t.Table.Name}' in database '{_db.Name}'", tree.ElementId, tree.Pointer) is not { } lowered)
            return null;
        check.Expression = lowered.Sql;
        check.Condition = lowered.Condition;
        check.Shape = lowered.Shape;
        return check;
    }

    /// <summary>
    /// Lowers a check to the columns its paths read, reporting what the lowering finds: MQ4056 on another dialect, and a code its column
    /// cannot store under <paramref name="misfit"/> (MQ3103 at a table, MQ3110 for a binding's check, as validation reports it).
    /// </summary>
    private LoweredCheck? LowerCheck(ExprRoot source, IReadOnlyDictionary<string, RColumn> columns, string what, string elementId, string? pointer,
        string misfit = "MQ3103")
    {
        var problems = new List<Diagnostic>();
        var lowered = ExprResolution.LowerCheck(_run.Model, _rdb, _db.Quoting, source, columns, elementId, _run.Model.GetDocument(elementId)?.Path,
            _run.EnumMemberOf, problems);
        ReportLowering(problems, what, elementId, pointer, misfit);
        return lowered;
    }

    /// <summary>Reports what a lowering found, on the check's element at its pointer, naming the check; MQ3103 goes under <paramref name="misfit"/>.</summary>
    private void ReportLowering(List<Diagnostic> problems, string what, string elementId, string? pointer, string misfit = "MQ3103")
    {
        foreach (var problem in problems)
            _run.AddDiagnostic(problem.Rule == "MQ3103" ? misfit : problem.Rule, what + ": " + problem.Message, elementId, pointer);
    }

    private string? SchemaName(string? schemaId) =>
        schemaId is not null && _schemaNames.TryGetValue(schemaId, out var name) ? name : _rdb.DefaultSchema;

    /// <summary>
    /// Registers a table: its holder and effective stereotype set, the annotations and storage its file and stereotypes give, and the
    /// keys of the entities bound to it.
    /// </summary>
    /// <param name="t">The table.</param>
    private void AddTable(TableBuild t)
    {
        var file = t.Source;
        t.Holder = _run.Model.HolderOf(file);
        t.Effective = _run.Model.EffectiveStereotypes(t.Holder);
        _run.FillPhysicalAnnotations(t.Table, file, t.Deps);
        t.Table.Storage = Storage([.. t.Effective.Select(s => s.Stereotype.Storage), file.Storage]);
        AddBinderKeys(t.Deps, t.Table.Key);
        AddBinderKeys(t.Deps, file.Id);
        var entries = new DependencySet(_run.Keys).Element(file.Id).Element(_db.Id);
        foreach (var applied in t.Effective)
            entries.Element(applied.Stereotype.Id);
        _run.AddPackageChain(entries, t.Holder.Package);
        t.Table.EntryKeys = entries.ToList();
        _tables.TryAdd(t.Table.Key, t);
        _tableOrder.Add(t);
        _run.Register(t.Table);
    }

    private void AddSequence(RSequence sequence, string sourceId, DependencySet deps)
    {
        if (!_sequences.TryAdd(sequence.Id, sequence))
            return;
        _sequenceOrder.Add((sequence, sourceId, deps));
        _run.Register(sequence);
    }

    // ---- placements ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Places each entity bound to the database on its binding's write (else source) table, for the foreign keys of relations; an entity
    /// without a binding here has no rows in the database.
    /// </summary>
    private void ComputePlacements()
    {
        foreach (var entity in _run.EntityOrder)
        {
            if (!_bindingByEntity.ContainsKey(entity.Id) || BindingHost(entity.Id) is not { } host)
                continue;
            var bound = new Placement(entity, _run.EntitySource(entity.Id)!) { Table = host };
            _placements[entity.Id] = bound;
            _placementOrder.Add(bound);
        }

        foreach (var p in _placementOrder)
        {
            // The in-scope base: the nearest ancestor that is placed here. A link that would close an inheritance cycle (MQ3002, so
            // only in a speculative resolution of an invalid snapshot) is left out, so the base links stay a forest and every walk
            // over them (roots, descendants, hosts) ends.
            foreach (var ancestor in _run.Chain(p.Source).Skip(1))
            {
                if (_placements.TryGetValue(ancestor.Id, out var basePlacement))
                {
                    if (!Reaches(basePlacement, p))
                    {
                        p.Base = basePlacement;
                        basePlacement.Derived.Add(p);
                    }

                    break;
                }
            }
        }

        foreach (var p in _placementOrder)
        {
            var root = p;
            var seen = new HashSet<Placement>(ReferenceEqualityComparer.Instance);
            while (root.Base is not null && seen.Add(root))
                root = root.Base;
            p.Root = root;
            foreach (var key in root.Entity.Key?.Attributes ?? p.Entity.Key?.Attributes ?? RList<RAttribute>.Empty)
                p.KeyIds.Add(key.Id);
        }
    }

    /// <summary>
    /// The most steps a walk up an entity's bases takes: a speculative resolution of an invalid snapshot may hold an inheritance cycle
    /// (MQ3002), and every walk must end; counting steps allocates nothing, which matters in walks made per binding field.
    /// </summary>
    internal const int MaxDepth = 4096;

    /// <summary>Whether <paramref name="target"/> is <paramref name="from"/> or one of its bases (the links made so far).</summary>
    private static bool Reaches(Placement from, Placement target)
    {
        var seen = new HashSet<Placement>(ReferenceEqualityComparer.Instance);
        for (var x = from; x is not null && seen.Add(x); x = x.Base)
        {
            if (ReferenceEquals(x, target))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The native type the type map gives a designed or extra column: a range or multirange column's comes from the map's
    /// <c>&lt;keyword&gt;:&lt;subtype&gt;</c> entry (<see cref="DialectTypeMaps.RangeKey"/>), else its <c>text</c> entry, as a dialect
    /// without range types stores the text form (MQ4056); any other column's from its keyword (or from a scalar type the caller passes,
    /// whose native type then applies).
    /// </summary>
    private string ColumnNativeType(RColumn c, RScalarType? scalar = null) =>
        !BuiltinTypes.IsRange(c.Type) ? NativeType(c.Type, c.Length, c.Precision, c.Scale, scalar, c.FixedLength, c.Unicode)
        : _typeMap.TryGetValue(DialectTypeMaps.RangeKey(c.Type, c.Subtype), out var range) ? range
        : NativeType("text", null, null, null);

    /// <summary>
    /// The native type of a logical type with its facets: the custom type's native type for the dialect when the value is of a custom
    /// type that declares one, else the effective type map's entry for the built-in keyword, or for its fixed-length and Unicode
    /// variant (<see cref="DialectTypeMaps.VariantKey"/>) when the column asks for one.
    /// </summary>
    private string NativeType(string type, int? length, int? precision, int? scale, RScalarType? scalar = null, bool fixedLength = false, bool? unicode = null)
    {
        var pattern = scalar is not null && scalar.NativeTypes.TryGetValue(_dialect, out var own) ? own : null;
        var keyword = pattern is null ? DialectTypeMaps.VariantKey(_typeMap, type, fixedLength, unicode) : type;
        var key = (keyword, length, precision, scale, pattern);
        if (!_nativeTypes.TryGetValue(key, out var native))
        {
            native = pattern is null
                ? DialectTypeMaps.Render(_typeMap, keyword, length, precision, scale, _conv)
                : DialectTypeMaps.RenderPattern(pattern, type, length, precision, scale, _conv);
            _nativeTypes[key] = native;
        }

        return native;
    }

    /// <summary>
    /// Renders a non-table name pattern with the column case (section 2.4). Same result as <see cref="NamePattern.Render"/>, with
    /// the pattern parsed once and word splits memoized for the run, since every column and constraint name goes through here.
    /// </summary>
    private string Render(string pattern, params (string Token, string Value)[] tokens)
    {
        if (_conv.ColumnCase == Model.CaseStyle.Preserve)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (token, value) in tokens)
                map[token] = value;
            return NamePattern.Render(pattern, map, _conv.ColumnCase);
        }

        if (!_patterns.TryGetValue(pattern, out var segments))
        {
            segments = ParsePattern(pattern);
            _patterns[pattern] = segments;
        }

        // The result depends only on the pattern and the values of the tokens it uses (many names repeat: foreign key and value
        // object columns, key columns), so it is memoized under exactly those.
        var memo = MemoKey(_renderKey, pattern, segments, tokens);
        if (_rendered.TryGetValue(memo, out var rendered))
            return rendered;

        var words = _renderWords;
        words.Clear();
        foreach (var (token, literal) in segments)
        {
            if (token is null)
            {
                words.AddRange(literal!);
                continue;
            }

            var value = Value(token, tokens);
            words.AddRange(value is null ? WordsOf("{" + token + "}") : WordsOf(value));
        }

        rendered = Casing.Join(words, _conv.ColumnCase);
        _rendered[memo] = rendered;
        return rendered;
    }

    /// <summary>
    /// The memo key of a rendered name: the pattern and the value of every token it uses, each length-prefixed (a token without a
    /// value is <c>-1</c>), so two different (pattern, values) pairs never give the same key, whatever characters they contain.
    /// </summary>
    /// <param name="key">A scratch builder (cleared first).</param>
    /// <param name="pattern">The pattern.</param>
    /// <param name="segments">The pattern's parsed segments.</param>
    /// <param name="tokens">The token values.</param>
    /// <returns>The key.</returns>
    internal static string MemoKey(System.Text.StringBuilder key, string pattern, List<(string? Token, IReadOnlyList<string>? Literal)> segments,
        (string Token, string Value)[] tokens)
    {
        key.Clear().Append(pattern.Length).Append(':').Append(pattern);
        foreach (var (token, _) in segments)
        {
            if (token is null)
                continue;
            if (Value(token, tokens) is { } value)
                key.Append('|').Append(value.Length).Append(':').Append(value);
            else
                key.Append("|-1");
        }

        return key.ToString();
    }

    private static string? Value(string token, (string Token, string Value)[] tokens)
    {
        foreach (var (name, v) in tokens)
        {
            if (string.Equals(name, token, StringComparison.Ordinal))
                return v;
        }

        return null;
    }

    private List<(string? Token, IReadOnlyList<string>? Literal)> ParsePattern(string pattern)
    {
        var segments = new List<(string? Token, IReadOnlyList<string>? Literal)>();
        var i = 0;
        while (i < pattern.Length)
        {
            var open = pattern.IndexOf('{', i);
            var close = open < 0 ? -1 : pattern.IndexOf('}', open + 1);
            if (open < 0 || close < 0)
            {
                segments.Add((null, WordsOf(pattern[i..])));
                break;
            }

            segments.Add((null, WordsOf(pattern[i..open])));
            segments.Add((pattern[(open + 1)..close], null));
            i = close + 1;
        }

        return segments;
    }

    private IReadOnlyList<string> WordsOf(string text)
    {
        if (!_words.TryGetValue(text, out var words))
        {
            words = Casing.Words(text);
            _words[text] = words;
        }

        return words;
    }

    /// <summary>
    /// Whether a dotted path lies below a head: it starts with the head and a dot (<c>path.StartsWith(head + ".")</c>, without building the
    /// string, which the binding checks did for every pair of fields).
    /// </summary>
    internal static bool IsBelow(string path, string head) =>
        path.Length > head.Length && path[head.Length] == '.' && path.StartsWith(head, StringComparison.Ordinal);

}
