using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine;

/// <summary>
/// Renders the statements of a resolved entity binding as parameterised SQL for a dialect (erratum E43; engine-design.md section 7,
/// "Bindings and materialize"), with the quoting, placeholder styles and dialect handling of <see cref="QuerySql"/>:
/// <list type="bullet">
/// <item><c>select</c>: every row, the columns the fields map aliased to the field names, filtered by the constants and the type codes
/// (and, with a soft delete, leaving out the deleted rows); a query source is wrapped as a derived table <c>FROM (&lt;query sql&gt;) q</c>.
/// A binding with bases (table per type, retire-projection-design.md section 4) joins each base's source on its foreign key
/// (<c>JOIN &lt;base&gt; b1 ON b1.&lt;key&gt; = t.&lt;column&gt;</c>, <c>b2</c> for the base's base) and reads the bases' fields first, root
/// first, each base filtered by its constants and the type codes of its table.</item>
/// <item><c>select-by-key</c>: the same, for the row whose key fields equal their parameters.</item>
/// <item><c>insert</c>: the written fields, the constants and the type code, without the columns the database fills; the generated key
/// columns come back through <c>RETURNING</c> (PostgreSQL, SQLite), <c>OUTPUT INSERTED</c> (SQL Server), <c>RETURNING … INTO</c> (Oracle)
/// or a second statement <c>SELECT LAST_INSERT_ID()</c> (MySQL, one identity key). A binding with bases writes its own table only, its key
/// columns first: the bases' rows come first, each through its base's <c>insert</c>, the root's returning the key.</item>
/// <item><c>update</c>: the updatable fields by key, the constants and the type codes in the where clause; with nothing to set, a count
/// of the matching rows.</item>
/// <item><c>delete</c>: by key plus the constants and the type codes, or the soft-delete update. A soft delete is rendered by the
/// statement of the table that holds its column: a binding with bases leaves it to the base's.</item>
/// <item><c>exists</c>: a count of the rows of the binding's own source with the key, filtered as its reads are, joining no base: the probe
/// that tells whether a base's row is one of this entity's when the base's table has no type column.</item>
/// <item>A child table entry (<see cref="RBindingChildTable"/>, retire-projection-design.md section 5) renders its own <c>select</c> (the rows of
/// one owner, ordered by the order column), <c>delete</c> (the rows of one owner) and <c>insert</c> (one row): writes replace an attribute's
/// rows, after the owner's insert and before the owner's delete.</item>
/// </list>
/// The type codes are those of <see cref="REntityBinding.DiscriminatorColumn"/>: <c>column = code</c> for one, <c>column IN (…)</c> for
/// several. A base of a binding (<see cref="RBindingBase"/>) renders its table's <c>insert</c>, <c>update</c> and <c>delete</c> for the
/// derived entity: the base's fields, constants and key, the derived entity's code in the table's type column, and the derived binding's
/// writes and delete; its <c>select</c>, <c>select-by-key</c> and <c>exists</c> are empty (the derived binding's select joins it).
/// Parameters are named after the fields. A statement the binding does not have (a write of a read-only binding, a lookup by key
/// without key fields) renders as the empty string.
/// </summary>
public static partial class BindingSql
{
    /// <summary>The statements a binding renders, in order.</summary>
    public static IReadOnlyList<string> Statements { get; } = ["select", "select-by-key", "insert", "update", "delete", "exists"];

    /// <summary>Renders one statement of a binding.</summary>
    /// <param name="binding">The resolved binding.</param>
    /// <param name="statement"><c>select</c>, <c>select-by-key</c>, <c>insert</c>, <c>update</c>, <c>delete</c> or <c>exists</c>.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the binding's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/> (<c>lists</c> does not apply).</param>
    /// <returns>The statement, empty when the binding does not have it.</returns>
    /// <exception cref="ArgumentException">The statement, the dialect or an option is not one this renderer knows.</exception>
    public static QuerySqlText Render(REntityBinding binding, string statement, string? dialect = null, QuerySqlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(statement);
        return Render(new Writer(binding, null, dialect, options), statement);
    }

