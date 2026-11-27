using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>An entity bound to a table, view or query (erratum E43): what the database object's <c>boundBy</c> lists.</summary>
/// <param name="EntityId">The entity's id.</param>
/// <param name="EntityName">The entity's name.</param>
/// <param name="BindingId">The binding's id.</param>
/// <param name="Reads">Whether the binding reads the object (it is the binding's source).</param>
/// <param name="Writes">Whether the binding writes the object (it is the binding's write table).</param>
/// <param name="Constants">The binding's constants: what tells several entities of one table apart.</param>
/// <param name="Discriminator">The type column the binding names or inherits from a base, with the entity's code and the codes its reads
/// keep: the other way the rows of entities sharing one table are told apart. Left out when the binding has none.</param>
public sealed record BoundByView(string EntityId, string EntityName, string BindingId, bool Reads, bool Writes, IReadOnlyList<BindingConstantView> Constants,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindingDiscriminatorView? Discriminator = null);

/// <summary>A binding's type column, as a table's list of bindings shows it.</summary>
/// <param name="Column">The column's physical name.</param>
/// <param name="Value">The entity's type code, a string or an integer: what its insert writes.</param>
/// <param name="Codes">The codes its reads, updates and deletes keep: its own and those of the entities derived from it that share the
/// column in the same table.</param>
public sealed record BindingDiscriminatorView(string Column, object Value, IReadOnlyList<object> Codes);

/// <summary>A constant column of a binding.</summary>
/// <param name="Column">The column's physical name.</param>
/// <param name="Value">The value (a string, a number or a bool), or <see langword="null"/> for NULL.</param>
public sealed record BindingConstantView(string Column, object? Value);

/// <summary>A resolved entity binding (erratum E43), as the resolved model's entity records carry it.</summary>
/// <param name="Id">The binding's id.</param>
/// <param name="Database">The database's id.</param>
/// <param name="SourceKind"><c>table</c>, <c>view</c> or <c>query</c>; <see langword="null"/> when the source does not resolve.</param>
/// <param name="Source">The source's key (a table key, a view or query id), as the binding names it when it does not resolve.</param>
/// <param name="SourceName">The source's name.</param>
/// <param name="WriteTable">The key of the table the binding writes, or <see langword="null"/> when it does not write.</param>
/// <param name="Delete"><c>key</c>, <c>soft</c> or <c>none</c>.</param>
/// <param name="SoftDeleteColumn">The column a soft delete sets, or <see langword="null"/>.</param>
/// <param name="SoftDeleteValue">The value a soft delete sets.</param>
/// <param name="Constants">The constants.</param>
/// <param name="Fields">The field map.</param>
/// <param name="Columns">Every source column with what accounts for it.</param>
/// <param name="WriteColumns">Every write table column with what accounts for it, when the write table is not the source.</param>
/// <param name="DiscriminatorValue">The entity's type code the binding sets, a string or an integer (several-types-design.md decision 42);
/// left out when unset.</param>
/// <param name="DiscriminatorColumn">The type column the entity shares with its base or the entities derived from it in one table, named
/// by the binding's <c>discriminatorColumn</c> or inherited from its nearest base's binding to the database; left out when there is none.</param>
/// <param name="BaseForeignKey">Table per type (retire-projection-design.md section 4): the id of the foreign key the binding's
/// <c>baseForeignKey</c> names, joining its table to its nearest bound base's; left out without one, or when it does not resolve.</param>
/// <param name="Key">With <paramref name="BaseForeignKey"/>: the key it gives, one field per key field of the base's binding on the foreign
/// key's column, which the field map does not hold; left out otherwise (the key fields are the field map's with <c>isKey</c>).</param>
/// <param name="Bases">The tables above the binding's own that its entity's rows also live in, nearest first; left out when there are none.</param>
public sealed record EntityBindingRecord(string Id, string Database, string? SourceKind, string Source, string SourceName, string? WriteTable, string Delete,
    string? SoftDeleteColumn, object? SoftDeleteValue, IReadOnlyList<BindingConstantView> Constants, IReadOnlyList<BindingFieldRecord> Fields,
    IReadOnlyList<BindingColumnRecord> Columns, IReadOnlyList<BindingColumnRecord> WriteColumns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? DiscriminatorValue = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindingDiscriminatorRecord? DiscriminatorColumn = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? BaseForeignKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindingFieldRecord>? Key = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindingBaseRecord>? Bases = null);

