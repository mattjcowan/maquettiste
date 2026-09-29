using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>Project settings: <c>.maquettiste/maquettiste.json</c> (engine-design.md section 2.4).</summary>
public sealed record ProjectSettings
{
    /// <summary>The <c>$schema</c> value; the canonical writer rewrites it.</summary>
    [JsonPropertyName("$schema")]
    public string? SchemaPath { get; init; }

    /// <summary>The model format version; phase 1 reads format 1.</summary>
    public required int FormatVersion { get; init; }

    /// <summary>The project name.</summary>
    public string? Name { get; init; }

    /// <summary>Output roots (the allowlist) and deny globs.</summary>
    public OutputSettings Outputs { get; init; } = new();

    /// <summary>The default hand-edit policy.</summary>
    public HandEditPolicy HandEdits { get; init; } = HandEditPolicy.Fail;

    /// <summary>Formatters run over rendered files.</summary>
    public IReadOnlyList<FormatterSettings> Formatters { get; init; } = [];

    /// <summary>Project-wide naming and mapping conventions (sparse over the built-in defaults).</summary>
    public Conventions Conventions { get; init; } = new();

    /// <summary>Per-database convention overrides; the key is a database name (D16).</summary>
    public IReadOnlyDictionary<string, Conventions> Databases { get; init; } = ImmutableDictionary<string, Conventions>.Empty;

    /// <summary>Type map overrides: dialect name → built-in keyword → native type pattern.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> TypeMaps { get; init; } =
        ImmutableDictionary<string, IReadOnlyDictionary<string, string>>.Empty;

    /// <summary>Inflector overrides.</summary>
    public InflectionSettings Inflection { get; init; } = new();

    /// <summary>Localization of the standard fields; <see langword="null"/> when the project declares none.</summary>
    public LocalizationSettings? Localization { get; init; }

    /// <summary>The reference data storage strategies and screen grouping.</summary>
    public ReferenceDataSettings ReferenceData { get; init; } = new();

    /// <summary>Per-pack settings; the key is a pack name.</summary>
    public IReadOnlyDictionary<string, PackSettings> Packs { get; init; } = ImmutableDictionary<string, PackSettings>.Empty;

    /// <summary>Rule severity overrides.</summary>
    public ValidationSettings Validation { get; init; } = new();

    /// <summary>Sandbox limits for scripts and templates.</summary>
    public SandboxLimits Limits { get; init; } = new();

    /// <summary>The editor's explorer settings: project-defined folders (explorer-redesign.md section 1.6).</summary>
    public ExplorerSettings Explorer { get; init; } = new();
}

/// <summary>The editor's explorer settings. The engine stores them; they change no generated output.</summary>
public sealed record ExplorerSettings
{
    /// <summary>Project-defined explorer folders, in match order: an element is listed in the first folder it matches.</summary>
    public IReadOnlyList<ExplorerFolder> Folders { get; init; } = [];

    /// <summary>Team scopes: named sets of explorer filter chips shared through the project (explorer-redesign.md section 3.2).</summary>
    public IReadOnlyList<ExplorerScope> Scopes { get; init; } = [];
}

/// <summary>A named set of explorer filter chips. Every member narrows the tree; an empty member does not filter.</summary>
public sealed record ExplorerScope
{
    /// <summary>The scope's name, shown in the scope picker.</summary>
    public required string Name { get; init; }

    /// <summary>Element kinds, any of.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>A domain (package) id: the domain and its sub-domains.</summary>
    public string? Domain { get; init; }

    /// <summary>Tag keys, any of.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    /// <summary>Stereotype keys, any of.</summary>
    public IReadOnlyList<string> Stereotypes { get; init; } = [];

    /// <summary>Category-tree node ids, any of; each includes its descendants.</summary>
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary>Only elements with validation errors.</summary>
    public bool Errors { get; init; }

    /// <summary>A diagram id: only the diagram's members.</summary>
    public string? Diagram { get; init; }
}

/// <summary>A project-defined explorer folder: one element kind and one condition.</summary>
public sealed record ExplorerFolder
{
    /// <summary>The folder's label.</summary>
    public required string Label { get; init; }

    /// <summary>An icon name from the editor's icon set; null for the kind's icon.</summary>
    public string? Icon { get; init; }

    /// <summary>The one element kind the folder holds.</summary>
    public required string Kind { get; init; }

    /// <summary>The condition.</summary>
    public required ExplorerFolderMatch Match { get; init; }
}

/// <summary>A folder condition: exactly one member is set.</summary>
public sealed record ExplorerFolderMatch
{
    /// <summary>A stereotype key.</summary>
    public string? Stereotype { get; init; }

    /// <summary>A tag key.</summary>
    public string? Tag { get; init; }

    /// <summary>A category-tree node id; the node's descendants match too.</summary>
    public string? Category { get; init; }
}

