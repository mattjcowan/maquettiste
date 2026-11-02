using System.Text.Json.Nodes;

namespace Maquettiste.Cli.Tests;

/// <summary>
/// <c>maquettiste binding drift|map|resync</c> (next-phase-2-design.md section 3.3, D35) over the billing fixture, where InvoiceNote and
/// CustomerNote share the table <c>notes</c>: a re-sync records InvoiceNote's provenance, a change to the table is drift, and the
/// operations catch up as <c>model materialize</c> does.
/// </summary>
public sealed class BindingCommandTests
{
    private const string Notes = ".maquettiste/model/databases/main/tables/notes.json";
    private const string InvoiceNote = ".maquettiste/model/entities/invoice-note.json";
    private const string CreatedAt = "01K6BND0000000000000000006";
    private const string Body = "01K6BND0000000000000000013";

    /// <summary>Records InvoiceNote's provenance with a re-sync from scratch.</summary>
    private static async Task RecordAsync(CliRepo repo)
    {
        var resynced = await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote");
        Assert.True(resynced.ExitCode == 0, resynced.ToString());
        Assert.NotNull(JsonNode.Parse(repo.Read(InvoiceNote))!["bindings"]![0]!["provenance"]);
    }

    /// <summary>Renames <c>body</c> to <c>note_body</c>, makes <c>created_at</c> nullable and adds <c>subject</c>, behind the store.</summary>
    private static void ChangeNotes(CliRepo repo)
    {
        var table = JsonNode.Parse(repo.Read(Notes))!.AsObject();
        var columns = table["columns"]!.AsArray();
        foreach (var column in columns.OfType<JsonObject>())
        {
            if ((string)column["name"]! == "body")
                column["name"] = "note_body";
            else if ((string)column["name"]! == "created_at")
                column["nullable"] = true;
        }

        columns.Add(new JsonObject { ["id"] = "01K6BNDADDED00000000000001", ["name"] = "subject", ["type"] = "string", ["length"] = 80 });
        WriteJson(repo, Notes, table);
    }

