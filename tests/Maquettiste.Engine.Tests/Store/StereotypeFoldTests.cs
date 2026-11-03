using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Editor;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// The batch operation <c>fold-into-stereotype</c> (next-phase-2-design.md section 3.1, "Fold into a stereotype") over the billing
/// fixture: the designed table notes adopts the members of <c>audited</c> that match its own columns, keys and indexes; what matched
/// leaves the file, every reference names the inherited member's keyed id, the write records one <c>rekey</c> and nothing else, and
/// the next migration changes no table. A difference the fold cannot hide from the database refuses the table (MQ4077) and nothing is
/// written.
/// </summary>
public sealed class StereotypeFoldTests
{
    private const string Audited = "01J92P0V25BX5TMQ8N9F6AJE61";
    private const string AuditedPath = ".maquettiste/model/vocabularies/stereotypes/audited.json";
    private const string NotesPath = ".maquettiste/model/databases/main/tables/notes.json";
    private const string Notes = "01K6BND0000000000000000001";
    private const string NotesBody = "01K6BND0000000000000000005";
    private const string NotesCreatedAt = "01K6BND0000000000000000006";
    private const string NotesIndex = "01K6BND0000000000000000007";
    private const string OwnIndex = "01K6BND0000000000000000008";
    private const string InvoiceNote = "01K6BND0000000000000000010";
    private const string CustomerNote = "01K6BND0000000000000000020";
    private const string BillingSchema = "01J92P0V1RC04SKQ5353EAKHG2";
    private const string CreatedAt = "01JC7AVD1TC0000000000000C1";
    private const string UpdatedAt = "01JC7AVD1TC0000000000000C2";
    private const string CreatedIndex = "01JC7AVD1TC0000000000000X1";

    private const string CreatedAtColumn =
        $$"""{"id": "{{CreatedAt}}", "name": "created_at", "type": "datetimeoffset", "nullable": false, "defaultSql": {"*": "CURRENT_TIMESTAMP"} }""";
    private const string UpdatedAtColumn = $$"""{"id": "{{UpdatedAt}}", "name": "updated_at", "type": "datetimeoffset"}""";
    private const string CreatedAtIndex = $$"""{"id": "{{CreatedIndex}}", "columns": [{"column": "{{CreatedAt}}"}]}""";

    private static CancellationToken Ct => EditorRepo.Ct;

    private static string Keyed(string member) => Notes + "." + member;

