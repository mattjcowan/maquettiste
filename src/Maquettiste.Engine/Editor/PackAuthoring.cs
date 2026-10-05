using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Planning;
using Maquettiste.Engine.Rendering;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Writing;

namespace Maquettiste.Engine;

/// <summary>A pack in the editor's pack list (generation-ui.md section 5.1).</summary>
/// <param name="Name">The pack name (its folder).</param>
/// <param name="Version">The pack version, or <see langword="null"/> when <c>pack.json</c> does not load.</param>
/// <param name="Description">The description.</param>
/// <param name="Enabled">Whether runs generate it (<c>packs.&lt;name&gt;.enabled</c>).</param>
/// <param name="Output">The pack's output base in this project.</param>
/// <param name="Units">The units, in <c>pack.json</c> order.</param>
/// <param name="FileCount">Files in the pack folder (hidden ones left out).</param>
/// <param name="Diagnostics">The pack's load diagnostics.</param>
public sealed record PackSummary(string Name, string? Version, string? Description, bool Enabled, string Output, IReadOnlyList<PackUnit> Units,
    int FileCount, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>One file of a pack folder.</summary>
/// <param name="Path">The pack-relative path, <c>/</c> separators.</param>
/// <param name="Size">Bytes.</param>
/// <param name="Hash">The SHA-256 of the bytes (the file's ETag).</param>
/// <param name="Role"><c>manifest</c>, <c>template</c> (a unit names it), <c>partial</c> (a template includes it), <c>script</c>,
/// <c>type-map</c> or <c>other</c>, derived with the loader's own rules (GU2).</param>
/// <param name="UsedBy">What uses it: <c>unit:&lt;id&gt;</c>, <c>companion:&lt;id&gt;</c> and <c>include:&lt;path&gt;</c>, ordinal.</param>
public sealed record PackFileInfo(string Path, long Size, string Hash, string Role, IReadOnlyList<string> UsedBy);

/// <summary>A pack parameter as the Parameters form shows it.</summary>
/// <param name="Name">The parameter name.</param>
/// <param name="Default">The default from <c>pack.json</c> <c>parameters</c>, or <see langword="null"/>.</param>
/// <param name="Value">The project's value from <c>packs.&lt;name&gt;.parameters</c>, or <see langword="null"/> (the default applies).</param>
/// <param name="Schema">The property schema from <c>parameterSchema</c>, or <see langword="null"/>.</param>
/// <param name="Required">Whether <c>parameterSchema.required</c> names it.</param>
public sealed record PackParameterInfo(string Name, JsonElement? Default, JsonElement? Value, JsonElement? Schema, bool Required);

/// <summary>A pack read for the pack editor: the document, its ETag, parameters, files and diagnostics.</summary>
/// <param name="Name">The pack name.</param>
/// <param name="Enabled">Whether runs generate it.</param>
/// <param name="Output">The output base.</param>
/// <param name="Hash">The SHA-256 of <c>pack.json</c>'s bytes (its ETag).</param>
/// <param name="Document">The whole <c>pack.json</c> document as written, every member kept; <see langword="null"/> when it is not JSON.</param>
/// <param name="Parameters">Every parameter the pack defaults or declares, and every project value, ordinal by name.</param>
/// <param name="Files">The folder's files, ordinal by path.</param>
/// <param name="Diagnostics">Load diagnostics, MQ6019 for output paths, MQ6003 and MQ6025 for template parse errors.</param>
public sealed record PackDocument(string Name, bool Enabled, string Output, string Hash, JsonElement? Document, IReadOnlyList<PackParameterInfo> Parameters,
    IReadOnlyList<PackFileInfo> Files, IReadOnlyList<Diagnostic> Diagnostics)
{
    /// <summary>What the pack's own scripts register (helpers, selectors, filters, transforms, rules), by kind then name; empty when they fail.</summary>
    public IReadOnlyList<ScriptRegistration> Registrations { get; init; } = [];
}

/// <summary>A pack file's text with its ETag.</summary>
/// <param name="Path">The pack-relative path.</param>
/// <param name="Hash">The SHA-256 of the bytes.</param>
/// <param name="Text">The UTF-8 text.</param>
public sealed record PackFileContent(string Path, string Hash, string Text);

/// <summary>The result of a pack write (file save or delete, <c>pack.json</c> save, new pack).</summary>
/// <param name="Outcome"><c>saved</c>, <c>conflict</c> (409: the hash changed; <paramref name="Hash"/> and <paramref name="Current"/> are the
/// disk version), <c>invalid</c> (422: nothing written), <c>referenced</c> (409: a unit or template still uses the file) or <c>not-found</c>.</param>
/// <param name="Hash">The new hash when saved (<see langword="null"/> after a delete); the disk hash on conflict.</param>
/// <param name="Current">The disk text on conflict, else <see langword="null"/>.</param>
/// <param name="Diagnostics">Parse diagnostics of the saved file, or why nothing was written.</param>
/// <param name="Files">The pack-relative files written (a new pack), ordinal.</param>
public sealed record PackWriteResult(SaveOutcome Outcome, string? Hash, string? Current, IReadOnlyList<Diagnostic> Diagnostics, IReadOnlyList<string> Files);

/// <summary>A pack path the request may not name: not relative, leaving the pack folder, through a link, or <c>pack.json</c> for a raw write.</summary>
/// <param name="message">Why.</param>
public sealed class PackPathException(string message) : ArgumentException(message);

/// <summary>Reads and writes pack folders for the editor and the MCP tools (generation-ui.md sections 3 and 5.1, GU1).</summary>
internal static partial class PackAuthoring
{
    /// <summary>The largest pack file the editor writes (generation-ui.md section 3.3).</summary>
    public const int MaxFileBytes = 1024 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    public static partial Regex NamePattern();

    /// <summary>The absolute folder of a pack; refuses a name that is not a pack key.</summary>
    public static string PackRoot(EngineOptions options, string pack)
    {
        if (string.IsNullOrEmpty(pack) || !NamePattern().IsMatch(pack))
            throw new PackPathException($"'{pack}' is not a pack name (lowercase letters, digits and single hyphens).");
        return Path.Combine(new ModelPaths(options).ModelRoot, "templates", pack);
    }

    /// <summary>
    /// Resolves a request's pack-relative path: lexically (relative, no <c>..</c>, no absolute segment), then the real path of every
    /// existing ancestor and of the target must stay inside the real pack folder, so a link to another pack or the model is refused
    /// (generation-ui.md section 5.1, the pack file guard).
    /// </summary>
    public static string Resolve(string packRoot, string path)
    {
        var full = PackFiles.Resolve(packRoot, path)
            ?? throw new PackPathException($"'{path}' must be a relative path inside the pack folder, with '/' separators and no '.' or '..' segment.");
        if (path.Split('/').Any(s => s.StartsWith('.')))
            throw new PackPathException($"'{path}' names a hidden file or folder, which packs do not use.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(packRoot));
        var realRoot = RealPath(root);
        var current = root;
        foreach (var segment in path.Split('/'))
        {
            current = Path.Combine(current, segment);
            if (!File.Exists(current) && !Directory.Exists(current))
                break;
            var real = RealPath(current);
            if (!real.StartsWith(realRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new PackPathException($"'{path}' resolves outside the pack folder through a link.");
        }

        return full;
    }

    /// <summary>The pack list (<c>GET /api/packs</c>).</summary>
    public static async Task<IReadOnlyList<PackSummary>> ListAsync(EngineOptions options, ISchemaRegistry schemas, ModelSnapshot snapshot, CancellationToken ct)
    {
        var templates = Path.Combine(new ModelPaths(options).ModelRoot, "templates");
        var names = Directory.Exists(templates)
            ? Directory.EnumerateDirectories(templates).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('.') && File.Exists(Path.Combine(templates, n, "pack.json"))).Order(StringComparer.Ordinal).ToList()
            : [];
        var loader = new PackLoader(options, schemas);
        var result = new List<PackSummary>(names.Count);
        foreach (var name in names)
        {
            var settings = snapshot.Settings.Packs.TryGetValue(name, out var s) ? s : new PackSettings();
            var (pack, diagnostics) = await loader.LoadNamedAsync(snapshot, name, ct).ConfigureAwait(false);
            var files = EnumerateFiles(Path.Combine(templates, name)).Count;
            result.Add(new PackSummary(name, pack?.Manifest.Version, pack?.Manifest.Description, settings.Enabled, settings.Output,
                pack?.Manifest.Units ?? [], files, diagnostics));
        }

        return result;
    }

    /// <summary>The pack read (<c>GET /api/packs/{pack}</c>), or <see langword="null"/> when there is no such pack.</summary>
    public static async Task<PackDocument?> ReadAsync(EngineOptions options, ISchemaRegistry schemas, ModelSnapshot snapshot, string name, CancellationToken ct)
    {
        var root = PackRoot(options, name);
        var manifestPath = Path.Combine(root, "pack.json");
        if (!File.Exists(manifestPath))
            return null;
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct).ConfigureAwait(false);
        JsonElement? document = null;
        try
        {
            using var parsed = JsonDocument.Parse(bytes);
            document = parsed.RootElement.Clone();
        }
        catch (JsonException)
        {
        }

        var settings = snapshot.Settings.Packs.TryGetValue(name, out var s) ? s : new PackSettings();
        var (pack, loadDiagnostics) = await new PackLoader(options, schemas).LoadNamedAsync(snapshot, name, ct).ConfigureAwait(false);
        var manifest = pack?.Manifest ?? TryManifest(document);
        var files = await DescribeFilesAsync(root, manifest, ct).ConfigureAwait(false);
        var paths = new ModelPaths(options);
        var relative = paths.ToRepoPath("templates/" + name);
        var diagnostics = new List<Diagnostic>(loadDiagnostics);
        if (manifest is not null)
        {
            diagnostics.AddRange(UnitRules.OutputRoots(manifest, settings, snapshot.Settings.Outputs.Allow, relative + "/pack.json"));
            // A loaded pack carries the loader's parse pass (MQ6003, MQ6025); one that does not load still shows its parse errors.
            if (pack is null)
                diagnostics.AddRange(await PackFileRoles.ParseAsync(root, relative, manifest, files, ct).ConfigureAwait(false));
        }

        return new PackDocument(name, settings.Enabled, settings.Output, ContentHash.Of(bytes), document, Parameters(manifest, settings), files,
            PackLoader.Sort(diagnostics.Distinct()));
    }

    /// <summary>One file's text (<c>GET /api/packs/{pack}/file?path=</c>), or <see langword="null"/> when it does not exist.</summary>
    public static async Task<PackFileContent?> ReadFileAsync(EngineOptions options, string name, string path, CancellationToken ct)
    {
        var full = Resolve(PackRoot(options, name), path);
        if (!File.Exists(full))
            return null;
        var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            throw new PackPathException($"'{path}' is not UTF-8 text; the editor edits text files only.");
        }

        return new PackFileContent(path, ContentHash.Of(bytes), text);
    }

    /// <summary>
    /// Writes one pack file (<c>PUT /api/packs/{pack}/file?path=</c>): <paramref name="expectedHash"/> is the hash the caller read, or
    /// <see langword="null"/> to create (refused as a conflict when the file exists). A template that fails to parse is still saved and
    /// reported (GU6). <c>pack.json</c> is refused: it is saved whole and canonical through <see cref="SaveManifestAsync"/>.
    /// </summary>
    public static async Task<PackWriteResult> WriteFileAsync(EngineServices services, ModelSnapshot snapshot, string name, string path, string text,
        string? expectedHash, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        RefuseManifest(path);
        if (!File.Exists(Path.Combine(root, "pack.json")))
            return new PackWriteResult(SaveOutcome.NotFound, null, null, [], []);
        var full = Resolve(root, path);
        if (text.Contains('\0', StringComparison.Ordinal))
            return Invalid("A pack file must be text: it holds a NUL character.", path);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > MaxFileBytes)
            return Invalid($"A pack file is at most 1 MB; this one is {bytes.Length} bytes.", path);
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
            if (Conflict(disk, expectedHash) is { } conflict)
                return conflict;
            await WriteGuardedAsync(services, full, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }

        var relative = new ModelPaths(services.Options).ToRepoPath("templates/" + name);
        var manifest = TryManifest(root);
        var diagnostics = manifest is null ? [] : PackFileRoles.Parse(relative, path, text, manifest, reached: true);
        return new PackWriteResult(SaveOutcome.Saved, ContentHash.Of(bytes), null, diagnostics, [path]);
    }

    /// <summary>Deletes one pack file (<c>DELETE /api/packs/{pack}/file?path=</c>); refused while a unit names it or a template includes it.</summary>
    public static async Task<PackWriteResult> DeleteFileAsync(EngineServices services, string name, string path, string expectedHash, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        RefuseManifest(path);
        var full = Resolve(root, path);
        if (!File.Exists(full))
            return new PackWriteResult(SaveOutcome.NotFound, null, null, [], []);
        var manifest = TryManifest(root);
        var files = await DescribeFilesAsync(root, manifest, ct).ConfigureAwait(false);
        var used = files.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.Ordinal))?.UsedBy ?? [];
        if (used.Count > 0)
        {
            return new PackWriteResult(SaveOutcome.Referenced, null, null, [RuleCatalog.Create("MQ6022",
                $"'{path}' is used by {string.Join(", ", used)}; change those first.", filePath: path)], []);
        }

        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
            if (Conflict(disk, expectedHash) is { } conflict)
                return conflict;
            var check = services.EnginePaths.CheckEngineWrite(WriteTarget.Model, full);
            if (!check.Allowed)
                throw new PackPathException($"'{path}' may not be deleted: {check.Reason}");
            File.Delete(full);
        }
        finally
        {
            WriteGate.Release();
        }

        return new PackWriteResult(SaveOutcome.Saved, null, null, [], [path]);
    }

    /// <summary>
    /// Saves the whole <c>pack.json</c> document (<c>PUT /api/packs/{pack}</c>), every member kept, schema-checked and written in canonical
    /// form as <c>pack new</c> writes it. A schema failure or a name that is not the folder is <c>invalid</c> and writes nothing; unit
    /// problems (a missing template, an output path outside the roots) are saved and reported.
    /// </summary>
    public static async Task<PackWriteResult> SaveManifestAsync(EngineServices services, ModelSnapshot snapshot, string name, ReadOnlyMemory<byte> body,
        string expectedHash, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        var full = Path.Combine(root, "pack.json");
        if (!File.Exists(full))
            return new PackWriteResult(SaveOutcome.NotFound, null, null, [], []);
        var relative = new ModelPaths(services.Options).ToRepoPath("templates/" + name);
        var packFile = relative + "/pack.json";
        JsonObject node;
        try
        {
            node = JsonNode.Parse(body.Span) as JsonObject ?? throw new JsonException("pack.json must be a JSON object.");
        }
        catch (JsonException ex)
        {
            return Invalid($"pack.json is not valid JSON: {ex.Message}", packFile, "MQ6001");
        }

        using (var document = JsonDocument.Parse(body))
        {
            var failures = services.Schemas.Evaluate("pack.json", document.RootElement, packFile);
            if (failures.Count > 0)
            {
                return new PackWriteResult(SaveOutcome.Invalid, null, null, [.. failures.Select(f => f.JsonPointer is { } p && p.EndsWith("/for", StringComparison.Ordinal)
                    ? RuleCatalog.Create("MQ6021", UnitRules.UnknownScope(null, ScopeAt(document.RootElement, p)), filePath: packFile, jsonPointer: p)
                    : RuleCatalog.Create("MQ6001", f.Message, filePath: packFile, jsonPointer: f.JsonPointer))], []);
            }
        }

        if (node["name"]?.GetValue<string>() is not { } named || !string.Equals(named, name, StringComparison.Ordinal))
            return Invalid($"pack.json must name the pack '{name}', its folder.", packFile, "MQ6001", "/name");
        var bytes = services.Json.Write(node, "pack.json", "templates/" + name + "/pack.json");
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var disk = await AtomicFile.ReadIfExistsAsync(full, ct).ConfigureAwait(false);
            if (Conflict(disk, expectedHash) is { } conflict)
                return conflict;
            await WriteGuardedAsync(services, full, bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }

        var read = await ReadAsync(services.Options, services.Schemas, snapshot, name, ct).ConfigureAwait(false);
        return new PackWriteResult(SaveOutcome.Saved, ContentHash.Of(bytes), null, read?.Diagnostics ?? [], ["pack.json"]);
    }

    /// <summary>
    /// Creates a pack (<c>POST /api/packs</c>): <c>empty</c> writes a <c>pack.json</c> with one <c>each entity</c> unit and its template;
    /// the name of a pack of this project copies that pack's files; <paramref name="starters"/> (the command line's built-in starters)
    /// supply other names. <c>conflict</c> when the folder exists; <c>invalid</c> for an unknown starter.
    /// </summary>
    public static async Task<PackWriteResult> CreateAsync(EngineServices services, string name, string from,
        IReadOnlyDictionary<string, IReadOnlyList<KeyValuePair<string, byte[]>>>? starters, CancellationToken ct)
    {
        var root = PackRoot(services.Options, name);
        var packFile = new ModelPaths(services.Options).ToRepoPath("templates/" + name + "/pack.json");
        if (Directory.Exists(root) || File.Exists(root))
            return new PackWriteResult(SaveOutcome.Conflict, null, null, [RuleCatalog.Create("MQ6001", $"templates/{name}/ already exists.", filePath: packFile)], []);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        JsonObject manifest;
        if (string.Equals(from, "empty", StringComparison.Ordinal))
        {
            manifest = new JsonObject
            {
                ["name"] = name,
                ["version"] = "0.1.0",
                ["engine"] = ">=1.0 <2.0",
                ["description"] = "A new template pack.",
                ["units"] = new JsonArray(new JsonObject
                {
                    ["id"] = "entity",
                    ["template"] = "entity.scriban",
                    ["for"] = "each entity",
                    ["output"] = "{{ kebab entity.name }}.txt",
                }),
            };
            files["entity.scriban"] = Encoding.UTF8.GetBytes("{{ entity.name }}\n{{~ for attribute in entity.attributes ~}}\n- {{ attribute.name }}\n{{~ end ~}}\n");
        }
        else
        {
            IEnumerable<KeyValuePair<string, byte[]>> source;
            var local = NamePattern().IsMatch(from) ? Path.Combine(new ModelPaths(services.Options).ModelRoot, "templates", from) : null;
            if (local is not null && File.Exists(Path.Combine(local, "pack.json")))
                source = EnumerateFiles(local).Select(p => new KeyValuePair<string, byte[]>(p, File.ReadAllBytes(Resolve(local, p))));
            else if (starters is not null && starters.TryGetValue(from, out var starter))
                source = starter;
            else
                return Invalid($"'{from}' is neither 'empty', a built-in starter nor a pack of this project.", packFile);
            JsonObject? found = null;
            foreach (var (path, bytes) in source)
            {
                if (path == "pack.json")
                    found = JsonNode.Parse(bytes, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
                else
                    files[path] = bytes;
            }

            manifest = found ?? throw new InvalidOperationException($"The starter '{from}' has no pack.json object.");
            manifest["name"] = name;
        }

        files["pack.json"] = services.Json.Write(manifest, "pack.json", "templates/" + name + "/pack.json");
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var (path, bytes) in files)
                await WriteGuardedAsync(services, Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)), bytes, ct).ConfigureAwait(false);
        }
        finally
        {
            WriteGate.Release();
        }

        return new PackWriteResult(SaveOutcome.Saved, ContentHash.Of(files["pack.json"]), null, [], [.. files.Keys]);
    }

    /// <summary>The folder's files, pack-relative, hidden segments left out, ordinal.</summary>
    public static IReadOnlyList<string> EnumerateFiles(string root) => PackFileRoles.EnumerateFiles(root);

    /// <summary>Roles and users of every file of a pack (<see cref="PackFileRoles.DescribeAsync"/>, shared with the loader).</summary>
    public static Task<IReadOnlyList<PackFileInfo>> DescribeFilesAsync(string root, PackManifest? manifest, CancellationToken ct) =>
        PackFileRoles.DescribeAsync(root, manifest, ct);

    private static IReadOnlyList<PackParameterInfo> Parameters(PackManifest? manifest, PackSettings settings)
    {
        var schema = manifest is null ? ImmutableSortedDictionary<string, JsonElement>.Empty : UnitRules.SchemaProperties(manifest);
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (manifest?.ParameterSchema is { ValueKind: JsonValueKind.Object } s && s.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array)
            required.UnionWith(list.EnumerateArray().Select(e => e.GetString() ?? ""));
        var names = new SortedSet<string>(StringComparer.Ordinal);
        names.UnionWith(schema.Keys);
        names.UnionWith(manifest?.Parameters.Keys ?? []);
        names.UnionWith(settings.Parameters.Keys);
        return [.. names.Select(n => new PackParameterInfo(n,
            manifest is not null && manifest.Parameters.TryGetValue(n, out var d) ? d : null,
            settings.Parameters.TryGetValue(n, out var v) ? v : null,
            schema.TryGetValue(n, out var p) ? p : null, required.Contains(n)))];
    }


    private static PackManifest? TryManifest(string root)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "pack.json")));
            return TryManifest(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    internal static PackManifest? TryManifest(JsonElement? document)
    {
        if (document is not { ValueKind: JsonValueKind.Object } element)
            return null;
        try
        {
            return element.Deserialize<PackManifest>(EngineJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ScopeAt(JsonElement root, string pointer)
    {
        var index = int.Parse(pointer.Split('/')[2], System.Globalization.CultureInfo.InvariantCulture);
        return root.TryGetProperty("units", out var units) && units.ValueKind == JsonValueKind.Array && index < units.GetArrayLength()
            && units[index].TryGetProperty("for", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    }

    private static void RefuseManifest(string path)
    {
        if (string.Equals(path, "pack.json", StringComparison.Ordinal))
            throw new PackPathException("pack.json is never written raw: save the whole document with PUT /api/packs/{pack} (save_pack), which keeps canonical form.");
    }

    private static PackWriteResult? Conflict(byte[]? disk, string? expectedHash)
    {
        var diskHash = disk is null ? null : ContentHash.Of(disk);
        if (string.Equals(diskHash, expectedHash, StringComparison.Ordinal))
            return null;
        string? text = null;
        if (disk is not null)
        {
            try
            {
                text = StrictUtf8.GetString(disk);
            }
            catch (DecoderFallbackException)
            {
            }
        }

        return new PackWriteResult(SaveOutcome.Conflict, diskHash, text, [], []);
    }

    private static async Task WriteGuardedAsync(EngineServices services, string full, byte[] bytes, CancellationToken ct)
    {
        var check = services.EnginePaths.CheckEngineWrite(WriteTarget.Model, full);
        if (!check.Allowed)
            throw new PackPathException($"The write to '{full}' is refused: {check.Reason}");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        await AtomicFile.WriteAsync(full, bytes, "pack", ct).ConfigureAwait(false);
    }

    private static PackWriteResult Invalid(string message, string file, string rule = "MQ6001", string? pointer = null) =>
        new(SaveOutcome.Invalid, null, null, [RuleCatalog.Create(rule, message, filePath: file, jsonPointer: pointer)], []);

    private static string RealPath(string path)
    {
        var info = Directory.Exists(path) ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
        if (info.LinkTarget is null)
        {
            var parent = Path.GetDirectoryName(path);
            return parent is null || parent == path ? path : Path.Combine(RealPath(parent), Path.GetFileName(path));
        }

        var target = info.ResolveLinkTarget(returnFinalTarget: true);
        return target is null ? path : RealPath(Path.TrimEndingDirectorySeparator(target.FullName));
    }
}
