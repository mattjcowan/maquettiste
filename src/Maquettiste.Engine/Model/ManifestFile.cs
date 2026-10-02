using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A per-pack manifest file: <c>.maquettiste/manifest/&lt;pack&gt;.json</c>, for every output root (engine-design.md section 12.2).
/// </summary>
public sealed record ManifestFile
{
    /// <summary>The <c>$schema</c> value; the canonical writer rewrites it.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>The pack name.</summary>
    public required string Pack { get; init; }

    /// <summary>
    /// One <c>[path, hash, unit]</c> triple per generated file, sorted by path in ordinal UTF-8 order. A hash prefixed
    /// <c>r:</c> is a regions skeleton hash; <c>o:</c> marks an owned file.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Files { get; init; } = [];
}