    /// <summary>Rewrites a model file on disk before the store reads it.</summary>
    private static void EditOnDisk(EditorRepo r, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(r.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllText(r.Repo.PathOf(path), node.ToJsonString(), new UTF8Encoding(false));
    }

    /// <summary>audited gains the given columns and indexes (its attributes stay).</summary>
    private static void Members(EditorRepo r, string columns, string indexes = "")
    {
        EditOnDisk(r, AuditedPath, s =>
        {
            s["columns"] = JsonNode.Parse("[" + columns + "]");
            if (indexes.Length > 0)
                s["indexes"] = JsonNode.Parse("[" + indexes + "]");
        });
    }

    /// <summary>notes gains an own index on created_at.</summary>
    private static void OwnCreatedAtIndex(EditorRepo r) =>
        EditOnDisk(r, NotesPath, t => t["indexes"]!.AsArray().Add(JsonNode.Parse($$"""{"id": "{{OwnIndex}}", "columns": [{"column": "{{NotesCreatedAt}}"}]}""")));

    private static string Fold(string target = $$"""
        "tables": ["{{Notes}}"]
        """, bool redefine = false, bool excludeMissing = false) =>
        $$"""{"op": "fold-into-stereotype", "id": "{{Audited}}", {{target}}{{(redefine ? ", \"redefine\": true" : "")}}{{(excludeMissing ? ", \"excludeMissing\": true" : "")}}}""";

    private static ModelBatch Batch(EditorRepo r, params string[] operations)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + string.Join(",", operations) + "]}"));
        Assert.Empty(parsed.Diagnostics);
        return parsed.Batch!;
    }

    private static Task<BatchResult> ApplyAsync(EditorRepo r, params string[] operations) =>
        r.Store.ApplyBatchAsync(Batch(r, operations), ChangeSource.Editor, Ct);

    private static void AssertSaved(BatchResult result) =>
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));

    /// <summary>The batch was refused with MQ4077 findings only; returns their messages.</summary>
    private static List<string> Refusals(BatchResult result)
    {
        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var findings = result.Items.SelectMany(i => i.Diagnostics).ToList();
        Assert.NotEmpty(findings);
        Assert.All(findings, d => Assert.Equal("MQ4077", d.Rule));
        return [.. findings.Select(d => d.Message)];
    }

    private static async Task CreateAsync(EditorRepo r, string json)
    {
        var result = await r.Store.CreateAsync(Encoding.UTF8.GetBytes(JsonNode.Parse(json)!.ToJsonString()), ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)));
    }

    private static async Task AssertNoErrorsAsync(EditorRepo r)
    {
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.True(report.Errors == 0, string.Join("; ", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Rule + " " + d.Message)));
    }

    [Fact]
    public async Task Fold_removes_own_columns_and_rewrites_references_to_keyed_ids()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn + "," + UpdatedAtColumn);
        // A query and a seed that name notes' created_at by its id.
        await CreateAsync(r, $$"""
            {"kind": "query", "id": "01K6QRY00000000000000000N1", "name": "RecentNotes", "database": "{{EditorRepo.MainDatabaseId}}",
             "from": {"source": "{{Notes}}", "alias": "n"},
             "select": [{"name": "body", "expression": {"column": "n.{{NotesBody}}"} }, {"name": "createdAt", "expression": {"column": "n.{{NotesCreatedAt}}"} }]}
            """);
        await CreateAsync(r, $$"""
            {"kind": "seed", "id": "01K6SED0000000000000000001", "name": "welcomeNotes", "target": "{{Notes}}",
             "columns": ["01K6BND0000000000000000002", "01K6BND0000000000000000003", "01K6BND0000000000000000004", "{{NotesBody}}", "{{NotesCreatedAt}}"],
             "rows": [{"id": "01K6SED0000000000000000002", "values": ["0190a000-0000-7000-8000-000000000001", "invoice", "0190a000-0000-7000-8000-000000000002", "Welcome", "2024-01-01T00:00:00+00:00"]}]}
            """);
        await AssertNoErrorsAsync(r);

        var result = await ApplyAsync(r, Fold(excludeMissing: true));

        AssertSaved(result);
        var snapshot = r.Store.Current!;
        var notes = snapshot.Get<Table>(Notes)!;
        Assert.Equal(["id", "entity_type", "entity_id", "body"], notes.Columns.Select(c => c.Name));
        Assert.Equal(["audited"], notes.Stereotypes);
        Assert.Equal([UpdatedAt], notes.Excludes);
        Assert.Equal([NotesIndex], notes.Indexes.Select(i => i.Id));
        foreach (var entity in new[] { InvoiceNote, CustomerNote })
            Assert.Contains(snapshot.Get<Entity>(entity)!.Bindings.Single().Fields, f => f.Column == Keyed(CreatedAt));
        Assert.Equal(Keyed(CreatedAt), snapshot.Get<Seed>("01K6SED0000000000000000001")!.Columns[4]);
        var query = snapshot.Get<Query>("01K6QRY00000000000000000N1")!;
        Assert.Equal(["n." + NotesBody, "n." + Keyed(CreatedAt)], query.Select.Select(f => f.Expression.Column));
        Assert.Empty(snapshot.ReferencesTo(NotesCreatedAt));
        await AssertNoErrorsAsync(r);

        // The inherited column keeps the shape the own one had.
        var table = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "notes");
        var created = table.Columns.Single(c => c.Name == "created_at");
        Assert.Equal((Keyed(CreatedAt), false, "stereotype"), (created.Key, created.Nullable, created.MemberOrigin!.Source));
        Assert.DoesNotContain(table.Columns, c => c.Name == "updated_at");
    }

    [Fact]
    public async Task Fold_records_one_rekey_per_database_with_constraint_moves()
    {
        await using var r = EditorRepo.Create();
        Members(r, CreatedAtColumn, CreatedAtIndex);
        OwnCreatedAtIndex(r);
        var first = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, first.Outcome);

        var result = await ApplyAsync(r, Fold());

        AssertSaved(result);
        var recorded = Assert.Single(result.Facts!);
        Assert.Equal(("rekey", "complete"), (recorded.Fact, recorded.State));
        var snapshot = r.Store.Current!;
        var rekey = snapshot.Get<Migration>(recorded.Id)!;
        Assert.Equal(EditorRepo.MainDatabaseId, rekey.Database);
        Assert.Equal(
            [("column", Notes, NotesCreatedAt, Keyed(CreatedAt), "created_at"), ("index", Notes, OwnIndex, Keyed(CreatedIndex), "ix_notes_created_at")],
            rekey.Keys.Select(k => (k.Kind, k.Table!, k.From, k.To, k.Name!)));
        Assert.Equal([NotesIndex], snapshot.Get<Table>(Notes)!.Indexes.Select(i => i.Id));
        await AssertNoErrorsAsync(r);

        // The next migration holds the rekey's fact row and no statement on a table.
        var second = await r.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Packs = ["sql-ddl"] }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, second.Outcome);
        var migration = Assert.Single(second.Changes, c => c.Kind == FileChangeKind.Added && c.Path.Contains("/migrations/", StringComparison.Ordinal)
            && !c.Path.StartsWith(".maquettiste/", StringComparison.Ordinal));
        Assert.DoesNotContain("billing.", r.Repo.ReadFile(migration.Path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Fold_refuses_a_type_difference_with_MQ4077()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, $$"""{"id": "{{CreatedAt}}", "name": "created_at", "type": "date", "nullable": false, "defaultSql": {"*": "CURRENT_TIMESTAMP"} }""");
        var files = r.Files();

        var messages = Refusals(await ApplyAsync(r, Fold(redefine: true)));

        var message = Assert.Single(messages);
        Assert.Contains("Table 'notes'", message, StringComparison.Ordinal);
        Assert.Contains("column 'created_at'", message, StringComparison.Ordinal);
        Assert.Contains("type", message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Fold_refuses_a_stable_id_difference_with_MQ4077()
    {
        // The stereotype's id takes a stable id and the own one does not: the capture holds no stableId, yet folding would change the
        // id values of the rows notes receives, and no redefinition changes it (next-phase-2-design.md section 3.4, decision 22).
        await using var r = EditorRepo.Create(packs: false);
        Members(r, """{"id": "01JC7AVD1TC0000000000000C3", "name": "id", "type": "uuid", "nullable": false, "stableId": true}""");
        var files = r.Files();

        var messages = Refusals(await ApplyAsync(r, Fold(redefine: true, excludeMissing: true)));

        var message = Assert.Single(messages);
        Assert.Contains("Table 'notes'", message, StringComparison.Ordinal);
        Assert.Contains("column 'id' differs from the stereotype's in stable id (false here, true on the stereotype)", message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Fold_writes_redefinitions_with_redefine()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, $$"""{"id": "{{CreatedAt}}", "name": "created_at", "type": "datetimeoffset"}""");
        var files = r.Files();

        // Without redefine the differences refuse the table.
        var refused = Assert.Single(Refusals(await ApplyAsync(r, Fold())));
        Assert.Contains("redefine", refused, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());

        var result = await ApplyAsync(r, Fold(redefine: true));

        AssertSaved(result);
        var notes = r.Store.Current!.Get<Table>(Notes)!;
        var redefinition = Assert.Single(notes.Redefines);
        Assert.Equal((CreatedAt, (bool?)false), (redefinition.Member, redefinition.Nullable));
        Assert.Equal("CURRENT_TIMESTAMP", redefinition.DefaultSql!["*"]);
        Assert.Equal(["defaultSql", "nullable"], redefinition.Facets);
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.Equal(0, report.Errors);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ4078");
        var created = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "notes").Columns.Single(c => c.Name == "created_at");
        Assert.Equal((Keyed(CreatedAt), false), (created.Key, created.Nullable));
        Assert.Equal(["defaultSql", "nullable"], created.MemberOrigin!.Redefined);
    }

    [Fact]
    public async Task Fold_excludes_missing_members_with_exclude_missing()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn + "," + UpdatedAtColumn, CreatedAtIndex);

        var refused = Assert.Single(Refusals(await ApplyAsync(r, Fold())));
        Assert.Contains("column 'updated_at'", refused, StringComparison.Ordinal);
        Assert.Contains("index on (created_at)", refused, StringComparison.Ordinal);
        Assert.Contains("excludeMissing", refused, StringComparison.Ordinal);

        var result = await ApplyAsync(r, Fold(excludeMissing: true));

        AssertSaved(result);
        var notes = r.Store.Current!.Get<Table>(Notes)!;
        Assert.Equal([UpdatedAt, CreatedIndex], notes.Excludes);
        Assert.DoesNotContain(notes.Columns, c => c.Name == "created_at");
        await AssertNoErrorsAsync(r);
    }

    [Fact]
    public async Task Fold_adopts_an_excluded_member_that_matches()
    {
        // Schema billing gives audited to every table; notes kept its own created_at by excluding the member and the index on it.
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn + "," + UpdatedAtColumn, CreatedAtIndex);
        EditOnDisk(r, ".maquettiste/model/databases/main/database.json", d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("audited"));
        EditOnDisk(r, NotesPath, t => t["excludes"] = new JsonArray(CreatedAt, CreatedIndex));
        await AssertNoErrorsAsync(r);

        var result = await ApplyAsync(r, Fold());

        // created_at matches, so its member leaves excludes; the index matches nothing and stays excluded; audited stays a default.
        AssertSaved(result);
        var notes = r.Store.Current!.Get<Table>(Notes)!;
        Assert.Equal([CreatedIndex], notes.Excludes);
        Assert.Empty(notes.Stereotypes);
        Assert.DoesNotContain(notes.Columns, c => c.Name == "created_at");
        await AssertNoErrorsAsync(r);
        var table = (await r.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, Ct)).View!.Tables.Single(t => t.Name == "notes");
        Assert.Equal("schema-default", table.Columns.Single(c => c.Name == "created_at").MemberOrigin!.Source);
    }

    [Fact]
    public async Task Fold_skips_tables_that_match_nothing()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn + "," + UpdatedAtColumn);
        const string Vendors = "01K6VND0000000000000000001";
        await CreateAsync(r, $$"""
            {"kind": "table", "id": "{{Vendors}}", "name": "vendors", "database": "{{EditorRepo.MainDatabaseId}}", "schema": "{{BillingSchema}}",
             "columns": [{"id": "01K6VND0000000000000000002", "name": "id", "type": "uuid", "nullable": false}],
             "primaryKey": {"columns": ["01K6VND0000000000000000002"]} }
            """);
        var vendorsHash = r.Store.Current!.GetDocument(Vendors)!.Hash;

        var result = await ApplyAsync(r, Fold($$"""
            "schema": "{{BillingSchema}}"
            """, excludeMissing: true));

        AssertSaved(result);
        var snapshot = r.Store.Current!;
        Assert.Equal(vendorsHash, snapshot.GetDocument(Vendors)!.Hash);
        Assert.Empty(snapshot.Get<Table>(Vendors)!.Stereotypes);
        Assert.Equal(["audited"], snapshot.Get<Table>(Notes)!.Stereotypes);
        await AssertNoErrorsAsync(r);
    }

    [Fact]
    public async Task Fold_refuses_a_synthesized_overlay()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn);
        var files = r.Files();

        var message = Assert.Single(Refusals(await ApplyAsync(r, Fold($$"""
            "tables": ["{{EditorRepo.OverlayTableId}}"]
            """))));

        Assert.Contains("materialize it first", message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Fold_refuses_when_another_operation_writes_a_target()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn);
        var document = (await r.Store.GetElementAsync(Notes, Ct))!;
        var notes = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        notes["description"] = "Notes on invoices and customers.";
        var files = r.Files();

        var result = await ApplyAsync(r,
            $$"""{"op": "update", "id": "{{Notes}}", "expectedHash": "{{document.Hash}}", "element": {{notes.ToJsonString()}}}""",
            Fold());

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        var refusal = Assert.Single(result.Items[1].Diagnostics);
        Assert.Equal("MQ4077", refusal.Rule);
        Assert.Contains("another operation of this batch (/operations/0)", refusal.Message, StringComparison.Ordinal);
        Assert.Equal("/operations/1", refusal.JsonPointer);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Fold_refuses_when_another_operation_writes_a_document_it_rewrites()
    {
        // invoiceNote's binding names notes' created_at: the fold rewrites it, so an update of it in the same batch refuses the fold at
        // the fold, naming the clash, and not with the planner's MQ1002 about two operations.
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn);
        var document = (await r.Store.GetElementAsync(InvoiceNote, Ct))!;
        var entity = JsonNode.Parse(document.Json.GetRawText())!.AsObject();
        entity["description"] = "A note on an invoice.";
        var files = r.Files();

        var result = await ApplyAsync(r,
            $$"""{"op": "update", "id": "{{InvoiceNote}}", "expectedHash": "{{document.Hash}}", "element": {{entity.ToJsonString()}}}""",
            Fold());

        Assert.Equal(SaveOutcome.Invalid, result.Outcome);
        Assert.Empty(result.Items[0].Diagnostics);
        var refusal = Assert.Single(result.Items[1].Diagnostics);
        Assert.Equal(("MQ4077", "/operations/1"), (refusal.Rule, refusal.JsonPointer));
        Assert.Contains("The fold rewrites entity", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("also written by another operation of this batch (/operations/0)", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Fold_refuses_a_primary_key_whose_columns_differ_with_MQ4077()
    {
        // audited keys on row_key, which notes lacks: excludeMissing excludes it, the own key matches the stereotype's, and the
        // inherited key then names an excluded column, so the folded key is not the own one.
        await using var r = EditorRepo.Create(packs: false);
        Members(r, """{"id": "01JC7AVD1TC0000000000000C9", "name": "row_key", "type": "uuid", "nullable": false}""");
        EditOnDisk(r, AuditedPath, s => s["primaryKey"] = JsonNode.Parse("""{"id": "01JC7AVD1TC0000000000000K9", "columns": ["01JC7AVD1TC0000000000000C9"]}"""));
        var files = r.Files();

        var refused = Assert.Single(Refusals(await ApplyAsync(r, Fold(excludeMissing: true))));

        Assert.Equal("Table 'notes': the primary key does not resolve on both sides.", refused);
        Assert.Equal(files, r.Files());
    }

    [Fact]
    public async Task Fold_refuses_with_MQ4077_when_its_candidate_cannot_be_verified()
    {
        // A fold whose candidate does not resolve cannot be compared with the database as it is: it adds no change it has not verified.
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn);
        var services = EngineServices.Create(r.Repo.Options);
        await using var store = new ModelStore(r.Repo.Options, services with { Resolver = new CandidateFailingResolver(services.Resolver) });
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + Fold() + "]}"));
        Assert.Empty(parsed.Diagnostics);
        var files = r.Files();

        var refused = Assert.Single(Refusals(await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct)));

        Assert.Equal(
            "The fold cannot be held to changing nothing in the database: the databases of the target tables do not resolve once folded. Nothing is written.",
            refused);
        Assert.Equal(files, r.Files());
        // The same fold applies with a resolver that takes the candidate.
        AssertSaved(await ApplyAsync(r, Fold()));
    }

    /// <summary>A resolver that cannot take a model where notes carries audited: the fold's candidate.</summary>
    private sealed class CandidateFailingResolver(IModelResolver inner) : IModelResolver, IDatabaseResolver
    {
        public Task<ResolvedModel> ResolveAsync(ModelSnapshot model, IProgress<ProgressUpdate>? progress, CancellationToken ct)
        {
            Check(model);
            return inner.ResolveAsync(model, progress, ct);
        }

        public Task<(IReadOnlyList<RDatabase?> Databases, IReadOnlyList<Diagnostic> Diagnostics)> ResolveDatabasesAsync(ModelSnapshot model,
            IReadOnlyList<string> databaseIds, bool freeze, CancellationToken ct)
        {
            Check(model);
            return ((IDatabaseResolver)inner).ResolveDatabasesAsync(model, databaseIds, freeze, ct);
        }

        private static void Check(ModelSnapshot model)
        {
            if (model.Get<Table>(Notes)?.Stereotypes.Contains("audited", StringComparer.Ordinal) == true)
                throw new InvalidOperationException("The candidate does not resolve.");
        }
    }

    [Fact]
    public async Task Fold_dry_run_writes_nothing_and_lists_the_rekey()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn, CreatedAtIndex);
        OwnCreatedAtIndex(r);
        // A second database: a dry run lists the facts of a change that reaches some databases, not all of them.
        await CreateAsync(r, """{"kind": "database", "id": "01K6VND0000000000000000009", "name": "archive", "dialect": "postgresql", "byConvention": "none"}""");
        var files = r.Files();

        var preview = await r.Store.PreviewBatchAsync(Batch(r, Fold()), Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        Assert.Equal(files, r.Files());
        Assert.Contains(preview.Files, f => f.Path == NotesPath);
        Assert.Contains(preview.Files, f => f.Path.EndsWith("/entities/invoice-note.json", StringComparison.Ordinal));
        var fact = Assert.Single(preview.Facts!);
        Assert.Equal("rekey", fact.Fact);
        Assert.Equal("rekey 1 column, 1 index", fact.Summary);
    }

    [Fact]
    public async Task Fold_is_byte_identical_with_the_sequential_id_generator()
    {
        async Task<SortedDictionary<string, string>> FoldOnceAsync()
        {
            await using var r = EditorRepo.Create(packs: false);
            Members(r, CreatedAtColumn + "," + UpdatedAtColumn, CreatedAtIndex);
            OwnCreatedAtIndex(r);
            AssertSaved(await ApplyAsync(r, Fold(excludeMissing: true)));
            return r.Files();
        }

        var first = await FoldOnceAsync();
        var second = await FoldOnceAsync();

        Assert.Equal(first, second);
        Assert.Contains(first.Keys, path => path.Contains("/migrations/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_fold_moves_provenance_keys()
    {
        await using var r = EditorRepo.Create(packs: false);
        Members(r, CreatedAtColumn + "," + UpdatedAtColumn);
        // A re-sync from scratch records the provenance of every column of notes on InvoiceNote's binding.
        AssertSaved(await ApplyAsync(r, $$"""{"op": "resync-binding", "database": "{{EditorRepo.MainDatabaseId}}", "entities": ["{{InvoiceNote}}"]}"""));
        var recorded = r.Store.Current!.Get<Entity>(InvoiceNote)!.Bindings.Single().Provenance!.Columns;
        Assert.True(recorded.ContainsKey(NotesCreatedAt));

        AssertSaved(await ApplyAsync(r, Fold(excludeMissing: true)));

        // created_at's entry moves to the inherited member's keyed id with its value: the fold reads as no drift.
        var snapshot = r.Store.Current!;
        var entity = snapshot.Get<Entity>(InvoiceNote)!;
        var moved = entity.Bindings.Single().Provenance!.Columns;
        Assert.Equal(recorded[NotesCreatedAt], moved[Keyed(CreatedAt)]);
        Assert.False(moved.ContainsKey(NotesCreatedAt));
        Assert.Equal(recorded.Where(p => p.Key != NotesCreatedAt), moved.Where(p => p.Key != Keyed(CreatedAt)));
        Assert.False(BindingDrift.Of(snapshot, entity, entity.Bindings.Single()).HasDrift);
        var report = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule is "MQ4087" or "MQ4088");
    }
}
