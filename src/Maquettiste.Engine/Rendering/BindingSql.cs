using System.Globalization;
using System.Text;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>
/// Renders the statements of a resolved entity binding as parameterised SQL for a dialect (erratum E43; engine-design.md section 7,
/// "Bindings and materialize"), with the quoting, placeholder styles and dialect handling of <see cref="QuerySql"/>:
/// <list type="bullet">
/// <item><c>select</c>: every row, the columns the fields map aliased to the field names, filtered by the constants (and, with a soft
/// delete, leaving out the deleted rows); a query source is wrapped as a derived table <c>FROM (&lt;query sql&gt;) q</c>.</item>
/// <item><c>select-by-key</c>: the same, for the row whose key fields equal their parameters.</item>
/// <item><c>insert</c>: the written fields and the constants, without the columns the database fills; the generated key columns come
/// back through <c>RETURNING</c> (PostgreSQL, SQLite), <c>OUTPUT INSERTED</c> (SQL Server), <c>RETURNING … INTO</c> (Oracle) or a
/// second statement <c>SELECT LAST_INSERT_ID()</c> (MySQL, one identity key).</item>
/// <item><c>update</c>: the updatable fields by key, the constants in the where clause; with nothing to set, a count of the matching
/// rows.</item>
/// <item><c>delete</c>: by key plus the constants, or the soft-delete update.</item>
/// </list>
/// Parameters are named after the fields. A statement the binding does not have (a write of a read-only binding, a lookup by key
/// without key fields) renders as the empty string.
/// </summary>
public static class BindingSql
{
    /// <summary>The statements a binding renders, in order.</summary>
    public static IReadOnlyList<string> Statements { get; } = ["select", "select-by-key", "insert", "update", "delete"];

    /// <summary>Renders one statement of a binding.</summary>
    /// <param name="binding">The resolved binding.</param>
    /// <param name="statement"><c>select</c>, <c>select-by-key</c>, <c>insert</c>, <c>update</c> or <c>delete</c>.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the binding's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/> (<c>lists</c> does not apply).</param>
    /// <returns>The statement, empty when the binding does not have it.</returns>
    /// <exception cref="ArgumentException">The statement, the dialect or an option is not one this renderer knows.</exception>
    public static QuerySqlText Render(REntityBinding binding, string statement, string? dialect = null, QuerySqlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(statement);
        var writer = new Writer(binding, dialect, options);
        switch (statement)
        {
            case "select":
                writer.Select(byKey: false);
                break;
            case "select-by-key":
                writer.Select(byKey: true);
                break;
            case "insert":
                writer.Insert();
                break;
            case "update":
                writer.Update();
                break;
            case "delete":
                writer.Delete();
                break;
            default:
                throw new ArgumentException($"'{statement}' is not a binding statement (select, select-by-key, insert, update, delete).", nameof(statement));
        }

        return writer.Result();
    }

    /// <summary>The parameter names of a statement in placeholder order (the template helper <c>binding_sql_parameters</c>).</summary>
    /// <param name="binding">The resolved binding.</param>
    /// <param name="statement">The statement.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the binding's database's.</param>
    /// <param name="options">The options.</param>
    /// <returns>The names, each once, in first-appearance order.</returns>
    public static IReadOnlyList<string> Parameters(REntityBinding binding, string statement, string? dialect = null, QuerySqlOptions? options = null) =>
        Render(binding, statement, dialect, options).Parameters;

    private sealed class Writer
    {
        private readonly REntityBinding _b;
        private readonly Dialect _dialect;
        private readonly Quoting _quoting;
        private readonly QuerySqlOptions _options;
        private readonly List<string> _parameters = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly StringBuilder _sb = new();

