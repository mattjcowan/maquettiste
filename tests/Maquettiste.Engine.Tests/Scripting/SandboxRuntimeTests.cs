using System.Collections.Concurrent;
using System.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Scripting;

namespace Maquettiste.Engine.Tests.Scripting;

public sealed class SandboxRuntimeTests
{
    /// <summary>Limits a runaway script cannot reach within the test, so only cancellation can stop it.</summary>
    private static readonly SandboxLimits Unbounded = Scripts.Limits with
    {
        ScriptTimeoutMs = 120_000, ScriptStatements = long.MaxValue, ScriptMemoryBytes = long.MaxValue / 4,
    };

    private const string Runaway = "maquettiste.helper('spin', () => { let i = 0; while (true) { i = (i + 1) % 7; } });";

    [Fact]
    public async Task A_runaway_script_stops_within_one_second_of_call_cancellation()
    {
        using var pool = Scripts.Pool(Runaway, Unbounded);
        using var cts = new CancellationTokenSource();
        var started = new ManualResetEventSlim();
        var run = Task.Run(() =>
        {
            using var lease = pool.Rent();
            started.Set();
            return lease.Sandbox.CallHelper("spin", [], Scripts.CtxWithToken(cts.Token));
        }, TestContext.Current.CancellationToken);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(run.IsCompleted);
        var clock = Stopwatch.StartNew();
        await cts.CancelAsync();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"stopped after {clock.ElapsedMilliseconds} ms");
        Assert.Equal(cts.Token, error.CancellationToken);
    }

    [Fact]
    public async Task A_runaway_script_stops_within_one_second_of_run_cancellation()
    {
        using var runCts = new CancellationTokenSource();
        using var pool = Scripts.PoolWithRunToken(Runaway, Unbounded, runCts.Token);
        var started = new ManualResetEventSlim();
        var run = Task.Run(() =>
        {
            using var lease = pool.Rent();
            started.Set();
            return lease.Sandbox.CallHelper("spin", [], Scripts.Ctx());
        }, TestContext.Current.CancellationToken);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        var clock = Stopwatch.StartNew();
        await runCts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"stopped after {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void A_cancelled_call_does_not_start()
    {
        using var pool = Scripts.Pool("maquettiste.helper('one', () => 1);");
        using var lease = pool.Rent();

        Assert.ThrowsAny<OperationCanceledException>(() => lease.Sandbox.CallHelper("one", [], Scripts.CtxWithToken(new CancellationToken(canceled: true))));
        Assert.ThrowsAny<OperationCanceledException>(() => Scripts.PoolWithRunToken("maquettiste.helper('one', () => 1);", Scripts.Limits, new CancellationToken(canceled: true)));
    }

    private const string Noisy = """
        const state = { calls: 0 };
        maquettiste.helper('noisy', (label) => {
          const values = [Math.random(), Math.random(), Math.random()].map(v => v.toFixed(12));
          const obj = { zeta: 1, alpha: 2, 10: 'ten', 2: 'two' };
          return {
            label,
            now: Date.now(),
            iso: new Date().toISOString(),
            local: new Date(2024, 0, 15, 8, 30).toString(),
            temporal: Temporal.Now.instant().toString(),
            random: values,
            keys: Object.keys(obj),
            sorted: ['b', 'B', 'a', 'é', 'e'].sort(),
            number: (1234567.891).toLocaleString(),
            upper: 'istanbul'.toUpperCase(),
          };
        });
        """;

    private static string Render(object? value) => System.Text.Json.JsonSerializer.Serialize(value);

    [Fact]
    public void The_same_script_gives_the_same_output_twice()
    {
        string RunOnce()
        {
            using var pool = Scripts.Pool(Noisy);
            using var lease = pool.Rent();
            return Render(lease.Sandbox.CallHelper("noisy", ["x"], Scripts.Ctx(seed: "pack/unit:ID1")));
        }

        var first = RunOnce();
        var second = RunOnce();

        Assert.Equal(first, second);
        Assert.Contains("\"now\":946684800000", first, StringComparison.Ordinal);
        Assert.Contains("\"iso\":\"2000-01-01T00:00:00.000Z\"", first, StringComparison.Ordinal);
        Assert.Contains("\"temporal\":\"2000-01-01T00:00:00Z\"", first, StringComparison.Ordinal);
        Assert.Contains("\"keys\":[\"2\",\"10\",\"zeta\",\"alpha\"]", first, StringComparison.Ordinal);
        Assert.Contains("\"upper\":\"ISTANBUL\"", first, StringComparison.Ordinal);
    }

    [Fact]
    public void Math_random_restarts_from_the_seed_at_every_call()
    {
        using var pool = Scripts.Pool("maquettiste.helper('r', () => [Math.random(), Math.random()]);");
        using var lease = pool.Rent();

        var a1 = Render(lease.Sandbox.CallHelper("r", [], Scripts.Ctx(seed: "a")));
        var b = Render(lease.Sandbox.CallHelper("r", [], Scripts.Ctx(seed: "b")));
        var a2 = Render(lease.Sandbox.CallHelper("r", [], Scripts.Ctx(seed: "a")));

        Assert.Equal(a1, a2);
        Assert.NotEqual(a1, b);
        var values = (IReadOnlyList<object?>)lease.Sandbox.CallHelper("r", [], Scripts.Ctx(seed: "a"))!;
        Assert.All(values, v => Assert.InRange((double)v!, 0.0, 1.0));
    }

    [Fact]
    public void Time_zone_dependent_apis_use_utc_whatever_the_host_zone()
    {
        using var pool = Scripts.Pool("""
            maquettiste.helper('zones', () => [
              Intl.DateTimeFormat().resolvedOptions().timeZone,
              new Intl.DateTimeFormat('en-US', { hour: 'numeric', minute: 'numeric' }).format(new Date(0)),
              Intl.DateTimeFormat('en-US', { hour: 'numeric', timeZone: undefined }).format(new Date(0)),
              Temporal.Now.timeZoneId(),
              Temporal.Now.plainDateTimeISO().toString(),
              Temporal.Now.zonedDateTimeISO().toString(),
              new Date(0).toLocaleString('en-US'),
              new Intl.DateTimeFormat() instanceof Intl.DateTimeFormat,
              Intl.DateTimeFormat.prototype.constructor === Intl.DateTimeFormat,
              Intl.DateTimeFormat.length,
              new (class Short extends Intl.DateTimeFormat { tag() { return 'sub'; } })('en-US').tag(),
            ].join(' | '));
            """);

        Assert.Equal(
            "UTC | 12:00 AM | 12 AM | UTC | 2000-01-01T00:00:00 | 2000-01-01T00:00:00+00:00[UTC] | 1/1/1970, 12:00:00 AM | true | true | 0 | sub",
            pool.Helper("zones"));
    }

    [Fact]
    public void Output_does_not_depend_on_the_host_culture()
    {
        string RunUnder(string culture)
        {
            var previous = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo(culture);
                System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo(culture);
                using var pool = Scripts.Pool(Noisy);
                using var lease = pool.Rent();
                return Render(lease.Sandbox.CallHelper("noisy", ["x"], Scripts.Ctx(seed: "s")));
            }
            finally
            {
                System.Globalization.CultureInfo.CurrentCulture = previous;
                System.Globalization.CultureInfo.CurrentUICulture = previous;
            }
        }

        var invariant = RunUnder("");
        Assert.Equal(invariant, RunUnder("tr-TR"));
        Assert.Equal(invariant, RunUnder("de-DE"));
    }

    [Fact]
    public void Parallel_use_rents_distinct_engines_and_matches_sequential_output()
    {
        const int Size = 4;
        // A generous wall-clock limit: under full-suite load a call can wait far longer than the 2 s default for a thread.
        using var pool = Scripts.Pool(Noisy + "\nmaquettiste.helper('square', n => n * n);", Scripts.Limits with { ScriptTimeoutMs = 60_000 }, size: Size);
        var sequential = Enumerable.Range(0, 64).Select(i =>
        {
            using var lease = pool.Rent();
            return Render(lease.Sandbox.CallHelper("noisy", [i], Scripts.Ctx(seed: "u" + i)));
        }).ToArray();
        var parallel = new string[64];
        var inUse = new ConcurrentDictionary<IScriptSandbox, bool>(ReferenceEqualityComparer.Instance);
        var engines = new ConcurrentDictionary<IScriptSandbox, bool>(ReferenceEqualityComparer.Instance);
        var held = 0;
        using var overlapped = new ManualResetEventSlim();

        Parallel.For(0, 64, new ParallelOptions { MaxDegreeOfParallelism = 8 }, i =>
        {
            using var lease = pool.Rent();
            Assert.True(inUse.TryAdd(lease.Sandbox, true), "a sandbox was rented twice at once");
            engines.TryAdd(lease.Sandbox, true);
            // Leases that never overlap in time reuse one engine (the pool hands back the last one returned); iteration 0 keeps its
            // lease until another lease is held at the same time, so the test does not depend on how a busy machine schedules them.
            if (Interlocked.Increment(ref held) >= 2)
                overlapped.Set();
            if (i == 0)
                overlapped.Wait(TimeSpan.FromSeconds(10));
            parallel[i] = Render(lease.Sandbox.CallHelper("noisy", [i], Scripts.Ctx(seed: "u" + i)));
            Assert.Equal((long)i * i, lease.Sandbox.CallHelper("square", [i], Scripts.Ctx()));
            Interlocked.Decrement(ref held);
            Assert.True(inUse.TryRemove(lease.Sandbox, out _));
        });

        Assert.Equal(sequential, parallel);
        Assert.True(engines.Count > 1, "parallel leases shared one engine");
    }

    [Fact]
    public void Every_engine_of_a_pool_registers_the_same_names()
    {
        using var pool = Scripts.Pool("maquettiste.helper('one', () => 1);", size: 2);
        using var a = pool.Rent();
        using var b = pool.Rent();

        Assert.NotSame(a.Sandbox, b.Sandbox);
        Assert.Equal(1L, a.Sandbox.CallHelper("one", [], Scripts.Ctx()));
        Assert.Equal(1L, b.Sandbox.CallHelper("one", [], Scripts.Ctx()));
        Assert.Single(pool.Registrations);
    }

    [Fact]
    public void Leases_beyond_the_pool_size_get_their_own_engine()
    {
        using var pool = Scripts.Pool("maquettiste.helper('one', () => 1);", size: 1);
        using (var a = pool.Rent())
        using (var b = pool.Rent())
        {
            Assert.NotSame(a.Sandbox, b.Sandbox);
            Assert.Equal(1L, b.Sandbox.CallHelper("one", [], Scripts.Ctx()));
        }

        using var again = pool.Rent();
        Assert.Equal(1L, again.Sandbox.CallHelper("one", [], Scripts.Ctx()));
    }

    [Fact]
    public void A_returned_lease_cannot_be_used_and_a_disposed_pool_cannot_rent()
    {
        var pool = Scripts.Pool("maquettiste.helper('one', () => 1);");
        var lease = pool.Rent();
        lease.Dispose();
        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.Sandbox);
        pool.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pool.Rent());
    }
}
