using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The store's incremental whole-model validation (change-driven-edits-design.md section 4, step 2): after every edit of the standard
/// sequence (<see cref="IncrementalValidationCheck.RunStandardEditsAsync"/>), the report worked out from the previous snapshot's equals a
/// fresh whole-model validation of the same snapshot, finding by finding and in order; edits of settings, packages and an entity's base
/// validate the whole model.
/// </summary>
public sealed class IncrementalValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Models() =>
    [
        "tests/fixtures/models/billing/.maquettiste",
        "samples/reference-app/.maquettiste",
        "tests/fixtures/models/showcase/.maquettiste",
        "tests/fixtures/models/reference-data/.maquettiste",
        "tests/fixtures/models/processes/.maquettiste",
        "tests/fixtures/models/bindings/.maquettiste",
    ];

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Every_edit_gives_the_whole_model_report(string model)
    {
        using var repo = new TempRepo();
        IncrementalValidationCheck.CopyModel(repo.ModelRoot, Path.Combine(Fixtures.RepoRoot, model));
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.RunStandardEditsAsync(Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", check.Log));
        Assert.True(check.Incremental >= 10, string.Join("\n", check.Log));
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task A_sweep_of_edits_gives_the_whole_model_report(string model)
    {
        using var repo = new TempRepo();
        IncrementalValidationCheck.CopyModel(repo.ModelRoot, Path.Combine(Fixtures.RepoRoot, model));
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.RunSweepAsync(12, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", check.Log));
        Assert.True(check.Incremental > check.Whole, string.Join("\n", check.Log));
    }

    [Fact]
    public async Task A_change_the_store_has_not_validated_yet_validates_the_whole_model_next()
    {
        using var repo = new TempRepo();
        IncrementalValidationCheck.CopyModel(repo.ModelRoot, Fixtures.Path("models", "billing", ".maquettiste"));
        await using var check = new IncrementalValidationCheck(repo.Options);
        await check.CheckAsync("load", false, Ct);
        var entity = check.Snapshot.All<Engine.Model.Entity>().First(e => e.Attributes.Count > 1);

        // Two edits in a row, the first never validated: the second has nothing to build on.
        await check.EditAsync(entity.Id, n => n["attributes"]![1]!["name"] = "first", Ct);
        Assert.NotNull(check.Store.LineageOf(check.Snapshot));
        await check.EditAsync(entity.Id, n => n["attributes"]![1]!["name"] = "second", Ct);
        Assert.Null(check.Store.LineageOf(check.Snapshot));
        await check.CheckAsync("second edit", false, Ct);
        await check.EditAsync(entity.Id, n => n["attributes"]![1]!["name"] = "third", Ct);
        await check.CheckAsync("third edit", true, Ct);
    }
}
