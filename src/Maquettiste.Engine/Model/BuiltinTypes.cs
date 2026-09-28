using System.Collections.Frozen;
using System.Collections.Immutable;

namespace Maquettiste.Engine.Model;

/// <summary>The built-in scalar type keywords (SPEC section 6). <c>decimal(p,s)</c> is written as <c>decimal</c> with facets (D3).</summary>
public static class BuiltinTypes
{
    private static readonly ImmutableArray<string> Keywords =
    [
        "string", "text", "bool", "int16", "int32", "int64", "decimal", "float", "double",
        "date", "time", "datetime", "datetimeoffset", "duration", "uuid", "ulid", "binary", "json",
    ];

    private static readonly FrozenSet<string> KeywordSet = Keywords.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Every keyword, in SPEC order.</summary>
    public static IReadOnlyList<string> All => Keywords;

    /// <summary>Whether a string is a built-in keyword (case-sensitive).</summary>
    /// <param name="keyword">The candidate keyword.</param>
    /// <returns><see langword="true"/> for a built-in keyword.</returns>
    public static bool IsBuiltin(string? keyword) => keyword is not null && KeywordSet.Contains(keyword);
}
