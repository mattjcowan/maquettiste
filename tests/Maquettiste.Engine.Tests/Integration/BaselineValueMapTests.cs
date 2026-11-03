using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Snapshots;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Value maps against a baseline (next-phase-2-design.md section 3.5, task D54): <c>validate --against</c> asks for the value map of
/// every code and name changed since the baseline in the databases that hold the type (MQ4112), and judges a value map stale when the
/// baseline's type held none of its codes and its names did not change (MQ4103), which <c>migration fold</c> removes. Each test copies
/// billing with the shared setup (<see cref="PaymentTerms"/>) into a temporary folder.
/// </summary>
public sealed class BaselineValueMapTests
{
    private const string Net90 = "01KAPT00000000000000000014";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<E2ERepo> SetupAsync()
    {
        var repo = PaymentTerms.Create();
        await PaymentTerms.AddAsync(repo);
        return repo;
    }

    private static async Task<string> SnapshotAsync(E2ERepo repo) =>
        (await new SnapshotLibrary(repo.Store).CreateAsync(new SnapshotCreateRequest("Base"), Ct)).Id;

    private static async Task<ValidationReport> AgainstAsync(E2ERepo repo, string against)
    {
        using var baseline = await ModelBaseline.OpenAsync(repo.Store, against, repo.Repo.RepoRoot, allowFolders: true, library: null, Ct);
        return await repo.Store.ValidateAgainstAsync(baseline, Ct);
    }

    private static async Task<FoldResult> FoldAsync(E2ERepo repo, string? against)
    {
        using var baseline = against is null ? null : await ModelBaseline.OpenAsync(repo.Store, against, repo.Repo.RepoRoot, allowFolders: true, library: null, Ct);
        return await repo.Store.FoldMigrationsAsync(new FoldRequest(null, baseline, false, ChangeSource.Editor), Ct);
    }

    /// <summary>Edits a document's file on disk (canonical bytes) and restarts the store, as a hand edit outside the tools would.</summary>
    private static async Task EditOnDiskAsync(E2ERepo repo, string id, string schema, Action<JsonObject> edit)
    {
        var path = (await repo.Store.GetElementAsync(id, Ct))!.Path;
        var node = JsonNode.Parse(repo.Repo.ReadFile(path))!.AsObject();
        edit(node);
        repo.Repo.WriteFile(path, System.Text.Encoding.UTF8.GetString(TestServices.Json.Write(node, schema, path)));
        await repo.RestartAsync();
    }

    /// <summary>Deletes the files of the given facts and restarts the store, as a copy that never recorded them would hold.</summary>
    private static async Task DropFactsAsync(E2ERepo repo, IEnumerable<RecordedFact> facts)
    {
        foreach (var fact in facts)
            File.Delete(repo.Repo.PathOf(fact.Path));
        await repo.RestartAsync();
    }

    private static string Errors(ValidationReport report) =>
        string.Join("\n", report.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Rule} {d.ElementId} {d.JsonPointer} {d.Message}"));

    [Fact]
    public async Task A_code_changed_since_the_baseline_without_a_map_is_MQ4112()
    {
        await using var repo = await SetupAsync();
        var against = await SnapshotAsync(repo);

        // net60 becomes net-60 in the seed file on disk: main stores the codes (invoices.payment_term), and nothing maps them.
        await EditOnDiskAsync(repo, PaymentTerms.PaymentTermSeed, "seed.json", seed => seed["rows"]![1]!["values"]![0] = "net-60");

        var report = await AgainstAsync(repo, against);
        var finding = Assert.Single(report.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("MQ4112", PaymentTerms.PaymentTermSeed, "/rows/1/values/0"), (finding.Rule, finding.ElementId, finding.JsonPointer));
        Assert.Contains("from 'net60' to 'net-60'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("database 'main'", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.Line);

        // The same change through a save carries MQ4114, which points at the refactor.
        await EditOnDiskAsync(repo, PaymentTerms.PaymentTermSeed, "seed.json", seed => seed["rows"]![1]!["values"]![0] = "net60");
        var saved = await repo.EditAsync(PaymentTerms.PaymentTermSeed, seed => seed["rows"]![1]!["values"]![0] = "net-60");
        Assert.Equal("/rows/1/values/0", Assert.Single(saved.Diagnostics, d => d.Rule == "MQ4114").JsonPointer);
    }

    [Fact]
    public async Task A_former_code_added_since_the_baseline_is_explained_by_its_map()
    {
        await using var repo = await SetupAsync();
        var against = await SnapshotAsync(repo);

        // The refactors record their value maps in main: the renamed row's cell and the retired row's former code are explained.
        var renamed = await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "net30", "to": "net-30" }""");
        var retired = await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "retire-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "due", "to": "net60" }""");
        Assert.Equal(0, (await AgainstAsync(repo, against)).Errors);