        public Writer(REntityBinding binding, string? dialect, QuerySqlOptions? options)
        {
            _b = binding;
            var name = dialect ?? binding.Database.Dialect;
            if (!SqlDialects.TryParse(name, out _dialect))
                throw new ArgumentException($"'{name}' is not a dialect (postgresql, sqlserver, mysql, sqlite, oracle).", nameof(dialect));
            SqlDialects.TryParseQuoting(binding.Database.Quoting, out _quoting);
            _options = options ?? QuerySqlOptions.Default;
            if (_options.Placeholder is not ("@" or ":" or "$"))
                throw new ArgumentException($"The placeholder style '{_options.Placeholder}' is not @, : or $.", nameof(options));
        }

        public QuerySqlText Result() => new(_sb.ToString(), [.. _parameters], [.. _diagnostics]);

        private string Q(string name) => SqlDialects.Quote(name, _dialect, _quoting);

        private string Qualified(RTable table) =>
            table.Schema is { Length: > 0 } schema && _dialect != Dialect.Sqlite ? Q(schema) + "." + Q(table.Name) : Q(table.Name);

        private string Literal(object? value) => SqlDialects.Literal(value, _dialect);

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

        private static string Equal(string column, object? value, Func<object?, string> literal) =>
            value is null ? column + " IS NULL" : column + " = " + literal(value);

        public void Select(bool byKey)
        {
            if (_b.SourceKind is null || (byKey && _b.Key.Count == 0))
                return;
            var alias = _b.SourceQuery is null ? "t" : "q";
            string Column(string name) => Q(alias) + "." + Q(name);
            var fields = _b.Fields.Where(f => f.ColumnName.Length > 0 && (f.SourceColumn ?? (object?)f.ViewColumn ?? f.QueryField) is not null).ToList();
            _sb.Append("SELECT ").Append(string.Join(", ", fields.Select(f => Column(f.ColumnName) + " AS " + Q(f.Name))));
            _sb.Append('\n').Append("FROM ");
            if (_b.SourceQuery is { } query)
            {
                var inner = QuerySql.Render(query.ForDerivedTable(), DialectTypeMaps.Name(_dialect), _options with { Lists = "expand" });
                _diagnostics.AddRange(inner.Diagnostics);
                _parameters.AddRange(inner.Parameters);
                _sb.Append("(\n").Append(inner.Sql).Append("\n) ").Append(Q(alias));
            }
            else if (_b.SourceTable is { } table)
            {
                _sb.Append(Qualified(table)).Append(' ').Append(Q(alias));
            }
            else if (_b.SourceView is { } view)
            {
                var name = view.Schema is { Length: > 0 } schema && _dialect != Dialect.Sqlite ? Q(schema) + "." + Q(view.Name) : Q(view.Name);
                _sb.Append(name).Append(' ').Append(Q(alias));
            }

            var conditions = new List<string>();
            foreach (var constant in _b.Constants.Where(c => (c.SourceColumn ?? (object?)c.ViewColumn ?? c.QueryField) is not null))
                conditions.Add(Equal(Column(constant.ColumnName), constant.Value, Literal));
            if (_b.Delete == "soft" && _b.SoftDeleteColumn is { } soft && ReferenceEquals(soft.Table, _b.SourceTable))
                conditions.Add(NotDeleted(Column(soft.Name)));
            if (byKey)
            {
                foreach (var key in _b.Key)
                    conditions.Add(Column(key.ColumnName) + " = " + Placeholder(key.Name));
            }

            if (conditions.Count > 0)
                _sb.Append('\n').Append("WHERE ").Append(string.Join(" AND ", conditions));
        }

        /// <summary>A row the soft delete has not marked: the column is null or holds another value.</summary>
        private string NotDeleted(string column) => _b.SoftDeleteValue is null
            ? column + " IS NOT NULL"
            : "(" + column + " IS NULL OR " + column + " <> " + Literal(_b.SoftDeleteValue) + ")";

