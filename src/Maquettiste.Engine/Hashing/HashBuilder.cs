using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Maquettiste.Engine.Hashing;

/// <summary>
/// Builds <c>H(...)</c>: SHA-256 over length-prefixed fields, as lowercase hex (engine-design.md section 11). Each field is
/// written as its byte length (8 bytes, little endian) followed by its bytes; a <see langword="null"/> string is written as
/// the length <see cref="ulong.MaxValue"/> with no bytes, so it differs from an empty string.
/// </summary>
/// <remarks>
/// Fields are gathered in a pooled buffer and hashed in one call when the builder finishes, which costs far less than one
/// incremental update per field for the short fields dependency hashes are made of. Past <see cref="StreamThreshold"/> bytes
/// the buffer is streamed into an incremental hash instead; the bytes hashed, and so the result, are the same either way.
/// </remarks>
public sealed class HashBuilder : IDisposable
{
    /// <summary>Buffered bytes beyond which the builder streams into an incremental hash.</summary>
    private const int StreamThreshold = 64 * 1024;

    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(512);
    private int _length;
    private IncrementalHash? _hash;
    private bool _finished;
    private bool _disposed;

    /// <summary>Adds a UTF-8 string field.</summary>
    /// <param name="value">The field, or <see langword="null"/>.</param>
    /// <returns>This builder.</returns>
    public HashBuilder Add(string? value)
    {
        if (value is null)
        {
            WriteLength(ulong.MaxValue);
            return this;
        }

        var count = Encoding.UTF8.GetByteCount(value);
        WriteLength((ulong)count);
        if (count <= StreamThreshold)
        {
            Encoding.UTF8.GetBytes(value, Reserve(count));
            _length += count;
        }
        else
        {
            Append(Encoding.UTF8.GetBytes(value));
        }

        return this;
    }

    /// <summary>Adds a byte field.</summary>
    /// <param name="value">The field.</param>
    /// <returns>This builder.</returns>
    public HashBuilder Add(ReadOnlySpan<byte> value)
    {
        WriteLength((ulong)value.Length);
        Append(value);
        return this;
    }

    /// <summary>Adds an integer field (its invariant decimal text).</summary>
    /// <param name="value">The field.</param>
    /// <returns>This builder.</returns>
    public HashBuilder Add(long value) => Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>Finishes the hash. The builder cannot be used afterwards.</summary>
    /// <returns>The lowercase hex SHA-256.</returns>
    public string Finish()
    {
        ObjectDisposedException.ThrowIf(_finished || _disposed, this);
        _finished = true;
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        if (_hash is null)
        {
            SHA256.HashData(_buffer.AsSpan(0, _length), digest);
        }
        else
        {
            _hash.AppendData(_buffer, 0, _length);
            _hash.GetHashAndReset(digest);
        }

        _length = 0;
        return Convert.ToHexStringLower(digest);
    }

    /// <summary>Computes <c>H(fields…)</c> in one call.</summary>
    /// <param name="fields">The fields, in order.</param>
    /// <returns>The lowercase hex SHA-256.</returns>
    public static string Of(params ReadOnlySpan<string?> fields)
    {
        using var builder = new HashBuilder();
        foreach (var field in fields)
            builder.Add(field);
        return builder.Finish();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposed = true;
        _hash?.Dispose();
        _hash = null;
        var buffer = _buffer;
        _buffer = [];
        if (buffer.Length > 0)
            ArrayPool<byte>.Shared.Return(buffer);
    }

    private void WriteLength(ulong length)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(Reserve(sizeof(ulong)), length);
        _length += sizeof(ulong);
    }

    private void Append(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length <= StreamThreshold)
        {
            bytes.CopyTo(Reserve(bytes.Length));
            _length += bytes.Length;
            return;
        }

        Flush();
        _hash!.AppendData(bytes);
    }

    /// <summary>Space for <paramref name="count"/> more bytes at the end of the buffer (at most <see cref="StreamThreshold"/>).</summary>
    private Span<byte> Reserve(int count)
    {
        ObjectDisposedException.ThrowIf(_finished || _disposed, this);
        if (_length + count > StreamThreshold)
            Flush();
        if (_length + count > _buffer.Length)
        {
            var larger = ArrayPool<byte>.Shared.Rent(Math.Max(_buffer.Length * 2, _length + count));
            _buffer.AsSpan(0, _length).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = larger;
        }

        return _buffer.AsSpan(_length, count);
    }

    /// <summary>Moves the buffered bytes into the incremental hash, creating it on first use.</summary>
    private void Flush()
    {
        _hash ??= IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        _hash.AppendData(_buffer, 0, _length);
        _length = 0;
    }
}
