using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A query over the tables and views of a database, written as data (<c>model/databases/&lt;db&gt;/queries/</c>, added 2026-10-02):
/// sources, joins, a select list, predicates, grouping, ordering, paging and collections. Its result shape is <see cref="Entity"/>,
/// or the select list when no entity is named. A query creates nothing in the database: packs turn it into a repository method.
/// </summary>
public sealed record Query : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Query;

    /// <summary>The id of the database.</summary>
    [ElementRef(ElementKind.Database)]
    public required string Database { get; init; }

    /// <summary>The entity each result row has, or <see langword="null"/> for an ad hoc row (the select list).</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>The parameters, in order.</summary>
    public IReadOnlyList<QueryParameter> Parameters { get; init; } = [];

    /// <summary>The first source.</summary>
    public required QuerySource From { get; init; }

    /// <summary>The joined sources, in order.</summary>
    public IReadOnlyList<QueryJoin> Joins { get; init; } = [];

    /// <summary>The select list.</summary>
    public required IReadOnlyList<QueryField> Select { get; init; }

    /// <summary>The row filter.</summary>
    public QueryPredicate? Where { get; init; }

    /// <summary>The grouping expressions.</summary>
    public IReadOnlyList<QueryExpression> GroupBy { get; init; } = [];

    /// <summary>The group filter.</summary>
    public QueryPredicate? Having { get; init; }

    /// <summary>The ordering.</summary>
    public IReadOnlyList<QueryOrder> OrderBy { get; init; } = [];

    /// <summary>Whether duplicate rows are removed.</summary>
    public bool Distinct { get; init; }

    /// <summary>The rows to skip and the most rows to return.</summary>
    public QueryPaging? Paging { get; init; }

    /// <summary>The collections filled per result row.</summary>
    public IReadOnlyList<QueryCollection> Collections { get; init; } = [];
}

/// <summary>A parameter of a query.</summary>
public sealed record QueryParameter
{
    /// <summary>The name expressions use (<c>{ "param": name }</c>).</summary>
    public required string Name { get; init; }

    /// <summary>A built-in type keyword or the id of a database type of the same database.</summary>
    [ElementRef(Keyed = true)]
    public required string Type { get; init; }

    /// <summary>The length facet.</summary>
    public int? Length { get; init; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; init; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; init; }

    /// <summary>Whether the caller passes a list of values.</summary>
    public bool Collection { get; init; }

    /// <summary>The value the generated method uses when the caller passes none.</summary>
    public JsonElement? Default { get; init; }

    /// <summary>A description of the parameter.</summary>
    public string? Description { get; init; }
}

/// <summary>The first source of a query.</summary>
public sealed record QuerySource
{
    /// <summary>A table or view file id, a table key (<c>&lt;entityId&gt;@&lt;databaseId&gt;</c>) or an entity id.</summary>
    [ElementRef(Keyed = true)]
    public required string Source { get; init; }

    /// <summary>The alias, or <see langword="null"/> for the source's name.</summary>
    public string? Alias { get; init; }
}

/// <summary>A joined source of a query.</summary>
public sealed record QueryJoin
{
    /// <summary>A table or view file id, a table key or an entity id, as <see cref="QuerySource.Source"/>.</summary>
    [ElementRef(Keyed = true)]
    public required string Source { get; init; }

    /// <summary>The alias, or <see langword="null"/> for the source's name.</summary>
    public string? Alias { get; init; }

    /// <summary>The join kind.</summary>
    public QueryJoinKind Kind { get; init; } = QueryJoinKind.Inner;

    /// <summary>The join condition; none for a cross join.</summary>
    public QueryPredicate? On { get; init; }
}

/// <summary>How a source is joined.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueryJoinKind>))]
public enum QueryJoinKind
{
    /// <summary><c>inner</c>.</summary>
    [JsonStringEnumMemberName("inner")] Inner,

    /// <summary><c>left</c> (outer).</summary>
    [JsonStringEnumMemberName("left")] Left,

    /// <summary><c>right</c> (outer).</summary>
    [JsonStringEnumMemberName("right")] Right,

    /// <summary><c>full</c> (outer).</summary>
    [JsonStringEnumMemberName("full")] Full,

