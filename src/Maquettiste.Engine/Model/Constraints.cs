using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// A named constraint (next-phase-3-design.md section 3.1; <c>model/constraints/</c>): one condition over typed parameters, written
/// once and cited by any site with <c>satisfies</c>, which binds each parameter to an expression. Lowering inlines it.
/// </summary>
public sealed record Constraint : Element, IPackaged
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Constraint;

    /// <summary>The id of the owning package, or <see langword="null"/>.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>The typed parameters the condition reads (index kind <c>parameter</c>).</summary>
    public IReadOnlyList<ConstraintParameter> Parameters { get; init; } = [];

    /// <summary>The condition, a boolean expression whose paths name the constraint's own parameters only.</summary>
    public required Expr Condition { get; init; }

    /// <summary>The text a failure reports, or <see langword="null"/>.</summary>
    public string? Message { get; init; }
}

/// <summary>A parameter of a <see cref="Constraint"/> (index kind <c>parameter</c>): its type is an attribute's type.</summary>
public sealed record ConstraintParameter : ElementBase
{
    /// <summary>The parameter's type.</summary>
    public required TypeRef Type { get; init; }
}
