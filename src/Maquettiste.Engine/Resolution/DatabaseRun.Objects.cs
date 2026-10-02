using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Routines, database types and SQL objects of one database (added 2026-10-01): each file resolved like a view, parameter, result
/// and field types resolved to native types (a database type of the same database included), and the columns whose
/// <c>nativeType</c> names a database type pointed at it.
/// </summary>
internal sealed partial class DatabaseRun
{
    private readonly Dictionary<string, RDatabaseType> _dbTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RDatabaseType> _dbTypesByName = new(StringComparer.Ordinal);
    private readonly List<(RDatabaseType Type, DatabaseType File, DependencySet Deps)> _typeOrder = [];
    private readonly List<(RRoutine Routine, Routine File, DependencySet Deps)> _routines = [];
    private readonly List<(RSqlObject Object, SqlObject File, DependencySet Deps)> _objects = [];

    /// <summary>The language a routine's body is written in when its file names none.</summary>
    private string DefaultLanguage => _dialect switch
    {
        "postgresql" => "plpgsql",
        "sqlserver" => "tsql",
        "oracle" => "plsql",
        _ => "sql",
    };

    /// <summary>Resolves the database types, routines and SQL objects of the database (before its tables, whose columns may use a type).</summary>
    private void CollectDatabaseObjects()
    {
        foreach (var file in _run.Model.All<DatabaseType>().Where(t => t.Database == _db.Id).OrderBy(t => t.Id, StringComparer.Ordinal))
        {
            var r = new RDatabaseType
            {
                Id = file.Id,
                Name = file.Name,
                Schema = SchemaName(file.Schema),
                Database = _rdb,
                TypeKind = ResolutionValues.Kebab(file.TypeKind),
                Base = file.Base,
                Length = file.Length,
                Precision = file.Precision,
                Scale = file.Scale,
                Check = file.Check,
                Members = [.. file.Members],
                Subtype = file.Subtype,
                Definition = ForDialect(file.Definition),
                Comment = file.Comment,
            };
            if (file.Base is { } baseType)
                r.BaseNativeType = NativeType(baseType, file.Length, file.Precision, file.Scale);
            if (file.Subtype is { } subtype)
                r.SubtypeNativeType = NativeType(subtype, null, null, null);
            r.IsCreated = r.Definition is not null || _dialect switch
            {
                "postgresql" => true,
                "sqlserver" => file.TypeKind == DatabaseTypeKind.Domain && file.Base is not null,
                _ => false,
            };
            var deps = new DependencySet(_run.Keys).Element(file.Id).Referrers(file.Id).Element(_db.Id).Add(TypeMaps).Add(Conventions);
            _run.FillPhysicalAnnotations(r, file, deps);
            _dbTypes.TryAdd(file.Id, r);
            _typeOrder.Add((r, file, deps));
            _run.Register(r);
        }

        // Names after ids, so a type whose name looks like another's id never hides it; the first file (by id) keeps a duplicate name.
        foreach (var (type, _, _) in _typeOrder)
        {
            _dbTypesByName.TryAdd(type.Name, type);
            if (type.Schema is { } schema)
                _dbTypesByName.TryAdd(schema + "." + type.Name, type);
        }

        foreach (var (type, file, deps) in _typeOrder)
        {
            type.NativeName = file.NativeName ?? (type.IsCreated ? Qualified(type.Schema, type.Name) : FallbackNativeType(file));
            var used = new List<IResolvedObject>();
            type.Fields = [.. file.Fields.Select(f =>
            {
                var (keyword, dbType, native) = Slot(f.Type, f.Length, f.Precision, f.Scale, f.NativeType, deps);
                if (dbType is not null && !used.Contains(dbType))
                    used.Add(dbType);
                return new RDatabaseTypeField
                {
                    Name = f.Name, Type = keyword, DbType = dbType, Length = f.Length, Precision = f.Precision, Scale = f.Scale, NativeType = native,
                };
            })];
            type.DependsOn = used;
        }

        foreach (var file in _run.Model.All<Routine>().Where(r => r.Database == _db.Id).OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            var deps = new DependencySet(_run.Keys).Element(file.Id).Referrers(file.Id).Element(_db.Id).Add(TypeMaps).Add(Conventions);
            var body = ForDialect(file.Body);
            var r = new RRoutine
            {
                Id = file.Id,
                Name = file.Name,
                Schema = SchemaName(file.Schema),
                Database = _rdb,
                RoutineKind = ResolutionValues.Kebab(file.RoutineKind),
                Language = file.Language ?? DefaultLanguage,
                Body = body ?? "",
                HasBody = body is not null,
                Deterministic = file.Deterministic,
                Security = ResolutionValues.Kebab(file.Security),
                Comment = file.Comment,
            };
            r.Parameters = [.. file.Parameters.Select(p =>
            {
                var (keyword, dbType, native) = Slot(p.Type, p.Length, p.Precision, p.Scale, p.NativeType, deps);
                return new RRoutineParameter
                {
                    Name = p.Name, Type = keyword, DbType = dbType, Length = p.Length, Precision = p.Precision, Scale = p.Scale, NativeType = native,
                    Mode = ResolutionValues.Kebab(p.Mode), Default = p.Default,
                };
            })];
            if (file.Returns is { } returns)
            {
                var result = new RRoutineReturns { Length = returns.Length, Precision = returns.Precision, Scale = returns.Scale };
                if (returns.Table is { } table)
                {
                    result.Table = [.. table.Select(c =>
                    {
                        var (keyword, dbType, native) = Slot(c.Type, c.Length, c.Precision, c.Scale, c.NativeType, deps);
                        return new RRoutineColumn
                        {
                            Name = c.Name, Type = keyword, DbType = dbType, Length = c.Length, Precision = c.Precision, Scale = c.Scale, NativeType = native,
                            Nullable = c.Nullable,
                        };
                    })];
                }
                else
                {
                    (result.Type, result.DbType, result.NativeType) = Slot(returns.Type, returns.Length, returns.Precision, returns.Scale, returns.NativeType, deps);
                }

                r.Returns = result;
            }

            _run.FillPhysicalAnnotations(r, file, deps);
            _routines.Add((r, file, deps));
            _run.Register(r);
        }

        foreach (var file in _run.Model.All<SqlObject>().Where(o => o.Database == _db.Id).OrderBy(o => o.Id, StringComparer.Ordinal))
        {
            var deps = new DependencySet(_run.Keys).Element(file.Id).Referrers(file.Id).Element(_db.Id);
            var body = ForDialect(file.Body);
            var r = new RSqlObject
            {
                Id = file.Id,
                Name = file.Name,
                Schema = SchemaName(file.Schema),
                Database = _rdb,
                ObjectKind = file.ObjectKind,
                Phase = ResolutionValues.Kebab(file.Phase),
                Body = body ?? "",
                HasBody = body is not null,
            };
            _run.FillPhysicalAnnotations(r, file, deps);
            _objects.Add((r, file, deps));
            _run.Register(r);
        }
    }

