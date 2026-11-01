using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Tests that load every processor on purpose run alone, after the parallel ones, so their spinning threads never starve the
/// wall-clock tests the runner runs at the same time.
/// </summary>
[CollectionDefinition(nameof(CpuLoadCollection), DisableParallelization = true)]
public sealed class CpuLoadCollection;

/// <summary>
/// <c>scriptTimeoutMs: 0</c> turns the sandbox's wall clock off (next-phase-1-design.md section 5.7), so script rules give the same
/// findings on a busy machine as on an idle one: only statements bound them.
/// </summary>
[Collection(nameof(CpuLoadCollection))]
public sealed class RuleDeterminismTests
{
    private const int EntityCount = 6;

    /// <summary>About five million statements per entity, and no regular expression.</summary>
    private const string HeavyRule = """
        const spend = (seed, rounds) => {
          let total = seed;
          for (let i = 0; i < rounds; i++) { total = (total * 31 + i) % 1000003; }
          return total;
        };
        maquettiste.rule({
          id: 'heavy',
          severity: 'warning',
          kinds: ['entity'],
          check(element, model, report) {
            report(`${element.name} spends ${spend(element.name.length, 4800000)}`, { pointer: '/name' });
          },
        });
        """;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_same_model_gives_the_same_diagnostics_under_cpu_load_with_the_clock_off()
    {
        // One validator per run: a validator reuses unchanged results (its rule result cache), and both runs must execute the rules.
        var model = Model(timeoutMs: 0);

        var idle = await ValidationFixture.Validator(scripts: new ScriptSandboxFactory()).ValidateAsync(model, ValidationScope.All, null, Ct);
        ValidationReport loaded;
        using (new Spinners(2 * Environment.ProcessorCount))
            loaded = await ValidationFixture.Validator(scripts: new ScriptSandboxFactory()).ValidateAsync(model, ValidationScope.All, null, Ct);

        Assert.Equal(EntityCount, idle.Diagnostics.Count(d => d.Rule == "x/heavy"));
        Assert.DoesNotContain(idle.Diagnostics, d => d.Rule is "MQ5002" or "MQ5003");
        Assert.Equal(ValidationFixture.ToGolden(idle.Diagnostics), ValidationFixture.ToGolden(loaded.Diagnostics));

        // The same rules under a 1 ms clock break the time limit: the clock is what 0 turned off.
        var clocked = await ValidationFixture.Validator(scripts: new ScriptSandboxFactory()).ValidateAsync(Model(timeoutMs: 1), ValidationScope.All, null, Ct);
        Assert.Equal(EntityCount, clocked.Diagnostics.Count(d => d.Rule == "MQ5003"));
        Assert.DoesNotContain(clocked.Diagnostics, d => d.Rule == "x/heavy");
    }

    private static ModelSnapshot Model(int timeoutMs)
    {
        var builder = new ModelBuilder(seed: 11);
        var billing = builder.Package("Billing");
        foreach (var name in new[] { "Customer", "Invoice", "InvoiceLine", "Payment", "Product", "Subscription" })
            builder.Entity(name, billing).Key("id", "uuid");
        // The memory limit counts every byte a call allocates, and each round allocates its numbers, so it is raised with the statements.
        builder.Settings(s => s with
        {
            Limits = s.Limits with { ScriptTimeoutMs = timeoutMs, ScriptStatements = 20_000_000, ScriptMemoryBytes = 1L << 30 },
        });
        var built = builder.Build();
        var script = new ScriptSource(".maquettiste/extensions/rules/heavy.js", HeavyRule, "heavy-rule");
        return ModelSnapshot.Create(built.Documents, built.Settings, built.SettingsHash, [], [script], 1);
    }

    /// <summary>Threads that spin until disposed, loading every processor.</summary>
    private sealed class Spinners : IDisposable
    {
        private readonly List<Thread> _threads = [];
        private volatile bool _stop;

        public Spinners(int count)
        {
            for (var i = 0; i < count; i++)
            {
                var thread = new Thread(Spin) { IsBackground = true, Name = "cpu-load-" + i };
                _threads.Add(thread);
                thread.Start();
            }
        }

        public void Dispose()
        {
            _stop = true;
            foreach (var thread in _threads)
                thread.Join();
        }

        private void Spin()
        {
            var x = 1UL;
            while (!_stop)
                x = (x * 6364136223846793005UL) + 1442695040888963407UL;
            GC.KeepAlive(x);
        }
    }
}
