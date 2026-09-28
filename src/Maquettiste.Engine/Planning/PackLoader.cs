using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Discovers and loads local template packs (W6; engine-design.md section 8): every <c>&lt;ModelRoot&gt;/templates/&lt;name&gt;/pack.json</c>
/// whose <c>name</c> equals its folder, in ordinal name order, minus those <c>packs.&lt;name&gt;.enabled: false</c> turns off. Each
/// <c>pack.json</c> is validated against <c>pack.json</c> (MQ6001), its engine range checked (MQ6002), its unit ids, template and
/// companion paths, script list and <c>types/*.json</c> type maps checked (MQ6001). A pack with any error is left out of
/// <see cref="PackSet.Packs"/> and its diagnostics are returned; the orchestrator stops the run on them, so a broken pack never has
/// its outputs orphaned. Shared packs by git reference are phase 4.
/// </summary>
/// <param name="options">The engine options.</param>
/// <param name="schemas">The schema registry (pack.json validation).</param>
internal sealed class PackLoader(EngineOptions options, ISchemaRegistry schemas) : IPackLoader
{
    private const string InvalidPack = "MQ6001";
    private const string EngineMismatch = "MQ6002";
    private static readonly UTF8Encoding StrictUtf8 = new(false, throwOnInvalidBytes: true);

    /// <inheritdoc/>
    public async Task<PackSet> LoadAsync(ModelSnapshot model, IReadOnlyCollection<string>? packNames, IProgress<ProgressUpdate>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(model);
        var paths = new ModelPaths(options);
        var templates = Path.Combine(paths.ModelRoot, "templates");
        var diagnostics = new List<Diagnostic>();
        var folders = Directory.Exists(templates)
            ? Directory.EnumerateDirectories(templates).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('.') && File.Exists(Path.Combine(templates, n, "pack.json")))
                .Order(StringComparer.Ordinal).ToList()
            : [];

        HashSet<string>? requested = null;
        if (packNames is not null)
        {
            requested = new HashSet<string>(packNames, StringComparer.Ordinal);
            foreach (var name in requested.Order(StringComparer.Ordinal).Where(n => !folders.Contains(n, StringComparer.Ordinal)))
                diagnostics.Add(RuleCatalog.Create(InvalidPack, $"Pack '{name}' does not exist: there is no templates/{name}/pack.json.",
                    filePath: paths.ToRepoPath("templates/" + name + "/pack.json")));
        }

        var candidates = folders.Where(n => requested is null || requested.Contains(n)).ToList();
        var loaded = new List<LoadedPack>();
        for (var i = 0; i < candidates.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var name = candidates[i];
            var settings = model.Settings.Packs.TryGetValue(name, out var s) ? s : new PackSettings();
            var relative = paths.ToRepoPath("templates/" + name);
            progress?.Report(new ProgressUpdate(PipelineStage.Plan, i, candidates.Count, relative + "/pack.json", name));
            if (!settings.Enabled)
                continue;
            var pack = await LoadOneAsync(name, loaded.Count, Path.Combine(templates, name), relative, settings, diagnostics, ct).ConfigureAwait(false);
            if (pack is not null)
                loaded.Add(pack);
        }

