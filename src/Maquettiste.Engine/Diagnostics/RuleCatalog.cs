using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Diagnostics;

/// <summary>A built-in rule: its id, default severity and short description.</summary>
/// <param name="Id">The rule id, for example <c>MQ2001</c>.</param>
/// <param name="DefaultSeverity">The severity unless <c>validation.rules</c> overrides it.</param>
/// <param name="Description">A short description, used for SARIF <c>rules[]</c>.</param>
/// <param name="QuickFix">The batch operation that fixes a finding of the rule, applied to the finding's element (phase-3-design.md
/// section 3, "Quick fixes"), or <see langword="null"/>.</param>
public sealed record RuleInfo(string Id, DiagnosticSeverity DefaultSeverity, string Description, string? QuickFix = null)
{
    /// <summary>Whether <c>validation.rules</c> may turn the rule off (every rule except MQ1xxx).</summary>
    public bool CanBeDisabled => !Id.StartsWith("MQ1", StringComparison.Ordinal);
}

/// <summary>A built-in rule as the editor's Validation settings and the MCP tool list it: <see cref="RuleInfo"/> with its family.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="DefaultSeverity">The severity unless <c>validation.rules</c> overrides it.</param>
/// <param name="Description">The short description.</param>
/// <param name="Family">The rule's hundreds group, for example <c>MQ72xx</c>.</param>
/// <param name="FamilyLabel">What the family's rules cover, for example <c>Localization</c>.</param>
/// <param name="CanBeOff">Whether <c>validation.rules</c> may set the rule to <c>off</c> (false for MQ1xxx).</param>
/// <param name="QuickFix">The batch operation that fixes a finding (for example <c>sync-enum</c>); left out of the JSON when none.</param>
public sealed record RuleCatalogEntry(string Id, DiagnosticSeverity DefaultSeverity, string Description, string Family, string FamilyLabel, bool CanBeOff,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? QuickFix = null);

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
        new("MQ1003", W, "File is not in canonical form; run maquettiste format to rewrite it."),
        new("MQ1004", E, "Duplicate id."),
        new("MQ1005", W, "File is in the wrong folder or its name does not match the element."),
        new("MQ1006", E, "Invalid ULID."),
        new("MQ1007", E, "Unsupported model format version."),
        new("MQ1008", W, "The .schema folder is out of date."),
        new("MQ1009", E, "A second tag vocabulary or category tree in the same scope (global or one domain); the ordinally first file is used."),

        new("MQ2001", E, "Dangling reference."),
        new("MQ2002", E, "Reference to an element of the wrong kind."),
        new("MQ2003", E, "Unknown stereotype."),
        new("MQ2004", E, "Stereotype not applicable to this kind."),
        new("MQ2005", E, "Unknown category."),
        new("MQ2006", I, "Undeclared tag (an error when the tag vocabulary is strict)."),
        new("MQ2007", E, "Attribute validation names an unknown rule."),
        new("MQ2008", E, "A tag or category is declared only in a domain vocabulary outside the element's domain chain."),

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
        new("MQ3021", E, "A domain vocabulary redeclares a tag key or category name of the global vocabulary or an enclosing domain's."),

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
        new("MQ4012", I, "An entity lands in no database: no database takes its domain by convention and no mapping names it."),
        new("MQ4013", W, "A database lists convention packages that its byConvention setting does not use."),
        new("MQ4014", E, "A convention package entry or an entity mapping names a schema that its database does not declare."),
        new("MQ4015", E, "A schema operation was refused: the schema is unknown or its name is taken, it still holds tables, views, sequences, convention entries or mappings and no target was given, or it is the default and no other schema becomes the default."),

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
        new("MQ6019", E, "A unit's output path cannot stay under an allowed output root."),
        new("MQ6020", E, "Two elements of one unit render the same output path."),
        new("MQ6021", E, "A unit's scope (for) is not a known scope."),
        new("MQ6022", E, "A unit names a template or companion template that is not in the pack."),
        new("MQ6023", E, "A project parameter value fails the pack's parameter schema."),
        new("MQ6024", W, "A project parameter value names a parameter the pack does not declare."),
        new("MQ6025", W, "A pack file that no unit reaches does not parse."),
        new("MQ6026", E, "A preview names an element outside its unit's scope."),

        new("MQ7001", E, "Duplicate code in a reference type, across all its seeds."),
        new("MQ7002", W, "Two codes of one reference type differ only by case."),
        new("MQ7003", E, "A row misses a required field."),
        new("MQ7004", E, "A seed cell does not match its column's type or facets."),
        new("MQ7005", E, "A seed column names neither a field nor an end of the target, names one twice, or a row has more values than columns."),
        new("MQ7006", E, "A collection attribute whose effective reference storage strategy in a database does not support collections."),
        new("MQ7007", E, "A storage choice names a strategy the project has not declared."),
        new("MQ7008", E, "Storage options fail the strategy's option schema."),
        new("MQ7009", E, "A seed cell names a row that does not exist."),
        new("MQ7010", E, "Invalid reference type field: code type, field type or reserved field name."),
        new("MQ7011", E, "An allowedValues entry on a reference-typed attribute names a code that is not in the type's rows."),
        new("MQ7012", E, "The enum lookup-table storage option ('lookup') is retired: convert the enum to a reference type, whose storage strategy then decides the lookup table."),
        new("MQ7013", E, "A uuid code is not a UUID in canonical form: 8-4-4-4-12 lowercase hexadecimal digits with hyphens."),

        new("MQ7101", E, "A seed targets an abstract entity."),
        new("MQ7102", E, "A seed key cell is missing, or two rows of one target share a key value."),
        new("MQ7103", W, "Required row references form a cycle, so no insert order exists."),
        new("MQ7104", W, "A seed has more than 10,000 rows or 5 MB."),
        new("MQ7105", E, "A column of an entity seed names a to-many end."),
        new("MQ7106", E, "A relation link stated twice: an entity seed has an end column of a relation that also has seeds."),

        new("MQ7201", E, "Invalid localization settings: a locale that is not BCP 47, a default not in the locales, a fallback naming an undeclared locale, a fallback cycle, or a required node kind that does not exist."),
        new("MQ7202", W, "A locale folder for an undeclared locale or for the default locale; its files are not loaded."),
        new("MQ7203", W, "An orphan translation: its id is not a localizable node of the model, or it has a field the node does not have."),
        new("MQ7204", I, "A locale is incomplete in a shard: the counts of missing and stale texts, for the required kinds."),
        new("MQ7205", I, "A missing translation, one per node and field (off unless validation.rules gives it a severity)."),
        new("MQ7206", I, "Stale translations in a shard: made from a default text that has changed since."),
        new("MQ7207", W, "A translation entry in the wrong shard (its node moved); it still applies."),
        new("MQ7208", E, "A translated description's Markdown sidecar does not exist."),
        new("MQ7209", E, "One id in two shards of one locale; the ordinally first path applies."),
        new("MQ7210", W, "A shard whose locale differs from its folder, or whose scope differs from its path; it applies to its declared locale and scope."),
        new("MQ7211", W, "A plural name on a to-one relation end, or its translation; it is never read."),

        new("MQ8001", E, "A branding color that is not a hex color (#rrggbb or #rgb)."),
        new("MQ8002", E, "The branding icon is not an .svg or .png file under branding/ in the model folder, or the file does not exist."),
        new("MQ8003", E, "The branding icon is not a safe SVG (scripts, event handlers, external references) or a PNG of at most 512 KB."),

        new("MQ9001", E, "A compound state (or the process) whose initial is not one of its direct children, or an initial on a state that is not compound; set initial to a direct child, or remove it to use the first child.", "set-initial"),
        new("MQ9002", E, "A state's type contradicts its children: an atomic, final, history or choice state with children, or a compound or parallel state without; change the type or move the children."),
        new("MQ9003", W, "An unreachable state: no path from the initial state through transitions, history defaults or invoke completions enters it (guards are ignored); add a transition to it or remove it."),
        new("MQ9004", W, "A dead end: a reachable atomic state with no transition on itself or an ancestor and no invoke; add a transition or make it final."),
        new("MQ9005", I, "No final state is reachable, so the process never completes (normal for some lifecycles); add a final state and a transition to it if instances should end."),
        new("MQ9006", W, "Overlapping transitions for one source and trigger: a transition after an unguarded one, or a guard tested twice, never fires; guard the earlier transition or remove the later one."),
        new("MQ9007", E, "Invalid targets: a state of another process, two targets in one region, or several targets not in orthogonal regions of one parallel ancestor; keep one target per region of this process."),
        new("MQ9008", E, "Trigger fields inconsistent: an event trigger without an event, an after trigger without a positive ISO 8601 duration, done on a state that cannot complete, an invoke trigger naming no invoke of the source, or a field of another trigger; fix the trigger or its field."),
        new("MQ9009", E, "A cycle of unguarded eventless transitions: the macrostep would never end; guard one of the transitions or give it an event."),
        new("MQ9010", E, "A choice state whose outgoing transitions are not all always, whose last transition is guarded (no default), or that has none; make them always transitions ending with an unguarded one."),
        new("MQ9011", E, "A history state outside a compound parent, or a default target that is not a descendant of its parent; move the history state into a compound state or pick a default inside it."),
        new("MQ9012", E, "A final state that is the source of transitions or invokes work; remove them, or make the state atomic."),
        new("MQ9013", W, "A declared event, guard, action or invoke that nothing uses (an invoke is used by an invoke-done or invoke-error transition); use it or remove it."),
        new("MQ9014", E, "A reference to a state, event, guard, action or invoke of another process; declare it in this process instead."),
        new("MQ9015", E, "Invokes: a sub-process invoke cycle, a process invoke without a process, or a human task without actors; break the cycle, set the process, or list the actors."),
        new("MQ9016", W, "A process diagram member that is not a state of the diagram's process; remove the member."),
        new("MQ9017", W, "A parallel state with one region; add a region or make the state compound."),
        new("MQ9018", W, "A done transition whose source has no reachable final state (in every region, for a parallel source), so it never fires; add a reachable final child or change the trigger."),
        new("MQ9019", E, "A process operation (sync-enum, set-lifecycle, set-initial) was refused: its element is not what the operation needs, the target is not a direct child or a process, the enum sync would remove members still in use, or another operation of the batch writes the process or enum it syncs; fix the operation, change the uses to a remaining member, or sync in a batch of its own."),

        new("MQ9101", E, "A gate needs more signatures than its signers can give (only person actors, repeat signing off); lower required, add signers, or allow repeat signers."),
        new("MQ9102", E, "A gate's required actors are not all among its signers; add them to signers."),
        new("MQ9103", E, "A gate on a transition whose trigger is not event, or two gates on one source and event; move the gate to an event transition or keep one gate."),
        new("MQ9104", E, "A gate without meanings; add at least one meaning that says what a signature means."),
        new("MQ9105", W, "A gate signer that may not raise the gate's event (the event lists its actors and leaves the signer out); add the signer to the event's actors."),
        new("MQ9106", I, "An actor no process references (event actors, gate signers, human tasks); use it in a process or remove it."),

        new("MQ9201", E, "Lifecycle inconsistent: a lifecycle without a subject, a subject whose lifecycle does not name the process, or an entity whose lifecycle names an orchestration or another entity's lifecycle; set lifecycle on the subject to the process."),
        new("MQ9202", E, "A bound attribute that is not a single-valued enum attribute of the subject (own, inherited or from a stereotype), or one set on an orchestration; bind a single-valued enum attribute of the subject."),
        new("MQ9203", E, "Enum drift: the bound enum's members differ from the lifecycle's root-level states (missing, extra or out of order); sync the enum from the process.", "sync-enum"),
        new("MQ9204", W, "The bound enum is used by other attributes or processes, so syncing it from the process changes them too; give the lifecycle its own enum if they should not follow."),
        new("MQ9205", W, "The bound attribute's default is not the lifecycle's initial root-level state; set the default to that state's name."),
    ];

    private static readonly FrozenDictionary<string, RuleInfo> ById = Rules.ToFrozenDictionary(r => r.Id, StringComparer.Ordinal);

    private static readonly FrozenDictionary<string, string> FamilyLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["MQ10xx"] = "Model files",
        ["MQ20xx"] = "References and vocabularies",
        ["MQ30xx"] = "Model structure",
        ["MQ40xx"] = "Databases and mappings",
        ["MQ50xx"] = "Extensions and script rules",
        ["MQ60xx"] = "Packs and generation",
        ["MQ70xx"] = "Reference types",
        ["MQ71xx"] = "Seeds",
        ["MQ72xx"] = "Localization",
        ["MQ80xx"] = "Branding",
        ["MQ90xx"] = "Processes",
        ["MQ91xx"] = "Actors and gates",
        ["MQ92xx"] = "Lifecycles",
        ["MQ93xx"] = "Scenarios",
        ["MQ94xx"] = "Process import and export",
        ["MQ95xx"] = "Process expressions and simulation",
    }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The hundreds group of a rule id: <c>MQ7204</c> is in <c>MQ72xx</c>.</summary>
    /// <param name="id">A built-in rule id.</param>
    /// <returns>The family key.</returns>
    public static string FamilyOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length >= 4 ? string.Concat(id.AsSpan(0, 4), "xx") : id;
    }

    /// <summary>What a family's rules cover.</summary>
    /// <param name="family">A family key from <see cref="FamilyOf"/>.</param>
    /// <returns>The label, or the key itself for an unknown family.</returns>
    public static string FamilyLabel(string family) => FamilyLabels.TryGetValue(family, out var label) ? label : family;

    /// <summary>Every built-in rule with its family and whether it can be turned off, ordered by id.</summary>
    /// <returns>The catalog entries.</returns>
    public static IReadOnlyList<RuleCatalogEntry> Describe() =>
        [.. Rules.Select(r => new RuleCatalogEntry(r.Id, r.DefaultSeverity, r.Description, FamilyOf(r.Id), FamilyLabel(FamilyOf(r.Id)), r.CanBeDisabled, r.QuickFix))];

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
