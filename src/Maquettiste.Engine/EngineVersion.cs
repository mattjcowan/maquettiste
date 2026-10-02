using System.Reflection;

namespace Maquettiste.Engine;

/// <summary>The engine's contract version, and the release it was built as.</summary>
public static class EngineVersion
{
    /// <summary>
    /// The semantic version packs' <c>engine</c> ranges are checked against, hashed into every unit's static hash. It changes
    /// whenever the same model and templates could render different bytes; it is independent of the NuGet package version.
    /// </summary>
    public const string Value = "1.0.0";

    /// <summary>The model format version this engine reads and writes (<c>formatVersion</c> in <c>maquettiste.json</c>).</summary>
    public const int FormatVersion = 1;

    /// <summary>
    /// The release this engine was built as, such as <c>0.5.3</c>: <see cref="Build"/> without the image's per-build marker (see
    /// <see cref="ProductOf"/>). This is the version people and tools compare; <see cref="Value"/> is the engine contract.
    /// </summary>
    public static string Product { get; } = ProductOf(Informational());

    /// <summary>
    /// The engine assembly's informational version without its <c>+</c> metadata, such as <c>0.5.3</c> from a local build or
    /// <c>0.5.3-b14a8131cfe39</c> in the image, whose package version carries a marker of the engine's inputs.
    /// </summary>
    public static string Build { get; } = BuildOf(Informational());

    /// <summary>An informational version without its <c>+</c> metadata: <c>0.5.3+abc</c> gives <c>0.5.3</c>.</summary>
    /// <param name="informational">The informational version.</param>
    /// <returns>The build version.</returns>
    public static string BuildOf(string informational)
    {
        ArgumentNullException.ThrowIfNull(informational);
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return (plus < 0 ? informational : informational[..plus]).Trim();
    }

    /// <summary>
    /// The release of an informational version: <see cref="BuildOf"/>, then without a final build marker, <c>b</c> and at least seven
    /// hex digits after a <c>-</c> or a <c>.</c>: <c>0.5.3-b14a8131cfe39</c> gives <c>0.5.3</c> and <c>0.6.0-rc.1.b3f9c2a1</c> gives
    /// <c>0.6.0-rc.1</c>.
    /// </summary>
    /// <param name="informational">The informational version.</param>
    /// <returns>The release.</returns>
    public static string ProductOf(string informational)
    {
        var build = BuildOf(informational);
        var marker = Math.Max(build.LastIndexOf("-b", StringComparison.Ordinal), build.LastIndexOf(".b", StringComparison.Ordinal));
        if (marker <= 0)
            return build;
        var hex = build.AsSpan(marker + 2);
        if (hex.Length < 7)
            return build;
        foreach (var c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
                return build;
        }

        return build[..marker];
    }

    private static string Informational()
    {
        var assembly = typeof(EngineVersion).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
