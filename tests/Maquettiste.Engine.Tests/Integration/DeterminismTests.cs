using System.Globalization;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Determinism (integration task 6; engine-design.md section 17): the billing fixture generated with every pack into separate temp
/// repos with one job and with eight, under the invariant culture, tr-TR and de-DE, gives byte-identical output trees, manifests and
/// schema snapshots.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DeterminismTests
{
    [Fact]
    public async Task One_job_and_eight_jobs_under_any_culture_give_byte_identical_trees_and_manifests()
    {
        var reference = await GenerateAsync(jobs: 1, culture: "");
        Assert.True(reference.Count > 70, "files: " + reference.Count);
        foreach (var (jobs, culture) in new[] { (8, "tr-TR"), (8, "de-DE"), (1, "tr-TR"), (8, "") })
        {
            var tree = await GenerateAsync(jobs, culture);
            Assert.Equal(reference.Keys, tree.Keys);
            foreach (var (path, sha) in reference)
                Assert.True(sha == tree[path], $"{path} differs with {jobs} jobs under '{culture}'");
        }
    }

    [Fact]
    public async Task The_pipeline_reproduces_the_renderer_golden_tree()
    {
        // The billing-demo golden tree was produced by the renderer alone (tests/fixtures/templates/golden); the full pipeline
        // (planner, post-processing, writer) must write the same bytes.
        await using var repo = E2ERepo.Create();
        await repo.ApplyAsync(jobs: 4);
        var differences = Golden.Compare(Fixtures.Path("templates", "golden", "billing-demo", "out"), repo.Repo.PathOf("src/Generated/demo"));
        Assert.Empty(differences);
    }

    private static async Task<SortedDictionary<string, string>> GenerateAsync(int jobs, string culture)
    {
        var (previous, previousUi) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture.Length == 0 ? CultureInfo.InvariantCulture : new CultureInfo(culture);
        try
        {
            await using var repo = E2ERepo.Create(parallelism: jobs, migrations: true);
            await repo.ApplyAsync(jobs: jobs);
            var tree = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, bytes) in repo.Tree())
                tree[path] = E2ERepo.Sha(bytes);
            return tree;
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }
}
