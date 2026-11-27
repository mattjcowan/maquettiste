using Maquettiste.Testing;

namespace Maquettiste.Bench.Tests;

/// <summary>
/// The bench's synthetic model at a small size, every projected entity stored with "Create tables…" (materialize-tables): the sql-ddl
/// pack writes the same table scripts byte for byte and no migration (retire-projection-design.md section 7). The synthetic model has
/// no reference types, so seed.sql must not change either. It starts from the projected model (<c>Projected</c>), as the generator
/// wrote it before 0.29.0; the generator itself now stores the tables the same way (temporary: pull request 3 deletes this test).
/// </summary>
public sealed class MaterializeShapeTests
{
    [Fact]
    public async Task The_synthetic_model_keeps_its_ddl_when_every_entity_gets_a_designed_table()
    {
        var ct = TestContext.Current.CancellationToken;
        using var temp = new TempFolder();
        var repo = Path.Combine(temp.Path, "repo");
        await SyntheticModelGenerator.WriteAsync(repo, BenchTestModels.Small() with { Projected = true }, ct);

        var proof = await MaterializeProof.RunAsync(temp.Options(repo), "db", ct);

        Assert.NotEmpty(proof.Materialized);
        Assert.True(proof.Errors.Count == 0, proof.Describe());
        Assert.True(proof.OtherFacts.Count == 0, string.Join("\n", proof.OtherFacts));
        Assert.True(proof.Projected.Count == 0, "Still projected: " + string.Join(", ", proof.Projected));
        Assert.Equal(proof.MigrationsBefore, proof.MigrationsAfter);
        Assert.True(proof.Changed.Count == 0, proof.Describe());
    }
}
