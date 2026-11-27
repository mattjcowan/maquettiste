using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

/// <summary>
/// Every model the tests and the samples ship loads with each id held once (no MQ1004): the loader's id table, which an edit patches rather
/// than rebuilds, and the resolver's lookups assume it. A validation family that tests duplicate ids on purpose names MQ1004 in its
/// <c>expected.json</c> and is left out.
/// </summary>
public sealed class FixtureIdTests
{
    public static TheoryData<string> Models()
    {
        var data = new TheoryData<string>();
        var roots = Directory.EnumerateDirectories(Path.Combine(Fixtures.Root, "models"))
            .Concat(Directory.EnumerateDirectories(Path.Combine(Fixtures.Root, "validation")))
            .Where(d => File.Exists(Path.Combine(d, ".maquettiste", "maquettiste.json")))
            .Where(d => !File.Exists(Path.Combine(d, "expected.json")) || !File.ReadAllText(Path.Combine(d, "expected.json")).Contains("\"MQ1004\"", StringComparison.Ordinal))
            .Select(d => Path.GetRelativePath(Fixtures.RepoRoot, d).Replace('\\', '/'))
            .Append("samples/reference-app")
            .Order(StringComparer.Ordinal);
        foreach (var root in roots)
            data.Add(root);
        return data;
    }

    [Theory]
    [MemberData(nameof(Models))]
    public async Task The_model_loads_with_every_id_held_once(string root)
    {
        var cache = Directory.CreateTempSubdirectory("maquettiste-fixture-ids-").FullName;
        try
        {
            var options = new EngineOptions { RepoRoot = Path.Combine(Fixtures.RepoRoot, root), CacheDirectory = cache };
            var store = new ModelStore(options);
            await using (store.ConfigureAwait(false))
            {
                var snapshot = await store.GetSnapshotAsync(TestContext.Current.CancellationToken);
                Assert.NotEmpty(snapshot.Documents);
                Assert.DoesNotContain(snapshot.LoadDiagnostics, d => d.Rule == "MQ1004");
                Assert.Equal(snapshot.Documents.Count, snapshot.Documents.Select(d => d.Element.Id).Distinct(StringComparer.Ordinal).Count());
            }
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
