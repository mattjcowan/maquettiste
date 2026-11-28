using Maquettiste.Engine;
using Maquettiste.Testing;

namespace Maquettiste.Bench.Tests;

/// <summary>
/// The store's incremental whole-model validation on a small synthetic model (change-driven-edits-design.md section 4, step 2): after
/// every edit of the standard sequence and of a sweep over the elements, the report equals a fresh whole-model validation of the same
/// snapshot, finding by finding and in order; and a generation run after an edit validates incrementally.
/// </summary>
public sealed class IncrementalValidationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_edit_of_a_synthetic_model_gives_the_whole_model_report()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await SyntheticModelGenerator.WriteAsync(repo, BenchTestModels.Small() with { IncludeExamplePacks = false }, Ct);
        await using var check = new IncrementalValidationCheck(temp.Options(repo));
        await check.RunStandardEditsAsync(Ct);
        await check.RunSweepAsync(4, Ct);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Join("\n", check.Log));
        Assert.True(check.Incremental > check.Whole, string.Join("\n", check.Log));
    }

    [Fact]
    public async Task A_generation_run_after_the_benchmark_edit_validates_incrementally()
    {
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        var model = BenchTestModels.Small() with { IncludeExamplePacks = false };
        await SyntheticModelGenerator.WriteAsync(repo, model, Ct);
        var options = temp.Options(repo);
        var store = new ModelStore(options);
        await using (store.ConfigureAwait(false))
        {
            var service = new GenerationService(store, options);
            Assert.Equal(RunOutcome.Succeeded, (await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct)).Outcome);
            await SyntheticModelGenerator.EditOneEntityAsync(repo, model, Ct);
            var edited = await service.RunAsync(new GenerationRequest { Jobs = 2 }, null, Ct);
            Assert.Equal(RunOutcome.Succeeded, edited.Outcome);
            var (incremental, validated, documents) = await IncrementalValidationCheck.SharedValidationAsync(store, options, Ct);
            Assert.True(incremental);
            Assert.True(validated < documents / 2, $"{validated} of {documents}");
        }
    }
}
