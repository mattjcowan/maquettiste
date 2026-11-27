using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Entity bindings to one database (erratum E43, engine-design.md section 7, "Bindings and materialize"). An entity's binding is
/// collected before anything is placed, so the entity is never projected here; the tables, views and queries a binding names get the
/// entity's file among their dependency keys as they are created (they list the binding in <c>BoundBy</c>); and once the queries are
/// resolved each binding resolves its source, type column, constants, field map, accounted columns, write table and delete, and the rules
/// MQ4044 to MQ4053, MQ4058, MQ4122 to MQ4126, MQ4128 to MQ4134 and MQ4136 report what it gets wrong with the JSON pointer of the binding's entry in
/// the entity's file. A binding with a base foreign key (table per type) is built after its base's, whose resolved binding it reads.
/// </summary>
internal sealed partial class DatabaseRun
{
    /// <summary>The first binding to this database of each entity, by entity id, with its index in the entity's bindings.</summary>
    private readonly Dictionary<string, (EntityBinding Binding, int Index)> _bindingByEntity = new(StringComparer.Ordinal);

    /// <summary>The bound entities in entity order.</summary>
    private readonly List<REntity> _boundEntities = [];

    /// <summary>The ids of the entities whose binding names a source or write table, by the id or key the binding writes.</summary>
    private readonly Dictionary<string, List<string>> _bindersBySource = new(StringComparer.Ordinal);

    private void CollectBindings()
    {
        foreach (var entity in _run.EntityOrder)
        {
            if (_run.EntitySource(entity.Id) is not { Bindings.Count: > 0 } source)
                continue;
            for (var i = 0; i < source.Bindings.Count; i++)
            {
                var binding = source.Bindings[i];
                if (!string.Equals(binding.Database, _db.Id, StringComparison.Ordinal))
                    continue;
                if (_bindingByEntity.TryGetValue(entity.Id, out var first))
                {
                    _run.AddDiagnostic("MQ4050", $"Entity '{entity.Name}' has a second binding to database '{_db.Name}' (the first is at index {first.Index.ToString(CultureInfo.InvariantCulture)}): an entity has one binding per database; this one is ignored.",
                        entity.Id, Pointer(i) + "/database");
                    continue;
                }

                _bindingByEntity[entity.Id] = (binding, i);
                _boundEntities.Add(entity);
                AddBinder(binding.Source, entity.Id);
                if (binding.Write?.Table is { } table)
                    AddBinder(table, entity.Id);
            }
        }

        void AddBinder(string key, string entityId)
        {
            if (!_bindersBySource.TryGetValue(key, out var list))
                _bindersBySource[key] = list = [];
            if (!list.Contains(entityId))
                list.Add(entityId);
        }
    }

    private static string Pointer(int index) => "/bindings/" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The entity whose binding to this database gives the entity's type column: the entity itself when its binding names
    /// <c>discriminatorColumn</c>, else its nearest base whose binding names one; <see langword="null"/> when none does. A binding that names
    /// one while a base's binding in the same table already does (<see cref="NamesInheritedTypeColumn"/>, MQ4126) gives none: the column is
    /// the base's.
    /// </summary>
    private REntity? TypeColumnOwner(REntity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var e = entity; e is not null && seen.Add(e.Id); e = e.Base)
        {
            if (!_bindingByEntity.TryGetValue(e.Id, out var bound))
                continue;
            if (bound.Binding.DiscriminatorColumn is not null && !NamesInheritedTypeColumn(e))
                return e;
            // A binding joined to its base's table by a foreign key (table per type) keeps its own table: the base's type column is the
            // base's table's, which the derived entity's statements reach through its bases.
            if (IsPerType(e))
                return null;
        }

