using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// An entity's <c>revisions</c> block (revisions-design.md decisions 1 to 11 and 32 to 36): what revisions of the type look like, where
/// the current one is held, and what a revise does to an instance. Every member is optional and left out of the file when unset; a
/// derived entity inherits its bases' block and overrides it item by item (decision 9), which the resolved <c>entity.revisions</c> merges.
/// This is the block as written, not merged.
/// </summary>
public sealed record EntityRevisions
{
    /// <summary>One to three levels, from the most significant; <see langword="null"/> takes a base's (MQ8301 when the family names none).</summary>
    public IReadOnlyList<RevisionLevel>? Levels { get; init; }

    /// <summary>
    /// Written between two levels that both hold a value (decision 4); <see langword="null"/> takes a base's, else <see cref="DefaultSeparator"/>.
    /// It may be empty only between a letters level and a numbers level (MQ8303).
    /// </summary>
    public string? Separator { get; init; }

    /// <summary>
    /// The id of the attribute that holds the current revision (decisions 10 and 11), which only revising moves; <see langword="null"/> takes
    /// a base's, else the team's system holds it.
    /// </summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public string? BoundAttribute { get; init; }

    /// <summary>What a revise does to each listed lifecycle that governs the type (decision 33); a lifecycle not listed starts over.</summary>
    public IReadOnlyList<RevisionLifecycleEntry>? Lifecycles { get; init; }

    /// <summary>What a revise does to each listed attribute the type holds (decision 34); an attribute not listed is copied.</summary>
    public IReadOnlyList<RevisionAttributeEntry>? Attributes { get; init; }

    /// <summary>What a revise does to the links through each listed relation end the type reaches (decision 35); an end not listed is the team's.</summary>
    public IReadOnlyList<RevisionLinkEntry>? Links { get; init; }

    /// <summary>The separator when neither the block nor a base writes one.</summary>
    public const string DefaultSeparator = ".";
}

/// <summary>
/// One level of a revision sequence (revisions-design.md decisions 2 and 3; section 6): a run of letters, of whole numbers, or a list of
/// values. Exactly one of <see cref="Format"/> and <see cref="Values"/> is given (MQ8301). Levels have no ids: lifecycles and steps name a
/// level by its number, 1 to 3 (decision 21).
/// </summary>
public sealed record RevisionLevel
{
    /// <summary>A label for templates, docs and the editor, unique among the levels; without one the level is "level n".</summary>
    public string? Name { get; init; }

    /// <summary><see cref="RevisionFormat.Letters"/> or <see cref="RevisionFormat.Numbers"/>; <see langword="null"/> for a list.</summary>
    public RevisionFormat? Format { get; init; }

    /// <summary>A list of values, used in order; the level ends at its last value. <see langword="null"/> for a format.</summary>
    public IReadOnlyList<string>? Values { get; init; }

    /// <summary>
    /// The level's first value, a JSON string (letters) or a non-negative integer (numbers); <see langword="null"/> takes the format's
    /// default: the first letter not skipped, or 1. Not beside <see cref="Values"/> (MQ8301). <see cref="FirstText"/> reads it as text.
    /// </summary>
    public JsonElement? First { get; init; }

    /// <summary>Letters never used, at any position, for a letters level only (MQ8301).</summary>
    public IReadOnlyList<string>? Skip { get; init; }

    /// <summary>For a numbers level only (MQ8301): the width numbers are padded to with zeros, 1 to 9; a value past it grows.</summary>
    public int? Width { get; init; }

    /// <summary>
    /// Whether the level holds nothing at first, and again whenever a higher level moves (decision 3); <see langword="null"/> is false. Every
    /// level below one that starts empty starts empty too (MQ8301).
    /// </summary>
    public bool? StartEmpty { get; init; }

    /// <summary><see cref="First"/> as written: a string as it is, an integer in its digits; <see langword="null"/> when absent.</summary>
    [JsonIgnore]
    public string? FirstText => First switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString(),
        { ValueKind: JsonValueKind.Number } number => number.TryGetInt64(out var n) ? n.ToString(CultureInfo.InvariantCulture) : number.GetRawText(),
        _ => null,
    };
}

/// <summary>What a revision level runs through (revisions-design.md decision 3).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<RevisionFormat>))]
public enum RevisionFormat
{
    /// <summary>Upper case A to Z, then AA, AB and on, as spreadsheet columns, so the level never runs out (<c>letters</c>).</summary>
    [JsonStringEnumMemberName("letters")] Letters,

    /// <summary>Whole numbers from the first, with no end (<c>numbers</c>).</summary>
    [JsonStringEnumMemberName("numbers")] Numbers,
}

/// <summary>What a revise does to one lifecycle that governs the type (revisions-design.md decision 33).</summary>
public sealed record RevisionLifecycleEntry
{
    /// <summary>The <see cref="OnRevise"/> word for going back to the initial state.</summary>
    public const string Restart = "restart";

    /// <summary>The <see cref="OnRevise"/> word for keeping the states as they are, with what is collected in them.</summary>
    public const string Keep = "keep";

