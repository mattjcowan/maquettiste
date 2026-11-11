using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>How <see cref="QuerySql"/> writes parameters (engine-design.md section 7, "Queries").</summary>
public sealed record QuerySqlOptions
{
    /// <summary>The default options: <c>@name</c> placeholders, lists expanded by the data access library.</summary>
    public static QuerySqlOptions Default { get; } = new();

    /// <summary>
    /// The placeholder style: <c>@</c> (<c>@name</c>, the default), <c>:</c> (<c>:name</c>) or <c>$</c> (<c>$1</c>, <c>$2</c>...
    /// numbered by first appearance; <see cref="QuerySqlText.Parameters"/> lists the names in that order).
    /// </summary>
    public string Placeholder { get; init; } = "@";

    /// <summary>
    /// How a list parameter (<c>collection: true</c>, and a collection's key list) is written in <c>in</c> and <c>notIn</c>:
    /// <c>expand</c> (the default: <c>x IN @ids</c>, for a data access library that expands a list into its items) or <c>any</c>
    /// (<c>x = ANY(@ids)</c> with an array parameter, on PostgreSQL; other dialects expand).
    /// </summary>
    public string Lists { get; init; } = "expand";
}

/// <summary>A rendered statement.</summary>
/// <param name="Sql">The SQL text, one clause per line.</param>
/// <param name="Parameters">The parameter names in first-appearance order (the positions of <c>$n</c> placeholders).</param>
/// <param name="Diagnostics">What could not be rendered (MQ4029: an <c>sql</c> expression without a text for the dialect).</param>
public sealed record QuerySqlText(string Sql, IReadOnlyList<string> Parameters, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>
/// Renders a resolved query as parameterised SQL for a dialect (added 2026-10-02; engine-design.md section 7, "Queries"): identifiers
/// quoted by the database's quoting setting, functions spelled per dialect, <c>ilike</c> lowered where the dialect has none, paging as
/// <c>LIMIT/OFFSET</c> or <c>OFFSET … FETCH NEXT</c>, and each collection as a statement of its own run once for every parent row (a
/// second round trip): the parent's statement carries the correlation columns, the collection's takes their values as a list.
/// </summary>
public static class QuerySql
{
    /// <summary>
    /// Renders a query (its collections excluded, their hidden key columns included). A query written as SQL text renders its text for
    /// the dialect (its own, else <c>"*"</c>'s; neither is MQ4029) as written, with only the white space at both ends removed (a final
    /// <c>;</c> stays), its <c>:name</c> placeholders in the options' style (sql-queries-design.md decision 5);
    /// <see cref="QuerySqlOptions.Lists"/> does not apply to it.
    /// </summary>
    /// <param name="query">The resolved query.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the query's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/>.</param>
    /// <returns>The statement.</returns>
    /// <exception cref="ArgumentException">The dialect or an option is not one this renderer knows.</exception>
    public static QuerySqlText Render(RQuery query, string? dialect = null, QuerySqlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        var writer = new Writer(query, dialect, options);
        if (query.Form == "sql")
            writer.HandWritten(query);
        else
            writer.Top(query);
        return writer.Result();
    }

    /// <summary>Renders the statement of one collection: its rows for every parent row at once, matched by the key columns.</summary>
    /// <param name="collection">The collection.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the query's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/>.</param>
    /// <returns>The statement.</returns>
    /// <exception cref="ArgumentException">The dialect or an option is not one this renderer knows.</exception>
    public static QuerySqlText RenderCollection(RQueryCollection collection, string? dialect = null, QuerySqlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var writer = new Writer(collection.Query, dialect, options);
        writer.Collection(collection);
        return writer.Result();
    }

    /// <summary>
    /// A query's statement in two parts (sql-queries-design.md decision 16): its <c>WITH</c> clause (empty without named queries) and
    /// the statement after it, rendered as one text so that the placeholders are numbered in text order; <see cref="QuerySqlText.Parameters"/>
    /// lists both parts' names. A binding writes the <c>WITH</c> before its own select over the statement. The statement is the body
    /// of a derived table: a query written as SQL text loses its final <c>;</c> and what follows it (<see cref="Wrapped"/>).
    /// </summary>
    internal static (string With, QuerySqlText Statement) RenderParts(RQuery query, string? dialect, QuerySqlOptions? options)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Form == "sql")
        {
            var handWritten = new Writer(query, dialect, options);
            handWritten.HandWritten(query, wrapped: true);
            return ("", handWritten.Result());
        }

        if (query.With.Count == 0)
            return ("", Render(query, dialect, options));
        var writer = new Writer(query, dialect, options);
        writer.Top(query);
        var whole = writer.Result();
        var start = writer.StatementStart;
        return (start == 0 ? "" : whole.Sql[..(start - 1)], whole with { Sql = whole.Sql[start..] });
    }

    /// <summary>
    /// The parameter names of a query's statement in placeholder order (the positions of <c>$n</c> placeholders), as
    /// <see cref="Render"/> lists them in <see cref="QuerySqlText.Parameters"/>; the template helper <c>query_sql_parameters</c>.
    /// </summary>
    /// <param name="query">The resolved query.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the query's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/>.</param>
    /// <returns>The names, each once, in first-appearance order.</returns>
    public static IReadOnlyList<string> Parameters(RQuery query, string? dialect = null, QuerySqlOptions? options = null) =>
        Render(query, dialect, options).Parameters;

    /// <summary>The aggregate functions: a query whose select list, having or order calls one is grouped.</summary>
    private static readonly FrozenSet<string> Aggregates = FrozenSet.Create(StringComparer.Ordinal,
        "COUNT", "COUNT_BIG", "SUM", "MIN", "MAX", "AVG", "STRING_AGG", "ARRAY_AGG", "GROUP_CONCAT", "LISTAGG", "JSON_AGG", "JSONB_AGG",
        "JSON_ARRAYAGG", "JSON_OBJECTAGG", "JSON_GROUP_ARRAY", "JSON_GROUP_OBJECT", "BOOL_AND", "BOOL_OR", "EVERY", "BIT_AND", "BIT_OR",
        "BIT_XOR", "STDDEV", "STDDEV_POP", "STDDEV_SAMP", "STDEV", "STDEVP", "VARIANCE", "VAR_POP", "VAR_SAMP", "VAR", "VARP", "ANY_VALUE");

    /// <summary>Whether a query is grouped: it has a GROUP BY, or its select list, having or order calls an aggregate function.</summary>
    internal static bool IsGrouped(RQuery query) =>
        query.GroupBy.Count > 0 || query.Select.Any(f => f.Expression is { } e && HasAggregate(e)) || (query.Having is { } having && HasAggregate(having))
        || query.OrderBy.Any(o => HasAggregate(o.Expression));

    /// <summary>Whether an expression calls an aggregate function (outside nested queries).</summary>
    internal static bool HasAggregate(RQueryExpression expression) =>
        (expression.Node == "call" && expression.Routine is null && expression.Call is { } call && Aggregates.Contains(call.ToUpperInvariant()))
        || expression.Args.Any(HasAggregate)
        || (expression.Cast is { } cast && HasAggregate(cast))
        || expression.Case.Any(w => HasAggregate(w.When) || HasAggregate(w.Then))
        || (expression.Else is { } otherwise && HasAggregate(otherwise));

    private static bool HasAggregate(RQueryPredicate predicate) =>
        predicate.And.Any(HasAggregate) || predicate.Or.Any(HasAggregate) || (predicate.Not is { } not && HasAggregate(not))
        || (predicate.Left is { } left && HasAggregate(left)) || (predicate.Right is { } right && HasAggregate(right)) || predicate.Values.Any(HasAggregate);

    /// <summary>
    /// The type a MySQL <c>CAST</c> takes for a built-in keyword: MySQL casts only to CHAR, SIGNED, UNSIGNED, DECIMAL(p,s), DATE,
    /// DATETIME, TIME, BINARY, JSON and DOUBLE.
    /// </summary>
    /// <param name="keyword">The keyword.</param>
    /// <param name="decimalType">The native decimal type with its precision and scale (<c>decimal(18,2)</c>).</param>
    /// <returns>The cast type.</returns>
    internal static string MySqlCastType(string keyword, string decimalType) => keyword switch
    {
        "int16" or "int32" or "int64" or "bool" or "duration" => "SIGNED",
        "binary" => "BINARY",
        "decimal" => decimalType.ToUpperInvariant(),
        "date" => "DATE",
        "datetime" or "datetimeOffset" => "DATETIME",
        "time" => "TIME",
        "json" => "JSON",
        "float" or "double" => "DOUBLE",
        _ => "CHAR",
    };

    /// <summary>One expression as the query's statement writes it for its database's dialect (to compare two expressions).</summary>
    internal static string ExpressionText(RQuery query, RQueryExpression expression) => new Writer(query, null, null).Text(expression);

    /// <summary>
    /// A condition over one row in PostgreSQL, as a CHECK needs it (row mode, next-phase-3-design.md section 3.1): column names
    /// unqualified and <c>COLLATE "C"</c> after each string operand of a comparison, <c>in</c>, <c>between</c>, <c>distinct</c> or
    /// <c>match</c>.
    /// </summary>
    /// <param name="condition">The condition.</param>
    /// <param name="quoting">How the database quotes identifiers.</param>
    /// <returns>The SQL text.</returns>
    internal static string RowText(RQueryPredicate condition, Quoting quoting) => new Writer(Dialect.PostgreSql, quoting).Condition(condition);

    /// <summary>A value over one row in PostgreSQL, in row mode (<see cref="RowText(RQueryPredicate, Quoting)"/>).</summary>
    /// <param name="value">The value.</param>
    /// <param name="quoting">How the database quotes identifiers.</param>
    /// <returns>The SQL text.</returns>
    internal static string RowText(RQueryExpression value, Quoting quoting) => new Writer(Dialect.PostgreSql, quoting).Text(value);

    /// <summary>The text an <c>sql</c> expression has for a dialect: its own, else <c>"*"</c>'s; a blank text counts as none.</summary>
    internal static string? SqlFor(IReadOnlyDictionary<string, string> texts, string dialect) =>
        texts.TryGetValue(dialect, out var text) && !string.IsNullOrWhiteSpace(text) ? text
        : texts.TryGetValue("*", out text) && !string.IsNullOrWhiteSpace(text) ? text
        : null;

    /// <summary>
    /// A SQL text as a statement (sql-queries-design.md decision 5): the text as written, with only the white space at both ends
    /// removed (<see cref="Rendering.SqlText.IsWhite"/>). A final <c>;</c> stays: some statements need one (a SQL Server MERGE, an
    /// Oracle block's <c>END;</c>) and others refuse one, and only the team knows which.
    /// </summary>
    internal static string Statement(string text) => Rendering.SqlText.Trim(text);

    /// <summary>
    /// A SQL text as the body of a derived table (<c>FROM (&lt;text&gt;) q</c>, decision 5): <see cref="Statement"/>, then cut before its
    /// final <c>;</c> when it has one (a <c>;</c> outside parentheses, comments, strings and quoted identifiers, with only white space
    /// and comments after it, which go with it), and the white space at the end removed again.
    /// </summary>
    internal static string Wrapped(string text, Dialect dialect)
    {
        var statement = Statement(text);
        return Rendering.SqlText.Scan(statement, dialect).FinalSemicolon is { } at ? Rendering.SqlText.Trim(statement[..at]) : statement;
    }

    /// <summary>The SQL spelling of the functions the renderer knows, per dialect; any other name is written as given.</summary>
    internal static string? FunctionName(string name, Dialect dialect) => name.ToLowerInvariant() switch
    {
        "lower" => "LOWER",
        "upper" => "UPPER",
        "coalesce" => "COALESCE",
        "count" => "COUNT",
        "sum" => "SUM",
        "min" => "MIN",
        "max" => "MAX",
        "avg" => "AVG",
        "length" => dialect == Dialect.SqlServer ? "LEN" : dialect == Dialect.MySql ? "CHAR_LENGTH" : "LENGTH",
        "now" => "CURRENT_TIMESTAMP",
        _ => null,
    };

    private sealed class Writer
    {
        private readonly RQuery? _root;
        private readonly bool _row;
        private readonly Dialect _dialect;
        private readonly string _dialectName;
        private readonly Quoting _quoting;
        private readonly QuerySqlOptions _options;
        private readonly List<string> _parameters = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly StringBuilder _sb = new();
        private string _newline = "\n";

        /// <summary>Where the statement starts after the <c>WITH</c> clause (0 without one).</summary>
        public int StatementStart { get; private set; }
        private IReadOnlyDictionary<string, string>? _typeMap;
        private EffectiveConventions? _conventions;

        public Writer(RQuery query, string? dialect, QuerySqlOptions? options)
        {
            _root = Root(query);
            var name = dialect ?? _root!.Database.Dialect;
            if (!SqlDialects.TryParse(name, out _dialect))
                throw new ArgumentException($"'{name}' is not a dialect (postgresql, sqlserver, mysql, sqlite, oracle).", nameof(dialect));
            _dialectName = DialectTypeMaps.Name(_dialect);
            SqlDialects.TryParseQuoting(_root!.Database.Quoting, out _quoting);
            _options = options ?? QuerySqlOptions.Default;
            if (_options.Placeholder is not ("@" or ":" or "$"))
                throw new ArgumentException($"The placeholder style '{_options.Placeholder}' is not @, : or $.", nameof(options));
            if (_options.Lists is not ("expand" or "any"))
                throw new ArgumentException($"The list style '{_options.Lists}' is not expand or any.", nameof(options));
        }

        // Row mode: one row's condition or value, with no query around it.
        public Writer(Dialect dialect, Quoting quoting)
        {
            _row = true;
            _dialect = dialect;
            _dialectName = DialectTypeMaps.Name(dialect);
            _quoting = quoting;
            _options = QuerySqlOptions.Default;
        }

        public string Condition(RQueryPredicate predicate) => Predicate(predicate, true);

        public QuerySqlText Result() => new(_sb.ToString(), [.. _parameters], [.. _diagnostics]);

        public string Text(RQueryExpression expression) => Expression(expression);

        private static RQuery Root(RQuery query)
        {
            while (query.Parent is { } parent)
                query = parent;
            return query;
        }

        public void Top(RQuery query)
        {
            With(query.With);
            var columns = query.Select.Select(f => Expression(f.Expression!) + " AS " + Q(f.Name)).ToList();
            var hidden = new HashSet<string>(StringComparer.Ordinal);
            var hiddenKeys = new List<RQueryExpression>();
            foreach (var collection in query.Collections)
            {
                foreach (var key in collection.Keys)
                {
                    if (key.Hidden && hidden.Add(key.ParentField))
                    {
                        columns.Add(Expression(key.Outer) + " AS " + Q(key.ParentField));
                        hiddenKeys.Add(key.Outer);
                    }
                }
            }

            Select(query.Distinct, columns);
            // A grouped parent groups by its hidden key columns too, or they would be neither grouped nor aggregated.
            Body(query, query.Where, null, IsGrouped(query) ? hiddenKeys : []);
            Order(query.OrderBy, query.Paging is not null);
            Paging(query.Paging);
        }

        /// <summary>
        /// A query written as SQL text (sql-queries-design.md decisions 4 and 5): the text for the dialect with the white space at both
        /// ends removed, each <c>:name</c> placeholder the scanner finds written in the options' style; nothing else changes. Wrapped
        /// in a derived table, it also loses its final <c>;</c> and the comments after it (<see cref="Wrapped"/>).
        /// </summary>
        public void HandWritten(RQuery query, bool wrapped = false)
        {
            if (SqlFor(query.Texts, _dialectName) is not { } text)
            {
                _diagnostics.Add(RuleCatalog.Create("MQ4029",
                    $"Query '{query.Name}' is written as SQL text without a text for {_dialectName} (and no \"*\" text), so it cannot be rendered there.",
                    query.Id, null, "/sql"));
                return;
            }

            var body = wrapped ? Wrapped(text, _dialect) : Statement(text);
            var at = 0;
            foreach (var placeholder in Rendering.SqlText.Scan(body, _dialect).Placeholders)
            {
                _sb.Append(body, at, placeholder.Offset - at).Append(Placeholder(placeholder.Name));
                at = placeholder.Offset + placeholder.Length;
            }

            _sb.Append(body, at, body.Length - at);
        }

        public void Collection(RQueryCollection collection)
        {
            var query = collection.Query;
            // A collection's statement runs on its own: it repeats the named queries it reads, directly or through others, in list order.
            var reads = new HashSet<RNamedQuery>();
            NamedReads(query, reads);
            With([.. reads.OrderBy(n => n.Index)]);
            var columns = query.Select.Select(f => Expression(f.Expression!) + " AS " + Q(f.Name)).ToList();
            foreach (var key in collection.Keys)
                columns.Add(Expression(key.Inner) + " AS " + Q(key.ChildField));
            Select(query.Distinct, columns);
            // A grouped collection groups by its key columns too: each group then belongs to one parent row.
            Body(query, query.Where, collection, IsGrouped(query) ? [.. collection.Keys.Select(k => k.Inner)] : []);
            Order(query.OrderBy, false);
        }

        /// <summary>
        /// The <c>WITH</c> clause of named queries (sql-queries-design.md decision 14): <c>WITH RECURSIVE</c> where the dialect has the
        /// keyword and one entry is recursive; each entry with its column list, its clauses indented one level, and a recursive one's
        /// repeating part after <c>UNION ALL</c> or <c>UNION</c>. Nothing without entries.
        /// </summary>
        private void With(IReadOnlyList<RNamedQuery> entries)
        {
            if (entries.Count == 0)
                return;
            var keyword = entries.Any(e => e.Recursive is not null) && _dialect is Dialect.PostgreSql or Dialect.MySql or Dialect.Sqlite;
            _sb.Append(keyword ? "WITH RECURSIVE " : "WITH ");
            var newline = _newline;
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (i > 0)
                    _sb.Append(',').Append(newline);
                _sb.Append(Q(entry.Name)).Append(" (").Append(string.Join(", ", entry.Columns.Select(c => Q(c.Name)))).Append(") AS (");
                _newline = newline + "    ";
                _sb.Append(_newline);
                Part(entry.Query);
                if (entry.Recursive is { } recursive)
                {
                    _sb.Append(_newline).Append(entry.Union == "distinct" ? "UNION" : "UNION ALL").Append(_newline);
                    Part(recursive);
                }

                _newline = newline;
                _sb.Append(_newline).Append(')');
            }

            _sb.Append(_newline);
            StatementStart = _sb.Length;
        }

        /// <summary>A named query's anchor or repeating part: its select list and body.</summary>
        private void Part(RQuery part)
        {
            Select(part.Distinct, [.. part.Select.Select(f => Expression(f.Expression!) + " AS " + Q(f.Name))]);
            Body(part, part.Where, null, []);
        }

        /// <summary>The named queries a query reads through its sources and nested queries, and those they read in turn.</summary>
        private static void NamedReads(RQuery query, HashSet<RNamedQuery> reads)
        {
            foreach (var source in query.Joins.Prepend(query.From))
            {
                if (source?.Named is { } named && reads.Add(named))
                {
                    NamedReads(named.Query, reads);
                    if (named.Recursive is { } recursive)
                        NamedReads(recursive, reads);
                }

                if (source?.On is { } on)
                    NamedReads(on, reads);
            }

            foreach (var expression in query.Select.Select(f => f.Expression).OfType<RQueryExpression>().Concat(query.GroupBy).Concat(query.OrderBy.Select(o => o.Expression)))
                NamedReads(expression, reads);
            if (query.Where is { } where)
                NamedReads(where, reads);
            if (query.Having is { } having)
                NamedReads(having, reads);
        }

        private static void NamedReads(RQueryPredicate predicate, HashSet<RNamedQuery> reads)
        {
            if (predicate.Exists is { } exists)
                NamedReads(exists, reads);
            foreach (var p in predicate.And.Concat(predicate.Or))
                NamedReads(p, reads);
            if (predicate.Not is { } not)
                NamedReads(not, reads);
            foreach (var e in predicate.Values.Append(predicate.Left).Append(predicate.Right))
            {
                if (e is not null)
                    NamedReads(e, reads);
            }
        }

        private static void NamedReads(RQueryExpression expression, HashSet<RNamedQuery> reads)
        {
            foreach (var e in expression.Args.Append(expression.Cast).Append(expression.Else).Concat(expression.Case.Select(w => w.Then)))
            {
                if (e is not null)
                    NamedReads(e, reads);
            }

            foreach (var when in expression.Case)
                NamedReads(when.When, reads);
            if (expression.Pred is { } pred)
                NamedReads(pred, reads);
        }

        private void Select(bool distinct, List<string> columns)
        {
            _sb.Append(distinct ? "SELECT DISTINCT " : "SELECT ").Append(string.Join(", ", columns));
        }

        /// <summary>
        /// FROM, the joins, WHERE (a collection's key equalities replaced by its key lists), GROUP BY (with <paramref name="keys"/>, the
        /// key columns a grouped statement adds, each once) and HAVING.
        /// </summary>
        private void Body(RQuery query, RQueryPredicate? where, RQueryCollection? collection, IReadOnlyList<RQueryExpression> keys)
        {
            _sb.Append(_newline).Append("FROM ").Append(Source(query.From!)); // a SQL text renders apart (sql-queries-design.md section 11)
            foreach (var join in query.Joins)
            {
                _sb.Append(_newline).Append(join.JoinKind switch
                {
                    "left" => "LEFT JOIN ",
                    "right" => "RIGHT JOIN ",
                    "full" => "FULL JOIN ",
                    "cross" => "CROSS JOIN ",
                    _ => "INNER JOIN ",
                }).Append(Source(join));
                if (join.JoinKind != "cross" && join.On is { } on)
                    _sb.Append(" ON ").Append(Predicate(on, true));
            }

            var conditions = new List<string>();
            if (where is not null)
            {
                if (collection is not null)
                {
                    foreach (var conjunct in where.Node == "and" ? where.And : [where])
                    {
                        if (!collection.KeyConjuncts.Contains(conjunct))
                            conditions.Add(Predicate(conjunct, false));
                    }
                }
                else
                {
                    conditions.Add(Predicate(where, true));
                }
            }

            if (collection is not null)
            {
                foreach (var key in collection.Keys)
                    conditions.Add(InList(Expression(key.Inner), key.Parameter, negate: false));
            }

            if (conditions.Count > 0)
                _sb.Append(_newline).Append("WHERE ").Append(string.Join(" AND ", conditions));
            var groups = query.GroupBy.Select(Expression).ToList();
            foreach (var key in keys)
            {
                var text = Expression(key);
                if (!groups.Contains(text, StringComparer.Ordinal))
                    groups.Add(text);
            }

            if (groups.Count > 0)
                _sb.Append(_newline).Append("GROUP BY ").Append(string.Join(", ", groups));
            if (query.Having is { } having)
                _sb.Append(_newline).Append("HAVING ").Append(Predicate(having, true));
        }

        private void Order(IReadOnlyList<RQueryOrder> orderBy, bool paged)
        {
            var terms = new List<string>();
            foreach (var order in orderBy)
            {
                var expression = Expression(order.Expression);
                var direction = order.Direction == "desc" ? " DESC" : " ASC";
                if (order.Nulls is { } nulls)
                {
                    if (_dialect is Dialect.PostgreSql or Dialect.Oracle or Dialect.Sqlite)
                    {
                        terms.Add(expression + direction + (nulls == "first" ? " NULLS FIRST" : " NULLS LAST"));
                        continue;
                    }

                    // No NULLS FIRST/LAST: sort on whether the value is null first.
                    terms.Add("CASE WHEN " + expression + " IS NULL THEN " + (nulls == "first" ? "0 ELSE 1" : "1 ELSE 0") + " END");
                }

                terms.Add(expression + direction);
            }

            if (terms.Count == 0 && paged && _dialect == Dialect.SqlServer)
                terms.Add("(SELECT NULL)"); // OFFSET … FETCH needs an ORDER BY
            if (terms.Count > 0)
                _sb.Append('\n').Append("ORDER BY ").Append(string.Join(", ", terms));
        }

        private void Paging(RQueryPaging? paging)
        {
            if (paging is null)
                return;
            var offset = paging.OffsetParameter is { } op ? Placeholder(op.Name) : paging.Offset?.ToString(CultureInfo.InvariantCulture);
            var limit = paging.LimitParameter is { } lp ? Placeholder(lp.Name) : paging.Limit?.ToString(CultureInfo.InvariantCulture);
            if (offset is null && limit is null)
                return;
            switch (_dialect)
            {
                case Dialect.SqlServer:
                    _sb.Append('\n').Append("OFFSET ").Append(offset ?? "0").Append(" ROWS");
                    if (limit is not null)
                        _sb.Append(" FETCH NEXT ").Append(limit).Append(" ROWS ONLY");
                    break;
                case Dialect.Oracle:
                    if (offset is not null)
                        _sb.Append('\n').Append("OFFSET ").Append(offset).Append(" ROWS");
                    if (limit is not null)
                        _sb.Append('\n').Append("FETCH NEXT ").Append(limit).Append(" ROWS ONLY");
                    break;
                default:
                    if (limit is not null)
                        _sb.Append('\n').Append("LIMIT ").Append(limit);
                    else if (_dialect == Dialect.Sqlite)
                        _sb.Append('\n').Append("LIMIT -1");
                    else if (_dialect == Dialect.MySql)
                        _sb.Append('\n').Append("LIMIT 18446744073709551615");
                    if (offset is not null)
                        _sb.Append(limit is null && _dialect == Dialect.PostgreSql ? "\n" : " ").Append("OFFSET ").Append(offset);
                    break;
            }
        }

        private string Source(RQuerySource source)
        {
            var name = source.Schema is { Length: > 0 } schema && _dialect != Dialect.Sqlite ? Q(schema) + "." + Q(source.Name) : Q(source.Name);
            return name + " " + Q(source.Alias);
        }

        private string Q(string name) => SqlDialects.Quote(name, _dialect, _quoting);

        private string Placeholder(string name)
        {
            var index = _parameters.IndexOf(name);
            if (index < 0)
            {
                _parameters.Add(name);
                index = _parameters.Count - 1;
            }

            return _options.Placeholder switch
            {
                "$" => "$" + (index + 1).ToString(CultureInfo.InvariantCulture),
                ":" => ":" + name,
                _ => "@" + name,
            };
        }

        private string InList(string left, string parameter, bool negate)
        {
            if (_options.Lists == "any" && _dialect == Dialect.PostgreSql)
                return left + (negate ? " <> ALL(" : " = ANY(") + Placeholder(parameter) + ")";
            return left + (negate ? " NOT IN " : " IN ") + Placeholder(parameter);
        }

        private string Predicate(RQueryPredicate predicate, bool top)
        {
            switch (predicate.Node)
            {
                case "and":
                case "or":
                {
                    var items = predicate.Node == "and" ? predicate.And : predicate.Or;
                    var text = string.Join(predicate.Node == "and" ? " AND " : " OR ", items.Select(p => Predicate(p, false)));
                    return top || items.Count == 1 ? text : "(" + text + ")";
                }

                case "not":
                    return "NOT (" + Predicate(predicate.Not!, true) + ")";
                case "exists":
                    return "EXISTS (" + Subquery(predicate.Exists!) + ")";
                case "match":
                    return Operand(predicate.Left!) + " ~ " + Expression(predicate.Right!);
                case "distinct":
                    return Operand(predicate.Left!) + (predicate.Op == "notDistinct" ? " IS NOT DISTINCT FROM " : " IS DISTINCT FROM ") + Operand(predicate.Right!);
                default:
                    return Compare(predicate);
            }
        }

        // An operand of a comparison; in row mode a string compares by code point, whatever the column's collation.
        private string Operand(RQueryExpression expression) =>
            _row && expression.Type == "string" ? Expression(expression) + " COLLATE \"C\"" : Expression(expression);

        private string Compare(RQueryPredicate predicate)
        {
            var left = predicate.Op is "isNull" or "isNotNull" ? Expression(predicate.Left!) : Operand(predicate.Left!);
            var op = predicate.Op;
            switch (op)
            {
                case "isNull":
                    return left + " IS NULL";
                case "isNotNull":
                    return left + " IS NOT NULL";
                case "in" or "notIn":
                    if (predicate.Right is { Node: "param", Param: { Collection: true } list })
                        return InList(left, list.Name, op == "notIn");
                    return left + (op == "notIn" ? " NOT IN (" : " IN (") + string.Join(", ", predicate.Values.Select(Operand)) + ")";
                case "between":
                    return left + " BETWEEN " + Operand(predicate.Values[0]) + " AND " + Operand(predicate.Values[1]);
                case "ilike" when _dialect != Dialect.PostgreSql:
                    return "LOWER(" + left + ") LIKE LOWER(" + Expression(predicate.Right!) + ")";
            }

            var symbol = op switch
            {
                "eq" => " = ",
                "ne" => " <> ",
                "lt" => " < ",
                "le" => " <= ",
                "gt" => " > ",
                "ge" => " >= ",
                "like" => " LIKE ",
                "ilike" => " ILIKE ",
                _ => " " + op + " ",
            };
            return left + symbol + Operand(predicate.Right!);
        }

        /// <summary>
        /// A nested query inline (an exists condition): SELECT 1 and its body, each clause on a line of its own indented one level
        /// deeper than the statement around it (the text of literals and sql expressions is written as is).
        /// </summary>
        private string Subquery(RQuery query)
        {
            var saved = _sb.ToString();
            var newline = _newline;
            _sb.Clear();
            _newline = newline + "    ";
            Select(false, ["1"]);
            Body(query, query.Where, null, []);
            var text = _sb.ToString();
            _newline = newline;
            _sb.Clear().Append(saved);
            return text;
        }

        private string Expression(RQueryExpression expression)
        {
            switch (expression.Node)
            {
                case "column":
                    return _row ? Q(expression.ColumnName!) : Q(expression.Alias!) + "." + Q(expression.ColumnName!);
                case "typed":
                    return expression.NativeType + " " + SqlDialects.Literal(expression.LiteralText, _dialect);
                case "pred":
                    return "(" + Predicate(expression.Pred!, true) + ")";
                case "param":
                    return Placeholder(expression.Param!.Name);
                case "value":
                    return expression.LiteralText ?? SqlDialects.Literal(expression.Value, _dialect);
                case "null":
                    return "NULL";
                case "op":
                    return Operation(expression);
                case "call":
                    return Call(expression);
                case "case":
                {
                    var text = new StringBuilder("CASE");
                    foreach (var when in expression.Case)
                        text.Append(" WHEN ").Append(Predicate(when.When, true)).Append(" THEN ").Append(Expression(when.Then));
                    if (expression.Else is { } otherwise)
                        text.Append(" ELSE ").Append(Expression(otherwise));
                    return text.Append(" END").ToString();
                }

                case "cast":
                    return "CAST(" + Expression(expression.Cast!) + " AS " + CastType(expression) + ")";
                case "sql":
                {
                    if (SqlFor(expression.Sql, _dialectName) is { } text)
                        return text;
                    _diagnostics.Add(RuleCatalog.Create("MQ4029",
                        $"Query '{_root?.Name}' has an sql expression without a text for {_dialectName} (and no \"*\" text), so it cannot be rendered there.",
                        _root?.Id));
                    return "NULL";
                }

                default:
                    return "NULL";
            }
        }

        private string Operation(RQueryExpression expression)
        {
            var args = expression.Args.Select(Expression).ToList();
            if (args.Count == 1)
                return "(" + (expression.Op == "-" ? args[0].StartsWith('-') ? "- " : "-" : "") + args[0] + ")";
            switch (expression.Op)
            {
                case "concat" when _dialect is Dialect.SqlServer or Dialect.MySql:
                    return "CONCAT(" + string.Join(", ", args) + ")";
                case "concat":
                    return "(" + string.Join(" || ", args) + ")";
                case "%" when _dialect == Dialect.Oracle:
                    return args.Skip(1).Aggregate(args[0], (left, right) => "MOD(" + left + ", " + right + ")");
                default:
                    return "(" + string.Join(" " + expression.Op + " ", args) + ")";
            }
        }

        private string Call(RQueryExpression expression)
        {
            var args = expression.Args.Select(Expression).ToList();
            if (expression.Routine is { } routine)
            {
                var name = routine.Schema is { Length: > 0 } schema && _dialect != Dialect.Sqlite ? Q(schema) + "." + Q(routine.Name) : Q(routine.Name);
                return name + "(" + string.Join(", ", args) + ")";
            }

            if (expression.Template is { } template)
                return Fill(template, args);
            var call = expression.Call!;
            var spelled = FunctionName(call, _dialect);
            if (spelled == "CURRENT_TIMESTAMP")
                return spelled;
            if (spelled == "COUNT" && args.Count == 0)
                return "COUNT(*)";
            return (spelled ?? call) + "(" + string.Join(", ", args) + ")";
        }

        // A template's placeholders: {0}, {1}… one argument, {*} all of them, {rest} those after the first.
        private static string Fill(string template, List<string> args)
        {
            var text = new StringBuilder();
            for (var i = 0; i < template.Length; i++)
            {
                var close = template[i] == '{' ? template.IndexOf('}', i) : -1;
                if (close < 0)
                {
                    text.Append(template[i]);
                    continue;
                }

                var name = template[(i + 1)..close];
                text.Append(name switch
                {
                    "*" => string.Join(", ", args),
                    "rest" => string.Join(", ", args.Skip(1)),
                    _ => args[int.Parse(name, CultureInfo.InvariantCulture)],
                });
                i = close;
            }

            return text.ToString();
        }

        private string CastType(RQueryExpression expression)
        {
            var keyword = expression.Type ?? "string";
            if (_root is null)
                return expression.NativeType ?? keyword;
            if (string.Equals(_dialectName, _root.Database.Dialect, StringComparison.Ordinal) && expression.NativeType is { Length: > 0 } native)
                return native;
            var settings = _root.Settings ?? throw new InvalidOperationException("A resolved query carries its project settings.");
            _typeMap ??= DialectTypeMaps.Effective(_dialect, settings);
            _conventions ??= EffectiveConventions.For(settings, _root.Database.Name);
            if (_dialect == Dialect.MySql)
                return MySqlCastType(keyword, DialectTypeMaps.Render(_typeMap, "decimal", null, null, null, _conventions));
            return _typeMap.ContainsKey(keyword) ? DialectTypeMaps.Render(_typeMap, keyword, null, null, null, _conventions) : keyword;
        }
    }
}
