using System.Collections.Concurrent;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Scripting;

/// <summary>
/// A pool of sandboxed engines that ran the same prepared scripts (engine-design.md section 10): one engine per worker, rented for
/// a whole unit. The first engine is built eagerly, so script load errors surface from <see cref="ScriptSandboxFactory.CreatePool"/>;
/// the others are built on demand. More leases than <c>size</c> get extra engines, which are dropped on return rather than kept.
/// </summary>
internal sealed class ScriptSandboxPool : IScriptSandboxPool
{
    private readonly IReadOnlyList<PreparedScript> _scripts;
    private readonly SandboxLimits _limits;
    private readonly MemberCatalog _catalog = new();
    private readonly CancellationToken _runToken;
    private readonly int _size;
    private readonly ConcurrentStack<ScriptSandbox> _idle = new();
    private int _disposed;

    /// <summary>Creates a pool and its first engine.</summary>
    /// <param name="scripts">The prepared scripts, in load order.</param>
    /// <param name="limits">The limits.</param>
    /// <param name="size">The number of engines kept.</param>
    /// <param name="runToken">The run's cancellation, wired into every engine.</param>
    public ScriptSandboxPool(IReadOnlyList<PreparedScript> scripts, SandboxLimits limits, int size, CancellationToken runToken)
    {
        _scripts = scripts;
        _limits = limits;
        _size = size;
        _runToken = runToken;
        var first = ScriptSandbox.Create(_scripts, _limits, _catalog, _runToken);
        Registrations = first.Registrations;
        _idle.Push(first);
    }

    /// <inheritdoc/>
    public IReadOnlyList<ScriptRegistration> Registrations { get; }

    /// <inheritdoc/>
    public IScriptSandboxLease Rent()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var sandbox = _idle.TryPop(out var idle) ? idle : ScriptSandbox.Create(_scripts, _limits, _catalog, _runToken);
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
