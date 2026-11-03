using System.Text.Json;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A node of an expression of the one expression language (next-phase-3-design.md section 3.1, <c>common.json#/$defs/expr</c>):
/// exactly one of <see cref="Value"/> (with an optional <see cref="Type"/>), <see cref="Null"/>, <see cref="Path"/>, <see cref="Ref"/>,
/// <see cref="Member"/>, <see cref="Now"/>, <see cref="Op"/> (with <see cref="Args"/>), <see cref="Call"/> (with <see cref="Args"/>),
/// <see cref="Case"/> (with an optional <see cref="Else"/>), <see cref="Satisfies"/> (with <see cref="With"/>) or <see cref="Text"/>.
/// Paths, refs and constraints are ids, so a rename never rewrites the node; the text form is printed from current names.
/// </summary>
public sealed record Expr
{
    /// <summary>A literal: a string, an integer or a boolean, or the text of a value of <see cref="Type"/>.</summary>
    public JsonElement? Value { get; init; }

    /// <summary>The type of a literal written as text: <c>decimal</c>, <c>double</c>, <c>date</c>, <c>time</c>, <c>datetime</c>,
    /// <c>datetimeoffset</c> or <c>uuid</c>.</summary>
    public string? Type { get; init; }

    /// <summary>The null literal, when <see langword="true"/>.</summary>
    public bool? Null { get; init; }

    /// <summary>
    /// A path of ids: to-one navigation ids (relation end ids), then one attribute, column, input member, context or payload
    /// attribute, or constraint parameter id. A value object member is <c>attributeId.memberId</c>; inherited and contributed
    /// members take their keyed ids.
    /// </summary>
    [ElementRef(Keyed = true)]
    public IReadOnlyList<string>? Path { get; init; }

    /// <summary>An enum member id or a reference-type code id.</summary>
    [ElementRef(Keyed = true)]
    public string? Ref { get; init; }

    /// <summary>A member of the selected object, in phase 2's path syntax (<c>properties.retentionDays</c>); rule conditions only.</summary>
    public string? Member { get; init; }

    /// <summary>The clock, a <c>datetimeoffset</c>, when <see langword="true"/>; guard sites only.</summary>
    public bool? Now { get; init; }

    /// <summary>An operator over <see cref="Args"/> (<c>eq</c>, <c>and</c>, <c>add</c>, <c>coalesce</c>…).</summary>
    public string? Op { get; init; }

    /// <summary>A function of the closed function table, over <see cref="Args"/>.</summary>
    public string? Call { get; init; }

    /// <summary>The operands of <see cref="Op"/> or the arguments of <see cref="Call"/>.</summary>
    public IReadOnlyList<Expr>? Args { get; init; }

    /// <summary>The branches of a case expression, at least one.</summary>
    public IReadOnlyList<ExprWhen>? Case { get; init; }

    /// <summary>The value of a case expression when no branch is taken; absent, null.</summary>
    public Expr? Else { get; init; }

    /// <summary>The id of the constraint a <c>satisfies</c> node cites.</summary>
    [ElementRef(Keyed = true)]
    public string? Satisfies { get; init; }

    /// <summary>The arguments of a <c>satisfies</c> node, one per parameter of the constraint.</summary>
    public IReadOnlyList<ExprArgument>? With { get; init; }

    /// <summary>The expression as text: accepted on writes and stored as a tree; in a file it is read, typed and warned (MQ3108).</summary>
    public string? Text { get; init; }
}

/// <summary>A branch of a case expression: taken only when <see cref="When"/> is true.</summary>
public sealed record ExprWhen
{
    /// <summary>The condition, a boolean expression.</summary>
    public required Expr When { get; init; }

    /// <summary>The value when the condition is true.</summary>
    public required Expr Then { get; init; }
}

/// <summary>One argument of a <c>satisfies</c> node: the value bound to one parameter of the constraint.</summary>
public sealed record ExprArgument
{
    /// <summary>The id of the constraint's parameter.</summary>
    [ElementRef(Keyed = true)]
    public required string Parameter { get; init; }

    /// <summary>The value bound to it.</summary>
    public required Expr Value { get; init; }
}
