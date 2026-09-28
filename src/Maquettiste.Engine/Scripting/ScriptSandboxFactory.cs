using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Scripting;

/// <summary>Creates pools of sandboxed Jint engines (W4; engine-design.md section 10).</summary>
internal sealed class ScriptSandboxFactory : IScriptSandboxFactory
{
    /// <inheritdoc/>
    /// <exception cref="ScriptErrorException">A script has a syntax error or throws while it loads (MQ6016, or MQ5002 for scripts under <c>extensions/rules/</c>).</exception>
    /// <exception cref="ScriptLimitException">A script exceeds a sandbox limit while it loads (MQ6007, or MQ5003 for rule scripts).</exception>
    public IScriptSandboxPool CreatePool(IReadOnlyList<ScriptSource> scripts, SandboxLimits limits, int size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);
        ct.ThrowIfCancellationRequested();
        return new ScriptSandboxPool(PreparedScript.PrepareAll(scripts, limits), limits, size, ct);
    }
}
