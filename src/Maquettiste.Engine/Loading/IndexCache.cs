using System.Collections.Frozen;
using System.Text;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Loading;

/// <summary>Flags of an index cache record.</summary>
[Flags]
internal enum CacheRecordFlags : byte
{
    /// <summary>No flags.</summary>
    None = 0,

    /// <summary>The bytes passed schema validation and deserialized under the header's engine version and schema set.</summary>
    Validated = 1,

    /// <summary>The bytes were in canonical form.</summary>
    Canonical = 2,
}

/// <summary>One file in the index cache.</summary>
/// <param name="Path">The model-relative path.</param>
/// <param name="Length">The file length at the time it was read.</param>
/// <param name="LastWriteTicks">The file's last-write time (UTC ticks) at the time it was read.</param>
/// <param name="Hash">The SHA-256 of <paramref name="Bytes"/>.</param>
/// <param name="Flags">What the loader learned about the bytes.</param>
/// <param name="Bytes">The file bytes.</param>
internal sealed record CacheRecord(string Path, long Length, long LastWriteTicks, string Hash, CacheRecordFlags Flags, byte[] Bytes)
{
    /// <summary>Whether two records describe the same stat, content and verdict.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when equivalent.</returns>
    public bool SameAs(CacheRecord other) =>
        Path == other.Path && Length == other.Length && LastWriteTicks == other.LastWriteTicks && Hash == other.Hash && Flags == other.Flags;
}

/// <summary>
/// The index cache <c>CacheDirectory/index.v1.bin</c> (engine-design.md section 5; S13 fast load). Little-endian binary: the header
/// <c>MQIX</c>, format 1, the engine version and a hash of the embedded schema set; then the record count and one record per file
/// (path, length, last-write ticks, SHA-256, flags, byte count, bytes). A record whose bytes no longer hash to its SHA-256, or a file
/// whose header differs, is ignored, so a stale, foreign or damaged cache costs a re-read, never a wrong model. Written to a temp file
/// beside it and moved into place, through <see cref="IOutputPathPolicy.CheckEngineWrite"/> with <see cref="WriteTarget.Cache"/>.
/// </summary>
internal static class IndexCache
{
    /// <summary>The cache file name.</summary>
    public const string FileName = "index.v1.bin";

    private const int Format = 1;
    private static ReadOnlySpan<byte> Magic => "MQIX"u8;

    /// <summary>Hashes the embedded schema files, so a cache written under other schemas is not trusted.</summary>
    /// <param name="schemas">The registry.</param>
    /// <returns>The hash.</returns>
    public static string SchemaSetHash(ISchemaRegistry schemas)
    {
        using var hash = new HashBuilder();
        foreach (var name in schemas.FileNames)
            hash.Add(name).Add(schemas.GetFileBytes(name).Span);
        return hash.Finish();
    }

    /// <summary>Reads the cache; returns an empty map when it is missing, damaged or from another engine or schema set.</summary>
    /// <param name="path">The cache file.</param>
    /// <param name="schemaSetHash">The current schema set hash.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The records by model-relative path.</returns>
    public static async Task<FrozenDictionary<string, CacheRecord>> ReadAsync(string path, string schemaSetHash, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(path))
                return FrozenDictionary<string, CacheRecord>.Empty;
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FrozenDictionary<string, CacheRecord>.Empty;
        }

        try
        {
            using var reader = new BinaryReader(new MemoryStream(bytes, writable: false), Encoding.UTF8);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)
                || reader.ReadInt32() != Format
                || reader.ReadString() != EngineVersion.Value
                || reader.ReadString() != schemaSetHash)
                return FrozenDictionary<string, CacheRecord>.Empty;

            var count = reader.ReadInt32();
            var records = new Dictionary<string, CacheRecord>(StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var recordPath = reader.ReadString();
                var length = reader.ReadInt64();
                var ticks = reader.ReadInt64();
                var hash = reader.ReadString();
                var flags = (CacheRecordFlags)reader.ReadByte();
                var byteCount = reader.ReadInt32();
                var content = reader.ReadBytes(byteCount);
                if (content.Length != byteCount)
                    return FrozenDictionary<string, CacheRecord>.Empty;
                if (ContentHash.Of(content) == hash) // content-addressed: a record whose bytes do not match is dropped
                    records[recordPath] = new CacheRecord(recordPath, length, ticks, hash, flags, content);
            }

            return records.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException or OverflowException)
        {
            return FrozenDictionary<string, CacheRecord>.Empty;
        }
    }

    /// <summary>Writes the cache atomically. Returns <see langword="false"/> when the path policy refuses it or the write fails.</summary>
    /// <param name="path">The cache file.</param>
    /// <param name="tempSuffix">A suffix that makes the temp file name unique to this writer.</param>
    /// <param name="schemaSetHash">The current schema set hash.</param>
    /// <param name="records">The records, written in ordinal path order.</param>
    /// <param name="paths">The engine-write guard.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Whether the cache was written.</returns>
    public static async Task<bool> WriteAsync(
        string path,
        string tempSuffix,
        string schemaSetHash,
        IEnumerable<CacheRecord> records,
        IOutputPathPolicy paths,
        CancellationToken ct)
    {
        var directory = System.IO.Path.GetDirectoryName(path)!;
        var temp = System.IO.Path.Combine(directory, "." + FileName + ".mq-" + tempSuffix + ".tmp");
        if (!paths.CheckEngineWrite(WriteTarget.Cache, path).Allowed || !paths.CheckEngineWrite(WriteTarget.Cache, temp).Allowed)
            return false;

        try
        {
            Directory.CreateDirectory(directory);
            var ordered = records.OrderBy(r => r.Path, StringComparer.Ordinal).ToList();
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            {
                using var buffer = new MemoryStream();
                using var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true);
                writer.Write(Magic);
                writer.Write(Format);
                writer.Write(EngineVersion.Value);
                writer.Write(schemaSetHash);
                writer.Write(ordered.Count);
                foreach (var record in ordered)
                {
                    writer.Write(record.Path);
                    writer.Write(record.Length);
                    writer.Write(record.LastWriteTicks);
                    writer.Write(record.Hash);
                    writer.Write((byte)record.Flags);
                    writer.Write(record.Bytes.Length);
                    writer.Write(record.Bytes);
                    if (buffer.Length >= 1 << 20)
                        await FlushAsync(writer, buffer, stream, ct).ConfigureAwait(false);
                }

                await FlushAsync(writer, buffer, stream, ct).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false; // the cache only accelerates loading; a failed write costs a re-read next time
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task FlushAsync(BinaryWriter writer, MemoryStream buffer, FileStream stream, CancellationToken ct)
    {
        writer.Flush();
        await stream.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), ct).ConfigureAwait(false);
        buffer.SetLength(0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
