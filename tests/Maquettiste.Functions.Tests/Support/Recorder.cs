using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Functions.Tests.Support;

/// <summary>
/// Writes real responses to <c>src/editor/src/mocks/recorded/</c> for the editor's mock layer (phase2-design.md sections 3.9 and 4.4)
/// when <c>MAQUETTISTE_RECORD=1</c>; otherwise does nothing.
/// </summary>
internal static class Recorder
{
    /// <summary>Whether recording is on.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("MAQUETTISTE_RECORD") == "1";

    /// <summary>The folder recordings go to.</summary>
    public static string Folder => Path.Combine(Fixtures.RepoRoot, "src", "editor", "src", "mocks", "recorded");

    /// <summary>Records a JSON response, indented, with LF line ends and a final newline.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="response">The response.</param>
    public static void Json(string name, TestResponse response)
    {
        if (!Enabled)
            return;
        var text = JsonNode.Parse(response.Body)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Write(name, text.Replace("\r\n", "\n", StringComparison.Ordinal) + "\n");
    }

    /// <summary>Records a text response as it came.</summary>
    /// <param name="name">The file name.</param>
    /// <param name="response">The response.</param>
    public static void Text(string name, TestResponse response)
    {
        if (Enabled)
            Write(name, response.Text);
    }

    private static void Write(string name, string text)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, name), text, new UTF8Encoding(false));
    }
}
