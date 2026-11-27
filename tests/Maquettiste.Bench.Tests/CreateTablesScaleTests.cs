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
        var small = Time(Options(1_000));
        var large = Time(Options(3_000));
        // Linear work gives about 3 (2.7 measured); the scan of every end gave 4.1 at these sizes and grows with the model. The bound leaves
        // room for a busy machine and garbage collection.
        Assert.True(large.TotalMilliseconds < 4 * Math.Max(small.TotalMilliseconds, 50),
            $"Create tables took {small.TotalMilliseconds:F0} ms on 1,000 entities and {large.TotalMilliseconds:F0} ms on 3,000");
    }

    private static SyntheticModelOptions Options(int entities) =>
        new() { Seed = 42, Entities = entities, Relations = entities * 4, Fanout = 1, IncludeExamplePacks = false };

    /// <summary>The faster of two runs, so one stall of the machine does not decide the result.</summary>
    private static TimeSpan Time(SyntheticModelOptions options)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            var time = SyntheticModel.Build(options).CreateTablesTime;
            if (time < best)
                best = time;
        }

        return best;
    }
}
