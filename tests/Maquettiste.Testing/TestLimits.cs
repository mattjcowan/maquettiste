using System.Text.Json.Nodes;

namespace Maquettiste.Testing;

/// <summary>
/// Sandbox limits for test repositories, so that a test's outcome does not depend on how busy the machine is; a test about a limit
/// sets its own. The fixtures that run process expressions set <c>limits.scriptTimeoutMs</c> to 0 themselves
/// (tests/fixtures/models/README.md).
/// </summary>
public static class TestLimits
{
    /// <summary>
    /// <c>limits.scriptTimeoutMs</c> for a repository that previews: a minute per script call and a preview deadline of four
    /// minutes (4 × the timeout). 0 would turn the scripts' clock off but leave a preview its 8-second deadline, which a cold
    /// preview on a busy runner has run past (MQ6007).
    /// </summary>
    public const int PreviewScriptTimeoutMs = 60_000;

    /// <summary>
    /// Sets <see cref="PreviewScriptTimeoutMs"/> in the <c>maquettiste.json</c> under a model root, keeping its other settings, in
    /// canonical form. For a copy of a fixture that previews and that the product shares (the editor's mocks and the MCP guide copy
    /// billing), so the fixture itself keeps the defaults.
    /// </summary>
    /// <param name="modelRoot">The <c>.maquettiste</c> folder.</param>
    public static void ForPreviews(string modelRoot)
    {
        var path = Path.Combine(modelRoot, "maquettiste.json");
        var settings = JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
        if (settings["limits"] is not JsonObject limits)
            settings["limits"] = limits = [];
        limits["scriptTimeoutMs"] = PreviewScriptTimeoutMs;
        File.WriteAllBytes(path, TestServices.Json.Write(settings, "maquettiste.json", "maquettiste.json"));
    }
}
