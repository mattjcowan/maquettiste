using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// sql-ddl renders the members a table inherits from its stereotypes (next-phase-2-design.md section 3.1, task D13) with no template
/// change: they are ordinary members of the resolved table. One PostgreSQL database <c>pg</c> whose schema <c>app</c> gives
/// <c>audited</c> (two audit columns, an index and a check) to every table by default, and an <c>owned</c> stereotype that contributes
/// an owner column and a foreign key to the table each carrier names in <c>properties.ownerTable</c>. The same tables written with
/// those members as their own render the same SQL. The server case skips unless <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> names a
/// running container.
/// </summary>
public sealed partial class StereotypeMembersDdlTests
{
    private const int Audited = 20;
    private const int CreatedAt = 21;
    private const int UpdatedAt = 22;
    private const int RecentIndex = 23;
    private const int UpdatedCheck = 24;
    private const int Owned = 30;
    private const int OwnerId = 31;
    private const int OwnerKey = 32;

    private static string Id(int n) => "01SM" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    private static JsonObject Column(int n, string name, string type, Action<JsonObject>? more = null)
    {
        var column = new JsonObject { ["id"] = Id(n), ["name"] = name, ["type"] = type };
        more?.Invoke(column);
        return column;
    }

    // ---- the members, as a stereotype's or as a table's own (the same facets) ----

    private static JsonObject CreatedAtColumn(int n) => Column(n, "created_at", "datetimeoffset", c =>
    {
        c["nullable"] = false;
        c["defaultSql"] = new JsonObject { ["*"] = "CURRENT_TIMESTAMP" };
        c["comment"] = "When the row was written first.";
    });

    private static JsonObject UpdatedAtColumn(int n, int precision) => Column(n, "updated_at", "datetimeoffset", c => c["precision"] = precision);

    private static JsonObject OwnerColumn(int n) => Column(n, "owner_id", "uuid", c => c["nullable"] = false);

    private static JsonObject Index(int n, int column, string? name = null)
    {
        var index = new JsonObject { ["id"] = Id(n) };
        if (name is not null)
            index["name"] = name;
        index["columns"] = new JsonArray(new JsonObject { ["column"] = Id(column) });
        return index;
    }

    private static JsonObject Check(int n, string expression = "updated_at IS NULL OR updated_at >= created_at") =>
        new() { ["id"] = Id(n), ["expression"] = new JsonObject { ["*"] = expression } };

