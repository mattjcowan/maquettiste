using System.Collections.Immutable;
using System.Text.Json.Serialization;
using Maquettiste.Engine.Ledger;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One migration fact (next-phase-1-design.md section 3.2; <c>model/databases/&lt;db&gt;/migrations/open/</c>): a change of a
/// database's physical shape that its data needs to follow, with the intent a migration needs to apply it. A recorded fact has no
/// <c>name</c>, so its file is <c>&lt;lowercase id&gt;.json</c> and a listing reads in recording order. Object keys are historical:
/// plain strings that no reference walker sees; a later <c>rekey</c> fact moves them forward. A fact belongs to its database and
/// is deleted with it.
/// </summary>
public sealed record Migration : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Migration;

    /// <summary>The database whose shape the fact changes; the fact belongs to it.</summary>
    [ElementRef(ElementKind.Database, Owning = true)]
    public required string Database { get; init; }

    /// <summary>What the fact records.</summary>
    public required MigrationFact Fact { get; init; }

    /// <summary>
    /// Ids of the facts this one follows: an ordering hint set by the write path. Not a reference: a fact that another copy folded
    /// away may leave a link dangling, which is a warning (MQ4111).
    /// </summary>
    public IReadOnlyList<string> After { get; init; } = [];

    /// <summary>The object of a <c>rename</c>, <c>change-type</c>, <c>drop</c> or <c>backfill</c>.</summary>
    public MigrationObject? Object { get; init; }

    /// <summary>The objects of an <c>adopt</c>, <c>baseline</c>, <c>value-map</c> or <c>state-map</c>; context for <c>raw</c>.</summary>
    public IReadOnlyList<MigrationObject> Objects { get; init; } = [];

    /// <summary>The state before: the name and schema of a <c>rename</c>, the type facets of a <c>change-type</c>.</summary>
    public MigrationState? From { get; init; }

    /// <summary>The state after.</summary>
    public MigrationState? To { get; init; }

    /// <summary>The intermediate names of a folded rename, oldest first.</summary>
    public IReadOnlyList<string> Via { get; init; } = [];

    /// <summary>The keys a <c>rekey</c> moved.</summary>
    public IReadOnlyList<MigrationRekey> Keys { get; init; } = [];

    /// <summary>How a <c>change-type</c> converts stored values.</summary>
    public MigrationConversion? Conversion { get; init; }

    /// <summary>The rows a step must not find, or why no check is needed.</summary>
    public MigrationDataCheck? DataCheck { get; init; }

    /// <summary>Why a <c>drop</c>'s data may go.</summary>
    public string? Acknowledge { get; init; }

    /// <summary>A <c>backfill</c>'s value per dialect name, or <c>"*"</c> for every dialect.</summary>
    public IReadOnlyDictionary<string, string> Value { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>A <c>backfill</c>'s rows per dialect name (default <c>{column} IS NULL</c>).</summary>
    public IReadOnlyDictionary<string, string> Where { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>A <c>raw</c> fact's statements per dialect name.</summary>
    public IReadOnlyDictionary<string, string> Sql { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>A <c>raw</c> fact's guard per dialect name: a query whose row means the end state holds.</summary>
    public IReadOnlyDictionary<string, string> SkipWhen { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Whether a <c>raw</c> fact runs before or after the schema statements.</summary>
    public MigrationPhase Phase { get; init; } = MigrationPhase.After;

    /// <summary>The environment of an <c>adopt</c>: a free name.</summary>
    public string? Environment { get; init; }

    /// <summary>The historical id of a <c>value-map</c>'s reference type; not a reference.</summary>
    public string? ReferenceType { get; init; }

    /// <summary>The historical id of a <c>state-map</c>'s process; not a reference.</summary>
    public string? Process { get; init; }

    /// <summary>Stored codes (or state codes) and their replacements.</summary>
    public IReadOnlyList<MigrationValue> Values { get; init; } = [];

    /// <summary>Whether the fact waits: migrators skip it until it is <see cref="Released"/>.</summary>
    public bool Held { get; init; }

    /// <summary>Whether a held fact may now run.</summary>
    public bool Released { get; init; }
}

/// <summary>What a migration fact records (<c>fact</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationFact>))]
public enum MigrationFact
{
    /// <summary>A table, view, sequence, column or database schema took a new name (<c>rename</c>).</summary>
    [JsonStringEnumMemberName("rename")] Rename,

    /// <summary>Physical keys moved, as materialize moves them (<c>rekey</c>); no DDL.</summary>
    [JsonStringEnumMemberName("rekey")] Rekey,

    /// <summary>A column's type changed (<c>change-type</c>).</summary>
    [JsonStringEnumMemberName("change-type")] ChangeType,

    /// <summary>A table, column or sequence was dropped (<c>drop</c>).</summary>
    [JsonStringEnumMemberName("drop")] Drop,

    /// <summary>Existing objects of an environment join the ledger (<c>adopt</c>).</summary>
    [JsonStringEnumMemberName("adopt")] Adopt,

    /// <summary>The database stands at the model's shape as of this fact (<c>baseline</c>).</summary>
    [JsonStringEnumMemberName("baseline")] Baseline,

    /// <summary>A column made not null gets values for its empty rows (<c>backfill</c>).</summary>
    [JsonStringEnumMemberName("backfill")] Backfill,

    /// <summary>Statements written by hand, behind a guard (<c>raw</c>).</summary>
    [JsonStringEnumMemberName("raw")] Raw,

    /// <summary>Stored codes of a reference type replaced (<c>value-map</c>).</summary>
    [JsonStringEnumMemberName("value-map")] ValueMap,

    /// <summary>Stored state codes of a process replaced (<c>state-map</c>).</summary>
    [JsonStringEnumMemberName("state-map")] StateMap,
}

/// <summary>When a <c>raw</c> fact runs, relative to the schema statements.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationPhase>))]
public enum MigrationPhase
{
    /// <summary>Before the schema statements: <c>before</c>.</summary>
    [JsonStringEnumMemberName("before")] Before,

    /// <summary>After the schema statements: <c>after</c>.</summary>
    [JsonStringEnumMemberName("after")] After,
}

/// <summary>Whether writes record migration facts (<c>migrations.record</c>).</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MigrationRecording>))]
public enum MigrationRecording
{
    /// <summary>Every write that reshapes a database records its facts: <c>automatic</c>.</summary>
    [JsonStringEnumMemberName("automatic")] Automatic,

    /// <summary>No write records facts; <c>validate --against</c> still checks: <c>off</c>.</summary>
    [JsonStringEnumMemberName("off")] Off,
}

/// <summary>
/// An object a migration fact names, by physical key and by its physical names at the time. It has no id, so the indexer registers
/// no sub-element, and its keys are plain strings that no reference walker sees.
/// </summary>
public sealed record MigrationObject
{
    /// <summary>
    /// What the object is: <c>table</c>, <c>column</c>, <c>view</c>, <c>sequence</c>, <c>index</c>, <c>unique</c>,
    /// <c>foreign-key</c>, <c>check</c>, <c>routine</c>, <c>database-type</c>, <c>sql-object</c> or <c>schema</c>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>The key of the table a member (a column, index or constraint) belongs to.</summary>
    public string? Table { get; init; }

    /// <summary>The object's physical key, as the schema diff matches it.</summary>
    public required string Key { get; init; }

    /// <summary>The object's database schema name at the time.</summary>
    public string? Schema { get; init; }

    /// <summary>A member's table name at the time.</summary>
    public string? TableName { get; init; }

    /// <summary>The object's name at the time.</summary>
    public string? Name { get; init; }

    /// <summary>The names the object had before (<c>baseline</c> only).</summary>
    public IReadOnlyList<string> FormerNames { get; init; } = [];
}

/// <summary>
/// A side of a migration fact: names for a <c>rename</c>; type facets for a <c>change-type</c>, with a reference column's strategy and
/// bound table.
/// </summary>
public sealed record MigrationState
{
    /// <summary>The object's name.</summary>
    public string? Name { get; init; }

    /// <summary>The object's database schema name.</summary>
    public string? Schema { get; init; }

    /// <summary>The column's built-in type keyword.</summary>
    public string? Type { get; init; }

    /// <summary>The column's length.</summary>
    public int? Length { get; init; }

    /// <summary>The column's precision.</summary>
    public int? Precision { get; init; }

    /// <summary>The column's scale.</summary>
    public int? Scale { get; init; }

    /// <summary>The column's effective native type.</summary>
    public string? NativeType { get; init; }

    /// <summary>A reference column's effective storage strategy.</summary>
    public string? Strategy { get; init; }

    /// <summary>
    /// The key of the table a reference column's reference type is bound to, at the time: a historical key like the object's, which
    /// later <c>rekey</c> facts move forward.
    /// </summary>
    public string? ReferenceTable { get; init; }

    /// <summary>Whether the column takes NULL.</summary>
    public bool? Nullable { get; init; }
}

/// <summary>One key a <c>rekey</c> fact moved.</summary>
public sealed record MigrationRekey
{
    /// <summary>What the key names: <c>table</c>, <c>column</c>, <c>sequence</c> or <c>sql-object</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>A column entry's table key before the fact.</summary>
    public string? Table { get; init; }

    /// <summary>The key before.</summary>
    public required string From { get; init; }

    /// <summary>The key after.</summary>
    public required string To { get; init; }

    /// <summary>The object's name at the time.</summary>
    public string? Name { get; init; }
}

/// <summary>How a <c>change-type</c> converts stored values: a cast, or an expression per dialect.</summary>
public sealed record MigrationConversion
{
    /// <summary>Whether a plain cast converts the values.</summary>
    public bool Cast { get; init; }

    /// <summary>The conversion per dialect name; <c>{table}</c> and <c>{column}</c> expand to quoted names.</summary>
    public IReadOnlyDictionary<string, string> Expression { get; init; } = ImmutableDictionary<string, string>.Empty;
}

/// <summary>A step's data check: a query per dialect whose rows the step must not find, or why no check is needed.</summary>
public sealed record MigrationDataCheck
{
    /// <summary>The query per dialect name; a row it returns stops the step.</summary>
    public IReadOnlyDictionary<string, string> Query { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>Why no check is needed.</summary>
    public string? Accept { get; init; }
}

/// <summary>A stored code (or state code) and its replacement.</summary>
public sealed record MigrationValue
{
    /// <summary>The stored value.</summary>
    public required string From { get; init; }

    /// <summary>Its replacement.</summary>
    public required string To { get; init; }
}

/// <summary>Migration facts (<c>migrations</c> in <c>maquettiste.json</c>, next-phase-1-design.md section 3.2).</summary>
public sealed record MigrationSettings
{
    /// <summary>Whether writes record facts; <c>off</c> records none, and <c>validate --against</c> still checks.</summary>
    public MigrationRecording Record { get; init; } = MigrationRecording.Automatic;

    /// <summary>
    /// Schema of the migration ledger tables in each PostgreSQL database (next-phase-1-design.md section 3.3): a lower-case
    /// identifier, quoted where it is a reserved word. MQ4067 when a database's modeled objects use it too.
    /// </summary>
    public string LedgerSchema { get; init; } = LedgerContract.DefaultSchema;
}
