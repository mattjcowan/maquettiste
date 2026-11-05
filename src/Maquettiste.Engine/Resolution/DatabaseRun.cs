using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Expressions;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The physical resolution of one database (engine-design.md sections 7.3 to 7.6): designed and imported tables passed through,
/// synthesized entity, child, junction and promoted tables, overlays merged, constraints named, mappings and join paths.
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
    private readonly Dictionary<string, Table> _overlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ForeignKeySpec> _foreignKeysById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RelationPhysical> _relationPhysical = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RColumn> _discriminators = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _columnNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<(string? Token, IReadOnlyList<string>? Literal)>> _patterns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _words = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rendered = new(StringComparer.Ordinal);
    private readonly System.Text.StringBuilder _renderKey = new();
    private readonly List<string> _renderWords = new(16);
    private readonly Dictionary<(string, int?, int?, int?, string?), string> _nativeTypes = [];
    private readonly Dictionary<RColumn, RScalarType> _columnScalars = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Placement, IReadOnlyList<string>> _keyDeps = new(ReferenceEqualityComparer.Instance);

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
        _rdb.ByConvention = db.ByConvention is { } byConvention ? ResolutionValues.Kebab(byConvention) : db.Packages.Count == 0 ? "all" : "packages";
        _rdb.Packages = [.. db.Packages.Select(p => new RConventionPackage
        {
            Package = run.PackageOf(p.Package),
            PackageId = p.Package,
            Schema = p.Schema is { } schemaId ? db.Schemas.FirstOrDefault(s => string.Equals(s.Id, schemaId, StringComparison.Ordinal))?.Name : null,
        })];
        var deps = new DependencySet(run.Keys).Element(db.Id).Referrers(db.Id);
        run.FillPhysicalAnnotations(_rdb, db, deps);
        _rdb.Dependencies = deps.ToList();
    }

    /// <summary>Resolves the database.</summary>
    public RDatabase Run()
    {
        foreach (var schema in _db.Schemas)
            _schemaNames.TryAdd(schema.Id, schema.Name);
        CollectBindings();
        CollectPhysicalFiles();
        ComputePlacements();
        CreateEntityTables();
        foreach (var relation in _run.RelationOrder)
        {
            _run.Ct.ThrowIfCancellationRequested();
            ResolveRelation(relation);
        }

        FinishTables();
        CheckForeignKeyColumns();
        CheckOnDeleteSets();
        FinishRelationMappings();
        ReportUnusedOverlays();
        BuildEntityMappings();
        BuildJoins();
        // Names are judged once the bindings, which Publish resolves, have added the entity checks they list to their write tables.
        var database = Publish();
        CheckIdentifiers();
        CheckNames();
        return database;
    }

    // ---- designed and imported tables, views, sequences, overlays ------------------------------------------------------------

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
        {
            if (table.Origin == TableOrigin.Synthesized)
            {
                var target = OverlayTarget(table);
                if (target is not null)
                    _overlays.TryAdd(target, table);
                continue;
            }

            BuildDesignedTable(table);
        }
    }

    private static string? OverlayTarget(Table table) =>
        table.Entity is { } entity ? (table.Attribute is { } attribute ? $"child:{entity}.{attribute}" : $"entity:{entity}")
        : table.Relation is { } relation ? $"relation:{relation}"
        : null;

    private void BuildDesignedTable(Table table)
    {
        var r = new RTable
        {
            Id = table.Id,
            Key = table.Id,
            Name = table.Name,
            Schema = SchemaName(table.Schema),
            Database = _rdb,
            Origin = ResolutionValues.Kebab(table.Origin),
            Comment = table.Comment,
        };
        var t = new TableBuild(_run.Keys, r, table, null, table.Id);
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
            var scalar = TypeColumn(t, c, column);
            ApplyFacetDefaults(c);
            ApplyNativeType(t, c, column.NativeType, () => ColumnNativeType(c, scalar));
            t.Columns.Add(c);
            t.ByKey.TryAdd(column.Id, c);
            _run.Register(c);
        }

        AddConstraintFiles(t, table);
    }

    /// <summary>Adds the constraints and indexes of a table file (designed table or overlay) as pending specs.</summary>
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
    /// Applies the physical properties and the annotations a column file sets (designed column, extra column or overlay entry) to a
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
                {
                    t.SequenceOverrides[c.Key] = sequenceId;
                    c.Sequence = _sequences.GetValueOrDefault(sequenceId);
                }

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
    /// Types a designed, imported, extra or inherited column by the enum, reference type or scalar type its own entry's <c>typeRef</c>
    /// names (next-phase-2-design.md section 3.2), before <see cref="ApplyFacetDefaults"/>: sets <see cref="RColumn.TypeRef"/>, fills
    /// the facets the column leaves out from the type (a reference type's code length and an enum's longest stored code in a string
    /// column; a scalar type's length, precision and scale in a column of its base), and for a reference type sets
    /// <see cref="RColumn.ReferenceType"/>, <see cref="RColumn.CodeType"/> and <see cref="RColumn.Strategy"/>, as a projected
    /// reference column has them. The column keeps its own physical type. The table depends on the type as a projected column's does.
    /// A type that is none of the three (a value object, MQ4082; a dangling id, MQ2001) types nothing.
    /// </summary>
    /// <returns>The scalar type whose native type for the dialect applies when the column sets no <c>nativeType</c> (its base is the
    /// column's type), else <see langword="null"/>.</returns>
    private RScalarType? TypeColumn(TableBuild t, RColumn c, Column column)
    {
        if (column.TypeRef is not { Ref: not null } typeRef)
            return null;
        var type = _run.TypeOf(typeRef, t.Deps, out _);
        switch (type)
        {
            case { Enum: { } e }:
                t.Deps.AddRange(_run.DepsOf(e).ToList());
                c.TypeRef = type;
                if (c.Type == "string")
                    c.Length ??= StringLength(e);
                return null;
            case { ReferenceType: { } reference }:
                t.Deps.Element(reference.Id).Add(ResolveRun.ReferenceDataKey);
                c.TypeRef = type;
                c.ReferenceType = reference;
                c.CodeType = reference.Code.Type;
                c.Strategy = _run.StorageChoice(reference.Id, _db.Id)?.Strategy;
                if (c.Type == "string")
                    c.Length ??= reference.Code.Length;
                return null;
            case { Scalar: { } scalar }:
                t.Deps.AddRange(_run.DepsOf(scalar).ToList());
                c.TypeRef = type;
                if (!string.Equals(c.Type, scalar.Base, StringComparison.Ordinal))
                    return null;
                c.Length ??= scalar.Length;
                c.Precision ??= scalar.Precision;
                c.Scale ??= scalar.Scale;
                _columnScalars[c] = scalar;
                return scalar;
            default:
                return null;
        }
    }

    /// <summary>The logical type that decides a column's native type: a reference column's code type, else the column's type.</summary>
    private static string PhysicalType(RColumn c) => c.Type == "reference" && c.CodeType is { } code ? code : c.Type;

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
    /// Types a table's, overlay's or stereotype's check written in the expression language, in the scope of the file that holds it, and
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

        foreach (var id in ExprResolution.Names(_run.Model, typed.Typed))
            t.Deps.Element(id);
        return new TreeCheck(new ExprRoot(typed.Tree, typed.Typed, typed.Scope, ExprSiteKind.Check, ExprColumns.None), keyPrefix, elementId, pointer);
    }

    /// <summary>
    /// A table's resolved check: a SQL text check as its spec says; a check in the expression language lowered to PostgreSQL through the
    /// table's final columns (a stereotype's check reads its columns under their keyed ids, an overlay's check an entity's attributes by
    /// their column keys), with its condition and shape. <see langword="null"/> leaves a tree check out when it reads a column the table
    /// does not have (validation, MQ4008 for a skipped stereotype, or <see cref="ReportUnstored"/> for an overlay's check, says so).
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
        List<string>? missing = null;
        foreach (var id in ExprResolution.Paths(tree.Source.Typed))
        {
            if (((tree.KeyPrefix is { } prefix ? t.Resolve(ContributedMembers.KeyOf(prefix, id)) : null) ?? t.Resolve(id)) is { } column)
                columns[id] = column;
            else
                (missing ??= []).Add(id);
        }

        if (missing is not null)
        {
            ReportUnstored(t, check.Name, tree, missing);
            return null;
        }

        if (LowerCheck(tree.Source, columns, $"Check '{check.Name}' of table '{t.Table.Name}' in database '{_db.Name}'", tree.ElementId, tree.Pointer) is not { } lowered)
            return null;
        check.Expression = lowered.Sql;
        check.Condition = lowered.Condition;
        check.Shape = lowered.Shape;
        return check;
    }

    /// <summary>
    /// MQ4008 at an overlay's own tree check for each path that reads no column of the table: an attribute of its entity, or an override
    /// entry of one, that the projection does not store in a column (the mapping ignores it, it is derived and not stored, a base's table
    /// per type holds it, or its value object is stored as JSON), so the check is reported where it is written rather than left out of
    /// the DDL silently. A stereotype's column is left
    /// to the findings that already name it (MQ4008 for a skipped stereotype, MQ2028 for an excluded member); a designed table's check
    /// reads only its own columns, as validation proves.
    /// </summary>
    private void ReportUnstored(TableBuild t, string name, TreeCheck tree, List<string> missing)
    {
        if (t.Overlay is not { } overlay || tree.KeyPrefix is not null || !string.Equals(tree.ElementId, overlay.Id, StringComparison.Ordinal))
            return;
        var contributed = t.Table.Key + ".";
        foreach (var id in missing)
        {
            if (id.StartsWith(contributed, StringComparison.Ordinal))
                continue;
            var member = ExprText.Print(new Expr { Path = [id] }, tree.Source.Scope);
            // A value object member whose value object the table stores as JSON is in the table, in that one column.
            var key = overlay.Columns.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal))?.Attribute ?? id;
            var why = key.IndexOf('.', StringComparison.Ordinal) is > 0 and var dot && t.Resolve(key[..dot]) is { Type: "json" } json
                ? $"which the table stores as JSON in its column '{json.Name}', not in a column of its own. The check is left out of the DDL; store the value object in columns (storage embedded), or read another member."
                : "which the table does not store: its mapping ignores it, does not store it, or stores it in another table. The check is left out of the DDL; store it in this table, or read another member.";
            _run.AddDiagnostic("MQ4008", $"Check '{name}' of table '{t.Table.Name}' in database '{_db.Name}' reads '{member}', {why}", overlay.Id, tree.Pointer);
        }
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
    /// Registers a table: its holder and effective stereotype set (next-phase-2-design.md section 3.1), annotations and storage from its
    /// file, or for a table without a file the stereotypes, merged default properties and storage its schema and package defaults give.
    /// </summary>
    /// <param name="t">The table.</param>
    /// <param name="holder">The holder of a table without a file when the caller knows it (a child table's, from its owner's), else
    /// <see langword="null"/>.</param>
    private void AddTable(TableBuild t, StereotypeHolder? holder = null)
    {
        var file = t.Source ?? t.Overlay;
        t.Holder = file is not null ? _run.Model.HolderOf(file) : holder ?? HolderWithoutFile(t.Table);
        t.Effective = _run.Model.EffectiveStereotypes(t.Holder);
        if (file is not null)
        {
            _run.FillPhysicalAnnotations(t.Table, file, t.Deps);
            t.Table.Storage = Storage([.. t.Effective.Select(s => s.Stereotype.Storage), file.Storage]);
        }
        else
        {
            _run.FillDefaultAnnotations(t.Table, t.Effective, t.Holder.Package, t.Deps);
            if (t.Effective.Count > 0)
                t.Table.Storage = Storage([.. t.Effective.Select(s => s.Stereotype.Storage)]);
        }

        AddBinderKeys(t.Deps, t.Table.Key);
        if (t.Source is { } designedFile)
        {
            AddBinderKeys(t.Deps, designedFile.Id);
            var entries = new DependencySet(_run.Keys).Element(designedFile.Id).Element(_db.Id);
            foreach (var applied in t.Effective)
                entries.Element(applied.Stereotype.Id);
            _run.AddPackageChain(entries, t.Holder.Package);
            t.Table.EntryKeys = entries.ToList();
        }

        if (t.Overlay is { } overlayFile)
            AddBinderKeys(t.Deps, overlayFile.Id);
        _tables.TryAdd(t.Table.Key, t);
        _tableOrder.Add(t);
        _run.Register(t.Table);
    }

    /// <summary>
    /// The holder of a synthesized entity or relation table without an overlay, by ids, by the rule validation uses
    /// (<see cref="StereotypeDefaults.SchemaOf"/>): its relation's or entity's package, and the schema its entity is routed to (a relation's
    /// table is in the default schema's entry). A child table takes its owner's holder instead (<see cref="CreateChildTable"/>).
    /// </summary>
    private StereotypeHolder HolderWithoutFile(RTable table)
    {
        var defaults = _run.Model.StereotypeDefaults;
        if (table.Relation is { } relation)
            return new StereotypeHolder(ElementKind.Table, [], [], _run.RelationSource(relation.Id)?.Package, defaults.SchemaOf(_db.Id, null));
        var entityId = table.Entity?.Id;
        return new StereotypeHolder(ElementKind.Table, [], [], entityId is null ? null : _run.EntitySource(entityId)?.Package,
            entityId is null ? defaults.SchemaOf(_db.Id, null) : defaults.SchemaOf(_db.Id, null, entityId));
    }

    private void AddSequence(RSequence sequence, string sourceId, DependencySet deps)
    {
        if (!_sequences.TryAdd(sequence.Id, sequence))
            return;
        _sequenceOrder.Add((sequence, sourceId, deps));
        _run.Register(sequence);
    }

    // ---- placements ---------------------------------------------------------------------------------------------------------

    private void ComputePlacements()
    {
        foreach (var entity in _run.EntityOrder)
        {
            var source = _run.EntitySource(entity.Id)!;
            var mapping = _run.MappingOf(_db.Id, entity.Id);
            if (_bindingByEntity.ContainsKey(entity.Id))
            {
                // A binding wins over projection (erratum E43): the entity never gets a table here, and a mapping element for the
                // same entity and database is ignored.
                if (mapping is not null)
                {
                    _run.AddDiagnostic("MQ4054", $"Mapping '{mapping.Name}' maps entity '{entity.Name}' to database '{_db.Name}', where the entity has a binding; the binding wins and the mapping is ignored. Delete the mapping.",
                        mapping.Id, "/entity");
                }

                if (BindingHost(entity.Id) is { } host)
                {
                    var bound = new Placement(entity, source, null) { Bound = true, ViaBinding = true, Table = host };
                    _placements[entity.Id] = bound;
                    _placementOrder.Add(bound);
                }

                continue;
            }

            if (!DatabaseScope.Places(_run.Model, _db, source.Package, mapping))
                continue;
            var placement = new Placement(entity, source, mapping);
            _placements[entity.Id] = placement;
            _placementOrder.Add(placement);
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

        foreach (var p in _placementOrder)
        {
            if (p.Root.Derived.Count > 0 || p.Base is not null)
                p.Strategy = p.Root.Mapping?.Inheritance ?? _conv.Inheritance;
            if (p.Mapping?.Table is { } tableId && _tables.TryGetValue(tableId, out var bound) && bound.IsDesigned)
            {
                p.Bound = true;
                p.Table = bound;
                bound.Table.Entity ??= p.Entity;
                bound.Deps.AddRange(_run.DepsOf(p.Entity).ToList());
            }
        }
    }

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

    /// <summary>The tables where rows of an entity live (the tables a dependent entity's foreign key columns go into).</summary>
    private List<TableBuild> HostsOf(Placement p)
    {
        if (p.Strategy == InheritanceStrategy.Tpc && !p.Bound)
        {
            var hosts = new List<TableBuild>();
            Collect(p);
            return hosts;

            void Collect(Placement x)
            {
                if (x.Table is not null && !x.Bound && !hosts.Contains(x.Table))
                    hosts.Add(x.Table);
                foreach (var d in x.Derived)
                    Collect(d);
            }
        }

        return p.Table is null ? [] : [p.Table];
    }

    // ---- entity tables ------------------------------------------------------------------------------------------------------

    private void CreateEntityTables()
    {
        // Shells first (TPH derived entities share the root's table), then columns in entity order.
        foreach (var p in _placementOrder)
        {
            if (p.Bound)
                continue;
            var ownsTable = p.Strategy switch
            {
                InheritanceStrategy.Tph => ReferenceEquals(p.Root, p),
                InheritanceStrategy.Tpc => !p.Entity.IsAbstract,
                _ => true,
            };
            if (!ownsTable)
                continue;
            p.Table = NewEntityTable(p.Entity.Id + "@" + _db.Id, p.Entity, null, _overlays.GetValueOrDefault("entity:" + p.Entity.Id), p.Entity.Id,
                DatabaseScope.SchemaFor(_run.Model, _db, p.Source.Package, p.Mapping));
        }

        foreach (var p in _placementOrder)
        {
            if (p.Strategy == InheritanceStrategy.Tph && !ReferenceEquals(p.Root, p) && !p.Bound)
                p.Table = p.Root.Table;
        }

        foreach (var p in _placementOrder)
        {
            _run.Ct.ThrowIfCancellationRequested();
            if (p.Bound)
            {
                if (!p.ViaBinding)
                    MatchBoundColumns(p);
                continue;
            }

            if (p.Table is null || !ReferenceEquals(p.Table.Table.Entity, p.Entity))
                continue;
            AddEntityColumns(p, p.Table);
        }
    }

    private TableBuild NewEntityTable(string key, REntity entity, RRelation? relation, Table? overlay, string sourceElementId, string? conventionSchema = null)
    {
        var name = overlay is { Name.Length: > 0 } ? overlay.Name : TableNameFor(entity.Name, ExplicitPlural(entity.Id));
        var r = new RTable
        {
            Id = key,
            Key = key,
            Name = name,
            Schema = SchemaName(overlay?.Schema ?? conventionSchema),
            Database = _rdb,
            Origin = "synthesized",
            Entity = entity,
            Relation = relation,
            Comment = overlay?.Comment,
        };
        var t = new TableBuild(_run.Keys, r, null, overlay, sourceElementId);
        t.Deps.AddRange(_run.DepsOf(entity).ToList()).Element(_db.Id).Add(Conventions).Add(TypeMaps).Referrers(entity.Id);
        if (_conv.PluralTables)
            t.Deps.Add(InflectionKey);
        if (overlay is not null)
        {
            t.Deps.Element(overlay.Id);
            AddConstraintFiles(t, overlay with { PrimaryKey = overlay.PrimaryKey is { } pk ? pk with { Columns = [] } : null });
            if (overlay.PrimaryKey is { Columns.Count: > 0 } overlayPk)
                t.Deferred.Add(() => { t.PrimaryKey.Clear(); t.PrimaryKey.AddRange(overlayPk.Columns); });
        }

        AddTable(t);
        return t;
    }

    /// <summary>The table name of an entity, enum or promoted entity by the <c>tableName</c> pattern (an explicit plural name wins).</summary>
    private string TableNameFor(string entityName, string? pluralName = null) =>
        NamePattern.Render(_conv.TableName, new Dictionary<string, string>(StringComparer.Ordinal) { ["entity"] = entityName, ["name"] = entityName },
            _conv.TableCase, _conv.PluralTables ? _run.Inflector : null, pluralName);

    /// <summary>The <c>pluralName</c> written in an entity's or enum's file, or <see langword="null"/>.</summary>
    private string? ExplicitPlural(string elementId) => _run.EntitySource(elementId)?.PluralName ?? _run.EnumSource(elementId)?.PluralName;

    private string ColumnName(string attributeName)
    {
        if (!_columnNames.TryGetValue(attributeName, out var name))
        {
            name = Casing.Apply(attributeName, _conv.ColumnCase);
            _columnNames[attributeName] = name;
        }

        return name;
    }

    /// <summary>
    /// The native type the type map gives a designed or extra column: a range or multirange column's comes from the map's
    /// <c>&lt;keyword&gt;:&lt;subtype&gt;</c> entry (<see cref="DialectTypeMaps.RangeKey"/>), else its <c>text</c> entry, as a dialect
    /// without range types stores the text form (MQ4056); any other column's from its keyword, or from the scalar type its
    /// <c>typeRef</c> names when <see cref="TypeColumn"/> found the scalar's native type applies (next-phase-2-design.md section 3.2).
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

    /// <summary>The custom type a synthesized column stores, when its native type came from that type (see <c>AddColumn</c>).</summary>
    private RScalarType? ScalarOf(RColumn? column) => column is not null && _columnScalars.TryGetValue(column, out var scalar) ? scalar : null;

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

    private void AddEntityColumns(Placement p, TableBuild t)
    {
        var entity = p.Entity;
        switch (p.Strategy)
        {
            case InheritanceStrategy.Tph:
                foreach (var a in entity.Attributes)
                    AddAttributeColumns(t, a, AttributeMappingFor(p, a), p.KeyIds.Contains(a.Id), false, entity.Name);
                foreach (var d in Descendants(p))
                {
                    t.Deps.AddRange(_run.DepsOf(d.Entity).ToList()).Referrers(d.Entity.Id);
                    foreach (var a in d.Entity.Attributes)
                    {
                        if (!t.ByKey.ContainsKey(a.Id) && !HasPathUnder(t, a.Id))
                            AddAttributeColumns(t, a, AttributeMappingFor(d, a), p.KeyIds.Contains(a.Id), true, entity.Name);
                    }
                }

                var discriminator = AddColumn(t, "discriminator", Render(_conv.DiscriminatorColumn), "string", 64, null, null, false, null, null, null);
                discriminator.IsDiscriminator = true;
                _discriminators[t.Table.Key] = discriminator;
                break;
            case InheritanceStrategy.Tpt when p.Base is not null:
                foreach (var a in entity.Attributes)
                {
                    if (p.KeyIds.Contains(a.Id) || ReferenceEquals(a.DeclaringEntity, entity))
                        AddAttributeColumns(t, a, AttributeMappingFor(p, a), p.KeyIds.Contains(a.Id), false, entity.Name);
                }

                break;
            default:
                foreach (var a in entity.Attributes)
                    AddAttributeColumns(t, a, AttributeMappingFor(p, a), p.KeyIds.Contains(a.Id), false, entity.Name);
                break;
        }

        if (!t.PrimaryKey.Any())
            t.PrimaryKey.AddRange(KeyColumnKeys(t, p));

        // TPT: the derived table's key is also a foreign key to the base table.
        if (p.Strategy == InheritanceStrategy.Tpt && p.Base?.Table is { } baseTable && !ReferenceEquals(baseTable, t))
        {
            t.ForeignKeys.Add(new ForeignKeySpec(t, [.. t.PrimaryKey], baseTable, null, [], "cascade", "no-action", null));
            // Its key columns follow the base table's (an overlay there may pin their native type), unless its own overlay pins them.
            t.Deps.AddRange(KeyDeps(p.Base));
            foreach (var key in t.PrimaryKey)
            {
                if (t.Resolve(key) is not { } column || baseTable.Resolve(key) is not { } referenced || PinsType(t.OverlayColumns.GetValueOrDefault(key)))
                    continue;
                (column.Type, column.Length, column.Precision, column.Scale, column.NativeType) =
                    (referenced.Type, referenced.Length, referenced.Precision, referenced.Scale, referenced.NativeType);
            }
        }

        ApplyKeyGeneration(p, t);
        AddAttributeConstraints(t, entity, p.Strategy == InheritanceStrategy.Tph ? Descendants(p).Select(d => d.Entity) : []);
        foreach (var action in t.Deferred.ToList())
            action();
        t.Deferred.Clear();
    }

    private static bool HasPathUnder(TableBuild t, string attributeId) =>
        t.Columns.Any(c => c.AttributePath is { } path && path.StartsWith(attributeId + ".", StringComparison.Ordinal));

    private static IEnumerable<Placement> Descendants(Placement p)
    {
        foreach (var d in p.Derived)
        {
            yield return d;
            foreach (var dd in Descendants(d))
                yield return dd;
        }
    }

    private static IEnumerable<string> KeyColumnKeys(TableBuild t, Placement p) =>
        (p.Root.Entity.Key?.Attributes ?? p.Entity.Key?.Attributes ?? RList<RAttribute>.Empty)
            .SelectMany(k => t.Columns.Where(c => c.AttributePath is { } path
                && (string.Equals(path, k.Id, StringComparison.Ordinal) || path.StartsWith(k.Id + ".", StringComparison.Ordinal))))
            .Select(c => c.Key);

    private AttributeMapping? AttributeMappingFor(Placement p, RAttribute a)
    {
        foreach (var candidate in new[] { p.Mapping, a.DeclaringEntity is { } d ? _run.MappingOf(_db.Id, d.Id) : null, p.Root.Mapping })
        {
            var found = candidate?.Attributes.FirstOrDefault(x => string.Equals(x.Attribute, a.Id, StringComparison.Ordinal));
            if (found is not null)
                return found;
        }

        return null;
    }

    /// <summary>Identity or sequence on the key column (section 7.5, D37).</summary>
    private void ApplyKeyGeneration(Placement p, TableBuild t)
    {
        if (p.Strategy == InheritanceStrategy.Tpt && p.Base is not null)
            return; // the key comes from the base row
        var strategy = _run.EntitySource(p.Root.Entity.Id)?.Key?.Strategy;
        if (t.PrimaryKey.Count != 1 || t.Resolve(t.PrimaryKey[0]) is not { } column)
            return;
        if (strategy == IdentityStrategy.DatabaseIdentity && !t.SequenceOverrides.ContainsKey(column.Key) && column.Sequence is null)
        {
            column.Identity = true;
            return;
        }

        if (strategy != IdentityStrategy.Sequence || column.Identity)
            return;
        var sequence = t.SequenceOverrides.TryGetValue(column.Key, out var id) ? _sequences.GetValueOrDefault(id) : null;
        if (sequence is null)
        {
            var key = p.Root.Entity.Id + ".sequence@" + _db.Id;
            if (!_sequences.TryGetValue(key, out sequence))
            {
                var tableName = p.Root.Table?.Table.Name ?? TableNameFor(p.Root.Entity.Name, ExplicitPlural(p.Root.Entity.Id));
                var type = column.Type is "int16" or "int32" or "int64" ? column.Type : "int64";
                sequence = new RSequence
                {
                    Id = key,
                    Name = Render(_conv.SequenceName, ("table", tableName), ("entity", p.Root.Entity.Name)),
                    Schema = t.Table.Schema,
                    Database = _rdb,
                    Type = type,
                    NativeType = DialectTypeMaps.Render(_typeMap, type, null, null, null, _conv),
                };
                var deps = new DependencySet(_run.Keys).AddRange(t.Deps.ToList()).Referrers(p.Root.Entity.Id);
                AddSequence(sequence, p.Root.Entity.Id, deps);
            }
        }

        column.Sequence = sequence;
        foreach (var member in Descendants(p.Root).Prepend(p.Root))
            _run.SetKeySequence(member.Entity, _db.Name, sequence);
    }

    /// <summary>Unique and indexed attributes, and alternate keys, as constraints of the table holding their columns.</summary>
    private void AddAttributeConstraints(TableBuild t, REntity entity, IEnumerable<REntity> more)
    {
        foreach (var e in more.Prepend(entity))
        {
            foreach (var a in e.Attributes)
            {
                if (!a.Unique && !a.Indexed)
                    continue;
                if (!ReferenceEquals(e, entity) && !ReferenceEquals(a.DeclaringEntity, e))
                    continue;
                var keys = ColumnsOf(t, a.Id);
                if (keys.Count == 0)
                    continue;
                if (a.Unique)
                    t.Uniques.Add(new UniqueSpec(keys, null, null));
                else
                    t.Indexes.Add(new IndexSpec([.. keys.Select(k => new IndexColumnSpec(k, false))], [], null, false, "default", null));
            }

            foreach (var ak in e.AlternateKeys)
            {
                var keys = ak.Attributes.SelectMany(a => ColumnsOf(t, a.Id)).ToList();
                if (keys.Count > 0 && ak.Attributes.All(a => ColumnsOf(t, a.Id).Count > 0) && !t.Uniques.Any(u => u.Columns.SequenceEqual(keys)))
                    t.Uniques.Add(new UniqueSpec(keys, null, ak.Name));
            }
        }
    }

    private static List<string> ColumnsOf(TableBuild t, string attributeId) =>
        [.. t.Columns.Where(c => c.AttributePath is { } path
                && (string.Equals(path, attributeId, StringComparison.Ordinal) || path.StartsWith(attributeId + ".", StringComparison.Ordinal)))
            .Select(c => c.Key)];

    /// <summary>Binds attributes to the columns of a designed or imported table: <c>AttributeMapping.Column</c>, else by name.</summary>
    private void MatchBoundColumns(Placement p)
    {
        var t = p.Table!;
        foreach (var a in p.Entity.Attributes)
        {
            var am = AttributeMappingFor(p, a);
            if (am is { Ignore: true })
                continue;
            if (a.Type.ValueObject is { } vo && !a.Collection)
            {
                foreach (var m in vo.Attributes)
                {
                    var expected = Render(_conv.ValueObjectColumn, ("attribute", am?.Prefix ?? a.Name), ("member", m.Name));
                    Bind(t, FindByName(t, expected), m, a.Id + "." + m.Id);
                }

                continue;
            }

            var column = am?.Column is { } columnId ? t.Resolve(columnId) : FindByName(t, p.KeyIds.Contains(a.Id)
                ? Render(_conv.KeyColumn, ("attribute", a.Name), ("entity", p.Entity.Name), ("table", t.Table.Name))
                : ColumnName(a.Name)) ?? FindByName(t, ColumnName(a.Name));
            Bind(t, column, a, a.Id);
        }

        static void Bind(TableBuild t, RColumn? column, RAttribute a, string path)
        {
            if (column is null || column.AttributePath is not null)
                return;
            column.Attribute = a;
            column.AttributePath = path;
            t.ByKey.TryAdd(path, column);
        }
    }

    private static RColumn? FindByName(TableBuild t, string name) =>
        t.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal))
        ?? t.Columns.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