/// <summary>One table above a binding's own (table per type): the base's binding that reads and writes it, and how the table below joins it.</summary>
/// <param name="Binding">The id of the base entity's binding to the database.</param>
/// <param name="Entity">The base entity's id.</param>
/// <param name="Table">The key of the table the base's binding writes, else its source's key.</param>
/// <param name="ForeignKey">The id of the foreign key that joins the table below to this one (its name for a key without an id).</param>
/// <param name="Columns">The foreign key's columns, in the table below, by key.</param>
/// <param name="ReferencedColumns">The columns of this table they reference, in the order of <paramref name="Columns"/>.</param>
/// <param name="DiscriminatorColumn">The table's type column, with the derived entity's code and the codes its statements keep; left out
/// without one.</param>
public sealed record BindingBaseRecord(string Binding, string Entity, string Table, string ForeignKey, IReadOnlyList<string> Columns,
    IReadOnlyList<string> ReferencedColumns,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindingDiscriminatorRecord? DiscriminatorColumn = null);

/// <summary>The type column of a resolved binding: one column a base entity and the entities derived from it share in one table.</summary>
/// <param name="Column">The column's physical name in the source.</param>
/// <param name="ColumnKey">The source column's key, for a table source.</param>
/// <param name="WriteColumn">The key of the write table's column the insert writes the code into, or <see langword="null"/>.</param>
/// <param name="Base">The id of the entity whose binding names the column: this binding's entity, or its nearest base that names one.</param>
/// <param name="Value">The entity's type code, a string or an integer: what the insert writes.</param>
/// <param name="Codes">The codes reads, updates and deletes filter on: the entity's own, then those of the entities derived from it that
/// share the column in the same table (for the entity whose binding names the column, its whole hierarchy there).</param>
public sealed record BindingDiscriminatorRecord(string Column, string? ColumnKey, string? WriteColumn, string Base, object Value, IReadOnlyList<object> Codes);

/// <summary>One field of a resolved binding.</summary>
/// <param name="Name">The field's name (the select's alias and the statements' parameter).</param>
/// <param name="Attribute">The attribute, <c>attributeId.memberId</c> or relation end id the field maps, as the file writes it.</param>
/// <param name="AttributeId">The attribute's id (a value object member's owner), or <see langword="null"/>.</param>
/// <param name="MemberId">The value object member's id, or <see langword="null"/>.</param>
/// <param name="EndId">The relation end's id, for a to-one navigation's key.</param>
/// <param name="Column">The source column's physical name.</param>
/// <param name="ColumnKey">The source column's key, for a table source.</param>
/// <param name="WriteColumn">The write table column's key, or <see langword="null"/> when the field is not written.</param>
/// <param name="Type">The column's built-in type keyword, or <see langword="null"/>.</param>
/// <param name="Nullable">Whether the column is nullable.</param>
/// <param name="IsKey">Whether the field maps a key attribute.</param>
/// <param name="IsGenerated">Whether the database fills the column.</param>
/// <param name="InInsert">Whether the insert writes the field.</param>
/// <param name="InUpdate">Whether the update sets the field.</param>
/// <param name="Holds">On a relation end with several types (several-types-design.md decision 46): <c>id</c>, <c>type</c> or <c>key</c>, what
/// the field's column holds; left out on today's fields.</param>
/// <param name="KeyOf">The entity of an <c>endId.entityId</c> field (<paramref name="Holds"/> is <c>key</c>); left out otherwise.</param>
public sealed record BindingFieldRecord(string Name, string Attribute, string? AttributeId, string? MemberId, string? EndId, string Column, string? ColumnKey,
    string? WriteColumn, string? Type, bool Nullable, bool IsKey, bool IsGenerated, bool InInsert, bool InUpdate,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Holds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? KeyOf = null);

