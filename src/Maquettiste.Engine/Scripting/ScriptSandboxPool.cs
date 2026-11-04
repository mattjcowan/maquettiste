using System.Collections.Concurrent;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Scripting;

/// <summary>
/// A pool of sandboxed engines that ran the same prepared scripts (engine-design.md section 10): one engine per worker, rented for
/// a whole unit. The first engine is built eagerly, so script load errors surface from <c>ScriptSandboxFactory.CreatePool</c>;
/// the others are built on demand. More leases than <c>size</c> get extra engines, which are dropped on return rather than kept.
/// </summary>
internal sealed class ScriptSandboxPool : IScriptSandboxPool
{
    private readonly IReadOnlyList<PreparedScript> _scripts;
    private readonly SandboxLimits _limits;
    private readonly MemberCatalog _catalog = new();
    private readonly CancellationToken _runToken;
    private readonly int _size;
    private readonly IReadOnlyList<ScriptLibrary> _libraries;
    private readonly IReadOnlyList<PreparedScript>? _tests;
    private readonly ConcurrentStack<ScriptSandbox> _idle = new();
    private int _disposed;

    /// <summary>Creates a pool and its first engine.</summary>
    /// <param name="scripts">The prepared scripts, in load order.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="size">The number of engines kept.</param>
    /// <param name="runToken">The run's cancellation, wired into every engine.</param>
    /// <param name="warnings">The scripts' load warnings (MQ5005), ordinal by path.</param>
    /// <param name="libraries">The packs <c>maquettiste.use</c> reaches (library packs); none by default.</param>
    /// <param name="tests">Helper test files, prepared scoped (a test pool, next-phase-2-design.md section 5.4); none by default.</param>
    /// <param name="testFailures">The test files that did not prepare (a syntax error), left out of <paramref name="tests"/>.</param>
    public ScriptSandboxPool(IReadOnlyList<PreparedScript> scripts, SandboxLimits limits, int size, CancellationToken runToken,
        IReadOnlyList<Diagnostic>? warnings = null, IReadOnlyList<ScriptLibrary>? libraries = null, IReadOnlyList<PreparedScript>? tests = null,
        IReadOnlyList<ScriptTestFailure>? testFailures = null)
    {
        _libraries = libraries ?? [];
        _scripts = scripts;
        _tests = tests;
        _limits = limits;
        _size = size;
        _runToken = runToken;
        Warnings = warnings ?? [];
        var first = ScriptSandbox.Create(_scripts, _limits, _catalog, _runToken, _libraries, _tests);
        Registrations = first.Registrations;
        Tests = first.Tests;
        TestFailures = [.. (testFailures ?? []).Concat(first.TestFailures).OrderBy(f => f.File, StringComparer.Ordinal)];
        _idle.Push(first);
    }

    /// <summary>The helper tests the test files registered (a test pool); empty otherwise.</summary>
    internal IReadOnlyList<ScriptTest> Tests { get; }

    /// <summary>The test files that did not prepare or load, ordinal by file.</summary>
    internal IReadOnlyList<ScriptTestFailure> TestFailures { get; }

    /// <summary>
    /// Runs one helper test (<see cref="Tests"/>) on an engine of the pool; an engine a limit faulted is dropped. An engine built again
    /// whose test files did not register the test at its index (a file that failed to load this time) does not run it: the result is
    /// <see cref="ScriptTestResult.Malformed"/>.
    /// </summary>
    /// <param name="index">The test's index.</param>
    /// <param name="ctx">The call context.</param>
    /// <returns>The result.</returns>
    internal ScriptTestResult RunTest(int index, ScriptCallContext ctx)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Tests.Count);
        var sandbox = _idle.TryPop(out var idle) ? idle : ScriptSandbox.Create(_scripts, _limits, _catalog, _runToken, _libraries, _tests);
        try
        {
            var test = Tests[index];
            if (!sandbox.HasTest(index, test))
            {
                return new ScriptTestResult(false, $"its engine was built again after a limit and its test files no longer register it at index {index}; a test file must register the same tests on every load.",
                    test.File, null, null, Malformed: true);
            }

            return sandbox.RunTest(index, ctx);
        }
        finally
        {
            Return(sandbox);
        }
    }

    /// <inheritdoc/>
    public IReadOnlyList<ScriptRegistration> Registrations { get; }

    /// <inheritdoc/>
    public IReadOnlyList<Diagnostic> Warnings { get; }

    /// <inheritdoc/>
    public IScriptSandboxLease Rent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var sandbox = _idle.TryPop(out var idle) ? idle : ScriptSandbox.Create(_scripts, _limits, _catalog, _runToken, _libraries, _tests);
        return new Lease(this, sandbox);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Drain();
    }

    private void Return(ScriptSandbox sandbox)
    {
        if (Volatile.Read(ref _disposed) != 0 || sandbox.Faulted || _idle.Count >= _size)
        {
            sandbox.Dispose();
            return;
        }

        _idle.Push(sandbox);
        if (Volatile.Read(ref _disposed) != 0)
            Drain();
    }

    private void Drain()
    {
        while (_idle.TryPop(out var sandbox))
            sandbox.Dispose();
    }

    private sealed class Lease(ScriptSandboxPool pool, ScriptSandbox sandbox) : IScriptSandboxLease
    {
        private int _returned;

        public IScriptSandbox Sandbox
        {
            get
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _returned) != 0, this);
                return sandbox;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _returned, 1) == 0)
                pool.Return(sandbox);
        }
    }
}

/// <summary>
/// A sandbox pool built on its first rent, so that a failure to load its scripts (a limit, or a script that throws) is that rent's
/// <see cref="ScriptLimitException"/> or <see cref="ScriptErrorException"/>, which the caller reports as it reports a call's, rather
/// than an exception where the pool is opened. A failed build is not kept: the next rent tries again. Thread-safe: one thread builds,
/// and the reads after the build take no lock.
/// </summary>
/// <param name="open">Builds the pool.</param>
internal sealed class LazySandboxPool(Func<IScriptSandboxPool> open) : IDisposable
{
    private readonly Lock _gate = new();
    private IScriptSandboxPool? _pool;
    private bool _disposed;

    /// <summary>Rents an engine, building the pool first when no rent has yet.</summary>
    /// <returns>The lease.</returns>
    public IScriptSandboxLease Rent()
    {
        var pool = Volatile.Read(ref _pool);
        if (pool is null)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                pool = _pool;
                if (pool is null)
                {
                    pool = open();
                    Volatile.Write(ref _pool, pool);
                }
            }
        }

        return pool.Rent();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _pool?.Dispose();
        }
    }
}