        progress?.Report(new ProgressUpdate(PipelineStage.Plan, candidates.Count, candidates.Count, null, null));
        return new PackSet(loaded, Sort(diagnostics));
    }

    /// <summary>Sorts diagnostics as reports do: path, line, column, rule, message.</summary>
    /// <param name="diagnostics">The diagnostics.</param>
    /// <returns>The sorted list.</returns>
    internal static IReadOnlyList<Diagnostic> Sort(IEnumerable<Diagnostic> diagnostics) =>
        [.. diagnostics.OrderBy(d => d.FilePath ?? "", StringComparer.Ordinal).ThenBy(d => d.Line ?? 0).ThenBy(d => d.Column ?? 0)
            .ThenBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)];

    private async Task<LoadedPack?> LoadOneAsync(string name, int order, string root, string relative, PackSettings settings,
        List<Diagnostic> diagnostics, CancellationToken ct)
    {
        var packFile = relative + "/pack.json";
        var bytes = await File.ReadAllBytesAsync(Path.Combine(root, "pack.json"), ct).ConfigureAwait(false);
        var errors = new List<Diagnostic>();
        var locator = new JsonPositionLocator();
        void Error(string rule, string message, string? pointer)
        {
            var position = pointer is null ? null : locator.Locate(bytes, pointer);
            errors.Add(new Diagnostic(rule, DiagnosticSeverity.Error, message, null, packFile, pointer, position?.Line, position?.Column));
        }

        PackManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            foreach (var failure in schemas.Evaluate("pack.json", document.RootElement, packFile))
                Error(InvalidPack, failure.Message, failure.JsonPointer);
            if (errors.Count > 0)
            {
                diagnostics.AddRange(errors);
                return null;
            }

            manifest = document.RootElement.Deserialize<PackManifest>(EngineJson.Options)!;
        }
        catch (JsonException ex)
        {
            var position = locator.FromException(bytes, ex);
            diagnostics.Add(new Diagnostic(InvalidPack, DiagnosticSeverity.Error, $"pack.json is not valid JSON: {ex.Message}", null, packFile, null,
                position?.Line, position?.Column));
            return null;
        }

        if (!string.Equals(manifest.Name, name, StringComparison.Ordinal))
            Error(InvalidPack, $"pack.json names the pack '{manifest.Name}', but its folder is '{name}'; the name must equal the folder.", "/name");

        if (!EngineRange.Satisfies(manifest.Engine, EngineVersion.Value, out var validRange))
        {
            Error(validRange ? EngineMismatch : InvalidPack, validRange
                ? $"Pack '{name}' requires engine {manifest.Engine}; this engine is {EngineVersion.Value}."
                : $"Pack '{name}' has an engine range that cannot be parsed: '{manifest.Engine}'.", "/engine");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < manifest.Units.Count; i++)
        {
            var unit = manifest.Units[i];
            var pointer = "/units/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (!ids.Add(unit.Id))
                Error(InvalidPack, $"Unit id '{unit.Id}' is used more than once in pack '{name}'.", pointer + "/id");
            CheckTemplate(root, unit.Template, pointer + "/template", Error);
            if (unit.Companion is { } companion)
                CheckTemplate(root, companion.Template, pointer + "/companion/template", Error);
            if (string.Equals(unit.For, "model", StringComparison.Ordinal) && unit.Where is { } where && HasElementFilters(where))
                Error(InvalidPack, $"Unit '{unit.Id}' is for 'model', which has no element: its 'where' can only name a database.", pointer + "/where");
        }

        var scripts = await LoadScriptsAsync(root, relative, manifest, Error, ct).ConfigureAwait(false);
        var typeMaps = await LoadTypeMapsAsync(root, relative, errors, ct).ConfigureAwait(false);

        if (errors.Count > 0)
        {
            diagnostics.AddRange(errors);
            return null;
        }

        var parameters = ImmutableSortedDictionary.CreateBuilder<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (key, value) in manifest.Parameters)
            parameters[key] = value;
        foreach (var (key, value) in settings.Parameters)
            parameters[key] = value;

        using var scriptsHash = new HashBuilder();
        scriptsHash.Add("mq-scripts-1").Add(scripts.Count);
        foreach (var script in scripts)
            scriptsHash.Add(script.Path).Add(script.Hash);

        return new LoadedPack(name, order, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), relative, manifest, settings,
            parameters.ToImmutable(), scripts, scriptsHash.Finish(), typeMaps);
    }

    private static bool HasElementFilters(UnitWhere where) =>
        where.Tags.Count > 0 || where.NotTags.Count > 0 || where.Stereotypes.Count > 0 || where.NotStereotypes.Count > 0
        || where.Categories.Count > 0 || where.Packages.Count > 0 || where.NotPackages.Count > 0 || where.Abstract is not null || where.Script is not null;

    private static void CheckTemplate(string root, string template, string pointer, Action<string, string, string?> error)
    {
        var full = PackFiles.Resolve(root, template);
        if (full is null)
            error(InvalidPack, $"Template path '{template}' must be a relative path inside the pack folder.", pointer);
        else if (!File.Exists(full))
            error(InvalidPack, $"Template '{template}' does not exist in the pack folder.", pointer);
    }

    private static async Task<IReadOnlyList<ScriptSource>> LoadScriptsAsync(string root, string relative, PackManifest manifest,
        Action<string, string, string?> error, CancellationToken ct)
    {
        var files = new List<(string Relative, string Full)>();
        if (manifest.Scripts.Count == 0)
        {
            foreach (var full in Directory.EnumerateFiles(root, "*.js", SearchOption.AllDirectories))
            {
                var packRelative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
                if (packRelative.Split('/').Any(segment => segment.StartsWith('.')))
                    continue;
                files.Add((packRelative, full));
            }

            files.Sort((a, b) => string.CompareOrdinal(a.Relative, b.Relative));
        }
        else
        {
            for (var i = 0; i < manifest.Scripts.Count; i++)
            {
                var path = manifest.Scripts[i];
                var pointer = "/scripts/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var full = PackFiles.Resolve(root, path);
                if (full is null)
                    error(InvalidPack, $"Script path '{path}' must be a relative path inside the pack folder.", pointer);
                else if (!File.Exists(full))
                    error(InvalidPack, $"Script '{path}' does not exist in the pack folder.", pointer);
                else if (files.Any(f => string.Equals(f.Relative, path, StringComparison.Ordinal)))
                    error(InvalidPack, $"Script '{path}' is listed more than once.", pointer);
                else
                    files.Add((path, full));
            }
        }

        var scripts = new List<ScriptSource>(files.Count);
        foreach (var (packRelative, full) in files)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            var path = relative + "/" + packRelative;
            try
            {
                scripts.Add(new ScriptSource(path, StrictUtf8.GetString(bytes), ContentHash.Of(bytes)));
            }
            catch (DecoderFallbackException)
            {
                error(InvalidPack, $"Script '{packRelative}' is not valid UTF-8.", null);
            }
        }

        return scripts;
    }

    private static async Task<IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>>> LoadTypeMapsAsync(string root, string relative,
        List<Diagnostic> errors, CancellationToken ct)
    {
        var folder = Path.Combine(root, "types");
        var maps = ImmutableSortedDictionary.CreateBuilder<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (!Directory.Exists(folder))
            return maps.ToImmutable();
        foreach (var full in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Path.GetFileName(full);
            if (fileName.StartsWith('.'))
                continue;
            var target = Path.GetFileNameWithoutExtension(full);
            var path = relative + "/types/" + fileName;
            var bytes = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(bytes);
                if (document.RootElement.ValueKind != JsonValueKind.Object)
                {
                    errors.Add(new Diagnostic(InvalidPack, DiagnosticSeverity.Error, "A type map must be a JSON object of strings.", null, path, "", 1, 1));
                    continue;
                }

                var map = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
                var valid = true;
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Name == "$schema")
                        continue;
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        var pointer = "/" + JsonPointer.Escape(property.Name);
                        var position = new JsonPositionLocator().Locate(bytes, pointer);
                        errors.Add(new Diagnostic(InvalidPack, DiagnosticSeverity.Error, $"Type map entry '{property.Name}' must be a string.",
                            null, path, pointer, position?.Line, position?.Column));
                        valid = false;
                        continue;
                    }

                    map[property.Name] = property.Value.GetString()!;
                }

                if (valid)
                    maps[target] = map.ToImmutable();
            }
            catch (JsonException ex)
            {
                var position = new JsonPositionLocator().FromException(bytes, ex);
                errors.Add(new Diagnostic(InvalidPack, DiagnosticSeverity.Error, $"Type map is not valid JSON: {ex.Message}", null, path, null,
                    position?.Line, position?.Column));
            }
        }

        return maps.ToImmutable();
    }
}