    /// <summary><c>cross</c>.</summary>
    [JsonStringEnumMemberName("cross")] Cross,
}

/// <summary>A field of a select list.</summary>
public sealed record QueryField
{
    /// <summary>The field's name in the result row, or <see langword="null"/> for the attribute's name.</summary>
    public string? Name { get; init; }

    /// <summary>The attribute of the result entity the field fills.</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? Attribute { get; init; }

    /// <summary>The field's type in an ad hoc row, or <see langword="null"/> to infer it.</summary>
    public string? Type { get; init; }

    /// <summary>Whether the field may be null, or <see langword="null"/> to infer it.</summary>
    public bool? Nullable { get; init; }

    /// <summary>The value.</summary>
    public required QueryExpression Expression { get; init; }
}

/// <summary>An ordering term.</summary>
public sealed record QueryOrder
{
    /// <summary>The value to order by.</summary>
    public required QueryExpression Expression { get; init; }

    /// <summary><c>asc</c> or <c>desc</c>.</summary>
    public QueryDirection Direction { get; init; } = QueryDirection.Asc;

    /// <summary>Where nulls sort, or <see langword="null"/> for the database's default.</summary>
    public QueryNulls? Nulls { get; init; }
}

/// <summary>A sort direction.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueryDirection>))]
public enum QueryDirection
{
    /// <summary><c>asc</c>.</summary>
    [JsonStringEnumMemberName("asc")] Asc,

    /// <summary><c>desc</c>.</summary>
    [JsonStringEnumMemberName("desc")] Desc,
}

/// <summary>Where nulls sort.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<QueryNulls>))]
public enum QueryNulls
{
    /// <summary><c>first</c>.</summary>
    [JsonStringEnumMemberName("first")] First,

    /// <summary><c>last</c>.</summary>
    [JsonStringEnumMemberName("last")] Last,
}

/// <summary>The paging of a query: each bound a parameter name or a number.</summary>
public sealed record QueryPaging
{
    /// <summary>The rows to skip: a JSON string (a parameter name) or a number.</summary>
    public JsonElement? Offset { get; init; }

    /// <summary>The most rows to return: a JSON string (a parameter name) or a number.</summary>
    public JsonElement? Limit { get; init; }
}

/// <summary>A collection filled per result row by a correlated nested query.</summary>
public sealed record QueryCollection
{
    /// <summary>The id of a collection attribute or of the relation end a to-many navigation leads to, or a name for an ad hoc row.</summary>
    [ElementRef(Keyed = true)]
    public required string Attribute { get; init; }

    /// <summary>The entity each element has, or <see langword="null"/> for an ad hoc element (the nested select list).</summary>
    [ElementRef(ElementKind.Entity)]
    public string? Entity { get; init; }

    /// <summary>The nested query.</summary>
    public required QuerySubquery Query { get; init; }
}

/// <summary>A query inside a collection or an <c>exists</c> condition; it may name the aliases of the queries around it.</summary>
public sealed record QuerySubquery
{
    /// <summary>The first source.</summary>
    public required QuerySource From { get; init; }

    /// <summary>The joined sources.</summary>
    public IReadOnlyList<QueryJoin> Joins { get; init; } = [];

    /// <summary>The select list (ignored by <c>exists</c>).</summary>
    public IReadOnlyList<QueryField> Select { get; init; } = [];

    /// <summary>The row filter.</summary>
    public QueryPredicate? Where { get; init; }

    /// <summary>The grouping expressions.</summary>
    public IReadOnlyList<QueryExpression> GroupBy { get; init; } = [];

    /// <summary>The group filter.</summary>
    public QueryPredicate? Having { get; init; }

    /// <summary>The ordering.</summary>
    public IReadOnlyList<QueryOrder> OrderBy { get; init; } = [];

    /// <summary>Whether duplicate rows are removed.</summary>
    public bool Distinct { get; init; }
}

/// <summary>A branch of a <c>case</c> expression.</summary>
public sealed record QueryWhen
{
    /// <summary>The condition.</summary>
    public required QueryPredicate When { get; init; }

    /// <summary>The value when the condition holds.</summary>
    public required QueryExpression Then { get; init; }
}

