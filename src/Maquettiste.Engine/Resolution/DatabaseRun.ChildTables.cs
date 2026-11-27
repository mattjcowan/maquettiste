using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Text;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The child tables of an entity binding (retire-projection-design.md section 5) and the member paths of any depth its fields walk (section 6).
/// An entry stores a list attribute, one row per item, or a single value object, at most one row, in a table of its own; the rows hold the owner's
/// key through the entry's owner foreign key. MQ4137 to MQ4141 report what an entry gets wrong, MQ4047 a child table column nothing accounts for,
/// and MQ4058 a member no field of the entry maps, each at the JSON pointer of the entry in the entity's file.
/// </summary>
internal sealed partial class DatabaseRun
{
    private sealed partial class BindingBuilder
    {
        /// <summary>
        /// The value object members a path of member ids walks below <paramref name="head"/>, outermost first; <see langword="null"/> when a
        /// segment names no member of the value object before it, or a segment other than the last reaches a list or a scalar. The head is a
        /// single value object, or with <paramref name="listHead"/> also a list of one (a child table's row is one item). A path that ends at a
        /// nested value object or a list maps a JSON column. Each value object walked joins the binding's dependencies.
        /// </summary>
        private List<RAttribute>? MemberChain(RAttribute head, IReadOnlyList<string> ids, bool listHead)
        {
            var chain = new List<RAttribute>(ids.Count);
            var walked = new List<string>(ids.Count);
            var current = head;
            for (var i = 0; i < ids.Count; i++)
            {
                if ((current.Collection && !(listHead && i == 0)) || current.Type is not { Kind: "value-object", ValueObject: { } valueObject })
                    return null;
                if (valueObject.Attributes.FirstOrDefault(m => m.Id == ids[i]) is not { } member)
                    return null;
                walked.Add(valueObject.Id);
                chain.Add(member);
                current = member;
            }

            foreach (var id in walked)
                _deps.Element(id);
            return chain;
        }

        /// <summary>The parameter a child table's insert writes a list item's position with.</summary>
        private const string PositionParameter = "position";

        /// <summary>
        /// Resolves each child table entry against the entity's attributes, the binding's database and its owning table (the write table, else the
        /// source table): MQ4137 for its attribute, MQ4138 for its table and owner foreign key, MQ4139 for the owner's columns, MQ4140 for its
        /// fields, constants and listed columns, MQ4141 for its order column, MQ4047 for a column nothing accounts for and MQ4058 for a member no
        /// field maps.
        /// </summary>
        private List<RBindingChildTable> ResolveChildTables(List<RBindingField> fields)
        {
            var result = new List<RBindingChildTable>(file.ChildTables.Count);
            var stored = new Dictionary<string, int>(StringComparer.Ordinal);
            var owning = _b.WriteTable ?? _b.SourceTable;
            var above = run.UpperLevels(entity);
            for (var i = 0; i < file.ChildTables.Count; i++)
            {
                var entry = file.ChildTables[i];
                var at = "/childTables/" + i.ToString(CultureInfo.InvariantCulture);
                var prefix = $"Child table {i.ToString(CultureInfo.InvariantCulture)} of the binding of entity '{entity.Name}' to database '{run._db.Name}'";
                var child = new RBindingChildTable { Id = entry.Id, Binding = _b, AttributeRef = entry.Attribute };
                result.Add(child);
                ResolveChildAttribute(child, entry, fields, stored, above, i, prefix, at);
                if (FindTable(entry.Table) is { } table)
                {
                    child.Table = table.Table;
                    if (!ReferenceEquals(table.Table, _b.SourceTable) && !ReferenceEquals(table.Table, _b.WriteTable))
                        _deps.AddRange(table.Deps.ToList());
                }
                else
                {
                    Report("MQ4138", $"{prefix} names table '{entry.Table}', which {WhatIs(entry.Table, tablesOnly: true)}.", at + "/table");
                }

                ResolveOwner(child, entry, owning, fields, prefix, at);
                var listed = new Dictionary<string, BindingColumnStatus>(StringComparer.Ordinal);
                if (child.Table is { } childTable)
                {
                    ResolveOrderColumn(child, entry, childTable, prefix, at);
                    child.Constants = ResolveChildConstants(entry, childTable, prefix, at);
                    for (var k = 0; k < entry.Columns.Count; k++)
                    {
                        if (FindTableColumn(childTable, entry.Columns[k].Column) is { } column)
                            listed.TryAdd(column.Name, entry.Columns[k].Status);
                        else
                            Report("MQ4140", $"{prefix} lists column '{entry.Columns[k].Column}', which child table '{childTable.Name}' does not have.", at + "/columns/" + k.ToString(CultureInfo.InvariantCulture) + "/column");
                    }
                }

                child.Fields = ResolveChildFields(child, entry, listed, prefix, at);
                CheckChildConstantColumns(child, prefix, at);
                CheckOwnerUpdates(child, prefix, at);
                CheckSingleRow(child, prefix, at);
                child.Writes = _b.Writes && child.Attribute is not null && child.Table is not null && child.OwnerForeignKey is not null
                    && child.Owner.All(o => o.Field is not null);
                if (child.Table is { } accounted)
                    AccountChild(child, accounted, listed, prefix, at);
                ReportChildUnmapped(child, entry, at);
            }

            return result;
        }

