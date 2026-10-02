using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>One resolved query, projected from <see cref="RQuery"/> (added 2026-10-02): the trees as the file writes them, with what
/// resolution adds (sources, field types, collection keys) and the SQL for the database's dialect.</summary>
/// <param name="Id">The query file's id.</param>
/// <param name="Name">The query's name.</param>
/// <param name="EntityId">The result entity's id, or <see langword="null"/> for an ad hoc row.</param>
/// <param name="Parameters">The parameters, in order.</param>
/// <param name="From">The first source.</param>
/// <param name="Joins">The joined sources.</param>
/// <param name="Select">The select list.</param>
/// <param name="Where">The row filter as the file writes it (a predicate tree), or <see langword="null"/>.</param>
/// <param name="GroupBy">The grouping expressions as the file writes them.</param>
/// <param name="Having">The group filter as the file writes it, or <see langword="null"/>.</param>
/// <param name="OrderBy">The ordering terms as the file writes them (<c>expression</c>, <c>direction</c>, <c>nulls</c>).</param>
/// <param name="Distinct">Whether duplicate rows are removed.</param>
/// <param name="Paging">The paging as the file writes it, or <see langword="null"/>.</param>
/// <param name="Collections">The collections.</param>
/// <param name="Sql">The SQL for the database's dialect (<c>@name</c> placeholders), with the hidden key columns its collections need.</param>
/// <param name="SqlParameters">The parameters the SQL names, in first-appearance order.</param>
/// <param name="Uses">The ids (keys, for a synthesized table) of the tables, views, routines and database types the query reads.</param>
/// <param name="DisplayName">As <see cref="TableView.DisplayName"/>.</param>
/// <param name="PluralName">As <see cref="TableView.PluralName"/>.</param>
/// <param name="Description">As <see cref="TableView.Description"/>.</param>
/// <param name="Stereotypes">As <see cref="TableView.Stereotypes"/>.</param>
/// <param name="Tags">As <see cref="TableView.Tags"/>.</param>
/// <param name="Category">As <see cref="TableView.Category"/>.</param>
/// <param name="Properties">As <see cref="TableView.Properties"/>.</param>
/// <param name="Generation">As <see cref="TableView.Generation"/>.</param>
public sealed record QueryView(
    string Id,
    string Name,
    string? EntityId,
    IReadOnlyList<QueryParameterView> Parameters,
    QuerySourceView From,
    IReadOnlyList<QuerySourceView> Joins,
    IReadOnlyList<QueryFieldView> Select,
    JsonElement? Where,
    IReadOnlyList<JsonElement> GroupBy,
    JsonElement? Having,
    IReadOnlyList<JsonElement> OrderBy,
    bool Distinct,
    JsonElement? Paging,
    IReadOnlyList<QueryCollectionView> Collections,
    string Sql,
    IReadOnlyList<string> SqlParameters,
    IReadOnlyList<string> Uses,
    string? DisplayName,
    string? PluralName,
    string? Description,
    IReadOnlyList<string> Stereotypes,
    IReadOnlyList<string> Tags,
    string? Category,
    IReadOnlyDictionary<string, object?> Properties,
    IReadOnlyDictionary<string, GenerationHints> Generation);

/// <summary>A parameter of a query.</summary>
/// <param name="Name">The name.</param>
/// <param name="Type">The built-in type keyword, or <see langword="null"/> when a database type gives the type.</param>
/// <param name="DbTypeId">The id of the database type, or <see langword="null"/>.</param>
/// <param name="Length">The length facet.</param>
/// <param name="Precision">The precision facet.</param>
/// <param name="Scale">The scale facet.</param>
/// <param name="NativeType">The native type for the database's dialect.</param>
/// <param name="CodeType">The built-in keyword a code type map maps.</param>
/// <param name="Collection">Whether the caller passes a list of values.</param>
/// <param name="Default">The default value, or <see langword="null"/>.</param>
/// <param name="Description">The description, or <see langword="null"/>.</param>
public sealed record QueryParameterView(string Name, string? Type, string? DbTypeId, int? Length, int? Precision, int? Scale, string NativeType, string CodeType,
    bool Collection, object? Default, string? Description);

