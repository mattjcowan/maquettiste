using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Entity bindings to one database (erratum E43, engine-design.md section 7, "Bindings and materialize"). An entity's binding is
/// collected before anything is placed, so the entity is never projected here; the tables, views and queries a binding names get the
/// entity's file among their dependency keys as they are created (they list the binding in <c>BoundBy</c>); and once the queries are
/// resolved each binding resolves its source, constants, field map, accounted columns, write table and delete, and the rules MQ4044 to
/// MQ4053 report what it gets wrong with the JSON pointer of the binding's entry in the entity's file.
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

    /// <summary>Adds the files of the entities bound to an object (its id or key) to its dependency keys: its <c>BoundBy</c> lists them.</summary>
    private void AddBinderKeys(DependencySet deps, string key)
    {
        if (_bindersBySource.Count == 0 || !_bindersBySource.TryGetValue(key, out var entities))
            return;
        foreach (var entityId in entities)
            deps.Element(entityId);
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
        foreach (var entity in _boundEntities)
        {
            _run.Ct.ThrowIfCancellationRequested();
            var (file, index) = _bindingByEntity[entity.Id];
            var binding = new BindingBuilder(this, entity, file, index, tables, viewsById, queriesById).Build();
            _run.SetEntityBinding(entity, _db.Name, binding);
            _run.Register(binding);
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
        private readonly REntityBinding _b = new() { Id = file.Id, Definition = file, Index = index };
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
            var fields = ResolveFields();
            var constants = ResolveConstants();
            var listed = ResolveListedColumns();
            ResolveDelete();
            ResolveKey(fields);
            ResolveWriteFlags(fields, listed);
            _b.Fields = fields;
            _b.Constants = constants;
            _b.Columns = Account(SourceColumns(), fields, constants, listed, write: _b.WriteTable is not null && ReferenceEquals(_b.WriteTable, _b.SourceTable));
            _b.WriteColumns = _b.WriteTable is { } writeTable && !ReferenceEquals(writeTable, _b.SourceTable)
                ? Account([.. writeTable.Columns.Select(c => (c.Name, (RColumn?)c, (string?)c.Type))], fields, constants, listed, write: true)
                : [];
            ReportUnaccounted();
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
            else if (queries.TryGetValue(key, out var query))
            {
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
            for (var i = 0; i < file.Fields.Count; i++)
            {
                var f = file.Fields[i];
                var at = "/fields/" + i.ToString(CultureInfo.InvariantCulture);
                var field = new RBindingField { AttributeRef = f.Attribute, Column = f.Column };
                if (!Target(field, f.Attribute))
                    Report("MQ4045", $"Field {i.ToString(CultureInfo.InvariantCulture)} of the binding of entity '{entity.Name}' to database '{run._db.Name}' names '{f.Attribute}', which is neither an attribute of the entity, a member of one of its value object attributes, nor the end of a to-one relation of the entity.", at + "/attribute");
                else if (!attributes.TryAdd(f.Attribute, i))
                    Report("MQ4045", $"The binding of entity '{entity.Name}' to database '{run._db.Name}' maps '{field.Name}' twice (fields {attributes[f.Attribute].ToString(CultureInfo.InvariantCulture)} and {i.ToString(CultureInfo.InvariantCulture)}).", at + "/attribute");

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

            foreach (var (relation, end) in run._run.RelationsWithEnd(reference))
            {
                if (relation.Ends.Count != 2 || end.IsMany || end.Opposite is not { } from || !IsSelfOrBase(from.Entity))
                    continue;
                _deps.Element(relation.Id);
                field.End = end;
                field.Navigation = run._run.InheritedNavigations(entity.Id).FirstOrDefault(n => ReferenceEquals(n.To, end));
                field.Name = field.Navigation is { } navigation ? navigation.Name + "Id" : Casing.Camel(end.Role + "Id");
                return true;
            }

            field.Name = reference;
            return false;
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

        /// <summary>MQ4051: a constant whose value does not fit its column (type, length, range, null).</summary>
        private void CheckFit(object? value, string? type, int? length, bool nullable, string column, string pointer)
        {
            if (type is null)
                return;
            string? problem = value switch
            {
                null => nullable ? null : "is NULL, and the column is not nullable",
                bool => type == "bool" ? null : "is a boolean",
                long l => type switch
                {
                    "int16" when l is < short.MinValue or > short.MaxValue => "does not fit a 16-bit integer",
                    "int32" when l is < int.MinValue or > int.MaxValue => "does not fit a 32-bit integer",
                    "int16" or "int32" or "int64" or "decimal" or "float" or "double" => null,
                    _ => "is a number",
                },
                decimal or double => type is "decimal" or "float" or "double" ? null : "is a number with a fraction",
                string s => type switch
                {
                    "int16" or "int32" or "int64" or "decimal" or "float" or "double" or "bool" => "is a text",
                    "string" when length is { } max && s.Length > max => $"is {s.Length.ToString(CultureInfo.InvariantCulture)} characters long, over the column's {max.ToString(CultureInfo.InvariantCulture)}",
                    _ => null,
                },
                _ => null,
            };
            if (problem is not null)
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

        private void ResolveDelete()
        {
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

        private void ResolveWriteFlags(List<RBindingField> fields, Dictionary<string, BindingColumnStatus> listed)
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

                if (field.IsKey && field.IsGenerated && _b.Writes)
                    generated.Add(field);
            }

            _b.Generated = generated;
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
                else if (listed.TryGetValue(name, out var status))
                {
                    entry.Status = ResolutionValues.Kebab(status);
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
    }
}