    /// <summary>The id of a lifecycle that governs the entity or a derived entity (MQ8312).</summary>
    [ElementRef(ElementKind.Process)]
    public required string Lifecycle { get; init; }

    /// <summary>
    /// <see cref="Restart"/>, <see cref="Keep"/>, or the id of a state of the lifecycle, entered as a restart enters the initial one. State
    /// ids are upper-case ULIDs, so they never clash with the two words. A state id is a reference; the two words are none.
    /// </summary>
    [ElementRef(IndexKinds = ["state"], Words = [Restart, Keep])]
    public required string OnRevise { get; init; }
}

/// <summary>What a revise does to one attribute the type holds (revisions-design.md decision 34).</summary>
public sealed record RevisionAttributeEntry
{
    /// <summary>The id of an attribute the entity holds: its own, an inherited one, or one from a stereotype it carries (MQ8312).</summary>
    [ElementRef(IndexKinds = ["attribute"])]
    public required string Attribute { get; init; }

    /// <summary>What the new revision's value is.</summary>
    public required AttributeOnRevise OnRevise { get; init; }
}

/// <summary>What a new revision's value of an attribute is (revisions-design.md decision 34).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AttributeOnRevise>))]
public enum AttributeOnRevise
{
    /// <summary>The earlier value, as for an attribute not listed; a derived type undoes a base's entry with it (<c>copy</c>).</summary>
    [JsonStringEnumMemberName("copy")] Copy,

    /// <summary>The attribute's default, else empty (<c>reset</c>).</summary>
    [JsonStringEnumMemberName("reset")] Reset,

    /// <summary>Empty (<c>clear</c>).</summary>
    [JsonStringEnumMemberName("clear")] Clear,
}

/// <summary>What a revise does to the links through one relation end the type reaches (revisions-design.md decision 35).</summary>
public sealed record RevisionLinkEntry
{
    /// <summary>The id of a relation end the entity navigates to (MQ8312).</summary>
    [ElementRef(IndexKinds = ["end"])]
    public required string End { get; init; }

    /// <summary>What happens to the links through it.</summary>
    public required LinkOnRevise OnRevise { get; init; }
}

/// <summary>What happens to a link on a revise (revisions-design.md decision 35).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LinkOnRevise>))]
public enum LinkOnRevise
{
    /// <summary>The earlier revision keeps the link, and the new one has none (<c>stay</c>).</summary>
    [JsonStringEnumMemberName("stay")] Stay,

    /// <summary>The link goes with the new revision as it is (<c>move</c>).</summary>
    [JsonStringEnumMemberName("move")] Move,

    /// <summary>The new revision gets a link of its own, and the earlier one keeps its link (<c>duplicate</c>; MQ8308 where the type's own end has max 1).</summary>
    [JsonStringEnumMemberName("duplicate")] Duplicate,
}

/// <summary>
/// A scenario step's <c>revision</c> (revisions-design.md decisions 14 and 40): a chosen name, a later value the sequence can form, or the
/// level that moves, 1 to 3. In JSON it is the bare string or the bare integer. Exactly one of <see cref="Name"/> and <see cref="Level"/>
/// is set.
/// </summary>
[JsonConverter(typeof(RevisionChoiceConverter))]
public readonly record struct RevisionChoice
{
    private RevisionChoice(string? name, int? level)
    {
        Name = name;
        Level = level;
    }

    /// <summary>The chosen name, for a string; <see langword="null"/> for a level.</summary>
    public string? Name { get; }

    /// <summary>The level, for an integer; <see langword="null"/> for a chosen name.</summary>
    public int? Level { get; }

    /// <summary>A chosen name.</summary>
    /// <param name="name">The name, a value of the sequence.</param>
    /// <returns>The choice.</returns>
    public static RevisionChoice FromName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return new(name, null);
    }

    /// <summary>A level.</summary>
    /// <param name="level">The level, 1 to 3.</param>
    /// <returns>The choice.</returns>
    public static RevisionChoice FromLevel(int level) => new(null, level);

    /// <summary>The value as a plain object: the name, or the level.</summary>
    /// <returns>A <see cref="string"/> or an <see cref="int"/>.</returns>
    public object ToPlain() => Name is { } name ? name : Level ?? 0;
}

/// <summary>Reads and writes a <see cref="RevisionChoice"/> as a bare string (a chosen name) or a bare integer (a level).</summary>
public sealed class RevisionChoiceConverter : JsonConverter<RevisionChoice>
{
    /// <inheritdoc/>
    public override RevisionChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => RevisionChoice.FromName(reader.GetString()!),
        JsonTokenType.Number when reader.TryGetInt32(out var level) => RevisionChoice.FromLevel(level),
        _ => throw new JsonException("A step's revision is a chosen name (a string) or a level (an integer, 1 to 3)."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, RevisionChoice value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.Name is { } name)
            writer.WriteStringValue(name);
        else
            writer.WriteNumberValue(value.Level ?? 0);
    }
}