    /// <summary>A name with its schema, unless there is none (SQLite).</summary>
    private static string Qualified(string? schema, string name) => schema is { Length: > 0 } ? schema + "." + name : name;

    /// <summary>
    /// The native type a dialect without the type's kind stores a value of it as: a domain's base, an enum's string (as long as its
    /// longest label), else the empty string.
    /// </summary>
    private string FallbackNativeType(DatabaseType file) => file.TypeKind switch
    {
        DatabaseTypeKind.Domain when file.Base is { } baseType => NativeType(baseType, file.Length, file.Precision, file.Scale),
        DatabaseTypeKind.Enum when file.Members.Count > 0 => NativeType("string", file.Members.Max(m => m.Length), null, null),
        _ => "",
    };

    /// <summary>
    /// Resolves a typed slot (a parameter, a result, a column of a table result, a composite field): the built-in keyword (or
    /// <see langword="null"/>), the database type the type names (or <see langword="null"/>) and the native type for the dialect (the
    /// slot's own, else the database type's native name, else the type map's). A type that is neither resolves to its text, which
    /// validation reports (MQ4018).
    /// </summary>
    private (string? Keyword, RDatabaseType? DbType, string Native) Slot(string? type, int? length, int? precision, int? scale, string? nativeType,
        DependencySet deps)
    {
        if (type is not null && _dbTypes.TryGetValue(type, out var dbType))
        {
            deps.Element(dbType.Id);
            return (null, dbType, nativeType ?? dbType.NativeName);
        }

        if (type is not null && BuiltinTypes.IsBuiltin(type))
        {
            var withDefaults = new RColumn { Type = type, Length = length, Precision = precision, Scale = scale };
            ApplyFacetDefaults(withDefaults);
            return (type, null, nativeType ?? NativeType(type, withDefaults.Length, withDefaults.Precision, withDefaults.Scale));
        }

        return (null, null, nativeType ?? type ?? "");
    }