/// <summary>A source of a query.</summary>
/// <param name="Source">The source as the file writes it.</param>
/// <param name="Alias">The alias.</param>
/// <param name="Kind"><c>from</c>, <c>inner</c>, <c>left</c>, <c>right</c>, <c>full</c> or <c>cross</c>.</param>
/// <param name="TableKey">The table's key, when the source is a table.</param>
/// <param name="ViewId">The view's id, when the source is a view.</param>
/// <param name="Name">The physical name.</param>
/// <param name="Schema">The schema name, or <see langword="null"/>.</param>
/// <param name="On">The join condition as the file writes it, or <see langword="null"/>.</param>
public sealed record QuerySourceView(string Source, string Alias, string Kind, string? TableKey, string? ViewId, string Name, string? Schema, JsonElement? On);

/// <summary>A field of a select list.</summary>
/// <param name="Name">The field's name in the result row.</param>
/// <param name="AttributeId">The attribute it fills, or <see langword="null"/>.</param>
/// <param name="Expression">The value as the file writes it (an expression tree).</param>
/// <param name="Type">The built-in type keyword, or <see langword="null"/> when unknown.</param>
/// <param name="NativeType">The native type, or <see langword="null"/> when unknown.</param>
/// <param name="CodeType">The keyword a code type map maps, or <see langword="null"/>.</param>
/// <param name="Nullable">Whether the value may be null.</param>
public sealed record QueryFieldView(string Name, string? AttributeId, JsonElement Expression, string? Type, string? NativeType, string? CodeType, bool Nullable);

/// <summary>A collection of a query: its nested query and the statement that fills it for every parent row.</summary>
/// <param name="Name">The collection's name.</param>
/// <param name="Attribute">The file's <c>attribute</c>: an attribute id, a relation end id or an ad hoc name.</param>
/// <param name="EntityId">The element entity's id, or <see langword="null"/> for an ad hoc element.</param>
/// <param name="Query">The nested query as the file writes it.</param>
/// <param name="Select">The nested select list, resolved.</param>
/// <param name="Keys">The correlation keys.</param>
/// <param name="Sql">The collection's statement for the database's dialect.</param>
/// <param name="SqlParameters">The parameters it names, in first-appearance order.</param>
public sealed record QueryCollectionView(string Name, string Attribute, string? EntityId, JsonElement Query, IReadOnlyList<QueryFieldView> Select,
    IReadOnlyList<QueryKeyView> Keys, string Sql, IReadOnlyList<string> SqlParameters);

/// <summary>A correlation key of a collection.</summary>
/// <param name="Outer">The parent's column (<c>alias.column</c>).</param>
/// <param name="ParentField">The parent row's field that carries the value.</param>
/// <param name="Hidden">Whether the parent's SQL adds the field as a hidden column.</param>
/// <param name="ChildField">The collection statement's column that carries the value.</param>
/// <param name="Parameter">The list parameter of the collection's statement.</param>
/// <param name="Type">The value's built-in type keyword, or <see langword="null"/>.</param>
/// <param name="NativeType">The value's native type, or <see langword="null"/>.</param>
public sealed record QueryKeyView(string Outer, string ParentField, bool Hidden, string ChildField, string Parameter, string? Type, string? NativeType);

