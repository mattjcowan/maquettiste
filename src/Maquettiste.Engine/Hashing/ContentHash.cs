using System.Security.Cryptography;
using System.Text;

namespace Maquettiste.Engine.Hashing;

/// <summary>SHA-256 content hashes as 64 lowercase hex characters: element ETags, manifest hashes and file hashes.</summary>
public static class ContentHash
{
    /// <summary>The length of a hash string.</summary>
    public const int Length = 64;

    /// <summary>Hashes bytes.</summary>
    /// <param name="bytes">The content.</param>
    /// <returns>The lowercase hex SHA-256.</returns>
    public static string Of(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Hashes the UTF-8 bytes of a string.</summary>
    /// <param name="text">The content.</param>
    /// <returns>The lowercase hex SHA-256.</returns>
    public static string Of(string text) => Of(Encoding.UTF8.GetBytes(text));

    /// <summary>Hashes a stream to its end.</summary>
    /// <param name="stream">The content.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The lowercase hex SHA-256.</returns>
    public static async Task<string> OfAsync(Stream stream, CancellationToken ct)
    {
        var hash = await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Whether a string is a well-formed hash (64 lowercase hex characters).</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when well formed.</returns>
    public static bool IsValid(string? value) =>
        value is { Length: Length } && value.AsSpan().IndexOfAnyExcept("0123456789abcdef") < 0;
}