        /// <summary>MQ4137: the entry's attribute is the entity's (or inherited and not stored by the base's binding), a list or a value object, stored once, and no field maps it.</summary>
        private void ResolveChildAttribute(RBindingChildTable child, BindingChildTable entry, List<RBindingField> fields, Dictionary<string, int> stored,
            List<REntity> above, int index, string prefix, string at)
        {
            at += "/attribute";
            if (entity.Attributes.FirstOrDefault(a => a.Id == entry.Attribute) is not { } attribute)
            {
                Report("MQ4137", $"{prefix} stores '{entry.Attribute}', which is not an attribute of the entity.", at);
                return;
            }

            child.Attribute = attribute;
            child.IsList = attribute.Collection;
            if (attribute.Type.ValueObject is { } valueObject)
                _deps.Element(valueObject.Id);
            if (!attribute.Collection && attribute.Type.Kind != "value-object")
                Report("MQ4137", $"{prefix} stores attribute '{attribute.Name}', which is neither a list nor a value object: map it as a field.", at);
            else if (!stored.TryAdd(attribute.Id, index))
                Report("MQ4137", $"{prefix} stores attribute '{attribute.Name}', which child table {stored[attribute.Id].ToString(CultureInfo.InvariantCulture)} already stores: an attribute has one child table.", at);
            else if (fields.FirstOrDefault(f => f.End is null && f.Attribute?.Id == attribute.Id) is { } field)
                Report("MQ4137", $"{prefix} stores attribute '{attribute.Name}', which field '{field.Name}' of the binding also maps: store it in its child table or in columns, not both.", at);
            else if (above.FirstOrDefault(l => l.Attributes.Any(a => a.Id == attribute.Id) && StoredBy(run._bindingByEntity[l.Id].Binding, attribute.Id)) is { } level)
                Report("MQ4137", $"{prefix} stores attribute '{attribute.Name}', which the binding of base '{level.Name}' already stores: the binding joins the base's table (up the chain) by its base foreign key, so the base's binding stores it there.", at);
        }

        /// <summary>Whether a binding file stores an attribute: a field on it or one of its members, or a child table.</summary>
        private static bool StoredBy(EntityBinding binding, string attributeId) =>
            binding.ChildTables.Any(c => c.Attribute == attributeId)
            || binding.Fields.Any(f => f.Attribute == attributeId || IsBelow(f.Attribute, attributeId));

