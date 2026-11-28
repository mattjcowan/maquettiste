using Maquettiste.Bench.Synthetic;

namespace Maquettiste.Bench.Tests;

/// <summary>
/// Building the synthetic model runs "Create tables…" in memory for every database (SyntheticModel.Tables.cs). It stays roughly linear in the
/// model: a binding field once looked its relation end up by scanning every end of every relation, once per foreign key column.
/// </summary>
public sealed class CreateTablesScaleTests
{
    [Fact]
    public void Create_tables_on_a_few_thousand_entities_stays_roughly_linear()
    {
        _ = SyntheticModel.Build(Options(200)); // compiles the code paths, so the timings below compare work, not first runs

        // The sizes alternate, so a stretch of a busy machine slows both, and each keeps its fastest run. Garbage collection pauses are left
        // out: they grow with the heap the test holds, not with the work Create tables does, and on a small runner they made linear work
        // look 4x. Without them, linear work gives about 3 at these sizes (3.0 measured); the scan of every end gave 5.3 to 6.2 and grows with
        // the model. The bound sits between them with room on both sides.
        var small = TimeSpan.MaxValue;
        var large = TimeSpan.MaxValue;
        for (var run = 0; run < 2; run++)
        {
            small = Min(small, Time(Options(1_000)));
            large = Min(large, Time(Options(3_000)));
        }

        Assert.True(large.TotalMilliseconds < 4.2 * Math.Max(small.TotalMilliseconds, 50),
            $"Create tables took {small.TotalMilliseconds:F0} ms on 1,000 entities and {large.TotalMilliseconds:F0} ms on 3,000 (garbage collection pauses left out)");
    }

    private static SyntheticModelOptions Options(int entities) =>
        new() { Seed = 42, Entities = entities, Relations = entities * 4, Fanout = 1, IncludeExamplePacks = false };

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    /// <summary>One build's Create tables time without the garbage collection pauses within it.</summary>
    private static TimeSpan Time(SyntheticModelOptions options)
    {
        var model = SyntheticModel.Build(options);
        return model.CreateTablesTime - model.CreateTablesGcPause;
    }
}
