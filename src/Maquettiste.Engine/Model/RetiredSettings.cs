using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Model;

/// <summary>
/// Settings members that earlier releases accepted and this one ignores. Since 0.5.5 an <c>outputs.allow</c> entry is only a path:
/// its <c>commit</c> flag (committed versus built roots, spec-errata E42) is gone. A <c>maquettiste.json</c> that still has it keeps
/// loading: the flag is dropped before the schema check, each entry that has one is reported with MQ1010 (info), and
/// <c>maquettiste format</c>, the settings save and every other settings write drop it.
/// </summary>
internal static class RetiredSettings
{
    /// <summary>The rule of a retired member.</summary>
    public const string Rule = "MQ1010";

    /// <summary>The indexes of the <c>outputs.allow</c> entries that set <c>commit</c>.</summary>
    /// <param name="root">The settings document.</param>
    /// <returns>The indexes, ascending.</returns>
    public static IReadOnlyList<int> CommitEntries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Object
            || !outputs.TryGetProperty("allow", out var allow) || allow.ValueKind != JsonValueKind.Array)
            return [];
        var found = new List<int>();
        var i = 0;
        foreach (var entry in allow.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("commit", out _))
                found.Add(i);
            i++;
        }

        return found;
    }

    /// <summary>One MQ1010 diagnostic per entry that sets <c>commit</c>.</summary>
    /// <param name="root">The settings document.</param>
    /// <param name="repoPath">The repo path of <c>maquettiste.json</c>.</param>
    /// <returns>The diagnostics.</returns>
    public static IReadOnlyList<Diagnostic> Findings(JsonElement root, string repoPath) =>
    [
        .. CommitEntries(root).Select(i => RuleCatalog.Create(Rule,
            "`commit` is ignored since 0.5.5; remove it (maquettiste format drops it): every output root is a folder generation may write under, and which outputs to commit is the team's choice.",
            null, repoPath, "/outputs/allow/" + i.ToString(CultureInfo.InvariantCulture) + "/commit")),
    ];

    /// <summary>Removes <c>commit</c> from every <c>outputs.allow</c> entry of a settings document.</summary>
    /// <param name="node">The document.</param>
    /// <returns>Whether anything was removed.</returns>
    public static bool Strip(JsonNode? node)
    {
        if (node is not JsonObject root || root["outputs"] is not JsonObject outputs || outputs["allow"] is not JsonArray allow)
            return false;
        var removed = false;
        foreach (var entry in allow)
        {
            if (entry is JsonObject obj && obj.Remove("commit"))
                removed = true;
        }

        return removed;
    }
}
