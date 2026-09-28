using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Maquettiste.Engine.Hashing;

namespace Maquettiste.Engine.Tests.Hashing;

public sealed class HashingTests
{
    [Fact]
    public void Content_hash_is_lowercase_sha256_hex()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", ContentHash.Of("abc"u8));
        Assert.Equal(ContentHash.Of("abc"u8), ContentHash.Of("abc"));
        Assert.True(ContentHash.IsValid(ContentHash.Of("x")));
        Assert.False(ContentHash.IsValid(ContentHash.Of("x").ToUpperInvariant()));
    }

    [Fact]
    public void Hash_builder_separates_fields_and_distinguishes_null_from_empty()
    {
        Assert.NotEqual(HashBuilder.Of("ab", "c"), HashBuilder.Of("a", "bc"));
        Assert.NotEqual(HashBuilder.Of(null), HashBuilder.Of(""));
        Assert.NotEqual(HashBuilder.Of("a"), HashBuilder.Of("a", ""));
        Assert.Equal(HashBuilder.Of("mq-unit-1", "x"), HashBuilder.Of("mq-unit-1", "x"));
        Assert.Equal(64, HashBuilder.Of("x").Length);
    }

    [Fact]
    public void Hash_builder_accepts_long_strings_and_bytes()
    {
        var text = new string('é', 5000);

        using var builder = new HashBuilder();
        var hash = builder.Add(text).Add([1, 2, 3]).Add(42L).Finish();

        Assert.True(ContentHash.IsValid(hash));
        Assert.Throws<ObjectDisposedException>(() => builder.Add("again"));
    }

    [Fact]
    public void Hash_builder_hashes_exactly_the_length_prefixed_fields()
    {
        // The builder buffers fields and streams past 64 KiB; the definition is SHA-256 over (8-byte little-endian length, bytes)
        // per field, ulong.MaxValue with no bytes for null. Small, null, multi-byte, large and many-small-field inputs all match it.
        var large = new string('é', 70_000);
        var bytes = Enumerable.Range(0, 100_000).Select(i => (byte)i).ToArray();
        var many = Enumerable.Range(0, 20_000).Select(i => "key-" + i).ToArray();

        Assert.Equal(Reference("a", "b"), HashBuilder.Of("a", "b"));
        Assert.Equal(Reference(null, ""), HashBuilder.Of(null, ""));
        Assert.Equal(Reference("x", large, "y"), HashBuilder.Of("x", large, "y"));
        Assert.Equal(Reference(many), HashBuilder.Of(many));

        using var builder = new HashBuilder();
        Assert.Equal(Reference("head", bytes, "42", large), builder.Add("head").Add(bytes).Add(42L).Add(large).Finish());
        Assert.Throws<ObjectDisposedException>(() => builder.Finish());

        var disposed = new HashBuilder();
        disposed.Dispose();
        Assert.Throws<ObjectDisposedException>(() => disposed.Add("x"));
        Assert.Throws<ObjectDisposedException>(() => disposed.Finish());
    }

    private static string Reference(params object?[] fields)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> prefix = stackalloc byte[8];
        foreach (var field in fields)
        {
            var data = field switch
            {
                null => null,
                string text => Encoding.UTF8.GetBytes(text),
                byte[] raw => raw,
                _ => throw new ArgumentException("Unsupported field."),
            };
            BinaryPrimitives.WriteUInt64LittleEndian(prefix, data is null ? ulong.MaxValue : (ulong)data.Length);
            hash.AppendData(prefix);
            if (data is not null)
                hash.AppendData(data);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
