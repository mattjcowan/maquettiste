using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.SchemaDiff;
using Maquettiste.Engine.Tests.Rendering;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Ledger;

/// <summary>The migration ledger contract (next-phase-1-design.md section 3.3): the DDL text, the rows document and the grant.</summary>
public sealed partial class LedgerContractTests
{
    [Fact]
    public void Ddl_is_idempotent_text_without_transaction_control_or_time()
    {
        var ddl = LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema);

        // One text, the same every time: LF only, ending with a line feed, the placeholder filled.
        Assert.Equal(ddl, LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema));
        Assert.DoesNotContain("\r", ddl, StringComparison.Ordinal);
        Assert.EndsWith(");\n", ddl, StringComparison.Ordinal);
        Assert.DoesNotContain("{schema}", ddl, StringComparison.Ordinal);
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS migration_ledger;\n", ddl, StringComparison.Ordinal);

        // Idempotent: every object is created only when missing, and the version row is inserted only once.
        var statements = Statements(ddl);
        foreach (var create in statements.Where(s => s.StartsWith("CREATE ", StringComparison.Ordinal)))
            Assert.Matches(@"^CREATE (SCHEMA|TABLE) IF NOT EXISTS ", create);
        var insert = Assert.Single(statements, s => s.StartsWith("INSERT ", StringComparison.Ordinal));
        Assert.EndsWith("ON CONFLICT (id) DO NOTHING", insert, StringComparison.Ordinal);
        Assert.Equal(3, statements.Count(s => s.StartsWith("CREATE TABLE ", StringComparison.Ordinal)));
        Assert.Contains($"migration_ledger.{LedgerContract.ContractTable} ", ddl, StringComparison.Ordinal);
        Assert.Contains($"migration_ledger.{LedgerContract.FactTable} ", ddl, StringComparison.Ordinal);
        Assert.Contains($"migration_ledger.{LedgerContract.ObjectTable} ", ddl, StringComparison.Ordinal);
        Assert.Contains($"VALUES (1, {LedgerContract.Version})", ddl, StringComparison.Ordinal);

        // No transaction control: the writer runs the DDL inside its own transaction.
        foreach (var keyword in new[] { "BEGIN;", "BEGIN TRANSACTION", "START TRANSACTION", "COMMIT", "ROLLBACK", "SAVEPOINT", "END;" })
            Assert.DoesNotContain(keyword, ddl, StringComparison.OrdinalIgnoreCase);

        // No time and no id: applied_at is the database's transaction time, so no writer's output holds one.
        Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", ddl);
        Assert.DoesNotMatch(@"[0-9A-HJKMNP-TV-Z]{26}|[0-9a-f]{8}-[0-9a-f]{4}-", ddl);

        // PostgreSQL only in version 1.
        Assert.True(LedgerContract.Supports(Dialect.PostgreSql));
        foreach (var dialect in new[] { Dialect.SqlServer, Dialect.MySql, Dialect.Sqlite, Dialect.Oracle })
        {
            Assert.False(LedgerContract.Supports(dialect));
            Assert.Throws<NotSupportedException>(() => LedgerContract.Ddl(dialect, LedgerContract.DefaultSchema));
            Assert.Throws<NotSupportedException>(() => LedgerContract.ReadGrant(dialect, LedgerContract.DefaultSchema, "reader"));
        }
    }

    [Fact]
    public void Ledger_schema_rows_match_the_postgresql_columns_and_nullability()
    {
        var schema = JsonNode.Parse(TestServices.Schemas.GetFileBytes("ledger.json").Span)!;
        var tables = Tables(LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema));
        var rows = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LedgerContract.ContractTable] = "contractRow",
            [LedgerContract.FactTable] = "factRow",
            [LedgerContract.ObjectTable] = "objectRow",
        };
        Assert.Equal(rows.Keys.Order(StringComparer.Ordinal), tables.Keys.Order(StringComparer.Ordinal));

        foreach (var (table, def) in rows)
        {
            var row = schema["$defs"]![def]!;
            var order = row["x-order"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
            var columns = tables[table];
            Assert.Equal(columns.Select(c => c.Name), order);
            Assert.Equal(order, row["required"]!.AsArray().Select(n => n!.GetValue<string>()));
            Assert.False(row["additionalProperties"]!.GetValue<bool>());

            foreach (var column in columns)
            {
                var property = Resolve(schema, row["properties"]![column.Name]!);
                var types = Types(property);
                Assert.True(column.Nullable == types.Contains("null"), $"{table}.{column.Name}: nullable {column.Nullable}, schema types [{string.Join(", ", types)}]");
                var expected = column.Type switch
                {
                    "smallint" or "integer" => "integer",
                    "text" or "timestamptz" => "string",
                    _ => throw new InvalidOperationException($"{table}.{column.Name}: unmapped type {column.Type}"),
                };
                Assert.Contains(expected, types);
                if (column.Type == "timestamptz")
                    Assert.Equal("date-time", property["format"]!.GetValue<string>());
            }
        }

        Assert.Equal(1, schema["$defs"]!["contractRow"]!["properties"]!["id"]!["const"]!.GetValue<int>());
        Assert.Equal(["contract", "facts", "objects"], schema["x-order"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void A_reserved_schema_name_is_quoted_in_the_ddl_and_the_grant()
    {
        var ddl = LedgerContract.Ddl(Dialect.PostgreSql, "user");
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS \"user\";\n", ddl, StringComparison.Ordinal);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"user\".contract ", ddl, StringComparison.Ordinal);
        Assert.Contains("SELECT version FROM \"user\".contract WHERE id = 1", ddl, StringComparison.Ordinal);
        Assert.Contains("INSERT INTO \"user\".contract ", ddl, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"(?<!"")user\.", ddl);
        Assert.Equal(LedgerContract.Ddl(Dialect.PostgreSql, "user").Replace("\"user\"", "x", StringComparison.Ordinal),
            LedgerContract.Ddl(Dialect.PostgreSql, "x"));

        Assert.Equal(
            "GRANT USAGE ON SCHEMA \"order\" TO reporting;\n" +
            "GRANT SELECT ON \"order\".contract, \"order\".applied_fact, \"order\".applied_object TO reporting;\n",
            LedgerContract.ReadGrant(Dialect.PostgreSql, "order", "reporting"));

        // A plain schema stays bare; a role is quoted unless it is plain (lower case, regular, not reserved).
        Assert.Equal(
            "GRANT USAGE ON SCHEMA migration_ledger TO \"Reporting Team\";\n" +
            "GRANT SELECT ON migration_ledger.contract, migration_ledger.applied_fact, migration_ledger.applied_object TO \"Reporting Team\";\n",
            LedgerContract.ReadGrant(Dialect.PostgreSql, LedgerContract.DefaultSchema, "Reporting Team"));
        Assert.EndsWith("TO \"select\";\n", LedgerContract.ReadGrant(Dialect.PostgreSql, LedgerContract.DefaultSchema, "select"), StringComparison.Ordinal);
        Assert.EndsWith("TO \"a\"\"b\";\n", LedgerContract.ReadGrant(Dialect.PostgreSql, LedgerContract.DefaultSchema, "a\"b"), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => LedgerContract.ReadGrant(Dialect.PostgreSql, LedgerContract.DefaultSchema, ""));
    }

    [Fact]
    public async Task The_schema_setting_reaches_the_ddl_and_the_resolved_database()
    {
        static ModelBuilder Model(string? ledgerSchema)
        {
            var model = new ModelBuilder();
            var main = model.Database("main", Dialect.PostgreSql);
            model.Table("notes", main).Column("id", "int64").PrimaryKey("id");
            var local = model.Database("local", Dialect.Sqlite);
            model.Table("cache", local).Column("id", "int64").PrimaryKey("id");
            if (ledgerSchema is not null)
                model.Settings(s => s with { Migrations = s.Migrations with { LedgerSchema = ledgerSchema } });
            return model;
        }

        // The default schema, then a configured one: PostgreSQL databases carry it, others have no ledger.
        var standard = ResolutionKit.Resolve(Model(null));
        Assert.Equal(LedgerContract.DefaultSchema, standard.Db("main").LedgerSchema);
        Assert.Null(standard.Db("local").LedgerSchema);

        var builder = Model("audit_trail");
        var resolved = ResolutionKit.Resolve(builder);
        var main = resolved.Db("main");
        Assert.Equal("audit_trail", main.LedgerSchema);
        Assert.Null(resolved.Db("local").LedgerSchema);
        Assert.Contains("s:migrations", main.Tables.MembershipKeys);
        Assert.Contains("s:migrations", main.Objects.MembershipKeys);
        Assert.DoesNotContain("s:migrations", resolved.Db("local").Tables.MembershipKeys);

        // The helper writes the DDL for that schema, and nothing for a database without a ledger.
        var unit = await Adhoc.RenderAsync("{{ for d in model.databases }}[{{ d.name }}]{{ ledger_ddl d }}{{ end }}", model: resolved);
        Assert.Equal("[local][main]" + LedgerContract.Ddl(Dialect.PostgreSql, "audit_trail"), unit.Text());
        Assert.Contains("CREATE TABLE IF NOT EXISTS audit_trail.applied_fact ", unit.Text(), StringComparison.Ordinal);

        // The planner's key follows the schema only: migrations.record renders no byte.
        string Hash(ModelSnapshot snapshot) =>
            new DependencyHasher(snapshot, ResolutionKit.Resolve(snapshot), new PackSet([], []), new Dictionary<string, SchemaDiffResult>()).CurrentHash("s:migrations");
        var configured = builder.Build();
        Assert.Matches("^[0-9a-f]{64}$", Hash(configured));
        Assert.NotEqual(Hash(Model(null).Build()), Hash(configured));
        builder.Settings(s => s with { Migrations = s.Migrations with { Record = MigrationRecording.Off } });
        Assert.Equal(Hash(configured), Hash(builder.Build()));
    }

    [Fact]
    public async Task The_ledger_ddl_helper_records_the_migrations_key()
    {
        var model = await BillingModel.GetAsync();
        var main = model.Databases.Single(d => d.Name == "main");

        var unit = await Adhoc.RenderAsync("{{ ledger_ddl model.databases[0] }}");
        Assert.Equal(LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema), unit.Text());
        Assert.Contains("s:migrations", unit.ReadKeys);
        Assert.Equal([.. main.Dependencies.Append("s:migrations").Append("k:database").Append("t:adhoc/main.scriban").Distinct().Order(StringComparer.Ordinal)],
            unit.ReadKeys);

        // Anything but a database fails the unit.
        var wrong = await Adhoc.RenderAsync("{{ ledger_ddl \"main\" }}");
        Assert.Equal("MQ6006", wrong.Error().Rule);
    }

    private sealed record DdlColumn(string Name, string Type, bool Nullable);

    /// <summary>The statements of the DDL, the dollar-quoted block kept whole, without their semicolons.</summary>
    private static List<string> Statements(string ddl)
    {
        var list = new List<string>();
        var start = 0;
        var dollar = false;
        for (var i = 0; i < ddl.Length; i++)
        {
            if (string.CompareOrdinal(ddl, i, "$ledger$", 0, 8) == 0)
            {
                dollar = !dollar;
                i += 7;
            }
            else if (ddl[i] == ';' && !dollar)
            {
                list.Add(Regex.Replace(ddl[start..i], @"\s+", " ").Trim());
                start = i + 1;
            }
        }

        Assert.Equal("", ddl[start..].Trim());
        return list;
    }

    /// <summary>Each CREATE TABLE's columns, in order, with their type and whether they take null.</summary>
    private static Dictionary<string, List<DdlColumn>> Tables(string ddl)
    {
        var tables = new Dictionary<string, List<DdlColumn>>(StringComparer.Ordinal);
        foreach (var statement in Statements(ddl))
        {
            var match = CreateTable().Match(statement);
            if (!match.Success)
                continue;
            var columns = new List<DdlColumn>();
            foreach (var part in TopLevel(match.Groups["body"].Value))
            {
                var words = part.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var notNull = part.Contains("NOT NULL", StringComparison.Ordinal) || part.Contains("PRIMARY KEY", StringComparison.Ordinal);
                columns.Add(new DdlColumn(words[0], words[1], !notNull));
            }

            tables.Add(match.Groups["table"].Value, columns);
        }

        return tables;
    }

    private static IEnumerable<string> TopLevel(string body)
    {
        var depth = 0;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            switch (body[i])
            {
                case '(':
                    depth++;
                    break;
                case ')':
                    depth--;
                    break;
                case ',' when depth == 0:
                    yield return body[start..i].Trim();
                    start = i + 1;
                    break;
            }
        }

        yield return body[start..].Trim();
    }

    private static JsonNode Resolve(JsonNode schema, JsonNode property)
    {
        if (property["$ref"]?.GetValue<string>() is not { } reference)
            return property;
        if (reference.StartsWith("#/$defs/", StringComparison.Ordinal))
            return schema["$defs"]![reference["#/$defs/".Length..]]!;
        var common = JsonNode.Parse(TestServices.Schemas.GetFileBytes("common.json").Span)!;
        Assert.StartsWith("common.json#/$defs/", reference, StringComparison.Ordinal);
        return common["$defs"]![reference["common.json#/$defs/".Length..]]!;
    }

    private static List<string> Types(JsonNode property) => property["type"] switch
    {
        JsonArray array => [.. array.Select(n => n!.GetValue<string>())],
        JsonValue value => [value.GetValue<string>()],
        _ => [],
    };

    [GeneratedRegex(@"^CREATE TABLE IF NOT EXISTS migration_ledger\.(?<table>[a-z_]+) \((?<body>.*)\)$")]
    private static partial Regex CreateTable();
}