    /// <summary>Renders one statement of a base's table for the derived entity whose binding has the base (table per type).</summary>
    /// <param name="level">One of a binding's <see cref="REntityBinding.Bases"/>.</param>
    /// <param name="statement"><c>insert</c>, <c>update</c> or <c>delete</c>; the others render as the empty string.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the binding's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/>.</param>
    /// <returns>The statement, empty when the level does not have it.</returns>
    /// <exception cref="ArgumentException">The statement, the dialect or an option is not one this renderer knows.</exception>
    public static QuerySqlText Render(RBindingBase level, string statement, string? dialect = null, QuerySqlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(level);
        ArgumentNullException.ThrowIfNull(statement);
        return Render(new Writer(level.Binding, level, dialect, options), statement);
    }

    /// <summary>The statements a child table entry renders, in order.</summary>
    public static IReadOnlyList<string> ChildStatements { get; } = ["select", "insert", "delete"];

    /// <summary>
    /// Renders one statement of a binding's child table entry (retire-projection-design.md section 5): <c>select</c>, the rows of one owner, the
    /// fields aliased to their names, filtered by the entry's constants and ordered by its order column; <c>delete</c>, the rows of one owner;
    /// <c>insert</c>, one row, with the owner's key, the position, the fields not generated and the constants. The owner's values are parameters
    /// named after the owner's fields (<c>ownerId</c>), the position <c>position</c>, the fields after their names. A read-only binding's entry
    /// renders only its select; the binding's other statements render as the empty string.
    /// </summary>
    /// <param name="child">One of a binding's <see cref="REntityBinding.ChildTables"/>.</param>
    /// <param name="statement"><c>select</c>, <c>insert</c> or <c>delete</c>; the binding's other statements render as the empty string.</param>
    /// <param name="dialect">A dialect name, or <see langword="null"/> for the binding's database's.</param>
    /// <param name="options">The options, or <see langword="null"/> for <see cref="QuerySqlOptions.Default"/>.</param>
    /// <returns>The statement, empty when the entry does not have it.</returns>
    /// <exception cref="ArgumentException">The statement, the dialect or an option is not one this renderer knows.</exception>
    public static QuerySqlText Render(RBindingChildTable child, string statement, string? dialect = null, QuerySqlOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(child);
        ArgumentNullException.ThrowIfNull(statement);
        var writer = new Writer(child.Binding, null, dialect, options);
        switch (statement)
        {
            case "select":
                writer.ChildSelect(child);
                break;
            case "insert":
                writer.ChildInsert(child);
                break;
            case "delete":
                writer.ChildDelete(child);
                break;
            case "select-by-key" or "update" or "exists":
                break;
            default:
                throw new ArgumentException($"'{statement}' is not a binding statement (select, select-by-key, insert, update, delete, exists).", nameof(statement));
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

    private static QuerySqlText Render(Writer writer, string statement)
    {
        switch (statement)
        {
            case "select":
                writer.Select(byKey: false, exists: false);
                break;
            case "select-by-key":
                writer.Select(byKey: true, exists: false);
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
            case "exists":
                writer.Select(byKey: true, exists: true);
                break;
            default:
                throw new ArgumentException($"'{statement}' is not a binding statement (select, select-by-key, insert, update, delete, exists).", nameof(statement));
        }

        return writer.Result();
    }

    [GeneratedRegex(@"(?<![\w$])\$(\d+)(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex DollarPlaceholder();

    private sealed class Writer
    {
        private readonly REntityBinding _b;
        private readonly RBindingBase? _level;
        private readonly RBindingDiscriminator? _type;
        private readonly bool _writes;
        private readonly string _delete;
        private readonly RColumn? _softColumn;
        private readonly object? _softValue;
        private readonly Dialect _dialect;
        private readonly Quoting _quoting;
        private readonly QuerySqlOptions _options;
        private readonly List<string> _parameters = [];
        private readonly List<Diagnostic> _diagnostics = [];
        private readonly StringBuilder _sb = new();

        /// <summary>A writer for a binding's own statements, or with <paramref name="level"/> for a base's table's statements of the derived entity.</summary>
        public Writer(REntityBinding binding, RBindingBase? level, string? dialect, QuerySqlOptions? options)
        {
            _b = binding;
            _level = level;
            // A base's statements for the derived entity: the derived entity's code in the base's type column, and the derived binding's
            // writes and delete (a soft delete is the base's, so its column and value are the same).
            var owner = level?.Derived ?? binding;
            _type = level is null ? binding.DiscriminatorColumn : level.DiscriminatorColumn;
            _writes = owner.Writes && binding.Writes;
            _delete = owner.Delete;
            (_softColumn, _softValue) = (owner.SoftDeleteColumn, owner.SoftDeleteValue);
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

        /// <summary>The type codes a read, update or delete keeps, or <see langword="null"/> without a type column.</summary>
        private string? Codes(RBindingDiscriminator? type, string column) => type is not { Codes: { Count: > 0 } codes }
            ? null
            : codes.Count == 1 ? column + " = " + Literal(codes[0]) : column + " IN (" + string.Join(", ", codes.Select(Literal)) + ")";

        private static bool Reads(RBindingConstant c) => (c.SourceColumn ?? (object?)c.ViewColumn ?? c.QueryField) is not null;

        private static bool Reads(RBindingDiscriminator d) => (d.SourceColumn ?? (object?)d.ViewColumn ?? d.QueryField) is not null;

        private static bool Reads(RBindingField f) => f.ColumnName.Length > 0 && (f.SourceColumn ?? (object?)f.ViewColumn ?? f.QueryField) is not null;

        /// <summary>The key fields a base foreign key gives, which are not in the field map: the insert writes them first.</summary>
        private IEnumerable<RBindingField> KeyOnly() => _b.Key.Where(k => !_b.Fields.Contains(k));

        /// <summary>One source the select reads: the binding's own, or a base's, joined on its foreign key to the one before it.</summary>
        private sealed record Part(REntityBinding Binding, string Alias, RBindingDiscriminator? Type, RForeignKey? Join, string? JoinTo);

        public void Select(bool byKey, bool exists)
        {
            if (_level is not null || _b.SourceKind is null || (byKey && _b.Key.Count == 0))
                return;
            var parts = new List<Part> { new(_b, _b.SourceQuery is null ? "t" : "q", _type, null, null) };
            if (!exists)
            {
                for (var i = 0; i < _b.Bases.Count; i++)
                {
                    var level = _b.Bases[i];
                    if (level.Binding.SourceKind is null)
                        break;
                    parts.Add(new Part(level.Binding, "b" + (i + 1).ToString(CultureInfo.InvariantCulture), level.DiscriminatorColumn, level.ForeignKey, parts[^1].Alias));
                }
            }

            string Column(Part part, string name) => Q(part.Alias) + "." + Q(name);

            // The bases' fields first, root first, then the binding's own; a name the binding's own fields use is read from its own source.
            var own = new HashSet<string>(_b.Fields.Where(Reads).Select(f => f.Name), StringComparer.Ordinal);
            var selected = new List<string>();
            var seen = new HashSet<string>(own, StringComparer.Ordinal);
            for (var i = parts.Count - 1; i >= 1; i--)
            {
                foreach (var field in parts[i].Binding.Fields.Where(Reads))
                {
                    if (seen.Add(field.Name))
                        selected.Add(Column(parts[i], field.ColumnName) + " AS " + Q(field.Name));
                }
            }

            selected.AddRange(_b.Fields.Where(Reads).Select(f => Column(parts[0], f.ColumnName) + " AS " + Q(f.Name)));

            // A query with named queries puts its WITH first, before the select over it (sql-queries-design.md decision 16). With bases,
            // every level's named queries go into that one WITH clause, which every dialect takes at the top of a statement (SQL Server
            // takes no WITH inside a derived table); a name an earlier level already uses is renamed for the later one.
            var sources = new List<string>();
            var entries = new List<string>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var recursive = false;
            foreach (var part in parts)
            {
                if (part.Binding.SourceQuery is { } source)
                {
                    var query = source.ForDerivedTable();
                    var (with, inner) = QuerySql.RenderParts(query, DialectTypeMaps.Name(_dialect), _options with { Lists = "expand" });
                    _diagnostics.AddRange(inner.Diagnostics);
                    var sql = Renumbered(inner.Sql, inner.Parameters);
                    with = Renumbered(with, inner.Parameters);
                    if (with.Length > 0)
                    {
                        foreach (var entry in query.With)
                        {
                            if (names.Add(entry.Name))
                                continue;
                            var renamed = entry.Name + "_" + part.Alias;
                            for (var n = 2; !names.Add(renamed); n++)
                                renamed = entry.Name + "_" + part.Alias + "_" + n.ToString(CultureInfo.InvariantCulture);
                            (with, sql) = (Renamed(with, entry.Name, renamed), Renamed(sql, entry.Name, renamed));
                        }

                        recursive |= with.StartsWith("WITH RECURSIVE ", StringComparison.Ordinal);
                        entries.Add(with[(with.StartsWith("WITH RECURSIVE ", StringComparison.Ordinal) ? "WITH RECURSIVE ".Length : "WITH ".Length)..]);
                    }

                    sources.Add("(\n" + sql + "\n) " + Q(part.Alias));
                }
                else if (part.Binding.SourceTable is { } table)
                {
                    sources.Add(Qualified(table) + " " + Q(part.Alias));
                }
                else if (part.Binding.SourceView is { } view)
                {
                    var name = view.Schema is { Length: > 0 } schema && _dialect != Dialect.Sqlite ? Q(schema) + "." + Q(view.Name) : Q(view.Name);
                    sources.Add(name + " " + Q(part.Alias));
                }
            }

            if (entries.Count > 0)
                _sb.Append(recursive ? "WITH RECURSIVE " : "WITH ").Append(string.Join(",\n", entries)).Append('\n');
            _sb.Append("SELECT ").Append(exists ? "COUNT(*)" : string.Join(", ", selected));
            _sb.Append('\n').Append("FROM ").Append(sources[0]);
            for (var i = 1; i < parts.Count; i++)
            {
                // Each side by its own source's column: the base's key field there, and the column of the table below that holds the key.
                var part = parts[i];
                var below = parts[i - 1];
                var on = part.Join!.Columns.Select((c, k) => Column(part, ColumnIn(part.Binding, part.Join.ReferencedColumns[k])) + " = " + Column(below, ColumnIn(below.Binding, c)));
                _sb.Append('\n').Append("JOIN ").Append(sources[i]).Append(" ON ").Append(string.Join(" AND ", on));
            }

            var conditions = new List<string>();
            foreach (var part in parts)
            {
                foreach (var constant in part.Binding.Constants.Where(Reads))
                    conditions.Add(Equal(Column(part, constant.ColumnName), constant.Value, Literal));
                if (part.Type is { } typeColumn && Reads(typeColumn) && Codes(typeColumn, Column(part, typeColumn.ColumnName)) is { } kept)
                    conditions.Add(kept);
            }

            // The soft delete's column filters reads: a binding with bases takes the root's, whatever it writes or deletes itself.
            if (_softColumn is { } soft && parts.Find(p => ReferenceEquals(soft.Table, p.Binding.SourceTable)) is { } holder)
                conditions.Add(NotDeleted(Column(holder, soft.Name)));
            if (byKey)
            {
                foreach (var key in _b.Key)
                    conditions.Add(Column(parts[0], key.ColumnName) + " = " + Placeholder(key.Name));
            }

            if (conditions.Count > 0)
                _sb.Append('\n').Append("WHERE ").Append(string.Join(" AND ", conditions));
        }

        /// <summary>
        /// The name a binding's source gives a table column: the source column of the key field or field that reads or writes it (a view's or a
        /// query's column may be named otherwise), else the column's own name.
        /// </summary>
        private static string ColumnIn(REntityBinding binding, RColumn column) =>
            binding.Key.Concat(binding.Fields).FirstOrDefault(f => ReferenceEquals(f.SourceColumn, column) || ReferenceEquals(f.WriteColumn, column))?.ColumnName
            ?? column.Name;

        /// <summary>
        /// A query's text with its parameters joined to the statement's: with <c>$n</c> placeholders, each one renumbered to the statement's
        /// position of its name, so two levels' queries never share a number for two names; the names of other styles are kept.
        /// </summary>
        private string Renumbered(string sql, IReadOnlyList<string> parameters)
        {
            if (_options.Placeholder != "$")
            {
                foreach (var name in parameters)
                    Placeholder(name);
                return sql;
            }

            var numbers = parameters.Select(Placeholder).ToList();
            return sql.Length == 0 ? sql : BindingSql.DollarPlaceholder().Replace(sql, m =>
                int.TryParse(m.Groups[1].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n >= 1 && n <= numbers.Count ? numbers[n - 1] : m.Value);
        }

        /// <summary>A named query's name renamed in a text: its quoted form, else the bare word where it stands as a name (not after a dot).</summary>
        private string Renamed(string sql, string name, string renamed)
        {
            var quoted = Q(name);
            if (!string.Equals(quoted, name, StringComparison.Ordinal))
                return sql.Replace(quoted, Q(renamed), StringComparison.Ordinal);
            return Regex.Replace(sql, @"(?<![\w.$""\[`])" + Regex.Escape(name) + @"(?![\w""\]`])", Q(renamed), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        }

        /// <summary>A row the soft delete has not marked: the column is null or holds another value.</summary>
        private string NotDeleted(string column) => _softValue is null
            ? column + " IS NOT NULL"
            : "(" + column + " IS NULL OR " + column + " <> " + Literal(_softValue) + ")";

        public void Insert()
        {
            if (!_writes || _b.WriteTable is not { } table)
                return;
            var columns = new List<string>();
            var values = new List<string>();
            foreach (var field in KeyOnly().Concat(_b.Fields).Where(f => f.InInsert && f.WriteColumn is not null))
            {
                columns.Add(Q(field.WriteColumn!.Name));
                values.Add(Placeholder(field.Name));
            }

            foreach (var constant in _b.Constants.Where(c => c.WriteColumn is not null))
            {
                columns.Add(Q(constant.WriteColumn!.Name));
                values.Add(Literal(constant.Value));
            }

            if (_type is { WriteColumn: { } typeColumn } discriminator)
            {
                columns.Add(Q(typeColumn.Name));
                values.Add(Literal(discriminator.Value));
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

        /// <summary>The where clause of a write: the key fields' columns of the write table, then the constants, then the type codes.</summary>
        private string WriteWhere()
        {
            var conditions = new List<string>();
            foreach (var key in _b.Key.Where(k => k.WriteColumn is not null))
                conditions.Add(Q(key.WriteColumn!.Name) + " = " + Placeholder(key.Name));
            foreach (var constant in _b.Constants.Where(c => c.WriteColumn is not null))
                conditions.Add(Equal(Q(constant.WriteColumn!.Name), constant.Value, Literal));
            if (_type is { WriteColumn: { } typeColumn } && Codes(_type, Q(typeColumn.Name)) is { } kept)
                conditions.Add(kept);
            return string.Join(" AND ", conditions);
        }

        public void Update()
        {
            if (!_writes || _b.WriteTable is not { } table || _b.Key.Count == 0)
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

        /// <summary>Whether a child table entry can render: its table and owner foreign key resolve, and a field of the binding fills each owner column.</summary>
        private static bool Renders(RBindingChildTable child) =>
            child.Table is not null && child.OwnerForeignKey is not null && child.Owner.Count > 0 && child.Owner.All(o => o.Field is not null);

        /// <summary>The where clause of a child table entry's select and delete: the owner's columns, then the constants.</summary>
        private string ChildWhere(RBindingChildTable child, string? alias)
        {
            string Column(string name) => alias is null ? Q(name) : Q(alias) + "." + Q(name);
            var conditions = child.Owner.Select(o => Column(o.Column.Name) + " = " + Placeholder(o.Parameter)).ToList();
            conditions.AddRange(child.Constants.Where(c => c.SourceColumn is not null).Select(c => Equal(Column(c.ColumnName), c.Value, Literal)));
            return string.Join(" AND ", conditions);
        }

        public void ChildSelect(RBindingChildTable child)
        {
            var fields = child.Fields.Where(f => f.Attribute is not null && f.SourceColumn is not null).ToList();
            if (!Renders(child) || fields.Count == 0)
                return;
            _sb.Append("SELECT ").Append(string.Join(", ", fields.Select(f => Q("t") + "." + Q(f.ColumnName) + " AS " + Q(f.Name))));
            _sb.Append('\n').Append("FROM ").Append(Qualified(child.Table!)).Append(' ').Append(Q("t"));
            _sb.Append('\n').Append("WHERE ").Append(ChildWhere(child, "t"));
            if (child.OrderColumn is { } order)
                _sb.Append('\n').Append("ORDER BY ").Append(Q("t")).Append('.').Append(Q(order.Name));
        }

        public void ChildDelete(RBindingChildTable child)
        {
            if (!child.Writes || !Renders(child))
                return;
            _sb.Append("DELETE FROM ").Append(Qualified(child.Table!)).Append('\n').Append("WHERE ").Append(ChildWhere(child, null));
        }

        public void ChildInsert(RBindingChildTable child)
        {
            if (!child.Writes || !Renders(child))
                return;
            var columns = new List<string>();
            var values = new List<string>();
            foreach (var owner in child.Owner)
            {
                columns.Add(Q(owner.Column.Name));
                values.Add(Placeholder(owner.Parameter));
            }

            if (child is { OrderColumn: { } order, OrderParameter: { } position })
            {
                columns.Add(Q(order.Name));
                values.Add(Placeholder(position));
            }

            foreach (var field in child.Fields.Where(f => f.InInsert && f.WriteColumn is not null))
            {
                columns.Add(Q(field.WriteColumn!.Name));
                values.Add(Placeholder(field.Name));
            }

            foreach (var constant in child.Constants.Where(c => c.WriteColumn is not null))
            {
                columns.Add(Q(constant.WriteColumn!.Name));
                values.Add(Literal(constant.Value));
            }

            _sb.Append("INSERT INTO ").Append(Qualified(child.Table!)).Append(" (").Append(string.Join(", ", columns)).Append(')')
                .Append('\n').Append("VALUES (").Append(string.Join(", ", values)).Append(')');
        }

        public void Delete()
        {
            if (_b.WriteTable is not { } table || _b.Key.Count == 0)
                return;
            switch (_delete)
            {
                case "key":
                    _sb.Append("DELETE FROM ").Append(Qualified(table)).Append('\n').Append("WHERE ").Append(WriteWhere());
                    break;
                case "soft" when _softColumn is { } soft && ReferenceEquals(soft.Table, table):
                    _sb.Append("UPDATE ").Append(Qualified(table)).Append(" SET ").Append(Q(soft.Name)).Append(" = ").Append(Literal(_softValue))
                        .Append('\n').Append("WHERE ").Append(WriteWhere());
                    break;
            }
        }
    }
}
