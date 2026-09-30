using Maquettiste.Bench.Synthetic;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Bench.Tests;

/// <summary>The synthetic process extension and the <c>time-processes</c> verb (phase-3-design.md section 4.5), at a small size.</summary>
public sealed class SyntheticProcessesTests
{
    private static readonly SyntheticProcessOptions Small = new() { Processes = 6, Large = 1, LargeStates = 120, Scenarios = 12, Steps = 8 };

    [Fact]
    public async Task The_extension_is_deterministic_validates_clean_and_every_scenario_passes()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = Path.Combine(Path.GetTempPath(), "mq-bench-proc-" + Guid.NewGuid().ToString("N"));
        var second = Path.Combine(Path.GetTempPath(), "mq-bench-proc-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = new SyntheticModelOptions { Entities = 20, Relations = 30, Enums = 3, ValueObjects = 2, ScalarTypes = 2, Packages = 2, IncludeExamplePacks = false };
            foreach (var root in new[] { first, second })
            {
                await SyntheticModelGenerator.WriteAsync(root, model, ct);
                await SyntheticProcesses.WriteAsync(root, Small, scenarios: true, ct);
            }

            var files = Directory.GetFiles(Path.Combine(first, ".maquettiste", "model"), "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList();
            Assert.Contains(files, f => f.Contains($"{Path.DirectorySeparatorChar}scenarios{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
            foreach (var file in files)
                Assert.Equal(File.ReadAllBytes(file), File.ReadAllBytes(Path.Combine(second, Path.GetRelativePath(first, file))));

            var store = new ModelStore(new EngineOptions { RepoRoot = first, CacheDirectory = Path.Combine(first, ".cache") });
            await using (store)
            {
                var report = await store.ValidateAsync(new ValidationScope(), ct);
                Assert.Equal(0, report.Errors);
                var snapshot = await store.GetSnapshotAsync(ct);
                Assert.Equal(6, snapshot.All<Process>().Count);
                Assert.Equal(12, snapshot.All<Scenario>().Count);
                Assert.Contains(snapshot.All<Process>(), p => p.States.Sum(s => 1 + s.States.Count) >= 120);
            }
        }
        finally
        {
            foreach (var root in new[] { first, second })
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Time_processes_measures_every_budget_of_section_4_5()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = Path.Combine(Path.GetTempPath(), "mq-bench-time-" + Guid.NewGuid().ToString("N"));
        try
        {
            var model = new SyntheticModelOptions { Entities = 20, Relations = 30, Enums = 3, ValueObjects = 2, ScalarTypes = 2, Packages = 2, IncludeExamplePacks = false };
            var timings = await TimeProcesses.MeasureAsync(root, model, Small, 2, 1, TextWriter.Null, ct);
            Assert.Equal(8, timings.Count);
            Assert.All(timings, t => Assert.True(t.Actual >= 0 && t.Limit > 0, t.Name));
            Assert.Equal(4, await TimeProcesses.RunAsync(["--processes", "none"], TextWriter.Null, TextWriter.Null, ct));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
