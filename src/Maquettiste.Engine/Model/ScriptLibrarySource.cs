using System.Text.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A pack whose helpers the project's scripts may call, as the model loader read it (library packs, next-phase-2-design.md
/// section 5.2): a pack of the closure of <c>maquettiste.json</c>'s <c>uses</c>.
/// </summary>
/// <param name="Pack">The pack name (its folder).</param>
/// <param name="Version">The version its <c>pack.json</c> declares.</param>
/// <param name="Uses">The names in its own <c>uses</c>, in <c>pack.json</c> order.</param>
/// <param name="Parameters">Its effective parameters: the manifest's defaults overridden by <c>packs.&lt;name&gt;.parameters</c>.</param>
/// <param name="Scripts">Its scripts, in load order, with repo-relative paths.</param>
/// <param name="Hash">The hash over its name, version, uses, canonical parameters and scripts.</param>
public sealed record ScriptLibrarySource(string Pack, string Version, IReadOnlyList<string> Uses,
    IReadOnlyDictionary<string, JsonElement> Parameters, IReadOnlyList<ScriptSource> Scripts, string Hash);
