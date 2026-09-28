using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Cli;

/// <summary>Text and JSON renderings of diagnostics (<c>schemas/v1/diagnostics.json</c> for JSON).</summary>
internal static class DiagnosticOutput
{
    /// <summary>JSON writer options shared by every JSON result: indented, LF, relaxed escaping.</summary>
    public static readonly JsonWriterOptions WriterOptions = new() { Indented = true, NewLine = "\n", Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>One line per diagnostic: <c>path(line,col): error MQ1002: message [element]</c>.</summary>
    /// <param name="d">The diagnostic.</param>
    /// <returns>The line.</returns>
    public static string Line(Diagnostic d)
    {
        var location = d.FilePath is { Length: > 0 } path
            ? path + (d.Line is { } line ? "(" + line.ToString(CultureInfo.InvariantCulture) + (d.Column is { } column ? "," + column.ToString(CultureInfo.InvariantCulture) : "") + ")" : "") + ": "
            : "";
        var severity = d.Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        };
        var element = d.ElementId is { } id && d.FilePath is null ? " [" + id + "]" : "";
        var pointer = d.JsonPointer is { Length: > 0 } p && d.Line is null ? " at " + p : "";
        return location + severity + " " + d.Rule + ": " + d.Message + pointer + element;
    }

    /// <summary>The severity counts line: <c>2 errors, 1 warning, 0 infos</c>.</summary>
    /// <param name="errors">Errors.</param>
    /// <param name="warnings">Warnings.</param>
    /// <param name="infos">Infos.</param>
    /// <returns>The text.</returns>
    public static string Counts(int errors, int warnings, int infos) =>
        string.Create(CultureInfo.InvariantCulture, $"{errors} error{(errors == 1 ? "" : "s")}, {warnings} warning{(warnings == 1 ? "" : "s")}, {infos} info{(infos == 1 ? "" : "s")}");

    /// <summary>Writes one diagnostic as a JSON object in the <c>diagnostics.json</c> key order; absent values are omitted.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="d">The diagnostic.</param>
    public static void Write(Utf8JsonWriter writer, Diagnostic d)
    {
        writer.WriteStartObject();
        writer.WriteString("rule", d.Rule);
        writer.WriteString("severity", d.Severity switch
        {
            DiagnosticSeverity.Error => "error",
            DiagnosticSeverity.Warning => "warning",
            _ => "info",
        });
        writer.WriteString("message", d.Message);
        if (d.ElementId is not null)
            writer.WriteString("elementId", d.ElementId);
        if (d.FilePath is not null)
            writer.WriteString("filePath", d.FilePath);
        if (d.JsonPointer is not null)
            writer.WriteString("jsonPointer", d.JsonPointer);
        if (d.Line is { } line)
            writer.WriteNumber("line", line);
        if (d.Column is { } column)
            writer.WriteNumber("column", column);
        writer.WriteEndObject();
    }

    /// <summary>Writes a diagnostics array property.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="name">The property name.</param>
    /// <param name="diagnostics">The diagnostics.</param>
    public static void WriteArray(Utf8JsonWriter writer, string name, IEnumerable<Diagnostic> diagnostics)
    {
        writer.WriteStartArray(name);
        foreach (var d in diagnostics)
            Write(writer, d);
        writer.WriteEndArray();
    }

    /// <summary>Serializes JSON through a callback into a string (LF, no trailing newline).</summary>
    /// <param name="write">Writes the document.</param>
    /// <returns>The JSON text.</returns>
    public static string ToJson(Action<Utf8JsonWriter> write)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
            write(writer);
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }
}
