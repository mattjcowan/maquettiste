using System.Text.Json;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// One project value of a pack parameter that an <c>x-ref</c> of the pack's <c>parameterSchema</c> types (next-phase-1-design.md
/// section 4.1): a string in <c>maquettiste.json</c> that holds an id.
/// </summary>
/// <param name="Pack">The pack (its folder name).</param>
/// <param name="Key">The parameter.</param>
/// <param name="Pointer">The string's pointer in <c>maquettiste.json</c>: <c>/packs/&lt;pack&gt;/parameters/&lt;key&gt;</c>, then the
/// path inside the value (an array item, an object member).</param>
/// <param name="Value">The string as written.</param>
/// <param name="Ref">The keyword that types it.</param>
/// <param name="Schema">The parameter's schema (<c>parameterSchema.properties.&lt;key&gt;</c>).</param>
/// <param name="Required">Whether <c>parameterSchema.required</c> names the parameter.</param>
internal sealed record PackParameterRef(string Pack, string Key, string Pointer, string Value, XRef Ref, JsonElement Schema, bool Required)
{
    /// <summary>The pointer of the parameter's entry in <c>maquettiste.json</c>.</summary>
    public string EntryPointer => "/packs/" + JsonPointer.Escape(Pack) + "/parameters/" + JsonPointer.Escape(Key);
}

/// <summary>An <c>of</c> id of an <c>x-ref</c> in a pack's <c>parameterSchema</c>.</summary>
/// <param name="Pack">The pack.</param>
/// <param name="Path">The repo path of its <c>pack.json</c>.</param>
/// <param name="Pointer">The <c>of</c> keyword's pointer in it.</param>
/// <param name="Of">The element id it names.</param>
internal sealed record PackParameterOf(string Pack, string Path, string Pointer, string Of);

/// <summary>
/// The <c>x-ref</c> sites of every local pack's <c>parameterSchema</c> and the project's values for them (next-phase-1-design.md
/// section 4.1, "Pack parameter values"). Deletes, and saves that remove a sub-element, read it to refuse or remove a value that
/// names what goes. It reads only <c>templates/&lt;pack&gt;/pack.json</c> (never a template), as the delete plan's pack filter check
/// does; a manifest that does not parse is the pack loader's to report and adds nothing. Immutable.
/// </summary>
internal sealed class PackParameterRefs
{
    private PackParameterRefs(IReadOnlyList<PackParameterRef> values, IReadOnlyList<PackParameterOf> ofs)
    {
        Values = values;
        Ofs = ofs;
    }

    /// <summary>No pack, or no typed parameter.</summary>
    public static PackParameterRefs Empty { get; } = new([], []);

    /// <summary>The typed values, by pack (ordinal), then parameter (ordinal), then the value's own order.</summary>
    public IReadOnlyList<PackParameterRef> Values { get; }

    /// <summary>The <c>of</c> ids of the packs' keywords, by pack, in file order.</summary>
    public IReadOnlyList<PackParameterOf> Ofs { get; }

    /// <summary>Reads the local packs' manifests and the project's values.</summary>
    /// <param name="paths">The model paths.</param>
    /// <param name="settings">The project settings holding the values.</param>
    /// <returns>The sites.</returns>
    public static PackParameterRefs Read(ModelPaths paths, ProjectSettings settings)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(settings);
        var templates = Path.Combine(paths.ModelRoot, "templates");
        if (!Directory.Exists(templates))
            return Empty;
        var values = new List<PackParameterRef>();
        var ofs = new List<PackParameterOf>();
        var found = new List<(string Pointer, string Value, XRef Ref)>();
        foreach (var folder in Directory.EnumerateDirectories(templates).Order(StringComparer.Ordinal))
        {
            var pack = Path.GetFileName(folder);
            var manifest = Path.Combine(folder, "pack.json");
            if (pack.StartsWith('.') || !File.Exists(manifest))
                continue;
            JsonElement schema;
            try
            {
                using var document = JsonDocument.Parse(DocumentReader.StripBom(File.ReadAllBytes(manifest)), DocumentReader.ParseOptions);
                if (!document.RootElement.TryGetProperty("parameterSchema", out var parameterSchema) || parameterSchema.ValueKind != JsonValueKind.Object)
                    continue;
                schema = parameterSchema.Clone();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                continue; // an unreadable manifest is the pack loader's to report
            }

            if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
                continue;
            var (refs, keywords, _) = PropertySchemaSet.Read(properties, "/parameterSchema/properties", static (_, _) => { });
            var path = paths.ToRepoPath("templates/" + pack + "/pack.json");
            foreach (var (pointer, xref) in keywords)
            {
                if (xref.Of is { } of && of != XRef.ElementScope)
                    ofs.Add(new PackParameterOf(pack, path, pointer + "/of", of));
            }

            if (refs.Count == 0 || !settings.Packs.TryGetValue(pack, out var packSettings))
                continue;
            var required = schema.TryGetProperty("required", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal)
                : [];
            foreach (var (key, value) in packSettings.Parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!refs.TryGetValue(key, out var node))
                    continue;
                found.Clear();
                PropertySchemaSet.Walk(node, value, "/packs/" + JsonPointer.Escape(pack) + "/parameters/" + JsonPointer.Escape(key), found);
                foreach (var (pointer, text, xref) in found)
                    values.Add(new PackParameterRef(pack, key, pointer, text, xref, properties.GetProperty(key), required.Contains(key)));
            }
        }

        return values.Count == 0 && ofs.Count == 0 ? Empty : new PackParameterRefs(values, ofs);
    }
}