/// <summary>A column of a binding's source or write table and what accounts for it.</summary>
/// <param name="Name">The column's physical name.</param>
/// <param name="Key">The column's key, for a table.</param>
/// <param name="Status"><c>field</c>, <c>constant</c>, <c>discriminator</c> (the binding's type column), <c>ignored</c>, <c>database</c>,
/// <c>computed</c>, <c>identity</c>, <c>default</c>, <c>soft-delete</c>, <c>relation</c> (a relation attribute on the foreign key's table) or <c>unaccounted</c>.</param>
/// <param name="Field">The name of the field that maps it, with status <c>field</c>.</param>
public sealed record BindingColumnRecord(string Name, string? Key, string Status, string? Field);

/// <summary>
/// The statements of one binding for a dialect (<c>GET /api/model/entities/{id}/bindings/{bindingId}/sql</c>, MCP
/// <c>preview_binding_sql</c>); a statement the binding does not have is <see langword="null"/>.
/// </summary>
/// <param name="EntityId">The entity's id.</param>
/// <param name="BindingId">The binding's id.</param>
/// <param name="Database">The database's id.</param>
/// <param name="Dialect">The dialect rendered.</param>
/// <param name="Select">Every row.</param>
/// <param name="SelectByKey">The row with a key.</param>
/// <param name="Insert">The insert.</param>
/// <param name="Update">The update by key.</param>
/// <param name="Delete">The delete (or soft-delete update) by key.</param>
/// <param name="Exists">The probe: a count of the rows of the binding's own source with the key, joining no base; left out without key fields.</param>
/// <param name="Bases">With bases (table per type), each base's table's statements for this entity, nearest first; left out without bases.</param>
public sealed record BindingSqlPreview(string EntityId, string BindingId, string Database, string Dialect, BindingStatement? Select, BindingStatement? SelectByKey,
    BindingStatement? Insert, BindingStatement? Update, BindingStatement? Delete,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] BindingStatement? Exists = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<BindingBaseSqlPreview>? Bases = null);

/// <summary>The statements one base's table renders for a derived entity (table per type).</summary>
/// <param name="Level">The level, from 1 for the nearest base up to the root.</param>
/// <param name="EntityId">The base entity's id.</param>
/// <param name="BindingId">The base's binding's id.</param>
/// <param name="Insert">The base row's insert, with the derived entity's type code; the root's returns the generated key.</param>
/// <param name="Update">The base row's update by key.</param>
/// <param name="Delete">The base row's delete (or the root's soft-delete update) by key.</param>
public sealed record BindingBaseSqlPreview(int Level, string EntityId, string BindingId, BindingStatement? Insert, BindingStatement? Update, BindingStatement? Delete);

/// <summary>One rendered statement of a binding.</summary>
/// <param name="Sql">The SQL text.</param>
/// <param name="Parameters">The parameter names in placeholder order.</param>
public sealed record BindingStatement(string Sql, IReadOnlyList<string> Parameters);

