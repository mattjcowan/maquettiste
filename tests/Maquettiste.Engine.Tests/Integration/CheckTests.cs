using System.Text.Json.Nodes;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Check mode (integration task 5): every root, in memory; a hand edit is a conflict, a stale, missing or orphaned file is
/// drift, with the precedence of engine-design.md section 16 (busy and internal 4, invalid 1, conflicts 3, drift 2). Check writes nothing.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CheckTests
{
    [Fact]
    public async Task A_hand_edit_is_a_conflict_and_check_writes_nothing()
    {
        await using var repo = await AppliedAsync();
        repo.Repo.WriteFile("db/e2e/entities/product.txt", "edited by hand\n");
        var tree = repo.Tree();
        var times = repo.WriteTimes();

        var check = await repo.RunAsync(GenerationMode.Check);

        E2ERepo.AssertOutcome(RunOutcome.Conflicts, check);
        var change = Assert.Single(check.Changes, c => c.Kind != FileChangeKind.Kept);
        Assert.Equal(("db/e2e/entities/product.txt", FileChangeKind.Conflict), (change.Path, change.Kind)); // check forces policy fail
        FullRunTests.AssertSameBytes(tree, repo.Tree());
        Assert.Equal(times, repo.WriteTimes());
        Assert.False(repo.Repo.Exists(".maquettiste/.cache/journal.jsonl"));
    }

    [Fact]
    public async Task A_stale_file_is_drift()
    {
        await using var repo = await AppliedAsync();
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        var times = repo.WriteTimes();

        var check = await repo.RunAsync(GenerationMode.Check);

        E2ERepo.AssertOutcome(RunOutcome.Drift, check);
        Assert.Contains(check.Changes, c => c.Path == "db/e2e/entities/customer.txt" && c.Kind == FileChangeKind.Modified);
        Assert.Contains(check.Changes, c => c.Path == "db/e2e/tables/customers.sql" && c.Kind == FileChangeKind.Modified);
        // Every root is checked: the demo pack's output under src/Generated is drift too.
        Assert.Contains(check.Changes, c => c.Path.StartsWith("src/Generated/", StringComparison.Ordinal) && c.Kind == FileChangeKind.Modified);
        Assert.Equal(times, repo.WriteTimes());
        Assert.Contains("length 120", repo.Repo.ReadFile("db/e2e/entities/customer.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_file_is_drift()
    {
        await using var repo = await AppliedAsync();
        File.Delete(repo.Repo.PathOf("db/e2e/tables/products.sql"));

        var check = await repo.RunAsync(GenerationMode.Check);

        E2ERepo.AssertOutcome(RunOutcome.Drift, check);
        var change = Assert.Single(check.Changes, c => c.Kind != FileChangeKind.Kept);
        Assert.Equal(("db/e2e/tables/products.sql", FileChangeKind.Added), (change.Path, change.Kind));
        Assert.False(repo.Repo.Exists("db/e2e/tables/products.sql"));
    }

    [Fact]
    public async Task An_orphaned_file_is_drift()
    {
        await using var repo = await AppliedAsync();
        var packFile = Path.Combine(repo.Repo.ModelRoot, "templates", "e2e", "pack.json");
        var pack = JsonNode.Parse(File.ReadAllText(packFile))!;
        var units = pack["units"]!.AsArray();
        units.Remove(units.Single(u => (string?)u!["id"] == "audited"));
        File.WriteAllText(packFile, pack.ToJsonString());

        var check = await repo.RunAsync(GenerationMode.Check);

        E2ERepo.AssertOutcome(RunOutcome.Drift, check);
        Assert.Equal(["db/e2e/audited/customer.txt", "db/e2e/audited/invoice.txt", "db/e2e/audited/payment.txt"],
            check.Changes.Where(c => c.Kind == FileChangeKind.Deleted).Select(c => c.Path));
        Assert.True(repo.Repo.Exists("db/e2e/audited/customer.txt"));

        // Apply deletes them, and check is clean again.
        var applied = await repo.ApplyAsync();
        Assert.Equal(3, applied.FilesDeleted);
        Assert.False(Directory.Exists(repo.Repo.PathOf("db/e2e/audited")));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
    }

    [Fact]
    public async Task Edits_inside_protected_regions_are_not_drift_and_every_root_is_checked()
    {
        await using var repo = await AppliedAsync();
        var region = repo.Repo.ReadFile("db/e2e/regions/customer.txt").Replace("// default body", "// kept by hand", StringComparison.Ordinal);
        repo.Repo.WriteFile("db/e2e/regions/customer.txt", region);

        var check = await repo.RunAsync(GenerationMode.Check);

        E2ERepo.AssertOutcome(RunOutcome.Succeeded, check);
        Assert.All(check.Changes, c => Assert.Equal(FileChangeKind.Kept, c.Kind));

        // A file under src/Generated (once a "built" root, unchecked) is checked like any other.
        repo.Repo.WriteFile("src/Generated/demo/docs/helpers.txt", "edited\n");
        var edited = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Conflicts, edited);
        Assert.Contains(edited.Changes, c => c.Path == "src/Generated/demo/docs/helpers.txt" && c.Kind == FileChangeKind.Conflict);
    }

    [Fact]
    public async Task Outcome_precedence_is_busy_then_invalid_then_conflicts_then_drift()
    {
        await using var repo = await AppliedAsync();

        // Conflicts (3) wins over drift (2): a hand edit plus a stale file.
        await repo.EditAsync(E2ERepo.CustomerId, n => n["attributes"]![1]!["length"] = 150);
        repo.Repo.WriteFile("db/e2e/entities/product.txt", "edited by hand\n");
        var both = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Conflicts, both);
        Assert.Contains(both.Changes, c => c.Kind == FileChangeKind.Modified);
        Assert.Contains(both.Changes, c => c.Kind == FileChangeKind.Conflict);

        // Invalid (1) wins over conflicts: a model error stops the run before anything renders.
        var customer = repo.Repo.PathOf(".maquettiste/model/entities/customer.json");
        var node = JsonNode.Parse(File.ReadAllText(customer))!;
        node["package"] = "01J92P0V0000000000000000ZZ";
        File.WriteAllText(customer, node.ToJsonString());
        var invalid = await repo.RunAsync(GenerationMode.Check);
        E2ERepo.AssertOutcome(RunOutcome.Invalid, invalid);
        Assert.Empty(invalid.Changes);
        Assert.Contains(invalid.Diagnostics, d => d.Severity == Diagnostics.DiagnosticSeverity.Error && d.ElementId == E2ERepo.CustomerId);

        // Busy (4) wins over everything: another holder of the run lock, with LockMode.Fail.
        await using (var held = HoldRunLock(repo))
        {
            var busy = await repo.RunAsync(GenerationMode.Check, lockMode: LockMode.Fail);
            E2ERepo.AssertOutcome(RunOutcome.Busy, busy);
            Assert.Empty(busy.Changes);
        }
    }

    internal static async Task<E2ERepo> AppliedAsync()
    {
        var repo = E2ERepo.Create();
        await repo.ApplyAsync();
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Check));
        return repo;
    }

    /// <summary>Holds the run lock the way another process would (an exclusive handle on the lock file).</summary>
    internal static FileStream HoldRunLock(E2ERepo repo) =>
        new(repo.Repo.PathOf(".maquettiste/.cache/run.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
}