        // A copy without the facts: the cell of the renamed row, and the former code the retired row left, each need theirs in main.
        await DropFactsAsync(repo, renamed.Facts!.Concat(retired.Facts!));
        var findings = (await AgainstAsync(repo, against)).Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.All(findings, f => Assert.Equal("MQ4112", f.Rule));
        Assert.Equal([(PaymentTerms.PaymentTerm, "/formerCodes/0"), (PaymentTerms.PaymentTermSeed, "/rows/0/values/0")],
            findings.Select(f => (f.ElementId!, f.JsonPointer!)).Order());
        Assert.Contains("'due'", findings.Single(f => f.ElementId == PaymentTerms.PaymentTerm).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_renamed_since_the_baseline_needs_its_names_fact()
    {
        await using var repo = await SetupAsync();
        var against = await SnapshotAsync(repo);

        var plain = await repo.EditAsync(PaymentTerms.PaymentTerm, t => t["name"] = "PaymentCondition");
        Assert.Contains(plain.Diagnostics, d => d.Rule == "MQ4114");
        var finding = Assert.Single((await AgainstAsync(repo, against)).Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal(("MQ4112", PaymentTerms.PaymentTerm, "/name"), (finding.Rule, finding.ElementId, finding.JsonPointer));
        Assert.Contains("database 'main'", finding.Message, StringComparison.Ordinal);

        // Named back, nothing changed; renamed by the refactor, its fact explains it.
        await repo.EditAsync(PaymentTerms.PaymentTerm, t => t["name"] = "PaymentTerm");
        Assert.Equal(0, (await AgainstAsync(repo, against)).Errors);
        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-reference-type", "id": "{{PaymentTerms.PaymentTerm}}", "name": "PaymentCondition" }""");
        var report = await AgainstAsync(repo, against);
        Assert.True(report.Errors == 0, Errors(report));
    }

    /// <summary>Adds the row net90 after the baseline and renames it to net-90 through the refactor, which records a value map in main.</summary>
    private static async Task<RecordedFact> BornAfterAsync(E2ERepo repo)
    {
        await repo.EditAsync(PaymentTerms.PaymentTermSeed, seed => seed["rows"]!.AsArray().Add(JsonNode.Parse($$"""{ "id": "{{Net90}}", "values": ["net90", "Net 90"] }""")));
        var renamed = await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "net90", "to": "net-90" }""");
        return Assert.Single(renamed.Facts!);
    }

    [Fact]
    public async Task A_value_map_for_a_code_born_after_the_baseline_is_stale_and_fold_removes_it()
    {
        await using var repo = await SetupAsync();
        var against = await SnapshotAsync(repo);
        var fact = await BornAfterAsync(repo);

        // No database built from the baseline stores net90: the map is stale, and nothing asks for it (the row is new, its former code
        // was no code there).
        var report = await AgainstAsync(repo, against);
        Assert.True(report.Errors == 0, Errors(report));
        var stale = Assert.Single(report.Diagnostics, d => d.Rule == "MQ4103");
        Assert.Equal((DiagnosticSeverity.Warning, fact.Id, "/fact"), (stale.Severity, stale.ElementId, stale.JsonPointer));
        Assert.Contains("'net90'", stale.Message, StringComparison.Ordinal);

        var folded = await FoldAsync(repo, against);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        Assert.Equal([fact.Id], folded.RemovedStale);
        Assert.False(File.Exists(repo.Repo.PathOf(fact.Path)));
        var after = await AgainstAsync(repo, against);
        Assert.True(after.Errors == 0, Errors(after));
        Assert.DoesNotContain(after.Diagnostics, d => d.Rule == "MQ4103");
    }

    [Fact]
    public async Task Without_a_baseline_model_no_value_map_is_stale()
    {
        await using var repo = await SetupAsync();
        await BornAfterAsync(repo);
        var held = Assert.Single(await PaymentTerms.ValueMapsAsync(repo, E2ERepo.MainDatabaseId));

        // A committed physical snapshot holds no rows: the value map, which it does not list as rendered, is judged by nothing.
        repo.Repo.WriteFile(".maquettiste/snapshots/main.json", $$"""
            {
              "database": "{{E2ERepo.MainDatabaseId}}",
              "name": "main",
              "dialect": "postgresql",
              "revision": 1
            }

            """);
        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(report.Diagnostics, d => d.Rule == "MQ4103");
        var folded = await FoldAsync(repo, null);
        Assert.Equal(SaveOutcome.Saved, folded.Outcome);
        Assert.Empty(folded.RemovedStale);
        Assert.Equal(held.Id, Assert.Single(await PaymentTerms.ValueMapsAsync(repo, E2ERepo.MainDatabaseId)).Id);

        // A value map of a code the baseline held is not stale against it either.
        var against = await SnapshotAsync(repo);
        await PaymentTerms.ApplyAsync(repo, $$"""{ "op": "rename-code", "id": "{{PaymentTerms.PaymentTerm}}", "code": "net30", "to": "net-30" }""");
        Assert.DoesNotContain((await AgainstAsync(repo, against)).Diagnostics, d => d.Rule == "MQ4103");
    }
}