    /// <summary>
    /// A repo with the sql-ddl pack writing to <c>db</c>, the database <c>pg</c> (PostgreSQL 16) and the two stereotypes. With
    /// <paramref name="inherited"/>, schema <c>app</c> gives <c>audited</c> by default and the stereotypes carry the members;
    /// without, the stereotypes carry none and each table declares the members as its own.
    /// </summary>
    private static PackRepo Create(bool inherited, int precision = 6)
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.Write(".maquettiste/maquettiste.json", Json(new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "stereotype-members",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }) },
            ["packs"] = new JsonObject { ["sql-ddl"] = new JsonObject { ["output"] = "db" } },
        }));
        var schema = new JsonObject { ["id"] = Id(2), ["name"] = "app" };
        if (inherited)
            schema["defaultStereotypes"] = new JsonArray("audited");
        repo.Write(".maquettiste/model/databases/pg/database.json", Json(new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json",
            ["kind"] = "database",
            ["id"] = Id(1),
            ["name"] = "pg",
            ["dialect"] = "postgresql",
            ["version"] = "16",
            ["byConvention"] = "none",
            ["schemas"] = new JsonArray(schema),
        }));

        var audited = new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/stereotype.json",
            ["kind"] = "stereotype",
            ["id"] = Id(Audited),
            ["key"] = "audited",
            ["name"] = "Audited",
            ["appliesTo"] = new JsonArray("table"),
        };
        var owned = new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/stereotype.json",
            ["kind"] = "stereotype",
            ["id"] = Id(Owned),
            ["key"] = "owned",
            ["name"] = "Owned",
            ["appliesTo"] = new JsonArray("table"),
            ["parameters"] = JsonNode.Parse("""
                { "properties": { "ownerTable": { "type": "string", "x-ref": { "kind": "table", "sameDatabase": true } } }, "required": ["ownerTable"] }
                """),
        };
        if (inherited)
        {
            audited["columns"] = new JsonArray(CreatedAtColumn(CreatedAt), UpdatedAtColumn(UpdatedAt, precision));
            audited["checks"] = new JsonArray(Check(UpdatedCheck));
            audited["indexes"] = new JsonArray(Index(RecentIndex, CreatedAt));
            owned["columns"] = new JsonArray(OwnerColumn(OwnerId));
            owned["foreignKeys"] = JsonNode.Parse($$"""
                [ { "id": "{{Id(OwnerKey)}}", "columns": ["{{Id(OwnerId)}}"], "referencesParameter": "ownerTable", "onDelete": "cascade" } ]
                """);
        }

        repo.Write(".maquettiste/model/vocabularies/stereotypes/audited.json", Json(audited));
        repo.Write(".maquettiste/model/vocabularies/stereotypes/owned.json", Json(owned));

        Table(repo, inherited, precision, 100, "accounts", [Column(101, "name", "string", c => { c["length"] = 80; c["nullable"] = false; })], owner: false);
        Table(repo, inherited, precision, 200, "orders", [Column(201, "total", "decimal", c => { c["precision"] = 12; c["scale"] = 2; })], owner: true,
            ownCheck: Check(209, "total >= 0"));
        Table(repo, inherited, precision, 300, "notes", [Column(301, "body", "text", c => c["nullable"] = false)], owner: true, indexName: "ix_notes_recent");
        return repo;
    }

    /// <summary>
    /// Writes a designed table: <c>id</c> (its key), the own columns, then the members (inherited, or written as its own after its
    /// columns in the inherited order: audited's, then owned's). A carrier of <c>owned</c> names <c>accounts</c> as its owner.
    /// </summary>
    private static void Table(PackRepo repo, bool inherited, int precision, int n, string name, JsonObject[] columns, bool owner,
        JsonObject? ownCheck = null, string? indexName = null)
    {
        var table = new JsonObject
        {
            ["$schema"] = "../../../../.schema/v1/table.json",
            ["kind"] = "table",
            ["id"] = Id(n),
            ["name"] = name,
            ["database"] = Id(1),
            ["schema"] = Id(2),
            ["origin"] = "designed",
        };
        if (inherited && owner)
        {
            table["stereotypes"] = new JsonArray("owned");
            table["properties"] = new JsonObject { ["ownerTable"] = Id(100) };
        }

        var all = new List<JsonNode> { Column(n + 90, "id", "uuid", c => c["nullable"] = false) };
        all.AddRange(columns);
        var checks = new List<JsonNode>();
        if (ownCheck is not null)
            checks.Add(ownCheck);
        var indexes = new List<JsonNode>();
        var foreignKeys = new List<JsonNode>();
        if (!inherited)
        {
            all.Add(CreatedAtColumn(n + CreatedAt));
            all.Add(UpdatedAtColumn(n + UpdatedAt, precision));
            checks.Add(Check(n + UpdatedCheck));
            indexes.Add(Index(n + RecentIndex, n + CreatedAt, indexName));
            if (owner)
            {
                all.Add(OwnerColumn(n + OwnerId));
                foreignKeys.Add(JsonNode.Parse($$"""
                    { "id": "{{Id(n + OwnerKey)}}", "columns": ["{{Id(n + OwnerId)}}"], "referencesTable": "{{Id(100)}}", "onDelete": "cascade" }
                    """)!);
            }
        }
        else if (indexName is not null)
        {
            table["redefines"] = new JsonArray(new JsonObject { ["member"] = Id(RecentIndex), ["name"] = indexName });
        }

        table["columns"] = new JsonArray([.. all]);
        table["primaryKey"] = new JsonObject { ["columns"] = new JsonArray(Id(n + 90)) };
        if (foreignKeys.Count > 0)
            table["foreignKeys"] = new JsonArray([.. foreignKeys]);
        if (checks.Count > 0)
            table["checks"] = new JsonArray([.. checks]);
        if (indexes.Count > 0)
            table["indexes"] = new JsonArray([.. indexes]);
        repo.Write($".maquettiste/model/databases/pg/tables/{name}.json", Json(table));
    }

    /// <summary>A file's text with a migration's state hash left out.</summary>
    private static string StateFree(string text) => StateLine().Replace(text, "-- To state: (state)");

    [GeneratedRegex("^-- To state: [0-9a-f]+$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex StateLine();

    /// <summary>Every file the pack wrote under <c>db</c>, by relative path, with its text.</summary>
    private static SortedDictionary<string, string> Output(PackRepo repo)
    {
        var root = repo.PathOf("db");
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            files[Path.GetRelativePath(root, path).Replace('\\', '/')] = File.ReadAllText(path);
        return files;
    }

    [Fact]
    public async Task Inherited_members_render_like_own_members()
    {
        using var inherited = Create(inherited: true);
        using var own = Create(inherited: false);
        var result = await inherited.GenerateCleanlyAsync();
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule is "MQ4076" or "MQ4079" or "MQ4003" or "MQ4008");
        await own.GenerateCleanlyAsync();

        // The same files with the same bytes: names from the conventions per table, the comment from the member entry, the check
        // ordinal after the table's own, the inherited index among the declared ones. Only a migration's state differs: it hashes the
        // capture, whose keys are the members' keyed ids on one side and the own ids on the other.
        var files = Output(inherited);
        var expected = Output(own);
        Assert.Equal(expected.Keys, files.Keys);
        foreach (var (path, text) in expected)
            Assert.Equal(StateFree(text), StateFree(files[path]));
        var schema = files["pg/schema.sql"];
        Assert.Contains("    total numeric(12,2) NULL,\n    created_at timestamptz(6) NOT NULL DEFAULT CURRENT_TIMESTAMP,\n"
            + "    updated_at timestamptz(6) NULL,\n    owner_id uuid NOT NULL,\n    CONSTRAINT pk_orders PRIMARY KEY (id),", schema, StringComparison.Ordinal);
        Assert.Contains("COMMENT ON COLUMN app.notes.created_at IS 'When the row was written first.';", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inherited_constraint_names_follow_each_table()
    {
        using var repo = Create(inherited: true);
        await repo.GenerateCleanlyAsync();
        var schema = repo.Read("db/pg/schema.sql");

        // One index, check and foreign key per stereotype, named for each carrier; notes redefines the index's name only.
        foreach (var table in new[] { "accounts", "orders" })
            Assert.Contains($"CREATE INDEX ix_{table}_created_at ON app.{table} (created_at);", schema, StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_notes_recent ON app.notes (created_at);", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("ix_notes_created_at", schema, StringComparison.Ordinal);
        foreach (var table in new[] { "orders", "notes" })
            Assert.Contains($"CONSTRAINT fk_{table}_owner_id FOREIGN KEY (owner_id) REFERENCES app.accounts (id) ON DELETE CASCADE", schema, StringComparison.Ordinal);

        Assert.DoesNotContain("fk_accounts", schema, StringComparison.Ordinal); // accounts does not carry owned
        Assert.Contains("CONSTRAINT ck_accounts_1 CHECK (updated_at IS NULL OR updated_at >= created_at)", schema, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT ck_orders_1 CHECK (total >= 0)", schema, StringComparison.Ordinal);
        Assert.Contains("CONSTRAINT ck_orders_2 CHECK (updated_at IS NULL OR updated_at >= created_at)", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_migration_from_a_widened_stereotype_column_applies_to_every_carrier()
    {
        using var repo = Create(inherited: true, precision: 3);
        await repo.GenerateCleanlyAsync();
        var create = repo.Read("db/pg/schema.sql");

        // updated_at goes from precision 3 to 6 on the stereotype alone: one migration alters it in every table, no table file changes.
        var tables = Directory.EnumerateFiles(repo.PathOf(".maquettiste/model/databases/pg/tables")).ToDictionary(p => p, File.ReadAllText);
        repo.EditJson(".maquettiste/model/vocabularies/stereotypes/audited.json", s => s["columns"]![1]!["precision"] = 6);
        await repo.GenerateCleanlyAsync();
        Assert.All(tables, t => Assert.Equal(t.Value, File.ReadAllText(t.Key)));
        var migration = MigrationFiles.Read(repo, "pg", 2);
        foreach (var table in new[] { "accounts", "orders", "notes" })
            Assert.Contains($"ALTER TABLE app.{table} ALTER COLUMN updated_at TYPE timestamptz(6);", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("-- TODO:", migration, StringComparison.Ordinal);

        var db = DbServer.Postgres();
        if (db is null)
            return;
        var output = await db.RunAsync("stereotype_members", create,
            "INSERT INTO app.accounts (id, name, created_at, updated_at) VALUES ('00000000-0000-0000-0000-000000000001', 'billing', '2026-01-01 09:00:00+00', "
            + "'2026-01-01 10:00:00.123+00');\n"
            + "INSERT INTO app.notes (id, body, owner_id) VALUES ('00000000-0000-0000-0000-000000000002', 'paid', '00000000-0000-0000-0000-000000000001');",
            migration,
            "SELECT 'types=' || string_agg(c.relname || '.' || format_type(a.atttypid, a.atttypmod), ',' ORDER BY c.relname) FROM pg_attribute a "
            + "JOIN pg_class c ON c.oid = a.attrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'app' AND a.attname = 'updated_at' AND c.relkind = 'r';\n"
            + "SELECT 'rows=' || count(*) FROM app.notes;\n"
            + "SELECT 'updated=' || to_char(updated_at AT TIME ZONE 'UTC', 'HH24:MI:SS.MS') FROM app.accounts;");
        Assert.Contains("types=accounts.timestamp(6) with time zone,notes.timestamp(6) with time zone,orders.timestamp(6) with time zone", output, StringComparison.Ordinal);
        Assert.Contains("rows=1", output, StringComparison.Ordinal);
        Assert.Contains("updated=10:00:00.123", output, StringComparison.Ordinal);
        await db.RunAsync("stereotype_members_final", repo.Read("db/pg/schema.sql"));
    }
}
