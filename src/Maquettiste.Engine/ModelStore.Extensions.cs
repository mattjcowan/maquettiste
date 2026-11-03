using System.Globalization;
using System.Text;
using System.Text.Json;
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
/// <param name="Path">The path under <c>extensions/</c>: <c>&lt;name&gt;.json</c>, <c>rules/&lt;name&gt;.js</c>, <c>rules/&lt;name&gt;.json</c>,
/// <c>seeds/&lt;name&gt;.js</c>, <c>kinds/&lt;type&gt;.json</c> or <c>calculated/&lt;name&gt;.js</c>.</param>
/// <param name="Kind"><c>schema</c> (custom properties, <c>extensions/*.json</c>), <c>rule</c> (a script rule, <c>extensions/rules/*.js</c>),
/// <c>declared-rules</c> (a rule declaration file, <c>extensions/rules/*.json</c>; section 5.6),
/// <c>seed-rows</c> (a seed's rows script, <c>extensions/seeds/*.js</c>; next-phase-2-design.md section 3.4), <c>custom-kind</c> (a custom
/// kind, <c>extensions/kinds/*.json</c>; section 4.2) or <c>calculated</c> (a calculation file, <c>extensions/calculated/*.js</c>; section
/// 5.5).</param>
/// <param name="Size">The size in bytes.</param>
/// <param name="Hash">The content hash (the expected hash of a write, delete or move).</param>
/// <param name="Diagnostics">What is wrong with the file on its own: an invalid schema (MQ5004), a rule script or calculation file that does not
/// load (MQ5002, MQ5003),
/// a rows script that does not load or does not register its rows exactly once (MQ7127), a script that keeps state at its top level
/// (MQ5005, a warning), a custom kind file that does not parse or pass <c>custom-kind.json</c> (MQ1001, MQ1002) and its own findings
/// (MQ1017, MQ1018, MQ2009, MQ7212), or a rule declaration file that does not fit <c>declared-rules.json</c> (MQ5014) or whose rules
/// repeat an id or name what does not exist (MQ5015, MQ5016).</param>
/// <param name="Rules">For a rule script that loads, the rules it registers (whole-model and resolved rules marked); for a valid rule
/// declaration file, the rules it declares (each marked resolved); empty for a schema.</param>
public sealed record ExtensionFileInfo(string Path, string Kind, long Size, string Hash, IReadOnlyList<Diagnostic> Diagnostics,
    IReadOnlyList<ExtensionRuleInfo> Rules)
{
    /// <summary>For a calculation file that loads, the calculated values it declares, in call order; empty for every other file.</summary>
    public IReadOnlyList<CalculatedValueDeclaration> Calculations { get; init; } = [];
}

/// <summary>One rule a script registers.</summary>
/// <param name="Id">The rule id as findings carry it: <c>x/&lt;id&gt;</c>.</param>
/// <param name="Severity">The severity the rule declares (<c>validation.rules</c> can override it).</param>
public sealed record ExtensionRuleInfo(string Id, DiagnosticSeverity Severity)
{
    /// <summary>Whether the rule was registered with <c>scope: "model"</c>: it checks the whole model once per whole-model validation.</summary>
    public bool WholeModel { get; init; }

    /// <summary>Whether the rule runs on the resolved model: a script rule registered with <c>scope: "resolved"</c> checks each resolved
    /// object of its kinds after resolution (next-phase-2-design.md section 5.5), and so does every declared rule (section 5.6).</summary>
    public bool Resolved { get; init; }
}

/// <summary>The body of <c>GET /api/extensions/files</c>.</summary>
/// <param name="Folder">The repo-relative folder the paths are under (<c>.maquettiste/extensions</c>).</param>
/// <param name="Files">The schemas, then the rules, then the rows scripts, then the custom kinds, then the calculation files, each ordinal by
/// path.</param>
public sealed record ExtensionFileList(string Folder, IReadOnlyList<ExtensionFileInfo> Files);

