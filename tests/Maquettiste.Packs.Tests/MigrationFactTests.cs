using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine;
using Maquettiste.Engine.Ledger;
using Maquettiste.Engine.Migrations;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Migration facts in sql-ddl's migrations (next-phase-1-design.md section 3.2, sql-ddl): files named by revision and content with
/// their states in the header, fact steps in place of the diff's changes on PostgreSQL, held facts as notes, the fact types phase 1
/// writes no step for as TODO lines, and one ledger row per fact a migration reaches, in its transaction. Facts are written as files
/// of the billing fixture beside the model edits they describe; the PostgreSQL cases skip unless
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c> names a running container.
/// </summary>
public sealed partial class MigrationFactTests
{
    private const string Skip = "Name a PostgreSQL container in the environment to run the migrations on it (see DbServer).";
    private const string MainId = "01J92P0V1QRN2181XM2ZWE02W4";
    private const string Notes = "01K6BND0000000000000000001";
    private const string NotesEntityType = "01K6BND0000000000000000003";
    private const string NotesBody = "01K6BND0000000000000000005";
    private const string Invoices = "01J92P0V0FJ23CGSNKM7P1W5V7@" + MainId;
    private const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string Payments = "01J92P0V0HEGSC6MW92CST5KA6@" + MainId;
    private const string PaymentReference = "01J92P0V14NH6GF73G93R3E22D";
    private const string InvoiceStatus = "01J92P0V0VB1Z49SWERMAGR4TV";
    private const string Customers = "01J92P0V0ETQKXXP951CMMNHH3@" + MainId;
    private const string CustomerSince = "01J92P0V0P4MBJRM6QN6H0RSVP";
    private const string NotesPath = ".maquettiste/model/databases/main/tables/notes.json";
    private const string InvoiceOverlayPath = ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json";

    private const string RenameBody = "01JFDA00000000000000000001";
    private const string RenameEntityType = "01JFDA00000000000000000002";
    private const string NarrowNumber = "01JFDA00000000000000000003";
    private const string FillSince = "01JFDA00000000000000000004";
    private const string DropReference = "01JFDA00000000000000000005";
    private const string RawStep = "01JFDA00000000000000000006";
    private const string HeldDrop = "01JFDA00000000000000000007";
    private const string Adopt = "01JFDA00000000000000000009";
    private const string Baseline = "01JFDA0000000000000000000A";
    private const string CastDuring = "01JFDA0000000000000000000B";
    private const string CastSpans = "01JFDA0000000000000000000C";
    private const string NotesDuring = "01K6BND00000000000000000R1";
    private const string NotesSpans = "01K6BND00000000000000000R2";