        /// <summary>
        /// MQ4138 for an owner foreign key that is not the child table's, does not reference the owning table, or references neither its primary key
        /// nor a unique key (or for a binding with no owning table); MQ4139 for a referenced column no field of the binding maps. Each column of the
        /// key is paired with the column it references and the binding's field there, whose value fills it.
        /// </summary>
        private void ResolveOwner(RBindingChildTable child, BindingChildTable entry, RTable? owning, List<RBindingField> fields, string prefix, string at)
        {
            if (child.Table is not { } table)
                return;
            at += "/ownerForeignKey";
            if (owning is null)
            {
                Report("MQ4138", $"{prefix} names owner foreign key '{entry.OwnerForeignKey}', but the binding reads {_b.SourceKind ?? "nothing"} '{_b.SourceName}' and writes no table, so no table of its own owns the rows: read or write a table.", at);
                return;
            }

            if (!run._foreignKeysById.TryGetValue(entry.OwnerForeignKey, out var spec) || !spec.Resolved || !ReferenceEquals(spec.Host.Table, table))
            {
                var what = spec is { Resolved: true } ? $"is foreign key '{spec.Result.Name}' of table '{spec.Host.Table.Name}'" : $"is no foreign key of database '{run._db.Name}'";
                Report("MQ4138", $"{prefix} names owner foreign key '{entry.OwnerForeignKey}', which {what}, not of child table '{table.Name}': name a foreign key of that table that references table '{owning.Name}'.", at);
                return;
            }

            var key = spec.Result;
            if (!ReferenceEquals(key.ReferencedTable, owning))
            {
                Report("MQ4138", $"{prefix} names owner foreign key '{key.Name}', which references table '{key.ReferencedTable.Name}', but the binding keeps its rows in table '{owning.Name}': name a foreign key that references that table.", at);
                return;
            }

            var referenced = key.ReferencedColumns;
            bool Same(IReadOnlyList<RColumn> columns) => columns.Count == referenced.Count && columns.All(c => referenced.Contains(c));
            if (!(owning.PrimaryKey is { } primary && Same(primary.Columns)) && !owning.Uniques.Any(u => Same(u.Columns)))
            {
                Report("MQ4138", $"{prefix} names owner foreign key '{key.Name}', which references {Names(referenced)} of table '{owning.Name}', neither its primary key nor a unique key: a row's owner is one row of the table.", at);
                return;
            }

            var owner = new List<RBindingOwnerColumn>(key.Columns.Count);
            var missing = new List<string>();
            var candidates = _b.Key.Concat(fields).ToList();
            for (var k = 0; k < key.Columns.Count && k < referenced.Count; k++)
            {
                var column = referenced[k];
                var field = candidates.FirstOrDefault(f => ReferenceEquals(f.WriteColumn, column) || ReferenceEquals(f.SourceColumn, column));
                if (field is null)
                    missing.Add(column.Name);
                owner.Add(new RBindingOwnerColumn
                {
                    Column = key.Columns[k], ReferencedColumn = column, Field = field, Parameter = "owner" + Casing.Pascal(field?.Name ?? column.Name),
                });
            }

            if (missing.Count > 0)
                Report("MQ4139", $"{prefix} names owner foreign key '{key.Name}', which references {Names(missing.Select(n => owning.Columns.First(c => c.Name == n)).ToList())} of table '{owning.Name}', but no field of the binding maps {(missing.Count == 1 ? "it" : "them")}: map a field for each, so each row knows its owner.", at);
            child.OwnerForeignKey = key;
            child.Owner = owner;
        }

        private static string Names(IReadOnlyList<RColumn> columns) =>
            (columns.Count == 1 ? "column " : "columns ") + string.Join(", ", columns.Select(c => "'" + c.Name + "'"));

        /// <summary>MQ4141: the order column is an integer column of the child table, not one of the owner foreign key's, on a list.</summary>
        private void ResolveOrderColumn(RBindingChildTable child, BindingChildTable entry, RTable table, string prefix, string at)
        {
            if (entry.OrderColumn is not { } name)
                return;
            at += "/orderColumn";
            if (FindTableColumn(table, name) is not { } column)
                Report("MQ4141", $"{prefix} names order column '{name}', which child table '{table.Name}' does not have.", at);
            else if (child.Attribute is { Collection: false } attribute)
                Report("MQ4141", $"{prefix} names order column '{column.Name}', but attribute '{attribute.Name}' is a single value object, at most one row: remove orderColumn.", at);
            else if (PhysicalType(column) is not ("int16" or "int32" or "int64"))
                Report("MQ4141", $"{prefix} names order column '{column.Name}', which is {PhysicalType(column)}: a position is an integer (int16, int32 or int64).", at);
            else if (child.OwnerForeignKey?.Columns.Contains(column) == true)
                Report("MQ4141", $"{prefix} names order column '{column.Name}', a column of the owner foreign key: name another column for the position.", at);
            else
                (child.OrderColumn, child.OrderParameter) = (column, PositionParameter);
        }

