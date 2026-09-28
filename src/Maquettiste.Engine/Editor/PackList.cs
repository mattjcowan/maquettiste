using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine;

/// <summary>The result of <see cref="GenerationService.GetPacksAsync"/> (E2, phase2-design.md section 3.8).</summary>
/// <param name="Packs">The manifest of every pack under <c>templates/</c> whose <c>pack.json</c> parses and matches its schema, enabled or
/// not, in ordinal folder order (<c>settings.packs</c> says which run).</param>
/// <param name="Diagnostics">Load diagnostics: the full pack loader's for enabled packs (the ones a run would stop on), and the parse and
/// schema diagnostics of disabled packs, which a run never loads.</param>
public sealed record PackListResult(IReadOnlyList<PackManifest> Packs, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Lists the packs of a model folder for the editor (E2).</summary>
internal static class PackCatalog
{
    private const string InvalidPack = "MQ6001";

    /// <summary>Lists every pack folder with a <c>pack.json</c>.</summary>
    /// <param name="options">The engine options.</param>
    /// <param name="schemas">The schema registry.</param>
    /// <param name="loader">The pack loader, for the enabled packs' full diagnostics.</param>
    /// <param name="snapshot">The snapshot, for the pack settings.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The manifests and diagnostics.</returns>
    public static async Task<PackListResult> ListAsync(EngineOptions options, ISchemaRegistry schemas, IPackLoader loader, ModelSnapshot snapshot,
        CancellationToken ct)
    {
        var paths = new ModelPaths(options);
        var templates = Path.Combine(paths.ModelRoot, "templates");
        var folders = Directory.Exists(templates)
            ? Directory.EnumerateDirectories(templates).Select(Path.GetFileName).OfType<string>()
                .Where(n => !n.StartsWith('.') && File.Exists(Path.Combine(templates, n, "pack.json")))
                .Order(StringComparer.Ordinal).ToList()
            : [];

        // The enabled packs go through the loader a run uses, so the list reports exactly what a run would stop on.
        var loaded = await loader.LoadAsync(snapshot, null, null, ct).ConfigureAwait(false);
        var diagnostics = new List<Diagnostic>(loaded.Diagnostics);
        var manifests = new List<PackManifest>();
        foreach (var name in folders)
        {
            ct.ThrowIfCancellationRequested();
            var enabled = !snapshot.Settings.Packs.TryGetValue(name, out var settings) || settings.Enabled;
            var file = paths.ToRepoPath("templates/" + name + "/pack.json");
            var bytes = await File.ReadAllBytesAsync(Path.Combine(templates, name, "pack.json"), ct).ConfigureAwait(false);
            var (manifest, errors) = Read(schemas, bytes, file);
            if (manifest is not null)
                manifests.Add(manifest);
            if (!enabled)
                diagnostics.AddRange(errors);
        }

        return new PackListResult(manifests, [.. diagnostics.Distinct().OrderBy(d => d.FilePath ?? "", StringComparer.Ordinal).ThenBy(d => d.Line ?? 0)
            .ThenBy(d => d.Column ?? 0).ThenBy(d => d.Rule, StringComparer.Ordinal).ThenBy(d => d.Message, StringComparer.Ordinal)]);
    }

    private static (PackManifest? Manifest, IReadOnlyList<Diagnostic> Errors) Read(ISchemaRegistry schemas, byte[] bytes, string file)
    {
        var locator = new JsonPositionLocator();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var failures = schemas.Evaluate("pack.json", document.RootElement, file);
            if (failures.Count > 0)
            {
                return (null, [.. failures.Select(f =>
                {
                    var position = f.JsonPointer is null ? null : locator.Locate(bytes, f.JsonPointer);
                    return new Diagnostic(InvalidPack, DiagnosticSeverity.Error, f.Message, null, file, f.JsonPointer, position?.Line, position?.Column);
                })]);
            }

            return (document.RootElement.Deserialize<PackManifest>(EngineJson.Options), []);
        }
        catch (JsonException ex)
        {
            var position = locator.FromException(bytes, ex);
            return (null, [new Diagnostic(InvalidPack, DiagnosticSeverity.Error, $"pack.json is not valid JSON: {ex.Message}", null, file, null,
                position?.Line, position?.Column)]);
        }
    }
}
