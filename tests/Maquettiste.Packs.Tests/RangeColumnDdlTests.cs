using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Range and multirange columns in sql-ddl (next-phase-1-design.md section 3.4): PostgreSQL's built-in range types in the create
/// script, and the migrations between them, each with the USING range_using gives it (a text round trip between two ranges or two
/// multiranges, PostgreSQL's own cast from a range to the multirange of its subtype) or a TODO and no ALTER where no cast exists;
/// moving between the old idiom (string with nativeType tstzrange) and a range keyword of the same native type writes nothing. One
/// PostgreSQL database <c>pg</c> (schema <c>app</c>) with a table <c>slots</c>; the server cases skip unless
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> names a running container.
/// </summary>
public sealed class RangeColumnDdlTests
{
    private static string Id(int n) => "01RC" + n.ToString("D22", CultureInfo.InvariantCulture);

    private static JsonObject Column(int n, string name, string type, string? subtype = null, Action<JsonObject>? more = null)
    {
        var column = new JsonObject { ["id"] = Id(n), ["name"] = name, ["type"] = type };
        if (subtype is not null)
            column["subtype"] = subtype;
        more?.Invoke(column);
        return column;
    }

    private static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";

    /// <summary>A repo with the sql-ddl pack writing to <c>db</c>, and the database <c>pg</c> (PostgreSQL 16).</summary>
    private static PackRepo Create()
    {
        var repo = PackRepo.Blank();
        PackRepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.Write(".maquettiste/maquettiste.json", Json(new JsonObject
        {
            ["$schema"] = ".schema/v1/maquettiste.json",
            ["formatVersion"] = 1,
            ["name"] = "range-columns",
            ["outputs"] = new JsonObject { ["allow"] = new JsonArray(new JsonObject { ["path"] = "db" }) },
            ["packs"] = new JsonObject { ["sql-ddl"] = new JsonObject { ["output"] = "db" } },
        }));
        repo.Write(".maquettiste/model/databases/pg/database.json", Json(new JsonObject
        {
            ["$schema"] = "../../../.schema/v1/database.json",
            ["kind"] = "database",
            ["id"] = Id(1),
            ["name"] = "pg",
            ["dialect"] = "postgresql",
            ["version"] = "16",
            ["byConvention"] = "none",
            ["schemas"] = new JsonArray(new JsonObject { ["id"] = Id(2), ["name"] = "app" }),
        }));
        return repo;
    }

    /// <summary>Writes the table <c>slots</c>: the key <c>id</c>, then the given columns, and the given indexes.</summary>
    private static void Slots(PackRepo repo, JsonObject[] columns, params JsonObject[] indexes)
    {
        var table = new JsonObject
        {
            ["$schema"] = "../../../../.schema/v1/table.json",
            ["kind"] = "table",
            ["id"] = Id(10),
            ["name"] = "slots",
            ["database"] = Id(1),
            ["schema"] = Id(2),
            ["origin"] = "designed",
            ["columns"] = new JsonArray([Column(11, "id", "int64", more: c => c["nullable"] = false), .. columns]),
            ["primaryKey"] = new JsonObject { ["name"] = "pk_slots", ["columns"] = new JsonArray(Id(11)) },
        };
        if (indexes.Length > 0)
            table["indexes"] = new JsonArray([.. indexes]);
        repo.Write(".maquettiste/model/databases/pg/tables/slots.json", Json(table));
    }

    /// <summary>
    /// The golden model. Step 1: seats (range of int32), stay (range of date), free (multirange of int32), and during (range of
    /// datetimeoffset, not null, with a literal default and a gist index). Step 2: seats becomes a range of int64 and free a
    /// multirange of int64 (no direct cast: a text round trip), stay the multirange of date (PostgreSQL's cast).
    /// </summary>
    private static void Step(PackRepo repo, int step) => Slots(repo,
        [
            Column(12, "seats", "range", step == 1 ? "int32" : "int64"),
            Column(13, "stay", step == 1 ? "range" : "multirange", "date"),
            Column(14, "free", "multirange", step == 1 ? "int32" : "int64"),
            Column(15, "during", "range", "datetimeoffset", c => { c["nullable"] = false; c["default"] = "[2026-01-01,2027-01-01)"; }),
        ],
        new JsonObject { ["id"] = Id(16), ["name"] = "ix_slots_during", ["method"] = "gist", ["columns"] = new JsonArray(new JsonObject { ["column"] = Id(15) }) });