        /// <summary>The entry's constants, each a column of the child table (MQ4140) whose value fits it (MQ4051).</summary>
        private List<RBindingConstant> ResolveChildConstants(BindingChildTable entry, RTable table, string prefix, string at)
        {
            var constants = new List<RBindingConstant>(entry.Constants.Count);
            for (var k = 0; k < entry.Constants.Count; k++)
            {
                var c = entry.Constants[k];
                var constantAt = at + "/constants/" + k.ToString(CultureInfo.InvariantCulture);
                var constant = new RBindingConstant { Column = c.Column, ColumnName = c.Column, Value = ResolutionValues.Plain(c.Value) };
                if (FindTableColumn(table, c.Column) is { } column)
                {
                    (constant.ColumnName, constant.SourceColumn, constant.WriteColumn) = (column.Name, column, _b.Writes ? column : null);
                    CheckFit(constant.Value, PhysicalType(column), column.Length, column.Nullable, column.Name, constantAt + "/value");
                }
                else
                {
                    Report("MQ4140", $"{prefix} names constant column '{c.Column}', which child table '{table.Name}' does not have.", constantAt + "/column");
                }

                constants.Add(constant);
            }

            return constants;
        }

        /// <summary>
        /// MQ4140 for a field whose path does not start with the entry's attribute or names no member of its type, a path mapped twice or both whole
        /// and member by member, a column the child table does not have or that two fields map, a column of the owner foreign key (MQ4139), and a
        /// name another parameter of the entry's statements already has. A list of a scalar, an enum or a reference type maps the attribute itself
        /// (named <c>value</c>); a value object's members are named after their path below the attribute.
        /// </summary>
        private List<RBindingField> ResolveChildFields(RBindingChildTable child, BindingChildTable entry, Dictionary<string, BindingColumnStatus> listed, string prefix, string at)
        {
            var result = new List<RBindingField>(entry.Fields.Count);
            var paths = new Dictionary<string, int>(StringComparer.Ordinal);
            var columns = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in child.Owner)
                names.TryAdd(o.Parameter, "the owner's");
            if (child.OrderParameter is { } position)
                names.TryAdd(position, "the position's");
            for (var j = 0; j < entry.Fields.Count; j++)
            {
                var f = entry.Fields[j];
                var fieldAt = at + "/fields/" + j.ToString(CultureInfo.InvariantCulture);
                var field = new RBindingField { AttributeRef = f.Attribute, Column = f.Column, Name = f.Attribute };
                result.Add(field);
                if (child.Attribute is { } attribute)
                {
                    var segments = f.Attribute.Split('.');
                    if (segments[0] != attribute.Id)
                    {
                        Report("MQ4140", $"{prefix} maps '{f.Attribute}', which does not start with attribute '{attribute.Name}' the entry stores: each path starts with the attribute's id.", fieldAt + "/attribute");
                    }
                    else if (segments.Length == 1)
                    {
                        if (attribute.Type.Kind == "value-object")
                            Report("MQ4140", $"{prefix} maps value object attribute '{attribute.Name}' itself: map its members (attributeId.memberId), each to a column.", fieldAt + "/attribute");
                        else
                            (field.Attribute, field.Name) = (attribute, "value");
                    }
                    else if (MemberChain(attribute, segments[1..], listHead: true) is { } chain)
                    {
                        (field.Attribute, field.Member, field.Members) = (attribute, chain[^1], chain);
                        field.Name = Casing.Camel(chain[0].Name) + string.Concat(chain.Skip(1).Select(m => Casing.Pascal(m.Name)));
                    }
                    else
                    {
                        Report("MQ4140", $"{prefix} maps '{f.Attribute}', which names no member of the type of attribute '{attribute.Name}'{(attribute.Type.Kind == "value-object" ? " (each segment after the attribute is a member of the value object before it)" : ", a list of single values: map the attribute itself")}.", fieldAt + "/attribute");
                    }

                    if (field.Attribute is not null)
                    {
                        if (!paths.TryAdd(f.Attribute, j))
                        {
                            Report("MQ4140", $"{prefix} maps '{field.Name}' twice (fields {paths[f.Attribute].ToString(CultureInfo.InvariantCulture)} and {j.ToString(CultureInfo.InvariantCulture)}).", fieldAt + "/attribute");
                        }
                        else if (paths.Keys.FirstOrDefault(p => p != f.Attribute && (IsBelow(p, f.Attribute) || IsBelow(f.Attribute, p))) is { } overlap)
                        {
                            Report("MQ4140", $"{prefix} maps '{field.Name}' both whole and member by member (fields {paths[overlap].ToString(CultureInfo.InvariantCulture)} and {j.ToString(CultureInfo.InvariantCulture)}): map the value object to one JSON column, or each member to a column.", fieldAt + "/attribute");
                        }
                        else if (!names.TryAdd(field.Name, $"field {j.ToString(CultureInfo.InvariantCulture)}'s") && names[field.Name] is var other)
                        {
                            Report("MQ4140", $"{prefix} names a field '{field.Name}', the name of {other} parameter, so the insert would take one value for both: rename the member.", fieldAt + "/attribute");
                        }
                    }
                }

                if (child.Table is not { } table)
                    continue;
                if (FindTableColumn(table, f.Column) is not { } column)
                {
                    field.ColumnName = f.Column;
                    Report("MQ4140", $"{prefix} maps '{field.Name}' to column '{f.Column}', which child table '{table.Name}' does not have.", fieldAt + "/column");
                    continue;
                }

                (field.ColumnName, field.SourceColumn, field.WriteColumn) = (column.Name, column, _b.Writes ? column : null);
                (field.Type, field.NativeType, field.Nullable) = (PhysicalType(column), column.NativeType, column.Nullable);
                if (field.Member is { } whole && (whole.Collection || whole.Type.Kind == "value-object") && field.Type != "json")
                    Report("MQ4140", $"{prefix} maps '{field.Name}', a {(whole.Collection ? "list" : "value object")} held whole, to column '{column.Name}' of type {field.Type}: a path that ends at a value object or a list maps a JSON column.", fieldAt + "/column");
                if (!columns.TryAdd(column.Name, j))
                    Report("MQ4140", $"{prefix} maps column '{column.Name}' twice (fields {columns[column.Name].ToString(CultureInfo.InvariantCulture)} and {j.ToString(CultureInfo.InvariantCulture)}).", fieldAt + "/column");
                else if (child.OwnerForeignKey?.Columns.Contains(column) == true)
                    Report("MQ4139", $"{prefix} maps '{field.Name}' to column '{column.Name}', a column of the owner foreign key, which holds the owner's key: map another column.", fieldAt + "/column");
                else if (ReferenceEquals(column, child.OrderColumn))
                    Report("MQ4141", $"{prefix} maps '{field.Name}' to column '{column.Name}', the order column, which holds the item's position: map another column.", fieldAt + "/column");

                var marked = listed.TryGetValue(column.Name, out var status) && status is BindingColumnStatus.Database or BindingColumnStatus.Computed;
                field.IsGenerated = marked || column.Identity || column.Computed is not null;
                field.InInsert = _b.Writes && !field.IsGenerated && field.Attribute is not null;
            }

