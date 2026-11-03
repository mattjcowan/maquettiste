using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary><c>maquettiste model export|stats</c>: the model as data, the same pages the editor API and the MCP server serve.</summary>
public sealed class ModelCommandTests
{
    [Fact]
    public async Task Model_calculated_prints_the_values_of_the_elements_given()
    {
        // next-phase-2-design.md section 5.5: one line per object and value, ordinal; JSON prints the answer; exit 1 when a value failed.
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/extensions/calculated/names.js", """
            function snake(text) { return text.replace(/([a-z0-9])([A-Z])/g, "$1_$2").toLowerCase(); }
            maquettiste.calculated({ name: "apiName", kinds: ["entity"], schema: { type: "string" }, value: (e) => snake(e.name) });
            maquettiste.calculated({ name: "plural", kinds: ["entity"], schema: { type: "string" }, value: (e) => snake(e.pluralName) });
            maquettiste.calculated({
              name: "short",
              kinds: ["entity"],
              schema: { type: "string", maxLength: 7 },
              value: (e) => e.name,
            });

            """);
        const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
        const string Customer = "01J92P0V0ETQKXXP951CMMNHH3";

        var text = await repo.RunAsync("model", "calculated", "--element", Invoice, "--element", Customer, "--name", "apiName", "--name", "plural");
        Assert.True(text.ExitCode == 0, text.Error);
        Assert.Equal(
            [
                $"entity Customer {Customer} apiName = \"customer\"",
                $"entity Customer {Customer} plural = \"customers\"",
                $"entity Invoice {Invoice} apiName = \"invoice\"",
                $"entity Invoice {Invoice} plural = \"invoices\"",
            ],
            Text.Lines(text.Out));

        // A value that fails its schema: left out, reported, exit 1.
        var json = await repo.RunAsync("model", "calculated", "--element", Customer, "--element", "01J00000000000000000000000", "--format", "json");
        Assert.Equal(1, json.ExitCode);
        var answer = JsonNode.Parse(json.Out)!;
        var item = Assert.Single(answer["items"]!.AsArray())!;
        Assert.Equal(["apiName", "plural"], item["calculated"]!.AsObject().Select(p => p.Key));
        Assert.Equal("MQ5011", (string)Assert.Single(answer["diagnostics"]!.AsArray())!["rule"]!);
        Assert.Equal(["01J00000000000000000000000"], answer["missing"]!.AsArray().Select(m => (string)m!));
        Assert.Equal(["apiName", "plural", "short"], answer["declarations"]!.AsArray().Select(d => (string)d!["name"]!));

        var usage = await repo.RunAsync("model", "calculated");
        Assert.Equal(4, usage.ExitCode);
        Assert.Contains("--element", usage.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_writes_documents_as_a_json_array_or_one_per_line_with_fields_trimmed()
    {
        using var repo = CliRepo.Billing();

        var json = await repo.RunAsync("model", "export", "--kind", "entity", "--fields", "name,attributes");
        Assert.True(json.ExitCode == 0, json.Error);
        var array = JsonNode.Parse(json.Out)!.AsArray();
        Assert.Equal(["Customer", "CustomerNote", "Invoice", "InvoiceLine", "InvoiceNote", "Payment", "Product", "RevenueMonth"], array.Select(d => (string)d!["name"]!));
        Assert.All(array, d => Assert.Equal(["attributes", "id", "kind", "name"], d!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal)));
        Assert.Contains("Exported 8 documents.", json.Error, StringComparison.Ordinal);

        var ndjson = await repo.RunAsync("model", "export", "--package", "Billing", "--format", "ndjson");
        Assert.True(ndjson.ExitCode == 0, ndjson.Error);
        var lines = Text.Lines(ndjson.Out);
        Assert.Equal(16, lines.Length);
        // A child package names its parent in parent; every other member names the package in package.
        Assert.All(lines.Select(l => JsonNode.Parse(l)!), d => Assert.Equal("01J92P0V01KDRN8GX5PGYCNKSX", (string)d[(string)d["kind"]! == "package" ? "parent" : "package"]!));
        Assert.Contains(lines, l => (string)JsonNode.Parse(l)!["name"]! == "Invoice");

        var output = Path.Combine(repo.Temp.Root, "out", "entities.ndjson");
        var toFile = await repo.RunAsync("model", "export", "--kind", "entity", "--format", "ndjson", "--out", output);
        Assert.True(toFile.ExitCode == 0, toFile.Error);
        Assert.Equal(8, Text.Lines(File.ReadAllText(output)).Length);

        var missing = await repo.RunAsync("model", "export", "--ids", "01J92P0V0FJ23CGSNKM7P1W5V9");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("no element has the id 01J92P0V0FJ23CGSNKM7P1W5V9", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Export_resolved_writes_the_records_of_a_scope()
    {
        using var repo = CliRepo.Billing();

        var entities = await repo.RunAsync("model", "export", "--resolved", "--scope", "entities", "--format", "ndjson");
        Assert.True(entities.ExitCode == 0, entities.Error);
        var records = Text.Lines(entities.Out).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal(8, records.Count);
        Assert.All(records, r => Assert.Equal("entity", (string)r["kind"]!));
        Assert.Contains(records[0]["attributes"]!.AsArray(), a => (bool)a!["isKey"]!);

        var databases = await repo.RunAsync("model", "export", "--resolved", "--scope", "databases", "--database", "main");
        Assert.True(databases.ExitCode == 0, databases.Error);
        var database = Assert.Single(JsonNode.Parse(databases.Out)!.AsArray())!;
        Assert.Equal("main", (string)database["name"]!);
        Assert.NotEmpty(database["tables"]!.AsArray());

        Assert.Equal(4, (await repo.RunAsync("model", "export", "--resolved", "--scope", "widgets")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "export", "--resolved", "--kind", "entity")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "export", "--scope", "entities")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "export", "--resolved", "--database", "nowhere")).ExitCode);
    }

    [Fact]
    public async Task Export_resolved_on_a_model_with_errors_reports_them_and_exits_1()
    {
        using var repo = CliRepo.Billing();
        var path = repo.PathOf(".maquettiste/model/entities/payment.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        node.Remove("key");
        File.WriteAllText(path, node.ToJsonString());

        var result = await repo.RunAsync("model", "export", "--resolved");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("the model has errors", result.Error, StringComparison.Ordinal);
        Assert.Equal("", result.Out);
    }

    [Fact]
    public async Task Materialize_previews_with_dry_run_applies_and_refuses_what_is_bound()
    {
        using var repo = CliRepo.Billing();
        var invoice = repo.PathOf(".maquettiste/model/entities/invoice.json");
        var before = File.ReadAllText(invoice);

        var dry = await repo.RunAsync("model", "materialize", "tables", "--database", "main", "Invoice", "--dry-run");
        Assert.True(dry.ExitCode == 0, dry.Error);
        Assert.StartsWith("materialize-tables would:", dry.Out, StringComparison.Ordinal);
        Assert.Contains("change table invoices", dry.Out, StringComparison.Ordinal);
        Assert.Contains("delete mapping Invoice in main", dry.Out, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(invoice));

        var applied = await repo.RunAsync("model", "materialize", "tables", "--database", "main", "Invoice", "--format", "json");
        Assert.True(applied.ExitCode == 0, applied.Error);
        Assert.Equal("saved", (string)JsonNode.Parse(applied.Out)!["result"]!["outcome"]!);
        Assert.Contains("\"bindings\"", File.ReadAllText(invoice), StringComparison.Ordinal);

        var again = await repo.RunAsync("model", "materialize", "tables", "--database", "main", "Invoice");
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("MQ4055", again.Error, StringComparison.Ordinal);

        var entities = await repo.RunAsync("model", "materialize", "entities", "--database", "main", "--package", "Billing", "notes", "--dry-run");
        Assert.Equal(1, entities.ExitCode);
        Assert.Contains("already bound by entity", entities.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "tables", "--database", "main")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "views", "--database", "main", "x")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "entities", "--database", "main", "--schema", "x", "notes")).ExitCode);

        // Attributes for a bound entity's columns: the materialized Invoice maps every column, and a constant is not a field.
        var nothing = await repo.RunAsync("model", "materialize", "attributes", "--database", "main", "Invoice", "--dry-run");
        Assert.Equal(1, nothing.ExitCode);
        Assert.Contains("nothing to add", nothing.Error, StringComparison.Ordinal);
        var constant = await repo.RunAsync("model", "materialize", "attributes", "--database", "main", "--column", "entity_type", "InvoiceNote", "--dry-run");
        Assert.Equal(1, constant.ExitCode);
        Assert.Contains("is already a constant", constant.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("model", "materialize", "attributes", "--database", "main", "--package", "Billing", "Invoice")).ExitCode);
    }

    [Fact]
    public async Task Stats_counts_the_kinds_and_per_package()
    {
        using var repo = CliRepo.Billing();

        var text = await repo.RunAsync("model", "stats");
        Assert.True(text.ExitCode == 0, text.Error);
        Assert.Contains(Text.Lines(text.Out), l => l.StartsWith("entity ", StringComparison.Ordinal) && l.EndsWith(" 8", StringComparison.Ordinal));
        Assert.StartsWith("total ", Text.Lines(text.Out)[^1], StringComparison.Ordinal);

        var json = await repo.RunAsync("model", "stats", "--by", "package", "--format", "json");
        Assert.True(json.ExitCode == 0, json.Error);
        var result = JsonNode.Parse(json.Out)!;
        Assert.Equal((int)result["total"]!, result["packages"]!.AsArray().Sum(p => (int)p!["count"]!));
        Assert.Contains(result["packages"]!.AsArray(), p => (string?)p!["name"] == "Billing");

        Assert.Equal(4, (await repo.RunAsync("model", "stats", "--by", "tag")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "count")).ExitCode);
    }

    [Fact]
    public async Task Stats_and_export_list_operations_by_package()
    {
        // next-phase-2-design.md section 4.1: an operation is a kind like any other; it joins the package its file states, and lives in
        // its subject's folder.
        using var repo = CliRepo.Billing();
        const string catalog = "01J92P0V025DRFTXKMS240G2NG";
        const string invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
        repo.Write(".maquettiste/model/operations/invoice/send.json", $$"""
            {
              "$schema": "../../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "01K9QPR0000000000000000001",
              "name": "send",
              "package": "{{catalog}}",
              "subject": "{{invoice}}",
              "effect": "change",
              "outcomes": [
                {
                  "id": "01K9QPR0000000000000000002",
                  "name": "applied"
                }
              ]
            }

            """);
        repo.Write(".maquettiste/model/operations/ping.json", """
            {
              "$schema": "../../.schema/v1/operation.json",
              "kind": "operation",
              "id": "01K9QPR0000000000000000003",
              "name": "ping",
              "effect": "read",
              "outcomes": [
                {
                  "id": "01K9QPR0000000000000000004",
                  "name": "pong"
                }
              ]
            }

            """);

        var text = await repo.RunAsync("model", "stats");
        Assert.True(text.ExitCode == 0, text.Error);
        Assert.Contains(Text.Lines(text.Out), l => l.StartsWith("operation ", StringComparison.Ordinal) && l.EndsWith(" 2", StringComparison.Ordinal));

        var stats = await repo.RunAsync("model", "stats", "--by", "package", "--format", "json");
        Assert.True(stats.ExitCode == 0, stats.Error);
        var row = JsonNode.Parse(stats.Out)!["packages"]!.AsArray().Single(p => (string?)p!["name"] == "Catalog")!;
        var kinds = row["kinds"]!.AsArray().ToDictionary(k => (string)k!["kind"]!, k => (int)k!["count"]!, StringComparer.Ordinal);
        Assert.Equal(1, kinds["operation"]);

        var export = await repo.RunAsync("model", "export", "--package", "Catalog", "--kind", "operation", "--format", "ndjson");
        Assert.True(export.ExitCode == 0, export.Error);
        var exported = Assert.Single(Text.Lines(export.Out));
        Assert.Equal(("send", invoice), ((string)JsonNode.Parse(exported)!["name"]!, (string)JsonNode.Parse(exported)!["subject"]!));

        var all = await repo.RunAsync("model", "export", "--kind", "operation", "--fields", "name,effect");
        Assert.True(all.ExitCode == 0, all.Error);
        Assert.Equal([("ping", "read"), ("send", "change")],
            JsonNode.Parse(all.Out)!.AsArray().Select(d => ((string)d!["name"]!, (string)d["effect"]!)).Order());

        // The resolved scope operations gives one record per operation, ordered by name; all holds them too.
        var resolved = await repo.RunAsync("model", "export", "--resolved", "--scope", "operations", "--format", "ndjson");
        Assert.True(resolved.ExitCode == 0, resolved.Error);
        var records = Text.Lines(resolved.Out).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Equal([("ping", null, "pong"), ("send", invoice, "applied")],
            records.Select(r => ((string)r["name"]!, (string?)r["subject"], (string)r["outcomes"]![0]!["name"]!)));
        var everything = await repo.RunAsync("model", "export", "--resolved", "--format", "ndjson");
        Assert.True(everything.ExitCode == 0, everything.Error);
        Assert.Equal(2, Text.Lines(everything.Out).Count(l => (string?)JsonNode.Parse(l)!["kind"] == "operation"));
    }

    [Fact]
    public async Task Catalog_holds_tables_seeds_and_reference_types_and_stats_list_them()
    {
        // next-phase-1-design.md section 4.3: every kind may join a package, and the package listings follow the file's package.
        using var repo = CliRepo.Billing();
        const string catalog = "01J92P0V025DRFTXKMS240G2NG";
        repo.Replace(".maquettiste/model/databases/main/tables/notes.json", "\"name\": \"notes\",", $"\"name\": \"notes\",\n  \"package\": \"{catalog}\",");
        repo.Write(".maquettiste/model/reference-types/shelf-zone.json", $$"""
            {
              "$schema": "../../.schema/v1/reference-type.json",
              "kind": "reference-type",
              "id": "01K9PKG0000000000000000001",
              "name": "ShelfZone",
              "package": "{{catalog}}",
              "code": {
                "id": "01K9PKG0000000000000000002",
                "length": 8
              },
              "label": {
                "id": "01K9PKG0000000000000000003",
                "length": 40
              }
            }

            """);
        repo.Write(".maquettiste/model/seeds/notes/welcome-notes.json", $$"""
            {
              "$schema": "../../../.schema/v1/seed.json",
              "kind": "seed",
              "id": "01K9PKG0000000000000000004",
              "name": "welcome_notes",
              "package": "{{catalog}}",
              "target": "01K6BND0000000000000000001",
              "columns": [
                "01K6BND0000000000000000002",
                "01K6BND0000000000000000003",
                "01K6BND0000000000000000004",
                "01K6BND0000000000000000005"
              ],
              "rows": [
                {
                  "id": "01K9PKG0000000000000000005",
                  "values": ["7d0f8d52-63a5-4c4e-9a43-0c8d1f6f0a11", "customer", "1c5b3a8e-2f7d-4e61-b0a9-5d4c3b2a1908", "Welcome aboard."]
                }
              ]
            }

            """);

        var stats = await repo.RunAsync("model", "stats", "--by", "package", "--format", "json");
        Assert.True(stats.ExitCode == 0, stats.Error);
        var row = JsonNode.Parse(stats.Out)!["packages"]!.AsArray().Single(p => (string?)p!["name"] == "Catalog")!;
        var kinds = row["kinds"]!.AsArray().ToDictionary(k => (string)k!["kind"]!, k => (int)k!["count"]!, StringComparer.Ordinal);
        Assert.Equal(1, kinds["table"]);
        Assert.Equal(1, kinds["seed"]);
        Assert.Equal(1, kinds["reference-type"]);

        var export = await repo.RunAsync("model", "export", "--package", "Catalog", "--format", "ndjson");
        Assert.True(export.ExitCode == 0, export.Error);
        var members = Text.Lines(export.Out).Select(l => JsonNode.Parse(l)!).Select(d => ((string)d["kind"]!, (string)d["name"]!)).ToList();
        Assert.Contains(("table", "notes"), members);
        Assert.Contains(("seed", "welcome_notes"), members);
        Assert.Contains(("reference-type", "ShelfZone"), members);
        Assert.Equal(catalog, (string)JsonNode.Parse(Text.Lines(export.Out).First(l => l.Contains("\"notes\"", StringComparison.Ordinal)))!["package"]!);
    }

    [Fact]
    public async Task Delete_prints_the_plan_with_dry_run_and_deletes_with_dependents_in_one_change()
    {
        using var repo = CliRepo.Billing();
        var product = ".maquettiste/model/entities/product.json";
        var relation = ".maquettiste/model/relations/refers-to.json";

        var preview = await repo.RunAsync("model", "delete", "Product", "--resolution", "delete-dependents", "--dry-run");
        Assert.True(preview.ExitCode == 0, preview.Error);
        Assert.Contains("Deleting entity Product (delete-dependents) would:", preview.Out, StringComparison.Ordinal);
        Assert.Contains("delete relation refers to (needs entity Product)", preview.Out, StringComparison.Ordinal);
        Assert.Contains("remove member Product from diagram Billing overview", preview.Out, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PathOf(product)));

        var json = await repo.RunAsync("model", "delete", "Product", "--resolution", "remove-references", "--dry-run", "--format", "json");
        Assert.Equal(1, json.ExitCode);
        var plan = JsonNode.Parse(json.Out)!;
        Assert.Equal("invalid", (string)plan["outcome"]!);
        Assert.Equal("MQ2001", (string)plan["refused"]![0]!["rule"]!);

        var refused = await repo.RunAsync("model", "delete", "Product");
        Assert.Equal(1, refused.ExitCode);
        Assert.Contains("--resolution remove-references or delete-dependents", refused.Error, StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PathOf(product)));

        var deleted = await repo.RunAsync("model", "delete", "01J92P0V0JR8BE8253SKT29ZG7", "--resolution", "delete-dependents");
        Assert.True(deleted.ExitCode == 0, deleted.Error);
        // The products table and the lines' product column go: two drops, recorded as pending migration facts.
        Assert.Contains("Deleted 2 elements and changed 1; recorded 2 migration facts (2 pending: maquettiste validate names what each needs).",
            deleted.Error, StringComparison.Ordinal);
        Assert.False(File.Exists(repo.PathOf(product)));
        Assert.False(File.Exists(repo.PathOf(relation)));

        Assert.Equal(1, (await repo.RunAsync("model", "delete", "Nothing")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "delete", "Invoice", "--resolution", "cascade")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "delete")).ExitCode);
    }

    /// <summary>model export and model stats take --kind custom:&lt;type&gt; (next-phase-2-design.md section 4.2).</summary>
    [Fact]
    public async Task Kind_filter_takes_a_custom_kind()
    {
        using var repo = CliRepo.Billing();
        repo.Write(".maquettiste/extensions/kinds/setting.json", """{ "type": "setting", "displayName": "Setting", "properties": { "unit": { "type": "string" }, "value": { "type": "integer" } } }""");
        repo.Write(".maquettiste/model/custom/setting/due-days.json", """
            { "kind": "custom", "id": "01JR6CK000000000000000000A", "name": "due_days", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "type": "setting",
              "content": { "unit": "days", "value": 30 } }
            """);

        var export = await repo.RunAsync("model", "export", "--kind", "custom:setting", "--fields", "name,content");
        Assert.True(export.ExitCode == 0, export.Error);
        var item = Assert.Single(JsonNode.Parse(export.Out)!.AsArray())!;
        Assert.Equal(("due_days", 30), ((string)item["name"]!, (int)item["content"]!["value"]!));
        Assert.Empty(JsonNode.Parse((await repo.RunAsync("model", "export", "--kind", "custom:flag")).Out)!.AsArray());

        var stats = await repo.RunAsync("model", "stats", "--kind", "custom:setting", "--format", "json");
        Assert.True(stats.ExitCode == 0, stats.Error);
        var result = JsonNode.Parse(stats.Out)!;
        Assert.Equal(1, (int)result["total"]!);
        Assert.Equal("custom:setting", (string)Assert.Single(result["kinds"]!.AsArray())!["kind"]!);
    }

    [Fact]
    public async Task Model_diff_prints_markdown_grouped_by_package()
    {
        // next-phase-2-design.md section 6.2: the review form, by package, with each changed element's field changes and the facts of
        // each database in their own group.
        using var repo = CliRepo.Billing();
        var id = await SnapshotAsync(repo, "Before review");
        await ReviewChangeAsync(repo);

        var markdown = await repo.RunAsync("model", "diff", id, "--format", "markdown");

        Assert.True(markdown.ExitCode == 0, markdown.Error);
        var lines = Text.Lines(markdown.Out);
        Assert.Equal($"# Model diff: `{id}` to `working`", lines[0]);
        Assert.Contains("2 added, 0 removed, 1 changed.", lines);
        var billing = Array.IndexOf(lines, "## Billing");
        var migrations = Array.IndexOf(lines, "## migrations of main");
        Assert.True(billing > 0 && migrations > billing, markdown.Out);
        Assert.Equal($"- changed entity `Invoice` (`{Invoice}`)", lines[billing + 1]);
        Assert.Equal("  - attribute `code`, `/name` from `\"number\"` to `\"code\"`", lines[billing + 2]);
        Assert.StartsWith("- added table `payment_method` (`", lines[billing + 3], StringComparison.Ordinal);
        Assert.StartsWith("- added migration `rename column billing.invoices.number` (`", lines[migrations + 1], StringComparison.Ordinal);

        // Text, the default, is the same lines without the marks.
        var text = await repo.RunAsync("model", "diff", id);
        Assert.Equal(0, text.ExitCode);
        Assert.Contains("    attribute code, /name from \"number\" to \"code\"", Text.Lines(text.Out));
        Assert.DoesNotContain("`", text.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_diff_takes_a_folder_and_a_snapshot()
    {
        using var repo = CliRepo.Billing();
        using var copy = new Maquettiste.Testing.TempRepo();
        CliRepo.CopyTree(repo.ModelRoot, copy.ModelRoot); // a CI checkout of the base stands in
        var before = await SnapshotAsync(repo, "Before review");
        await ReviewChangeAsync(repo);
        var after = await SnapshotAsync(repo, "After review");

        var json = await repo.RunAsync("model", "diff", copy.RepoRoot, after, "--format", "json");

        Assert.True(json.ExitCode == 0, json.Error);
        var comparison = JsonNode.Parse(json.Out)!;
        Assert.Equal((copy.RepoRoot, after), ((string)comparison["from"]!, (string)comparison["to"]!));
        Assert.Equal(["Billing", "migrations of main"], comparison["packages"]!.AsArray().Select(p => (string)p!["name"]!));
        Assert.Equal(3, comparison["elements"]!.AsArray().Count);
        Assert.Null(comparison["next"]);

        // The folder renders the lines the snapshot of the same model renders, after the heading.
        var folder = Text.Lines((await repo.RunAsync("model", "diff", copy.RepoRoot, "--format", "markdown")).Out);
        var snapshot = Text.Lines((await repo.RunAsync("model", "diff", before, "--format", "markdown")).Out);
        Assert.Equal($"# Model diff: `{copy.RepoRoot}` to `working`", folder[0]);
        Assert.Equal(snapshot[1..], folder[1..]);
        Assert.Equal(3, folder.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Model_diff_element_prints_one_elements_fields()
    {
        using var repo = CliRepo.Billing();
        var id = await SnapshotAsync(repo, "Before review");
        await ReviewChangeAsync(repo);

        var text = await repo.RunAsync("model", "diff", id, "--element", Invoice);
        var json = await repo.RunAsync("model", "diff", id, "working", "--element", Invoice, "--format", "json");

        Assert.True(text.ExitCode == 0, text.Error);
        Assert.Equal([$"  changed entity Invoice ({Invoice})", "    attribute code, /name from \"number\" to \"code\""], Text.Lines(text.Out));
        var field = Assert.Single(JsonNode.Parse(json.Out)!["fields"]!.AsArray())!;
        Assert.Equal((InvoiceNumber, "attribute", "code", "/name"),
            ((string?)field["subId"], (string?)field["subKind"], (string?)field["subName"], (string?)field["subPointer"]));
        var missing = await repo.RunAsync("model", "diff", id, "--element", "01J00000000000000000000000");
        Assert.Equal(1, missing.ExitCode);
        Assert.Contains("neither side has the element '01J00000000000000000000000'", missing.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Model_diff_exits_4_for_a_side_that_names_nothing()
    {
        using var repo = CliRepo.Billing();

        var unknown = await repo.RunAsync("model", "diff", "no-such-base");
        Assert.Equal(4, unknown.ExitCode);
        Assert.Contains("'no-such-base' names neither a model snapshot nor a folder.", unknown.Error, StringComparison.Ordinal);
        Directory.CreateDirectory(repo.PathOf("notes"));
        var notProject = await repo.RunAsync("model", "diff", "./notes");
        Assert.Equal(4, notProject.ExitCode);
        Assert.Contains("'./notes' is not a project", notProject.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("model", "diff")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "diff", "working", "working", "working")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("model", "diff", "working", "--format", "yaml")).ExitCode);
        var same = await repo.RunAsync("model", "diff", "working", "--format", "markdown");
        Assert.Equal((0, "# Model diff: `working` to `working`\n\n0 added, 0 removed, 0 changed.\n"), (same.ExitCode, same.Out));
    }

    private const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";

    internal static async Task<string> SnapshotAsync(CliRepo repo, string name)
    {
        var created = await repo.RunAsync("snapshot", "create", name, "--format", "json");
        Assert.True(created.ExitCode == 0, created.Error);
        return (string)JsonNode.Parse(created.Out)!["id"]!;
    }

    /// <summary>
    /// The review's change, one batch through <c>model apply</c>: attribute number of Invoice renamed to code (main records a rename
    /// fact), and table payment_method created in main with package Billing.
    /// </summary>
    internal static async Task ReviewChangeAsync(CliRepo repo)
    {
        const string Path = ".maquettiste/model/entities/invoice.json";
        var hash = Engine.Hashing.ContentHash.Of(File.ReadAllBytes(repo.PathOf(Path)));
        var invoice = JsonNode.Parse(repo.Read(Path))!.AsObject();
        invoice["attributes"]!.AsArray().Single(a => (string?)a!["id"] == InvoiceNumber)!["name"] = "code";
        var batch = new JsonObject
        {
            ["operations"] = new JsonArray(
                new JsonObject { ["op"] = "update", ["id"] = Invoice, ["expectedHash"] = hash, ["element"] = invoice },
                JsonNode.Parse("""
                    { "op": "create", "element": { "kind": "table", "name": "payment_method", "database": "@database:main",
                      "schema": "@schema:billing", "package": "01J92P0V01KDRN8GX5PGYCNKSX",
                      "columns": [ { "name": "code", "type": "string", "length": 16, "nullable": false } ],
                      "primaryKey": { "columns": [ "@column:billing.payment_method.code" ] } } }
                    """)),
        };
        repo.Write("review.json", batch.ToJsonString());
        var applied = await repo.RunAsync("model", "apply", "review.json");
        Assert.True(applied.ExitCode == 0, applied.Out + applied.Error);
    }
}
