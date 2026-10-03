namespace Maquettiste.Engine.Resolution;

/// <summary>
/// A resolved query (added 2026-10-02): its sources resolved to the database's tables and views, its column references to their
/// columns, its parameters to native types, with the annotations (<see cref="RAnnotated"/>) of its file. The trees cross to templates
/// as plain objects (<see cref="RQueryExpression"/>, <see cref="RQueryPredicate"/>) a pack can walk per dialect; <see cref="Sql"/> is
/// the engine's own rendering for the database's dialect (<c>QuerySql</c>, the template helper <c>query_sql</c>). A nested query (a
/// collection's, or an <c>exists</c> condition's) is an <see cref="RQuery"/> too, with <see cref="Parent"/> set and no parameters,
/// paging, collections or annotations of its own.
/// </summary>
public sealed class RQuery : RAnnotated
{
    /// <inheritdoc/>
    public override string Kind => "query";

    /// <summary>The query's name (for a nested query, its collection's name, or <c>exists</c>).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The database.</summary>
    public RDatabase Database { get; internal set; } = null!;

    /// <summary>The entity each result row has, or <see langword="null"/> for an ad hoc row (the select list).</summary>
    public REntity? Entity { get; internal set; }

    /// <summary>The parameters, in order (a nested query uses its top query's).</summary>
    public IReadOnlyList<RQueryParameter> Parameters { get; internal set; } = [];

    /// <summary>The first source.</summary>
    public RQuerySource From { get; internal set; } = null!;

    /// <summary>The joined sources, in order.</summary>
    public IReadOnlyList<RQuerySource> Joins { get; internal set; } = [];

    /// <summary>The select list.</summary>
    public IReadOnlyList<RQueryField> Select { get; internal set; } = [];

    /// <summary>The row filter.</summary>
    public RQueryPredicate? Where { get; internal set; }

    /// <summary>The grouping expressions.</summary>
    public IReadOnlyList<RQueryExpression> GroupBy { get; internal set; } = [];

    /// <summary>The group filter.</summary>
    public RQueryPredicate? Having { get; internal set; }

    /// <summary>The ordering.</summary>
    public IReadOnlyList<RQueryOrder> OrderBy { get; internal set; } = [];

    /// <summary>Whether duplicate rows are removed.</summary>
    public bool Distinct { get; internal set; }

    /// <summary>The paging, or <see langword="null"/>.</summary>
    public RQueryPaging? Paging { get; internal set; }

    /// <summary>The collections filled per result row.</summary>
    public IReadOnlyList<RQueryCollection> Collections { get; internal set; } = [];

    /// <summary>The query that encloses a nested query, or <see langword="null"/> for a top query.</summary>
    public RQuery? Parent { get; internal set; }

    /// <summary>
    /// The SQL for the database's dialect with <c>@name</c> placeholders (<c>QuerySql.Render</c> with the default options), with the
    /// hidden key columns the collections need; empty when it cannot be rendered there (MQ4029).
    /// </summary>
    public string Sql { get; internal set; } = "";

    /// <summary>
    /// The tables, views, routines and database types the query (and its nested queries) reads, in first-use order; a query is a
    /// dependent of each.
    /// </summary>
    public IReadOnlyList<IResolvedObject> Uses { get; internal set; } = [];

    /// <summary>The entity bindings that read the query (erratum E43), by (entity name, entity id); empty for a nested query.</summary>
    public IReadOnlyList<REntityBinding> BoundBy { get; internal set; } = [];

    /// <summary>
    /// A copy without the ordering, for a derived table (a binding's select over the query, erratum E43): an order inside a derived
    /// table means nothing, and SQL Server refuses one without paging. A paged query keeps its order, which the paging needs.
    /// </summary>
    internal RQuery ForDerivedTable()
    {
        if (OrderBy.Count == 0 || Paging is not null)
            return this;
        var copy = (RQuery)MemberwiseClone();
        copy.OrderBy = [];
        return copy;
    }

