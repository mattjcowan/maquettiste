using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Ledger;

/// <summary>
/// The migration ledger contract (next-phase-1-design.md section 3.3; <c>schemas/v1/ledger.json</c>, <c>docs/ledger.md</c>): three
/// tables in one schema of each PostgreSQL database, <c>contract</c> (one version row), <c>applied_fact</c> (one row per applied
/// migration fact) and <c>applied_object</c> (one row per object whose identity the writer owns). The table names are fixed; only
/// the schema is a setting (<c>migrations.ledgerSchema</c>). The DDL is one embedded text, idempotent, without transaction
/// control, times or ids, so a writer runs it inside its own transaction before its rows. The engine never reads a ledger.
/// </summary>
public static class LedgerContract
{
    private const string Resource = "Maquettiste.Engine.Ledger.ledger.postgresql.sql";
    private const string Placeholder = "{schema}";

    private static readonly Lazy<string> PostgreSqlText = new(LoadPostgreSql);

    /// <summary>The contract version the DDL creates and the writers of this engine write against.</summary>
    public const int Version = 1;

    /// <summary>The default ledger schema (<c>migrations.ledgerSchema</c>): it names the job, not a product.</summary>
    public const string DefaultSchema = "migration_ledger";

    /// <summary>The table holding the one version row.</summary>
    public const string ContractTable = "contract";

    /// <summary>The table holding one row per applied fact.</summary>
    public const string FactTable = "applied_fact";

    /// <summary>The table holding one row per object whose identity the writer owns.</summary>
    public const string ObjectTable = "applied_object";

    /// <summary>Whether the dialect has a ledger: PostgreSQL only in version 1.</summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <returns>Whether <see cref="Ddl"/> and <see cref="ReadGrant"/> answer for it.</returns>
    public static bool Supports(Dialect dialect) => dialect == Dialect.PostgreSql;

    /// <summary>
    /// The ledger DDL for one schema: LF line endings, every <c>{schema}</c> filled with the schema name, quoted when it is a
    /// reserved word of the dialect (or not a plain lower-case identifier). No time, id or transaction statement is added.
    /// </summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <param name="schema">The ledger schema (<c>migrations.ledgerSchema</c>).</param>
    /// <returns>The DDL text, ending with a line feed.</returns>
    /// <exception cref="NotSupportedException">The dialect has no ledger (<see cref="Supports"/> is false).</exception>
    /// <exception cref="ArgumentException">The schema is empty.</exception>
    public static string Ddl(Dialect dialect, string schema)
    {
        if (!Supports(dialect))
            throw new NotSupportedException($"The {DialectInfo.Name(dialect)} dialect has no migration ledger in contract version {Version}.");
        return PostgreSqlText.Value.Replace(Placeholder, QuoteIdentifier(dialect, schema, nameof(schema)), StringComparison.Ordinal);
    }

    /// <summary>
    /// The read grant for one role: usage on the schema and <c>SELECT</c> on the three tables, two statements on two lines. Roles
    /// belong to environments, so the engine prints this and never generates it.
    /// </summary>
    /// <param name="dialect">The database's dialect.</param>
    /// <param name="schema">The ledger schema, quoted when reserved.</param>
    /// <param name="role">The role; quoted unless it is a plain lower-case identifier that is not a reserved word.</param>
    /// <returns>The grant text, ending with a line feed.</returns>
    /// <exception cref="NotSupportedException">The dialect has no ledger (<see cref="Supports"/> is false).</exception>
    /// <exception cref="ArgumentException">The schema or the role is empty.</exception>
    public static string ReadGrant(Dialect dialect, string schema, string role)
    {
        if (!Supports(dialect))
            throw new NotSupportedException($"The {DialectInfo.Name(dialect)} dialect has no migration ledger in contract version {Version}.");
        var s = QuoteIdentifier(dialect, schema, nameof(schema));
        var r = QuoteIdentifier(dialect, role, nameof(role));
        return $"GRANT USAGE ON SCHEMA {s} TO {r};\n" +
            $"GRANT SELECT ON {s}.{ContractTable}, {s}.{FactTable}, {s}.{ObjectTable} TO {r};\n";
    }

    /// <summary>
    /// A schema or role name as the ledger's SQL writes it: as is when it is plain (<c>^[a-z_][a-z0-9_]*$</c>, which PostgreSQL
    /// reads unchanged without quotes) and not a reserved word (<c>SqlDialects.IsReserved</c>, the list <c>sql_quote</c> reads),
    /// else in double quotes with any double quote doubled.
    /// </summary>
    internal static string QuoteIdentifier(Dialect dialect, string name, string parameter)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("The name is empty.", parameter);
        if (IsPlain(name) && !SqlDialects.IsReserved(dialect, name))
            return name;
        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static bool IsPlain(string name)
    {
        if (!(char.IsAsciiLetterLower(name[0]) || name[0] == '_'))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_'))
                return false;
        }

        return true;
    }

    private static string LoadPostgreSql()
    {
        using var stream = typeof(LedgerContract).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException($"Missing resource {Resource}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