    [Fact]
    public async Task Range_columns_create_and_migrate_on_postgresql()
    {
        using var repo = Create();
        Step(repo, 1);
        var first = await repo.GenerateCleanlyAsync();
        Assert.DoesNotContain(first.Diagnostics, d => d.Rule is "MQ4056" or "MQ4057" or "MQ3013");
        var golden = Fixtures.Path("golden", "sql-ddl", "range-columns");
        Golden.AssertMatches(Path.Combine(golden, "step1"), repo.PathOf("db"));
        var create = repo.Read("db/pg/schema.sql");
        Assert.Contains("    seats int4range NULL,\n    stay daterange NULL,\n    free int4multirange NULL,\n    during tstzrange NOT NULL DEFAULT '[2026-01-01,2027-01-01)',", create,
            StringComparison.Ordinal);
        Assert.Contains("CREATE INDEX ix_slots_during ON app.slots USING gist (during);", create, StringComparison.Ordinal);

        Step(repo, 2);
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(golden, "final"), repo.PathOf("db"));
        var migration = MigrationFiles.Read(repo, "pg", 2);
        Assert.Contains("ALTER TABLE app.slots ALTER COLUMN seats TYPE int8range USING seats::text::int8range;", migration, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE app.slots ALTER COLUMN stay TYPE datemultirange USING stay::datemultirange;", migration, StringComparison.Ordinal);
        Assert.Contains("ALTER TABLE app.slots ALTER COLUMN free TYPE int8multirange USING free::text::int8multirange;", migration, StringComparison.Ordinal);

        var db = DbServer.Postgres();
        if (db is null)
            return;
        var output = await db.RunAsync("range_columns", create,
            "INSERT INTO app.slots (id, seats, stay, free) VALUES (1, '[1,10)', '[2026-01-01,2026-02-01)', '{[1,3),[5,8)}');",
            migration,
            "SELECT 'row=' || seats::text || '|' || stay::text || '|' || free::text || '|' || during::text FROM app.slots;\n"
            + "SELECT 'types=' || string_agg(format_type(atttypid, atttypmod), ',' ORDER BY attnum) FROM pg_attribute WHERE attrelid = 'app.slots'::regclass AND attnum > 1;");
        Assert.Contains("row=[1,10)|{[2026-01-01,2026-02-01)}|{[1,3),[5,8)}|[\"2026-01-01 00:00:00+00\",\"2027-01-01 00:00:00+00\")", output, StringComparison.Ordinal);
        Assert.Contains("types=int8range,datemultirange,int8multirange,tstzrange", output, StringComparison.Ordinal);
        await db.RunAsync("range_columns_final", repo.Read("db/pg/schema.sql"));
    }

    [Fact]
    public async Task Multirange_to_range_is_a_todo_without_an_alter()
    {
        using var repo = Create();
        Slots(repo, [Column(12, "days", "multirange", "date"), Column(13, "seats", "range", "int32")]);
        await repo.GenerateCleanlyAsync();
        Slots(repo, [Column(12, "days", "range", "date"), Column(13, "seats", "multirange", "decimal")]);
        await repo.GenerateCleanlyAsync();

        var migration = MigrationFiles.Read(repo, "pg", 2);
        Assert.Contains("-- TODO: slots.days changes from datemultirange to daterange, which PostgreSQL cannot convert (no cast exists from a multirange to a "
            + "range, and range_merge keeps only the outer bounds of its ranges); give a change-type migration fact the conversion expression. This "
            + "migration leaves the column as it is.", migration, StringComparison.Ordinal);
        Assert.Contains("-- TODO: slots.seats changes from int4range to nummultirange, which PostgreSQL cannot convert (no cast exists from a range to a "
            + "multirange of another subtype)", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN", migration, StringComparison.Ordinal);
        Assert.DoesNotContain("the cast keeps each value", migration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_old_string_idiom_and_a_range_column_with_one_native_type_write_no_alter()
    {
        using var repo = Create();
        JsonObject Idiom() => Column(12, "during", "string", more: c => c["nativeType"] = "tstzrange");
        JsonObject Range() => Column(12, "during", "range", "datetimeoffset");
        Slots(repo, [Idiom()]);
        await repo.GenerateCleanlyAsync();
        Slots(repo, [Range()]);
        await repo.GenerateCleanlyAsync();
        Slots(repo, [Idiom()]);
        await repo.GenerateCleanlyAsync();

        foreach (var revision in new[] { 2, 3 })
        {
            var migration = MigrationFiles.Read(repo, "pg", revision);
            Assert.DoesNotContain("ALTER TABLE", migration, StringComparison.Ordinal);
            Assert.DoesNotContain("-- TODO:", migration, StringComparison.Ordinal);
        }
    }
}
