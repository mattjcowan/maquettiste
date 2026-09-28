using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Maquettiste.Engine;

/// <summary>Creates element ids.</summary>
public interface IIdGenerator
{
    /// <summary>Returns a new id: an uppercase Crockford ULID matching <see cref="IdFormat.Pattern"/>.</summary>
    /// <returns>The id.</returns>
    string NewId();
}

/// <summary>The default id generator: ULIDs from a clock and a cryptographic random source.</summary>
/// <param name="timeProvider">The clock; <see langword="null"/> means <see cref="TimeProvider.System"/>.</param>
public sealed class UlidIdGenerator(TimeProvider? timeProvider = null) : IIdGenerator
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <inheritdoc/>
    public string NewId()
    {
        Span<byte> randomness = stackalloc byte[10];
        RandomNumberGenerator.Fill(randomness);
        return Ulid.NewUlid(_time.GetUtcNow(), randomness).ToString();
    }
}

/// <summary>The id format: uppercase Crockford base32 ULIDs (D1).</summary>
public static class IdFormat
{
    /// <summary>The regular expression every id matches.</summary>
    public const string Pattern = "^[0-7][0-9A-HJKMNP-TV-Z]{25}$";

    /// <summary>The id length.</summary>
    public const int Length = 26;

    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Whether a string is a valid id.</summary>
    /// <param name="value">The candidate.</param>
    /// <returns><see langword="true"/> when it matches <see cref="Pattern"/>.</returns>
    public static bool IsValid(string? value)
    {
        if (value is not { Length: Length } || value[0] < '0' || value[0] > '7')
            return false;
        foreach (var c in value)
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Formats a 48-bit timestamp and an 80-bit value as an id: 10 characters of time, 16 characters of randomness.
    /// Deterministic generators (tests, the benchmark) use it.
    /// </summary>
    /// <param name="timestampMs">Milliseconds since the Unix epoch, below 2^48.</param>
    /// <param name="randomHigh">The high 16 bits of the random part.</param>
    /// <param name="randomLow">The low 64 bits of the random part.</param>
    /// <returns>The id.</returns>
    public static string Format(long timestampMs, ushort randomHigh, ulong randomLow)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(timestampMs);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(timestampMs, 1L << 48);
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, ((ulong)timestampMs << 16) | randomHigh);
        BinaryPrimitives.WriteUInt64BigEndian(bytes[8..], randomLow);
        return new Ulid(bytes).ToString();
    }
}
