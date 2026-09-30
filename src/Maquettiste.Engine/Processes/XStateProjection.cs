using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Processes;

/// <summary>Options of an XState export (phase-3-design.md section 5.1).</summary>
public sealed record XStateExportOptions
{
    /// <summary>Resolves a process id to its name, for the <c>src</c> of a sub-process invoke; the id is written when it returns null.</summary>
    public Func<string, string?>? ProcessName { get; init; }

    /// <summary>Options that resolve process names from a model.</summary>
    /// <param name="model">The model.</param>
    /// <returns>The options.</returns>
    public static XStateExportOptions For(ModelSnapshot model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new() { ProcessName = id => model.Get<Process>(id)?.Name };
    }
}

/// <summary>The result of an XState export.</summary>
/// <param name="Json">The XState config: canonical JSON (fixed key order, two-space indent, trailing newline).</param>
/// <param name="Diagnostics">MQ9406 (what went into <c>meta</c>) and MQ9404 (data that found no place).</param>
public sealed record XStateExport(string Json, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Options of an XState import (phase-3-design.md section 5.2).</summary>
public sealed record XStateImportOptions
{
    /// <summary>The model the process joins: actor ids it lacks are dropped (MQ9404), ids of other kinds or processes are refused
    /// (MQ9405), and a <c>src</c> naming one of its processes becomes a sub-process invoke. Null skips these checks.</summary>
    public ModelSnapshot? Model { get; init; }

    /// <summary>The process re-imported over (<c>into</c>): its id is kept, and nodes the config does not identify keep the ids and
    /// descriptive fields of the nodes they match by path (states), by name (events, guards, actions, invokes) or by source,
    /// trigger and position (transitions).</summary>
    public Process? Into { get; init; }

    /// <summary>The package (domain) of the process; overrides what the config carries.</summary>
    public string? Package { get; init; }

    /// <summary>The process name; overrides the config's machine id.</summary>
    public string? Name { get; init; }

    /// <summary>The use; overrides what the config carries.</summary>
    public ProcessUse? Use { get; init; }

    /// <summary>The subject entity; overrides what the config carries.</summary>
    public string? Subject { get; init; }

    /// <summary>The id generator: one id seeds the import, and every id the import creates derives from that seed and the node's
    /// path in the config, so one path always gets one id within an import and another import gets fresh ids. Null means ULIDs.</summary>
    public IIdGenerator? Ids { get; init; }

    /// <summary>The repo-relative path the process file is written for (it sets the <c>$schema</c> reference).</summary>
    public string DocumentPath { get; init; } = ".maquettiste/model/processes/process.json";
}

/// <summary>The result of an XState import.</summary>
/// <param name="File">The canonical process file, or null when the input was refused (MQ9403).</param>
/// <param name="Process">The process record read from <paramref name="File"/>, or null when refused.</param>
/// <param name="Diagnostics">MQ9401 to MQ9405, with JSON pointers into the config.</param>
/// <param name="Created">The ids the import created (nodes that carried no id and matched nothing in <c>into</c>), in creation order.</param>
/// <param name="Removed">With <c>into</c>: ids of the process's nodes the imported document no longer has, ordinal.</param>
public sealed record XStateImport(byte[]? File, Process? Process, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Created,
    IReadOnlyList<string> Removed)
{
    /// <summary>Whether any diagnostic is an error: the import must not be applied.</summary>
    public bool HasErrors => Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
}

/// <summary>
/// The XState v5 projection of a process (phase-3-design.md section 5): <see cref="Export"/> writes a machine config and
/// <see cref="Import"/> reads one back. Everything with no XState home travels in <c>meta.maquettiste</c>, so a process exported
/// then imported gives the same canonical file, and two exports of one process give the same bytes.
/// </summary>
public static class XStateProjection
{
    /// <summary>The media format name.</summary>
    public const string Format = "xstate";

    /// <summary>The key under <c>meta</c> that carries the model data.</summary>
    public const string MetaKey = "maquettiste";

    /// <summary>
    /// The fixed key order of every config object (the section 5.1 table's order); keys not listed (kept opaque data) follow in the
    /// order they were written back, and <c>meta</c> is always last.
    /// </summary>
    public static IReadOnlyList<string> KeyOrder { get; } =
    [
        "id", "src", "initial", "states", "context", "type", "history", "target", "entry", "exit", "on", "after", "always", "onDone",
        "onError", "invoke", "guard", "actions", "reenter",
    ];

    internal const string DefaultPath = ".maquettiste/model/processes/process.json";

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        IndentCharacter = ' ',
        IndentSize = 2,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Exports a process to an XState v5 machine config.</summary>
    /// <param name="process">The process.</param>
    /// <param name="json">The canonical writer (the process is read in canonical form, defaults omitted).</param>
    /// <param name="options">Options; null resolves no process names.</param>
    /// <returns>The config and the export diagnostics.</returns>
    public static XStateExport Export(Process process, ICanonicalJson json, XStateExportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(json);
        var document = JsonNode.Parse(json.Serialize(process, "process.json", DefaultPath))!.AsObject();
        var (config, diagnostics) = new XStateExporter(document, options ?? new()).Run();
        return new XStateExport(Write(config), diagnostics);
    }

    /// <summary>Imports an XState machine config as a process document.</summary>
    /// <param name="config">The config text (JSON).</param>
    /// <param name="json">The canonical writer.</param>
    /// <param name="options">Options; null imports a new process with no model checks.</param>
    /// <returns>The document, the diagnostics and the ids created or removed.</returns>
    public static XStateImport Import(string config, ICanonicalJson json, XStateImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(json);
        return new XStateImporter(json, options ?? new()).Run(config);
    }

    /// <summary>Writes a config node as canonical text: two-space indent, <c>\n</c> line ends, a trailing newline.</summary>
    /// <param name="node">The node.</param>
    /// <returns>The text.</returns>
    internal static string Write(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            node.WriteTo(writer);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan) + "\n";
    }

    internal static Diagnostic Finding(string rule, string message, string? pointer, string? elementId = null) =>
        new(rule, RuleCatalog.Get(rule).DefaultSeverity, message, elementId, null, pointer, null, null);

    /// <summary>Milliseconds of an ISO 8601 duration (a year 365 days, a month 30 days), or null when it is not one.</summary>
    internal static long? Milliseconds(string? duration) =>
        StatechartModel.ParseDuration(duration) is { } span ? (long)span.TotalMilliseconds : null;

    /// <summary>The ISO 8601 duration of a number of milliseconds, in days, hours, minutes and seconds (<c>PT0S</c> for zero).</summary>
    internal static string Duration(long milliseconds)
    {
        var ms = Math.Max(0, milliseconds);
        var days = ms / 86_400_000;
        ms %= 86_400_000;
        var hours = ms / 3_600_000;
        ms %= 3_600_000;
        var minutes = ms / 60_000;
        ms %= 60_000;
        var text = new System.Text.StringBuilder("P");
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (days > 0)
            text.Append(days.ToString(inv)).Append('D');
        if (hours > 0 || minutes > 0 || ms > 0 || days == 0)
        {
            text.Append('T');
            if (hours > 0)
                text.Append(hours.ToString(inv)).Append('H');
            if (minutes > 0)
                text.Append(minutes.ToString(inv)).Append('M');
            if (ms > 0 || (hours == 0 && minutes == 0))
            {
                var seconds = ms / 1000m;
                text.Append(seconds.ToString("0.###", inv)).Append('S');
            }
        }

        return text.ToString();
    }
}