            return result;
        }

        /// <summary>
        /// MQ4140 for a constant on a column the entry already uses (a field's, the owner foreign key's, the order column or another constant's):
        /// the statements would name it twice, so the constant is dropped from them.
        /// </summary>
        private void CheckChildConstantColumns(RBindingChildTable child, string prefix, string at)
        {
            var used = new HashSet<RColumn>(child.Fields.Select(f => f.SourceColumn).OfType<RColumn>().Concat(child.Owner.Select(o => o.Column)), ReferenceEqualityComparer.Instance);
            if (child.OrderColumn is { } order)
                used.Add(order);
            for (var k = 0; k < child.Constants.Count; k++)
            {
                var constant = child.Constants[k];
                if (constant.SourceColumn is not { } column)
                    continue;
                if (used.Add(column))
                    continue;
                Report("MQ4140", $"{prefix} names constant column '{column.Name}', a column the entry already uses (a field, the owner foreign key, the order column or another constant): a column is used once.", at + "/constants/" + k.ToString(CultureInfo.InvariantCulture) + "/column");
                (constant.SourceColumn, constant.WriteColumn) = (null, null);
            }
        }

        /// <summary>
        /// MQ4143: the owner foreign key references a unique key, not the primary key, the binding updates a field on that key, and the foreign key
        /// does not cascade on update: changing the value breaks the rows' tie to their owner.
        /// </summary>
        private void CheckOwnerUpdates(RBindingChildTable child, string prefix, string at)
        {
            if (child.OwnerForeignKey is not { } key || child.Binding.WriteTable is null || key.OnUpdate == "cascade")
                return;
            var primary = key.ReferencedTable.PrimaryKey?.Columns ?? [];
            if (primary.Count == key.ReferencedColumns.Count && key.ReferencedColumns.All(c => primary.Contains(c)))
                return;
            if (child.Owner.FirstOrDefault(o => o.Field is { InUpdate: true }) is not { } updated)
                return;
            Report("MQ4143", $"{prefix} names owner foreign key '{key.Name}' on a unique key whose field '{updated.Field!.Name}' the binding updates, and the key does not cascade on update: changing the value leaves the rows pointing at the old one, or the database refuses it. Give the foreign key onUpdate \"cascade\", or make the field read only.", at + "/ownerForeignKey");
        }

