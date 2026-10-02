using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine;

/// <summary>One file of the model's <c>extensions/</c> folder (<c>GET /api/extensions/files</c>).</summary>
/// <param name="Path">The path under <c>extensions/</c>: <c>&lt;name&gt;.json</c> or <c>rules/&lt;name&gt;.js</c>.</param>
/// <param name="Kind"><c>schema</c> (custom properties, <c>extensions/*.json</c>) or <c>rule</c> (a script rule, <c>extensions/rules/*.js</c>).</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Hash">The content hash (the expected hash of a write, delete or move).</param>
/// <param name="Diagnostics">What is wrong with the file on its own: an invalid schema (MQ5004), a rule script that does not load (MQ5002, MQ5003).</param>
/// <param name="Rules">For a rule script that loads, the rules it registers; empty for a schema.</param>
public sealed record ExtensionFileInfo(string Path, string Kind, long Size, string Hash, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ExtensionRuleInfo> Rules);

/// <summary>One rule a script registers.</summary>
/// <param name="Id">The rule id as findings carry it: <c>x/&lt;id&gt;</c>.</param>
/// <param name="Severity">The severity the rule declares (<c>validation.rules</c> can override it).</param>
public sealed record ExtensionRuleInfo(string Id, DiagnosticSeverity Severity);

/// <summary>The body of <c>GET /api/extensions/files</c>.</summary>
/// <param name="Folder">The repo-relative folder the paths are under (<c>.maquettiste/extensions</c>).</param>
/// <param name="Files">The schemas, then the rules, each ordinal by path.</param>
public sealed record ExtensionFileList(string Folder, IReadOnlyList<ExtensionFileInfo> Files);

/// <summary>One extension file's text with its hash.</summary>
/// <param name="Path">The path under <c>extensions/</c>.</param>
/// <param name="Kind"><c>schema</c> or <c>rule</c>.</param>
/// <param name="Hash">The content hash.</param>
/// <param name="Text">The UTF-8 text.</param>
public sealed record ExtensionFileContent(string Path, string Kind, string Hash, string Text);

/// <summary>The body of <c>POST /api/extensions/file/move</c>.</summary>
/// <param name="From">The source path under <c>extensions/</c>.</param>
/// <param name="To">The target path (must not exist, and of the same kind: a schema stays a schema, a rule a rule).</param>
public sealed record ExtensionFileMove(string? From, string? To);

/// <summary>The result of an extension file write, delete or move.</summary>
/// <param name="Outcome"><c>saved</c>, <c>conflict</c> (the hash changed; <paramref name="Hash"/> and <paramref name="Current"/> are the disk
/// version), <c>invalid</c> (nothing written) or <c>not-found</c>.</param>
/// <param name="Hash">The new hash when saved (null after a delete); the disk hash on conflict.</param>
/// <param name="Current">The disk text on conflict, else null.</param>
/// <param name="Text">The text written when saved: a schema is written in canonical form, so it may differ from the text sent.</param>
/// <param name="Diagnostics">Why it was refused; for a saved rule script, its syntax and load check (MQ5002, MQ5003 with line and column).</param>
/// <param name="Files">The paths written or deleted.</param>
public sealed record ExtensionWriteResult(SaveOutcome Outcome, string? Hash, string? Current, string? Text, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<string> Files);

/// <summary>A path the request may not name under <c>extensions/</c>.</summary>
/// <param name="message">Why.</param>
public sealed class ExtensionPathException(string message) : ArgumentException(message);

