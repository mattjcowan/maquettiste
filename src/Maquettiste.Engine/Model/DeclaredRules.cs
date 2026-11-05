using System.Text.Json;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Model;

/// <summary>A rule declaration file, <c>extensions/rules/*.json</c> (next-phase-2-design.md section 5.6).</summary>
public sealed record DeclaredRuleFile
{
    /// <summary>The rules the file declares.</summary>
    public IReadOnlyList<DeclaredRule> Rules { get; init; } = [];
}

/// <summary>One declared rule: which resolved objects it judges, what must hold of each, and the finding where it does not.</summary>
public sealed record DeclaredRule
{
    /// <summary>The rule's id; its findings are <c>x/&lt;id&gt;</c>.</summary>
    public required string Id { get; init; }

    /// <summary>The findings' severity, unless <c>validation.rules</c> sets another.</summary>
    public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

    /// <summary>What the rule checks, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>Which resolved objects the rule judges.</summary>
    public required RuleSelector Select { get; init; }

    /// <summary>What must hold for each selected object.</summary>
    public required RuleCondition Require { get; init; }

    /// <summary>The finding's text, with placeholders.</summary>
    public required string Message { get; init; }
}

/// <summary>Which resolved objects a rule judges. Each key reads one value or a list into a list; a list means any of its values.</summary>
public sealed record RuleSelector
{
    /// <summary>The resolved kinds.</summary>
    [JsonConverter(typeof(RuleValuesConverter))]
    public required IReadOnlyList<string> Kind { get; init; }

    /// <summary>Stereotype keys, matched against the effective set.</summary>
    [JsonConverter(typeof(RuleValuesConverter))]
    public IReadOnlyList<string> Stereotype { get; init; } = [];

    /// <summary>Tag keys.</summary>
    [JsonConverter(typeof(RuleValuesConverter))]
    public IReadOnlyList<string> Tag { get; init; } = [];

    /// <summary>Custom property names that must hold a non-null value.</summary>
    [JsonConverter(typeof(RuleValuesConverter))]
    public IReadOnlyList<string> Property { get; init; } = [];

    /// <summary>Package ids; nested packages match too.</summary>
    [JsonConverter(typeof(RuleValuesConverter))]
    public IReadOnlyList<string> Package { get; init; } = [];
}

/// <summary>A condition of a declared rule. Exactly one member is set.</summary>
public sealed record RuleCondition
{
    /// <summary>Holds when every condition holds.</summary>
    public IReadOnlyList<RuleCondition>? All { get; init; }

    /// <summary>Holds when at least one condition holds.</summary>
    public IReadOnlyList<RuleCondition>? Any { get; init; }

    /// <summary>Holds when the condition does not.</summary>
    public RuleCondition? Not { get; init; }

    /// <summary>Holds when the value at the path is present.</summary>
    public string? Has { get; init; }

    /// <summary>Holds when the value at the path equals the value.</summary>
    [JsonPropertyName("equals")]
    public RuleEquality? Equal { get; init; }

    /// <summary>Holds when the number of matching items of a list is within bounds.</summary>
    public RuleCount? Count { get; init; }

    /// <summary>Holds when the object links to a number of objects of a kind within bounds.</summary>
    public RuleReferences? References { get; init; }

    /// <summary>Holds when a number of objects of a kind within bounds link to the object.</summary>
    public RuleReferences? ReferencedBy { get; init; }

    /// <summary>
    /// Holds when the expression, which reads the selected object's members (<see cref="Expr.Member"/>), is true; judged as a guard
    /// (next-phase-3-design.md section 3.1).
    /// </summary>
    public Expr? Expression { get; init; }
}

/// <summary>The <c>equals</c> test.</summary>
/// <param name="Path">The path of the value.</param>
/// <param name="Value">The value it must equal.</param>
public sealed record RuleEquality(string Path, JsonElement Value);

/// <summary>The <c>count</c> test.</summary>
/// <param name="Of">The path of a list of objects.</param>
/// <param name="Where">The condition each counted item meets, relative to the item; all items when <see langword="null"/>.</param>
/// <param name="Min">The least count; 1 when both bounds are absent.</param>
/// <param name="Max">The greatest count.</param>
public sealed record RuleCount(string Of, RuleCondition? Where, int? Min, int? Max);

/// <summary>The <c>references</c> and <c>referencedBy</c> tests.</summary>
/// <param name="Kind">The kinds of the linked objects, as the index names them.</param>
/// <param name="Field">The reference's field, or <see langword="null"/> for any.</param>
/// <param name="Min">The least count; 1 when absent.</param>
/// <param name="Max">The greatest count.</param>
/// <param name="ExcludeRetired">Whether a linked object that is retired, or sits in a retired element, does not count.</param>
public sealed record RuleReferences([property: JsonConverter(typeof(RuleValuesConverter))] IReadOnlyList<string> Kind, string? Field, int? Min, int? Max, bool ExcludeRetired = false);

/// <summary>A loaded rule declaration file.</summary>
/// <param name="Rules">Its rules.</param>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Hash">The file hash.</param>
public sealed record DeclaredRuleDocument(DeclaredRuleFile Rules, string Path, string Hash);

/// <summary>Reads one string or a list of strings into a list; writes a list.</summary>
internal sealed class RuleValuesConverter : JsonConverter<IReadOnlyList<string>>
{
    /// <inheritdoc/>
    public override IReadOnlyList<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return [reader.GetString()!];
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected a string or a list of strings.");
        var values = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected a string.");
            values.Add(reader.GetString()!);
        }

        return values;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<string> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (var item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
