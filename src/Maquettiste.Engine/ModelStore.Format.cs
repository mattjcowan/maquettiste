using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine;

/// <summary>The result of <see cref="ModelStore.FormatAsync"/>.</summary>
/// <param name="Formatted">The repo-relative paths rewritten in canonical form, ordinal.</param>
/// <param name="Skipped">The files left as they are because they are not valid enough to rewrite safely (not JSON, no known kind, a
/// schema error), ordinal; validate gives the reasons.</param>
/// <param name="Refused">The requested paths that are not model files (outside the model folder, not <c>maquettiste.json</c>, an
/// element file or a locale shard, or missing), as given; nothing is written when any is refused.</param>
/// <param name="Total">The model files considered.</param>
public sealed record ModelFormatResult(IReadOnlyList<string> Formatted, IReadOnlyList<string> Skipped, IReadOnlyList<string> Refused, int Total);

public sealed partial class ModelStore
{
    /// <summary>
    /// Rewrites model files in canonical form (SPEC section 11), as <c>maquettiste format</c> does: <c>maquettiste.json</c> (which also
    /// loses the retired <c>commit</c> flag, MQ1010), the element files under <c>model/</c> and the locale shards. A file already in
    /// canonical form is not touched; a file that is not valid enough to rewrite is left as it is and listed in
    /// <see cref="ModelFormatResult.Skipped"/>. The rewritten files are staged and renamed into place together under the store's write
    /// gate, then reloaded, so subscribers get one change set; a format changes no content, only bytes.
    /// </summary>
    /// <param name="paths">Repo-relative (or model-relative) paths of the files to rewrite; <see langword="null"/> for every model file.</param>
    /// <param name="source">What caused the change.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What was rewritten, skipped or refused.</returns>
    public async Task<ModelFormatResult> FormatAsync(IReadOnlyList<string>? paths, ChangeSource source, CancellationToken ct)
    {
        await LoadedAsync(ct).ConfigureAwait(false);
        var model = _paths.Value;
        var targets = new SortedSet<string>(StringComparer.Ordinal);
        var refused = new List<string>();
        if (paths is null)
        {
            if (Directory.Exists(model.ModelRoot))
            {
                foreach (var full in Directory.EnumerateFiles(model.ModelRoot, "*.json", SearchOption.AllDirectories))
                {
                    var modelPath = Path.GetRelativePath(model.ModelRoot, full).Replace('\\', '/');
                    if (Formattable(modelPath) is not null)
                        targets.Add(modelPath);
                }
            }
        }
        else
        {
            foreach (var path in paths)
            {
                var modelPath = path is null ? null : model.ToModelPath(path);
                if (modelPath is null || Formattable(modelPath) is null || !File.Exists(model.FullPath(modelPath)))
                    refused.Add(path ?? "");
                else
                    targets.Add(modelPath);
            }
        }

        if (refused.Count > 0)
            return new ModelFormatResult([], [], refused, targets.Count);

        var notifications = new List<ChangeSet>();
        var formatted = new List<string>();
        var skipped = new List<string>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var writes = new List<(string Path, byte[] Bytes)>();
            foreach (var modelPath in targets)
            {
                ct.ThrowIfCancellationRequested();
                var repoPath = model.ToRepoPath(modelPath);
                var full = model.FullPath(modelPath);
                byte[] bytes;
                try
                {
                    bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                var canonical = ModelFormatter.Canonical(_services.Schemas, _services.Json, bytes, Formattable(modelPath)!.Value, repoPath);
                if (canonical is null)
                    skipped.Add(repoPath);
                else if (!canonical.AsSpan().SequenceEqual(bytes))
                {
                    writes.Add((full, canonical));
                    formatted.Add(repoPath);
                }
            }

            if (writes.Count > 0)
            {
                var batchId = Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + "-" + Interlocked.Increment(ref _batchCounter).ToString(CultureInfo.InvariantCulture);
                var failure = await new AtomicFileSet(_services.EnginePaths, model.ModelRoot).ApplyAsync(writes, [], batchId, ct).ConfigureAwait(false);
                if (failure is not null)
                    throw new IOException($"The model files could not be rewritten ({failure.Path}); nothing was changed. {failure.Reason}");

                // The files are on disk: index them even if the caller has given up.
                var changes = await ReloadAsync(formatted, false, source, CancellationToken.None).ConfigureAwait(false);
                if (!changes.IsEmpty)
                    notifications.Add(changes);
            }
        }
        finally
        {
            _gate.Release();
        }

        foreach (var changeSet in notifications)
            await NotifyAsync(changeSet, ct).ConfigureAwait(false);
        return new ModelFormatResult(formatted, skipped, [], targets.Count);
    }

    private static ModelFileKind? Formattable(string modelPath) =>
        ModelPaths.Classify(modelPath) is { } kind && kind is ModelFileKind.Settings or ModelFileKind.Element or ModelFileKind.LocaleShard ? kind : null;
}

/// <summary>The canonical bytes of one model file, shared by <see cref="ModelStore.FormatAsync"/> and <c>maquettiste format</c>.</summary>
internal static class ModelFormatter
{
    /// <summary>The canonical bytes of a model file, or <see langword="null"/> when it is not valid enough to rewrite safely.</summary>
    /// <param name="schemas">The schema registry.</param>
    /// <param name="json">The canonical writer.</param>
    /// <param name="bytes">The file.</param>
    /// <param name="kind">The file kind: settings, element or locale shard.</param>
    /// <param name="repoPath">The repo-relative path.</param>
    /// <returns>The canonical bytes.</returns>
    public static byte[]? Canonical(ISchemaRegistry schemas, ICanonicalJson json, byte[] bytes, ModelFileKind kind, string repoPath)
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