/// <summary>One extension file's text with its hash.</summary>
/// <param name="Path">The path under <c>extensions/</c>.</param>
/// <param name="Kind"><c>schema</c>, <c>rule</c>, <c>declared-rules</c>, <c>seed-rows</c>, <c>custom-kind</c> or <c>calculated</c>.</param>
/// <param name="Hash">The content hash.</param>
/// <param name="Text">The UTF-8 text.</param>
public sealed record ExtensionFileContent(string Path, string Kind, string Hash, string Text);

/// <summary>The body of <c>POST /api/extensions/file/move</c>.</summary>
/// <param name="From">The source path under <c>extensions/</c>.</param>
/// <param name="To">The target path (must not exist, and of the same kind: a schema stays a schema, a rule a rule, a rows script a rows
/// script, a calculation file a calculation file).</param>
public sealed record ExtensionFileMove(string? From, string? To);

/// <summary>The result of an extension file write, delete or move.</summary>
/// <param name="Outcome"><c>saved</c>, <c>conflict</c> (the hash changed; <paramref name="Hash"/> and <paramref name="Current"/> are the disk
/// version), <c>invalid</c> (nothing written) or <c>not-found</c>.</param>
/// <param name="Hash">The new hash when saved (null after a delete); the disk hash on conflict.</param>
/// <param name="Current">The disk text on conflict, else null.</param>
/// <param name="Text">The text written when saved: a schema is written in canonical form, so it may differ from the text sent.</param>
/// <param name="Diagnostics">Why it was refused (for a custom kind file deleted or moved while elements use it, one MQ1019 per element);
/// for a saved rule script, its syntax and load check (MQ5002, MQ5003 with line and column, or the warning MQ5005); for a saved custom
/// kind file, its own findings and the MQ3033 findings of its elements. Lists of elements hold at most 50, then one line with the count.</param>
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

    [GeneratedRegex(@"^(?:(?:kinds/|rules/)?[A-Za-z0-9][A-Za-z0-9._-]*\.json|(?:rules|seeds|calculated)/[A-Za-z0-9][A-Za-z0-9._-]*\.js)$", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionPathPattern();

    /// <summary>
    /// The files of the model's <c>extensions/</c> folder: the extension schemas (<c>extensions/*.json</c>, custom properties), the
    /// script rules (<c>extensions/rules/*.js</c>), the seeds' rows scripts (<c>extensions/seeds/*.js</c>), the custom kinds
    /// (<c>extensions/kinds/*.json</c>) and the calculation files (<c>extensions/calculated/*.js</c>), each with its hash and what
    /// is wrong with it on its own. A schema's problems are its load diagnostics; a script's are a load of that script alone in the
    /// sandbox (syntax, a registration without an id, a limit, a rows script that does not register its rows once). No rows script runs.
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
            IEnumerable<string> Scripts(string sub) => Directory.Exists(Path.Combine(folder, sub))
                ? Directory.EnumerateFiles(Path.Combine(folder, sub), "*.js", SearchOption.TopDirectoryOnly).Select(f => sub + "/" + Path.GetFileName(f))
                : [];
            var kinds = Path.Combine(folder, "kinds");
            var declarations = Path.Combine(folder, "rules");
            var found = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Select(f => Path.GetFileName(f))
                .Concat(Scripts("rules"))
                .Concat(Directory.Exists(declarations)
                    ? Directory.EnumerateFiles(declarations, "*.json", SearchOption.TopDirectoryOnly).Select(f => "rules/" + Path.GetFileName(f)) : [])
                .Concat(Scripts("seeds"))
                .Concat(Directory.Exists(kinds) ? Directory.EnumerateFiles(kinds, "*.json", SearchOption.TopDirectoryOnly).Select(f => "kinds/" + Path.GetFileName(f)) : [])
                .Concat(Scripts("calculated"))
                .Where(p => ExtensionPathPattern().IsMatch(p))
                .OrderBy(p => KindOfExtension(p) switch
                {
                    "schema" => 0, "rule" => 1, DeclaredRulesKind => 2, SeedRowsKind => 3, CustomKindFileKind => 4, _ => 5,
                })
                .ThenBy(p => p, StringComparer.Ordinal);
            List<Diagnostic>? declared = null; // the declarations' static checks, once per list
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
                var calculations = new List<CalculatedValueDeclaration>();
                IReadOnlyList<Diagnostic> diagnostics = kind switch
                {
                    "rule" => CheckRuleScript(snapshot, repoPath, DecodeExtension(bytes) ?? "", ct, rules),
                    SeedRowsKind => CheckRowsScript(snapshot, repoPath, DecodeExtension(bytes) ?? "", ct),
                    CustomKindFileKind => CustomKindFindings(snapshot, repoPath),
                    CalculationFileKind => CheckCalculationScript(snapshot, repoPath, DecodeExtension(bytes) ?? "", ct, calculations),
                    DeclaredRulesKind => [.. snapshot.LoadDiagnostics.Where(d => string.Equals(d.FilePath, repoPath, StringComparison.Ordinal))
                        .Concat((declared ??= CheckDeclaredRules(snapshot, ct)).Where(d => string.Equals(d.FilePath, repoPath, StringComparison.Ordinal)))
                        .Order(Diagnostic.Order)],
                    _ => [.. snapshot.LoadDiagnostics.Where(d => string.Equals(d.FilePath, repoPath, StringComparison.Ordinal))],
                };
                if (kind == DeclaredRulesKind)
                    rules.AddRange(DeclaredRuleInfos(snapshot.DeclaredRules.FirstOrDefault(d => string.Equals(d.Path, repoPath, StringComparison.Ordinal))));
                files.Add(new ExtensionFileInfo(path, kind, bytes.LongLength, ContentHash.Of(bytes), diagnostics, rules) { Calculations = calculations });
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
    /// (or MQ5003) with line and column. A rows script (<c>seeds/&lt;name&gt;.js</c>) is written as sent and loaded alone, never run: a
    /// script that does not load or does not register its rows exactly once comes back as MQ7127. A calculation file
    /// (<c>calculated/&lt;name&gt;.js</c>, section 5.5) is written as sent and loaded alone, never called: MQ5002, MQ5003 and MQ5005 come back
    /// at once. A rule declaration file (<c>rules/&lt;name&gt;.json</c>, section 5.6) must be JSON; it is written in canonical form and
    /// checked alone: MQ5014, or MQ5015 against the other declarations and the rule scripts and MQ5016, with pointers. A custom kind file
    /// (<c>kinds/&lt;type&gt;.json</c>, next-phase-2-design.md section 4.2) must parse and pass <c>custom-kind.json</c>, else nothing is
    /// written (MQ1001, MQ1002); it is written in canonical form, and the answer carries its own findings and the MQ3033 findings of the
    /// elements of its type (decision 20: at most 50, then the count; they never refuse the write). The snapshot is refreshed before the
    /// call returns, so the next validation runs the new rules.
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
        else if (kind == CustomKindFileKind)
        {
            if (CheckCustomKindText(bytes, repoPath) is { Count: > 0 } refused)
                return new ExtensionWriteResult(SaveOutcome.Invalid, null, null, null, refused, []);
            bytes = _services.Json.Write(JsonNode.Parse(bytes)!, "custom-kind.json", modelPath);
        }
        else if (kind == DeclaredRulesKind)
        {
            // A declaration file (section 5.6) must be JSON; it is written in canonical form, then checked alone: its MQ5014 findings, or
            // MQ5015 and MQ5016 against the other declarations and the rule scripts. They never refuse the write.
            var parsed = new DocumentReader(_services.Schemas, _services.Json).ReadDeclaredRules(bytes, repoPath, trusted: false);
            if (parsed.Json.ValueKind == JsonValueKind.Undefined)
                return new ExtensionWriteResult(SaveOutcome.Invalid, null, null, null, parsed.Diagnostics, []);
            bytes = _services.Json.Write(JsonNode.Parse(parsed.Json.GetRawText())!, "declared-rules.json", modelPath);
            diagnostics = parsed.DeclaredRules is { } file
                ? CheckDeclaredRules(snapshot, ct, new DeclaredRuleDocument(file, repoPath, ContentHash.Of(bytes)))
                    .Where(d => string.Equals(d.FilePath, repoPath, StringComparison.Ordinal)).Order(Diagnostic.Order).ToList()
                : parsed.Diagnostics;
        }
        else
        {
            diagnostics = kind switch
            {
                SeedRowsKind => CheckRowsScript(snapshot, repoPath, text, ct),
                CalculationFileKind => CheckCalculationScript(snapshot, repoPath, text, ct),
                _ => CheckRuleScript(snapshot, repoPath, text, ct),
            };
        }

        var written = await ExtensionWriteAsync(full, expectedHash, [repoPath], async () =>
        {
            GuardExtensionWrite(full);
            await AtomicFile.WriteAsync(full, bytes, "ext", ct).ConfigureAwait(false);
        }, source, ct).ConfigureAwait(false);
        if (written is null && kind == CustomKindFileKind)
            diagnostics = await CustomKindWriteFindingsAsync(TypeOfKindFile(path), repoPath, ct).ConfigureAwait(false);
        return written ?? new ExtensionWriteResult(SaveOutcome.Saved, ContentHash.Of(bytes), null, Encoding.UTF8.GetString(bytes), diagnostics, [path]);
    }

    /// <summary>Deletes one extension file when its hash is still <paramref name="expectedHash"/>. A custom kind file that elements still use
    /// is refused (<c>invalid</c>, one MQ1019 per element that would lose its kind, at most 50, then the count).</summary>
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
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        if (!File.Exists(full))
            return new ExtensionWriteResult(SaveOutcome.NotFound, null, null, null, [], []);
        if (UsedCustomKind(snapshot, path) is { Count: > 0 } used)
            return new ExtensionWriteResult(SaveOutcome.Invalid, null, null, null, used, []);
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
    /// the same kind: a schema stays a <c>&lt;name&gt;.json</c>, a rule a <c>rules/&lt;name&gt;.js</c>, a rows script a
    /// <c>seeds/&lt;name&gt;.js</c>, a custom kind a <c>kinds/&lt;type&gt;.json</c>. Nothing changes the seeds that name a moved rows script:
    /// they report it missing (MQ7116) until they name the new path. A custom kind file that elements still use is refused as a delete is.
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
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var paths = _paths.Value;
        var targetRepo = paths.ToRepoPath("extensions/" + to);
        if (!File.Exists(origin))
            return new ExtensionWriteResult(SaveOutcome.NotFound, null, null, null, [], []);
        if (!string.Equals(KindOfExtension(from), KindOfExtension(to), StringComparison.Ordinal))
        {
            return InvalidExtension(RuleCatalog.Create("MQ5004",
                $"'{to}' is not the same kind of file as '{from}': a schema is <name>.json, a script rule rules/<name>.js, a rule declaration file rules/<name>.json, a rows script seeds/<name>.js, a custom kind kinds/<type>.json, a calculation file calculated/<name>.js.",
                null, targetRepo));
        }

        if (UsedCustomKind(snapshot, from) is { Count: > 0 } used)
            return new ExtensionWriteResult(SaveOutcome.Invalid, null, null, null, used, []);

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
    /// column; when it loads, its load warning (MQ5005, top-level <c>let</c> or <c>var</c>, unless <c>validation.rules</c> sets it
    /// off), and then fills <paramref name="rules"/> with what it registers.
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
                    rules.AddRange(sandbox.Rules.Select(r => new ExtensionRuleInfo("x/" + r.Id, r.Severity) { WholeModel = r.WholeModel, Resolved = r.Resolved }));
            }

            return Validation.ModelValidator.ApplySettings(snapshot.Settings, [.. pool.Warnings]);
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
    /// Loads one calculation file alone in the sandbox (next-phase-2-design.md section 5.5), never calling its value functions: a syntax
    /// error, a malformed <c>maquettiste.calculated</c> call, another registration, or a file that throws or breaks a limit while loading is
    /// MQ5002 or MQ5003 with the file's path, line and column; when it loads, its load warning (MQ5005, unless <c>validation.rules</c> sets
    /// it off), and then fills <paramref name="calculations"/> with what it declares, in call order. Loaded alone, a (name, kind) pair
    /// another file also declares is not found here; validation reports it on the later file.
    /// </summary>
    private List<Diagnostic> CheckCalculationScript(ModelSnapshot snapshot, string repoPath, string code, CancellationToken ct,
        List<CalculatedValueDeclaration>? calculations = null)
    {
        try
        {
            using var pool = _services.Scripts.CreatePool([new ScriptSource(repoPath, code, ContentHash.Of(Encoding.UTF8.GetBytes(code)))],
                snapshot.Settings.Limits, 1, ct);
            calculations?.AddRange(pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.CalculatedValue && r.CalculatedValue is not null)
                .Select(r => r.CalculatedValue!));
            return Validation.ModelValidator.ApplySettings(snapshot.Settings, [.. pool.Warnings]);
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
            return [RuleCatalog.Create("MQ5002", "The calculation file does not load: " + e.Message, null, repoPath)];
        }
    }

    /// <summary>
    /// What the snapshot's calculation files declare, each file loaded alone as <c>list_extension_files</c> loads it (a file that does not
    /// load declares nothing), in path then call order. Calls no value function.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The declarations.</returns>
    internal IReadOnlyList<CalculatedValueDeclaration> CalculationDeclarations(ModelSnapshot snapshot, CancellationToken ct)
    {
        var declarations = new List<CalculatedValueDeclaration>();
        foreach (var script in snapshot.CalculationScripts)
            CheckCalculationScript(snapshot, script.Path, script.Code, ct, declarations);
        return declarations;
    }

    /// <summary>
    /// Loads one rows script alone in the sandbox (next-phase-2-design.md section 3.4), never running its rows function: a syntax error, a
    /// script that throws or breaks a limit while loading, a registration other than <c>maquettiste.rows</c>, or rows registered not
    /// exactly once is MQ7127 with the script's path, line and column; when it loads, its load warning (MQ5005, unless
    /// <c>validation.rules</c> sets it off).
    /// </summary>
    private List<Diagnostic> CheckRowsScript(ModelSnapshot snapshot, string repoPath, string code, CancellationToken ct)
    {
        try
        {
            using var pool = _services.Scripts.CreatePool([new ScriptSource(repoPath, code, ContentHash.Of(Encoding.UTF8.GetBytes(code)))],
                snapshot.Settings.Limits, 1, ct);
            if (pool.Registrations.Count(r => r.Kind == ScriptRegistrationKind.Rows) != 1)
            {
                return [RuleCatalog.Create("MQ7127",
                    $"Rows script {repoPath} registers no rows: call maquettiste.rows((model, seed) => [...]) exactly once.", null, repoPath)];
            }

            return Validation.ModelValidator.ApplySettings(snapshot.Settings, [.. pool.Warnings]);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (ScriptErrorException e)
        {
            return [e.Diagnostic with { Rule = "MQ7127", FilePath = e.Diagnostic.FilePath ?? repoPath }];
        }
        catch (ScriptLimitException e)
        {
            return [e.Diagnostic with { Rule = "MQ7127", FilePath = e.Diagnostic.FilePath ?? repoPath }];
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return [RuleCatalog.Create("MQ7127", "The rows script does not load: " + e.Message, null, repoPath)];
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
    /// The absolute path of a path under <c>extensions/</c>: <c>&lt;name&gt;.json</c>, <c>rules/&lt;name&gt;.js</c> or <c>seeds/&lt;name&gt;.js</c>
    /// (letters, digits, dots, hyphens and underscores, not starting with a dot), and neither the folders nor the file may be a link.
    /// </summary>
    private string ResolveExtensionPath(string path)
    {
        if (string.IsNullOrEmpty(path) || !ExtensionPathPattern().IsMatch(path))
        {
            throw new ExtensionPathException(
                $"'{path}' is not an extension file path: a custom property schema is <name>.json, a script rule rules/<name>.js, a rows script seeds/<name>.js, a custom kind kinds/<type>.json and a calculation file calculated/<name>.js, under extensions/.");
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

    /// <summary>The kind of a rule declaration file (<c>extensions/rules/*.json</c>, next-phase-2-design.md section 5.6).</summary>
    private const string DeclaredRulesKind = "declared-rules";

    /// <summary>The kind of a seed's rows script (<c>extensions/seeds/*.js</c>).</summary>
    private const string SeedRowsKind = "seed-rows";

    /// <summary>The kind of a custom kind file (<c>extensions/kinds/*.json</c>).</summary>
    private const string CustomKindFileKind = "custom-kind";

    /// <summary>The kind of a calculation file (<c>extensions/calculated/*.js</c>, next-phase-2-design.md section 5.5).</summary>
    private const string CalculationFileKind = "calculated";

    /// <summary>The most elements a custom kind file's answer lists, before the line with the count (decision 20).</summary>
    private const int MaxListedElements = 50;

    private static string KindOfExtension(string path) =>
        path.StartsWith("rules/", StringComparison.Ordinal) ? (path.EndsWith(".json", StringComparison.Ordinal) ? DeclaredRulesKind : "rule")
        : path.StartsWith("seeds/", StringComparison.Ordinal) ? SeedRowsKind
        : path.StartsWith("kinds/", StringComparison.Ordinal) ? CustomKindFileKind
        : path.StartsWith("calculated/", StringComparison.Ordinal) ? CalculationFileKind
        : "schema";

    /// <summary>The type a custom kind file declares when valid: its file stem (MQ1017 flags a file whose type differs).</summary>
    private static string TypeOfKindFile(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>A custom kind file's problems on its own: its load diagnostics (MQ1001, MQ1002) and its findings in the property schema set
    /// (MQ1017, MQ1018, MQ2009, MQ7212), in order.</summary>
    private static List<Diagnostic> CustomKindFindings(ModelSnapshot snapshot, string repoPath) =>
    [
        .. snapshot.LoadDiagnostics.Concat(snapshot.PropertySchemas.Diagnostics)
            .Where(d => string.Equals(d.FilePath, repoPath, StringComparison.Ordinal))
            .Distinct()
            .Order(Diagnostic.Order),
    ];

    /// <summary>A custom kind text that does not parse (MQ1001) or pass <c>custom-kind.json</c> (MQ1002); empty when it may be written.</summary>
    private List<Diagnostic> CheckCustomKindText(byte[] bytes, string repoPath)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            return [RuleCatalog.Create("MQ1001", "The custom kind file is not valid JSON: " + ex.Message, null, repoPath)];
        }

        using (document)
        {
            return [.. _services.Schemas.Evaluate("custom-kind.json", document.RootElement, repoPath)
                .Select(d => d with { Rule = "MQ1002", Severity = DiagnosticSeverity.Error })
                .Order(Diagnostic.Order)];
        }
    }

    /// <summary>
    /// The answer of a saved custom kind file: its own findings, then the MQ3033 findings of the elements of its type, validated alone, at
    /// most <see cref="MaxListedElements"/> and then one line with the count.
    /// </summary>
    private async Task<IReadOnlyList<Diagnostic>> CustomKindWriteFindingsAsync(string type, string repoPath, CancellationToken ct)
    {
        var snapshot = await LoadedAsync(ct).ConfigureAwait(false);
        var findings = CustomKindFindings(snapshot, repoPath);
        var ids = snapshot.All<Custom>().Where(c => string.Equals(c.Type, type, StringComparison.Ordinal)).Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
        if (ids.Count == 0)
            return findings;
        var report = await ValidateCoreAsync(snapshot, new ValidationScope(ids, IncludeReferrers: false, IncludeScriptRules: false), committedStale: false, ct)
            .ConfigureAwait(false);
        var content = report.Diagnostics.Where(d => d.Rule == "MQ3033").ToList();
        findings.AddRange(content.Take(MaxListedElements));
        if (content.Count > MaxListedElements)
        {
            findings.Add(RuleCatalog.Create("MQ3033", string.Create(CultureInfo.InvariantCulture,
                $"{content.Count} findings in the elements of the custom kind '{type}'; the first {MaxListedElements} are listed."), null, repoPath));
        }

        return findings;
    }

    /// <summary>
    /// For a custom kind file the snapshot holds as the declaration of its type, one MQ1019 per element of that type, which a delete or a
    /// move would leave without its kind (ordered by name and id, at most <see cref="MaxListedElements"/>, then one line with the count);
    /// empty for any other file or a kind no element uses.
    /// </summary>
    private static List<Diagnostic> UsedCustomKind(ModelSnapshot snapshot, string path)
    {
        if (KindOfExtension(path) != CustomKindFileKind)
            return [];
        var type = TypeOfKindFile(path);
        var repoPath = snapshot.PropertySchemas.ContentOf(type)?.Path;
        if (repoPath is null || !repoPath.EndsWith("extensions/" + path, StringComparison.Ordinal))
            return [];
        var users = snapshot.All<Custom>().Where(c => string.Equals(c.Type, type, StringComparison.Ordinal))
            .OrderBy(c => c.Name, StringComparer.Ordinal).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var refused = users.Take(MaxListedElements)
            .Select(c => RuleCatalog.Create("MQ1019", $"The custom element '{c.Name}' would lose its custom kind '{type}'.", c.Id,
                snapshot.GetDocument(c.Id)?.Path, "/type"))
            .ToList();
        if (users.Count > MaxListedElements)
        {
            refused.Add(RuleCatalog.Create("MQ1019", string.Create(CultureInfo.InvariantCulture,
                $"{users.Count} custom elements use the custom kind '{type}'; the first {MaxListedElements} are listed."), null, repoPath));
        }

        return refused;
    }

    /// <summary>
    /// The static checks of the rule declarations (MQ5015, MQ5016; next-phase-2-design.md section 5.6), with <paramref name="written"/> in
    /// place of the file of its path when given: the ids are compared with the rule scripts' when they all load, and calculated values in
    /// paths are checked when the calculation files load, as a whole-model validation does.
    /// </summary>
    private List<Diagnostic> CheckDeclaredRules(ModelSnapshot snapshot, CancellationToken ct, DeclaredRuleDocument? written = null)
    {
        var candidate = snapshot;
        if (written is not null)
        {
            IReadOnlyList<DeclaredRuleDocument> declared =
                [.. snapshot.DeclaredRules.Where(d => !string.Equals(d.Path, written.Path, StringComparison.Ordinal)), written];
            candidate = ModelSnapshot.CreateAfter(snapshot, snapshot.Documents, snapshot.Settings, snapshot.SettingsHash, snapshot.Extensions,
                snapshot.RuleScripts, snapshot.Version, [.. snapshot.LoadDiagnostics.Where(d => !string.Equals(d.FilePath, written.Path, StringComparison.Ordinal))],
                _options.EffectiveParallelism, ct, calculationScripts: snapshot.CalculationScripts, declaredRules: declared);
        }

        if (candidate.DeclaredRules.Count == 0)
            return [];
        Dictionary<string, string>? scriptRules = candidate.RuleScripts.Count == 0 ? [] : null;
        if (scriptRules is null)
        {
            try
            {
                using var pool = _services.Scripts.CreatePool(candidate.RuleScripts, candidate.Settings.Limits, 1, ct);
                scriptRules = pool.Registrations.Where(r => r.Kind == ScriptRegistrationKind.Rule)
                    .GroupBy(r => Validation.ModelValidator.Bare(r.Name), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Select(r => r.DeclaredIn).Min(StringComparer.Ordinal)!, StringComparer.Ordinal);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                scriptRules = null; // a rule script that does not load (or needs a library): ids are compared among declarations only
            }
        }

        Func<string, string, bool>? declares = candidate.CalculationScripts.Count == 0 ? static (_, _) => false : null;
        if (declares is null && _services.Calculations is { } calculations)
        {
            var loaded = calculations.Load(candidate);
            if (!loaded.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error))
                declares = (kind, name) => loaded.ByKind.TryGetValue(kind, out var byKind) && byKind.ByName.ContainsKey(name);
        }

        return Validation.ModelValidator.ApplySettings(candidate.Settings, Validation.DeclaredRuleChecks.Check(candidate, scriptRules, declares, written?.Path));
    }

    /// <summary>The rules a valid declaration file declares, as <c>list_extension_files</c> lists them: each runs on the resolved model.</summary>
    private static IEnumerable<ExtensionRuleInfo> DeclaredRuleInfos(DeclaredRuleDocument? file) =>
        file?.Rules.Rules.Select(r => new ExtensionRuleInfo("x/" + r.Id, r.Severity) { Resolved = true }) ?? [];

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