    /// <summary>The project settings, for rendering casts in another dialect's type map.</summary>
    internal Model.ProjectSettings? Settings { get; set; }

    /// <summary>The file's record this query resolves: the <see cref="Model.Query"/>, or a nested query's <see cref="Model.QuerySubquery"/>.</summary>
    internal object? Definition { get; set; }
}

/// <summary>A parameter of a resolved query.</summary>
public sealed class RQueryParameter
{
    /// <summary>The name.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The built-in type keyword, or <see langword="null"/> when a database type gives the type.</summary>
    public string? Type { get; internal set; }

    /// <summary>The database type, when the file names one.</summary>
    public RDatabaseType? DbType { get; internal set; }

    /// <summary>The length facet.</summary>
    public int? Length { get; internal set; }

    /// <summary>The precision facet.</summary>
    public int? Precision { get; internal set; }

    /// <summary>The scale facet.</summary>
    public int? Scale { get; internal set; }

    /// <summary>The native type for the database's dialect.</summary>
    public string NativeType { get; internal set; } = "";

    /// <summary>
    /// The built-in keyword a code type map should map: <see cref="Type"/>, or for a database type its domain base (an enum type's
    /// labels are strings), else <c>string</c>.
    /// </summary>
    public string CodeType { get; internal set; } = "string";

    /// <summary>Whether the caller passes a list of values.</summary>
    public bool Collection { get; internal set; }

    /// <summary>The default value as a plain value, or <see langword="null"/>.</summary>
    public object? Default { get; internal set; }

    /// <summary>The description, or <see langword="null"/>.</summary>
    public string? Description { get; internal set; }
}

/// <summary>A source of a resolved query: the first (<see cref="JoinKind"/> <c>from</c>) or a joined one.</summary>
public sealed class RQuerySource
{
    /// <summary>The alias column references use.</summary>
    public string Alias { get; internal set; } = "";

    /// <summary><c>from</c> for the first source, else <c>inner</c>, <c>left</c>, <c>right</c>, <c>full</c> or <c>cross</c>.</summary>
    public string JoinKind { get; internal set; } = "from";

    /// <summary>The source as the file writes it (an id, a table key or an entity id).</summary>
    public string Source { get; internal set; } = "";

    /// <summary>The table, when the source is one.</summary>
    public RTable? Table { get; internal set; }

    /// <summary>The view, when the source is one.</summary>
    public RView? View { get; internal set; }

    /// <summary>The physical name of the table or view.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The schema name, or <see langword="null"/>.</summary>
    public string? Schema { get; internal set; }

    /// <summary>The join condition, or <see langword="null"/> (the first source, a cross join).</summary>
    public RQueryPredicate? On { get; internal set; }

    /// <summary>Whether the source's columns can be null in a row although the column is not (the outer side of an outer join).</summary>
    public bool Optional { get; internal set; }
}

/// <summary>A field of a resolved select list.</summary>
public sealed class RQueryField
{
    /// <summary>The field's name in the result row (the attribute's when the file gives none).</summary>
    public string Name { get; internal set; } = "";

    /// <summary>
    /// The attribute of the result entity the field fills (for a value object member, the value object attribute), or
    /// <see langword="null"/>.
    /// </summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>
    /// The member of the value object <see cref="Attribute"/> the field fills (the file writes <c>attributeId.memberId</c>), or
    /// <see langword="null"/> when the field fills the attribute itself.
    /// </summary>
    public RAttribute? Member { get; internal set; }

    /// <summary>
    /// The to-one navigation of the result entity whose foreign key the field fills (the file names the relation end the navigation
    /// leads to, or the navigation's id), or <see langword="null"/>.
    /// </summary>
    public RNavigation? Navigation { get; internal set; }

    /// <summary>
    /// The column of the result entity's table that holds the foreign key <see cref="Navigation"/> fills (its key is
    /// <c>&lt;end id&gt;.&lt;key attribute id&gt;</c>), or <see langword="null"/>.
    /// </summary>
    public RColumn? ForeignKeyColumn { get; internal set; }