        public void Insert()
        {
            if (!_b.Writes || _b.WriteTable is not { } table)
                return;
            var columns = new List<string>();
            var values = new List<string>();
            foreach (var field in _b.Fields.Where(f => f.InInsert && f.WriteColumn is not null))
            {
                columns.Add(Q(field.WriteColumn!.Name));
                values.Add(Placeholder(field.Name));
            }

            foreach (var constant in _b.Constants.Where(c => c.WriteColumn is not null))
            {
                columns.Add(Q(constant.WriteColumn!.Name));
                values.Add(Literal(constant.Value));
            }

            var generated = _b.Generated.Where(g => g.WriteColumn is not null).ToList();
            _sb.Append("INSERT INTO ").Append(Qualified(table));
            if (columns.Count > 0)
                _sb.Append(" (").Append(string.Join(", ", columns)).Append(')');
            if (generated.Count > 0 && _dialect == Dialect.SqlServer)
                _sb.Append('\n').Append("OUTPUT ").Append(string.Join(", ", generated.Select(g => "INSERTED." + Q(g.WriteColumn!.Name) + " AS " + Q(g.Name))));
            if (columns.Count > 0)
                _sb.Append('\n').Append("VALUES (").Append(string.Join(", ", values)).Append(')');
            else
                _sb.Append(_dialect == Dialect.MySql ? " () VALUES ()" : "\nDEFAULT VALUES");
            if (generated.Count == 0)
                return;
            switch (_dialect)
            {
                case Dialect.PostgreSql or Dialect.Sqlite:
                    _sb.Append('\n').Append("RETURNING ").Append(string.Join(", ", generated.Select(g => Q(g.WriteColumn!.Name) + " AS " + Q(g.Name))));
                    break;
                case Dialect.Oracle:
                    _sb.Append('\n').Append("RETURNING ").Append(string.Join(", ", generated.Select(g => Q(g.WriteColumn!.Name))))
                        .Append(" INTO ").Append(string.Join(", ", generated.Select(g => Placeholder(g.Name))));
                    break;
                case Dialect.MySql when generated.Count == 1 && generated[0].WriteColumn!.Identity:
                    _sb.Append(";\n").Append("SELECT LAST_INSERT_ID() AS ").Append(Q(generated[0].Name));
                    break;
            }
        }

        /// <summary>The where clause of a write: the key fields' columns of the write table, then the constants.</summary>
        private string WriteWhere()
        {
            var conditions = new List<string>();
            foreach (var key in _b.Key.Where(k => k.WriteColumn is not null))
                conditions.Add(Q(key.WriteColumn!.Name) + " = " + Placeholder(key.Name));
            foreach (var constant in _b.Constants.Where(c => c.WriteColumn is not null))
                conditions.Add(Equal(Q(constant.WriteColumn!.Name), constant.Value, Literal));
            return string.Join(" AND ", conditions);
        }

        public void Update()
        {
            if (!_b.Writes || _b.WriteTable is not { } table || _b.Key.Count == 0)
                return;
            var sets = _b.Fields.Where(f => f.InUpdate && f.WriteColumn is not null)
                .Select(f => Q(f.WriteColumn!.Name) + " = " + Placeholder(f.Name)).ToList();
            if (sets.Count == 0)
            {
                _sb.Append("SELECT COUNT(*) FROM ").Append(Qualified(table)).Append('\n').Append("WHERE ").Append(WriteWhere());
                return;
            }

            _sb.Append("UPDATE ").Append(Qualified(table)).Append(" SET ").Append(string.Join(", ", sets))
                .Append('\n').Append("WHERE ").Append(WriteWhere());
        }

        public void Delete()
        {
            if (_b.WriteTable is not { } table || _b.Key.Count == 0)
                return;
            switch (_b.Delete)
            {
                case "key":
                    _sb.Append("DELETE FROM ").Append(Qualified(table)).Append('\n').Append("WHERE ").Append(WriteWhere());
                    break;
                case "soft" when _b.SoftDeleteColumn is { } soft:
                    _sb.Append("UPDATE ").Append(Qualified(table)).Append(" SET ").Append(Q(soft.Name)).Append(" = ").Append(Literal(_b.SoftDeleteValue))
                        .Append('\n').Append("WHERE ").Append(WriteWhere());
                    break;
            }
        }
    }
}
