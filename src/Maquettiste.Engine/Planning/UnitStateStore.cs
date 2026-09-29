using System.Collections.Frozen;
using System.Text;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Pipeline;

namespace Maquettiste.Engine.Planning;

/// <summary>
/// Stores unit states in <c>CacheDirectory/units/&lt;pack&gt;.v4.bin</c> (W6; engine-design.md section 11; the file is named after
/// the format). The file is a cache: a missing, truncated or foreign file (other magic, format or engine version) loads as empty, which
/// only means every unit of the pack renders again (and the explanation says <c>state-reset</c>, <see cref="WasReset"/>). Layout
/// (format 4): <c>MQUS</c>, format, engine version, then the string table (count, then each distinct read key and static-parts text
/// once, length-prefixed UTF-8, in first-use order), then the state count and per state its key, input hash, read keys (7-bit encoded
/// indexes into the table), outputs (path, manifest hash, length, last-write ticks), the per-key hashes (a 7-bit count, zero or the
/// read key count, then 16 bytes each, generation-ui.md section 4.2), the static parts (a 7-bit table index plus one, zero for
/// none) and the element names (a 7-bit count, then per named element key its position among the read keys and the table index of
/// its label, <c>Name (kind)</c>, positions ascending; format 4, so a cause can name a deleted element); strings are length-prefixed
/// UTF-8. Saving deletes the files of formats 2 and 3 (<c>.v1.bin</c>, <c>.v3.bin</c>).
/// States are written sorted by key. Read keys repeat across units (every unit of an entity reads that entity's keys), so the table
/// keeps the file small and the decoded states share one string per distinct key.
/// </summary>
/// <remarks>
/// The store remembers, per pack, the bytes it last read or wrote with their states: a load that reads the same bytes again returns
/// those states without decoding (a watch or editor host runs one store for many runs). Only byte-identical content is reused, so an
/// edited, replaced or foreign file is always decoded. The states handed out are read-only and shared between loads.
/// </remarks>
/// <param name="options">The engine options.</param>
/// <param name="paths">The engine-write guard (<see cref="WriteTarget.Cache"/>).</param>
internal sealed class UnitStateStore(EngineOptions options, IOutputPathPolicy paths) : IUnitStateStore
{
    private const int Format = 4;
    private static ReadOnlySpan<byte> Magic => "MQUS"u8;

    private readonly EngineFiles _files = new(paths, WriteTarget.Cache);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Decoded> _last = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _reset = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public bool WasReset(string pack) => _reset.TryGetValue(pack, out var reset) && reset;

