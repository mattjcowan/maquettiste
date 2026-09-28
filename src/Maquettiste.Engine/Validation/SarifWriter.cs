using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Writes SARIF 2.1.0 (W2): tool <c>maquettiste</c>, rules from <see cref="RuleCatalog"/> plus the <c>x/</c> ids seen, results in
/// diagnostic order, <c>artifactLocation { uri, uriBaseId: "%SRCROOT%" }</c>, a region when the line is known, and
/// <c>properties { elementId, jsonPointer }</c>.
/// </summary>
public static class SarifWriter
{
    private const string SchemaUri = "https://docs.oasis-open.org/sarif/sarif/v2.1.0/errata01/os/schemas/sarif-schema-2.1.0.json";

    private const int FlushThreshold = 16 * 1024;

    /// <summary>
    /// Writes a SARIF log asynchronously (a <see cref="System.Text.Json.Utf8JsonWriter"/> flushed with <c>FlushAsync</c>), so it can
    /// stream straight to a response body that refuses synchronous I/O.
    /// </summary>
    /// <param name="output">The stream to write UTF-8 JSON to.</param>
    /// <param name="diagnostics">The diagnostics, in order.</param>
    /// <param name="toolVersion">The tool version.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>A task that completes when the log is written and flushed.</returns>
    public static async Task WriteAsync(Stream output, IReadOnlyList<Diagnostic> diagnostics, string toolVersion, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(toolVersion);
        ct.ThrowIfCancellationRequested();

        var rules = RuleTable(diagnostics);
        var ruleIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < rules.Count; i++)
            ruleIndex[rules[i].Id] = i;

        var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, NewLine = "\n" });
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteString("$schema", SchemaUri);
            writer.WriteString("version", "2.1.0");
            writer.WriteStartArray("runs");
            writer.WriteStartObject();

            writer.WriteStartObject("tool");
            writer.WriteStartObject("driver");
            writer.WriteString("name", "maquettiste");
            writer.WriteString("version", toolVersion);
            writer.WriteStartArray("rules");
            foreach (var rule in rules)
            {
                writer.WriteStartObject();
                writer.WriteString("id", rule.Id);
                writer.WriteStartObject("shortDescription");
                writer.WriteString("text", rule.Description);
                writer.WriteEndObject();
                writer.WriteStartObject("defaultConfiguration");
                writer.WriteString("level", Level(rule.DefaultSeverity));
                writer.WriteEndObject();
                writer.WriteEndObject();
                await FlushIfNeededAsync(writer, ct).ConfigureAwait(false);
            }

            writer.WriteEndArray();
            writer.WriteEndObject(); // driver
            writer.WriteEndObject(); // tool

            writer.WriteStartObject("originalUriBaseIds");
            writer.WriteStartObject("%SRCROOT%");
            writer.WriteStartObject("description");
            writer.WriteString("text", "The repository root.");
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteStartArray("results");
            foreach (var d in diagnostics)
            {
                ct.ThrowIfCancellationRequested();
                WriteResult(writer, d, ruleIndex);
                await FlushIfNeededAsync(writer, ct).ConfigureAwait(false);
            }

            writer.WriteEndArray();
            writer.WriteEndObject(); // run
            writer.WriteEndArray();
            writer.WriteEndObject();
            await writer.FlushAsync(ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async ValueTask FlushIfNeededAsync(Utf8JsonWriter writer, CancellationToken ct)
    {
        if (writer.BytesPending >= FlushThreshold)
            await writer.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>The <c>rules[]</c> table: every catalog rule in id order, then the <c>x/</c> ids seen, ordinal.</summary>
    private static List<RuleInfo> RuleTable(IReadOnlyList<Diagnostic> diagnostics)
    {
        var rules = new List<RuleInfo>(RuleCatalog.All);
        var custom = new SortedDictionary<string, DiagnosticSeverity>(StringComparer.Ordinal);
        foreach (var d in diagnostics)
        {
            if (!RuleCatalog.TryGet(d.Rule, out _))
                custom.TryAdd(d.Rule, d.Severity);
        }

        foreach (var (id, severity) in custom)
            rules.Add(new RuleInfo(id, severity, id.StartsWith("x/", StringComparison.Ordinal) ? "JavaScript validation rule " + id[2..] + "." : "Rule " + id + "."));
        return rules;
    }

    private static void WriteResult(Utf8JsonWriter writer, Diagnostic d, Dictionary<string, int> ruleIndex)
    {
        writer.WriteStartObject();
        writer.WriteString("ruleId", d.Rule);
        writer.WriteNumber("ruleIndex", ruleIndex[d.Rule]);
        writer.WriteString("level", Level(d.Severity));
        writer.WriteStartObject("message");
        writer.WriteString("text", d.Message);
        writer.WriteEndObject();

        if (d.FilePath is { Length: > 0 } path)
        {
            writer.WriteStartArray("locations");
            writer.WriteStartObject();
            writer.WriteStartObject("physicalLocation");
            writer.WriteStartObject("artifactLocation");
            writer.WriteString("uri", EscapePath(path));
            writer.WriteString("uriBaseId", "%SRCROOT%");
            writer.WriteEndObject();
            if (d.Line is { } line)
            {
                writer.WriteStartObject("region");
                writer.WriteNumber("startLine", line);
                if (d.Column is { } column)
                    writer.WriteNumber("startColumn", column);
                writer.WriteEndObject();
            }

            writer.WriteEndObject(); // physicalLocation
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        if (d.ElementId is not null || d.JsonPointer is not null)
        {
            writer.WriteStartObject("properties");
            if (d.ElementId is not null)
                writer.WriteString("elementId", d.ElementId);
            if (d.JsonPointer is not null)
                writer.WriteString("jsonPointer", d.JsonPointer);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static string Level(DiagnosticSeverity severity) => severity switch
    {
        DiagnosticSeverity.Error => "error",
        DiagnosticSeverity.Warning => "warning",
        _ => "note",
    };

    /// <summary>Percent-encodes each segment of a repo-relative path for a relative URI reference.</summary>
    private static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));
}
