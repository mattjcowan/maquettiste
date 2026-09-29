using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>A template pack manifest: <c>templates/&lt;pack&gt;/pack.json</c> (engine-design.md section 2.5).</summary>
public sealed record PackManifest
{
    /// <summary>The <c>$schema</c> value; the canonical writer rewrites it.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>The pack name; must equal its folder name.</summary>
    public required string Name { get; init; }

    /// <summary>The pack version (semver).</summary>
    public required string Version { get; init; }

    /// <summary>The engine version range the pack needs, for example <c>"&gt;=1.0 &lt;2.0"</c>.</summary>
    public required string Engine { get; init; }

    /// <summary>What the pack generates.</summary>
    public string? Description { get; init; }

    /// <summary>Parameter names with their default values.</summary>
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>
    /// The optional parameter schema (generation-ui.md section 3.2, E18): <c>properties</c> (a JSON Schema properties object, the subset
    /// extension schemas accept) and <c>required</c>; the editor builds the Parameters form from it and MQ6023 checks project values.
    /// </summary>
    public JsonElement? ParameterSchema { get; init; }

    /// <summary>Pack-relative script paths; empty means every <c>*.js</c> in the pack, in ordinal path order.</summary>
    public IReadOnlyList<string> Scripts { get; init; } = [];

    /// <summary>Whether templates read <c>schema_diff</c>, so the engine keeps schema snapshots.</summary>
    public bool UsesSchemaDiff { get; init; }

    /// <summary>The render units.</summary>
    public required IReadOnlyList<PackUnit> Units { get; init; }
}

/// <summary>One render unit of a pack: a template applied to a scope.</summary>
public sealed record PackUnit
{
    /// <summary>The unit id (<c>^[a-z][a-z0-9-]*$</c>), unique in the pack.</summary>
    public required string Id { get; init; }

    /// <summary>The pack-relative template path.</summary>
    public required string Template { get; init; }

    /// <summary><c>model</c>, <c>each package|entity|relation|enum|value object|table</c>, or <c>select &lt;name&gt;</c>.</summary>
    public required string For { get; init; }

    /// <summary>Filters over the scope's elements.</summary>
    public UnitWhere? Where { get; init; }

    /// <summary>A Scriban expression for the output path under <see cref="PackSettings.Output"/>; <see langword="null"/> means file blocks only.</summary>
    public string? Output { get; init; }

    /// <summary>The output mode.</summary>
    public OutputMode Mode { get; init; } = OutputMode.Overwrite;

    /// <summary>A formatter name, <c>"none"</c>, or <see langword="null"/> to choose by extension.</summary>
    public string? Formatter { get; init; }

    /// <summary>Custom template delimiters.</summary>
    public Delimiters? Delimiters { get; init; }

    /// <summary>The companion file; required when <see cref="Mode"/> is <see cref="OutputMode.Pair"/>.</summary>
    public PairCompanion? Companion { get; init; }

    /// <summary>Names of JavaScript pre-render transforms; results merge into <c>data</c>.</summary>
    public IReadOnlyList<string> Transforms { get; init; } = [];
}

/// <summary>Unit filters. List filters match any value; every filter that is set must match.</summary>
public sealed record UnitWhere
{
    /// <summary>Tag keys, any of which must be present.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Tag keys, none of which may be present.</summary>
    public IReadOnlyList<string> NotTags { get; init; } = [];

    /// <summary>Stereotype keys, any of which must be present.</summary>
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Stereotype keys, none of which may be present.</summary>
    public IReadOnlyList<string> NotStereotypes { get; init; } = [];

    /// <summary>Category ids or names; descendants match.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Package ids or qualified names; sub-packages match.</summary>
    public IReadOnlyList<string> Packages { get; init; } = [];

    /// <summary>Package ids or qualified names to exclude; sub-packages match.</summary>
    public IReadOnlyList<string> NotPackages { get; init; } = [];

    /// <summary>A database name: tables in it; entities and relations mapped (not ignored) in it.</summary>
    public string? Database { get; init; }

    /// <summary>Filters entities by abstractness.</summary>
    public bool? Abstract { get; init; }

    /// <summary>The name of a JavaScript filter.</summary>
    public string? Script { get; init; }
}

/// <summary>A unit's output mode (SPEC section 12).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<OutputMode>))]
public enum OutputMode
{
    /// <summary>Fully generated; rewritten when inputs change: <c>overwrite</c>.</summary>
    [JsonStringEnumMemberName("overwrite")] Overwrite,

    /// <summary>Written only when missing; owned afterwards: <c>once</c>.</summary>
    [JsonStringEnumMemberName("once")] Once,

    /// <summary>Regenerated, keeping protected regions: <c>regions</c>.</summary>
    [JsonStringEnumMemberName("regions")] Regions,

    /// <summary>A generated file plus a companion written once: <c>pair</c>.</summary>
    [JsonStringEnumMemberName("pair")] Pair,
}

/// <summary>Custom template delimiters.</summary>
public sealed record Delimiters
{
    /// <summary>The opening delimiter, for example <c>&lt;%</c>.</summary>
    public required string Open { get; init; }

    /// <summary>The closing delimiter, for example <c>%&gt;</c>.</summary>
    public required string Close { get; init; }
}

/// <summary>The companion of a <c>pair</c> unit.</summary>
public sealed record PairCompanion
{
    /// <summary>The pack-relative companion template.</summary>
    public required string Template { get; init; }

    /// <summary>A Scriban expression for the companion's output path.</summary>
    public required string Output { get; init; }
}

/// <summary>An extension schema: <c>extensions/*.json</c>. Not an element; it has no id.</summary>
public sealed record ExtensionSchema
{
    /// <summary>The <c>$schema</c> value; the canonical writer rewrites it.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>The extension's name.</summary>
    public required string Name { get; init; }

    /// <summary>What the extension adds.</summary>
    public string? Description { get; init; }

    /// <summary>Which elements the extension applies to.</summary>
    public required ExtensionTarget AppliesTo { get; init; }

    /// <summary>A JSON Schema <c>properties</c> object.</summary>
    public required JsonElement Properties { get; init; }

    /// <summary>Property names that are required.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];
}

/// <summary>
/// The elements an extension applies to: an element whose kind is listed (or any kind when <see cref="Kinds"/> is empty) and,
/// when <see cref="Stereotypes"/> is non-empty, that carries one of them.
/// </summary>
public sealed record ExtensionTarget
{
    /// <summary>Kind names (engine-design.md section 2.2) plus <c>attribute</c>, <c>enum-member</c> and <c>column</c>.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>Stereotype keys.</summary>
    public IReadOnlyList<string> Stereotypes { get; init; } = [];
}