    [Fact]
    public async Task Migration_files_are_named_by_revision_and_content_and_name_their_states()
    {
        using var a = PackRepo.Billing();
        using var b = PackRepo.Billing();
        var firstA = MigrationFiles.Added(await a.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");
        var firstB = MigrationFiles.Added(await b.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");

        // The name is the revision and the content key; two copies of one model write the same file.
        Assert.Matches(MigrationFiles.Named(), Path.GetFileName(firstA));
        Assert.StartsWith("db/main/migrations/0001-", firstA, StringComparison.Ordinal);
        Assert.Equal(firstA, firstB);
        var first = a.Read(firstA);
        Assert.Equal("none", Header(first, "From state"));
        Assert.Matches("^[0-9a-f]{12}$", Header(first, "To state"));
        Assert.Equal("none", Header(first, "Facts"));

        // Two copies that diverge from one revision write distinct names that sort side by side, each following the first.
        RenameNotesColumn(a, RenameBody, NotesBody, "body", "text");
        RenameNotesColumn(b, RenameEntityType, NotesEntityType, "entity_type", "kind");
        var secondA = MigrationFiles.Added(await a.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");
        var secondB = MigrationFiles.Added(await b.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");
        Assert.StartsWith("db/main/migrations/0002-", secondA, StringComparison.Ordinal);
        Assert.StartsWith("db/main/migrations/0002-", secondB, StringComparison.Ordinal);
        Assert.NotEqual(secondA, secondB);
        foreach (var (repo, path, fact) in new[] { (a, secondA, RenameBody), (b, secondB, RenameEntityType) })
        {
            var text = repo.Read(path);
            Assert.StartsWith("-- Migration 0002 of database main (PostgreSQL 16): schema revision 1 to 2.\n", text, StringComparison.Ordinal);
            Assert.Equal(Header(first, "To state"), Header(text, "From state"));
            Assert.NotEqual(Header(text, "From state"), Header(text, "To state"));
            Assert.Equal(fact, Header(text, "Facts"));
            Assert.Equal(firstA, MigrationFiles.Of(repo, "main", 1));
        }

        // The rename is the diff's own statement, followed by its ledger row, after the ledger DDL.
        var renamed = a.Read(secondA);
        AssertBefore(renamed, "BEGIN;\n\n" + Prelude() + "\n", "ALTER TABLE billing.notes RENAME COLUMN body TO text;");
        AssertBefore(renamed, "ALTER TABLE billing.notes RENAME COLUMN body TO text;", Row(a, RenameBody));
        Assert.Contains("ALTER TABLE billing.notes RENAME COLUMN body TO text;\n\n" + Row(a, RenameBody) + "\n", renamed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Migration_files_under_against_are_named_by_content()
    {
        using var repo = PackRepo.Billing();
        var first = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        using var baseline = new TempRepo();
        PackRepo.CopyTree(repo.PathOf(".maquettiste"), baseline.ModelRoot);
        RenameNotesColumn(repo, RenameBody, NotesBody, "body", "text");

        // Against a folder: the file has no revision, and it starts from the state the baseline model is at.
        await using (var store = new ModelStore(repo.Repo.Options))
        {
            var service = new GenerationService(store, repo.Repo.Options) { AllowFolderBaselines = true };
            var run = await service.RunAsync(new GenerationRequest { Packs = ["sql-ddl"], Against = baseline.RepoRoot }, null, TestContext.Current.CancellationToken);
            Assert.True(run.Outcome == RunOutcome.Succeeded, PackRepo.Describe(run));
            var path = MigrationFiles.Added(run, "main");
            var key = Path.GetFileNameWithoutExtension(path);
            Assert.Matches(MigrationFiles.Keyed(), Path.GetFileName(path));
            var text = repo.Read(path);
            Assert.StartsWith($"-- Migration {key} of database main (PostgreSQL 16): from a baseline model.\n", text, StringComparison.Ordinal);
            Assert.Equal(Header(first, "To state"), Header(text, "From state"));
            Assert.Equal(RenameBody, Header(text, "Facts"));
            Assert.Contains("ALTER TABLE billing.notes RENAME COLUMN body TO text;\n\n" + Row(repo, RenameBody), text, StringComparison.Ordinal);
        }

        // The committed snapshot was left alone: the next plain run diffs from revision 1, under a revision name and another key.
        var second = MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");
        Assert.StartsWith("db/main/migrations/0002-", second, StringComparison.Ordinal);
        Assert.Equal(Header(first, "To state"), Header(repo.Read(second), "From state"));
    }

    [Fact]
    public async Task A_complete_change_type_renders_its_data_check_and_conversion()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        NarrowInvoiceNumber(repo, NarrowNumber, """{ "cast": true }""", "select 1 from {table} where length({column}) > 20");
        var migration = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));

        var guard = "DO $mq$ BEGIN IF EXISTS (select 1 from billing.invoices where length(number) > 20) THEN RAISE EXCEPTION "
            + $"'migration fact {NarrowNumber}: the data check found rows the step must not find'; END IF; END $mq$;";
        const string Alter = "ALTER TABLE billing.invoices ALTER COLUMN number TYPE varchar(20) USING number::varchar(20);";
        Assert.Contains(guard + "\n\n" + Alter + "\n\n" + Row(repo, NarrowNumber) + "\n", migration, StringComparison.Ordinal);
        // The fact replaced the narrowing TODO; the view that reads the column is dropped before and created again after.
        Assert.DoesNotContain("TODO: invoices.number", migration, StringComparison.Ordinal);
        AssertBefore(migration, "DROP VIEW billing.outstanding_invoices;", guard);
        AssertBefore(migration, Alter, "CREATE VIEW billing.outstanding_invoices");
        Assert.Contains("BEGIN;\n\n" + Prelude() + "\n\n", migration, StringComparison.Ordinal);
        Assert.Contains("-- One transaction: the DDL and a migration ledger row for each migration fact the migration reaches (1).\n", migration,
            StringComparison.Ordinal);
        Assert.Equal(NarrowNumber, Header(migration, "Facts"));

        // An expression conversion takes the cast's place, with {table} and {column} expanded; idempotent, the step is guarded by the
        // column's type in the catalog.
        using var expressed = PackRepo.Billing();
        expressed.EditJson(".maquettiste/maquettiste.json", s => s["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["idempotent"] = true });
        await expressed.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        NarrowInvoiceNumber(expressed, NarrowNumber, """{ "expression": { "postgresql": "left({column}, 20)" } }""", null, "No number is longer than 20 characters.");
        var guarded = expressed.Read(MigrationFiles.Added(await expressed.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        Assert.Contains($"-- Migration fact {NarrowNumber} needs no data check: No number is longer than 20 characters.\n\nDO $mq$ BEGIN\n"
            + "CREATE TEMP TABLE mq_type_probe (probe varchar(20)) ON COMMIT DROP;\n", guarded, StringComparison.Ordinal);
        Assert.Contains("a.attrelid = to_regclass('billing.invoices') AND a.attname = 'number' AND NOT a.attisdropped", guarded, StringComparison.Ordinal);
        Assert.Contains("THEN\nALTER TABLE billing.invoices ALTER COLUMN number TYPE varchar(20) USING left(number, 20);\nEND IF;\nDROP TABLE mq_type_probe;\nEND $mq$;\n\n"
            + Row(expressed, NarrowNumber), guarded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cast_fact_on_a_range_column_renders_through_range_using()
    {
        using var repo = PackRepo.Billing();
        NotesRanges(repo, ("range", "int32"), ("multirange", "int32"));
        var first = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        NotesRanges(repo, ("range", "int64"), ("range", "int32"));
        CastNotesRange(repo, CastDuring, NotesDuring, "during", ("range", "int4range"), ("range", "int8range"));
        CastNotesRange(repo, CastSpans, NotesSpans, "spans", ("multirange", "int4multirange"), ("range", "int4range"));
        var second = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));

        // Between two ranges the cast goes through the text form, as the diff's own retype does; the fact's row follows its step.
        const string Alter = "ALTER TABLE billing.notes ALTER COLUMN during TYPE int8range USING during::text::int8range;";
        Assert.Contains(Alter + "\n\n" + Row(repo, CastDuring) + "\n", second, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: notes.during", second, StringComparison.Ordinal);
        // No cast exists from a multirange to a range: the fact cannot be applied as written, so a TODO that names the fact takes
        // the diff's place, the column stays as it is and no row is written.
        Assert.Contains($"-- TODO: migration fact {CastSpans} (change-type of column notes.spans) casts between range types: the column changes "
            + "from int4multirange to int4range, which PostgreSQL cannot convert (no cast exists from a multirange to a range, and range_merge "
            + "keeps only the outer bounds of its ranges); give the fact a conversion expression. This migration leaves the column as it is and "
            + "records nothing for the fact.\n", second, StringComparison.Ordinal);
        Assert.DoesNotContain("-- TODO: notes.spans", second, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER COLUMN spans", second, StringComparison.Ordinal);
        Assert.DoesNotContain(Row(repo, CastSpans), second, StringComparison.Ordinal);

        var db = DbServer.Postgres();
        if (db is null)
            return;
        var output = await db.RunAsync("range_cast_fact", first, Rows + "UPDATE billing.notes SET during = '[1,5)', spans = '{[1,3),[7,9)}';\n", second,
            "SELECT 'during=' || during::text || ' ' || pg_typeof(during) || ' spans=' || spans::text || ' ' || pg_typeof(spans) FROM billing.notes;\n"
            + "SELECT 'facts=' || string_agg(fact_id, ',' ORDER BY fact_id) FROM migration_ledger.applied_fact;\n");
        Assert.Contains("during=[1,5) int8range spans={[1,3),[7,9)} int4multirange", output, StringComparison.Ordinal);
        Assert.Contains("facts=" + CastDuring, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_released_held_drop_renders_in_the_next_migration()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);

        // Held: the migration keeps the column, says so where the drop would go, and records nothing.
        DropCustomerSince(repo, held: true, released: false);
        var held = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        Assert.Contains($"-- Held: migration fact {HeldDrop} (drop of column customers.customer_since) waits until it is released;", held,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DROP COLUMN", held, StringComparison.Ordinal);
        Assert.DoesNotContain("applied_fact", held, StringComparison.Ordinal);
        Assert.Equal("none", Header(held, "Facts"));

        // Released: the fact alone makes the next diff, whose migration drops the column the model left two revisions ago.
        DropCustomerSince(repo, held: true, released: true);
        var path = MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");
        Assert.StartsWith("db/main/migrations/0003-", path, StringComparison.Ordinal);
        var released = repo.Read(path);
        Assert.Equal(HeldDrop, Header(released, "Facts"));
        Assert.Equal(Header(held, "To state"), Header(released, "From state"));
        Assert.Contains("-- Data loss: dropping column customers.customer_since deletes its values.\n\n"
            + "ALTER TABLE IF EXISTS billing.customers DROP COLUMN IF EXISTS customer_since;\n\n" + Row(repo, HeldDrop) + "\n", released, StringComparison.Ordinal);

        // Rendered once: the committed snapshot lists it, so the diff is empty again.
        var again = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.DoesNotContain(again.Changes, c => c.Kind == FileChangeKind.Added);
    }

    /// <summary>
    /// Outside PostgreSQL no fact writes a step, but a held drop still waits: SQL Server and SQLite write the held note where the
    /// diff's drop would go, never the drop, and once the fact is released the next migration names it in a TODO line.
    /// </summary>
    [Fact]
    public async Task Outside_PostgreSQL_a_held_drop_writes_its_note_and_its_release_a_todo()
    {
        var databases = new[]
        {
            (Name: "reporting", Id: "01J92P0V2A0000000000000001", Fact: "01JFDA0000000000000000000D", Title: "SQL Server"),
            (Name: "local", Id: "01J92P0V2A0000000000000002", Fact: "01JFDA0000000000000000000E", Title: "SQLite"),
        };
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        repo.EditJson(".maquettiste/model/entities/customer.json", customer =>
        {
            var attributes = customer["attributes"]!.AsArray();
            attributes.Remove(attributes.First(a => (string?)a!["id"] == CustomerSince));
        });
        void Hold(bool released)
        {
            foreach (var db in databases)
            {
                repo.Write($".maquettiste/model/databases/{db.Name}/migrations/open/{db.Fact.ToLowerInvariant()}.json",
                    "{\n  \"$schema\": \"../../../../../.schema/v1/migration.json\",\n  \"kind\": \"migration\",\n  \"id\": \"" + db.Fact + "\",\n"
                    + "  \"database\": \"" + db.Id + "\",\n  \"fact\": \"drop\",\n  \"object\": {\n    \"kind\": \"column\",\n"
                    + "    \"table\": \"01J92P0V0ETQKXXP951CMMNHH3@" + db.Id + "\",\n    \"key\": \"" + CustomerSince + "\",\n"
                    + "    \"tableName\": \"customers\",\n    \"name\": \"customer_since\"\n  },\n"
                    + "  \"acknowledge\": \"Nobody reads the customer-since date any more.\",\n  \"held\": true,\n  \"released\": "
                    + (released ? "true" : "false") + "\n}\n");
            }
        }

        Hold(released: false);
        var held = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.Contains("DROP COLUMN", repo.Read(MigrationFiles.Added(held, "main")), StringComparison.Ordinal);
        foreach (var db in databases)
        {
            var migration = repo.Read(MigrationFiles.Added(held, db.Name));
            Assert.Contains($"-- Held: migration fact {db.Fact} (drop of column customers.customer_since) waits until it is released;", migration,
                StringComparison.Ordinal);
            Assert.DoesNotContain("DROP COLUMN", migration, StringComparison.Ordinal);
            Assert.DoesNotContain("Data loss", migration, StringComparison.Ordinal);
            Assert.Equal("none", Header(migration, "Facts"));
        }

        Hold(released: true);
        var released = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        foreach (var db in databases)
        {
            var migration = repo.Read(MigrationFiles.Added(released, db.Name));
            Assert.Equal(db.Fact, Header(migration, "Facts"));
            Assert.Contains($"-- TODO: migration fact {db.Fact} (drop of column customers.customer_since) was released after an earlier migration "
                + $"wrote its held note; {db.Title} has no fact steps, so reach its end state by hand.\n", migration, StringComparison.Ordinal);
            Assert.DoesNotContain("DROP COLUMN", migration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Apply_then_check_after_a_migration_is_clean()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        RenameNotesColumn(repo, RenameBody, NotesBody, "body", "text");
        NarrowInvoiceNumber(repo, NarrowNumber, """{ "cast": true }""", "select 1 from {table} where length({column}) > 20");
        var applied = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var path = MigrationFiles.Added(applied, "main");

        var check = await repo.GenerateAsync(GenerationMode.Check, packs: ["sql-ddl"]);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));
        Assert.DoesNotContain(check.Diagnostics, d => d.Rule == "MQ6018");
        var again = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        Assert.DoesNotContain(again.Changes, c => c.Kind is FileChangeKind.Added or FileChangeKind.Modified or FileChangeKind.OrphanedOwned);
        Assert.True(File.Exists(repo.PathOf(path)));
    }

    [Fact]
    public async Task Counter_named_migration_files_stay_owned_after_the_move_to_content_names()
    {
        // A project whose first migration carries the name earlier versions gave it (db/main/migrations/0001.sql), owned by the pack.
        using var repo = PackRepo.Billing();
        var first = MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main");
        const string Old = "db/main/migrations/0001.sql";
        File.Move(repo.PathOf(first), repo.PathOf(Old));
        var manifest = repo.Read(".maquettiste/manifest/sql-ddl.json");
        Assert.Contains("\"" + first + "\"", manifest, StringComparison.Ordinal);
        repo.Write(".maquettiste/manifest/sql-ddl.json", manifest.Replace("\"" + first + "\"", "\"" + Old + "\"", StringComparison.Ordinal));

        // Nothing renders for it, and it is neither drift nor an orphan.
        var check = await repo.GenerateAsync(GenerationMode.Check, packs: ["sql-ddl"], force: true);
        Assert.True(check.Outcome == RunOutcome.Succeeded, PackRepo.Describe(check));
        Assert.DoesNotContain(check.Changes, c => c.Path.StartsWith("db/main/migrations/", StringComparison.Ordinal) && c.Kind != FileChangeKind.Kept);

        // The next migration takes a content name beside it; the counter-named file stays on disk and owned.
        RenameNotesColumn(repo, RenameBody, NotesBody, "body", "text");
        var next = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"], force: true);
        Assert.StartsWith("db/main/migrations/0002-", MigrationFiles.Added(next, "main"), StringComparison.Ordinal);
        Assert.Contains(next.Changes, c => c.Path == Old && c.Kind == FileChangeKind.Kept);
        Assert.True(File.Exists(repo.PathOf(Old)));
        Assert.Contains("\"" + Old + "\"", repo.Read(".maquettiste/manifest/sql-ddl.json"), StringComparison.Ordinal);
        var after = await repo.GenerateAsync(GenerationMode.Check, packs: ["sql-ddl"]);
        Assert.True(after.Outcome == RunOutcome.Succeeded, PackRepo.Describe(after));
    }

    [Fact]
    public async Task Adopt_and_baseline_render_a_todo()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        AddFact(repo, Adopt, $$"""
              "database": "{{MainId}}",
              "fact": "adopt",
              "objects": [
                {
                  "kind": "table",
                  "key": "{{Customers}}",
                  "schema": "billing",
                  "name": "customers"
                }
              ],
              "environment": "production"
            """);
        AddFact(repo, Baseline, $$"""
              "database": "{{MainId}}",
              "fact": "baseline",
              "after": [
                "{{Adopt}}"
              ]
            """);
        var migration = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));

        Assert.Equal($"{Adopt}, {Baseline}", Header(migration, "Facts"));
        AssertBefore(migration, $"-- TODO: migration fact {Adopt} adopts customers (environment production); this pack writes no adopt step or object rows yet.",
            $"-- TODO: migration fact {Baseline} is a baseline");
        // Neither reaches its end state here, so the migration writes no ledger at all.
        Assert.DoesNotContain("applied_fact", migration, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerContract.DefaultSchema, migration, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_map_writes_a_data_step_on_PostgreSQL_and_a_TODO_elsewhere()
    {
        // Renaming the PaymentMethod code card records a value map in each database that stores the codes (payments.method).
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        await using (var store = new ModelStore(repo.Repo.Options))
        {
            var parsed = store.ParseBatch("""{ "operations": [ { "op": "rename-code", "id": "01J92P0V2F0000000000000001", "code": "card", "to": "credit" } ] }"""u8);
            var applied = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, TestContext.Current.CancellationToken);
            Assert.Equal(SaveOutcome.Saved, applied.Outcome);
            Assert.Equal(3, applied.Facts!.Count);
        }

        var result = await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]);
        var main = repo.Read(MigrationFiles.Added(result, "main"));
        var id = Header(main, "Facts");

        // PostgreSQL (a CHECK in main): the check is dropped, the stored codes mapped, the check created again, then the fact's row;
        // all before the schema statements.
        var step = $"-- Migration fact {id} (value-map): maps PaymentMethod codes card -> credit.\n\n"
            + "ALTER TABLE billing.payments DROP CONSTRAINT IF EXISTS ck_payments_method_ref;\n"
            + "ALTER TABLE billing.payments DROP CONSTRAINT IF EXISTS fk_payments_method_ref;\n\n"
            + "UPDATE billing.payments SET method = CASE method WHEN 'card' THEN 'credit' ELSE method END WHERE method IN ('card');\n\n"
            + "ALTER TABLE billing.payments ADD CONSTRAINT ck_payments_method_ref CHECK (method IN ('credit', 'transfer', 'cash'));\n\n"
            + Row(repo, id);
        Assert.Contains(step, main, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO: migration fact", main, StringComparison.Ordinal);

        // SQL Server and SQLite have no step: a TODO line names the codes and the column, and no ledger row is written.
        foreach (var (database, dialect) in new[] { ("reporting", "SQL Server"), ("local", "SQLite") })
        {
            var migration = repo.Read(MigrationFiles.Added(result, database));
            var fact = Header(migration, "Facts");
            Assert.Contains($"-- TODO: migration fact {fact} maps PaymentMethod codes card -> credit in payments.method; {dialect} has no value-map step in this pack: "
                + "update the stored codes, and rename what is named after the type, by hand.", migration, StringComparison.Ordinal);
            Assert.DoesNotContain("applied_fact", migration, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Fact_steps_run_on_postgres_and_a_second_run_does_nothing()
    {
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        using var repo = PackRepo.Billing();
        repo.EditJson(".maquettiste/maquettiste.json", s => s["packs"]!["sql-ddl"]!["parameters"] = new JsonObject { ["idempotent"] = true });
        var first = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));

        // A rename, a narrowing with a conversion that is not idempotent itself, a backfill, a drop and a raw step after the schema.
        RenameNotesColumn(repo, RenameBody, NotesBody, "body", "text");
        NarrowInvoiceNumber(repo, NarrowNumber, """{ "expression": { "*": "'N' || {column}" } }""", "select 1 from {table} where length({column}) > 19");
        FillCustomerSince(repo);
        DropPaymentReference(repo);
        AddRaw(repo, "UPDATE billing.notes SET text = 'none' WHERE text = '';", "SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM billing.notes WHERE text = '');");
        var second = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        Assert.Equal($"{RenameBody}, {NarrowNumber}, {FillSince}, {DropReference}, {RawStep}", Header(second, "Facts"));
        Assert.Contains("UPDATE billing.customers SET customer_since = DATE '2020-01-01' WHERE customer_since IS NULL;\n\n"
            + "ALTER TABLE billing.customers ALTER COLUMN customer_since SET NOT NULL;", second, StringComparison.Ordinal);

        var output = await db.RunAsync("facts_twice", first, Rows, second, second,
            "SELECT 'number=' || number || ' ' || format_type(atttypid, atttypmod) FROM billing.invoices, pg_attribute WHERE attrelid = 'billing.invoices'::regclass AND attname = 'number';\n"
            + "SELECT 'since=' || customer_since || ' ' || attnotnull FROM billing.customers, pg_attribute WHERE attrelid = 'billing.customers'::regclass AND attname = 'customer_since';\n"
            + "SELECT 'text=' || text FROM billing.notes;\n"
            + "SELECT 'reference=' || count(*) FROM information_schema.columns WHERE table_name = 'payments' AND column_name = 'reference';\n"
            + "SELECT 'facts=' || count(*) || ' ' || string_agg(fact_id, ',' ORDER BY fact_id) FROM migration_ledger.applied_fact;\n");

        // Each step ran once: the number got one prefix, the backfill filled the row, the raw step its guard allowed.
        Assert.Contains("number=NINV-1 character varying(20)", output, StringComparison.Ordinal);
        Assert.Contains("since=2020-01-01 true", output, StringComparison.Ordinal);
        Assert.Contains("text=none", output, StringComparison.Ordinal);
        Assert.Contains("reference=0", output, StringComparison.Ordinal);
        Assert.Contains($"facts=5 {string.Join(',', new[] { RenameBody, NarrowNumber, FillSince, DropReference, RawStep }.Order(StringComparer.Ordinal))}", output,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_migration_records_its_facts_once_and_a_failed_one_none()
    {
        var db = DbServer.Postgres();
        if (db is null)
        {
            Assert.Skip(Skip);
            return;
        }

        using var repo = PackRepo.Billing();
        var first = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        RenameNotesColumn(repo, RenameBody, NotesBody, "body", "text");
        NarrowInvoiceNumber(repo, NarrowNumber, """{ "cast": true }""", "select 1 from {table} where length({column}) > 20");
        var second = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        RenameNotesColumn(repo, RenameEntityType, NotesEntityType, "entity_type", "kind");
        var third = repo.Read(MigrationFiles.Added(await repo.GenerateCleanlyAsync(packs: ["sql-ddl"]), "main"));
        Assert.Equal(RenameEntityType, Header(third, "Facts"));
        Assert.DoesNotContain(RenameBody, third, StringComparison.Ordinal);

        const string Query = "SELECT 'ledger=' || coalesce(to_regclass('migration_ledger.applied_fact')::text, 'none');\n"
            + "SELECT 'body=' || count(*) FROM information_schema.columns WHERE table_name = 'notes' AND column_name = 'body';\n";
        const string Long = "UPDATE billing.invoices SET number = 'INV-0000000000000000000001';\n";
        var output = await db.RunAsync("facts_failed", first, Rows, Long, "\\set ON_ERROR_STOP off\n" + second + "\\set ON_ERROR_STOP on\n" + Query);

        // The data check stopped the migration: its transaction took the ledger DDL, the rename and the rows with it.
        Assert.Contains($"migration fact {NarrowNumber}: the data check found rows the step must not find", output, StringComparison.Ordinal);
        Assert.Contains("ledger=none", output, StringComparison.Ordinal);
        Assert.Contains("body=1", output, StringComparison.Ordinal);

        // Run as written, each migration records the facts it reaches, once each.
        output = await db.RunAsync("facts_once", first, Rows, second, third,
            "SELECT 'row=' || fact_id || ' ' || content_hash || ' ' || applied_by FROM migration_ledger.applied_fact ORDER BY fact_id;\n");
        var rows = FactRow().Matches(second + third).Select(m => "row=" + m.Groups["id"].Value + " " + m.Groups["hash"].Value + " sql-ddl").ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal(rows.Order(StringComparer.Ordinal), output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("row=", StringComparison.Ordinal)));
    }

    /// <summary>A customer, an invoice and an empty note, for the PostgreSQL cases.</summary>
    private const string Rows = """
        INSERT INTO billing.customers (id, name, email, created_at) VALUES ('00000000-0000-0000-0000-000000000001', 'Ada', 'ada@example.com', now());
        INSERT INTO billing.invoices (id, number, issued_on, created_at, customer_id) VALUES ('00000000-0000-0000-0000-000000000002', 'INV-1', DATE '2026-01-31', now(), '00000000-0000-0000-0000-000000000001');
        INSERT INTO billing.notes (id, entity_type, entity_id, body) VALUES ('00000000-0000-0000-0000-000000000003', 'invoice', '00000000-0000-0000-0000-000000000002', '');

        """;

    private static void RenameNotesColumn(PackRepo repo, string id, string column, string from, string to)
    {
        repo.EditJson(NotesPath, table => table["columns"]!.AsArray().First(c => (string?)c!["id"] == column)!["name"] = to);
        AddFact(repo, id, $$"""
              "database": "{{MainId}}",
              "fact": "rename",
              "object": {
                "kind": "column",
                "table": "{{Notes}}",
                "key": "{{column}}",
                "schema": "billing",
                "tableName": "notes",
                "name": "{{to}}"
              },
              "from": {
                "name": "{{from}}"
              },
              "to": {
                "name": "{{to}}"
              }
            """);
    }

    /// <summary>Sets the notes table's two range columns, during and spans, each a range keyword and a subtype.</summary>
    private static void NotesRanges(PackRepo repo, (string Type, string Subtype) during, (string Type, string Subtype) spans) => repo.EditJson(NotesPath, table =>
    {
        var columns = table["columns"]!.AsArray();
        foreach (var (id, name, (type, subtype)) in new[] { (NotesDuring, "during", during), (NotesSpans, "spans", spans) })
        {
            if (columns.FirstOrDefault(c => (string?)c!["id"] == id) is { } old)
                columns.Remove(old);
            columns.Add(new JsonObject { ["id"] = id, ["name"] = name, ["type"] = type, ["subtype"] = subtype });
        }
    });

    /// <summary>A change-type fact with a plain cast on a range column of the notes table.</summary>
    private static void CastNotesRange(PackRepo repo, string id, string column, string name, (string Type, string Native) from, (string Type, string Native) to) =>
        AddFact(repo, id, $$"""
              "database": "{{MainId}}",
              "fact": "change-type",
              "object": {
                "kind": "column",
                "table": "{{Notes}}",
                "key": "{{column}}",
                "schema": "billing",
                "tableName": "notes",
                "name": "{{name}}"
              },
              "from": {
                "type": "{{from.Type}}",
                "nativeType": "{{from.Native}}"
              },
              "to": {
                "type": "{{to.Type}}",
                "nativeType": "{{to.Native}}"
              },
              "conversion": { "cast": true }
            """);

    private static void NarrowInvoiceNumber(PackRepo repo, string id, string conversion, string? query, string? accept = null)
    {
        repo.EditJson(InvoiceOverlayPath, table => table["columns"]![0]!["nativeType"] = "varchar(20)");
        var check = query is not null ? $$"""{ "query": { "*": "{{query}}" } }""" : $$"""{ "accept": "{{accept}}" }""";
        AddFact(repo, id, $$"""
              "database": "{{MainId}}",
              "fact": "change-type",
              "object": {
                "kind": "column",
                "table": "{{Invoices}}",
                "key": "{{InvoiceNumber}}",
                "schema": "billing",
                "tableName": "invoices",
                "name": "number"
              },
              "from": {
                "type": "string",
                "length": 32,
                "nativeType": "varchar(32)"
              },
              "to": {
                "type": "string",
                "length": 32,
                "nativeType": "varchar(20)"
              },
              "conversion": {{conversion}},
              "dataCheck": {{check}}
            """);
    }

    private static void FillCustomerSince(PackRepo repo)
    {
        repo.EditJson(".maquettiste/model/entities/customer.json",
            customer => customer["attributes"]!.AsArray().First(a => (string?)a!["id"] == CustomerSince)!["required"] = true);
        AddFact(repo, FillSince, $$"""
              "database": "{{MainId}}",
              "fact": "backfill",
              "object": {
                "kind": "column",
                "table": "{{Customers}}",
                "key": "{{CustomerSince}}",
                "schema": "billing",
                "tableName": "customers",
                "name": "customer_since"
              },
              "value": {
                "*": "DATE '2020-01-01'"
              }
            """);
    }

    private static void DropCustomerSince(PackRepo repo, bool held, bool released)
    {
        repo.EditJson(".maquettiste/model/entities/customer.json", customer =>
        {
            var attributes = customer["attributes"]!.AsArray();
            if (attributes.FirstOrDefault(a => (string?)a!["id"] == CustomerSince) is { } since)
                attributes.Remove(since);
        });
        AddFact(repo, HeldDrop, $$"""
              "database": "{{MainId}}",
              "fact": "drop",
              "object": {
                "kind": "column",
                "table": "{{Customers}}",
                "key": "{{CustomerSince}}",
                "schema": "billing",
                "tableName": "customers",
                "name": "customer_since"
              },
              "acknowledge": "Nobody reads the customer-since date any more.",
              "held": {{(held ? "true" : "false")}},
              "released": {{(released ? "true" : "false")}}
            """);
    }

    private static void DropPaymentReference(PackRepo repo)
    {
        repo.EditJson(".maquettiste/model/entities/payment.json", payment =>
        {
            var attributes = payment["attributes"]!.AsArray();
            attributes.Remove(attributes.First(a => (string?)a!["id"] == PaymentReference));
        });
        AddFact(repo, DropReference, $$"""
              "database": "{{MainId}}",
              "fact": "drop",
              "object": {
                "kind": "column",
                "table": "{{Payments}}",
                "key": "{{PaymentReference}}",
                "schema": "billing",
                "tableName": "payments",
                "name": "reference"
              },
              "acknowledge": "Bank references are kept in the ledger export."
            """);
    }

    private static void AddRaw(PackRepo repo, string sql, string skipWhen) => AddFact(repo, RawStep, $$"""
          "database": "{{MainId}}",
          "fact": "raw",
          "sql": {
            "*": "{{sql}}"
          },
          "skipWhen": {
            "*": "{{skipWhen}}"
          }
        """);

    private static void AddFact(PackRepo repo, string id, string fields) =>
        repo.Write($".maquettiste/model/databases/main/migrations/open/{id.ToLowerInvariant()}.json",
            "{\n  \"$schema\": \"../../../../../.schema/v1/migration.json\",\n  \"kind\": \"migration\",\n  \"id\": \"" + id + "\",\n"
            + string.Join('\n', fields.TrimEnd().Split('\n').Select(l => l.StartsWith("  ", StringComparison.Ordinal) ? l : "  " + l)) + "\n}\n");

    /// <summary>The value of a migration's header line (<c>-- From state: ...</c>, <c>-- To state: ...</c>, <c>-- Facts: ...</c>).</summary>
    private static string Header(string migration, string name)
    {
        var line = migration.Split('\n').First(l => l.StartsWith("-- " + name + ": ", StringComparison.Ordinal));
        return line[("-- " + name + ": ").Length..];
    }

    private static string Prelude() => LedgerContract.Ddl(Dialect.PostgreSql, LedgerContract.DefaultSchema).TrimEnd('\n');

    /// <summary>The ledger row a migration writes for a fact of the repo, with the fact content hash of its file.</summary>
    private static string Row(PackRepo repo, string id)
    {
        using var document = JsonDocument.Parse(repo.Read($".maquettiste/model/databases/main/migrations/open/{id.ToLowerInvariant()}.json"));
        var hash = FactHash.Of(document.RootElement);
        return $"INSERT INTO migration_ledger.applied_fact (fact_id, content_hash, applied_by) VALUES ('{id}', '{hash}', 'sql-ddl') ON CONFLICT (fact_id) DO NOTHING;";
    }

    private static void AssertBefore(string text, string first, string second)
    {
        var a = text.IndexOf(first, StringComparison.Ordinal);
        var b = text.IndexOf(second, StringComparison.Ordinal);
        Assert.True(a >= 0 && b > a, $"Expected\n  {first}\nbefore\n  {second}\nin\n{text}");
    }

    [GeneratedRegex(@"^INSERT INTO migration_ledger\.applied_fact \(fact_id, content_hash, applied_by\) VALUES \('(?<id>[0-9A-Z]{26})', '(?<hash>[0-9a-f]{64})', 'sql-ddl'\) ON CONFLICT \(fact_id\) DO NOTHING;$", RegexOptions.Multiline)]
    private static partial Regex FactRow();
}
