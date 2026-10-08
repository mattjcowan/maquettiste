using System.Text.Json.Nodes;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Incremental runs with the real renderer (integration tasks 3 and 4): an edit re-renders exactly the units whose recorded reads
/// changed, rewrites only the files whose bytes changed, and new files that start contributing to a resolved object (a mapping, a
/// table overlay, a relation) re-render its readers, so an incremental run equals a forced one.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class IncrementalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Editing_one_entity_rerenders_only_the_units_that_read_it_and_rewrites_only_changed_files()
    {
        await using var repo = E2ERepo.Create();
        var first = await repo.ApplyAsync();
        var before = await PlanAsync(repo);
        Assert.All(before.Units, u => Assert.True(u.Skipped, u.Key));
        Assert.Equal(first.UnitsRendered, before.Units.Count);
        var s0 = await repo.Store.GetSnapshotAsync(Ct);
        repo.AgeFiles();
        var bytes0 = repo.Tree();
        var times0 = repo.WriteTimes();

        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var s1 = await repo.Store.GetSnapshotAsync(Ct);

        // The units to render are exactly those whose recorded read keys changed hash.
        var expected = before.Units.Where(u => u.ReadKeys.Any(k => Changed(s0, s1, k))).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
        var plan = await PlanAsync(repo);
        var rendered = plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(expected, rendered);
        Assert.Contains("e2e/entity:" + E2ERepo.CustomerId, rendered);
        Assert.Contains("e2e/table:" + E2ERepo.CustomerId + "@" + E2ERepo.MainDatabaseId, rendered);
        Assert.Contains("billing-demo/entity:" + E2ERepo.CustomerId, rendered);
        Assert.DoesNotContain("e2e/entity:" + E2ERepo.ProductId, rendered);
        Assert.DoesNotContain("e2e/types:" + E2ERepo.ProductId, rendered);
        Assert.True(rendered.Count < plan.Units.Count / 2, $"{rendered.Count} of {plan.Units.Count} units rendered");

        var result = await repo.ApplyAsync();
        Assert.Equal(expected.Count, result.UnitsRendered);
        Assert.Equal(plan.Units.Count - expected.Count, result.UnitsSkipped);

        // Only files whose bytes changed were written (and are listed as Modified); every other file keeps its mtime.
        var bytes1 = repo.Tree();
        var times1 = repo.WriteTimes();
        Assert.Equal(bytes0.Keys, bytes1.Keys);
        var modified = result.Changes.Where(c => c.Kind == FileChangeKind.Modified).Select(c => c.Path).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("db/e2e/entities/customer.txt", modified);
        Assert.Contains("db/e2e/tables/customers.sql", modified);
        Assert.Contains("varchar(150)", repo.Repo.ReadFile("db/e2e/tables/customers.sql"), StringComparison.Ordinal);
        Assert.DoesNotContain("db/e2e/index.txt", modified); // re-rendered (it lists Customer) but byte-identical
        Assert.Contains("e2e/index", rendered);
        Assert.All(result.Changes, c => Assert.True(c.Kind is FileChangeKind.Modified or FileChangeKind.Kept, c.Kind + " " + c.Path));
        foreach (var path in bytes0.Keys.Where(E2ERepo.IsOutput))
        {
            var changed = !bytes0[path].AsSpan().SequenceEqual(bytes1[path]);
            Assert.True(changed == modified.Contains(path), "bytes changed but not reported, or reported but unchanged: " + path);
            Assert.True(changed == (times0[path] != times1[path]), "mtime does not follow the bytes: " + path);
        }

        Assert.Equal(modified.Count, result.FilesWritten);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Creating_a_mapping_rerenders_the_units_that_read_the_entity()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        var productsBefore = repo.Repo.ReadFile("db/e2e/tables/products.sql");
        Assert.Contains("list_price_amount", productsBefore, StringComparison.Ordinal);

        await repo.CreateElementAsync($$"""
            {"kind": "mapping", "name": "Product in main", "database": "{{E2ERepo.MainDatabaseId}}", "entity": "{{E2ERepo.ProductId}}",
             "attributes": [{"attribute": "{{E2ERepo.ProductListPriceAttributeId}}", "storage": "json"}]}
            """);
        var result = await repo.ApplyAsync();

        var products = repo.Repo.ReadFile("db/e2e/tables/products.sql");
        Assert.DoesNotContain("list_price_amount", products, StringComparison.Ordinal);
        Assert.Contains("list_price jsonb", products, StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/products.sql" && c.Kind == FileChangeKind.Modified);
        Assert.Contains(result.Changes, c => c.Path == "src/Generated/demo/db/billing/products.sql" && c.Kind == FileChangeKind.Modified);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Creating_a_table_overlay_rerenders_the_table_units()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();

        await repo.CreateElementAsync($$"""
            {"kind": "table", "database": "{{E2ERepo.MainDatabaseId}}", "origin": "synthesized", "entity": "{{E2ERepo.CustomerId}}",
             "columns": [{"id": "01J92P0VZZ0000000000000001", "attribute": "{{E2ERepo.CustomerNameAttributeId}}", "nativeType": "varchar(99)",
                          "comment": "Overlay comment."}]}
            """);
        var result = await repo.ApplyAsync();

        var customers = repo.Repo.ReadFile("db/e2e/tables/customers.sql");
        Assert.Contains("name varchar(99) NOT NULL, -- Overlay comment.", customers, StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/customers.sql" && c.Kind == FileChangeKind.Modified);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Creating_a_relation_rerenders_both_entities_and_their_tables()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();

        await repo.CreateElementAsync($$"""
            {"kind": "relation", "name": "prefers", "package": "01J92P0V01KDRN8GX5PGYCNKSX", "ends": [
              {"id": "01J92P0VZZ0000000000000002", "entity": "{{E2ERepo.ProductId}}", "role": "favoriteProduct", "navigation": "favoriteProduct", "min": 0, "max": 1},
              {"id": "01J92P0VZZ0000000000000003", "entity": "{{E2ERepo.CustomerId}}", "role": "fans", "navigation": "fans"}]}
            """);
        var result = await repo.ApplyAsync();

        Assert.Contains("favorite_product_id", repo.Repo.ReadFile("db/e2e/tables/customers.sql"), StringComparison.Ordinal);
        Assert.Contains(result.Changes, c => c.Path == "db/e2e/tables/customers.sql" && c.Kind == FileChangeKind.Modified);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Editing_a_pack_type_map_rerenders_the_units_that_used_it()
    {
        // The renderer records t:<pack>/types/<target>.json when type_of reads a pack type map; the dependency hasher must hash it.
        await using var repo = E2ERepo.Create(demo: false);
        await repo.ApplyAsync();
        var before = await PlanAsync(repo);
        var readers = before.Units.Where(u => u.ReadKeys.Contains("t:e2e/types/csharp.json")).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(8, readers.Count);
        Assert.All(readers, k => Assert.StartsWith("e2e/types:", k, StringComparison.Ordinal));

        var map = Path.Combine(repo.Repo.ModelRoot, "templates", "e2e", "types", "csharp.json");
        var node = JsonNode.Parse(File.ReadAllText(map))!;
        node["uuid"] = "System.Guid";
        File.WriteAllText(map, node.ToJsonString());

        var plan = await PlanAsync(repo);
        Assert.Equal(readers, plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal));
        var result = await repo.ApplyAsync();
        Assert.Equal(8, result.UnitsRendered);
        Assert.Contains("System.Guid Id,", repo.Repo.ReadFile("db/e2e/types/Customer.cs"), StringComparison.Ordinal);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Project_properties_reach_templates_and_editing_one_rerenders_only_the_units_that_read_project()
    {
        // project.properties (maquettiste.json "properties"): every pack's templates read them; reading project records s:project.
        await using var repo = E2ERepo.Create(demo: false, settings: s => s["properties"] = new JsonObject { ["baseNamespace"] = "Acme.Billing" });
        var index = Path.Combine(repo.Repo.ModelRoot, "templates", "e2e", "index.scriban");
        File.WriteAllText(index, "{{ project.name }}|{{ project.properties.baseNamespace }}\n" + File.ReadAllText(index));
        await repo.ApplyAsync();
        Assert.StartsWith("billing|Acme.Billing\n", repo.Repo.ReadFile("db/e2e/index.txt").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        var before = await PlanAsync(repo);
        Assert.Equal(["e2e/index"], before.Units.Where(u => u.ReadKeys.Contains("s:project")).Select(u => u.Key));

        var settingsPath = Path.Combine(repo.Repo.ModelRoot, "maquettiste.json");
        var node = JsonNode.Parse(File.ReadAllBytes(settingsPath))!.AsObject();
        node["properties"]!["baseNamespace"] = "Acme.Invoicing";
        File.WriteAllBytes(settingsPath, TestServices.Json.Write(node, "maquettiste.json", "maquettiste.json"));

        var plan = await PlanAsync(repo);
        Assert.Equal(["e2e/index"], plan.Units.Where(u => !u.Skipped).Select(u => u.Key));
        await repo.ApplyAsync();
        Assert.StartsWith("billing|Acme.Invoicing\n", repo.Repo.ReadFile("db/e2e/index.txt").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    [Fact]
    public async Task Editing_a_partial_rerenders_only_its_users_and_editing_a_unit_template_rerenders_that_unit()
    {
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        var before = await PlanAsync(repo);
        var users = before.Units.Where(u => u.ReadKeys.Contains("t:billing-demo/_property.scriban")).Select(u => u.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(8, users.Count);
        Assert.All(users, k => Assert.StartsWith("billing-demo/entity:", k, StringComparison.Ordinal));

        var partial = Path.Combine(repo.Repo.ModelRoot, "templates", "billing-demo", "_property.scriban");
        File.WriteAllText(partial, File.ReadAllText(partial).Replace("{ get; init; }", "{ get; set; }", StringComparison.Ordinal));
        var plan = await PlanAsync(repo);
        Assert.Equal(users, plan.Units.Where(u => !u.Skipped).Select(u => u.Key).Order(StringComparer.Ordinal));
        var result = await repo.ApplyAsync();
        Assert.Equal(8, result.UnitsRendered);
        Assert.Contains("{ get; set; }", repo.Repo.ReadFile("src/Generated/demo/src/Billing/Customer.g.cs"), StringComparison.Ordinal);

        // A unit's own template is part of its static hash: editing it re-renders that unit's instances only.
        var template = Path.Combine(repo.Repo.ModelRoot, "templates", "e2e", "index.scriban");
        File.WriteAllText(template, File.ReadAllText(template) + "end\n");
        var again = await repo.ApplyAsync();
        Assert.Equal(1, again.UnitsRendered);
        Assert.Equal("db/e2e/index.txt", Assert.Single(again.Changes).Path);
        await AssertIncrementalEqualsForcedAsync(repo);
    }

    /// <summary>A forced run after the incremental one changes no file: incremental output equals forced output.</summary>
    internal static async Task AssertIncrementalEqualsForcedAsync(E2ERepo repo)
    {
        var tree = repo.Tree();
        var forced = await repo.ApplyAsync(force: true);
        Assert.All(forced.Changes, c => Assert.True(c.Kind == FileChangeKind.Kept, "forced run changed " + c.Kind + " " + c.Path));
        FullRunTests.AssertSameBytes(tree, repo.Tree());
    }

    internal static async Task<GenerationPlan> PlanAsync(E2ERepo repo)
    {
        var result = await repo.Service.PlanAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        return result.Plan!;
    }

    /// <summary>Whether a model dependency key's hash differs between two snapshots (public snapshot API only).</summary>
    private static bool Changed(ModelSnapshot before, ModelSnapshot after, string key) =>
        !string.Equals(KeyHash(before, key), KeyHash(after, key), StringComparison.Ordinal);

    private static string KeyHash(ModelSnapshot model, string key) => key[..2] switch
    {
        "e:" => model.GetDocument(key[2..])?.DependencyHash ?? "absent",
        "r:" => model.ReferrersHash(key[2..]),
        "k:" => KindInfo.TryGet(key[2..], out var info) ? model.KindSetHash(info.Kind) : "absent",
        _ => "unchanged", // templates, settings and schema diffs do not change in these tests
    };
}
