namespace Maquettiste.Engine;

/// <summary>The engine's contract version.</summary>
public static class EngineVersion
{
    /// <summary>
    /// The semantic version packs' <c>engine</c> ranges are checked against, hashed into every unit's static hash. It changes
    /// whenever the same model and templates could render different bytes; it is independent of the NuGet package version.
    /// </summary>
    public const string Value = "1.0.0";

    /// <summary>The model format version this engine reads and writes (<c>formatVersion</c> in <c>maquettiste.json</c>).</summary>
    public const int FormatVersion = 1;
}