public sealed partial class ModelStore
{
    /// <summary>The largest extension file the store writes.</summary>
    public const int MaxExtensionFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding ExtensionUtf8 = new(false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"^(?:[A-Za-z0-9][A-Za-z0-9._-]*\.json|rules/[A-Za-z0-9][A-Za-z0-9._-]*\.js)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPathPattern();

    /// <summary>
    /// The files of the model's <c>extensions/</c> folder: the extension schemas (<c>extensions/*.json</c>, custom properties) and the
    /// script rules (<c>extensions/rules/*.js</c>), each with its hash and what is wrong with it on its own. A schema's problems are its
    /// load diagnostics; a rule's are a load of that script alone in the sandbox (syntax, a registration without an id, a limit).
    /// </summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The list.</returns>
    public async Task<ExtensionFileList> ListExtensionFilesAsync(CancellationToken ct)
    {
        var snapshot = await GetSnapshotAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var folder = paths.FullPath("extensions");
        var files = new List<ExtensionFileInfo>();
        if (Directory.Exists(folder))
        {
            var found = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f))
                .Concat(Directory.Exists(Path.Combine(folder, "rules"))
                    ? Directory.EnumerateFiles(Path.Combine(folder, "rules"), "*.js", SearchOption.TopDirectoryOnly).Select(f => "rules/" + Path.GetFileName(f))
                    : [])
                .Where(p => ExtensionPathPattern().IsMatch(p))
                .OrderBy(p => p.StartsWith("rules/", StringComparison.Ordinal) ? 1 : 0)
                .ThenBy(p => p, StringComparer.Ordinal);
            foreach (var path in found)
            {
                var full = paths.FullPath("extensions/" + path);
                if (new FileInfo(full).LinkTarget is not null)
                    continue;
                var bytes = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
                if (bytes is null)
                    continue;
                var repoPath = paths.ToRepoPath("extensions/" + path);
                var kind = KindOfExtension(path);
                var rules = new List<ExtensionRuleInfo>();
                IReadOnlyList<Diagnostic> diagnostics = kind == "rule"
                    ? CheckRuleScript(snapshot, repoPath, DecodeExtension(bytes) ?? "", ct, rules)
                    : [.. snapshot.LoadDiagnostics.Where(d => string.Equals(d.FilePath, repoPath, StringComparison.Ordinal))];
                files.Add(new ExtensionFileInfo(path, kind, bytes.LongLength, ContentHash.Of(bytes), diagnostics, rules));
            }
        }