    /// <summary>
    /// The database type a column file's <c>nativeType</c> names, by id or by name (plain or schema-qualified), or
    /// <see langword="null"/> when it is a plain native type. The column's dependencies gain the type's file.
    /// </summary>
    private RDatabaseType? DbTypeOf(string? nativeType, TableBuild t)
    {
        if (nativeType is null || (!_dbTypes.TryGetValue(nativeType, out var type) && !_dbTypesByName.TryGetValue(nativeType, out type)))
            return null;
        t.Deps.Element(type.Id);
        return type;
    }

    /// <summary>
    /// Points a column at the database type its file's <c>nativeType</c> names and takes the type's native name, unless the type has
    /// none for the dialect (a composite on SQLite), when the column keeps <paramref name="computed"/>.
    /// </summary>
    private void ApplyNativeType(TableBuild t, RColumn c, string? nativeType, Func<string> computed)
    {
        if (DbTypeOf(nativeType, t) is { } type)
        {
            c.DbType = type;
            c.NativeType = type.NativeName.Length > 0 ? type.NativeName : computed();
            return;
        }

        c.NativeType = nativeType ?? computed();
    }

    /// <summary>Fills each routine's and SQL object's <c>dependsOn</c> once every object of the database exists, and freezes their dependencies.</summary>
    private void FinishDatabaseObjects(List<RView> views, List<RSequence> sequences)
    {
        var byId = new Dictionary<string, IResolvedObject>(StringComparer.Ordinal);
        foreach (var t in _tableOrder)
        {
            byId.TryAdd(t.Table.Id, t.Table);
            if (t.Overlay is { } overlay)
                byId.TryAdd(overlay.Id, t.Table);
        }

        foreach (var v in views)
            byId.TryAdd(v.Id, v);
        foreach (var s in sequences)
            byId.TryAdd(s.Id, s);
        foreach (var (type, _, _) in _typeOrder)
            byId.TryAdd(type.Id, type);
        foreach (var (routine, _, _) in _routines)
            byId.TryAdd(routine.Id, routine);
        foreach (var (obj, _, _) in _objects)
            byId.TryAdd(obj.Id, obj);

        List<IResolvedObject> Resolve(IReadOnlyList<string> ids, DependencySet deps)
        {
            var list = new List<IResolvedObject>();
            foreach (var id in ids)
            {
                deps.Element(id);
                if (byId.TryGetValue(id, out var target) && !list.Contains(target))
                    list.Add(target);
            }

            return list;
        }

        foreach (var (routine, file, deps) in _routines)
        {
            routine.DependsOn = Resolve(file.DependsOn, deps);
            routine.Dependencies = deps.ToList();
        }

        foreach (var (obj, file, deps) in _objects)
        {
            obj.DependsOn = Resolve(file.DependsOn, deps);
            obj.Dependencies = deps.ToList();
        }

        foreach (var (type, _, deps) in _typeOrder)
            type.Dependencies = deps.ToList();
    }
}