/// <summary>
/// A node of an expression tree: exactly one of <see cref="Column"/>, <see cref="Param"/>, <see cref="Value"/>, <see cref="Null"/>,
/// <see cref="Op"/>, <see cref="Call"/>, <see cref="Case"/>, <see cref="Cast"/> or <see cref="Sql"/> is set (the schema says which
/// others go with it).
/// </summary>
public sealed record QueryExpression
{
    /// <summary>A column: <c>alias.column_name</c>; the column part may also be an attribute id or a column key.</summary>
    [ElementRef(Keyed = true)]
    public string? Column { get; init; }

    /// <summary>A parameter's name.</summary>
    public string? Param { get; init; }

    /// <summary>A literal string, number or boolean.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>The null literal, when <see langword="true"/>.</summary>
    public bool? Null { get; init; }

    /// <summary>An operator over <see cref="Args"/>: <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c> or <c>concat</c>.</summary>
    public string? Op { get; init; }

    /// <summary>A function name, or the id of a routine of the database.</summary>
    [ElementRef(Keyed = true)]
    public string? Call { get; init; }

    /// <summary>The operands of <see cref="Op"/> or the arguments of <see cref="Call"/>.</summary>
    public IReadOnlyList<QueryExpression>? Args { get; init; }

    /// <summary>The branches of a case expression.</summary>
    public IReadOnlyList<QueryWhen>? Case { get; init; }

    /// <summary>The value of a case expression when no branch holds.</summary>
    public QueryExpression? Else { get; init; }

    /// <summary>The value a cast converts.</summary>
    public QueryExpression? Cast { get; init; }

    /// <summary>The built-in type keyword of a cast.</summary>
    public string? Type { get; init; }

    /// <summary>An opaque expression per dialect name, or <c>"*"</c>.</summary>
    public IReadOnlyDictionary<string, string>? Sql { get; init; }
}

/// <summary>
/// A node of a predicate tree: exactly one of <see cref="And"/>, <see cref="Or"/>, <see cref="Not"/>, <see cref="Op"/> (with
/// <see cref="Left"/> and <see cref="Right"/>) or <see cref="Exists"/>.
/// </summary>
public sealed record QueryPredicate
{
    /// <summary>Conditions that must all hold.</summary>
    public IReadOnlyList<QueryPredicate>? And { get; init; }

    /// <summary>Conditions of which one must hold.</summary>
    public IReadOnlyList<QueryPredicate>? Or { get; init; }

    /// <summary>A condition that must not hold.</summary>
    public QueryPredicate? Not { get; init; }

    /// <summary>
    /// A comparison: <c>eq</c>, <c>ne</c>, <c>lt</c>, <c>le</c>, <c>gt</c>, <c>ge</c>, <c>like</c>, <c>ilike</c>, <c>in</c>,
    /// <c>notIn</c>, <c>between</c>, <c>isNull</c> or <c>isNotNull</c>.
    /// </summary>
    public string? Op { get; init; }

    /// <summary>The left operand.</summary>
    public QueryExpression? Left { get; init; }

    /// <summary>The right operand: one expression, or a list for <c>in</c>, <c>notIn</c> and <c>between</c> (a single expression
    /// is read as a list of one).</summary>
    [JsonConverter(typeof(QueryOperandsConverter))]
    public IReadOnlyList<QueryExpression>? Right { get; init; }

    /// <summary>A nested query that must return a row.</summary>
    public QuerySubquery? Exists { get; init; }
}

/// <summary>Reads <see cref="QueryPredicate.Right"/> as one expression or a list; writes a list of one as the expression.</summary>
internal sealed class QueryOperandsConverter : JsonConverter<IReadOnlyList<QueryExpression>>
{
    /// <inheritdoc/>
    public override IReadOnlyList<QueryExpression>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
            return JsonSerializer.Deserialize<List<QueryExpression>>(ref reader, options);
        var single = JsonSerializer.Deserialize<QueryExpression>(ref reader, options);
        return single is null ? null : [single];
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<QueryExpression> value, JsonSerializerOptions options)
    {
        if (value.Count == 1)
            JsonSerializer.Serialize(writer, value[0], options);
        else
            JsonSerializer.Serialize(writer, value, options);
    }
}
