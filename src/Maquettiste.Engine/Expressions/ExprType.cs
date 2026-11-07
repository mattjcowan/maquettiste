using System.Globalization;

namespace Maquettiste.Engine.Expressions;

/// <summary>
/// The kinds of site an expression is written at (next-phase-3-design.md section 3.1, decision 5): the site decides how an
/// unknown answer counts, not the expression.
/// </summary>
public enum ExprSiteKind
{
    /// <summary>A table, column or entity check: true or unknown passes, false fails, as a CHECK.</summary>
    Check,

    /// <summary>A derived value: the value, null included.</summary>
    Value,

    /// <summary>A process or operation guard: only true passes.</summary>
    Guard,

    /// <summary>A declared rule's condition, judged as a guard.</summary>
    RuleCondition,

    /// <summary>
    /// A value an action sets on the object (object-effects-design.md decision 3): typed as a value site is, against the attribute's type,
    /// and reads what a guard reads (the context, the object under <c>subject</c>, the payload, <c>now()</c>, <c>current_actor()</c>, <c>in_state</c>).
    /// </summary>
    Set,

    /// <summary>
    /// A right's condition (rights-design.md decision 6): only true holds, as at a guard. It reads the object under <c>subject</c> (to-one
    /// paths too), <c>now()</c>, <c>current_actor()</c> and <c>in_state</c>; never the context or a payload, since a right is also checked
    /// outside a run.
    /// </summary>
    Right,
}

/// <summary>
/// The type of an expression node (next-phase-3-design.md section 3.1, typing): <c>bool</c>, <c>int</c> (with <see cref="Bits"/> 16, 32
/// or 64), <c>decimal</c>, <c>double</c>, <c>string</c>, <c>date</c>, <c>time</c>, <c>datetime</c>, <c>datetimeoffset</c>,
/// <c>uuid</c>, <c>json</c>, <c>enum</c> (<see cref="Of"/> the enum's id) or <c>code</c> (<see cref="Of"/> the reference type's id).
/// The typer also meets <c>null</c>, the type of the null literal, which fits every type, and the kinds the core does not have
/// (<c>float</c>, <c>binary</c>, <c>duration</c>, <c>range</c>, <c>value-object</c>…), which it refuses (MQ3103).
/// </summary>
/// <param name="Kind">The kind.</param>
/// <param name="Of">The enum or reference type id of an <c>enum</c> or <c>code</c> type; else <see langword="null"/>.</param>
/// <param name="Bits">The width of an <c>int</c>: 16, 32 or 64; else 0.</param>
public sealed record ExprType(string Kind, string? Of, int Bits = 0)
{
    /// <summary><c>bool</c>.</summary>
    public static ExprType Bool { get; } = new("bool", null);

    /// <summary>A 16-bit <c>int</c>.</summary>
    public static ExprType Int16 { get; } = new("int", null, 16);

    /// <summary>A 32-bit <c>int</c>.</summary>
    public static ExprType Int32 { get; } = new("int", null, 32);

    /// <summary>A 64-bit <c>int</c>.</summary>
    public static ExprType Int64 { get; } = new("int", null, 64);

    /// <summary><c>decimal</c>, exact in both targets.</summary>
    public static ExprType Decimal { get; } = new("decimal", null);

    /// <summary><c>double</c>, an IEEE double in both targets.</summary>
    public static ExprType Double { get; } = new("double", null);

    /// <summary><c>string</c>.</summary>
    public static ExprType String { get; } = new("string", null);

    /// <summary><c>date</c>.</summary>
    public static ExprType Date { get; } = new("date", null);

    /// <summary><c>time</c>: compared only.</summary>
    public static ExprType Time { get; } = new("time", null);

    /// <summary><c>datetime</c>, without an offset.</summary>
    public static ExprType DateTime { get; } = new("datetime", null);

    /// <summary><c>datetimeoffset</c>; computed in UTC.</summary>
    public static ExprType DateTimeOffset { get; } = new("datetimeoffset", null);

    /// <summary><c>uuid</c>: equality only.</summary>
    public static ExprType Uuid { get; } = new("uuid", null);

    /// <summary><c>json</c>: read through functions only.</summary>
    public static ExprType Json { get; } = new("json", null);

    /// <summary>The type of the null literal, which fits every type.</summary>
    public static ExprType Null { get; } = new("null", null);

    /// <summary>The type of a value typed by an enum.</summary>
    /// <param name="enumId">The enum's id.</param>
    /// <returns>The type.</returns>
    public static ExprType Enum(string enumId) => new("enum", enumId);

    /// <summary>The type of a value typed by a reference type: one of its codes.</summary>
    /// <param name="referenceTypeId">The reference type's id.</param>
    /// <returns>The type.</returns>
    public static ExprType Code(string referenceTypeId) => new("code", referenceTypeId);

    /// <summary>
    /// The type of a built-in scalar keyword: <c>text</c> reads as <c>string</c>, <c>ulid</c> as <c>uuid</c>, each <c>int</c> keeps its
    /// width; a keyword the core does not have (<c>float</c>, <c>binary</c>, <c>duration</c>) keeps its own kind, which the typer refuses.
    /// </summary>
    /// <param name="keyword">A <c>builtinType</c> keyword.</param>
    /// <returns>The type.</returns>
    public static ExprType FromBuiltin(string keyword) => keyword switch
    {
        "string" or "text" => String,
        "bool" => Bool,
        "int16" => Int16,
        "int32" => Int32,
        "int64" => Int64,
        "decimal" => Decimal,
        "double" => Double,
        "date" => Date,
        "time" => Time,
        "datetime" => DateTime,
        "datetimeoffset" => DateTimeOffset,
        "uuid" or "ulid" => Uuid,
        "json" => Json,
        _ => new ExprType(keyword, null),
    };

    /// <summary>Whether the core has the type (the null literal's type included).</summary>
    public bool IsAvailable => Kind is "bool" or "int" or "decimal" or "double" or "string" or "date" or "time" or "datetime"
        or "datetimeoffset" or "uuid" or "json" or "enum" or "code" or "null";

    /// <summary>Whether the type is a number: <c>int</c>, <c>decimal</c> or <c>double</c>.</summary>
    public bool IsNumber => Kind is "int" or "decimal" or "double";

    /// <summary>Whether the type is a date or a timestamp.</summary>
    public bool IsTemporal => Kind is "date" or "datetime" or "datetimeoffset";

    /// <summary>Whether values of the type have an order: numbers, strings, dates, times and timestamps.</summary>
    public bool IsOrdered => IsNumber || Kind is "string" or "date" or "time" or "datetime" or "datetimeoffset";

    /// <summary>Whether values of the type can be compared for equality: every available type but <c>json</c>.</summary>
    public bool IsComparable => IsAvailable && Kind != "json";

    /// <summary>The type as messages write it: <c>int32</c>, <c>decimal</c>, <c>enum 01J…</c>.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => Kind switch
    {
        "int" => "int" + Bits.ToString(CultureInfo.InvariantCulture),
        "enum" or "code" => Kind + " " + Of,
        _ => Kind,
    };
}