    private static void WriteJson(CliRepo repo, string path, JsonNode node) =>
        repo.Write(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");

    private static JsonObject Attribute(CliRepo repo, string id) =>
        JsonNode.Parse(repo.Read(InvoiceNote))!["attributes"]!.AsArray().OfType<JsonObject>().Single(a => (string)a["id"]! == id);

    [Fact]
    public async Task Drift_prints_added_renamed_and_retyped()
    {
        using var repo = CliRepo.Billing();
        await RecordAsync(repo);
        ChangeNotes(repo);

        var drift = await repo.RunAsync("binding", "drift", "--database", "main", "InvoiceNote");

        Assert.True(drift.ExitCode == 0, drift.ToString());
        Assert.Equal(
            "InvoiceNote in main (notes)\n"
            + "  added subject (unaccounted)\n"
            + "  renamed note_body (attribute body)\n"
            + "  retyped created_at (attribute createdAt)\n",
            drift.Out);
        Assert.Contains("1 binding listed, 1 with drift.", drift.Error, StringComparison.Ordinal);

        // Every binding: CustomerNote has no provenance and lists only the column nothing accounts for; RevenueMonth reads a query.
        var all = await repo.RunAsync("binding", "drift", "--all", "--format", "json");
        Assert.True(all.ExitCode == 0, all.ToString());
        var bindings = JsonNode.Parse(all.Out)!["bindings"]!.AsArray();
        Assert.Equal(["CustomerNote", "InvoiceNote", "RevenueMonth"], bindings.Select(b => (string)b!["entityName"]!));
        var customer = bindings[0]!;
        Assert.False((bool)customer["hasProvenance"]!);
        Assert.Equal("subject", (string)Assert.Single(customer["added"]!.AsArray())!["name"]!);
        Assert.Empty(customer["renamed"]!.AsArray());
        var invoice = bindings[1]!;
        Assert.Equal((Body, "body"), ((string)invoice["renamed"]![0]!["attribute"]!, (string)invoice["renamed"]![0]!["attributeName"]!));
        Assert.Equal(CreatedAt, (string)invoice["retyped"]![0]!["column"]!);
        Assert.Empty(bindings[2]!["added"]!.AsArray());

        Assert.Equal(4, (await repo.RunAsync("binding", "drift", "--database", "nowhere")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("binding", "drift", "Nobody")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("binding", "drift", "--keep-names")).ExitCode);
    }

    [Fact]
    public async Task Drift_check_exits_2_on_drift()
    {
        using var repo = CliRepo.Billing();
        await RecordAsync(repo);

        var clean = await repo.RunAsync("binding", "drift", "--check");
        Assert.True(clean.ExitCode == 0, clean.ToString());
        Assert.Equal("", clean.Out);

        repo.Replace(Notes, "\"name\": \"body\"", "\"name\": \"note_body\"");
        var drifted = await repo.RunAsync("binding", "drift", "--check");
        Assert.Equal(2, drifted.ExitCode);
        Assert.Contains("renamed note_body (attribute body)", drifted.Out, StringComparison.Ordinal);

        // A binding in step listed with --all does not drift.
        var customer = await repo.RunAsync("binding", "drift", "--check", "--all", "CustomerNote");
        Assert.True(customer.ExitCode == 0, customer.ToString());
        Assert.Equal("CustomerNote in main (notes), no provenance\n  nothing to report\n", customer.Out);
    }

    [Fact]
    public async Task Map_dry_run_writes_nothing()
    {
        using var repo = CliRepo.Billing();
        // A new column and an unmapped attribute of the same loose name.
        var table = JsonNode.Parse(repo.Read(Notes))!.AsObject();
        table["columns"]!.AsArray().Add(new JsonObject { ["id"] = "01K6BNDADDED00000000000001", ["name"] = "Note_Subject", ["type"] = "string", ["length"] = 80 });
        WriteJson(repo, Notes, table);
        var entity = JsonNode.Parse(repo.Read(InvoiceNote))!.AsObject();
        entity["attributes"]!.AsArray().Add(new JsonObject { ["id"] = "01K6BNDADDED00000000000002", ["name"] = "noteSubject", ["type"] = "string", ["length"] = 80 });
        WriteJson(repo, InvoiceNote, entity);
        var before = repo.Tree(".maquettiste");

        var dry = await repo.RunAsync("binding", "map", "--database", "main", "InvoiceNote", "--dry-run");

        Assert.True(dry.ExitCode == 0, dry.ToString());
        Assert.StartsWith("map-by-name would:\n  change entity InvoiceNote", dry.Out, StringComparison.Ordinal);
        Assert.Equal(before, repo.Tree(".maquettiste"));

        var applied = await repo.RunAsync("binding", "map", "--database", "main", "01K6BND0000000000000000010", "--format", "json");
        Assert.True(applied.ExitCode == 0, applied.ToString());
        Assert.Equal("saved", (string)JsonNode.Parse(applied.Out)!["result"]!["outcome"]!);
        var binding = JsonNode.Parse(repo.Read(InvoiceNote))!["bindings"]![0]!;
        Assert.Contains(binding["fields"]!.AsArray(), f => (string)f!["attribute"]! == "01K6BNDADDED00000000000002" && (string)f["column"]! == "01K6BNDADDED00000000000001");
        Assert.NotNull(binding["provenance"]);

        // Nothing left to map is refused (MQ4089), and an entity is required.
        var again = await repo.RunAsync("binding", "map", "--database", "main", "InvoiceNote");
        Assert.Equal(1, again.ExitCode);
        Assert.Contains("MQ4089", again.Error, StringComparison.Ordinal);
        Assert.Equal(4, (await repo.RunAsync("binding", "map", "--database", "main")).ExitCode);
        Assert.Equal(4, (await repo.RunAsync("binding", "map", "InvoiceNote")).ExitCode);
    }

    [Fact]
    public async Task Resync_keep_names_keeps_attribute_names()
    {
        using var repo = CliRepo.Billing();
        await RecordAsync(repo);
        repo.Replace(Notes, "\"name\": \"body\"", "\"name\": \"note_body\"");

        var kept = await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote", "--keep-names");

        Assert.True(kept.ExitCode == 0, kept.ToString());
        Assert.Equal("body", (string)Attribute(repo, Body)["name"]!);
        var drift = await repo.RunAsync("binding", "drift", "--check");
        Assert.True(drift.ExitCode == 0, drift.ToString());

        // Without --keep-names the attribute that followed its column takes the new name and keeps its id.
        repo.Replace(Notes, "\"name\": \"note_body\"", "\"name\": \"text_body\"");
        Assert.Equal(0, (await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote")).ExitCode);
        Assert.Equal("body", (string)Attribute(repo, Body)["name"]!); // it no longer followed its column: the name was kept last time
    }

    [Fact]
    public async Task Resync_column_takes_a_name_or_a_key()
    {
        using var repo = CliRepo.Billing();
        await RecordAsync(repo);
        ChangeNotes(repo);

        // A physical name: only that column's entry and attribute come into step.
        var byName = await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote", "--column", "note_body");
        Assert.True(byName.ExitCode == 0, byName.ToString());
        Assert.Equal("noteBody", (string)Attribute(repo, Body)["name"]!);
        var left = await repo.RunAsync("binding", "drift", "InvoiceNote");
        Assert.DoesNotContain("renamed", left.Out, StringComparison.Ordinal);
        Assert.Contains("retyped created_at (attribute createdAt)", left.Out, StringComparison.Ordinal);

        // A key: the column id.
        var byKey = await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote", "--column", CreatedAt);
        Assert.True(byKey.ExitCode == 0, byKey.ToString());
        Assert.DoesNotContain("retyped", (await repo.RunAsync("binding", "drift", "InvoiceNote")).Out, StringComparison.Ordinal);

        // --column serves one entity only, and names a column the source has.
        Assert.Equal(4, (await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote", "CustomerNote", "--column", "subject")).ExitCode);
        var unknown = await repo.RunAsync("binding", "resync", "--database", "main", "InvoiceNote", "--column", "nowhere");
        Assert.Equal(1, unknown.ExitCode);
        Assert.Contains("MQ4089", unknown.Error, StringComparison.Ordinal);
    }
}
