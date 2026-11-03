using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// The acceptance tests of stereotypes that contribute structure (next-phase-2-design.md section 3.1, task D14), each on a copy of the
/// billing fixture with the sql-ddl pack. "AT1's copy": <c>audited</c> keeps its attributes and gains the columns <c>created_at</c> and
/// <c>updated_at</c> and an index on <c>created_at</c>; schema <c>billing</c> lists it in <c>defaultStereotypes</c>; <c>notes</c>, which
/// has its own <c>created_at</c>, excludes that member and the index on it. The shared fixture does not change.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class StereotypeMembersTests
{
    private const string Audited = "01J92P0V25BX5TMQ8N9F6AJE61";
    private const string AuditedPath = ".maquettiste/model/vocabularies/stereotypes/audited.json";
    private const string DatabasePath = ".maquettiste/model/databases/main/database.json";
    private const string TablesFolder = ".maquettiste/model/databases/main/tables/";
    private const string NotesPath = TablesFolder + "notes.json";
    private const string Notes = "01K6BND0000000000000000001";
    private const string NotesCreatedAt = "01K6BND0000000000000000006";
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string Main = E2ERepo.MainDatabaseId;
    private const string Customer = E2ERepo.CustomerId;
    private const string Invoice = E2ERepo.InvoiceId;
    private const string InvoiceLine = "01J92P0V0GWFR78HZH0P8Z3GY7";
    private const string Payment = "01J92P0V0HEGSC6MW92CST5KA6";
    private const string Product = E2ERepo.ProductId;
    private const string Settles = "01J92P0V1DKHD5Q02DV6S5M53D";
    private const string CreatedAt = "01JC7AVD1TC0000000000000C1";
    private const string UpdatedAt = "01JC7AVD1TC0000000000000C2";
    private const string CreatedIndex = "01JC7AVD1TC0000000000000X1";
    private const string OptOut = "01JC7AVD1TC0000000000000T1";
    private const string ProductOverlay = "01JC7AVD1TC0000000000000T2";

    /// <summary>The seven tables of schema billing.</summary>
    private static readonly string[] BillingTables = ["customers", "invoice_lines", "invoices", "notes", "payment_invoice", "payments", "products"];

    /// <summary>The tables of entities that carry <c>audited</c>: they store its attributes as columns and skip its members.</summary>
    private static readonly string[] Stored = ["customers", "invoices", "payments"];

    /// <summary>The tables that inherit both members and the index under AT1's copy.</summary>
    private static readonly string[] Inheriting = ["invoice_lines", "payment_invoice", "products"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Keyed(string tableKey, string member) => tableKey + "." + member;

    private static string Synthesized(string id) => id + "@" + Main;

    // ---- setup ----

    /// <summary>A copy of billing with the sql-ddl pack (output <c>db</c>), and nothing changed in the model yet.</summary>
    private static E2ERepo Billing()
    {
        var repo = E2ERepo.Create(demo: false);
        E2ERepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        return repo;
    }

    /// <summary>
    /// AT1's copy, written on disk before the store reads it. <paramref name="updatedPrecision"/> gives <c>updated_at</c> a precision.
    /// </summary>
    private static E2ERepo At1Copy(int? updatedPrecision = null)
    {
        var repo = Billing();
        EditOnDisk(repo, AuditedPath, "stereotype.json", s =>
        {
            var updated = new JsonObject { ["id"] = UpdatedAt, ["name"] = "updated_at", ["type"] = "datetimeoffset" };
            if (updatedPrecision is { } precision)
                updated["precision"] = precision;
            s["columns"] = new JsonArray(
                new JsonObject
                {
                    ["id"] = CreatedAt, ["name"] = "created_at", ["type"] = "datetimeoffset", ["nullable"] = false,
                    ["defaultSql"] = new JsonObject { ["*"] = "CURRENT_TIMESTAMP" },
                },
                updated);
            s["indexes"] = new JsonArray(new JsonObject
            {
                ["id"] = CreatedIndex, ["columns"] = new JsonArray(new JsonObject { ["column"] = CreatedAt }),
            });
        });
        EditOnDisk(repo, DatabasePath, "database.json", d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("audited"));
        EditOnDisk(repo, NotesPath, "table.json", t => t["excludes"] = new JsonArray(CreatedAt, CreatedIndex));
        return repo;
    }

    /// <summary>Rewrites a model file on disk, canonically, before the store reads it.</summary>
    private static void EditOnDisk(E2ERepo repo, string path, string schema, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllBytes(repo.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllBytes(repo.Repo.PathOf(path), TestServices.Json.Write(node, schema, path));
    }

    /// <summary>Writes an overlay of an entity's table in database main, holding the given keys besides its identity.</summary>
    private static string Overlay(E2ERepo repo, string id, string entity, Action<JsonObject> keys)
    {
        var path = TablesFolder + id.ToLowerInvariant() + ".json";
        var node = new JsonObject
        {
            ["$schema"] = "../../../../.schema/v1/table.json",
            ["kind"] = "table",
            ["id"] = id,
            ["database"] = Main,
            ["origin"] = "synthesized",
            ["entity"] = entity,
        };
        keys(node);
        File.WriteAllBytes(repo.Repo.PathOf(path), TestServices.Json.Write(node, "table.json", path));
        return path;
    }

    // ---- reading ----

    private static async Task<RDatabase> MainAsync(E2ERepo repo)
    {
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        var model = await repo.Store.Services.Resolver.ResolveAsync(snapshot, null, Ct);
        return model.Databases.Single(d => d.Id == Main);
    }

    private static RTable Table(RDatabase database, string name) => database.Tables.Single(t => t.Name == name);

    private static RColumn? Column(RTable table, string name) => table.Columns.SingleOrDefault(c => c.Name == name);

    /// <summary>The model files under the main database's tables folder, path to bytes.</summary>
    private static SortedDictionary<string, string> TableFiles(E2ERepo repo)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in repo.Repo.ListFiles().Where(p => p.StartsWith(TablesFolder, StringComparison.Ordinal)))
            files[path] = repo.Repo.ReadFile(path);
        return files;
    }

    private static async Task<IReadOnlyList<Diagnostic>> ValidateCleanAsync(E2ERepo repo)
    {
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.True(report.Errors == 0, string.Join("\n", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => $"{d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}")));
        return report.Diagnostics;
    }

    private static async Task GenerateAsync(E2ERepo repo) =>
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Apply, packs: ["sql-ddl"]));

    /// <summary>Generates with sql-ddl and returns the text of the one migration of main the run adds.</summary>
    private static async Task<string> NextMigrationAsync(E2ERepo repo)
    {
        var result = await repo.RunAsync(GenerationMode.Apply, packs: ["sql-ddl"]);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, result);
        var migration = Assert.Single(result.Changes, c => c.Kind == FileChangeKind.Added && c.Path.StartsWith("db/main/migrations/", StringComparison.Ordinal));
        return repo.Repo.ReadFile(migration.Path);
    }

    private static async Task<BatchResult> ApplyBatchAsync(E2ERepo repo, string operations)
    {
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":" + operations + "}"));
        Assert.Empty(parsed.Diagnostics);
        var result = await repo.Store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        return result;
    }

    // ---- the acceptance lines ----

    [Fact]
    public async Task Audited_columns_reach_every_table_of_schema_billing_from_one_line()
    {
        await using var repo = At1Copy();

        var main = await MainAsync(repo);

        Assert.Equal(BillingTables, main.Tables.Where(t => t.Schema == "billing").Select(t => t.Name).Order(StringComparer.Ordinal));
        foreach (var name in BillingTables)
        {
            var table = Table(main, name);
            Assert.NotNull(Column(table, "created_at"));
            Assert.NotNull(Column(table, "updated_at"));
            Assert.Contains(table.Stereotypes, s => s.Key == "audited");
        }

        // customers, invoices and payments store audited's attributes and inherit no member.
        foreach (var name in Stored)
        {
            var table = Table(main, name);
            Assert.All(table.Columns, c => Assert.Equal("own", c.MemberOrigin.Source));
            Assert.All(table.Indexes, i => Assert.Equal("own", i.MemberOrigin.Source));
            Assert.NotNull(Column(table, "created_at")!.Attribute);
        }

        // products, invoice_lines and payment_invoice inherit both columns and the index, keyed by their table key.
        foreach (var name in Inheriting)
        {
            var table = Table(main, name);
            foreach (var (column, member) in new[] { ("created_at", CreatedAt), ("updated_at", UpdatedAt) })
            {
                var inherited = Column(table, column)!;
                Assert.Equal((Keyed(table.Key, member), "schema-default", member), (inherited.Key, inherited.MemberOrigin.Source, inherited.MemberOrigin.MemberId));
            }

            var index = Assert.Single(table.Indexes, i => i.MemberOrigin.Source != "own");
            Assert.Equal((Keyed(table.Key, CreatedIndex), "schema-default"), (index.Id, index.MemberOrigin.Source));
        }

        Assert.Equal(Synthesized(Product), Table(main, "products").Key);
        Assert.Equal(Synthesized(InvoiceLine), Table(main, "invoice_lines").Key);
        Assert.Equal(Synthesized(Settles), Table(main, "payment_invoice").Key);

        // notes keeps its own created_at and inherits updated_at, but not the index on the member it excludes.
        var notes = Table(main, "notes");
        Assert.Equal((NotesCreatedAt, "own"), (Column(notes, "created_at")!.Key, Column(notes, "created_at")!.MemberOrigin.Source));
        Assert.Equal((Keyed(Notes, UpdatedAt), "schema-default"), (Column(notes, "updated_at")!.Key, Column(notes, "updated_at")!.MemberOrigin.Source));
        Assert.All(notes.Indexes, i => Assert.Equal("own", i.MemberOrigin.Source));

        // The view and the sequence of the schema get nothing: audited does not apply to them.
        Assert.Empty(main.Views.Single(v => v.Name == "outstanding_invoices").Stereotypes);
        Assert.Empty(Assert.Single(main.Sequences).Stereotypes);
        var diagnostics = await ValidateCleanAsync(repo);
        Assert.DoesNotContain(diagnostics, d => d.Rule is "MQ2004" or "MQ4079");
    }

    [Fact]
    public async Task Invoice_line_opts_out_in_one_line()
    {
        await using var repo = At1Copy();
        Overlay(repo, OptOut, InvoiceLine, t => t["excludeStereotypes"] = new JsonArray("audited"));

        var main = await MainAsync(repo);

        var lines = Table(main, "invoice_lines");
        Assert.Equal(Synthesized(InvoiceLine), lines.Key);
        Assert.Null(Column(lines, "created_at"));
        Assert.Null(Column(lines, "updated_at"));
        Assert.DoesNotContain(lines.Indexes, i => i.MemberOrigin.Source != "own");
        Assert.DoesNotContain(lines.Stereotypes, s => s.Key == "audited");
        foreach (var name in BillingTables.Where(n => n != "invoice_lines"))
        {
            Assert.NotNull(Column(Table(main, name), "created_at"));
            Assert.NotNull(Column(Table(main, name), "updated_at"));
        }

        Assert.Equal(Keyed(Synthesized(Product), CreatedIndex), Assert.Single(Table(main, "products").Indexes, i => i.MemberOrigin.Source != "own").Id);
        var diagnostics = await ValidateCleanAsync(repo);
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ2030");
    }

    [Fact]
    public async Task A_nullable_exception_states_only_nullable_and_keeps_the_key()
    {
        await using var repo = At1Copy();
        var before = Column(Table(await MainAsync(repo), "products"), "created_at")!;
        var key = before.Key;
        Assert.Equal(("01J92P0V0JR8BE8253SKT29ZG7@01J92P0V1QRN2181XM2ZWE02W4.01JC7AVD1TC0000000000000C1", false), (key, before.Nullable));
        Overlay(repo, ProductOverlay, Product, t => t["redefines"] = new JsonArray(new JsonObject { ["member"] = CreatedAt, ["nullable"] = true }));
        await repo.Store.RescanAsync(false, Ct);

        var created = Column(Table(await MainAsync(repo), "products"), "created_at")!;

        Assert.Equal((key, true, "schema-default"), (created.Key, created.Nullable, created.MemberOrigin.Source));
        Assert.Equal(["nullable"], created.MemberOrigin.Redefined);
        Assert.Equal(("timestamptz(6)", "CURRENT_TIMESTAMP"), (created.NativeType, created.DefaultSql));
        await ValidateCleanAsync(repo);

        // The table follows the stereotype for every facet it does not redefine.
        await repo.EditAsync(Audited, s => s["columns"]![0]!["precision"] = 3);
        created = Column(Table(await MainAsync(repo), "products"), "created_at")!;
        Assert.Equal((key, true, "timestamptz(3)"), (created.Key, created.Nullable, created.NativeType));

        // Removing the entry changes no key and no seed: the write touches the overlay and its migration facts only.
        var saved = await repo.EditAsync(ProductOverlay, t => t.Remove("redefines"));
        var facts = (saved.Facts ?? []).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal([ProductOverlay], saved.Changes!.Changed.Select(c => c.Id).Where(id => !facts.Contains(id!)));
        created = Column(Table(await MainAsync(repo), "products"), "created_at")!;
        Assert.Equal((key, false), (created.Key, created.Nullable));
        Assert.Empty(created.MemberOrigin.Redefined);
    }

    [Fact]
    public async Task A_stereotype_column_type_change_changes_every_table_in_one_plan()
    {
        await using var repo = At1Copy(updatedPrecision: 3);
        await GenerateAsync(repo);
        var tables = TableFiles(repo);

        var saved = await repo.EditAsync(Audited, s => s["columns"]![1]!["precision"] = 6);

        // A widening records no migration fact, and no table file changes.
        Assert.Empty(saved.Facts ?? []);
        Assert.Equal([Audited], saved.Changes!.Changed.Select(c => c.Id));
        Assert.Equal(tables, TableFiles(repo));

        // One plan, one migration for main: updated_at widens in every table that inherits it, in one schema diff.
        var plan = (await repo.Service.PlanAsync(new GenerationRequest { Packs = ["sql-ddl"] }, null, Ct)).Plan!;
        var migration = Assert.Single(plan.Changes, c => c.Kind == FileChangeKind.Added && c.Path.Contains("/migrations/", StringComparison.Ordinal));
        Assert.StartsWith("db/main/migrations/", migration.Path, StringComparison.Ordinal);
        await repo.Service.ApplyAsync(plan.Id, null, Ct);
        var text = repo.Repo.ReadFile(migration.Path);
        foreach (var table in new[] { "products", "invoice_lines", "payment_invoice", "notes" })
            Assert.Contains($"ALTER TABLE billing.{table} ALTER COLUMN updated_at TYPE timestamptz(6);", text, StringComparison.Ordinal);
        foreach (var table in Stored)
            Assert.DoesNotContain($"TABLE billing.{table} ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("-- TODO", text, StringComparison.Ordinal);
        Assert.Equal(tables, TableFiles(repo));
    }

    [Fact]
    public async Task Renaming_the_stereotype_column_changes_no_table_file()
    {
        await using var repo = At1Copy();
        await GenerateAsync(repo);
        var tables = TableFiles(repo);

        var saved = await repo.EditAsync(Audited, s => s["columns"]![1]!["name"] = "modified_at");

        // The write touches audited and four new fact files: one complete rename per carrying table, keyed by its inherited copy.
        var facts = saved.Facts!;
        Assert.Equal(4, facts.Count);
        Assert.Equal(facts.Select(f => f.Id).Append(Audited).Order(StringComparer.Ordinal), saved.Changes!.Changed.Select(c => c.Id).Order(StringComparer.Ordinal));
        Assert.All(saved.Changes.Changed.Where(c => c.Id != Audited), c => Assert.StartsWith(".maquettiste/model/databases/main/migrations/open/", c.Path, StringComparison.Ordinal));
        var keys = new List<string>();
        foreach (var fact in facts)
        {
            Assert.Equal(("rename", "complete"), (fact.Fact, fact.State));
            var recorded = Assert.IsType<Migration>((await repo.Store.GetElementAsync(fact.Id, Ct))!.Element);
            Assert.Equal("column", recorded.Object!.Kind);
            Assert.Equal(Keyed(recorded.Object.Table!, UpdatedAt), recorded.Object.Key);
            Assert.Equal(("updated_at", "modified_at"), (recorded.From!.Name, recorded.To!.Name));
            keys.Add(recorded.Object.Key!);
        }

        Assert.Equal(
            new[] { Keyed(Notes, UpdatedAt), Keyed(Synthesized(InvoiceLine), UpdatedAt), Keyed(Synthesized(Product), UpdatedAt), Keyed(Synthesized(Settles), UpdatedAt) }
                .Order(StringComparer.Ordinal),
            keys.Order(StringComparer.Ordinal));
        Assert.Equal(tables, TableFiles(repo));

        // The next migration renames the column in those four tables.
        var text = await NextMigrationAsync(repo);
        foreach (var table in new[] { "products", "invoice_lines", "payment_invoice", "notes" })
            Assert.Contains($"ALTER TABLE billing.{table} RENAME COLUMN updated_at TO modified_at;", text, StringComparison.Ordinal);
        foreach (var table in Stored)
            Assert.DoesNotContain($"TABLE billing.{table} ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Folding_the_audit_columns_of_billing_plans_no_database_step()
    {
        // audited keeps its attributes and gains the two columns only, shaped as its attributes store them: no index, no default.
        await using var repo = Billing();
        EditOnDisk(repo, AuditedPath, "stereotype.json", s => s["columns"] = new JsonArray(
            new JsonObject { ["id"] = CreatedAt, ["name"] = "created_at", ["type"] = "datetimeoffset", ["nullable"] = false },
            new JsonObject { ["id"] = UpdatedAt, ["name"] = "updated_at", ["type"] = "datetimeoffset" }));
        await ApplyBatchAsync(repo, $$"""
            [ { "op": "materialize-tables", "database": "{{Main}}", "entities": ["{{Customer}}", "{{Invoice}}", "{{InvoiceLine}}", "{{Payment}}", "{{Product}}"] } ]
            """);
        await GenerateAsync(repo);
        var snapshot = await repo.Store.GetSnapshotAsync(Ct);
        Table File(ModelSnapshot model, string name) => model.All<Table>().Single(t => t.Name == name && t.Database == Main);
        // invoices' overlay listed audited, which its entity's attributes realize: the designed table excludes both members.
        Assert.Equal(["audited"], File(snapshot, "invoices").Stereotypes);
        Assert.Equal([CreatedAt, UpdatedAt], File(snapshot, "invoices").Excludes);
        var untouched = new[] { "products", "invoice_lines" }.ToDictionary(n => n, n => snapshot.GetDocument(File(snapshot, n).Id)!.Hash);
        var owned = Stored.SelectMany(n => File(snapshot, n).Columns.Where(c => c.Name is "created_at" or "updated_at")
            .Select(c => (From: c.Id, To: Keyed(File(snapshot, n).Id, c.Name == "created_at" ? CreatedAt : UpdatedAt)))).ToList();
        Assert.Equal(6, owned.Count);

        var result = await ApplyBatchAsync(repo, $$"""
            [ { "op": "fold-into-stereotype", "id": "{{Audited}}", "schema": "{{BillingSchema}}", "redefine": true, "excludeMissing": true } ]
            """);

        snapshot = await repo.Store.GetSnapshotAsync(Ct);
        foreach (var name in Stored)
        {
            var table = File(snapshot, name);
            Assert.DoesNotContain(table.Columns, c => c.Name is "created_at" or "updated_at");
            Assert.Contains("audited", table.Stereotypes);
            Assert.Empty(table.Excludes);
        }

        // Each entity's binding names the inherited copies.
        foreach (var (entity, name) in new[] { (Customer, "customers"), (Invoice, "invoices"), (Payment, "payments") })
        {
            var fields = snapshot.Get<Entity>(entity)!.Bindings.Single().Fields;
            var table = File(snapshot, name).Id;
            Assert.Contains(fields, f => f.Column == Keyed(table, CreatedAt));
            Assert.Contains(fields, f => f.Column == Keyed(table, UpdatedAt));
        }

        // notes folds created_at, its default kept by a redefinition, and excludes updated_at.
        var notes = snapshot.Get<Table>(Notes)!;
        Assert.DoesNotContain(notes.Columns, c => c.Name == "created_at");
        var redefinition = Assert.Single(notes.Redefines);
        Assert.Equal((CreatedAt, "CURRENT_TIMESTAMP"), (redefinition.Member, redefinition.DefaultSql!["*"]));
        Assert.Equal(["defaultSql"], redefinition.Facets);
        Assert.Equal([UpdatedAt], notes.Excludes);
        // products and invoice_lines match nothing and are skipped.
        Assert.All(untouched, u => Assert.Equal(u.Value, snapshot.GetDocument(File(snapshot, u.Key).Id)!.Hash));

        // One rekey for main, with the seven column moves.
        var recorded = Assert.Single(result.Facts!);
        Assert.Equal(("rekey", "complete", Main), (recorded.Fact, recorded.State, recorded.Database));
        var rekey = snapshot.Get<Migration>(recorded.Id)!;
        Assert.All(rekey.Keys, k => Assert.Equal("column", k.Kind));
        Assert.Equal(owned.Append((From: NotesCreatedAt, To: Keyed(Notes, CreatedAt))).Order(), rekey.Keys.Select(k => (k.From, k.To)).Order());
        await ValidateCleanAsync(repo);

        // The next migration holds no statement on a table: only the rekey's row in the ledger.
        var text = await NextMigrationAsync(repo);
        Assert.DoesNotContain("billing.", text, StringComparison.Ordinal);
        Assert.Contains("applied_fact (fact_id, content_hash, applied_by) VALUES ('" + recorded.Id + "'", text, StringComparison.Ordinal);
    }
}
