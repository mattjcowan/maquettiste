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
public sealed record BoundByView(string EntityId, string EntityName, string BindingId, bool Reads, bool Writes, IReadOnlyList<BindingConstantView> Constants);

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
public sealed record EntityBindingRecord(string Id, string Database, string? SourceKind, string Source, string SourceName, string? WriteTable, string Delete,
    string? SoftDeleteColumn, object? SoftDeleteValue, IReadOnlyList<BindingConstantView> Constants, IReadOnlyList<BindingFieldRecord> Fields,
    IReadOnlyList<BindingColumnRecord> Columns, IReadOnlyList<BindingColumnRecord> WriteColumns);

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
public sealed record BindingFieldRecord(string Name, string Attribute, string? AttributeId, string? MemberId, string? EndId, string Column, string? ColumnKey,
    string? WriteColumn, string? Type, bool Nullable, bool IsKey, bool IsGenerated, bool InInsert, bool InUpdate);

/// <summary>A column of a binding's source or write table and what accounts for it.</summary>
/// <param name="Name">The column's physical name.</param>
/// <param name="Key">The column's key, for a table.</param>
/// <param name="Status"><c>field</c>, <c>constant</c>, <c>ignored</c>, <c>database</c>, <c>computed</c>, <c>identity</c>, <c>default</c>,
/// <c>soft-delete</c> or <c>unaccounted</c>.</param>
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
public sealed record BindingSqlPreview(string EntityId, string BindingId, string Database, string Dialect, BindingStatement? Select, BindingStatement? SelectByKey,
    BindingStatement? Insert, BindingStatement? Update, BindingStatement? Delete);

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
            ReferenceEquals(b.WriteTable, target), Constants(b)))];

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
        [.. b.Fields.Select(f => new BindingFieldRecord(f.Name, f.AttributeRef, f.Attribute?.Id, f.Member?.Id, f.End?.Id, f.ColumnName, f.SourceColumn?.Key,
            f.WriteColumn?.Key, f.Type, f.Nullable, f.IsKey, f.IsGenerated, f.InInsert, f.InUpdate))],
        Columns(b.Columns),
        Columns(b.WriteColumns));

    private static IReadOnlyList<BindingColumnRecord> Columns(IReadOnlyList<RBindingColumn> columns) =>
        [.. columns.Select(c => new BindingColumnRecord(c.Name, c.Column?.Key, c.Status, c.Field?.Name))];

    /// <summary>The five statements of a binding for a dialect, with what the dialect cannot render.</summary>
    public static (BindingSqlPreview Preview, IReadOnlyList<Diagnostic> Diagnostics) Preview(REntityBinding binding, string? dialect, QuerySqlOptions? options)
    {
        var diagnostics = new List<Diagnostic>();
        BindingStatement? Statement(string name)
        {
            var text = BindingSql.Render(binding, name, dialect, options);
            diagnostics.AddRange(text.Diagnostics);
            return text.Sql.Length == 0 ? null : new BindingStatement(text.Sql, text.Parameters);
        }

        var name = dialect is not null && Rendering.SqlDialects.TryParse(dialect, out var parsed) ? DialectTypeMaps.Name(parsed) : binding.Database.Dialect;
        var preview = new BindingSqlPreview(binding.Entity.Id, binding.Id, binding.Database.Id, name, Statement("select"), Statement("select-by-key"),
            Statement("insert"), Statement("update"), Statement("delete"));
        return (preview, [.. diagnostics.DistinctBy(d => (d.Rule, d.Message))]);
    }
}