        return null;
    }

    /// <summary>
    /// Whether an entity's binding to this database joins its base's table by a foreign key (table per type): it names
    /// <c>baseForeignKey</c>, has a bound base, and keeps its rows in another table than that base's binding.
    /// </summary>
    private bool IsPerType(REntity entity) =>
        _bindingByEntity.TryGetValue(entity.Id, out var bound) && bound.Binding.BaseForeignKey is not null && BoundBase(entity) is { } b
        && !string.Equals(BindingHostKey(bound.Binding), BindingHostKey(_bindingByEntity[b.Id].Binding), StringComparison.Ordinal);

    /// <summary>The nearest base of an entity with a binding to this database, or <see langword="null"/>.</summary>
    private REntity? BoundBase(REntity entity)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { entity.Id };
        for (var e = entity.Base; e is not null && seen.Add(e.Id); e = e.Base)
        {
            if (_bindingByEntity.ContainsKey(e.Id))
                return e;
        }

        return null;
    }

    /// <summary>The bound bases whose tables each entity's rows also live in, by entity id (built on first use).</summary>
    private readonly Dictionary<string, List<REntity>> _upperLevels = new(StringComparer.Ordinal);

    /// <summary>
    /// The bound bases whose tables an entity's rows also live in (table per type), nearest first, read from the files: the base a
    /// <c>baseForeignKey</c> reaches, then its own levels; a binding without one in its bound base's table shares the base's levels. Empty
    /// for every other binding. Whether the foreign key itself fits is the binding's resolution's (MQ4128 to MQ4130).
    /// </summary>
    private List<REntity> UpperLevels(REntity entity)
    {
        if (_upperLevels.TryGetValue(entity.Id, out var cached))
            return cached;
        _upperLevels[entity.Id] = []; // a cycle guard while the chain is walked
        var levels = new List<REntity>();
        if (_bindingByEntity.TryGetValue(entity.Id, out var own) && BoundBase(entity) is { } b)
        {
            if (IsPerType(entity))
                levels.Add(b);
            if (levels.Count > 0 || string.Equals(BindingHostKey(own.Binding), BindingHostKey(_bindingByEntity[b.Id].Binding), StringComparison.Ordinal))
                levels.AddRange(UpperLevels(b).Where(l => !levels.Contains(l)));
        }

        _upperLevels[entity.Id] = levels;
        return levels;
    }

    /// <summary>Whether an entity's rows live in the table a host key names: its own, or one of its levels' (table per type).</summary>
    private bool KeepsRowsIn(REntity entity, string host) =>
        string.Equals(BindingHostKey(_bindingByEntity[entity.Id].Binding), host, StringComparison.Ordinal)
        || UpperLevels(entity).Exists(l => string.Equals(BindingHostKey(_bindingByEntity[l.Id].Binding), host, StringComparison.Ordinal));

    /// <summary>
    /// MQ4126: whether the entity's binding names <c>discriminatorColumn</c> while the binding of one of its bases to this database, in the
    /// same table, names one too. A derived entity bound to another table may name its own.
    /// </summary>
    private bool NamesInheritedTypeColumn(REntity entity)
    {
        if (!_bindingByEntity.TryGetValue(entity.Id, out var own) || own.Binding.DiscriminatorColumn is null)
            return false;
        var host = BindingHostKey(own.Binding);
        for (var e = entity.Base; e is not null; e = e.Base)
        {
            if (_bindingByEntity.TryGetValue(e.Id, out var bound) && bound.Binding.DiscriminatorColumn is not null
                && string.Equals(BindingHostKey(bound.Binding), host, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// What holds a binding's rows, by table key (another source by its id as written): the table it writes, else (writing nowhere) its
    /// source.
    /// </summary>
    private string BindingHostKey(EntityBinding binding)
    {
        var key = binding.Write is { None: true } ? binding.Source : binding.Write?.Table ?? binding.Source;
        return _tables.TryGetValue(key, out var t) || _tables.TryGetValue(key + "@" + _db.Id, out t) ? t.Table.Key : key;
    }

    /// <summary>The bound entities that use each type column, by the entity whose binding names it, in entity order (built once).</summary>
    private Dictionary<REntity, List<REntity>>? _typeColumnMembers;

    /// <summary>The bound entities that use the type column the entity's binding names, itself included, in entity order.</summary>
    private List<REntity> TypeColumnMembers(REntity owner)
    {
        if (_typeColumnMembers is null)
        {
            _typeColumnMembers = new Dictionary<REntity, List<REntity>>(ReferenceEqualityComparer.Instance);
            foreach (var e in _boundEntities)
            {
                // The type column of its own table, then those of the tables above it (table per type): the entity has rows in each.
                foreach (var o in new[] { TypeColumnOwner(e) }.Concat(UpperLevels(e).Select(TypeColumnOwner)))
                {
                    if (o is null)
                        continue;
                    if (!_typeColumnMembers.TryGetValue(o, out var list))
                        _typeColumnMembers[o] = list = [];
                    if (!list.Contains(e))
                        list.Add(e);
                }
            }
        }

        return _typeColumnMembers.TryGetValue(owner, out var members) ? members : [];
    }

    /// <summary>Whether an entity is another or derives from it.</summary>
    private static bool DerivesFrom(REntity entity, REntity from)
    {
        for (var e = entity; e is not null; e = e.Base)
        {
            if (ReferenceEquals(e, from))
                return true;
        }

        return false;
    }

    /// <summary>Whether two type codes are the same code: both text or both integers, with the same text.</summary>
    private static bool SameCode(object a, object b) => (a is string) == (b is string) && string.Equals(CodeText(a), CodeText(b), StringComparison.Ordinal);

    /// <summary>
    /// Adds the files of the entities bound to an object (its id or key) to its dependency keys: its <c>BoundBy</c> lists them. A binding
    /// that lists entity checks also adds what each check depends on (<see cref="REntityCheck.DependsOn"/>: the base that declares an
    /// inherited one, a value object a path reads, an enum, a seed, a constraint), since the checks it writes into its table read them.
    /// </summary>
    private void AddBinderKeys(DependencySet deps, string key)
    {
        if (_bindersBySource.Count == 0 || !_bindersBySource.TryGetValue(key, out var entities))
            return;
        foreach (var entityId in entities)
        {
            deps.Element(entityId);
            if (_bindingByEntity[entityId].Binding.Checks.Count == 0 || _run.EntityById(entityId) is not { } entity)
                continue;
            foreach (var check in entity.Checks)
            {
                if (_bindingByEntity[entityId].Binding.Checks.Contains(check.Id, StringComparer.Ordinal))
                {
                    foreach (var id in check.DependsOn)
                        deps.Element(id);
                }
            }
        }
    }

    /// <summary>
    /// The designed or imported table a bound entity's rows live in, for the foreign keys of its relations: the binding's write table,
    /// else its source when that is a table; <see langword="null"/> for a read-only binding over a view or a query.
    /// </summary>
    private TableBuild? BindingHost(string entityId)
    {
        var (binding, _) = _bindingByEntity[entityId];
        if (binding.Write is { None: true })
            return null;
        var key = binding.Write?.Table ?? binding.Source;
        return _tables.TryGetValue(key, out var t) && t.IsDesigned ? t : null;
    }

    /// <summary>The bindings resolved so far in this database, by entity id.</summary>
    private readonly Dictionary<string, REntityBinding> _resolvedBindings = new(StringComparer.Ordinal);

    /// <summary>The entities whose binding waits for its base's (a guard against a cycle of bases).</summary>
    private readonly HashSet<string> _building = new(StringComparer.Ordinal);

    private void ResolveBindings(List<RView> views)
    {
        if (_bindingByEntity.Count == 0)
            return;
        var tables = new Dictionary<string, TableBuild>(StringComparer.Ordinal);
        foreach (var t in _tableOrder)
        {
            tables.TryAdd(t.Table.Key, t);
            if (t.Overlay is { } overlay)
                tables.TryAdd(overlay.Id, t);
            if (t.Source is { } designed)
                tables.TryAdd(designed.Id, t);
        }

        var viewsById = views.ToDictionary(v => v.Id, StringComparer.Ordinal);
        var queriesById = _queries.ToDictionary(q => q.Query.Id, q => q.Query, StringComparer.Ordinal);
        var boundBy = new Dictionary<object, List<REntityBinding>>(ReferenceEqualityComparer.Instance);
        var built = new List<REntityBinding>(_boundEntities.Count);
        // A binding that joins its base's table (table per type), or shares the table of a base that does, reads the base's resolved
        // binding: the base is built first.
        _resolvedBindings.Clear();
        REntityBinding BuildOne(REntity entity)
        {
            if (_resolvedBindings.TryGetValue(entity.Id, out var done))
                return done;
            if (UpperLevels(entity).Count > 0 && BoundBase(entity) is { } b && _building.Add(entity.Id))
            {
                BuildOne(b);
                _building.Remove(entity.Id);
            }

            var (file, index) = _bindingByEntity[entity.Id];
            var result = new BindingBuilder(this, entity, file, index, tables, viewsById, queriesById).Build();
            _resolvedBindings[entity.Id] = result;
            return result;
        }

        foreach (var entity in _boundEntities)
        {
            _run.Ct.ThrowIfCancellationRequested();
            var (file, index) = _bindingByEntity[entity.Id];
            var binding = BuildOne(entity);
            _run.SetEntityBinding(entity, _db.Name, binding);
            _run.Register(binding);
            built.Add(binding);
            AddBindingChecks(entity, file, index, binding);
            foreach (var target in new object?[] { binding.SourceTable, binding.SourceView, binding.SourceQuery, binding.WriteTable })
            {
                if (target is null)
                    continue;
                if (!boundBy.TryGetValue(target, out var list))
                    boundBy[target] = list = [];
                if (!list.Contains(binding))
                    list.Add(binding);
            }
        }

        foreach (var (target, list) in boundBy)
        {
            var sorted = list.OrderBy(b => b.Entity.Name, StringComparer.Ordinal).ThenBy(b => b.Entity.Id, StringComparer.Ordinal).ToList();
            switch (target)
            {
                case RTable t:
                    t.BoundBy = sorted;
                    break;
                case RView v:
                    v.BoundBy = sorted;
                    break;
                case RQuery q:
                    q.BoundBy = sorted;
                    break;
            }
        }

        ShowBoundSensitivity(built);
    }

    /// <summary>
    /// The entity checks a binding lists (next-phase-3-design.md section 3.1, decision 16), each lowered through the binding's field map
    /// onto its write table and added to that table's checks after its own, named by the naming convention from the check's name, with
    /// <see cref="RMemberOrigin.Source"/> <c>binding</c>; each check lists the binding. A check whose paths the binding does not map to
    /// varying columns of its write table, or a binding without one, adds nothing (MQ3110), nor does one holding a code its column cannot
    /// store (MQ3110 here too); on a dialect other than PostgreSQL the DDL leaves it out (MQ4056).
    /// </summary>
    private void AddBindingChecks(REntity entity, EntityBinding file, int index, REntityBinding binding)
    {
        if (file.Checks.Count == 0)
            return;
        var added = new List<RCheck>();
        for (var k = 0; k < file.Checks.Count; k++)
        {
            var id = file.Checks[k];
            if (entity.Checks.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal)) is not { } check)
                continue;
            if (!check.ListedBy.Contains(binding))
                check.ListedBy.Add(binding);
            if (binding.WriteTable is not { } table || check.Condition?.Root is not { } source)
                continue;
            // A check is lowered into one table: one that reads what a table above the binding's own stores (table per type) adds nothing,
            // and the validator reports it from the files (MQ4135, ExpressionRules).
            if (binding.Bases.Count > 0 && binding.Bases[0].Binding.Entity is { } above
                && ExprResolution.Paths(source.Typed).Any(p => above.Attributes.Any(a => a.Id == p.Split('.')[0])))
                continue;

            var columns = new Dictionary<string, RColumn>(StringComparer.Ordinal);
            foreach (var path in ExprResolution.Paths(source.Typed))
            {
                var field = binding.Fields.FirstOrDefault(f => string.Equals(f.AttributeRef, path, StringComparison.Ordinal));
                if (field?.WriteColumn is { } column && !ExprResolution.ColumnOf(column).FixedLength)
                    columns[path] = column;
            }

            var name = Render(_conv.CheckName, ("table", table.Name), ("name", check.Name));
            if (LowerCheck(source, columns, $"Check '{name}' that the binding of entity '{entity.Name}' writes into table '{table.Name}' of database '{_db.Name}'",
                entity.Id, Pointer(index) + "/checks/" + k.ToString(CultureInfo.InvariantCulture), misfit: "MQ3110") is not { } lowered)
                continue;
            added.Add(new RCheck
            {
                Id = check.Id,
                Name = name,
                Expression = lowered.Sql,
                Condition = lowered.Condition,
                Shape = lowered.Shape,
                Properties = check.Properties,
                MemberOrigin = RMemberOrigin.Binding,
            });
        }

        if (added.Count > 0 && binding.WriteTable is { } written)
            written.Checks = [.. written.Checks, .. added];
    }

    /// <summary>
    /// A column that stores no attribute (a designed, imported, extra or inherited column) and states no sensitivity shows the strongest own
    /// value of the attributes the bindings' fields map to it, read or written (next-phase-2-design.md section 3.2, decision 9). Attributes
    /// still hold their own values here: they show the columns' values only in the pass after every database
    /// (<see cref="ResolveRun"/>'s <c>ResolveShared</c>), so neither side reads what the other borrowed. The table already depends on each
    /// entity bound to it.
    /// </summary>
    private static void ShowBoundSensitivity(List<REntityBinding> bindings)
    {
        foreach (var binding in bindings)
        {
            foreach (var field in binding.Fields)
            {
                if ((field.Member ?? field.Attribute)?.Sensitive is not { } value)
                    continue;
                foreach (var column in new[] { field.SourceColumn, field.WriteColumn })
                {
                    if (column is { Attribute: null, OwnSensitive: null })
                        column.Sensitive = ResolveRun.Stronger(column.Sensitive, value);
                }
            }
        }
    }

    /// <summary>The resolution of one binding.</summary>
    private sealed class BindingBuilder(
        DatabaseRun run,
        REntity entity,
        EntityBinding file,
        int index,
        Dictionary<string, TableBuild> tables,
        Dictionary<string, RView> views,
        Dictionary<string, RQuery> queries)
    {
        private readonly string _at = Pointer(index);
        private readonly REntityBinding _b = new() { Id = file.Id, Definition = file, Index = index, DiscriminatorValue = ResolutionValues.Plain(file.DiscriminatorValue) };
        private DependencySet _deps = null!;

        public REntityBinding Build()
        {
            _b.Entity = entity;
            _b.Database = run._rdb;
            _deps = new DependencySet(run._run.Keys).Element(run._db.Id);
            for (var e = entity; e is not null; e = e.Base)
                _deps.Element(e.Id);
            _b.Description = file.Description is { } description
                ? description.Text ?? (description.File is null ? null : run._run.Model.GetDocument(entity.Id)?.SidecarText)
                : null;
            _b.Tags = [.. file.Tags];
            _b.Properties = ResolutionValues.PlainMap(file.Properties);

            ResolveSource();
            ResolveWrite();
            ResolveBase();
            var fields = ResolveFields();
            ResolveTypeColumn();
            ResolveBaseTypeColumns();
            var constants = ResolveConstants();
            var listed = ResolveListedColumns();
            ResolveDelete();
            ResolveKey(fields);
            ResolveWriteFlags(fields, constants, listed);
            CheckQueryParameters(fields);
            _b.Fields = fields;
            _b.Constants = constants;
            // The key a base foreign key gives is not in the field map, but its columns are accounted for as the key fields'.
            List<RBindingField> accounted = _baseKey.Count == 0 ? fields : [.. fields, .. _baseKey];
            _b.Columns = Account(SourceColumns(), accounted, constants, listed, write: _b.WriteTable is not null && ReferenceEquals(_b.WriteTable, _b.SourceTable));
            _b.WriteColumns = _b.WriteTable is { } writeTable && !ReferenceEquals(writeTable, _b.SourceTable)
                ? Account([.. writeTable.Columns.Select(c => (c.Name, (RColumn?)c, (string?)c.Type))], accounted, constants, listed, write: true)
                : [];
            ReportUnaccounted();
            ReportUnmapped();
            ReportDrift();
            _b.Dependencies = _deps.ToList();
            return _b;
        }

        private void Report(string rule, string message, string pointer) => run._run.AddDiagnostic(rule, message, entity.Id, _at + pointer);

        // ---- source and write table ------------------------------------------------------------------------------------------

        private void ResolveSource()
        {
            var key = file.Source;
            if (FindTable(key) is { } t)
            {
                _b.SourceKind = "table";
                _b.SourceTable = t.Table;
                _b.SourceName = t.Table.Name;
                _deps.AddRange(t.Deps.ToList());
            }
            else if (views.TryGetValue(key, out var view))
            {
                _b.SourceKind = "view";
                _b.SourceView = view;
                _b.SourceName = view.Name;
                _deps.AddRange(view.Dependencies);
            }
            else if (queries.TryGetValue(key, out var query) && query.Form == "sql" && (query.Writes.Count > 0 || query.Select.Count == 0))
            {
                // A query written as SQL text that changes data or returns no rows is no source (sql-queries-design.md decision 15).
                Report("MQ4044", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' reads query '{query.Name}', which is written as SQL text and {(query.Writes.Count > 0 ? "writes tables" : "declares no columns")}: an entity reads only through a query that returns rows and changes none.", "/source");
                _b.SourceName = query.Name;
                _deps.AddRange(query.Dependencies);
            }
            else if (query is not null)
            {
                if (query.Form == "sql" && run._dialect == "sqlserver")
                    CheckNestable(query);
                _b.SourceKind = "query";
                _b.SourceQuery = query;
                _b.SourceName = query.Name;
                _deps.AddRange(query.Dependencies);
            }
            else
            {
                Report("MQ4044", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' reads '{key}', which {WhatIs(key, tablesOnly: false)}.", "/source");
                _b.SourceName = key;
            }
        }

        /// <summary>
        /// MQ4097: SQL Server takes a derived table that neither starts with WITH nor ends with an ORDER BY outside parentheses without
        /// OFFSET, FETCH or TOP; the binding's select wraps the query's text in one (sql-queries-design.md decision 15).
        /// </summary>
        private void CheckNestable(RQuery query)
        {
            if (query.SqlText is not { } text)
                return;
            var scan = Rendering.SqlText.Scan(QuerySql.Statement(text), Dialect.SqlServer);
            var problem = scan.FirstWord == "WITH" ? "starts with WITH"
                : scan.OrderBy && !scan.OffsetFetchOrTop ? "has an ORDER BY outside parentheses with no OFFSET, FETCH or TOP"
                : null;
            if (problem is not null)
                Report("MQ4097", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' reads query '{query.Name}', whose SQL text {problem}, which SQL Server cannot nest as a derived table: rewrite the text, or read the query some other way.", "/source");
        }

        private TableBuild? FindTable(string key) =>
            tables.TryGetValue(key, out var t) ? t
            : tables.TryGetValue(key + "@" + run._db.Id, out t) ? t
            : null;

        private string WhatIs(string reference, bool tablesOnly)
        {
            var element = run._run.Model.Get<Element>(reference.Split('@')[0]);
            var other = element switch
            {
                Table t => t.Database,
                View v => v.Database,
                Query q => q.Database,
                _ => null,
            };
            if (other is not null && other != run._db.Id)
                return $"is {element!.KindName} '{element.Name}' of database '{run._run.Model.Get<Database>(other)?.Name ?? other}', not of '{run._db.Name}'";
            if (element is Entity e)
                return $"is entity '{e.Name}', which has no table in database '{run._db.Name}'";
            if (element is not null)
                return $"is {element.KindName} '{element.Name}', not a {(tablesOnly ? "table" : "table, view or query")}";
            return $"is no {(tablesOnly ? "table" : "table, view or query")} of database '{run._db.Name}'";
        }

        private void ResolveWrite()
        {
            if (file.Write is { None: true })
                return;
            if (file.Write?.Table is { } key)
            {
                if (FindTable(key) is { } t)
                {
                    _b.WriteTable = t.Table;
                    _b.Writes = true;
                    if (!ReferenceEquals(t.Table, _b.SourceTable))
                        _deps.AddRange(t.Deps.ToList());
                }
                else
                {
                    Report("MQ4044", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' writes '{key}', which {WhatIs(key, tablesOnly: true)}.", "/write/table");
                }

                return;
            }

            if (_b.SourceTable is { } source)
            {
                _b.WriteTable = source;
                _b.Writes = true;
            }
        }

        // ---- table per type --------------------------------------------------------------------------------------------------

        /// <summary>The binding of the base whose table <c>baseForeignKey</c> joins, once the key fits; else <see langword="null"/>.</summary>
        private REntityBinding? _base;

        /// <summary>The key <c>baseForeignKey</c> gives: one field per key field of the base's binding, on the foreign key's column.</summary>
        private readonly List<RBindingField> _baseKey = [];

        /// <summary>
        /// Table per type (retire-projection-design.md section 4): resolves <c>baseForeignKey</c> against the binding's write table (else its
        /// source) and the table of the binding of the entity's nearest bound base, and gives the binding its bases: that base, then the base's
        /// own. MQ4130 for an entity with no bound base, MQ4133 for a binding in its base's table, MQ4128 for a key that is not the table's or
        /// does not reference the base's table, MQ4129 for one whose columns are not the table's whole primary key or that does not reference
        /// the key of the base's binding, MQ4131 for a binding that writes while its base does not (it is then read only). A binding without
        /// the key in the table of a base that has bases shares them.
        /// </summary>
        private void ResolveBase()
        {
            var baseEntity = run.BoundBase(entity);
            if (file.BaseForeignKey is not { } foreignKeyId)
            {
                // A subtype in the table of a base joined to its own base: it shares the base's bases.
                if (baseEntity is not null && run.UpperLevels(entity).Count > 0 && run._resolvedBindings.TryGetValue(baseEntity.Id, out var shared))
                    _b.Bases = [.. shared.Bases.Select(l => Level(l.Binding, l.ForeignKey))];
                return;
            }

            const string at = "/baseForeignKey";
            var prefix = $"The binding of entity '{entity.Name}' to database '{run._db.Name}'";
            if (baseEntity is null)
            {
                Report("MQ4130", $"{prefix} names a base foreign key, but {(entity.Base is null ? "the entity has no base" : "no base of the entity has a binding to the database")}, so no base's table holds the rest of its rows: bind the base to its table, or remove baseForeignKey.", at);
                return;
            }

            var baseFile = run._bindingByEntity[baseEntity.Id].Binding;
            if (string.Equals(HostOf(file), HostOf(baseFile), StringComparison.Ordinal))
            {
                Report("MQ4133", $"{prefix} names a base foreign key, but it keeps its rows in '{HostName(file)}', the table of base '{baseEntity.Name}': a subtype in its base's table needs no join (its type code tells its rows apart), so remove baseForeignKey, or bind the entity to a table of its own.", at);
                return;
            }

            if (!run._resolvedBindings.TryGetValue(baseEntity.Id, out var baseBinding))
                return; // a cycle of bases, which the entity rules report
            var own = _b.WriteTable ?? _b.SourceTable;
            if (!run._foreignKeysById.TryGetValue(foreignKeyId, out var spec) || !spec.Resolved || own is null || !ReferenceEquals(spec.Host.Table, own))
            {
                var what = spec is { Resolved: true } ? $"is foreign key '{spec.Result.Name}' of table '{spec.Host.Table.Name}'" : $"is no foreign key of database '{run._db.Name}'";
                Report("MQ4128", own is null
                    ? $"{prefix} names base foreign key '{foreignKeyId}', but the binding reads {_b.SourceKind ?? "nothing"} '{_b.SourceName}' and writes no table, so no table of its own holds the key: read or write a table."
                    : $"{prefix} names base foreign key '{foreignKeyId}', which {what}, not of table '{own.Name}' the binding {(_b.WriteTable is null ? "reads" : "writes")}: name a foreign key of that table that references the base's table.", at);
                return;
            }

            var key = spec.Result;
            var baseTable = baseBinding.WriteTable ?? baseBinding.SourceTable;
            if (baseTable is not null && !ReferenceEquals(key.ReferencedTable, baseTable))
            {
                Report("MQ4128", $"{prefix} names base foreign key '{key.Name}', which references table '{key.ReferencedTable.Name}', but base '{baseEntity.Name}' keeps its rows in table '{baseTable.Name}': name a foreign key that references that table.", at);
                return;
            }

            var primary = own.PrimaryKey?.Columns ?? [];
            if (primary.Count != key.Columns.Count || !primary.All(c => key.Columns.Contains(c)))
            {
                Report("MQ4129", $"{prefix} names base foreign key '{key.Name}' on {Columns(key.Columns)}, but the primary key of table '{own.Name}' is {(primary.Count == 0 ? "missing" : Columns(primary))}: a row of the table is one row of the base's table, so the key's columns are the whole primary key.", at);
                return;
            }

            // The key, in the order of the base's key fields: each field's column in the base's table is one the foreign key references. A base
            // read from a view or a query has no table to reference, so the columns pair by name.
            var fields = new List<RBindingField>();
            foreach (var baseField in baseBinding.Key)
            {
                var referenced = baseTable is null ? null : baseField.WriteColumn is { } w && ReferenceEquals(w.Table, baseTable) ? w : baseField.SourceColumn;
                var j = -1;
                for (var k = 0; k < key.ReferencedColumns.Count && j < 0; k++)
                {
                    if (referenced is not null ? ReferenceEquals(key.ReferencedColumns[k], referenced) : string.Equals(key.ReferencedColumns[k].Name, baseField.ColumnName, StringComparison.Ordinal))
                        j = k;
                }

                if (j < 0)
                    break;
                fields.Add(KeyField(baseField, key.Columns[j]));
            }

            if (baseBinding.Key.Count == 0 || fields.Count != key.Columns.Count)
            {
                Report("MQ4129", $"{prefix} names base foreign key '{key.Name}', which references {Columns(key.ReferencedColumns)} of table '{key.ReferencedTable.Name}', but the key of base '{baseEntity.Name}' is {(baseBinding.Key.Count == 0 ? "not mapped there" : string.Join(", ", baseBinding.Key.Select(f => "'" + f.ColumnName + "'")))}: the key references the base's key columns.", at);
                return;
            }

            if (_b.Writes && !baseBinding.Writes)
            {
                Report("MQ4131", $"{prefix} writes, but the binding of base '{baseEntity.Name}' writes no table ({(baseBinding.SourceKind is "view" or "query" ? "it reads " + baseBinding.SourceKind + " '" + baseBinding.SourceName + "'" : "it writes none")}), so the base's row of an insert has nowhere to go: write \"none\", or let the base write its table.", "/write");
                _b.Writes = false;
                foreach (var field in fields)
                    field.InInsert = false;
            }

            // A hard delete through the base's binding removes the base's row alone: the database refuses it while this table's row
            // points at it, unless the key cascades.
            if (baseBinding.Delete == "key" && key.OnDelete != "cascade")
                Report("MQ4136", $"{prefix} names base foreign key '{key.Name}', which does not cascade on delete, while the binding of base '{baseEntity.Name}' deletes by key: deleting a {entity.Name} through the base fails, since its row in table '{own.Name}' still points at the base's row. Give the foreign key onDelete \"cascade\".", at);

            _base = baseBinding;
            _baseKey.AddRange(fields);
            _b.BaseForeignKey = key;
            _b.Bases = [Level(baseBinding, key), .. baseBinding.Bases.Select(l => Level(l.Binding, l.ForeignKey))];
            _deps.AddRange(baseBinding.Dependencies);

            static string Columns(IReadOnlyList<RColumn> columns) => string.Join(", ", columns.Select(c => "'" + c.Name + "'"));
        }

        private RBindingBase Level(REntityBinding binding, RForeignKey key) => new() { Derived = _b, Binding = binding, ForeignKey = key };

        /// <summary>A key field the base foreign key gives: the base's key field's name and attribute, on the foreign key's column.</summary>
        private RBindingField KeyField(RBindingField baseField, RColumn column)
        {
            var found = FindSourceColumn(column.Name);
            var write = _b.WriteTable is { } w && ReferenceEquals(column.Table, w) ? column : _b.WriteTable is { } other ? FindTableColumn(other, column.Name) : null;
            return new RBindingField
            {
                Name = baseField.Name,
                AttributeRef = baseField.AttributeRef,
                Attribute = entity.Attributes.FirstOrDefault(a => ReferenceEquals(a, baseField.Attribute) || a.Id == baseField.Attribute?.Id) ?? baseField.Attribute,
                Column = column.Key,
                ColumnName = found?.Name ?? column.Name,
                SourceColumn = found?.Table,
                ViewColumn = found?.View,
                QueryField = found?.Query,
                WriteColumn = write,
                IsKey = true,
                InInsert = _b.Writes && write is not null,
                Type = PhysicalType(column),
                NativeType = column.NativeType,
                Nullable = false,
            };
        }

        /// <summary>
        /// The type column of each table above the binding's own (table per type): the column the base's binding there names or inherits in
        /// that table, with the entity's code, and the codes of the entity and the entities derived from it that keep rows in the table. MQ4125
        /// for a code an earlier entity with rows in the table already has, or one the column cannot hold.
        /// </summary>
        private void ResolveBaseTypeColumns()
        {
            if (_b.Bases.Count == 0)
                return;
            var codeAt = file.DiscriminatorValue is not null ? "/discriminatorValue" : "";
            var value = run.CodeOf(entity);
            foreach (var level in _b.Bases)
            {
                var levelEntity = level.Binding.Entity;
                if (run.TypeColumnOwner(levelEntity) is not { } owner || level.Binding.DiscriminatorColumn is not { } column)
                    continue;
                _deps.Element(owner.Id);
                var host = HostOf(run._bindingByEntity[levelEntity.Id].Binding);
                var codes = new List<object> { value };
                var members = run.TypeColumnMembers(owner);
                foreach (var other in members)
                {
                    if (ReferenceEquals(other, entity) || !DerivesFrom(other, entity) || !run.KeepsRowsIn(other, host))
                        continue;
                    _deps.Element(other.Id);
                    var code = run.CodeOf(other);
                    if (!codes.Exists(c => SameCode(c, code)))
                        codes.Add(code);
                }

                level.DiscriminatorColumn = new RBindingDiscriminator
                {
                    Column = column.Column, ColumnName = column.ColumnName, SourceColumn = column.SourceColumn, ViewColumn = column.ViewColumn,
                    QueryField = column.QueryField, WriteColumn = column.WriteColumn, Base = owner, Value = value, Codes = codes,
                };
                if ((column.WriteColumn ?? column.SourceColumn) is { } fit)
                    CheckCodeFit(value, PhysicalType(fit), fit.Length, fit.Name, codeAt);
                foreach (var other in members)
                {
                    if (ReferenceEquals(other, entity))
                        break;
                    if (!run.KeepsRowsIn(other, host) || !SameCode(run.CodeOf(other), value))
                        continue;
                    Report("MQ4125", $"Entities '{other.Name}' and '{entity.Name}' share type column '{column.ColumnName}' in '{HostName(run._bindingByEntity[levelEntity.Id].Binding)}' and both have type code {QuotedCode(value)}, so reads cannot tell their rows apart: set a different discriminatorValue on one.", codeAt);
                    break;
                }
            }
        }

        /// <summary>
        /// MQ4132: whether a field of a binding joined to its base's table maps what the base's binding maps: an attribute the bound base has
        /// (its own or inherited), or the end of a to-one relation the base holds.
        /// </summary>
        private bool MapsInherited(RBindingField field, REntity baseEntity) =>
            field.End is { Opposite.Entity: { } holder } ? DerivesFrom(baseEntity, holder)
            : field.Attribute is { } attribute && baseEntity.Attributes.Any(a => a.Id == attribute.Id);

        // ---- columns ---------------------------------------------------------------------------------------------------------

        /// <summary>The source's columns as (name, table column, type keyword).</summary>
        private List<(string Name, RColumn? Column, string? Type)> SourceColumns() =>
            _b.SourceTable is { } t ? [.. t.Columns.Select(c => (c.Name, (RColumn?)c, (string?)c.Type))]
            : _b.SourceView is { } v ? [.. v.Columns.Select(c => (c.Name, (RColumn?)null, c.Type))]
            : _b.SourceQuery is { } q ? [.. q.Select.Select(f => (f.Name, (RColumn?)null, f.Type))]
            : [];

        /// <summary>A column of the source by physical name, column key or attribute id; else loosely by name, when only one matches.</summary>
        private (string Name, RColumn? Table, RViewColumn? View, RQueryField? Query, string? Type, bool Nullable)? FindSourceColumn(string name)
        {
            if (_b.SourceTable is { } t)
                return FindTableColumn(t, name) is { } c ? (c.Name, c, null, null, PhysicalType(c), c.Nullable) : null;
            if (_b.SourceView is { } v)
            {
                var column = v.Columns.FirstOrDefault(c => c.Name == name) ?? Loose(v.Columns, c => c.Name, name);
                return column is null ? null : (column.Name, null, column, null, column.Type, column.Nullable);
            }

            if (_b.SourceQuery is { } q)
            {
                var field = q.Select.FirstOrDefault(f => f.Name == name) ?? Loose(q.Select, f => f.Name, name);
                return field is null ? null : (field.Name, null, null, field, field.CodeType ?? field.Type, field.Nullable);
            }

            return null;
        }

        internal static RColumn? FindTableColumn(RTable table, string name) =>
            table.Columns.FirstOrDefault(c => c.Name == name)
            ?? table.Columns.FirstOrDefault(c => c.Key == name)
            ?? table.Columns.FirstOrDefault(c => c.Id == name)
            ?? table.Columns.FirstOrDefault(c => c.Attribute?.Id == name)
            ?? Loose(table.Columns, c => c.Name, name);

        /// <summary>The write table's column for a source column: the same column, else by key, then by name.</summary>
        private RColumn? WriteColumnFor(string written, string name, RColumn? sourceColumn)
        {
            if (_b.WriteTable is not { } w)
                return null;
            if (sourceColumn is not null && ReferenceEquals(sourceColumn.Table, w))
                return sourceColumn;
            return (sourceColumn is not null ? w.Columns.FirstOrDefault(c => c.Key == sourceColumn.Key) : null)
                ?? FindTableColumn(w, written)
                ?? FindTableColumn(w, name);
        }

        private static T? Loose<T>(IEnumerable<T> columns, Func<T, string> nameOf, string name) where T : class
        {
            var ignoringCase = columns.Where(c => string.Equals(nameOf(c), name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (ignoringCase.Count > 0)
                return ignoringCase.Count == 1 ? ignoringCase[0] : null;
            var folded = Fold(name);
            var matches = columns.Where(c => Fold(nameOf(c)) == folded).Take(2).ToList();
            return matches.Count == 1 ? matches[0] : null;

            static string Fold(string text) => text.Replace("_", "", StringComparison.Ordinal).ToUpperInvariant();
        }

        private static string PhysicalType(RColumn c) => c.Type == "reference" && c.CodeType is { } code ? code : c.Type;

        // ---- fields ----------------------------------------------------------------------------------------------------------

        private List<RBindingField> ResolveFields()
        {
            var fields = new List<RBindingField>();
            var attributes = new Dictionary<string, int>(StringComparer.Ordinal);
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            var perTypeBase = run.IsPerType(entity) ? run.BoundBase(entity) : null;
            for (var i = 0; i < file.Fields.Count; i++)
            {
                var f = file.Fields[i];
                var at = "/fields/" + i.ToString(CultureInfo.InvariantCulture);
                var field = new RBindingField { AttributeRef = f.Attribute, Column = f.Column };
                if (!Target(field, f.Attribute))
                {
                    // Every field form that names a relation end resolves or gets MQ4045 (several-types-design.md decision 46).
                    Report("MQ4045", $"Field {i.ToString(CultureInfo.InvariantCulture)} of the binding of entity '{entity.Name}' to database '{run._db.Name}' names '{f.Attribute}', which is neither an attribute of the entity, a member of one of its value object attributes, nor the end of a to-one relation of the entity.", at + "/attribute");
                }
                else if (!attributes.TryAdd(f.Attribute, i))
                    Report("MQ4045", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' maps '{field.Name}' twice (fields {attributes[f.Attribute].ToString(CultureInfo.InvariantCulture)} and {i.ToString(CultureInfo.InvariantCulture)}).", at + "/attribute");
                else if (perTypeBase is not null && MapsInherited(field, perTypeBase))
                    Report("MQ4132", $"Field '{field.Name}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' maps {(field.End is null ? "an attribute" : "the key of a relation end")} that base '{perTypeBase.Name}' has, but the binding joins the base's table by its base foreign key, so the base's binding maps it there: remove the field.", at + "/attribute");

                if (FindSourceColumn(f.Column) is { } column)
                {
                    (field.ColumnName, field.SourceColumn, field.ViewColumn, field.QueryField) = (column.Name, column.Table, column.View, column.Query);
                    field.Type = column.Type;
                    field.NativeType = column.Table?.NativeType ?? column.View?.NativeType ?? column.Query?.NativeType;
                    field.Nullable = column.Nullable;
                    if (!columns.TryAdd(column.Name, i))
                        Report("MQ4045", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' maps column '{column.Name}' twice (fields {columns[column.Name].ToString(CultureInfo.InvariantCulture)} and {i.ToString(CultureInfo.InvariantCulture)}).", at + "/column");
                }
                else
                {
                    field.ColumnName = f.Column;
                    if (_b.SourceKind is not null)
                        Report("MQ4045", $"Field '{field.Name}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' reads column '{f.Column}', which {_b.SourceKind} '{_b.SourceName}' does not have.", at + "/column");
                }

                field.WriteColumn = WriteColumnFor(f.Column, field.ColumnName, field.SourceColumn);
                if (field.End is not null)
                    CheckAgainstStorage(field, at);
                fields.Add(field);
            }

            return fields;
        }

        /// <summary>Fills what a field's <c>attribute</c> names; false when it names nothing of the entity.</summary>
        private bool Target(RBindingField field, string reference)
        {
            if (entity.Attributes.FirstOrDefault(a => a.Id == reference) is { } attribute)
            {
                field.Attribute = attribute;
                field.Name = attribute.Name;
                return true;
            }

            var dot = reference.IndexOf('.', StringComparison.Ordinal);
            if (dot > 0 && entity.Attributes.FirstOrDefault(a => a.Id == reference[..dot]) is { } owner)
            {
                var memberId = reference[(dot + 1)..];
                var member = owner.Type is { Kind: "value-object", ValueObject: { } vo } && !owner.Collection ? vo.Attributes.FirstOrDefault(m => m.Id == memberId) : null;
                if (member is null || member.Collection || member.Type.Kind == "value-object")
                {
                    field.Name = reference;
                    return false;
                }

                _deps.Element(owner.Type.ValueObject!.Id);
                field.Attribute = owner;
                field.Member = member;
                field.Name = owner.Name + Casing.Pascal(member.Name);
                return true;
            }

            // An end the entity holds: the entity is covered by the other end, and the end is to-one (several-types-design.md decision 46).
            // <endId> maps its key or id column; on an end with several types, <endId>.type its type column and <endId>.<entityId> its key
            // column for that type.
            var (endId, form) = dot > 0 ? (reference[..dot], reference[(dot + 1)..]) : (reference, null);
            foreach (var (relation, end) in run._run.RelationsWithEnd(endId))
            {
                if (relation.Ends.Count != 2 || end.IsMany || end.Opposite is not { } opposite || !HoldsEnd(relation, opposite))
                    continue;
                REntity? keyOf = null;
                if (form is not null && (!end.HasSeveralTypes || (form != "type" && (keyOf = CoveredBy(relation, end, form)) is null)))
                    continue;
                _deps.Element(relation.Id);
                if (opposite.HasSeveralTypes)
                    _deps.Add(EndCoverage.TypesKey(opposite.Id));
                field.End = end;
                field.Navigation = run._run.InheritedNavigations(entity.Id).FirstOrDefault(n => ReferenceEquals(n.To, end));
                var stem = field.Navigation?.Name;
                (field.Name, field.Holds, field.KeyOf) = form switch
                {
                    null => (stem is not null ? stem + "Id" : Casing.Camel(end.Role + "Id"), end.HasSeveralTypes ? "id" : null, null),
                    "type" => (stem is not null ? stem + "Type" : Casing.Camel(end.Role + "Type"), "type", null),
                    _ => (stem is not null ? stem + Casing.Pascal(keyOf!.Name) + "Id" : Casing.Camel(end.Role + Casing.Pascal(keyOf!.Name) + "Id"), "key", keyOf),
                };
                return true;
            }

            field.Name = reference;
            return false;
        }

        /// <summary>Whether the entity holds an end through its opposite: it is (or derives from) the opposite's entity, or the opposite covers it.</summary>
        private bool HoldsEnd(RRelation relation, REnd opposite)
        {
            if (opposite.Entity is { } from)
                return IsSelfOrBase(from);
            return run._run.RelationSource(relation.Id)?.Ends.FirstOrDefault(e => e.Id == opposite.Id) is { } source
                && EndCoverage.Covers(run._run.Model, source, entity.Id);
        }

        /// <summary>The entity an end with several types covers, by id, or <see langword="null"/>.</summary>
        private REntity? CoveredBy(RRelation relation, REnd end, string entityId) =>
            run._run.RelationSource(relation.Id)?.Ends.FirstOrDefault(e => e.Id == end.Id) is { } source && EndCoverage.Covers(run._run.Model, source, entityId)
                ? run._run.EntityById(entityId)
                : null;

        /// <summary>
        /// MQ4117 for a field that names an end of a relation with an end of several types stored in this database (decision 46): it maps
        /// another column than the mapping names for it, or <c>&lt;endId&gt;.&lt;entityId&gt;</c> names an entity that isn't a target there,
        /// or whose pointer has several columns. With no such mapping the field stands alone.
        /// </summary>
        private void CheckAgainstStorage(RBindingField field, string at)
        {
            var end = field.End!;
            if (run._typeStorage.Find(x => x.Relation.Ends.Contains(end)) is not { Links: { } links })
                return;
            var i = ReferenceEquals(links.Ends[0].End, end) ? 0 : 1;
            var table = _b.WriteTable ?? _b.SourceTable;
            var pairs = links.Pairs.Where(p => p.Result is not null && !p.Ends[i].IsHolder && ReferenceEquals(p.Table.Table, table)
                && ReferenceEquals(p.Ends[1 - i].Target.Entity, entity)).ToList();
            var mapping = links.Mapping;
            if (field.Holds == "key")
            {
                var pair = pairs.Find(p => ReferenceEquals(p.Ends[i].Target.Entity, field.KeyOf));
                if (pair is null)
                {
                    if (!links.Ends[i].Targets.Exists(t => ReferenceEquals(t.Entity, field.KeyOf)))
                        Report("MQ4117", $"Field '{field.Name}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' names type '{field.KeyOf!.Name}' of end '{end.Role}', which isn't a target there: it isn't placed in the database, or its rows are in a covered base's table.", at + "/attribute");
                    return;
                }

                if (pair.Ends[i].Result!.Columns.Count > 1)
                {
                    Report("MQ4117", $"Field '{field.Name}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' maps one column, but the key of type '{field.KeyOf!.Name}' of end '{end.Role}' has {pair.Ends[i].Result!.Columns.Count.ToString(CultureInfo.InvariantCulture)}; list its columns in the binding's 'columns' instead.", at + "/attribute");
                    return;
                }

                pairs = [pair];
            }

            var named = pairs.Select(p => field.Holds == "type" ? p.Ends[i].Result!.TypeColumn : p.Ends[i].Result!.Columns.Count == 1 ? p.Ends[i].Result!.Columns[0] : null)
                .OfType<RColumn>().Distinct().ToList();
            var column = field.SourceColumn ?? field.WriteColumn;
            if (named.Count == 1 && column is not null && !ReferenceEquals(named[0], column) && !ReferenceEquals(named[0], field.WriteColumn))
            {
                Report("MQ4117", $"Field '{field.Name}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' maps column '{column.Name}', but mapping '{mapping.Name}' names column '{named[0].Name}' of table '{named[0].Table.Name}' for it.", at + "/column");
            }
        }

        private bool IsSelfOrBase(REntity other)
        {
            for (var e = entity; e is not null; e = e.Base)
            {
                if (ReferenceEquals(e, other))
                    return true;
            }

            return false;
        }

        // ---- constants and listed columns ------------------------------------------------------------------------------------

        private List<RBindingConstant> ResolveConstants()
        {
            var constants = new List<RBindingConstant>();
            for (var i = 0; i < file.Constants.Count; i++)
            {
                var c = file.Constants[i];
                var at = "/constants/" + i.ToString(CultureInfo.InvariantCulture);
                var constant = new RBindingConstant { Column = c.Column, ColumnName = c.Column, Value = ResolutionValues.Plain(c.Value) };
                var found = FindSourceColumn(c.Column);
                if (found is { } column)
                {
                    (constant.ColumnName, constant.SourceColumn, constant.ViewColumn, constant.QueryField) = (column.Name, column.Table, column.View, column.Query);
                    CheckFit(constant.Value, column.Type, column.Table?.Length, column.Nullable, column.Name, at + "/value");
                }
                else if (_b.SourceKind is not null)
                {
                    Report("MQ4048", $"Constant column '{c.Column}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' is not a column of {_b.SourceKind} '{_b.SourceName}', so it cannot filter what the binding reads.", at + "/column");
                }

                constant.WriteColumn = WriteColumnFor(c.Column, constant.ColumnName, constant.SourceColumn);
                if (_b.DiscriminatorColumn is { } typeColumn
                    && ((found is not null && HasSourceColumn(typeColumn) && constant.ColumnName == typeColumn.ColumnName)
                        || (constant.WriteColumn is not null && ReferenceEquals(constant.WriteColumn, typeColumn.WriteColumn))))
                {
                    var whose = ReferenceEquals(typeColumn.Base, entity) ? "the type column the binding names" : $"the type column the binding inherits from base '{typeColumn.Base.Name}'";
                    Report("MQ4124", $"Constant column '{constant.ColumnName}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' is {whose}, which already holds the entity's type code: remove the constant, and set discriminatorValue if the code should be another.", at + "/column");
                }

                if (_b.Writes && constant.WriteColumn is null && (found is not null || _b.SourceKind is null))
                {
                    Report("MQ4049", $"Constant column '{c.Column}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' is not a column of the table it writes ('{_b.WriteTable!.Name}'), so an insert cannot set it: name a column of that table, or write nowhere (write \"none\").", at + "/column");
                }
                else if (constant.WriteColumn is { } writeColumn && !ReferenceEquals(writeColumn, constant.SourceColumn))
                {
                    CheckFit(constant.Value, PhysicalType(writeColumn), writeColumn.Length, writeColumn.Nullable, writeColumn.Name, at + "/value");
                }

                constants.Add(constant);
            }

            return constants;
        }

        private static bool HasSourceColumn(RBindingDiscriminator d) => (d.SourceColumn ?? (object?)d.ViewColumn ?? d.QueryField) is not null;

        // ---- type column -----------------------------------------------------------------------------------------------------

        /// <summary>
        /// The type column the entity shares with its base or the entities derived from it in one table: the column the binding's
        /// <c>discriminatorColumn</c> names, else the one its nearest base's binding to the database names, resolved against this binding's
        /// source and write table like a constant (MQ4122 when either lacks it). The insert writes the entity's type code; the reads, updates
        /// and deletes filter on its code and those of the entities derived from it that share the column in the same table, so the entity
        /// that names the column reaches every row of its hierarchy there and none of another entity's. MQ4123 for a column no derived
        /// entity shares in the same table, MQ4125 for a code another entity sharing the column in the same table already has, or one the
        /// column cannot hold, MQ4126 for a binding that names the column a base's binding in the same table already names.
        /// </summary>
        private void ResolveTypeColumn()
        {
            if (run.TypeColumnOwner(entity) is not { } owner)
                return;
            var own = ReferenceEquals(owner, entity);
            if (!own && file.DiscriminatorColumn is not null)
                Report("MQ4126", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' names type column '{file.DiscriminatorColumn}', but it keeps its rows in the table of base '{owner.Name}', whose binding already names the type column: remove discriminatorColumn, since the column is inherited.", "/discriminatorColumn");
            var text = run._bindingByEntity[owner.Id].Binding.DiscriminatorColumn!;
            var d = new RBindingDiscriminator { Column = text, ColumnName = text, Base = owner, Value = run.CodeOf(entity) };
            _b.DiscriminatorColumn = d;
            var named = own ? $"names type column '{text}'" : $"inherits type column '{text}' from the binding of base '{owner.Name}'";
            var codeAt = file.DiscriminatorValue is not null ? "/discriminatorValue" : "";
            // A derived binding that reads (or writes) what the base's binding reads (or writes) leaves a missing column to the base's report.
            var ownerFile = run._bindingByEntity[owner.Id].Binding;
            var found = FindSourceColumn(text);
            if (found is { } column)
            {
                (d.ColumnName, d.SourceColumn, d.ViewColumn, d.QueryField) = (column.Name, column.Table, column.View, column.Query);
                CheckCodeFit(d.Value, column.Type, column.Table?.Length, column.Name, codeAt);
            }
            else if (_b.SourceKind is not null && (own || !string.Equals(KeyOf(file.Source), KeyOf(ownerFile.Source), StringComparison.Ordinal)))
            {
                Report("MQ4122", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' {named}, which {_b.SourceKind} '{_b.SourceName}' does not have, so its reads cannot tell the types' rows apart.", own ? "/discriminatorColumn" : "/source");
            }

            d.WriteColumn = WriteColumnFor(text, d.ColumnName, d.SourceColumn);
            if (_b.Writes && d.WriteColumn is null && (found is not null || _b.SourceKind is null)
                && (own || !string.Equals(HostOf(file), HostOf(ownerFile), StringComparison.Ordinal)))
            {
                Report("MQ4122", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' {named}, which is not a column of the table it writes ('{_b.WriteTable!.Name}'), so an insert cannot write its type code: name a column of that table, or write nowhere (write \"none\").",
                    own ? "/discriminatorColumn" : file.Write?.Table is not null ? "/write/table" : "/source");
            }
            else if (d.WriteColumn is { } writeColumn && !ReferenceEquals(writeColumn, d.SourceColumn))
            {
                CheckCodeFit(d.Value, PhysicalType(writeColumn), writeColumn.Length, writeColumn.Name, codeAt);
            }

            // The entities that share the column in the same table: an entity reads its own rows and those of the entities derived from it
            // (for the entity that names the column, every member of its hierarchy there), and never the rows of another entity.
            var host = HostOf(file);
            var codes = new List<object> { d.Value };
            var members = run.TypeColumnMembers(owner);
            foreach (var other in members)
            {
                if (ReferenceEquals(other, entity) || !DerivesFrom(other, entity) || !run.KeepsRowsIn(other, host))
                    continue;
                _deps.Element(other.Id);
                var code = run.CodeOf(other);
                if (!codes.Exists(c => SameCode(c, code)))
                    codes.Add(code);
            }

            d.Codes = codes;
            if (own && !members.Exists(o => !ReferenceEquals(o, entity) && run.KeepsRowsIn(o, host)))
                Report("MQ4123", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' names type column '{text}', but no entity derived from '{entity.Name}' has a binding to the database that shares it in the same table, so the column tells no rows apart: remove discriminatorColumn, or bind the derived entities to that table.", "/discriminatorColumn");

            // Two entities that share the column in one table with one code: reported on the later one, in entity order.
            foreach (var other in members)
            {
                if (ReferenceEquals(other, entity))
                    break;
                if (!run.KeepsRowsIn(other, host) || !SameCode(run.CodeOf(other), d.Value))
                    continue;
                Report("MQ4125", $"Entities '{other.Name}' and '{entity.Name}' share type column '{d.ColumnName}' in '{HostName(file)}' and both have type code {QuotedCode(d.Value)}, so reads cannot tell their rows apart: set a different discriminatorValue on one.", codeAt);
                break;
            }
        }

        /// <summary>What holds a binding's rows, by key or id: the table it writes, else (writing nowhere) its source.</summary>
        private string HostOf(EntityBinding binding) => run.BindingHostKey(binding);

        /// <summary>A table's key for any way a binding names it; another source's id as written.</summary>
        private string KeyOf(string key) => FindTable(key)?.Table.Key ?? key;

        private string HostName(EntityBinding binding)
        {
            var key = HostKey(binding);
            return FindTable(key)?.Table.Name ?? (views.TryGetValue(key, out var v) ? v.Name : queries.TryGetValue(key, out var q) ? q.Name : key);
        }

        private static string HostKey(EntityBinding binding) => binding.Write is { None: true } ? binding.Source : binding.Write?.Table ?? binding.Source;

        private static string QuotedCode(object code) => code is string ? "'" + code + "'" : CodeText(code);

        /// <summary>MQ4125: a type code the type column cannot hold (type, length, range).</summary>
        private void CheckCodeFit(object value, string? type, int? length, string column, string pointer)
        {
            if (type is not null && FitProblem(value, type, length, nullable: true) is { } problem)
                Report("MQ4125", $"The type code {QuotedCode(value)} of entity '{entity.Name}' in database '{run._db.Name}' {problem} (type column '{column}' is {type}): set a discriminatorValue the column can hold.", pointer);
        }

        /// <summary>MQ4051: a constant whose value does not fit its column (type, length, range, null).</summary>
        private void CheckFit(object? value, string? type, int? length, bool nullable, string column, string pointer)
        {
            if (type is not null && FitProblem(value, type, length, nullable) is { } problem)
                Report("MQ4051", $"The constant for column '{column}' of the binding of entity '{entity.Name}' to database '{run._db.Name}' {problem} (the column is {type}).", pointer);
        }

        private Dictionary<string, BindingColumnStatus> ResolveListedColumns()
        {
            var listed = new Dictionary<string, BindingColumnStatus>(StringComparer.Ordinal);
            for (var i = 0; i < file.Columns.Count; i++)
            {
                var c = file.Columns[i];
                var name = FindSourceColumn(c.Column)?.Name ?? (_b.WriteTable is { } w ? FindTableColumn(w, c.Column)?.Name : null);
                if (name is null)
                {
                    if (_b.SourceKind is not null)
                        Report("MQ4045", $"Column '{c.Column}' listed by the binding of entity '{entity.Name}' to database '{run._db.Name}' is not a column of {_b.SourceKind} '{_b.SourceName}'{(_b.WriteTable is { } wt && !ReferenceEquals(wt, _b.SourceTable) ? " nor of table '" + wt.Name + "'" : "")}.", "/columns/" + i.ToString(CultureInfo.InvariantCulture) + "/column");
                    continue;
                }

                listed.TryAdd(name, c.Status);
            }

            return listed;
        }

        // ---- delete, key, write flags ----------------------------------------------------------------------------------------

        /// <summary>
        /// The delete of a binding joined to its base's table: its own <c>delete</c>, absent following the base's, and agreeing with it (MQ4134):
        /// under a base that deletes softly, a binding that writes deletes softly too (an explicit <c>key</c> or <c>none</c> is refused). A soft
        /// delete is the root's: it marks the root's row, so a <c>soft</c> entry names the root's column. Whatever the binding writes or deletes,
        /// its reads leave out the rows the root's soft delete marked: <see cref="REntityBinding.SoftDeleteColumn"/> and its value are the root's.
        /// </summary>
        private void ResolveDeleteFromBase(REntityBinding baseBinding)
        {
            var prefix = $"The binding of entity '{entity.Name}' to database '{run._db.Name}'";
            var baseName = baseBinding.Entity.Name;
            // The root's soft delete reaches every binding below it, for its reads (a base below the root carries it on).
            var column = baseBinding.SoftDeleteColumn;
            (_b.SoftDeleteColumn, _b.SoftDeleteValue) = (column, column is null ? null : baseBinding.SoftDeleteValue);
            var soft = column is not null;
            switch (file.Delete?.Mode)
            {
                case null when _b.Writes:
                    _b.Delete = soft ? "soft" : baseBinding.Delete;
                    return;
                case BindingDeleteMode.Key or BindingDeleteMode.None when soft && _b.Writes:
                    Report("MQ4134", file.Delete!.Mode == BindingDeleteMode.Key
                        ? $"{prefix} deletes by key, but the binding of base '{baseName}' deletes softly: a delete by key would remove the base's row its soft delete keeps. Remove delete, so the binding follows its base."
                        : $"{prefix} never deletes, but the binding of base '{baseName}' deletes softly, so its rows could only be removed through the base. Remove delete, so the binding follows its base.", "/delete");
                    _b.Delete = file.Delete.Mode == BindingDeleteMode.Key ? "key" : "none";
                    return;
                case BindingDeleteMode.Key when _b.Writes:
                    _b.Delete = "key";
                    return;
                case BindingDeleteMode.Soft when !soft:
                    Report("MQ4134", $"{prefix} deletes softly, but the binding of base '{baseName}' {(baseBinding.Delete == "key" ? "deletes by key" : "never deletes")}: a soft delete marks the base's row, so the base's binding sets it. Delete softly there, or remove delete here.", "/delete");
                    _b.Delete = "none";
                    return;
                case BindingDeleteMode.Soft when _b.Writes:
                    if (file.Delete?.Soft is { } entry && !ReferenceEquals(FindTableColumn(column!.Table, entry.Column), column))
                        Report("MQ4134", $"{prefix} deletes softly by column '{entry.Column}', but the soft delete is the base's: the binding of base '{baseName}' sets column '{column.Name}' of table '{column.Table.Name}'. Remove delete, so the binding follows its base.", "/delete/soft/column");
                    _b.Delete = "soft";
                    return;
                default:
                    _b.Delete = "none";
                    return;
            }
        }

        private void ResolveDelete()
        {
            if (_base is { } baseBinding)
            {
                ResolveDeleteFromBase(baseBinding);
                return;
            }

            var mode = file.Delete?.Mode ?? (_b.Writes ? BindingDeleteMode.Key : BindingDeleteMode.None);
            switch (mode)
            {
                case BindingDeleteMode.Key when _b.WriteTable is not { PrimaryKey: not null }:
                    if (file.Delete is not null || _b.WriteTable is not null)
                    {
                        Report("MQ4053", _b.WriteTable is { } table
                            ? $"The binding of entity '{entity.Name}' to database '{run._db.Name}' deletes by key from table '{table.Name}', which has no primary key: declare one, delete softly, or delete \"none\"."
                            : $"The binding of entity '{entity.Name}' to database '{run._db.Name}' deletes by key but writes no table: name a write table, or delete \"none\".", "/delete");
                    }

                    _b.Delete = "none";
                    return;
                case BindingDeleteMode.Soft when file.Delete?.Soft is { } soft:
                    if (_b.WriteTable is not { } writeTable)
                    {
                        Report("MQ4053", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' deletes softly but writes no table: name a write table, or delete \"none\".", "/delete");
                        _b.Delete = "none";
                        return;
                    }

                    _b.SoftDeleteColumn = FindTableColumn(writeTable, soft.Column);
                    _b.SoftDeleteValue = ResolutionValues.Plain(soft.Value);
                    if (_b.SoftDeleteColumn is not { } column)
                    {
                        Report("MQ4045", $"The soft delete of the binding of entity '{entity.Name}' to database '{run._db.Name}' sets column '{soft.Column}', which table '{writeTable.Name}' does not have.", "/delete/soft/column");
                        _b.Delete = "none";
                        return;
                    }

                    if (_b.DiscriminatorColumn is { WriteColumn: { } typeColumn } && ReferenceEquals(typeColumn, column))
                        Report("MQ4124", $"The soft delete of the binding of entity '{entity.Name}' to database '{run._db.Name}' sets column '{column.Name}', the type column, which holds the entity's type code: a soft delete would overwrite the code. Mark deleted rows in another column.", "/delete/soft/column");
                    CheckFit(_b.SoftDeleteValue, PhysicalType(column), column.Length, column.Nullable, column.Name, "/delete/soft/value");
                    _b.Delete = "soft";
                    return;
                case BindingDeleteMode.Key:
                    _b.Delete = "key";
                    return;
                default:
                    _b.Delete = "none";
                    return;
            }
        }

        private void ResolveKey(List<RBindingField> fields)
        {
            // A base foreign key gives the key (MQ4128 to MQ4130 and MQ4133 when it doesn't): the binding maps no field for it.
            if (file.BaseForeignKey is not null)
            {
                _b.Key = [.. _baseKey];
                return;
            }

            var key = new List<RBindingField>();
            var missing = new List<string>();
            foreach (var attribute in entity.Key?.Attributes ?? RList<RAttribute>.Empty)
            {
                var field = fields.FirstOrDefault(f => ReferenceEquals(f.Attribute, attribute) && f.Member is null);
                if (field is null)
                {
                    missing.Add(attribute.Name);
                    continue;
                }

                field.IsKey = true;
                key.Add(field);
            }

            _b.Key = key;
            if (_b.Writes && missing.Count > 0)
                Report("MQ4046", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' writes but maps no field for the key {(missing.Count == 1 ? "attribute" : "attributes")} {string.Join(", ", missing.Select(m => "'" + m + "'"))}: map {(missing.Count == 1 ? "it" : "them")}, or write nowhere (write \"none\").", "/fields");
            if (_b.Writes && _b.WriteTable is { } writeTable && !ReferenceEquals(writeTable, _b.SourceTable))
            {
                foreach (var field in key.Where(f => f.WriteColumn is null))
                    Report("MQ4052", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' writes table '{writeTable.Name}', which has no column for key field '{field.Name}' (source column '{field.ColumnName}'): writes find their row by key.", "/write/table");
            }
        }

        /// <summary>
        /// Which fields the insert and the update write. A field whose column a constant or the type column writes is read only: that
        /// value is written once, and the update finds its row by it, so the statements never name the column twice.
        /// </summary>
        private void ResolveWriteFlags(List<RBindingField> fields, List<RBindingConstant> constants, Dictionary<string, BindingColumnStatus> listed)
        {
            var generated = new List<RBindingField>();
            foreach (var field in fields)
            {
                var column = field.WriteColumn;
                var marked = listed.TryGetValue(field.ColumnName, out var status) && status is BindingColumnStatus.Database or BindingColumnStatus.Computed
                    || (column is not null && listed.TryGetValue(column.Name, out status) && status is BindingColumnStatus.Database or BindingColumnStatus.Computed);
                field.IsGenerated = marked || (column is not null && (column.Identity || column.Computed is not null || (column.Sequence is not null && column.IsPrimaryKey)));
                field.InInsert = _b.Writes && column is not null && !field.IsGenerated && field.Attribute is not null;
                var target = field.Member ?? field.Attribute;
                field.InUpdate = field.InInsert && !field.IsKey && !(target is { ReadOnly: true } or { Immutable: true }) && !(field.Attribute is { ReadOnly: true } or { Immutable: true });
                if (field.End is not null)
                {
                    field.InInsert = _b.Writes && column is not null && !field.IsGenerated;
                    field.InUpdate = field.InInsert;
                }

                if (column is not null && (constants.Any(c => ReferenceEquals(c.WriteColumn, column)) || ReferenceEquals(_b.DiscriminatorColumn?.WriteColumn, column)))
                    (field.InInsert, field.InUpdate) = (false, false);

                if (field.IsKey && field.IsGenerated && _b.Writes)
                    generated.Add(field);
            }

            _b.Generated = generated;
        }

        /// <summary>
        /// MQ4121: a parameter of the source query with the name of one of the binding's fields (ignoring case, as many drivers match
        /// parameter names). The statements name their parameters after the fields, so the select by key would take one value for both,
        /// and code that passes a record's values by name would fill the query's parameter with the field.
        /// </summary>
        private void CheckQueryParameters(List<RBindingField> fields)
        {
            if (_b.SourceQuery is not { } query)
                return;
            foreach (var parameter in query.Parameters)
            {
                if (fields.FirstOrDefault(f => string.Equals(f.Name, parameter.Name, StringComparison.OrdinalIgnoreCase)) is { } field)
                    Report("MQ4121", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' reads query '{query.Name}', whose parameter '{parameter.Name}' has the name of the binding's field '{field.Name}': the statements name their parameters after the fields, so one name would carry two values. Rename the query's parameter.", "/source");
            }
        }

        // ---- accounting ------------------------------------------------------------------------------------------------------

        private List<RBindingColumn> Account(List<(string Name, RColumn? Column, string? Type)> columns, List<RBindingField> fields,
            List<RBindingConstant> constants, Dictionary<string, BindingColumnStatus> listed, bool write)
        {
            var result = new List<RBindingColumn>();
            foreach (var (name, column, _) in columns)
            {
                var entry = new RBindingColumn { Name = name, Column = column };
                var field = write && column is not null
                    ? fields.FirstOrDefault(f => ReferenceEquals(f.WriteColumn, column)) ?? fields.FirstOrDefault(f => f.ColumnName == name && f.WriteColumn is null)
                    : fields.FirstOrDefault(f => f.ColumnName == name);
                var constant = write && column is not null
                    ? constants.FirstOrDefault(c => ReferenceEquals(c.WriteColumn, column))
                    : constants.FirstOrDefault(c => c.ColumnName == name);
                if (field is not null)
                {
                    entry.Status = "field";
                    entry.Field = field;
                }
                else if (constant is not null)
                {
                    entry.Status = "constant";
                    entry.Constant = constant;
                }
                else if (_b.DiscriminatorColumn is { } d && (write && column is not null ? ReferenceEquals(d.WriteColumn, column) : HasSourceColumn(d) && d.ColumnName == name))
                {
                    entry.Status = "discriminator";
                }
                else if (listed.TryGetValue(name, out var status))
                {
                    entry.Status = ResolutionValues.Kebab(status);
                }
                else if (column is not null && run._relationAttributeColumns.TryGetValue(column, out var stored))
                {
                    // A relation attribute on the foreign key's table (retire-projection-design.md section 6): the relation's mapping stores it.
                    entry.Status = "relation";
                    _deps.Element(stored.Relation.Id).Element(stored.Mapping.Id);
                }
                else if (column is not null && ReferenceEquals(column, _b.SoftDeleteColumn))
                {
                    entry.Status = "soft-delete";
                }
                else if (column is not null && (column.Identity || (column.Sequence is not null && column.IsPrimaryKey)))
                {
                    entry.Status = "identity";
                }
                else if (column is not null && column.Computed is not null)
                {
                    entry.Status = "computed";
                }
                else if (column is not null && (column.Default is not null || column.DefaultSql is not null))
                {
                    entry.Status = "default";
                }

                result.Add(entry);
            }

            return result;
        }

        /// <summary>MQ4047: the owner's "a mapping for every column": a column nothing accounts for, once per table the binding uses.</summary>
        private void ReportUnaccounted()
        {
            foreach (var (columns, what) in new[] { (_b.Columns, $"{_b.SourceKind} '{_b.SourceName}'"), (_b.WriteColumns, $"table '{_b.WriteTable?.Name}'") })
            {
                var names = columns.Where(c => c.Status == "unaccounted").Select(c => c.Name).ToList();
                if (names.Count == 0)
                    continue;
                Report("MQ4047", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' accounts for no {(names.Count == 1 ? "column" : "columns")} {string.Join(", ", names.Select(n => "'" + n + "'"))} of {what}: map {(names.Count == 1 ? "it" : "each")} to a field, give {(names.Count == 1 ? "it" : "each")} a constant, or list {(names.Count == 1 ? "it" : "them")} in columns (ignored, database or computed).", "");
            }
        }

        /// <summary>
        /// MQ4058: the stored attributes of the entity that no field maps, so the entity reads them from nowhere, once per binding with
        /// the names (the editor's field map reports the same). An attribute counts when it is stored: own, inherited or a stereotype's
        /// virtual one, and not a collection or a derived attribute that is not stored (a stored derivation counts). A value object
        /// attribute counts as each of its members a field can map. A field naming the attribute maps it, even when its column is
        /// wrong (MQ4045's). Every binding is checked, whatever it writes or deletes; the to-one relation ends a binding can map are
        /// not attributes and are left out. A source that is nothing of the database is MQ4044's, so it reports nothing here.
        /// </summary>
        private void ReportUnmapped()
        {
            if (_b.SourceKind is null)
                return;
            var mapped = file.Fields.Select(f => f.Attribute).ToHashSet(StringComparer.Ordinal);
            // What the tables above the binding's own store (table per type): the attributes of the nearest base whose table it joins.
            var above = run.UpperLevels(entity).FirstOrDefault() ?? (file.BaseForeignKey is not null ? run.BoundBase(entity) : null);
            var names = new List<string>();
            foreach (var attribute in entity.Attributes)
            {
                if (attribute.Collection || attribute.Derived is { Stored: false })
                    continue;
                if (above is not null && above.Attributes.Any(a => a.Id == attribute.Id))
                    continue;
                if (attribute.Type is { Kind: "value-object", ValueObject: { } valueObject })
                {
                    foreach (var member in valueObject.Attributes)
                    {
                        if (!member.Collection && member.Type.Kind != "value-object" && !mapped.Contains(attribute.Id + "." + member.Id))
                            names.Add(attribute.Name + Casing.Pascal(member.Name));
                    }
                }
                else if (!mapped.Contains(attribute.Id))
                {
                    names.Add(attribute.Name);
                }
            }

            if (names.Count > 0)
                Report("MQ4058", $"No field maps {string.Join(", ", names)}: the entity reads {(names.Count == 1 ? "it" : "them")} from nowhere.", "/fields");
        }

        /// <summary>
        /// MQ4088 and MQ4087 for a binding with provenance (next-phase-2-design.md section 3.3, decisions 5 and 6), from files alone
        /// (<see cref="BindingDrift"/>): MQ4088 when the source is not a table file or none of the recorded columns is one of its columns;
        /// else MQ4087, once for the binding, when a column a field maps was renamed or retyped since it was recorded. Added columns are
        /// MQ4047's (or accounted for), and a removed column a field still maps is MQ4045's. A source that is nothing of the database is
        /// MQ4044's, so it reports nothing here.
        /// </summary>
        private void ReportDrift()
        {
            // A source that resolves to nothing of the database is MQ4044's (decision 15).
            if (file.Provenance is not { } provenance || _b.SourceKind is null || run._run.EntitySource(entity.Id) is not { } source)
                return;
            var drift = BindingDrift.Of(run._run.Model, source, file);
            var prefix = $"The binding of entity '{entity.Name}' to database '{run._db.Name}'";
            if (drift.Source == DriftSource.NotTableFile)
            {
                Report("MQ4088", $"{prefix} records provenance, but its source {_b.SourceKind} '{_b.SourceName}' is not a designed or imported table file, the only source whose columns have ids: remove provenance, or re-sync the binding (resync-binding) once it reads a table file.", "/provenance");
                return;
            }

            if (drift.Source != DriftSource.TableFile)
                return;
            if (drift.Stale)
            {
                var count = provenance.Columns.Count;
                Report("MQ4088", $"{prefix} records provenance for {count.ToString(CultureInfo.InvariantCulture)} {(count == 1 ? "column" : "columns")}, none of which is a column of table '{drift.Table!.Name}', so it describes another source and is ignored: re-sync the binding (resync-binding), or remove provenance.", "/provenance");
                return;
            }

            if (drift.Renamed.Count == 0 && drift.Retyped.Count == 0)
                return;
            // One entry per mapped column and field, in the source's column order then field order.
            var order = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var i = 0; i < drift.Columns.Count; i++)
                order.TryAdd(drift.Columns[i].Id, i);
            var renamed = drift.Renamed.Select(c => (c.Column, c.Field)).ToHashSet();
            var retyped = drift.Retyped.Select(c => (c.Column, c.Field)).ToHashSet();
            var changes = drift.Renamed.Concat(drift.Retyped)
                .DistinctBy(c => (c.Column, c.Field))
                .OrderBy(c => order.GetValueOrDefault(c.Column))
                .ThenBy(c => c.Field)
                .Select(c =>
                {
                    var both = renamed.Contains((c.Column, c.Field)) && retyped.Contains((c.Column, c.Field));
                    var how = both ? "renamed and retyped" : renamed.Contains((c.Column, c.Field)) ? "renamed" : "retyped";
                    return $"'{c.Name}' {how} (attribute '{c.AttributeName}')";
                });
            Report("MQ4087", $"{prefix} maps columns that changed since its provenance was recorded: {string.Join(", ", changes)}. Re-sync the binding (resync-binding).", "");
        }
    }
}