/// <summary>The result of <see cref="GenerationService.GetBindingSqlAsync"/>.</summary>
/// <param name="Preview">The statements, or <see langword="null"/> when the model has errors or the entity or binding is unknown.</param>
/// <param name="Diagnostics">The errors that prevented the preview, or what the dialect cannot render.</param>
public sealed record BindingSqlResult(BindingSqlPreview? Preview, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Projects resolved bindings into the API's records.</summary>
internal static class BindingViews
{
    public static IReadOnlyList<BoundByView> BoundBy(IReadOnlyList<REntityBinding> bindings, object target) =>
        [.. bindings.Select(b => new BoundByView(b.Entity.Id, b.Entity.Name, b.Id,
            ReferenceEquals(b.SourceTable, target) || ReferenceEquals(b.SourceView, target) || ReferenceEquals(b.SourceQuery, target),
            ReferenceEquals(b.WriteTable, target), Constants(b),
            b.DiscriminatorColumn is { } d ? new BindingDiscriminatorView(d.ColumnName, d.Value, [.. d.Codes]) : null))];

    public static IReadOnlyList<BindingConstantView> Constants(REntityBinding b) => [.. b.Constants.Select(c => new BindingConstantView(c.ColumnName, c.Value))];

    public static EntityBindingRecord Record(REntityBinding b) => new(
        b.Id,
        b.Database.Id,
        b.SourceKind,
        b.SourceTable?.Key ?? b.SourceView?.Id ?? b.SourceQuery?.Id ?? b.Definition?.Source ?? "",
        b.SourceName,
        b.WriteTable?.Key,
        b.Delete,
        b.SoftDeleteColumn?.Name,
        b.SoftDeleteValue,
        Constants(b),
        // holds and keyOf on the field forms of several-types-design.md decision 46, the binding's discriminatorValue (section 19.8), its
        // type column and its table per type members: each left out when null, so today's bindings keep their records.
        [.. b.Fields.Select(Field)],
        Columns(b.Columns),
        Columns(b.WriteColumns),
        b.DiscriminatorValue,
        Discriminator(b.DiscriminatorColumn),
        b.BaseForeignKey is { } baseKey ? baseKey.Id ?? baseKey.Name : null,
        b.BaseForeignKey is null ? null : [.. b.Key.Select(Field)],
        b.Bases.Count == 0 ? null : [.. b.Bases.Select(l => new BindingBaseRecord(l.Binding.Id, l.Binding.Entity.Id,
            (l.Binding.WriteTable ?? l.Binding.SourceTable)?.Key ?? l.Binding.Definition?.Source ?? "", l.ForeignKey.Id ?? l.ForeignKey.Name,
            [.. l.ForeignKey.Columns.Select(c => c.Key)], [.. l.ForeignKey.ReferencedColumns.Select(c => c.Key)], Discriminator(l.DiscriminatorColumn)))]);

    private static BindingFieldRecord Field(RBindingField f) => new(f.Name, f.AttributeRef, f.Attribute?.Id, f.Member?.Id, f.End?.Id, f.ColumnName,
        f.SourceColumn?.Key, f.WriteColumn?.Key, f.Type, f.Nullable, f.IsKey, f.IsGenerated, f.InInsert, f.InUpdate, f.Holds, f.KeyOf?.Id);

    private static BindingDiscriminatorRecord? Discriminator(RBindingDiscriminator? d) =>
        d is null ? null : new BindingDiscriminatorRecord(d.ColumnName, d.SourceColumn?.Key, d.WriteColumn?.Key, d.Base.Id, d.Value, [.. d.Codes]);

    private static IReadOnlyList<BindingColumnRecord> Columns(IReadOnlyList<RBindingColumn> columns) =>
        [.. columns.Select(c => new BindingColumnRecord(c.Name, c.Column?.Key, c.Status, c.Field?.Name))];

    /// <summary>The statements of a binding for a dialect (its bases' too), with what the dialect cannot render.</summary>
    public static (BindingSqlPreview Preview, IReadOnlyList<Diagnostic> Diagnostics) Preview(REntityBinding binding, string? dialect, QuerySqlOptions? options)
    {
        var diagnostics = new List<Diagnostic>();
        BindingStatement? Statement(string name, RBindingBase? level = null)
        {
            var text = level is null ? BindingSql.Render(binding, name, dialect, options) : BindingSql.Render(level, name, dialect, options);
            diagnostics.AddRange(text.Diagnostics);
            return text.Sql.Length == 0 ? null : new BindingStatement(text.Sql, text.Parameters);
        }

        var name = dialect is not null && Rendering.SqlDialects.TryParse(dialect, out var parsed) ? DialectTypeMaps.Name(parsed) : binding.Database.Dialect;
        var preview = new BindingSqlPreview(binding.Entity.Id, binding.Id, binding.Database.Id, name, Statement("select"), Statement("select-by-key"),
            Statement("insert"), Statement("update"), Statement("delete"), Statement("exists"),
            binding.Bases.Count == 0 ? null : [.. binding.Bases.Select((l, i) => new BindingBaseSqlPreview(i + 1, l.Binding.Entity.Id, l.Binding.Id,
                Statement("insert", l), Statement("update", l), Statement("delete", l)))]);
        return (preview, [.. diagnostics.DistinctBy(d => (d.Rule, d.Message))]);
    }
}
