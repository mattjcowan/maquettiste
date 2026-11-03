using System.Text.Json.Serialization;

namespace Maquettiste.Engine.Model;

/// <summary>
/// The status of an element or sub-element (<c>status</c>; next-phase-1-design.md section 4.5): the four values requirements use.
/// It has no default; a reference to a retired element warns (MQ2022).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<ElementStatus>))]
public enum ElementStatus
{
    /// <summary><c>proposed</c>: suggested, not yet agreed.</summary>
    [JsonStringEnumMemberName("proposed")] Proposed,

    /// <summary><c>approved</c>: agreed and in use.</summary>
    [JsonStringEnumMemberName("approved")] Approved,

    /// <summary><c>deprecated</c>: still in use, on its way out.</summary>
    [JsonStringEnumMemberName("deprecated")] Deprecated,

    /// <summary><c>retired</c>: no longer to be used; references to it warn (MQ2022).</summary>
    [JsonStringEnumMemberName("retired")] Retired,
}
