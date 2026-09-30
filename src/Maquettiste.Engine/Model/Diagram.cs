using System.Text.Json.Serialization;
namespace Maquettiste.Engine.Model;

/// <summary>A saved canvas: membership, positions and view state only (<c>model/diagrams/</c>). Never visible to templates.</summary>
public sealed record Diagram : Element
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override ElementKind Kind => ElementKind.Diagram;

    /// <summary>The id of the package the diagram belongs to, or <see langword="null"/>.</summary>
    [ElementRef(ElementKind.Package)]
    public string? Package { get; init; }

    /// <summary>Set when the diagram is that process's statechart: its members are then states of the process (MQ9016).</summary>
    [ElementRef(ElementKind.Process)]
    public string? Process { get; init; }

    /// <summary>The elements shown, with positions.</summary>
    public IReadOnlyList<DiagramMember> Members { get; init; } = [];

    /// <summary>The saved viewport.</summary>
    public Viewport? Viewport { get; init; }
}

/// <summary>One element on a diagram.</summary>
public sealed record DiagramMember
{
    /// <summary>The id of the element shown.</summary>
    [ElementRef]
    public required string Element { get; init; }

    /// <summary>The x position.</summary>
    public double X { get; init; }

    /// <summary>The y position.</summary>
    public double Y { get; init; }

    /// <summary>The width, when resized.</summary>
    public double? Width { get; init; }

    /// <summary>The height, when resized.</summary>
    public double? Height { get; init; }

    /// <summary>Whether the node is collapsed.</summary>
    public bool Collapsed { get; init; }
}

/// <summary>A diagram viewport.</summary>
public sealed record Viewport
{
    /// <summary>The x offset.</summary>
    public double X { get; init; }

    /// <summary>The y offset.</summary>
    public double Y { get; init; }

    /// <summary>The zoom factor.</summary>
    public double Zoom { get; init; } = 1;
}
