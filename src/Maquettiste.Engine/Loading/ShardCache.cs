using System.Collections.Immutable;
using System.Text;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Loading;

/// <summary>
/// The parsed-shard cache (reference-types-seeds-localization.md section 5, step 4): a locale shard that passed validation is kept
/// under <c>&lt;cache&gt;/shards/&lt;content hash&gt;.bin</c> in a compact binary form beside the index cache, so a restart over the cache
/// volume rebuilds the shard's entries without tokenizing its JSON. The file is keyed by the shard's content hash alone (the parsed
/// form depends on nothing else), written atomically through the output path policy, and ignored when unreadable or of another format.
/// Completeness is not cached here: it depends on the source model's nodes as well, and costs 25 to 40 ms at spec scale.
/// </summary>
internal static class ShardCache
{
    /// <summary>The folder under the cache directory.</summary>
    public const string Folder = "shards";

    private const int Format = 1;
    private static ReadOnlySpan<byte> Magic => "MQSH"u8;

    /// <summary>The cache file of a shard's content hash.</summary>
    /// <param name="cacheDirectory">The cache directory.</param>
    /// <param name="hash">The shard's content hash (lowercase hex).</param>
    /// <returns>The path.</returns>
    public static string PathOf(string cacheDirectory, string hash) => Path.Combine(cacheDirectory, Folder, hash + ".bin");

    /// <summary>Reads a cached shard, or <see langword="null"/> when the file is missing, unreadable or of another format.</summary>
    /// <param name="file">The cache file.</param>
    /// <returns>The shard or null.</returns>
    public static LocaleShard? TryRead(string file)
    {
        byte[] bytes;
        try
        {
            if (!File.Exists(file))
                return null;
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            return Decode(bytes);
        }
        catch (Exception ex) when (ex is EndOfStreamException or FormatException or ArgumentException or IOException)
        {
            return null;
        }
    }

    /// <summary>Decodes the binary form.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The shard, or null for another format.</returns>
    internal static LocaleShard? Decode(byte[] bytes)
    {
        if (bytes.Length < 8 || !bytes.AsSpan(0, 4).SequenceEqual(Magic))
            return null;
        using var reader = new BinaryReader(new MemoryStream(bytes, 4, bytes.Length - 4, writable: false), Encoding.UTF8);
        if (reader.ReadInt32() != Format)
            return null;
        var schema = Optional(reader);
        var kind = reader.ReadString();
        var locale = reader.ReadString();
        var scope = reader.ReadString();
        var count = reader.ReadInt32();
        var entries = new Dictionary<string, TranslationEntry>(count, StringComparer.Ordinal);
        for (var i = 0; i < count; i++)
        {
            var key = reader.ReadString();
            var display = Optional(reader);
            var plural = Optional(reader);
            var label = Optional(reader);
            Description? description = reader.ReadByte() switch
            {
                0 => null,
                1 => new Description { Text = reader.ReadString() },
                2 => new Description { File = reader.ReadString() },
                _ => throw new FormatException("Unknown description form."),
            };
            var srcCount = reader.ReadInt32();
            IReadOnlyDictionary<string, string> src = ImmutableDictionary<string, string>.Empty;
            if (srcCount > 0)
            {
                var map = new Dictionary<string, string>(srcCount, StringComparer.Ordinal);
                for (var j = 0; j < srcCount; j++)
                    map[reader.ReadString()] = reader.ReadString();
                src = map;
            }

            entries[key] = new TranslationEntry { DisplayName = display, PluralName = plural, Label = label, Description = description, Src = src };
        }

        return new LocaleShard { SchemaPath = schema, Kind = kind, Locale = locale, Scope = scope, Entries = entries };
    }

    /// <summary>Encodes a shard; entries in ordinal key order, so equal shards give equal bytes.</summary>
    /// <param name="shard">The shard.</param>
    /// <returns>The bytes.</returns>
    internal static byte[] Encode(LocaleShard shard)
    {
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Magic);
            writer.Write(Format);
            Optional(writer, shard.SchemaPath);
            writer.Write(shard.Kind);
            writer.Write(shard.Locale);
            writer.Write(shard.Scope);
            writer.Write(shard.Entries.Count);
            foreach (var (key, entry) in shard.Entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                writer.Write(key);
                Optional(writer, entry.DisplayName);
                Optional(writer, entry.PluralName);
                Optional(writer, entry.Label);
                if (entry.Description is null)
                    writer.Write((byte)0);
                else if (entry.Description.File is { } file)
                {
                    writer.Write((byte)2);
                    writer.Write(file);
                }
                else
                {
                    writer.Write((byte)1);
                    writer.Write(entry.Description.Text ?? "");
                }

                writer.Write(entry.Src.Count);
                foreach (var (field, fingerprint) in entry.Src.OrderBy(s => s.Key, StringComparer.Ordinal))
                {
                    writer.Write(field);
                    writer.Write(fingerprint);
                }
            }
        }

        return buffer.ToArray();
    }

    /// <summary>Writes a shard's cache file when missing (temp file, then a move), through the output path policy; failures are ignored.</summary>
    /// <param name="cacheDirectory">The cache directory.</param>
    /// <param name="hash">The shard's content hash.</param>
    /// <param name="shard">The parsed shard.</param>
    /// <param name="paths">The output path policy.</param>
    public static void TryWrite(string cacheDirectory, string hash, LocaleShard shard, IOutputPathPolicy paths)
    {
        var file = PathOf(cacheDirectory, hash);
        var temp = file + "." + Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) + "-" + Environment.CurrentManagedThreadId.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".tmp";
        if (!paths.CheckEngineWrite(WriteTarget.Cache, file).Allowed || !paths.CheckEngineWrite(WriteTarget.Cache, temp).Allowed)
            return;
        try
        {
            if (File.Exists(file))
                return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllBytes(temp, Encode(shard));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception inner) when (inner is IOException or UnauthorizedAccessException)
            {
                // Best effort: a leftover temp file is harmless.
            }
        }
    }

    /// <summary>Removes the cache files of shard hashes the model no longer has; failures are ignored.</summary>
    /// <param name="cacheDirectory">The cache directory.</param>
    /// <param name="live">The hashes of the shards loaded now.</param>
    /// <param name="paths">The output path policy.</param>
    public static void Prune(string cacheDirectory, IReadOnlySet<string> live, IOutputPathPolicy paths)
    {
        var folder = Path.Combine(cacheDirectory, Folder);
        try
        {
            if (!Directory.Exists(folder))
                return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                var hash = name.EndsWith(".bin", StringComparison.Ordinal) ? name[..^4] : null;
                if ((hash is null || !live.Contains(hash)) && paths.CheckEngineWrite(WriteTarget.Cache, file).Allowed)
                    File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the next write prunes again.
        }
    }

    private static string? Optional(BinaryReader reader) => reader.ReadBoolean() ? reader.ReadString() : null;

    private static void Optional(BinaryWriter writer, string? value)
    {
        writer.Write(value is not null);
        if (value is not null)
            writer.Write(value);
    }
}
