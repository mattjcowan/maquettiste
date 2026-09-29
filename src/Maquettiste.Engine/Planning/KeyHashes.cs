using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// The per-key hashes unit state format 3 records for explanation (generation-ui.md section 4.2): each read key's hash, truncated to
/// <see cref="Size"/> bytes. A hexadecimal hash keeps its first 16 bytes; any other value (<see cref="DependencyHasher.Absent"/>) is
/// hashed first, so every value maps to 16 bytes deterministically.
/// </summary>
internal static class KeyHashes
{
    /// <summary>The bytes per key.</summary>
    public const int Size = 16;

    /// <summary>The truncated hashes of keys, in order.</summary>
    /// <param name="keys">The keys.</param>
    /// <param name="currentHash">The current hash of a key.</param>
    /// <returns><see cref="Size"/> bytes per key.</returns>
    public static byte[] Of(IReadOnlyList<string> keys, Func<string, string> currentHash)
    {
        var bytes = new byte[keys.Count * Size];
        for (var i = 0; i < keys.Count; i++)
            Truncate(currentHash(keys[i]), bytes.AsSpan(i * Size, Size));
        return bytes;
    }

    /// <summary>Writes a hash's truncated form.</summary>
    /// <param name="hash">The hash text.</param>
    /// <param name="destination"><see cref="Size"/> bytes.</param>
    public static void Truncate(string hash, Span<byte> destination)
    {
        if (hash.Length >= Size * 2 && Convert.FromHexString(hash.AsSpan(0, Size * 2), destination, out _, out var written) == System.Buffers.OperationStatus.Done
            && written == Size)
            return;
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(hash), digest);
        digest[..Size].CopyTo(destination);
    }

    /// <summary>Whether the recorded hash of key <paramref name="index"/> differs from <paramref name="current"/>.</summary>
    /// <param name="recorded">The recorded hashes.</param>
    /// <param name="index">The key's index.</param>
    /// <param name="current">The key's current hash.</param>
    /// <returns><see langword="true"/> when they differ.</returns>
    public static bool Differs(ReadOnlySpan<byte> recorded, int index, string current)
    {
        Span<byte> now = stackalloc byte[Size];
        Truncate(current, now);
        return !recorded.Slice(index * Size, Size).SequenceEqual(now);
    }

    /// <summary>Parses static parts (<c>name=value</c> lines) into a dictionary.</summary>
    /// <param name="parts">The parts, or <see langword="null"/>.</param>
    /// <returns>Values by name, ordinal.</returns>
    public static SortedDictionary<string, string> Parts(string? parts)
    {
        var result = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(parts))
            return result;
        foreach (var line in parts.Split('\n'))
        {
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
                result[line[..eq]] = line[(eq + 1)..];
        }

        return result;
    }

    /// <summary>A count as text, invariant.</summary>
    internal static string Count(int value) => value.ToString(CultureInfo.InvariantCulture);
}