/// <summary>What <see cref="GenerationService.GetQuerySqlAsync"/> returns.</summary>
/// <param name="Preview">The statements, or <see langword="null"/> when the model has errors (they come back) or no query has the id.</param>
/// <param name="Diagnostics">Validation and resolution diagnostics, and what the dialect cannot render (MQ4029).</param>
public sealed record QuerySqlResult(QuerySqlPreview? Preview, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>The SQL of one query for one dialect.</summary>
/// <param name="Id">The query's id.</param>
/// <param name="Name">The query's name.</param>
/// <param name="Database">The database's id.</param>
/// <param name="Dialect">The dialect rendered.</param>
/// <param name="Sql">The query's statement.</param>
/// <param name="Parameters">The parameters it names, in first-appearance order.</param>
/// <param name="Collections">One statement per collection, run after the parent rows with the list of their key values.</param>
public sealed record QuerySqlPreview(string Id, string Name, string Database, string Dialect, string Sql, IReadOnlyList<string> Parameters,
    IReadOnlyList<QueryCollectionSql> Collections);

/// <summary>The statement of one collection.</summary>
/// <param name="Name">The collection's name.</param>
/// <param name="Sql">The statement.</param>
/// <param name="Parameters">The parameters it names, in first-appearance order (the key lists included).</param>
/// <param name="Keys">The correlation keys.</param>
public sealed record QueryCollectionSql(string Name, string Sql, IReadOnlyList<string> Parameters, IReadOnlyList<QueryKeyView> Keys);

/// <summary>Projects resolved queries into <see cref="QueryView"/> records.</summary>
internal static class QueryViews
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    public static QueryView Project(RQuery query)
    {
        var file = query.Definition as Query;
        var rendered = query.Sql.Length == 0 ? null : QuerySql.Render(query);
        return new QueryView(
            query.Id,
            query.Name,
            query.Entity?.Id,
            [.. query.Parameters.Select(p => new QueryParameterView(p.Name, p.Type, p.DbType?.Id, p.Length, p.Precision, p.Scale, p.NativeType, p.CodeType,
                p.Collection, p.Default, p.Description))],
            Source(query.From, null),
            [.. query.Joins.Select((j, i) => Source(j, file is not null && i < file.Joins.Count ? file.Joins[i].On : null))],
            Fields(query.Select, file?.Select),
            Tree(file?.Where),
            [.. (file?.GroupBy ?? []).Select(g => Tree(g)!.Value)],
            Tree(file?.Having),
            [.. (file?.OrderBy ?? []).Select(o => Tree(o)!.Value)],
            query.Distinct,
            Tree(file?.Paging),
            [.. query.Collections.Select(Collection)],
            rendered?.Sql ?? "",
            rendered?.Parameters ?? [],
            [.. query.Uses.Select(u => u is RTable table ? table.Key : u.Id)],
            OrNull(query.DisplayName),
            OrNull(query.PluralName),
            query.Description,
            [.. query.Stereotypes.Select(s => s.Key)],
            query.Tags,
            query.Category?.Id,
            query.Properties,
            query.Generation);
    }

    /// <summary>The statements of a query for a dialect, with what the dialect cannot render.</summary>
    public static (QuerySqlPreview Preview, IReadOnlyList<Diagnostic> Diagnostics) Preview(RQuery query, string? dialect, QuerySqlOptions? options)
    {
        var main = QuerySql.Render(query, dialect, options);
        var diagnostics = new List<Diagnostic>(main.Diagnostics);
        var collections = new List<QueryCollectionSql>();
        foreach (var collection in query.Collections)
        {
            var text = QuerySql.RenderCollection(collection, dialect, options);
            diagnostics.AddRange(text.Diagnostics);
            collections.Add(new QueryCollectionSql(collection.Name, text.Sql, text.Parameters, [.. collection.Keys.Select(Key)]));
        }

        var name = dialect is not null && Rendering.SqlDialects.TryParse(dialect, out var parsed) ? DialectTypeMaps.Name(parsed) : query.Database.Dialect;
        var distinct = diagnostics.DistinctBy(d => (d.Rule, d.Message)).ToList();
        return (new QuerySqlPreview(query.Id, query.Name, query.Database.Id, name, main.Sql, main.Parameters, collections), distinct);
    }

    private static QueryCollectionView Collection(RQueryCollection collection)
    {
        var rendered = RootOf(collection.Query).Sql.Length == 0 ? null : QuerySql.RenderCollection(collection);
        return new QueryCollectionView(
            collection.Name,
            collection.Definition?.Attribute ?? collection.Name,
            collection.Entity?.Id,
            Tree(collection.Definition?.Query) ?? EmptyObject,
            Fields(collection.Query.Select, collection.Definition?.Query.Select),
            [.. collection.Keys.Select(Key)],
            rendered?.Sql ?? "",
            rendered?.Parameters ?? []);
    }

    private static QueryKeyView Key(RQueryKey key) =>
        new(key.Outer.Column ?? key.Outer.Alias + "." + key.Outer.ColumnName, key.ParentField, key.Hidden, key.ChildField, key.Parameter, key.Type, key.NativeType);

    private static QuerySourceView Source(RQuerySource source, QueryPredicate? on) =>
        new(source.Source, source.Alias, source.JoinKind, source.Table?.Key, source.View?.Id, source.Name, source.Schema, Tree(on));

    private static List<QueryFieldView> Fields(IReadOnlyList<RQueryField> fields, IReadOnlyList<QueryField>? files) =>
        [.. fields.Select((f, i) => new QueryFieldView(f.Name, f.Attribute?.Id,
            files is not null && i < files.Count ? Tree(files[i].Expression)!.Value : EmptyObject, f.Type, f.NativeType, f.CodeType, f.Nullable))];

    private static RQuery RootOf(RQuery query)
    {
        while (query.Parent is { } parent)
            query = parent;
        return query;
    }

    private static JsonElement? Tree<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.SerializeToElement(value, EngineJson.Options);

    private static string? OrNull(string value) => value.Length == 0 ? null : value;
}
