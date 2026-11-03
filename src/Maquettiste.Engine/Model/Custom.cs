using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// An element of a custom kind the project declares (next-phase-2-design.md section 4.2; <c>model/custom/&lt;type&gt;/</c>). The engine's
/// keys sit where they sit on every element; the declared fields sit under <see cref="Content"/>, checked against the custom kind file
/// <c>extensions/kinds/&lt;type&gt;.json</c> (MQ3033). No database resolves a custom element.
/// </summary>
public sealed record Custom : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Custom;

    /// <summary>The id of the owning package, or <see langword="null"/> for the root. Membership only.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The custom kind's type key: the stem of a file under <c>extensions/kinds/</c> (MQ1019).</summary>
    public required string Type { get; init; }

    /// <summary>
    /// The values of the declared fields, as written. Free-form for the canonical writer (keys in ordinal order at every level), so
    /// loading and formatting never read the custom kind file.
    /// </summary>
    public IReadOnlyDictionary<string, JsonElement> Content { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;
}

/// <summary>
/// A custom kind a project declares in <c>extensions/kinds/&lt;type&gt;.json</c> (next-phase-2-design.md section 4.2): the shape of the
/// content of its elements, with the dialect of extension files plus <c>x-member</c>.
/// </summary>
public sealed record CustomKind
{
    /// <summary>The type key, equal to the file stem (MQ1017).</summary>
    public required string Type { get; init; }

    /// <summary>The display name, or <see langword="null"/>; editors and templates fall back to <see cref="Type"/>.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The plural, or <see langword="null"/>; falls back to the inflected display name.</summary>
    public string? PluralName { get; init; }

    /// <summary>Markdown text, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>The JSON Schema <c>properties</c> object: one schema per content key.</summary>
    public required JsonElement Properties { get; init; }

    /// <summary>The content keys an element must hold.</summary>
    public IReadOnlyList<string> Required { get; init; } = [];
}

/// <summary>A loaded custom kind file.</summary>
/// <param name="Kind">The declaration.</param>
/// <param name="Path">The repo-relative path (<c>.maquettiste/extensions/kinds/&lt;type&gt;.json</c>).</param>
/// <param name="Hash">The content hash of the file.</param>
public sealed record CustomKindDocument(CustomKind Kind, string Path, string Hash);
