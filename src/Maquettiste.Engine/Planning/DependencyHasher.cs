using System.Collections.Concurrent;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Current hashes of dependency keys for one run (W6; engine-design.md section 11). Hashes are computed on first use and cached for
/// the run, so the object is thread-safe and meant to live exactly as long as the snapshot, resolved model and packs it was built over.
/// </summary>
/// <param name="model">The snapshot.</param>
/// <param name="resolved">The resolved model.</param>
/// <param name="packs">The packs (template hashes).</param>
/// <param name="schemaDiffs">Schema diffs by database name.</param>
internal sealed class DependencyHasher(ModelSnapshot model, ResolvedModel resolved, PackSet packs, IReadOnlyDictionary<string, SchemaDiffResult> schemaDiffs)
    : IDependencyHasher
{
    /// <summary>The hash of a key that no longer resolves.</summary>
    public const string Absent = "absent";

    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string[], TableFields> _tables = new(ReferenceEqualityComparer.Instance);
    private Func<string, string>? _compute;

    /// <inheritdoc/>
    public string CurrentHash(string dependencyKey)
    {
        ArgumentNullException.ThrowIfNull(dependencyKey);
        return _cache.TryGetValue(dependencyKey, out var hash) ? hash : _cache.GetOrAdd(dependencyKey, _compute ??= Compute);
    }

    /// <inheritdoc/>
    /// <remarks>The keys are expected sorted and distinct; a list that is not is sorted and deduplicated first, so any caller gets the same hash.</remarks>
    public string InputHash(string staticHash, IReadOnlyList<string> sortedReadKeys)
    {
        ArgumentNullException.ThrowIfNull(staticHash);
        ArgumentNullException.ThrowIfNull(sortedReadKeys);
        var keys = IsSortedDistinct(sortedReadKeys) ? sortedReadKeys : [.. sortedReadKeys.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        using var builder = new HashBuilder();
        builder.Add(staticHash);
        foreach (var key in keys)
            builder.Add(key).Add(CurrentHash(key));
        return builder.Finish();
    }

    /// <summary>
    /// Whether <see cref="InputHash"/> of these inputs equals <paramref name="expected"/>, computed without allocating: the change
    /// detector asks this of every stored unit state in a run, so the fields are gathered in a pooled buffer and the digest is
    /// compared as hex in place. The bytes hashed are exactly those <see cref="InputHash"/> hashes.
    /// </summary>
    /// <param name="staticHash">The unit's static hash.</param>
    /// <param name="sortedReadKeys">The recorded read keys.</param>
    /// <param name="expected">The stored input hash.</param>
    /// <returns><see langword="true"/> when equal.</returns>
    internal bool InputHashEquals(string staticHash, IReadOnlyList<string> sortedReadKeys, string expected)
    {
        if (sortedReadKeys is UnitStateStore.TableKeys indexed && expected.Length == ContentHash.Length
            && FromTable(staticHash, indexed, expected) is { } answer)
            return answer;
        if (expected.Length != ContentHash.Length || !IsSortedDistinct(sortedReadKeys))
            return string.Equals(InputHash(staticHash, sortedReadKeys), expected, StringComparison.Ordinal);
        var size = FieldSize(staticHash);
        foreach (var key in sortedReadKeys)
            size += FieldSize(key) + FieldSize(CurrentHash(key));
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var written = WriteField(buffer, 0, staticHash);
            foreach (var key in sortedReadKeys)
            {
                written = WriteField(buffer, written, key);
                written = WriteField(buffer, written, CurrentHash(key));
            }

            Span<byte> digest = stackalloc byte[System.Security.Cryptography.SHA256.HashSizeInBytes];
            System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0, written), digest);
            Span<char> hex = stackalloc char[ContentHash.Length];
            return Convert.TryToHexStringLower(digest, hex, out _) && hex.SequenceEqual(expected);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// <see cref="InputHashEquals"/> for read keys decoded from a unit state file (indexes into the file's key table, which every
    /// state of the file shares): each key's two hash fields (the key and its current hash) are encoded once per table index and
    /// copied, and sortedness is checked on precomputed ordinal ranks. The bytes hashed are the same; <see langword="null"/> when the
    /// keys are not sorted and distinct (the general path sorts them first).
    /// </summary>
    private bool? FromTable(string staticHash, UnitStateStore.TableKeys keys, string expected)
    {
        var table = _tables.GetOrAdd(keys.Table, static t => new TableFields(t));
        var indexes = keys.Indexes;
        for (var i = 1; i < indexes.Length; i++)
        {
            if (table.Rank[indexes[i - 1]] >= table.Rank[indexes[i]])
                return null;
        }

        var size = FieldSize(staticHash);
        foreach (var index in indexes)
            size += table.Field(index, this).Length;
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(size);
        try
        {
            var written = WriteField(buffer, 0, staticHash);
            foreach (var index in indexes)
            {
                var field = table.Field(index, this);
                field.CopyTo(buffer, written);
                written += field.Length;
            }

            Span<byte> digest = stackalloc byte[System.Security.Cryptography.SHA256.HashSizeInBytes];
            System.Security.Cryptography.SHA256.HashData(buffer.AsSpan(0, written), digest);
            Span<char> hex = stackalloc char[ContentHash.Length];
            return Convert.TryToHexStringLower(digest, hex, out _) && hex.SequenceEqual(expected);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>Per key table of a unit state file: ordinal ranks, and each key's encoded fields (key, current hash) once computed.</summary>
    private sealed class TableFields
    {
        private readonly string[] _keys;
        private readonly byte[]?[] _fields;

        public TableFields(string[] keys)
        {
            _keys = keys;
            _fields = new byte[]?[keys.Length];
            var order = new int[keys.Length];
            for (var i = 0; i < order.Length; i++)
                order[i] = i;
            Array.Sort(order, (a, b) => string.CompareOrdinal(keys[a], keys[b]));
            // Equal keys share a rank, so a list repeating a key (only a file this engine did not write could) is not "distinct".
            Rank = new int[keys.Length];
            for (var r = 0; r < order.Length; r++)
                Rank[order[r]] = r > 0 && string.Equals(keys[order[r]], keys[order[r - 1]], StringComparison.Ordinal) ? Rank[order[r - 1]] : r;
        }

        /// <summary>Each key's position in ordinal order (equal keys share one).</summary>
        public int[] Rank { get; }

        /// <summary>The key's two <see cref="HashBuilder"/> fields, as <see cref="WriteField"/> writes them.</summary>
        public byte[] Field(int index, DependencyHasher hasher)
        {
            var field = Volatile.Read(ref _fields[index]);
            if (field is not null)
                return field;
            var key = _keys[index];
            var hash = hasher.CurrentHash(key);
            field = new byte[FieldSize(key) + FieldSize(hash)];
            WriteField(field, WriteField(field, 0, key), hash);
            Volatile.Write(ref _fields[index], field); // a race writes equal bytes
            return field;
        }
    }

    private static int FieldSize(string value) => sizeof(ulong) + System.Text.Encoding.UTF8.GetByteCount(value);

    /// <summary>Writes one <see cref="HashBuilder"/> field: the UTF-8 byte count (8 bytes, little endian), then the bytes.</summary>
    private static int WriteField(byte[] buffer, int offset, string value)
    {
        var count = System.Text.Encoding.UTF8.GetBytes(value, buffer.AsSpan(offset + sizeof(ulong)));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(offset), (ulong)count);
        return offset + sizeof(ulong) + count;
    }

    /// <summary>Whether a list is in strictly increasing ordinal order.</summary>
    /// <param name="keys">The keys.</param>
    /// <returns><see langword="true"/> when sorted and distinct.</returns>
    internal static bool IsSortedDistinct(IReadOnlyList<string> keys)
    {
        for (var i = 1; i < keys.Count; i++)
        {
            if (string.CompareOrdinal(keys[i - 1], keys[i]) >= 0)
                return false;
        }

        return true;
    }

    private string Compute(string key)
    {
        var colon = key.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0)
            return Absent;
        var rest = key[(colon + 1)..];
        return key[..colon] switch
        {
            "e" => model.GetDocument(rest)?.DependencyHash ?? Absent,
            "k" => KindInfo.TryGet(rest, out var info) ? model.KindSetHash(info.Kind) : Absent,
            "r" => model.ReferrersHash(rest),
            "s" => SettingsHash(rest),
            "l" => LocaleHash(rest),
            "t" => TemplateHash(rest),
            "d" => resolved.Find(rest) is RDatabase database && schemaDiffs.TryGetValue(database.Name, out var diff) ? diff.Hash : Absent,
            _ => Absent,
        };
    }

    private string SettingsHash(string section)
    {
        var settings = model.Settings;
        return section switch
        {
            // Database overrides are part of the conventions (engine-design.md section 11).
            "conventions" => CanonicalForm.Hash("s:conventions", new { settings.Conventions, settings.Databases }),
            "typeMaps" => CanonicalForm.Hash("s:typeMaps", settings.TypeMaps),
            "inflection" => CanonicalForm.Hash("s:inflection", settings.Inflection),
            "localization" => CanonicalForm.Hash("s:localization", settings.Localization),
            // Strategy declarations and every referenceStorage choice (project and per database), by database name.
            "referenceData" => CanonicalForm.Hash("s:referenceData", new
            {
                settings.ReferenceData,
                Project = settings.Conventions.ReferenceStorage,
                Databases = new SortedDictionary<string, StorageChoice?>(
                    settings.Databases.Where(p => p.Value.ReferenceStorage is not null).ToDictionary(p => p.Key, p => p.Value.ReferenceStorage), StringComparer.Ordinal),
            }),
            _ => Absent,
        };
    }

    /// <summary><c>l:&lt;locale&gt;:&lt;owner&gt;</c>: the owner's entries in that locale and their sidecars.</summary>
    private string LocaleHash(string localeAndOwner)
    {
        var colon = localeAndOwner.IndexOf(':', StringComparison.Ordinal);
        return colon <= 0 ? Absent : model.Localization.OwnerHash(localeAndOwner[..colon], localeAndOwner[(colon + 1)..]);
    }

    private string TemplateHash(string packAndPath)
    {
        var slash = packAndPath.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0)
            return Absent;
        var name = packAndPath[..slash];
        var pack = packs.Packs.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));
        if (pack is null)
            return Absent;
        return PackFiles.Hash(pack.RootPath, packAndPath[(slash + 1)..]) ?? Absent;
    }
}
