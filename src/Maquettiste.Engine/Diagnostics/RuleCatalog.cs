using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace Maquettiste.Engine.Diagnostics;

/// <summary>A built-in rule: its id, default severity and short description.</summary>
/// <param name="Id">The rule id, for example <c>MQ2001</c>.</param>
/// <param name="DefaultSeverity">The severity unless <c>validation.rules</c> overrides it.</param>
/// <param name="Description">A short description, used for SARIF <c>rules[]</c>.</param>
public sealed record RuleInfo(string Id, DiagnosticSeverity DefaultSeverity, string Description)
{
    /// <summary>Whether <c>validation.rules</c> may turn the rule off (every rule except MQ1xxx).</summary>
    public bool CanBeDisabled => !Id.StartsWith("MQ1", StringComparison.Ordinal);
}

/// <summary>The built-in rule catalog (engine-design.md section 6). JavaScript rules use <c>x/&lt;id&gt;</c> and are not listed here.</summary>
public static class RuleCatalog
{
    private const DiagnosticSeverity E = DiagnosticSeverity.Error;
    private const DiagnosticSeverity W = DiagnosticSeverity.Warning;
    private const DiagnosticSeverity I = DiagnosticSeverity.Info;

    private static readonly ImmutableArray<RuleInfo> Rules =
    [
        new("MQ1001", E, "Invalid JSON."),
        new("MQ1002", E, "Schema violation."),
        new("MQ1003", W, "File is not in canonical form."),
        new("MQ1004", E, "Duplicate id."),
        new("MQ1005", W, "File is in the wrong folder or its name does not match the element."),
        new("MQ1006", E, "Invalid ULID."),
        new("MQ1007", E, "Unsupported model format version."),
        new("MQ1008", W, "The .schema folder is out of date."),
        new("MQ1009", E, "A second tag vocabulary or category tree; the ordinally first file is used."),

        new("MQ2001", E, "Dangling reference."),
        new("MQ2002", E, "Reference to an element of the wrong kind."),
        new("MQ2003", E, "Unknown stereotype."),
        new("MQ2004", E, "Stereotype not applicable to this kind."),
        new("MQ2005", E, "Unknown category."),
        new("MQ2006", I, "Undeclared tag (an error when the tag vocabulary is strict)."),
        new("MQ2007", E, "Attribute validation names an unknown rule."),

        new("MQ3001", E, "Duplicate name in scope."),
        new("MQ3002", E, "Inheritance cycle."),
        new("MQ3003", E, "Package cycle."),
        new("MQ3004", E, "Category cycle."),
        new("MQ3005", E, "Entity without a key."),
        new("MQ3006", E, "Key names a missing attribute."),
        new("MQ3007", E, "Duplicate attribute name, virtual attributes included."),
        new("MQ3008", E, "Relation end count does not match its kind."),
        new("MQ3009", E, "Navigation collides with a member of the entity."),
        new("MQ3010", E, "Invalid cardinality."),
        new("MQ3011", E, "Set-null on a required end."),
        new("MQ3012", E, "Enum member name or code duplicated, or a flags value that is not a power of two."),
        new("MQ3013", E, "Facet invalid for the type."),
        new("MQ3014", E, "Custom scalar base is not a built-in type."),
        new("MQ3015", E, "Value object containment cycle."),
        new("MQ3016", E, "Composition child with two owners."),
        new("MQ3017", W, "Default value looks like a credential."),
        new("MQ3018", E, "Invalid name for the element kind."),
        new("MQ3019", E, "Literal default does not match the attribute's type (use defaultExpression for now, today, new-uuid or new-ulid)."),
        new("MQ3020", E, "A stereotype's key cannot change."),

        new("MQ4001", E, "Identifier longer than the dialect's limit."),
        new("MQ4002", E, "Duplicate table name in a schema."),
        new("MQ4003", E, "Duplicate column name."),
        new("MQ4004", E, "Two mappings for one element in one database."),
        new("MQ4005", E, "Foreign key column type does not match the referenced column."),
        new("MQ4006", W, "Native type unknown to the dialect map."),
        new("MQ4007", E, "Overlay references a missing column key."),
        new("MQ4008", E, "Constraint or index names a missing column."),
        new("MQ4009", E, "Mapping option invalid for the element."),
        new("MQ4010", E, "View body missing for the database's dialect."),
        new("MQ4011", E, "Relation between bound tables names no foreign key or junction end binding."),

        new("MQ5001", E, "Property fails its extension schema."),
        new("MQ5002", E, "Validation rule script error."),
        new("MQ5003", E, "Validation rule exceeded a sandbox limit."),
        new("MQ5004", E, "Invalid extension file."),

        new("MQ6001", E, "Invalid pack.json."),
        new("MQ6002", E, "Engine version range not satisfied."),
        new("MQ6003", E, "Template parse error."),
        new("MQ6004", E, "Output path refused."),
        new("MQ6005", E, "Duplicate or case-colliding output path."),
        new("MQ6006", E, "Render error."),
        new("MQ6007", E, "Sandbox limit exceeded."),
        new("MQ6008", E, "Formatter failed or its version does not match."),
        new("MQ6009", E, "Hand edit."),
        new("MQ6010", E, "Protected region lost."),
        new("MQ6011", W, "Text outside file blocks in a unit without output."),
        new("MQ6012", E, "Non-deterministic builtin used."),
        new("MQ6013", E, "Helper name collides with a builtin."),
        new("MQ6014", W, "Unit names an unknown formatter."),
        new("MQ6015", E, "Regions mode on a built root."),
        new("MQ6016", E, "Script error."),
        new("MQ6017", E, "Selector returned an unknown id."),
        new("MQ6018", E, "Stale schema snapshot."),
    ];

    private static readonly FrozenDictionary<string, RuleInfo> ById = Rules.ToFrozenDictionary(r => r.Id, StringComparer.Ordinal);

    /// <summary>Every built-in rule, ordered by id.</summary>
    public static IReadOnlyList<RuleInfo> All => Rules;

    /// <summary>Looks a rule up by id.</summary>
    /// <param name="id">The rule id.</param>
    /// <param name="rule">The rule when found.</param>
    /// <returns><see langword="true"/> for a built-in rule.</returns>
    public static bool TryGet(string id, [MaybeNullWhen(false)] out RuleInfo rule) => ById.TryGetValue(id, out rule);

    /// <summary>Returns a built-in rule.</summary>
    /// <param name="id">The rule id.</param>
    /// <returns>The rule.</returns>
    /// <exception cref="KeyNotFoundException">The id is not a built-in rule.</exception>
    public static RuleInfo Get(string id) => ById[id];

    /// <summary>Creates a diagnostic with the rule's default severity.</summary>
    /// <param name="ruleId">The built-in rule id.</param>
    /// <param name="message">The message.</param>
    /// <param name="elementId">The element id, when known.</param>
    /// <param name="filePath">The repo-relative file path, when known.</param>
    /// <param name="jsonPointer">The JSON pointer, when known.</param>
    /// <returns>The diagnostic, without line and column.</returns>
    public static Diagnostic Create(string ruleId, string message, string? elementId = null, string? filePath = null, string? jsonPointer = null) =>
        new(ruleId, Get(ruleId).DefaultSeverity, message, elementId, filePath, jsonPointer, null, null);
}
