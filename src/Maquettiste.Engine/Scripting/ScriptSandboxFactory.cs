using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Scripting;

/// <summary>Creates pools of sandboxed Jint engines (W4; engine-design.md section 10).</summary>
internal sealed class ScriptSandboxFactory : IScriptSandboxFactory
{
    /// <inheritdoc/>
    /// <exception cref="ScriptErrorException">A script has a syntax error or throws while it loads (MQ6016, or MQ5002 for scripts under <c>extensions/rules/</c>).</exception>
    /// <exception cref="ScriptLimitException">A script exceeds a sandbox limit while it loads (MQ6007, or MQ5003 for rule scripts).</exception>
    public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct) =>
        CreatePool(scripts, [], limits, size, ct);

    /// <inheritdoc/>
    /// <exception cref="ScriptErrorException">A script has a syntax error or throws while it loads (MQ6016, or MQ5002 for scripts under <c>extensions/rules/</c>).</exception>
    /// <exception cref="ScriptLimitException">A script exceeds a sandbox limit while it loads (MQ6007, or MQ5003 for rule scripts).</exception>
    public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, IReadOnlyList<ScriptLibrary> libraries, SandboxLimits limits, int size,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ct.ThrowIfCancellationRequested();
        var prepared = PreparedScript.PrepareAll(scripts, limits);
        IReadOnlyList<Diagnostic> warnings = [.. prepared.Select(p => p.Warning).OfType<Diagnostic>().OrderBy(d => d.FilePath, StringComparer.Ordinal)];
        return new ScriptSandboxPool(prepared, limits, size, ct, warnings, libraries);
    }

    /// <summary>
    /// Creates the one-engine pool of a pack's helper tests (next-phase-2-design.md section 5.4): the pack's scripts load as usual, the
    /// globals freeze, and then each test file loads in its own function scope (the rule-file form), with <c>maquettiste.test</c>
    /// offered only while they load. A test file with a syntax error, or one that fails to load, is a
    /// <see cref="ScriptSandboxPool.TestFailures"/> entry and registers no test; the other files still load. Normal pools never see
    /// test files.
    /// </summary>
    /// <param name="scripts">The pack's scripts, in load order.</param>
    /// <param name="tests">The test files, in load order (ordinal).</param>
    /// <param name="libraries">The packs <c>maquettiste.use</c> reaches: the pack's uses closure.</param>
    /// <param name="limits">The sandbox limits.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The pool.</returns>
    /// <exception cref="ScriptErrorException">A pack script has a syntax error or throws while it loads (MQ6016).</exception>
    /// <exception cref="ScriptLimitException">A pack script exceeds a sandbox limit while it loads (MQ6007).</exception>
    internal static ScriptSandboxPool CreateTestPool(IReadOnlyList<ScriptSource> scripts, IReadOnlyList<ScriptSource> tests,
        IReadOnlyList<ScriptLibrary> libraries, SandboxLimits limits, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(tests);
        ArgumentNullException.ThrowIfNull(libraries);
        ArgumentNullException.ThrowIfNull(limits);
        ct.ThrowIfCancellationRequested();
        var prepared = PreparedScript.PrepareAll(scripts, limits);
        var preparedTests = new List<PreparedScript>(tests.Count);
        var failures = new List<ScriptTestFailure>();
        foreach (var test in tests)
        {
            try
            {
                preparedTests.AddRange(PreparedScript.PrepareAll([test], limits, scoped: true));
            }
            catch (ScriptErrorException e)
            {
                failures.Add(new ScriptTestFailure(test.Path, e.Diagnostic));
            }
        }

        return new ScriptSandboxPool(prepared, limits, 1, ct, [], libraries, preparedTests, failures);
    }
}