    /// <summary>The value.</summary>
    public RQueryExpression Expression { get; internal set; } = null!;

    /// <summary>The built-in type keyword: the file's, else inferred from the expression; <see langword="null"/> when unknown.</summary>
    public string? Type { get; internal set; }

    /// <summary>The native type for the database's dialect, or <see langword="null"/> when unknown.</summary>
    public string? NativeType { get; internal set; }

    /// <summary>The built-in keyword a code type map should map (as <see cref="RQueryParameter.CodeType"/>), or <see langword="null"/>.</summary>
    public string? CodeType { get; internal set; }

    /// <summary>Whether the value may be null: the file's, else inferred (a nullable column, the outer side of a join, an aggregate).</summary>
    public bool Nullable { get; internal set; } = true;
}

/// <summary>An ordering term of a resolved query.</summary>
public sealed class RQueryOrder
{
    /// <summary>The value to order by.</summary>
    public RQueryExpression Expression { get; internal set; } = null!;

    /// <summary><c>asc</c> or <c>desc</c>.</summary>
    public string Direction { get; internal set; } = "asc";

    /// <summary><c>first</c>, <c>last</c> or <see langword="null"/>.</summary>
    public string? Nulls { get; internal set; }
}

/// <summary>The paging of a resolved query: each bound a parameter or a number (or neither).</summary>
public sealed class RQueryPaging
{
    /// <summary>The parameter that gives the rows to skip, or <see langword="null"/>.</summary>
    public RQueryParameter? OffsetParameter { get; internal set; }

    /// <summary>The rows to skip as a number, or <see langword="null"/>.</summary>
    public long? Offset { get; internal set; }

    /// <summary>The parameter that gives the most rows to return, or <see langword="null"/>.</summary>
    public RQueryParameter? LimitParameter { get; internal set; }

    /// <summary>The most rows to return as a number, or <see langword="null"/>.</summary>
    public long? Limit { get; internal set; }
}

/// <summary>
/// A collection of a resolved query: a nested query run once for all parent rows (a second round trip), its rows matched to their
/// parent by <see cref="Keys"/> (the correlation).
/// </summary>
public sealed class RQueryCollection
{
    /// <summary>The collection's name: the attribute's or the navigation's, or the name the file gives an ad hoc collection.</summary>
    public string Name { get; internal set; } = "";

    /// <summary>The collection attribute it fills, or <see langword="null"/>.</summary>
    public RAttribute? Attribute { get; internal set; }

    /// <summary>The to-many navigation it fills, or <see langword="null"/>.</summary>
    public RNavigation? Navigation { get; internal set; }

    /// <summary>The entity each element has, or <see langword="null"/> for an ad hoc element (the nested select list).</summary>
    public REntity? Entity { get; internal set; }

    /// <summary>The nested query.</summary>
    public RQuery Query { get; internal set; } = null!;

    /// <summary>The correlation: each equality between a column of the nested query and one of the parent's, in where order.</summary>
    public IReadOnlyList<RQueryKey> Keys { get; internal set; } = [];

    /// <summary>The position of the collection in its query's list.</summary>
    public int Index { get; internal set; }

    /// <summary>The file's record of the collection.</summary>
    internal Model.QueryCollection? Definition { get; set; }

    /// <summary>The conjuncts of the nested query's where that the keys replace in the collection's statement.</summary>
    internal HashSet<RQueryPredicate> KeyConjuncts { get; } = [];
}

/// <summary>
/// One correlation key of a collection: the parent's row carries the value as <see cref="ParentField"/>, the collection's statement
/// takes the list of parent values as the parameter <see cref="Parameter"/> and returns each row's value as <see cref="ChildField"/>.
/// </summary>
public sealed class RQueryKey
{
    /// <summary>The parent's column expression.</summary>
    public RQueryExpression Outer { get; internal set; } = null!;

    /// <summary>The nested query's expression it equals.</summary>
    public RQueryExpression Inner { get; internal set; } = null!;