        /// <summary>
        /// MQ4144: a single value object's child table where nothing keeps each owner to one row: no primary key or unique constraint whose columns
        /// are exactly the owner foreign key's columns, with the constants' columns.
        /// </summary>
        private void CheckSingleRow(RBindingChildTable child, string prefix, string at)
        {
            if (child.IsList || child.Attribute is null || child.Table is not { } table || child.Owner.Count == 0)
                return;
            var wanted = new HashSet<RColumn>(child.Owner.Select(o => o.Column).Concat(child.Constants.Select(c => c.SourceColumn).OfType<RColumn>()), ReferenceEqualityComparer.Instance);
            bool Exactly(IReadOnlyList<RColumn> columns) => columns.Count == wanted.Count && columns.All(wanted.Contains);
            if ((table.PrimaryKey is { } primary && Exactly(primary.Columns)) || table.Uniques.Any(u => Exactly(u.Columns)))
                return;
            Report("MQ4144", $"{prefix} stores single value object '{child.Attribute.Name}' in table '{table.Name}', but no primary key or unique constraint there is exactly {Names([.. wanted])}, so an owner could have several rows: declare one.", at + "/table");
        }

        /// <summary>Every column of the child table with what accounts for it; MQ4047 for those nothing does.</summary>
        private void AccountChild(RBindingChildTable child, RTable table, Dictionary<string, BindingColumnStatus> listed, string prefix, string at)
        {
            var result = new List<RBindingColumn>(table.Columns.Count);
            foreach (var column in table.Columns)
            {
                var entry = new RBindingColumn { Name = column.Name, Column = column };
                if (child.Fields.FirstOrDefault(f => ReferenceEquals(f.SourceColumn, column)) is { } field)
                    (entry.Status, entry.Field) = ("field", field);
                else if (child.Owner.Any(o => ReferenceEquals(o.Column, column)))
                    entry.Status = "owner";
                else if (ReferenceEquals(child.OrderColumn, column))
                    entry.Status = "order";
                else if (child.Constants.FirstOrDefault(c => ReferenceEquals(c.SourceColumn, column)) is { } constant)
                    (entry.Status, entry.Constant) = ("constant", constant);
                else if (listed.TryGetValue(column.Name, out var status))
                    entry.Status = ResolutionValues.Kebab(status);
                else if (column.Identity || (column.Sequence is not null && column.IsPrimaryKey))
                    entry.Status = "identity";
                else if (column.Computed is not null)
                    entry.Status = "computed";
                else if (column.Default is not null || column.DefaultSql is not null)
                    entry.Status = "default";
                result.Add(entry);
            }

            child.Columns = result;
            var names = result.Where(c => c.Status == "unaccounted").Select(c => c.Name).ToList();
            if (names.Count > 0)
                Report("MQ4047", $"{prefix} accounts for no {(names.Count == 1 ? "column" : "columns")} {string.Join(", ", names.Select(n => "'" + n + "'"))} of child table '{table.Name}': map {(names.Count == 1 ? "it" : "each")} to a field, give {(names.Count == 1 ? "it" : "each")} a constant, or list {(names.Count == 1 ? "it" : "them")} in columns (ignored, database or computed).", at);
        }

        /// <summary>
        /// MQ4058 for what the entry's fields leave unmapped: a list of single values with no field on the attribute, else the members of the value
        /// object (each item's, for a list), to any depth, as for the binding's own value object attributes.
        /// </summary>
        private void ReportChildUnmapped(RBindingChildTable child, BindingChildTable entry, string at)
        {
            if (child.Attribute is not { } attribute || child.Table is null)
                return;
            var mapped = entry.Fields.Select(f => f.Attribute).ToHashSet(StringComparer.Ordinal);
            var names = new List<string>();
            if (attribute.Type is { Kind: "value-object", ValueObject: { } valueObject })
            {
                // A value object with no member a field can map is skipped, as a list is.
                UnmappedMembers(mapped, attribute.Id, attribute.Name, valueObject, names, 0);
            }
            else if (!mapped.Contains(attribute.Id))
            {
                names.Add(attribute.Name);
            }

            if (names.Count > 0)
                Report("MQ4058", $"No field of child table '{child.Table.Name}' maps {string.Join(", ", names)}: the entity reads {(names.Count == 1 ? "it" : "them")} from nowhere.", at + "/fields");
        }
    }
}
