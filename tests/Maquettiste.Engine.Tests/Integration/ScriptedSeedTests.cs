using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Seeds whose rows a script computes from the model (next-phase-2-design.md section 3.4, decisions 11 to 17): a reference type of the
/// billing copy takes one row per entity the stereotype <c>audited</c> marks. Row ids derive from the seed and the key; a script runs only
/// when something reads its rows (a unit, whole-model validation, a save of the seed), through the engine's rows cache; its reads
/// render the reading unit again; and its failures are MQ7116, MQ7127, MQ7128 and MQ5005.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ScriptedSeedTests
{
    private const string TypeId = "01K8SCR0000000000000000001";
    private const string SeedId = "01K8SCR0000000000000000010";
    private const string ScriptPath = ".maquettiste/extensions/seeds/audited-records.js";
    private const string SeedPath = ".maquettiste/model/seeds/audited-records.json";
    private const string Pack = "rows";

    /// <summary>The script of the design's example: one row per audited entity, its lower-case name as code.</summary>
    private const string AuditedRows = """
        maquettiste.rows((model, seed) => model.withStereotype("audited")
          .filter(d => d.kind === "entity")
          .map(d => ({ code: d.name.toLowerCase(), label: d.displayName ?? d.name })));
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A billing copy with the reference type, its scripted seed and the script, written before the store loads, and a pack of two model
    /// units: <c>codes</c> lists every reference type's rows, <c>plain</c> reads nothing. <paramref name="lowered"/> names rules whose
    /// severity <c>validation.rules</c> lowers to a warning, so a preview renders past them.
    /// </summary>
    private static E2ERepo Create(string script = AuditedRows, string? seed = null, params string[] lowered)
    {
        var repo = E2ERepo.Create(demo: false, settings: s =>
        {
            E2ERepo.RecordOff(s);
            s["packs"]![Pack] = new JsonObject { ["output"] = "db/rows" };
            if (lowered.Length > 0)
                s["validation"] = new JsonObject { ["rules"] = new JsonObject(lowered.Select(r => KeyValuePair.Create(r, (JsonNode?)"warning"))) };
        });
        repo.Repo.WriteFile(".maquettiste/model/reference-types/audited-record-type.json", $$$"""
            {"kind":"reference-type","id":"{{{TypeId}}}","name":"AuditedRecordType",
             "code":{"id":"01K8SCR0000000000000000002","length":32},"label":{"id":"01K8SCR0000000000000000003","length":64}}
            """);
        repo.Repo.WriteFile(SeedPath, seed ?? $$$"""
            {"kind":"seed","id":"{{{SeedId}}}","name":"AuditedRecords","target":"{{{TypeId}}}","columns":["code","label"],
             "rowsFrom":{"script":"seeds/audited-records.js"}}
            """);
        repo.Repo.WriteFile(ScriptPath, script);
        var folder = Path.Combine(repo.Repo.ModelRoot, "templates", Pack);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "pack.json"), """
            {"name": "rows", "version": "1.0.0", "engine": ">=1.0", "units": [
              {"id": "codes", "template": "codes.scriban", "for": "model", "output": "codes.txt"},
              {"id": "plain", "template": "plain.scriban", "for": "model", "output": "plain.txt"}]}
            """);
        File.WriteAllText(Path.Combine(folder, "codes.scriban"),
            "{{ for t in model.reference_types }}{{ t.name }}:{{ for r in t.rows }} {{ r.code }}={{ r.id }}{{ end }}\n{{ end }}");
        File.WriteAllText(Path.Combine(folder, "plain.scriban"), "plain\n");
        return repo;
    }

    private static string RowId(string code) => StableIds.RowId(SeedId, [code]);

    private static string Codes(string text) =>
        string.Join(' ', text.Split(':', 2)[1].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=')[0]));

    private static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    /// <summary>The script runs of the store's and the service's rows caches.</summary>
    private static long Runs(E2ERepo repo) => repo.Store.Services.SeedRows.Runs + repo.Service.Services.SeedRows.Runs;

    /// <summary>The seed in the resolved model previews render from (the store's shared resolution of the current snapshot).</summary>
    private static async Task<RSeed> ResolvedSeedAsync(E2ERepo repo)
    {
        var shared = await repo.Store.ResolvedAsync(await repo.Store.GetSnapshotAsync(Ct), Ct);
        return Assert.IsType<RSeed>(shared.Model!.Find(SeedId));
    }

    private static Task<GenerationResult> ApplyAsync(E2ERepo repo) =>
        repo.Service.RunAsync(new GenerationRequest { Mode = GenerationMode.Apply, Jobs = 2, Packs = [Pack] }, null, Ct);

    private static async Task<GenerationPlan> PlanAsync(E2ERepo repo) =>
        (await repo.Service.PlanAsync(new GenerationRequest { Packs = [Pack] }, null, Ct)).Plan!;

    [Fact]
    public async Task A_rows_script_gives_a_reference_type_one_row_per_audited_entity()
    {
        // The acceptance case of scripted rows (D45; scope "Done when"): the codes unit loops over reference_type.rows, the plain unit
        // reads nothing. Customer, Invoice and Payment carry audited in the billing model.
        await using var repo = Create();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await ApplyAsync(repo));
        Assert.Equal($"AuditedRecordType: customer={RowId("customer")} invoice={RowId("invoice")} payment={RowId("payment")}\n",
            repo.Repo.ReadFile("db/rows/codes.txt"));
        var type = Assert.IsType<RReferenceType>((await repo.Store.ResolvedAsync(await repo.Store.GetSnapshotAsync(Ct), Ct)).Model!.Find(TypeId));
        Assert.Equal(["customer", "invoice", "payment"], type.Rows.Select(r => (string)r.Code!));
        Assert.Equal(["Customer", "Invoice", "Payment"], type.Rows.Select(r => r.Label));

        // Product gains audited: the next plan renders the looping unit again, and not the other.
        await repo.EditAsync(E2ERepo.ProductId, n => n["stereotypes"] = new JsonArray("soft-delete", "audited"));
        var plan = await PlanAsync(repo);
        Assert.False(plan.Units.Single(u => u.Unit == "codes").Skipped);
        Assert.True(plan.Units.Single(u => u.Unit == "plain").Skipped);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await ApplyAsync(repo));
        Assert.Equal("customer invoice payment product", Codes(repo.Repo.ReadFile("db/rows/codes.txt")));
        Assert.Contains($"product={RowId("product")}", repo.Repo.ReadFile("db/rows/codes.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scripted_row_ids_derive_from_the_seed_and_the_key()
    {
        await using var repo = Create();

        var preview = await repo.Service.PreviewAsync(Pack, "codes", null, Ct);

        var text = Assert.Single(preview.Files).Text;
        Assert.Equal($"AuditedRecordType: customer={RowId("customer")} invoice={RowId("invoice")} payment={RowId("payment")}\n", text);
        // The same ids on every run: a restarted host derives them again.
        await repo.RestartAsync();
        Assert.Equal(text, Assert.Single((await repo.Service.PreviewAsync(Pack, "codes", null, Ct)).Files).Text);
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task A_preview_of_a_unit_that_reads_no_seed_runs_no_script()
    {
        await using var repo = Create();

        var plain = await repo.Service.PreviewAsync(Pack, "plain", null, Ct);

        // The validation every preview waits on leaves the scripted seed unjudged, and the render of a unit that does not read its rows
        // leaves the resolved seed's rows unbuilt: the script never ran.
        Assert.Equal("plain\n", Assert.Single(plain.Files).Text);
        Assert.Equal(0, Runs(repo));
        var seed = await ResolvedSeedAsync(repo);
        Assert.False(seed.RowsBuilt);
        // A unit that reads them builds them, running the script once.
        await repo.Service.PreviewAsync(Pack, "codes", null, Ct);
        Assert.True(seed.RowsBuilt);
        Assert.Equal(1, Runs(repo));
        // Whole-model validation judges them from the cached run.
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(1, Runs(repo));
    }

    [Fact]
    public async Task A_save_outside_the_seed_scope_runs_no_script()
    {
        await using var repo = Create();
        await repo.Store.GetSnapshotAsync(Ct);
        var cache = repo.Store.Services.SeedRows;
        var before = cache.Runs;

        // Customer is one of the documents the script reads: a run would see the change, so only scope keeps it from running.
        await repo.EditAsync(E2ERepo.CustomerId, n => n["description"] = "Someone we bill, monthly.");

        Assert.Equal(before, cache.Runs);
        // A save of the seed itself runs it (on the snapshots the save compares).
        await repo.EditAsync(SeedId, n => n["description"] = "One row per audited entity.");
        Assert.True(cache.Runs > before);
    }

    [Fact]
    public async Task An_extension_write_runs_no_rows_script()
    {
        await using var repo = Create();
        await repo.Store.GetSnapshotAsync(Ct);
        var cache = repo.Store.Services.SeedRows;

        var result = await repo.Store.WriteExtensionFileAsync("seeds/more.js", "maquettiste.rows(() => { throw new Error('never run'); });\n", null, Ct);

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(0, cache.Runs);
        Assert.Contains((await repo.Store.GetSnapshotAsync(Ct)).SeedScripts, s => s.Path == ".maquettiste/extensions/seeds/more.js");
        Assert.Equal(0, cache.Runs);
    }

    [Fact]
    public async Task Unchanged_inputs_reuse_the_cached_rows()
    {
        await using var repo = Create();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await ApplyAsync(repo));
        // Validation and the codes unit read the rows of one snapshot: the script ran once.
        Assert.Equal(1, Runs(repo));

        // Product is not audited, so the script never read it: the next snapshot's validation and plan reuse the rows.
        await repo.EditAsync(E2ERepo.ProductId, n => n["description"] = "Something we sell.");
        var plan = await PlanAsync(repo);
        Assert.True(plan.Units.Single(u => u.Unit == "codes").Skipped);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await ApplyAsync(repo));
        Assert.Equal(1, Runs(repo));
    }

    [Fact]
    public async Task A_change_to_a_read_element_renders_the_reading_unit_again()
    {
        await using var repo = Create();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await ApplyAsync(repo));
        Assert.Equal("customer invoice payment", Codes(repo.Repo.ReadFile("db/rows/codes.txt")));
        var keys = (await PlanAsync(repo)).Units.Single(u => u.Unit == "codes");
        Assert.True(keys.Skipped);

        // Product gains audited: the script's withStereotype read (ix:) changes, so the unit that read its rows renders again.
        await repo.EditAsync(E2ERepo.ProductId, n => n["stereotypes"] = new JsonArray("soft-delete", "audited"));
        var plan = await PlanAsync(repo);
        var codes = plan.Units.Single(u => u.Unit == "codes");
        Assert.False(codes.Skipped);
        Assert.Contains(codes.ReadKeys, k => k == "x:seeds/audited-records.js");
        Assert.Contains(codes.ReadKeys, k => k.StartsWith("ix:", StringComparison.Ordinal));
        Assert.True(plan.Units.Single(u => u.Unit == "plain").Skipped);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await ApplyAsync(repo));
        Assert.Equal("customer invoice payment product", Codes(repo.Repo.ReadFile("db/rows/codes.txt")));

        // Editing the script renders it again too (its x: key).
        repo.Repo.WriteFile(ScriptPath, AuditedRows.Replace("toLowerCase()", "toUpperCase()", StringComparison.Ordinal));
        await repo.Store.RescanAsync(false, Ct);
        Assert.False((await PlanAsync(repo)).Units.Single(u => u.Unit == "codes").Skipped);
    }

    [Fact]
    public async Task A_script_that_throws_is_MQ7127_and_fails_the_reading_unit()
    {
        await using var repo = Create("maquettiste.rows((model, seed) => {\n  throw new Error('not ready');\n});\n");

        var failure = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ7127");
        Assert.Equal(DiagnosticSeverity.Error, failure.Severity);
        Assert.Equal((SeedPath, "/rowsFrom/script", SeedId), (failure.FilePath, failure.JsonPointer, failure.ElementId));
        Assert.Contains("not ready", failure.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", failure.Message, StringComparison.Ordinal);

        // Previews do not judge the scripted seed, so only the unit that reads its rows meets the failure.
        var codes = await repo.Service.PreviewAsync(Pack, "codes", null, Ct);
        Assert.Empty(codes.Files);
        var unit = Assert.Single(codes.Diagnostics, d => d.Rule == "MQ7127");
        Assert.Equal(DiagnosticSeverity.Error, unit.Severity);
        Assert.Throws<Maquettiste.Engine.Scripting.ScriptErrorException>(() => (ResolvedSeedAsync(repo).GetAwaiter().GetResult()).Rows);
        var plain = await repo.Service.PreviewAsync(Pack, "plain", null, Ct);
        Assert.Equal("plain\n", Assert.Single(plain.Files).Text);
    }

    [Fact]
    public async Task A_key_outside_the_columns_is_MQ7128()
    {
        await using var repo = Create("""
            maquettiste.rows(() => [
              { code: "a", label: "A" },
              { code: "b", label: "B", colour: "blue" },
            ]);
            """, null, "MQ7128");

        var finding = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ7128");
        Assert.Equal((SeedPath, "/rowsFrom/script"), (finding.FilePath, finding.JsonPointer));
        Assert.Contains("'colour'", finding.Message, StringComparison.Ordinal);
        // The row with the unknown key is left out; the other stays.
        var codes = await repo.Service.PreviewAsync(Pack, "codes", null, Ct);
        Assert.Equal($"AuditedRecordType: a={RowId("a")}\n", Assert.Single(codes.Files).Text);
    }

    [Fact]
    public async Task A_relation_seed_with_a_script_is_MQ7128()
    {
        await using var repo = Create(seed: """
            {"kind":"seed","id":"01K8SCR0000000000000000010","name":"Lines","target":"01J92P0V1BWHG0REWKSTR292RS",
             "columns":["01J92P0V1GMF7GJPA7981CH7YG","01J92P0V1H4D2M1HCK82ASJEWT"],"rowsFrom":{"script":"seeds/audited-records.js"}}
            """);

        var finding = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ7128");
        Assert.Equal((SeedPath, "/rowsFrom/script"), (finding.FilePath, finding.JsonPointer));
        Assert.Contains("no row key", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_script_or_rows_beside_it_is_MQ7116()
    {
        await using var repo = Create(seed: $$$"""
            {"kind":"seed","id":"{{{SeedId}}}","name":"AuditedRecords","target":"{{{TypeId}}}","columns":["code","label"],
             "rowsFrom":{"script":"seeds/missing.js"}}
            """);
        repo.Repo.WriteFile(".maquettiste/model/seeds/beside.json", $$$"""
            {"kind":"seed","id":"01K8SCR0000000000000000020","name":"Beside","target":"{{{TypeId}}}","columns":["code","label"],
             "rowsFrom":{"script":"seeds/audited-records.js"},"rows":[{"id":"01K8SCR0000000000000000021","values":["x","X"]}]}
            """);

        var findings = (await ValidateAsync(repo)).Where(d => d.Rule == "MQ7116").OrderBy(d => d.FilePath, StringComparer.Ordinal).ToList();

        Assert.Equal(2, findings.Count);
        Assert.Equal((SeedPath, "/rowsFrom/script"), (findings[0].FilePath, findings[0].JsonPointer));
        Assert.Contains("seeds/missing.js of seed 'AuditedRecords' is missing", findings[0].Message, StringComparison.Ordinal);
        Assert.Equal((".maquettiste/model/seeds/beside.json", "/rows"), (findings[1].FilePath, findings[1].JsonPointer));
    }

    [Fact]
    public async Task Top_level_let_in_a_rows_script_is_MQ5005()
    {
        await using var repo = Create("let count = 0;\nmaquettiste.rows(() => [{ code: 'one', label: 'One' }]);\n");

        var warning = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ5005");
        Assert.Equal((DiagnosticSeverity.Warning, ScriptPath, 1), (warning.Severity, warning.FilePath, warning.Line));
        Assert.StartsWith("Rows script ", warning.Message, StringComparison.Ordinal);
        // Its rows still come, but they are never cached: each validation that judges the seed runs it again.
        var before = Runs(repo);
        await repo.Store.ValidateAsync(new ValidationScope([SeedId]), Ct);
        await repo.Store.ValidateAsync(new ValidationScope([SeedId]), Ct);
        Assert.Equal(before + 2, Runs(repo));
        var codes = await repo.Service.PreviewAsync(Pack, "codes", null, Ct);
        Assert.Equal($"AuditedRecordType: one={RowId("one")}\n", Assert.Single(codes.Files).Text);
    }

    [Fact]
    public async Task A_default_naming_a_scripted_code_is_judged_by_whole_model_validation()
    {
        await using var repo = Create();
        const string AttributeId = "01K8SCR0000000000000000030";

        // A save of Product does not run the type's script and does not judge a code no stored row holds.
        await repo.EditAsync(E2ERepo.ProductId, n => n["attributes"]!.AsArray().Add(new JsonObject
        {
            ["id"] = AttributeId, ["name"] = "recordType", ["type"] = new JsonObject { ["ref"] = TypeId }, ["default"] = "customer",
        }));
        Assert.DoesNotContain(await ValidateAsync(repo), d => d.Rule == "MQ3019");

        var saved = await repo.EditAsync(E2ERepo.ProductId, n =>
            n["attributes"]!.AsArray().Single(a => (string?)a!["id"] == AttributeId)!["default"] = "nothing");
        Assert.DoesNotContain(saved.Diagnostics, d => d.Rule == "MQ3019");
        var finding = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ3019");
        Assert.Equal(AttributeId, finding.ElementId);
        Assert.Contains("'nothing' is not a code", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_foreign_key_entry_finds_a_scripted_key_whatever_its_characters()
    {
        await using var repo = Create();
        const string Regions = "01K8SCR0000000000000000040", RegionCode = "01K8SCR0000000000000000041", RegionName = "01K8SCR0000000000000000042";
        const string Offices = "01K8SCR0000000000000000050", OfficeCode = "01K8SCR0000000000000000051", OfficeRegion = "01K8SCR0000000000000000052";
        const string OfficeRegionKey = "01K8SCR0000000000000000053";
        // Regions keyed by their code take their rows from a script; offices name their region by that code. A key with a character
        // JSON may escape (é, &) is the same key in a stored cell and in a scripted row.
        repo.Repo.WriteFile(".maquettiste/model/databases/main/tables/regions.json", $$$"""
            {"kind":"table","id":"{{{Regions}}}","name":"regions","database":"{{{E2ERepo.MainDatabaseId}}}","columns":[
              {"id":"{{{RegionCode}}}","name":"code","type":"string","length":32,"nullable":false},
              {"id":"{{{RegionName}}}","name":"name","type":"string","length":8}],
             "primaryKey":{"columns":["{{{RegionCode}}}"]}}
            """);
        repo.Repo.WriteFile(".maquettiste/model/databases/main/tables/offices.json", $$$"""
            {"kind":"table","id":"{{{Offices}}}","name":"offices","database":"{{{E2ERepo.MainDatabaseId}}}","columns":[
              {"id":"{{{OfficeCode}}}","name":"code","type":"string","length":8,"nullable":false},
              {"id":"{{{OfficeRegion}}}","name":"region_code","type":"string","length":32,"nullable":false}],
             "primaryKey":{"columns":["{{{OfficeCode}}}"]},
             "foreignKeys":[{"id":"{{{OfficeRegionKey}}}","name":"fk_offices_region","columns":["{{{OfficeRegion}}}"],"referencesTable":"{{{Regions}}}"}]}
            """);
        repo.Repo.WriteFile(".maquettiste/extensions/seeds/regions.js",
            "maquettiste.rows(() => [{ code: \"café\", name: \"Café\" }, { code: \"R&D\", name: \"Research and development\" }]);\n");
        repo.Repo.WriteFile(".maquettiste/model/seeds/regions.json", $$$"""
            {"kind":"seed","id":"01K8SCR0000000000000000043","name":"regions","target":"{{{Regions}}}","columns":["{{{RegionCode}}}","{{{RegionName}}}"],
             "rowsFrom":{"script":"seeds/regions.js"}}
            """);
        repo.Repo.WriteFile(".maquettiste/model/seeds/offices.json", $$$"""
            {"kind":"seed","id":"01K8SCR0000000000000000054","name":"offices","target":"{{{Offices}}}","columns":["{{{OfficeCode}}}","{{{OfficeRegionKey}}}"],
             "rows":[{"id":"01K8SCR0000000000000000055","values":["PAR","café"]},{"id":"01K8SCR0000000000000000056","values":["LAB","R&D"]},
                     {"id":"01K8SCR0000000000000000057","values":["NOW","nowhere"]}]}
            """);

        var diagnostics = await ValidateAsync(repo);

        // Only the office whose region no row holds is reported.
        var missing = Assert.Single(diagnostics, d => d.Rule == "MQ7111");
        Assert.Equal((".maquettiste/model/seeds/offices.json", "/rows/2/values/1"), (missing.FilePath, missing.JsonPointer));
        Assert.Contains("\"nowhere\"", missing.Message, StringComparison.Ordinal);
        // A finding on a scripted row names it by its key as written.
        var tooLong = Assert.Single(diagnostics, d => d.Rule == "MQ7109");
        Assert.Equal("/rowsFrom/script", tooLong.JsonPointer);
        Assert.StartsWith("Scripted row 'R&D': ", tooLong.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_x_ref_to_a_scripted_row_is_reported_as_unknown()
    {
        await using var repo = Create();
        repo.Repo.WriteFile(".maquettiste/extensions/record-links.json", """
            {"name": "recordLinks", "appliesTo": {"kinds": ["entity"]},
             "properties": {"favoriteRecord": {"type": "string", "x-ref": {"kind": "row"}}}}
            """);
        var customer = JsonNode.Parse(repo.Repo.ReadFile(".maquettiste/model/entities/customer.json"))!.AsObject();
        customer["properties"] = new JsonObject { ["favoriteRecord"] = RowId("customer") };
        repo.Repo.WriteFile(".maquettiste/model/entities/customer.json", customer.ToJsonString());

        var dangling = Assert.Single(await ValidateAsync(repo), d => d.Rule == "MQ2001");
        Assert.Equal(E2ERepo.CustomerId, dangling.ElementId);
        Assert.Contains(RowId("customer"), dangling.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Snapshots_keep_seed_scripts_across_writes()
    {
        await using var repo = Create();
        var loaded = await repo.Store.GetSnapshotAsync(Ct);
        Assert.Equal([ScriptPath], loaded.SeedScripts.Select(s => s.Path));

        await repo.EditAsync(E2ERepo.CustomerId, n => n["description"] = "Someone we bill.");
        var settings = await repo.Store.GetSettingsAsync(Ct);
        var node = JsonNode.Parse(settings.Json.GetRawText())!.AsObject();
        node["name"] = "Billing two";
        var saved = await repo.Store.SaveSettingsAsync(System.Text.Encoding.UTF8.GetBytes(node.ToJsonString()), settings.Hash, ChangeSource.Editor, Ct);
        Assert.Equal(SaveOutcome.Saved, saved.Outcome);

        var after = await repo.Store.GetSnapshotAsync(Ct);
        Assert.NotSame(loaded, after);
        Assert.Equal(loaded.SeedScripts, after.SeedScripts);
    }
}