/// <summary>Output roots and deny rules.</summary>
public sealed record OutputSettings
{
    /// <summary>The allowlist (SPEC section 4 <c>allow</c> roots): repo-relative folders generated files may be written under.</summary>
    public IReadOnlyList<OutputRoot> Allow { get; init; } = [];

    /// <summary>Deny globs (<c>*</c>, <c>**</c>, <c>?</c>) over repo-relative paths.</summary>
    public IReadOnlyList<string> Deny { get; init; } = [];
}

/// <summary>An output root.</summary>
public sealed record OutputRoot
{
    /// <summary>The repo-relative folder.</summary>
    public required string Path { get; init; }

    /// <summary>Whether output under the root is committed (manifest under <c>.maquettiste/manifest/</c>) or built (gitignored).</summary>
    public bool Commit { get; init; }
}

/// <summary>What a run does with a generated file whose disk content differs from its manifest hash.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HandEditPolicy>))]
public enum HandEditPolicy
{
    /// <summary>Report a conflict and write nothing for it: <c>fail</c>.</summary>
    [JsonStringEnumMemberName("fail")] Fail,

    /// <summary>Overwrite the edited file: <c>overwrite</c>.</summary>
    [JsonStringEnumMemberName("overwrite")] Overwrite,

    /// <summary>Leave the file and its old manifest entry: <c>skip</c>.</summary>
    [JsonStringEnumMemberName("skip")] Skip,
}

/// <summary>A formatter: a command that reads a file on stdin and writes the formatted file on stdout.</summary>
public sealed record FormatterSettings
{
    /// <summary>The formatter's name, referenced by <see cref="PackUnit.Formatter"/>.</summary>
    public required string Name { get; init; }

    /// <summary>File extensions (with the dot) the formatter applies to when a unit names none.</summary>
    public required IReadOnlyList<string> Extensions { get; init; }

    /// <summary>The executable.</summary>
    public required string Command { get; init; }

    /// <summary>Arguments; <c>{path}</c> is replaced by the repo-relative path.</summary>
    public IReadOnlyList<string> Args { get; init; } = [];

    /// <summary>The pinned version; the version command's output must contain it.</summary>
    public required string Version { get; init; }

    /// <summary>Arguments that make the command print its version.</summary>
    public IReadOnlyList<string> VersionArgs { get; init; } = ["--version"];

    /// <summary>The per-file timeout.</summary>
    public int TimeoutSeconds { get; init; } = 30;
}

/// <summary>Inflector overrides.</summary>
public sealed record InflectionSettings
{
    /// <summary>Singular → plural overrides.</summary>
    public IReadOnlyDictionary<string, string> Plurals { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Words with no plural form.</summary>
    public IReadOnlyList<string> Uncountable { get; init; } = [];
}

/// <summary>Settings for one template pack.</summary>
public sealed record PackSettings
{
    /// <summary>Whether the pack runs.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>A repo-relative folder prefixed to the pack's output paths.</summary>
    public string Output { get; init; } = "";

    /// <summary>Parameter values that override the pack's defaults.</summary>
    public IReadOnlyDictionary<string, JsonElement> Parameters { get; init; } = ImmutableDictionary<string, JsonElement>.Empty;

