using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// What "Create tables…" takes over from the sql-ddl pack (a reference type's lookup table) is adopted by the write's rekey fact: the
/// next schema diff reads it as already there, once, and an undo leaves nothing behind. Also the refusals that never guess (a storage
/// strategy the pack decides, a relation to an abstract base with no table) and the choices that come back with a refusal, over the
/// billing model's SQL Server database before its tables are made.
/// </summary>
public sealed class MaterializeAdoptionTests
{
    private const string Reporting = "01J92P0V2A0000000000000001";
    private const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";
    private const string Ingredient = "01JRDE00000000000000000002";
    private const string Recipe = "01JRDE00000000000000000001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<BatchResult> ApplyAsync(PackRepo repo, string operations)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operations + "]}"));
        Assert.Empty(parsed.Diagnostics);
        var result = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        return result;
    }

    private static async Task<MaterializePlan> PlanAsync(PackRepo repo, MaterializeRequest request)
    {
        await using var store = new ModelStore(repo.Repo.Options);
        return await store.PlanMaterializeAsync(request, Ct);
    }

    /// <summary>The migrations a run writes, each with its text.</summary>
    private static async Task<List<string>> NewMigrationsAsync(PackRepo repo)
    {
        var before = Migrations(repo);
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        return [.. Migrations(repo).Except(before).Select(p => File.ReadAllText(Path.Combine(repo.Repo.RepoRoot, p)))];
    }

    private static HashSet<string> Migrations(PackRepo repo) =>
        [.. Directory.EnumerateFiles(Path.Combine(repo.Repo.RepoRoot, "db"), "*.sql", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(repo.Repo.RepoRoot, p)).Where(p => p.Contains("migrations", StringComparison.Ordinal))];

    /// <summary>Whether a migration holds any schema statement (not only comments, the transaction, the ledger's rows and adoptions).</summary>
    private static bool ChangesSchema(string migration) => SchemaLines(migration).Count > 0;

    /// <summary>
    /// A migration's schema statements: those that are not comments, the transaction, the ledger's own objects and rows, or the block
    /// that creates what a rekey fact adopted (<see cref="AdoptionLines"/>), which changes nothing in a database that holds it.
    /// </summary>
    internal static List<string> SchemaLines(string migration) =>
        [.. Statements(migration).Where(s => !s.Adoption).Select(s => s.Text).Where(st => st is not ("BEGIN;" or "COMMIT;" or "GO" or "BEGIN TRANSACTION;" or "COMMIT TRANSACTION;")
            && !st.StartsWith("SET XACT_ABORT", StringComparison.Ordinal) && !st.Contains("migration_ledger", StringComparison.Ordinal))];

    /// <summary>
    /// The statements of a migration's adoption blocks: each block starts with a comment line "-- Adopted by migration fact" and ends at
    /// the next empty line. The sql-ddl pack writes there what a rekey fact of "Create tables…" adopted (a table or a reference column's
    /// constraint the earlier seed.sql made), so a database built from the migrations alone gets it.
    /// </summary>
    internal static List<string> AdoptionLines(string migration) => [.. Statements(migration).Where(s => s.Adoption).Select(s => s.Text)];

    /// <summary>
    /// Whether an adoption statement only creates what is missing: CREATE ... IF NOT EXISTS, a SQL Server OBJECT_ID or SCHEMA_ID test, a
    /// PostgreSQL DO block that tests pg_constraint, a MySQL prepared statement behind an information_schema test, an Oracle block
    /// that ignores "already exists", a comment set again, or rows inserted only when their key is missing.
    /// </summary>
    internal static bool IsIdempotent(string statement) =>
        new[]
        {
            "CREATE TABLE IF NOT EXISTS ", "CREATE SCHEMA IF NOT EXISTS ", "CREATE INDEX IF NOT EXISTS ", "CREATE UNIQUE INDEX IF NOT EXISTS ", "CREATE TRIGGER IF NOT EXISTS ",
            "IF OBJECT_ID(", "IF SCHEMA_ID(", "IF NOT EXISTS (SELECT 1 FROM sys.indexes ", "DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_constraint ", "SET @mq_sql = IF(NOT EXISTS ",
            "PREPARE mq_stmt ", "EXECUTE mq_stmt;", "DEALLOCATE PREPARE mq_stmt;", "COMMENT ON ",
        }.Any(p => statement.StartsWith(p, StringComparison.Ordinal))
        || (statement.StartsWith("BEGIN\n    EXECUTE IMMEDIATE ", StringComparison.Ordinal) && statement.Contains("IF SQLCODE NOT IN (", StringComparison.Ordinal))
        // A reference type's rows, inserted when their code is missing.
        || (statement.StartsWith("INSERT INTO ", StringComparison.Ordinal) && statement.EndsWith(" DO NOTHING;", StringComparison.Ordinal))
        || statement.StartsWith("INSERT IGNORE INTO ", StringComparison.Ordinal)
        || (statement.StartsWith("MERGE INTO ", StringComparison.Ordinal) && statement.Contains("\nWHEN NOT MATCHED THEN INSERT", StringComparison.Ordinal)
            && !statement.Contains("WHEN MATCHED THEN", StringComparison.Ordinal));

    /// <summary>A migration's statements in order, each marked when it is in an adoption block.</summary>
    private static List<(string Text, bool Adoption)> Statements(string migration)
    {
        var statements = new List<(string, bool)>();
        var current = new StringBuilder();
        var block = "";
        var adoption = false;
        foreach (var raw in migration.Split('\n'))
        {
            var l = raw.TrimEnd();
            if (current.Length == 0 && l.Length == 0)
            {
                adoption = false;
                continue;
            }

            if (current.Length == 0 && l.StartsWith("--", StringComparison.Ordinal))
            {
                adoption |= l.StartsWith("-- Adopted by migration fact", StringComparison.Ordinal);
                continue;
            }

            if (current.Length == 0)
                block = l.StartsWith("DO $", StringComparison.Ordinal) && !l.EndsWith("$;", StringComparison.Ordinal) ? "do" : l == "BEGIN" ? "plsql" : "";
            current.Append(l).Append('\n');
            var done = block switch
            {
                "do" => l.StartsWith("END $", StringComparison.Ordinal),
                "plsql" => l == "/",
                _ => l.EndsWith(';') || l == "GO",
            };
            if (done)
            {
                statements.Add((current.ToString().TrimEnd(), adoption));
                current.Clear();
            }
        }

        return statements;
    }



    [Fact]
    public async Task A_lookup_table_keeps_the_schema_its_storage_names_and_the_database_declares_it()
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        repo.EditJson(".maquettiste/maquettiste.json", s => s["conventions"]!["referenceStorage"]!["options"] = new JsonObject { ["schema"] = "ref" });

        await ApplyAsync(repo, $$"""{"op":"materialize-tables","database":"{{PackRepo.MainDatabaseId}}","entities":["{{Ingredient}}","{{Recipe}}"]}""");

        await using var store = new ModelStore(repo.Repo.Options);
        var snapshot = await store.GetSnapshotAsync(Ct);
        var main = snapshot.Get<Database>(PackRepo.MainDatabaseId)!;
        var schema = Assert.Single(main.Schemas, s => s.Name == "ref");
        Assert.Contains(snapshot.All<Table>(), t => t.Database == main.Id && t.Name == "allergens" && t.Schema == schema.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("my-lookup")]
    public async Task A_storage_strategy_the_pack_decides_is_refused_naming_the_type(string? strategy)
    {
        using var repo = PackRepo.ReferenceData("lookup-table");
        repo.EditJson(".maquettiste/maquettiste.json", s =>
        {
            if (strategy is null)
                s["conventions"]!.AsObject().Remove("referenceStorage");
            else
                s["conventions"]!["referenceStorage"]!["strategy"] = strategy;
        });
        if (strategy is not null)
        {
            repo.EditJson(".maquettiste/maquettiste.json", s => s["referenceData"]!["strategies"]![strategy] = new JsonObject { ["collections"] = true });
            repo.EditJson(".maquettiste/maquettiste.json", s => s["packs"]!["sql-ddl"]!["parameters"] = JsonNode.Parse(
                "{\"strategyMap\": {\"" + strategy + "\": \"lookup-table\", \"check\": \"check\", \"lookup-table\": \"lookup-table\", \"native\": \"native\"} }"));
        }

        var plan = await PlanAsync(repo, new MaterializeRequest("materialize-tables", PackRepo.MainDatabaseId, [Ingredient, Recipe]));

        Assert.False(plan.Valid);
        var message = Assert.Single(plan.Diagnostics).Message;
        Assert.Contains("Reference type '", message, StringComparison.Ordinal);
        Assert.Contains("Set conventions.referenceStorage (or the type's own storage) to lookup-table, check or native", message, StringComparison.Ordinal);
    }


    [Fact]
    public async Task A_relation_to_an_abstract_base_stored_per_concrete_entity_is_refused_and_the_choices_come_back()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        repo.Write(".maquettiste/model/entities/document.json", """
            { "kind": "entity", "id": "01K9MATER000000000000000B0", "name": "Document", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "abstract": true,
              "key": { "attributes": ["01K9MATER000000000000000B1"] },
              "attributes": [ { "id": "01K9MATER000000000000000B1", "name": "id", "type": "uuid", "required": true } ] }
            """);
        repo.Write(".maquettiste/model/entities/memo.json", """
            { "kind": "entity", "id": "01K9MATER000000000000000B2", "name": "Memo", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "base": "01K9MATER000000000000000B0",
              "attributes": [ { "id": "01K9MATER000000000000000B3", "name": "text", "type": "string", "length": 80 } ] }
            """);
        repo.Write(".maquettiste/model/relations/files.json", $$"""
            { "kind": "relation", "id": "01K9MATER000000000000000B4", "name": "files", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "relationKind": "association",
              "ends": [ { "id": "01K9MATER000000000000000B5", "role": "customer", "entity": "{{Customer}}", "min": 0, "max": "*" },
                        { "id": "01K9MATER000000000000000B6", "role": "document", "entity": "01K9MATER000000000000000B0", "min": 0, "max": 1 } ] }
            """);

        var plan = await PlanAsync(repo, new MaterializeRequest("materialize-tables", Reporting, [Customer, "01K9MATER000000000000000B0", "01K9MATER000000000000000B2"])
        {
            Hierarchy = new Dictionary<string, string> { ["*"] = "per-concrete" },
        });

        Assert.False(plan.Valid);
        Assert.Contains("Relation 'files'", Assert.Single(plan.Diagnostics).Message, StringComparison.Ordinal);
        var choice = Assert.Single(plan.Choices!.Hierarchies);
        Assert.Equal(("Document", "per-concrete"), (choice.RootName, choice.Shape));
        var again = await PlanAsync(repo, new MaterializeRequest("materialize-tables", Reporting, [Customer, "01K9MATER000000000000000B0", "01K9MATER000000000000000B2"])
        {
            Hierarchy = new Dictionary<string, string> { ["01K9MATER000000000000000B0"] = "per-type" },
        });
        Assert.True(again.Valid, string.Join("; ", again.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    [Fact]
    public async Task A_relation_list_attribute_can_be_stored_as_json_by_its_id_from_the_choices()
    {
        using var repo = PackRepo.BillingDialectsWithoutTables();
        repo.EditJson(".maquettiste/model/relations/settles.json", r => r["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = "01K9MATER00000000000000050", ["name"] = "remarks", ["type"] = "string", ["length"] = 40, ["collection"] = true,
        }));
        var entities = new[] { Customer, "01J92P0V0FJ23CGSNKM7P1W5V7", "01J92P0V2C0000000000000001", "01J92P0V0GWFR78HZH0P8Z3GY7", "01J92P0V0HEGSC6MW92CST5KA6", "01J92P0V0JR8BE8253SKT29ZG7" };

        var refused = await PlanAsync(repo, new MaterializeRequest("materialize-tables", Reporting, entities));
        Assert.False(refused.Valid);
        Assert.Contains(refused.Choices!.Lists, l => l.Attribute == "01K9MATER00000000000000050" && l.EntityName == "settles");

        var json = await PlanAsync(repo, new MaterializeRequest("materialize-tables", Reporting, entities)
        {
            ListStorage = new Dictionary<string, string> { ["01K9MATER00000000000000050"] = "json" },
        });
        Assert.True(json.Valid, string.Join("; ", json.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message)));
    }
}