    /// <summary>The folder holding the state files.</summary>
    internal string Folder => Path.Combine(Path.GetFullPath(options.CacheDirectory), "units");

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, UnitState>> LoadAsync(string pack, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var file = FileOf(pack);
        byte[] bytes;
        try
        {
            if (!File.Exists(file))
            {
                // A state file of the previous format and no file of this one: every unit renders, and says why.
                _reset[pack] = LegacyFilesOf(pack).Any(File.Exists);
                return FrozenDictionary<string, UnitState>.Empty;
            }

            bytes = await File.ReadAllBytesAsync(file, ct).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return FrozenDictionary<string, UnitState>.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return FrozenDictionary<string, UnitState>.Empty;
        }

        if (_last.TryGetValue(file, out var last) && last.Bytes.AsSpan().SequenceEqual(bytes))
            return last.States;
        var states = Decode(bytes);
        _reset[pack] = states is null;
        if (states is null)
        {
            _last.TryRemove(file, out _);
            return FrozenDictionary<string, UnitState>.Empty;
        }

        var shared = new System.Collections.ObjectModel.ReadOnlyDictionary<string, UnitState>(states);
        _last[file] = new Decoded(bytes, shared);
        return shared;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(string pack, IReadOnlyCollection<UnitState> states, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(states);
        var file = FileOf(pack);
        _last.TryRemove(file, out var previous);
        _reset.TryRemove(pack, out _);
        foreach (var legacy in LegacyFilesOf(pack))
        {
            if (File.Exists(legacy))
                _files.Delete(legacy);
        }
        if (states.Count == 0)
        {
            _files.Delete(file);
            return;
        }

        var indexed = new List<UnitState>(states.Count);
        var bytes = Encode(states, indexed);

        // The same bytes as the file this store last read or wrote, still of that length on disk (a run that skipped every unit of
        // the pack): the file is left alone, as the manifest store leaves an unchanged manifest, so its time stamp keeps saying
        // "unchanged" (the last-run record compares it).
        if (previous is null || !previous.Bytes.AsSpan().SequenceEqual(bytes) || !SameLength(file, bytes.Length))
            await _files.WriteAsync(file, bytes, ct).ConfigureAwait(false);

        // Remembered as a load of these bytes decodes them (read keys as indexes into the written key table), so the next run
        // neither decodes the file nor hashes every key again when it saves the states of the units it skipped.
        var byKey = new Dictionary<string, UnitState>(indexed.Count, StringComparer.Ordinal);
        foreach (var state in indexed)
            byKey[state.Key] = state;
        _last[file] = new Decoded(bytes, new System.Collections.ObjectModel.ReadOnlyDictionary<string, UnitState>(byKey));
    }

    /// <summary>
    /// The states this store last read or wrote for a pack, without reading the file again (the last-run record reads the states a
    /// run's writer just saved), or <see langword="null"/> when it holds none.
    /// </summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The states, or <see langword="null"/>.</returns>
    internal IReadOnlyDictionary<string, UnitState>? Remembered(string pack) =>
        _last.TryGetValue(FileOf(pack), out var last) ? last.States : null;

    private static bool SameLength(string file, int length)
    {
        var info = new FileInfo(file);
        return info.Exists && info.Length == length;
    }

    /// <summary>The state file of a pack. Pack names are kebab keys; any other name is replaced by a hash so it cannot form a path.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The absolute path.</returns>
    internal string FileOf(string pack) => Path.Combine(Folder, SafeName(pack) + ".v" + Format.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".bin");

    /// <summary>The state file of format 2, which was named <c>.v1.bin</c>; deleted when the pack's states are next saved.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The absolute path.</returns>
    internal string LegacyFileOf(string pack) => Path.Combine(Folder, SafeName(pack) + ".v1.bin");

    /// <summary>The state files of earlier formats (2, named <c>.v1.bin</c>, and 3); deleted when the pack's states are next saved.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The absolute paths.</returns>
    internal IEnumerable<string> LegacyFilesOf(string pack) => [LegacyFileOf(pack), Path.Combine(Folder, SafeName(pack) + ".v3.bin")];

    /// <summary>A file-name-safe form of a pack name.</summary>
    /// <param name="pack">The pack name.</param>
    /// <returns>The name, or <c>x-</c> plus 16 hex characters of its hash.</returns>
    internal static string SafeName(string pack)
    {
        var safe = pack.Length is > 0 and <= 64 && char.IsAsciiLetterLower(pack[0])
            && pack.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');
        return safe ? pack : "x-" + ContentHash.Of(pack)[..16];
    }

    /// <summary>Encodes states.</summary>
    /// <param name="states">The states.</param>
    /// <returns>The bytes.</returns>
    internal static byte[] Encode(IReadOnlyCollection<UnitState> states) => Encode(states, null);

    /// <summary>Encodes states; with <paramref name="indexed"/>, also gives them back as a load of the bytes would (key order, indexed read keys).</summary>
    /// <param name="states">The states.</param>
    /// <param name="indexed">Receives the states with <see cref="TableKeys"/> read keys, or <see langword="null"/>.</param>
    /// <returns>The bytes.</returns>
    internal static byte[] Encode(IReadOnlyCollection<UnitState> states, List<UnitState>? indexed)
    {
        var ordered = SortedByKey(states);
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        var strings = new List<string>();
        var body = new ByteWriter(ordered.Count * 256); // about 280 bytes per state in a typical file
        int IndexOf(string value)
        {
            if (!table.TryGetValue(value, out var index))
            {
                index = strings.Count;
                table[value] = index;
                strings.Add(value);
            }

            return index;
        }

        // The element names after the static parts: count, then (position among the read keys, label table index), positions ascending.
        int[] WriteNames(ByteWriter writer, UnitState state)
        {
            if (state.Names is not { Count: > 0 } names)
            {
                writer.WriteVarInt(0);
                return [];
            }

            var positions = new List<int>();
            var labels = new List<int>();
            var k = 0;
            foreach (var key in state.ReadKeys)
            {
                if (names.TryGetValue(key, out var label))
                {
                    positions.Add(k);
                    labels.Add(IndexOf(label));
                }

                k++;
            }

            writer.WriteVarInt(positions.Count);
            for (var i = 0; i < positions.Count; i++)
            {
                writer.WriteVarInt(positions[i]);
                writer.WriteVarInt(labels[i]);
            }

            return [.. labels];
        }

        // States decoded from a file (the skipped units of an incremental run: nearly all of them) carry their keys as indexes into
        // that file's table: each old index is looked up once and remembered, so the new table is built in the same first-use order
        // without hashing every key of every state.
        var remaps = new Dictionary<string[], int[]>(ReferenceEqualityComparer.Instance);
        var stateIndexes = indexed is null ? null : new int[ordered.Count][];
        var ranges = indexed is null ? null : new (int Start, int Length)[ordered.Count];
        var hashStarts = indexed is null ? null : new int[ordered.Count];
        var partIndexes = indexed is null ? null : new int[ordered.Count];
        var nameIndexes = indexed is null ? null : new int[ordered.Count][];
        body.WriteInt32(ordered.Count);
        for (var s = 0; s < ordered.Count; s++)
        {
            var state = ordered[s];
            var start = body.Length;
            if (state.ReadKeys is TableKeys tableKeys)
            {
                if (!remaps.TryGetValue(tableKeys.Table, out var remap))
                {
                    remap = new int[tableKeys.Table.Length];
                    Array.Fill(remap, -1);
                    remaps[tableKeys.Table] = remap;
                }

                // Keys first (first-use order must follow the states), so a state whose keys all keep their index can be copied as
                // the bytes it was read from or last written as: the same record, byte for byte.
                var olds = tableKeys.Indexes;
                var same = tableKeys.Source is not null && ReferenceEquals(tableKeys.Owner, state);
                for (var k = 0; k < olds.Length; k++)
                {
                    var index = remap[olds[k]];
                    if (index < 0)
                        remap[olds[k]] = index = IndexOf(tableKeys.Table[olds[k]]);
                    same &= index == olds[k];
                }

                // The static parts are the record's last table index: it must keep its index too.
                var partsIndex = -1;
                if (same && tableKeys.PartsIndex >= 0)
                {
                    partsIndex = remap[tableKeys.PartsIndex];
                    if (partsIndex < 0)
                        remap[tableKeys.PartsIndex] = partsIndex = IndexOf(tableKeys.Table[tableKeys.PartsIndex]);
                    same = partsIndex == tableKeys.PartsIndex && string.Equals(tableKeys.Table[partsIndex], state.StaticParts, StringComparison.Ordinal);
                }
                else if (same)
                {
                    same = state.StaticParts is null;
                }

                // The element names' labels are table indexes too: each must keep its index.
                for (var n = 0; same && n < tableKeys.NameIndexes.Length; n++)
                {
                    var old = tableKeys.NameIndexes[n];
                    var index = remap[old];
                    if (index < 0)
                        remap[old] = index = IndexOf(tableKeys.Table[old]);
                    same = index == old;
                }

                if (same)
                {
                    body.WriteBytes(tableKeys.Source.AsSpan(tableKeys.Start, tableKeys.Length));
                    if (stateIndexes is not null)
                    {
                        stateIndexes[s] = olds; // unchanged indexes; the array is never modified
                        ranges![s] = (start, body.Length - start);
                        partIndexes![s] = partsIndex;
                        nameIndexes![s] = tableKeys.NameIndexes;
                        hashStarts![s] = System.Runtime.InteropServices.MemoryMarshal.TryGetArray(state.KeyHashes, out var segment)
                            && ReferenceEquals(segment.Array, tableKeys.Source) && segment.Count > 0
                            ? start + (segment.Offset - tableKeys.Start)
                            : -1;
                    }

                    continue;
                }

                body.WriteString(state.Key);
                body.WriteString(state.InputHash);
                body.WriteVarInt(olds.Length);
                var written = stateIndexes is null ? null : stateIndexes[s] = new int[olds.Length];
                for (var k = 0; k < olds.Length; k++)
                {
                    var index = remap[olds[k]];
                    body.WriteVarInt(index);
                    written?[k] = index;
                }
            }
            else
            {
                body.WriteString(state.Key);
                body.WriteString(state.InputHash);
                body.WriteVarInt(state.ReadKeys.Count);
                var written = stateIndexes is null ? null : stateIndexes[s] = new int[state.ReadKeys.Count];
                var k = 0;
                foreach (var key in state.ReadKeys)
                {
                    var index = IndexOf(key);
                    body.WriteVarInt(index);
                    written?[k++] = index;
                }
            }

            body.WriteVarInt(state.Outputs.Count);
            foreach (var output in state.Outputs)
            {
                body.WriteString(output.Path);
                body.WriteString(output.ManifestHash);
                body.WriteInt64(output.Length);
                body.WriteInt64(output.LastWriteUtcTicks);
            }

            var hashes = state.KeyHashes.Length == state.ReadKeys.Count * KeyHashes.Size && state.ReadKeys.Count > 0 ? state.KeyHashes.Span : default;
            body.WriteVarInt(hashes.IsEmpty ? 0 : state.ReadKeys.Count);
            hashStarts?[s] = hashes.IsEmpty ? -1 : body.Length;
            body.WriteBytes(hashes);
            var parts = state.StaticParts is null ? -1 : IndexOf(state.StaticParts);
            partIndexes?[s] = parts;
            body.WriteVarInt(parts + 1);
            var labelIndexes = WriteNames(body, state);
            nameIndexes?[s] = labelIndexes;

            if (ranges is not null)
                ranges[s] = (start, body.Length - start);
        }

        var head = new ByteWriter((strings.Count * 48) + 64);
        head.WriteBytes(Magic);
        head.WriteInt32(Format);
        head.WriteString(EngineVersion.Value);
        head.WriteInt32(strings.Count);
        foreach (var value in strings)
            head.WriteString(value);
        var bytes = new byte[head.Length + body.Length];
        head.Written.CopyTo(bytes);
        body.Written.CopyTo(bytes.AsSpan(head.Length));
        if (indexed is not null)
        {
            var keyTable = strings.ToArray();
            for (var s = 0; s < ordered.Count; s++)
            {
                var state = ordered[s];
                var (start, length) = ranges![s];
                var keys = new TableKeys(keyTable, stateIndexes![s], bytes, head.Length + start, length, partIndexes![s]) { NameIndexes = nameIndexes![s] };
                var copy = new UnitState(state.Key, state.InputHash, keys, state.Outputs)
                {
                    Names = state.Names,
                    KeyHashes = hashStarts![s] < 0 ? default : new ReadOnlyMemory<byte>(bytes, head.Length + hashStarts[s], keys.Count * KeyHashes.Size),
                    StaticParts = partIndexes[s] < 0 ? null : keyTable[partIndexes[s]],
                };
                keys.Owner = copy;
                indexed.Add(copy);
            }
        }

        return bytes;
    }

    /// <summary>Decodes states, or returns <see langword="null"/> for anything but a complete file of this format and engine version.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <returns>The states by key.</returns>
    internal static Dictionary<string, UnitState>? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true));
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic) || reader.ReadInt32() != Format
                || !string.Equals(reader.ReadString(), EngineVersion.Value, StringComparison.Ordinal))
                return null;
            var strings = new string[Count(reader, bytes.Length)];
            for (var i = 0; i < strings.Length; i++)
                strings[i] = reader.ReadString();

            var count = Count(reader, bytes.Length);
            var states = new Dictionary<string, UnitState>(count, StringComparer.Ordinal);
            for (var i = 0; i < count; i++)
            {
                var start = (int)stream.Position;
                var key = reader.ReadString();
                var inputHash = reader.ReadString();
                var indexes = new int[Index(reader, bytes.Length + 1)];
                for (var k = 0; k < indexes.Length; k++)
                    indexes[k] = Index(reader, strings.Length);
                var outputs = new UnitOutput[Index(reader, bytes.Length + 1)];
                for (var o = 0; o < outputs.Length; o++)
                    outputs[o] = new UnitOutput(reader.ReadString(), reader.ReadString(), reader.ReadInt64(), reader.ReadInt64());
                var hashCount = Index(reader, indexes.Length + 1);
                if (hashCount != 0 && hashCount != indexes.Length)
                    throw new FormatException("Invalid hash count.");
                var hashStart = (int)stream.Position;
                var hashLength = hashCount * KeyHashes.Size;
                if (hashStart + hashLength > bytes.Length)
                    throw new EndOfStreamException();
                stream.Position = hashStart + hashLength;
                var partsIndex = Index(reader, strings.Length + 1) - 1;
                var positions = new int[Index(reader, indexes.Length + 1)];
                var labels = new int[positions.Length];
                for (var n = 0; n < positions.Length; n++)
                {
                    positions[n] = Index(reader, indexes.Length);
                    labels[n] = Index(reader, strings.Length);
                    if (n > 0 && positions[n] <= positions[n - 1])
                        throw new FormatException("Invalid name position.");
                }

                var keys = new TableKeys(strings, indexes, bytes, start, (int)stream.Position - start, partsIndex) { NameIndexes = labels };
                var state = new UnitState(key, inputHash, keys, outputs)
                {
                    Names = positions.Length == 0 ? null : new TableNames(strings, indexes, positions, labels),
                    KeyHashes = hashLength == 0 ? default : new ReadOnlyMemory<byte>(bytes, hashStart, hashLength),
                    StaticParts = partsIndex < 0 ? null : strings[partsIndex],
                };
                keys.Owner = state;
                states[key] = state;
            }

            return stream.Position == stream.Length ? states : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or DecoderFallbackException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The states ordered by key (ordinal); the input itself when it already is.</summary>
    private static IReadOnlyList<UnitState> SortedByKey(IReadOnlyCollection<UnitState> states)
    {
        if (states is IReadOnlyList<UnitState> list)
        {
            var sorted = true;
            for (var i = 1; i < list.Count && sorted; i++)
                sorted = string.CompareOrdinal(list[i - 1].Key, list[i].Key) <= 0;
            if (sorted)
                return list;
        }

        return [.. states.OrderBy(s => s.Key, StringComparer.Ordinal)];
    }

    /// <summary>Reads a 7-bit encoded index below <paramref name="limit"/>.</summary>
    private static int Index(BinaryReader reader, int limit)
    {
        var index = reader.Read7BitEncodedInt();
        if (index < 0 || index >= limit)
            throw new FormatException("Invalid index.");
        return index;
    }

    /// <summary>
    /// A growable little-endian buffer that writes what <see cref="BinaryWriter"/> would (<see cref="BinaryWriter.Write(string)"/>'s
    /// 7-bit length prefix and UTF-8, <see cref="BinaryWriter.Write7BitEncodedInt(int)"/>), without a stream underneath.
    /// </summary>
    private sealed class ByteWriter(int capacity)
    {
        private byte[] _buffer = new byte[Math.Max(capacity, 256)];

        public int Length { get; private set; }

        public ReadOnlySpan<byte> Written => _buffer.AsSpan(0, Length);

        public void WriteBytes(ReadOnlySpan<byte> bytes)
        {
            bytes.CopyTo(Reserve(bytes.Length));
            Length += bytes.Length;
        }

        public void WriteInt32(int value)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(Reserve(4), value);
            Length += 4;
        }

        public void WriteInt64(long value)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(Reserve(8), value);
            Length += 8;
        }

        public void WriteVarInt(int value)
        {
            var span = Reserve(5);
            var n = 0;
            var v = (uint)value;
            while (v >= 0x80)
            {
                span[n++] = (byte)(v | 0x80);
                v >>= 7;
            }

            span[n++] = (byte)v;
            Length += n;
        }

        public void WriteString(string value)
        {
            if (value.Length < 0x80 && System.Text.Ascii.IsValid(value))
            {
                // ASCII (keys, hashes, paths): UTF-8 is the same bytes, and the length fits the one-byte 7-bit prefix.
                var span = Reserve(value.Length + 1);
                span[0] = (byte)value.Length;
                System.Text.Ascii.FromUtf16(value, span[1..], out _);
                Length += value.Length + 1;
                return;
            }

            var count = Encoding.UTF8.GetByteCount(value);
            WriteVarInt(count);
            Encoding.UTF8.GetBytes(value, Reserve(count));
            Length += count;
        }

        private Span<byte> Reserve(int count)
        {
            if (Length + count > _buffer.Length)
                Array.Resize(ref _buffer, Math.Max(_buffer.Length * 2, Length + count));
            return _buffer.AsSpan(Length, count);
        }
    }

    /// <summary>A file's bytes and what they decode to.</summary>
    private sealed record Decoded(byte[] Bytes, IReadOnlyDictionary<string, UnitState> States);

    /// <summary>
    /// The read keys of a decoded state: indexes into the file's key table, which the decoded states share (smaller than one string
    /// array per state, and it lets <see cref="Encode(IReadOnlyCollection{UnitState}, List{UnitState})"/> map old indexes to new
    /// ones instead of hashing every key again).
    /// </summary>
    /// <param name="table">The file's key table.</param>
    /// <param name="indexes">The state's keys as table indexes, in order.</param>
    /// <param name="source">The file bytes the state's record was read from or written to, or <see langword="null"/>.</param>
    /// <param name="start">Where the record starts in <paramref name="source"/>.</param>
    /// <param name="length">The record's length.</param>
    /// <param name="partsIndex">The index of the state's static parts in <paramref name="table"/>, or -1.</param>
    internal sealed class TableKeys(string[] table, int[] indexes, byte[]? source = null, int start = 0, int length = 0, int partsIndex = -1) : IReadOnlyList<string>
    {
        /// <summary>The index of the state's static parts in <see cref="Table"/>, or -1.</summary>
        public int PartsIndex => partsIndex;

        /// <summary>The shared key table.</summary>
        public string[] Table => table;

        /// <summary>The table indexes of the state's element name labels, in record order.</summary>
        public int[] NameIndexes { get; init; } = [];

        /// <summary>The indexes into <see cref="Table"/>.</summary>
        public int[] Indexes => indexes;

        /// <summary>
        /// The bytes holding the state's whole record (key, input hash, key indexes into <see cref="Table"/>, outputs), so a save that
        /// gives every key the same index copies it; <see langword="null"/> when unknown.
        /// </summary>
        public byte[]? Source => source;

        /// <summary>The record's offset in <see cref="Source"/>.</summary>
        public int Start => start;

        /// <summary>The record's length.</summary>
        public int Length => length;

        /// <summary>
        /// The state the record belongs to (set once, by whoever made both). A copy of the state, or another state reusing these keys,
        /// is not the owner, so its record is always encoded afresh.
        /// </summary>
        public UnitState? Owner { get; set; }

        /// <inheritdoc/>
        public int Count => indexes.Length;

        /// <inheritdoc/>
        public string this[int index] => table[indexes[index]];

        /// <inheritdoc/>
        public IEnumerator<string> GetEnumerator()
        {
            foreach (var index in indexes)
                yield return table[index];
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A decoded state's element names, built into a dictionary on first use (a plan explanation reads few of them).</summary>
    /// <param name="table">The file's string table.</param>
    /// <param name="keys">The state's read keys as table indexes.</param>
    /// <param name="positions">The named keys' positions among the read keys.</param>
    /// <param name="labels">Their labels as table indexes.</param>
    internal sealed class TableNames(string[] table, int[] keys, int[] positions, int[] labels) : IReadOnlyDictionary<string, string>
    {
        private Dictionary<string, string>? _names;

        private Dictionary<string, string> Names
        {
            get
            {
                if (_names is null)
                {
                    var names = new Dictionary<string, string>(positions.Length, StringComparer.Ordinal);
                    for (var n = 0; n < positions.Length; n++)
                        names[table[keys[positions[n]]]] = table[labels[n]];
                    _names = names;
                }

                return _names;
            }
        }

        /// <inheritdoc/>
        public string this[string key] => Names[key];

        /// <inheritdoc/>
        public IEnumerable<string> Keys => Names.Keys;

        /// <inheritdoc/>
        public IEnumerable<string> Values => Names.Values;

        /// <inheritdoc/>
        public int Count => positions.Length;

        /// <inheritdoc/>
        public bool ContainsKey(string key) => Names.ContainsKey(key);

        /// <inheritdoc/>
        public bool TryGetValue(string key, [System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out string value) => Names.TryGetValue(key, out value);

        /// <inheritdoc/>
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => Names.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private static int Count(BinaryReader reader, int limit)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > limit)
            throw new FormatException("Invalid count.");
        return count;
    }
}
