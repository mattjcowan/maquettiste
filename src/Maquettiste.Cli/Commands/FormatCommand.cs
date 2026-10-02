using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Cli.Commands;

/// <summary>
/// <c>maquettiste format</c>: rewrites every model file in canonical form (SPEC section 11), the form the editor and the engine
/// write, so hand-written files stop reporting MQ1003 (and <c>maquettiste.json</c> loses the retired <c>commit</c> flag, MQ1010). It covers <c>maquettiste.json</c>, the element files under <c>model/</c>
/// and the locale shards. A file that is not valid JSON, has no known kind or fails its schema is left as it is and listed (run
/// <c>maquettiste validate</c> for the reasons). <c>--check</c> writes nothing, lists the files that would change and exits 2 (drift, as
/// <c>generate --check</c>) when there is any. Exit 0 otherwise, and 1 when there is no model.
/// </summary>
internal static class FormatCommand
{
    /// <summary>The exit code of <c>--check</c> when a file would change: drift, as in <c>generate --check</c>.</summary>
    public const int WouldChange = Program.ExitCodes.Drift;

    /// <summary>Runs the command.</summary>
    /// <param name="context">The global context.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(GlobalContext context, CancellationToken ct)
    {
        context.Line.Expect("format", 1, "--check");
        var check = context.Line.Has("--check");
        var repo = context.RepoRoot();
        var modelRoot = Path.Combine(repo, GlobalContext.ModelFolder);
        if (!File.Exists(Path.Combine(modelRoot, ModelPaths.SettingsFile)))
        {
            await context.Error.WriteLineAsync($"maquettiste: no model found: {Path.Combine(modelRoot, ModelPaths.SettingsFile)} does not exist (run maquettiste init).").ConfigureAwait(false);
            return Program.ExitCodes.Invalid;
        }

        var schemas = new SchemaRegistry();
        var json = new CanonicalJson(schemas);
        var files = new GuardedFiles(new OutputPathPolicy(context.EngineOptions(repo), null));
        var changed = new List<string>();
        var skipped = new List<string>();
        var total = 0;
        foreach (var path in Directory.EnumerateFiles(modelRoot, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var modelPath = Path.GetRelativePath(modelRoot, path).Replace('\\', '/');
            var kind = ModelPaths.Classify(modelPath);
            if (kind is not (ModelFileKind.Settings or ModelFileKind.Element or ModelFileKind.LocaleShard))
                continue;
            total++;
            var repoPath = GlobalContext.ModelFolder + "/" + modelPath;
            var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            var canonical = Canonical(schemas, json, bytes, kind.Value, repoPath);
            if (canonical is null)
            {
                skipped.Add(repoPath);
                continue;
            }

            if (canonical.AsSpan().SequenceEqual(bytes))
                continue;
            changed.Add(repoPath);
            if (!check)
                await files.WriteAsync(WriteTarget.Model, path, canonical, overwrite: true, ct).ConfigureAwait(false);
        }

        foreach (var path in changed)
            await context.Out.WriteLineAsync((check ? "would format " : "formatted ") + path).ConfigureAwait(false);
        foreach (var path in skipped)
            await context.Error.WriteLineAsync($"maquettiste: {path} was left as it is: it is not a valid model file (run maquettiste validate).").ConfigureAwait(false);
        var files1 = total == 1 ? "file" : "files";
        context.Info(check
            ? string.Create(CultureInfo.InvariantCulture, $"Format check: {changed.Count} of {total} model {files1} would change.")
            : string.Create(CultureInfo.InvariantCulture, $"Formatted {changed.Count} of {total} model {files1}."));
        return check && changed.Count > 0 ? WouldChange : Program.ExitCodes.Success;
    }

    /// <summary>The canonical bytes of a model file, or <see langword="null"/> when it is not valid enough to rewrite safely.</summary>
    /// <param name="schemas">The schema registry.</param>
    /// <param name="json">The canonical writer.</param>
    /// <param name="bytes">The file.</param>
    /// <param name="kind">The file kind.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <returns>The canonical bytes.</returns>
    internal static byte[]? Canonical(SchemaRegistry schemas, CanonicalJson json, byte[] bytes, ModelFileKind kind, string repoPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
        }
        catch (JsonException)
        {
            return null;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            string schemaFile;
            switch (kind)
            {
                case ModelFileKind.Settings:
                    schemaFile = ModelPaths.SettingsFile;
                    break;
                case ModelFileKind.LocaleShard:
                    schemaFile = DocumentReader.LocaleShardSchema;
                    break;
                default:
                    if (!root.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String || !KindInfo.TryGet(kindValue.GetString()!, out var info))
                        return null;
                    schemaFile = info.SchemaFile;
                    break;
            }

            var node = JsonNode.Parse(root.GetRawText())!;
            if (kind == ModelFileKind.Settings && RetiredSettings.Strip(node))
            {
                // outputs.allow[].commit (ignored since 0.5.5, MQ1010) is dropped; the rest must still pass the schema.
                using var stripped = JsonDocument.Parse(node.ToJsonString());
                return schemas.Evaluate(schemaFile, stripped.RootElement, repoPath).Any(d => d.Severity == DiagnosticSeverity.Error)
                    ? null
                    : json.Write(node, schemaFile, repoPath);
            }

            if (schemas.Evaluate(schemaFile, root, repoPath).Any(d => d.Severity == DiagnosticSeverity.Error))
                return null;
            return json.Write(node, schemaFile, repoPath);
        }
    }
}