    /// <summary>The parent row's field that carries the value: a select field over the same column, else a hidden key column.</summary>
    public string ParentField { get; internal set; } = "";

    /// <summary>Whether <see cref="ParentField"/> is a hidden column the parent's SQL adds after the select list.</summary>
    public bool Hidden { get; internal set; }

    /// <summary>The column of the collection's statement that carries the value for matching (<c>mq_key0</c>, <c>mq_key1</c>...).</summary>
    public string ChildField { get; internal set; } = "";

    /// <summary>The list parameter of the collection's statement (<c>mq_keys0</c>, <c>mq_keys1</c>...).</summary>
    public string Parameter { get; internal set; } = "";

    /// <summary>The value's built-in type keyword, or <see langword="null"/>.</summary>
    public string? Type { get; internal set; }

    /// <summary>The value's native type, or <see langword="null"/>.</summary>
    public string? NativeType { get; internal set; }

    /// <summary>The built-in keyword a code type map should map, or <see langword="null"/>.</summary>
    public string? CodeType { get; internal set; }
}

/// <summary>A branch of a resolved case expression.</summary>
public sealed class RQueryWhen
{
    /// <summary>The condition.</summary>
    public RQueryPredicate When { get; internal set; } = null!;

    /// <summary>The value when the condition holds.</summary>
    public RQueryExpression Then { get; internal set; } = null!;
}

/// <summary>
/// A node of a resolved expression tree. <see cref="Node"/> says which members apply: <c>column</c> (<see cref="Alias"/>,
/// <see cref="ColumnName"/>, <see cref="Column"/> or <see cref="ViewColumn"/>, <see cref="Source"/>, <see cref="IsOuter"/>),
/// <c>param</c> (<see cref="Param"/>), <c>value</c> (<see cref="Value"/>), <c>null</c>, <c>op</c> (<see cref="Op"/>,
/// <see cref="Args"/>), <c>call</c> (<see cref="Call"/>, <see cref="Routine"/>, <see cref="Args"/>), <c>case</c> (<see cref="Case"/>,
/// <see cref="Else"/>), <c>cast</c> (<see cref="Cast"/>, <see cref="Type"/>) or <c>sql</c> (<see cref="Sql"/>, <see cref="SqlText"/>).
/// <see cref="Type"/>, <see cref="NativeType"/> and <see cref="Nullable"/> are inferred for every node.
/// </summary>
public sealed class RQueryExpression
{
    /// <summary><c>column</c>, <c>param</c>, <c>value</c>, <c>null</c>, <c>op</c>, <c>call</c>, <c>case</c>, <c>cast</c> or <c>sql</c>.</summary>
    public string Node { get; internal set; } = "";

    /// <summary>A column reference as the file writes it.</summary>
    public string? Column { get; internal set; }

    /// <summary>The column's source alias.</summary>
    public string? Alias { get; internal set; }

    /// <summary>The column's physical name.</summary>
    public string? ColumnName { get; internal set; }

    /// <summary>The table column, when the source is a table.</summary>
    public RColumn? TableColumn { get; internal set; }

    /// <summary>The view column, when the source is a view.</summary>
    public RViewColumn? ViewColumn { get; internal set; }

    /// <summary>The source the column belongs to.</summary>
    public RQuerySource? Source { get; internal set; }

    /// <summary>Whether the column belongs to an enclosing query (a correlation).</summary>
    public bool IsOuter { get; internal set; }

    /// <summary>The parameter.</summary>
    public RQueryParameter? Param { get; internal set; }

    /// <summary>The literal as a plain value (string, long, decimal, double or bool).</summary>
    public object? Value { get; internal set; }

    /// <summary>A typed decimal literal's text as SQL writes it (<c>{ "value": "2.0", "type": "decimal" }</c>), else null.</summary>
    internal string? LiteralText { get; set; }

    /// <summary>Whether the node is the null literal.</summary>
    public bool Null { get; internal set; }

