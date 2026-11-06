using System.Text;
using System.Text.Json;
using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste expression preview</c> (next-phase-3-design.md section 3.1, "CLI and MCP"): one expression of an element, stored
/// (<c>&lt;element&gt; --at &lt;pointer&gt;</c>) or written as text at a site (<c>--text --on --site</c>), printed as its text, type,
/// nullability, JavaScript and PostgreSQL text (or why there is none), and with <c>--values</c> its answer under the site's mode. It reads
/// that element and what its expression names, as the MCP tool <c>preview_expression</c> does; the model is neither validated nor resolved.
/// </summary>
internal static class ExpressionCommand
{
    /// <summary>Runs <c>expression &lt;verb&gt;</c>.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code: 1 when the expression does not parse or type, or its evaluation fails.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        var line = context.Line;
        var verb = line.Positionals.Count > 1 ? line.Positionals[1] : throw new UsageException("'expression' needs a verb: preview.");
        if (verb != "preview")
            throw new UsageException($"Unknown expression verb '{verb}': use preview.");
        var text = line.Value("--text");
        if (text is null)
        {
            line.Expect("expression preview", 3, "--at", "--database", "--lifecycle", "--values", "--format");
        }
        else
        {
            line.Expect("expression preview", 2, "--text", "--on", "--site", "--database", "--lifecycle", "--values", "--format");
            if (line.Value("--on") is null || line.Value("--site") is null)
                throw new UsageException("--text needs --on <element> and --site <check|value|guard|pointer>.");
        }

        var json = line.Choice("--format", "text", "text", "json") == "json";
        var values = Values(context, line.Value("--values"));
        if (await ProjectGuard.RepoAsync(context).ConfigureAwait(false) is not { } repo)
            return Program.ExitCodes.Invalid;

        var store = new ModelStore(context.EngineOptions(repo));
        await using (store.ConfigureAwait(false))
        {
            await store.LoadAsync(ct).ConfigureAwait(false);
            var element = await ResolveAsync(store, text is null ? line.Positionals[2] : line.Value("--on")!, ct).ConfigureAwait(false);
            ExpressionPreview result;
            try
            {
                result = await store.PreviewExpressionAsync(new ExpressionPreviewRequest
                {
                    Element = element,
                    At = line.Value("--at"),
                    Text = text,
                    Site = line.Value("--site"),
                    Database = line.Value("--database"),
                    Lifecycle = line.Value("--lifecycle"),
                    Values = values,
                }, ct).ConfigureAwait(false);
            }
            catch (ArgumentException e)
            {
                throw new UsageException(e.Message.Split(" (Parameter", 2)[0]);
            }

            if (json)
                await context.Out.WriteLineAsync(JsonSerializer.Serialize(result, CliJson.Options)).ConfigureAwait(false);
            else
                await WriteTextAsync(context, result, values is not null).ConfigureAwait(false);
            return result.Preview is null || result.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? Program.ExitCodes.Invalid : Program.ExitCodes.Success;
        }
    }

    /// <summary>The values of <c>--values</c>: a JSON object, inline or in a file (<c>-</c> for standard input); <see langword="null"/> without.</summary>
    private static JsonElement? Values(GlobalContext context, string? option)
    {
        if (option is null)
            return null;
        string source;
        if (option.TrimStart().StartsWith('{'))
        {
            source = option;
        }
        else if (option == "-")
        {
            var stream = context.Environment.OpenStandardInput?.Invoke() ?? throw new UsageException("standard input is not available; pass the values or a file to --values.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            source = reader.ReadToEnd();
        }
        else
        {
            var path = Path.GetFullPath(option, context.Environment.CurrentDirectory);
            if (!File.Exists(path))
                throw new UsageException($"--values takes a JSON object or a file that holds one; '{option}' is neither.");
            source = File.ReadAllText(path, Encoding.UTF8);
        }

        try
        {
            using var document = JsonDocument.Parse(source);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new UsageException("--values must hold a JSON object: the members the expression reads, by name.");
            return document.RootElement.Clone();
        }
        catch (JsonException e)
        {
            throw new UsageException("--values is not valid JSON: " + e.Message);
        }
    }

    /// <summary>An element by id (a sub-element's too), else by name (exact, then ignoring case); several or none is a usage error.</summary>
    private static async Task<string> ResolveAsync(ModelStore store, string key, CancellationToken ct)
    {
        if (await store.GetElementAsync(key, ct).ConfigureAwait(false) is not null)
            return key;
        var index = await store.GetIndexAsync(ct).ConfigureAwait(false);
        var matches = index.Where(e => string.Equals(e.Name, key, StringComparison.Ordinal)).ToList();
        if (matches.Count == 0)
            matches = [.. index.Where(e => string.Equals(e.Name, key, StringComparison.OrdinalIgnoreCase))];
        return matches.Count == 1 ? matches[0].Id : throw new UsageException(matches.Count == 0
            ? $"no element has the id or name '{key}'."
            : $"'{key}' names {matches.Count} elements; pass one id: {string.Join(", ", matches.Select(m => m.Id + " (" + m.Kind + " " + m.Name + ")"))}.");
    }

    /// <summary>One line per form; the diagnostics go to standard error.</summary>
    private static async Task WriteTextAsync(GlobalContext context, ExpressionPreview result, bool answered)
    {
        if (result.Preview is { } p)
        {
            var text = new StringBuilder()
                .Append("text: ").Append(p.Text).Append('\n')
                .Append("type: ").Append(p.Type).Append('\n')
                .Append("nullable: ").Append(p.Nullable ? "true" : "false").Append('\n')
                .Append("javascript: ").Append(p.JavaScript).Append('\n')
                .Append("postgresql: ").Append(p.Sql ?? "none: " + p.SqlNote).Append('\n');
            if (answered)
                text.Append("answer: ").Append(JsonSerializer.Serialize(p.Answer, CliJson.Options)).Append('\n');
            await context.Out.WriteAsync(text.ToString()).ConfigureAwait(false);
        }

        foreach (var d in result.Diagnostics)
            await context.Error.WriteLineAsync(DiagnosticOutput.Line(d)).ConfigureAwait(false);
    }
}