    /// <summary>A hand-edit policy for this pack, overriding the project's.</summary>
    public HandEditPolicy? HandEdits { get; init; }
}

/// <summary>Validation settings.</summary>
public sealed record ValidationSettings
{
    /// <summary>Rule id → <c>error</c>, <c>warning</c>, <c>info</c> or <c>off</c>. MQ1xxx rules cannot be turned off.</summary>
    public IReadOnlyDictionary<string, string> Rules { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>Limits for the Jint sandbox and the Scriban renderer.</summary>
public sealed record SandboxLimits
{
    /// <summary>The wall-clock limit of one top-level script call, in milliseconds.</summary>
    public int ScriptTimeoutMs { get; init; } = 2000;

    /// <summary>The statement limit of one top-level script call.</summary>
    public long ScriptStatements { get; init; } = 5_000_000;

    /// <summary>The script recursion depth limit.</summary>
    public int ScriptRecursion { get; init; } = 256;

    /// <summary>The script memory limit, in bytes.</summary>
    public long ScriptMemoryBytes { get; init; } = 67_108_864;

    /// <summary>The Scriban loop iteration limit.</summary>
    public int TemplateLoopLimit { get; init; } = 1_000_000;

    /// <summary>The Scriban recursion limit.</summary>
    public int TemplateRecursionLimit { get; init; } = 64;
}

/// <summary>
/// Naming and mapping conventions. Every value is optional: the effective value is the built-in default, overridden by the
/// project's <see cref="ProjectSettings.Conventions"/>, then by the database's entry in <see cref="ProjectSettings.Databases"/>.
/// Built-in defaults are given in each member's summary. Name patterns take the tokens <c>{entity} {attribute} {member} {role}
/// {key} {table} {columns} {name} {relation} {entity1} {entity2}</c>.
/// </summary>
public sealed record Conventions
{
    /// <summary>Table name casing (default <c>snake</c>).</summary>
    public CaseStyle? TableCase { get; init; }

    /// <summary>Whether table names pluralize the last word of <c>{entity}</c> (default <see langword="true"/>).</summary>
    public bool? PluralTables { get; init; }

    /// <summary>Column and constraint name casing (default <c>snake</c>).</summary>
    public CaseStyle? ColumnCase { get; init; }

    /// <summary>Table name pattern (default <c>"{entity}"</c>).</summary>
    public string? TableName { get; init; }

    /// <summary>Key column name pattern (default <c>"{attribute}"</c>).</summary>
    public string? KeyColumn { get; init; }

    /// <summary>Foreign key column name pattern (default <c>"{role}_{key}"</c>).</summary>
    public string? ForeignKeyColumn { get; init; }

    /// <summary>Junction table name pattern (default <c>"{entity1}_{entity2}"</c>).</summary>
    public string? JunctionTable { get; init; }

    /// <summary>Child table name pattern for value object collections (default <c>"{entity}_{attribute}"</c>).</summary>
    public string? ChildTable { get; init; }

    /// <summary>Embedded value object column pattern (default <c>"{attribute}_{member}"</c>).</summary>
    public string? ValueObjectColumn { get; init; }

    /// <summary>The position column of ordered collections (default <c>"position"</c>).</summary>
    public string? OrderColumn { get; init; }

    /// <summary>The discriminator column (default <c>"discriminator"</c>).</summary>
    public string? DiscriminatorColumn { get; init; }

    /// <summary>Primary key name pattern (default <c>"pk_{table}"</c>).</summary>
    public string? PrimaryKeyName { get; init; }

    /// <summary>Foreign key name pattern (default <c>"fk_{table}_{columns}"</c>).</summary>
    public string? ForeignKeyName { get; init; }

    /// <summary>Unique constraint name pattern (default <c>"uq_{table}_{columns}"</c>).</summary>
    public string? UniqueName { get; init; }

    /// <summary>Index name pattern (default <c>"ix_{table}_{columns}"</c>).</summary>
    public string? IndexName { get; init; }

    /// <summary>Check constraint name pattern (default <c>"ck_{table}_{name}"</c>).</summary>
    public string? CheckName { get; init; }

    /// <summary>Name pattern of a key sequence the resolver synthesizes for the <c>sequence</c> identity strategy (default <c>"{table}_seq"</c>).</summary>
    public string? SequenceName { get; init; }

    /// <summary>The default string length (default 255).</summary>
    public int? DefaultStringLength { get; init; }

    /// <summary>The default decimal precision (default 18).</summary>
    public int? DecimalPrecision { get; init; }

    /// <summary>The default decimal scale (default 2).</summary>
    public int? DecimalScale { get; init; }

    /// <summary>The default fractional-second precision (default 6).</summary>
    public int? DatetimePrecision { get; init; }

    /// <summary>Enum storage (default <c>int</c>).</summary>
    public StorageKind? EnumStorage { get; init; }

    /// <summary>Value object storage (default <c>embedded</c>).</summary>
    public StorageKind? ValueObjectStorage { get; init; }

    /// <summary>Value object collection storage (default <c>table</c>).</summary>
    public StorageKind? ValueObjectCollectionStorage { get; init; }

    /// <summary>The shape of relations that carry attributes (default <c>junction</c>, D8).</summary>
    public RelationShape? RelationsWithAttributes { get; init; }

    /// <summary>The inheritance strategy (default <c>tph</c>).</summary>
    public InheritanceStrategy? Inheritance { get; init; }

    /// <summary>The storage choice for reference types (project default here, per-database override under <c>databases</c>).</summary>
    public StorageChoice? ReferenceStorage { get; init; }
}

/// <summary>A casing style for generated names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CaseStyle>))]
public enum CaseStyle
{
    /// <summary><c>snake</c>: invoice_line.</summary>
    [JsonStringEnumMemberName("snake")] Snake,

    /// <summary><c>pascal</c>: InvoiceLine.</summary>
    [JsonStringEnumMemberName("pascal")] Pascal,

    /// <summary><c>camel</c>: invoiceLine.</summary>
    [JsonStringEnumMemberName("camel")] Camel,

    /// <summary><c>kebab</c>: invoice-line.</summary>
    [JsonStringEnumMemberName("kebab")] Kebab,

    /// <summary><c>upper-snake</c>: INVOICE_LINE.</summary>
    [JsonStringEnumMemberName("upper-snake")] UpperSnake,

    /// <summary><c>preserve</c>: the name as written.</summary>
    [JsonStringEnumMemberName("preserve")] Preserve,
}