        return new ExtensionFileList(paths.ToRepoPath("extensions"), files);
    }

    /// <summary>One extension file's text and hash; <see langword="null"/> when it does not exist.</summary>
    /// <param name="path">The path under <c>extensions/</c>.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The file.</returns>
    /// <exception cref="ExtensionPathException">The path is refused, or the file is not UTF-8 text.</exception>
    public async Task<ExtensionFileContent?> ReadExtensionFileAsync(string path, CancellationToken ct)
    {
        var full = ResolveExtensionPath(path);
        var bytes = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
        if (bytes is null)
            return null;
        var text = DecodeExtension(bytes) ?? throw new ExtensionPathException($"'{path}' is not UTF-8 text.");
        return new ExtensionFileContent(path, KindOfExtension(path), ContentHash.Of(bytes), text);
    }

    /// <summary>
    /// Writes one extension file through the engine's write guard (<see cref="WriteTarget.Model"/>), when its hash is still
    /// <paramref name="expectedHash"/> (<see langword="null"/> creates it; an existing file is then a conflict). A schema must be valid JSON
    /// that passes <c>extension.json</c>, else nothing is written and the MQ5004 diagnostics come back; it is written in canonical form.
    /// A rule script is written as sent, then loaded alone in the sandbox: its syntax error or failed registration comes back as MQ5002
    /// (or MQ5003) with line and column. The snapshot is refreshed before the call returns, so the next validation runs the new rules.
    /// </summary>
    /// <param name="path">The path under <c>extensions/</c>.</param>
    /// <param name="text">The whole text.</param>
    /// <param name="expectedHash">The hash read, or <see langword="null"/> to create.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">What caused the change (the editor or an agent).</param>
    /// <returns>The result.</returns>
    /// <exception cref="ExtensionPathException">The path is refused.</exception>
    public async Task<ExtensionWriteResult> WriteExtensionFileAsync(string path, string text, string? expectedHash, CancellationToken ct,
        ChangeSource source = ChangeSource.Editor)
    {
        ArgumentNullException.ThrowIfNull(text);
        var full = ResolveExtensionPath(path);
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var modelPath = "extensions/" + path;
        var repoPath = paths.ToRepoPath(modelPath);
        if (text.Contains('\0', StringComparison.Ordinal))
            return InvalidExtension(RuleCatalog.Create("MQ5004", "An extension file must be text: it holds a NUL character.", null, repoPath));
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxExtensionFileBytes)
        {
            return InvalidExtension(RuleCatalog.Create("MQ5004",
                string.Create(CultureInfo.InvariantCulture, $"An extension file is at most 1 MB; this one is {bytes.Length} bytes."), null, repoPath));
        }

        var kind = KindOfExtension(path);
        IReadOnlyList<Diagnostic> diagnostics = [];
        if (kind == "schema")
        {
            var parsed = new DocumentReader(_services.Schemas, _services.Json).ReadExtension(bytes, repoPath, trusted: false);
            if (!parsed.Valid)
                return new ExtensionWriteResult(SaveOutcome.Invalid, null, null, null, parsed.Diagnostics, []);
            bytes = _services.Json.Write(JsonNode.Parse(parsed.Json.GetRawText())!, "extension.json", modelPath);
        }
        else
        {
            diagnostics = CheckRuleScript(snapshot, repoPath, text, ct);
        }

        var written = await ExtensionWriteAsync(full, expectedHash, [repoPath], async () =>
        {
            GuardExtensionWrite(full);
            await AtomicFile.WriteAsync(full, bytes, "ext", ct).ConfigureAwait(false);
        }, source, ct).ConfigureAwait(false);
        return written ?? new ExtensionWriteResult(SaveOutcome.Saved, ContentHash.Of(bytes), null, Encoding.UTF8.GetString(bytes), diagnostics, [path]);
    }

    /// <summary>Deletes one extension file when its hash is still <paramref name="expectedHash"/>.</summary>
    /// <param name="path">The path under <c>extensions/</c>.</param>
    /// <param name="expectedHash">The hash read.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">What caused the change.</param>
    /// <returns>The result.</returns>
    /// <exception cref="ExtensionPathException">The path is refused.</exception>
    public async Task<ExtensionWriteResult> DeleteExtensionFileAsync(string path, string expectedHash, CancellationToken ct,
        ChangeSource source = ChangeSource.Editor)
    {
        ArgumentNullException.ThrowIfNull(expectedHash);
        var full = ResolveExtensionPath(path);
        await LoadedAsync(ct).ConfigureAwait(false);
        if (!File.Exists(full))
            return new ExtensionWriteResult(SaveOutcome.NotFound, null, null, null, [], []);
        var written = await ExtensionWriteAsync(full, expectedHash, [_paths.Value.ToRepoPath("extensions/" + path)], () =>
        {
            GuardExtensionWrite(full);
            File.Delete(full);
            return Task.CompletedTask;
        }, source, ct).ConfigureAwait(false);
        return written ?? new ExtensionWriteResult(SaveOutcome.Saved, null, null, null, [], [path]);
    }

    /// <summary>
    /// Moves (renames) one extension file when its hash is still <paramref name="expectedHash"/>. The target must not exist and must be of
    /// the same kind: a schema stays a <c>&lt;name&gt;.json</c>, a rule a <c>rules/&lt;name&gt;.js</c>. Nothing refers to an extension file
    /// by its path, so nothing else changes.
    /// </summary>
    /// <param name="move">The source and target.</param>
    /// <param name="expectedHash">The source file's hash.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="source">What caused the change.</param>
    /// <returns>The result; the hash is the moved file's.</returns>
    /// <exception cref="ExtensionPathException">A path is refused.</exception>
    public async Task<ExtensionWriteResult> MoveExtensionFileAsync(ExtensionFileMove move, string expectedHash, CancellationToken ct,
        ChangeSource source = ChangeSource.Editor)
    {
        ArgumentNullException.ThrowIfNull(move);
        ArgumentNullException.ThrowIfNull(expectedHash);
        var from = move.From ?? throw new ExtensionPathException("from is required.");
        var to = move.To ?? throw new ExtensionPathException("to is required.");
        var origin = ResolveExtensionPath(from);
        var target = ResolveExtensionPath(to);
        await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var targetRepo = paths.ToRepoPath("extensions/" + to);
        if (!File.Exists(origin))
            return new ExtensionWriteResult(SaveOutcome.NotFound, null, null, null, [], []);
        if (!string.Equals(KindOfExtension(from), KindOfExtension(to), StringComparison.Ordinal))
        {
            return InvalidExtension(RuleCatalog.Create("MQ5004",
                $"'{to}' is not the same kind of file as '{from}': a schema is <name>.json, a script rule rules/<name>.js.", null, targetRepo));
        }

        if (File.Exists(target) || Directory.Exists(target))
            return InvalidExtension(RuleCatalog.Create("MQ5004", $"'{to}' already exists.", null, targetRepo));
        byte[]? moved = null;
        var written = await ExtensionWriteAsync(origin, expectedHash, [paths.ToRepoPath("extensions/" + from), targetRepo], async () =>
        {
            GuardExtensionWrite(origin);
            GuardExtensionWrite(target);
            moved = await File.ReadAllBytesAsync(origin, ct).ConfigureAwait(false);
            await AtomicFile.WriteAsync(target, moved, "ext", ct).ConfigureAwait(false);
            File.Delete(origin);
        }, source, ct).ConfigureAwait(false);
        return written ?? new ExtensionWriteResult(SaveOutcome.Saved, ContentHash.Of(moved!), null, null, [], [from, to]);
    }

    /// <summary>
    /// Loads one rule script alone in the sandbox, as validation does: a syntax error, a <c>maquettiste.rule</c> call without an id, a
    /// script that throws or breaks a limit while loading. Returns its MQ5002 or MQ5003 diagnostic with the script's path, line and
    /// column; empty when it loads, and then fills <paramref name="rules"/> with what it registers.
    /// </summary>
    private List<Diagnostic> CheckRuleScript(ModelSnapshot snapshot, string repoPath, string code, CancellationToken ct, List<ExtensionRuleInfo>? rules = null)
    {
        try
        {
            using var pool = _services.Scripts.CreatePool([new ScriptSource(repoPath, code, ContentHash.Of(Encoding.UTF8.GetBytes(code)))],
                snapshot.Settings.Limits, 1, ct);
            if (rules is not null)
            {
                using var lease = pool.Rent();
                if (lease.Sandbox is ScriptSandbox sandbox)
                    rules.AddRange(sandbox.Rules.Select(r => new ExtensionRuleInfo("x/" + r.Id, r.Severity)));
            }

            return [];
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ScriptErrorException e)
        {
            return [e.Diagnostic with { Rule = "MQ5002", FilePath = e.Diagnostic.FilePath ?? repoPath }];
        }
        catch (ScriptLimitException e)
        {
            return [e.Diagnostic with { Rule = "MQ5003", FilePath = e.Diagnostic.FilePath ?? repoPath }];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [RuleCatalog.Create("MQ5002", "The rule script does not load: " + e.Message, null, repoPath)];
        }
    }

    /// <summary>
    /// Runs a write under the store's gate when the disk hash of <paramref name="full"/> is still <paramref name="expectedHash"/>, then
    /// reloads <paramref name="repoPaths"/> and publishes what changed. Returns the conflict, or <see langword="null"/> when written.
    /// </summary>
    private async Task<ExtensionWriteResult?> ExtensionWriteAsync(string full, string? expectedHash, IReadOnlyList<string> repoPaths, Func<Task> write,
        ChangeSource source, CancellationToken ct)
    {
        ChangeSet changes;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
            var diskHash = disk is null ? null : ContentHash.Of(disk);
            if (!string.Equals(diskHash, expectedHash, StringComparison.Ordinal))
                return new ExtensionWriteResult(SaveOutcome.Conflict, diskHash, disk is null ? null : DecodeExtension(disk), null, [], []);
            await write().ConfigureAwait(false);
            changes = await ReloadAsync(repoPaths, false, source, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        await NotifyAsync(changes, ct).ConfigureAwait(false);
        return null;
    }

    private void GuardExtensionWrite(string full)
    {
        var check = _services.EnginePaths.CheckEngineWrite(WriteTarget.Model, full);
        if (!check.Allowed)
            throw new ExtensionPathException($"The write to '{full}' is refused: {check.Reason}");
    }

    /// <summary>
    /// The absolute path of a path under <c>extensions/</c>: <c>&lt;name&gt;.json</c> or <c>rules/&lt;name&gt;.js</c> (letters, digits, dots,
    /// hyphens and underscores, not starting with a dot), and neither the folders nor the file may be a link.
    /// </summary>
    private string ResolveExtensionPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !ExtensionPathPattern().IsMatch(path))
        {
            throw new ExtensionPathException(
                $"'{path}' is not an extension file path: a custom property schema is <name>.json and a script rule rules/<name>.js, under extensions/.");
        }

        var paths = _paths.Value;
        var current = paths.FullPath("extensions");
        foreach (var segment in ("extensions/" + path).Split('/').Skip(1).Prepend(""))
        {
            if (segment.Length > 0)
                current = Path.Combine(current, segment);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var info = Directory.Exists(current) ? (FileSystemInfo)new DirectoryInfo(current) : new FileInfo(current);
                if (info.LinkTarget is not null)
                    throw new ExtensionPathException($"'{path}' goes through a link, which the editor does not follow.");
            }
        }

        return paths.FullPath("extensions/" + path);
    }

    private static string KindOfExtension(string path) => path.StartsWith("rules/", StringComparison.Ordinal) ? "rule" : "schema";

    private static ExtensionWriteResult InvalidExtension(Diagnostic diagnostic) => new(SaveOutcome.Invalid, null, null, null, [diagnostic], []);

    private static string? DecodeExtension(byte[] bytes)
    {
        try
        {
            return ExtensionUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}
