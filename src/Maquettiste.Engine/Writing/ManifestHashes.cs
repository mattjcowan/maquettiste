using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.Writing;

/// <summary>
/// Manifest hash forms (engine-design.md section 12.2; D14): a plain content hash, <c>r:</c> + the hash of a regions file's skeleton
/// (every region body emptied), <c>o:</c> + the content hash of an owned file (<c>once</c>, companions), and <c>b:</c> or <c>bc:</c> +
/// the hash of a <c>block</c> unit's lines (<see cref="ManagedBlock"/>; <c>bc:</c> when the engine created the file).
/// </summary>
internal static class ManifestHashes
{
    /// <summary>The regions prefix.</summary>
    public const string RegionsPrefix = "r:";

    /// <summary>The owned prefix.</summary>
    public const string OwnedPrefix = "o:";

    private static ReadOnlySpan<byte> KeepMarker => "maquettiste:keep"u8;

    private static ReadOnlySpan<byte> EndMarker => "maquettiste:end-keep"u8;

    /// <summary>Whether a manifest hash marks an owned file.</summary>
    /// <param name="hash">The manifest hash.</param>
    /// <returns><see langword="true"/> for <c>o:</c>.</returns>
    public static bool IsOwned(string hash) => hash.StartsWith(OwnedPrefix, StringComparison.Ordinal);

    /// <summary>Whether a manifest hash marks a regions file.</summary>
    /// <param name="hash">The manifest hash.</param>
    /// <returns><see langword="true"/> for <c>r:</c>.</returns>
    public static bool IsRegions(string hash) => hash.StartsWith(RegionsPrefix, StringComparison.Ordinal);

    /// <summary>
    /// Computes the manifest hash that disk bytes would have in the form of <paramref name="form"/>: the skeleton hash for an
    /// <c>r:</c> form, the hash of the block's lines for a <c>b:</c> or <c>bc:</c> form (the block named by
    /// <paramref name="blockMarker"/>; <c>"absent"</c> when the file has no single such block or no marker is given), the content
    /// hash otherwise (with the same prefix).
    /// </summary>
    /// <param name="form">A manifest hash whose prefix decides the form.</param>
    /// <param name="bytes">The disk bytes.</param>
    /// <param name="contentHash">The content hash of <paramref name="bytes"/>, already computed.</param>
    /// <param name="blockMarker">For a block form, the block's <c>&lt;pack&gt;/&lt;unit&gt;</c> marker (<see cref="ManagedBlock.Marker"/>).</param>
    /// <returns>The comparable hash.</returns>
    public static string Comparable(string form, ReadOnlySpan<byte> bytes, string contentHash, string? blockMarker = null)
    {
        if (IsRegions(form))
            return RegionsPrefix + ContentHash.Of(Skeleton(bytes));
        if (ManagedBlock.IsBlock(form))
            return blockMarker is null ? "absent" : ManagedBlock.Comparable(form, bytes, blockMarker) ?? "absent";
        return IsOwned(form) ? OwnedPrefix + contentHash : contentHash;
    }

    /// <summary>
    /// Returns a regions file with every region body removed: a line holding <c>maquettiste:keep</c> followed by whitespace opens a
    /// region, and the next line holding <c>maquettiste:end-keep</c> closes it; both marker lines stay, the lines between go. An
    /// unclosed region runs to the end of the file. Line terminators are kept as they are.
    /// </summary>
    /// <param name="bytes">The file bytes.</param>
    /// <returns>The skeleton bytes.</returns>
    public static byte[] Skeleton(ReadOnlySpan<byte> bytes)
    {
        var output = new List<byte>(bytes.Length);
        var inRegion = false;
        var rest = bytes;
        while (!rest.IsEmpty)
        {
            var newline = rest.IndexOf((byte)'\n');
            var line = newline < 0 ? rest : rest[..(newline + 1)];
            rest = newline < 0 ? [] : rest[(newline + 1)..];
            if (!inRegion)
            {
                output.AddRange(line);
                inRegion = OpensRegion(line);
            }
            else if (line.IndexOf(EndMarker) >= 0)
            {
                output.AddRange(line);
                inRegion = false;
            }
        }

        return [.. output];
    }

    private static bool OpensRegion(ReadOnlySpan<byte> line)
    {
        var at = line.IndexOf(KeepMarker);
        if (at < 0)
            return false;
        var after = line[(at + KeepMarker.Length)..];
        return !after.IsEmpty && after[0] is (byte)' ' or (byte)'\t';
    }
}