    /// <summary>The operator: <c>+</c>, <c>-</c>, <c>*</c>, <c>/</c>, <c>%</c> or <c>concat</c>.</summary>
    public string? Op { get; internal set; }

    /// <summary>The function name as the file writes it (a routine's id for a routine).</summary>
    public string? Call { get; internal set; }

    /// <summary>The routine a call names, or <see langword="null"/>.</summary>
    public RRoutine? Routine { get; internal set; }

    /// <summary>The operands or arguments.</summary>
    public IReadOnlyList<RQueryExpression> Args { get; internal set; } = [];

    /// <summary>The branches of a case expression.</summary>
    public IReadOnlyList<RQueryWhen> Case { get; internal set; } = [];

    /// <summary>The value of a case expression when no branch holds.</summary>
    public RQueryExpression? Else { get; internal set; }

    /// <summary>The value a cast converts.</summary>
    public RQueryExpression? Cast { get; internal set; }

    /// <summary>The opaque expression per dialect name (or <c>"*"</c>).</summary>
    public IReadOnlyDictionary<string, string> Sql { get; internal set; } = System.Collections.Frozen.FrozenDictionary<string, string>.Empty;

    /// <summary>The opaque expression for the database's dialect (or <c>"*"</c>), or <see langword="null"/>.</summary>
    public string? SqlText { get; internal set; }

    /// <summary>The built-in type keyword (a cast's target), inferred; <see langword="null"/> when unknown.</summary>
    public string? Type { get; internal set; }

    /// <summary>The native type for the database's dialect, or <see langword="null"/> when unknown.</summary>
    public string? NativeType { get; internal set; }

    /// <summary>The built-in keyword a code type map should map, or <see langword="null"/> when unknown.</summary>
    public string? CodeType { get; internal set; }

    /// <summary>Whether the value may be null.</summary>
    public bool Nullable { get; internal set; } = true;
}

/// <summary>
/// A node of a resolved predicate tree. <see cref="Node"/> says which members apply: <c>and</c> / <c>or</c> (<see cref="And"/> /
/// <see cref="Or"/>), <c>not</c> (<see cref="Not"/>), <c>compare</c> (<see cref="Op"/>, <see cref="Left"/>, <see cref="Right"/> or
/// <see cref="Values"/>) or <c>exists</c> (<see cref="Exists"/>).
/// </summary>
public sealed class RQueryPredicate
{
    /// <summary><c>and</c>, <c>or</c>, <c>not</c>, <c>compare</c> or <c>exists</c>.</summary>
    public string Node { get; internal set; } = "";

    /// <summary>The conditions of an <c>and</c>.</summary>
    public IReadOnlyList<RQueryPredicate> And { get; internal set; } = [];

    /// <summary>The conditions of an <c>or</c>.</summary>
    public IReadOnlyList<RQueryPredicate> Or { get; internal set; } = [];

    /// <summary>The condition a <c>not</c> negates.</summary>
    public RQueryPredicate? Not { get; internal set; }

    /// <summary>The comparison operator (<c>eq</c>, <c>ne</c>, <c>lt</c>, <c>le</c>, <c>gt</c>, <c>ge</c>, <c>like</c>, <c>ilike</c>,
    /// <c>in</c>, <c>notIn</c>, <c>between</c>, <c>isNull</c>, <c>isNotNull</c>).</summary>
    public string? Op { get; internal set; }

    /// <summary>The left operand.</summary>
    public RQueryExpression? Left { get; internal set; }

    /// <summary>The right operand of a two-operand comparison (for <c>in</c> and <c>notIn</c>, a single list parameter).</summary>
    public RQueryExpression? Right { get; internal set; }

    /// <summary>The right operands of <c>in</c>, <c>notIn</c> (a list) and <c>between</c> (low and high).</summary>
    public IReadOnlyList<RQueryExpression> Values { get; internal set; } = [];

    /// <summary>The nested query of an <c>exists</c>.</summary>
    public RQuery? Exists { get; internal set; }
}
