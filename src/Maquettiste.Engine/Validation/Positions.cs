using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Json;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Validation;

/// <summary>
/// Fills <see cref="Diagnostics.Diagnostic.Line"/> and <see cref="Diagnostics.Diagnostic.Column"/> for diagnostics that carry a file
/// path and a JSON pointer. The bytes of a model file are read from disk when the file still has the snapshot's hash, else taken
/// from the snapshot's parsed JSON (canonical files start at offset 0, so the positions agree). A pointer that does not resolve
/// (a property that is absent, such as a missing <c>key</c>) falls back to its nearest resolving ancestor. Positions come from
/// <see cref="PointerLocator"/>, which has the contract of W1's <see cref="JsonPositionLocator"/> (the value's position, 1-based,
/// in characters); switch to that class once it is implemented.
/// One instance per validation run; not thread-safe.
/// </summary>
/// <param name="options">The engine options (repo and model roots).</param>
/// <param name="model">The snapshot.</param>
internal sealed class Positions(EngineOptions options, ModelSnapshot model)
{
    private readonly Dictionary<string, byte[]?> _bytes = new(StringComparer.Ordinal);
    private Dictionary<string, (string? Hash, JsonElement? Json)>? _hashes;

    /// <summary>Returns the position of a pointer in a file, or <see langword="null"/> when the file or pointer cannot be found.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="pointer">The JSON pointer.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The 1-based line and column.</returns>
    public async ValueTask<(int Line, int Column)?> LocateAsync(string path, string pointer, CancellationToken ct)
    {
        if (!_bytes.TryGetValue(path, out var bytes))
            _bytes[path] = bytes = await ReadAsync(path, ct).ConfigureAwait(false);
        if (bytes is null)
            return null;
        for (string? candidate = pointer; candidate is not null; candidate = Ptr.Parent(candidate))
        {
            if (Locate(bytes, candidate) is { } position)
                return position;
        }

        return null;
    }

    private static (int Line, int Column)? Locate(byte[] bytes, string pointer) => PointerLocator.Locate(bytes, pointer);

    private async Task<byte[]?> ReadAsync(string path, CancellationToken ct)
    {
        _hashes ??= BuildHashIndex();
        var (expectedHash, json) = _hashes.TryGetValue(path, out var known) ? known : (null, null);

        var full = FullPath(path);
        if (full is not null && File.Exists(full))
        {
            try
            {
                var disk = await File.ReadAllBytesAsync(full, ct).ConfigureAwait(false);
                if (expectedHash is null || ContentHash.Of(disk) == expectedHash)
                    return disk;
            }
            catch (IOException)
            {
                // Fall back to the snapshot's JSON below.
            }
            catch (UnauthorizedAccessException)
            {
                // Fall back to the snapshot's JSON below.
            }
        }

        return json is { } element ? System.Text.Encoding.UTF8.GetBytes(element.GetRawText()) : null;
    }

    private Dictionary<string, (string? Hash, JsonElement? Json)> BuildHashIndex()
    {
        var index = new Dictionary<string, (string? Hash, JsonElement? Json)>(StringComparer.Ordinal);
        foreach (var doc in model.Documents)
            index.TryAdd(doc.Path, (doc.Hash, doc.Json));
        foreach (var extension in model.Extensions)
            index.TryAdd(extension.Path, (extension.Hash, null));
        return index;
    }

    private string? FullPath(string path)
    {
        if (path.Length == 0 || Path.IsPathRooted(path) || string.IsNullOrEmpty(options.RepoRoot))
            return null;
        const string modelPrefix = ".maquettiste/";
        if (options.ModelRoot is { Length: > 0 } modelRoot && path.StartsWith(modelPrefix, StringComparison.Ordinal))
            return Path.Combine(modelRoot, path[modelPrefix.Length..].Replace('/', Path.DirectorySeparatorChar));
        return Path.Combine(options.RepoRoot, path.Replace('/', Path.DirectorySeparatorChar));
    }
}

/// <summary>
/// Maps a JSON pointer to the 1-based line and column of the value it names, with <see cref="Utf8JsonReader"/>. The column counts
/// characters (UTF-8 lead bytes), not bytes.
/// </summary>
internal static class PointerLocator
{
    private sealed class Frame(bool isArray)
    {
        public bool IsArray { get; } = isArray;

        public int Index { get; set; }

        public string? Property { get; set; }
    }

    /// <summary>Locates a pointer.</summary>
    /// <param name="utf8">The file bytes.</param>
    /// <param name="pointer">The pointer.</param>
    /// <returns>The position of the value, or <see langword="null"/> when the pointer does not resolve or the JSON is invalid.</returns>
    public static (int Line, int Column)? Locate(ReadOnlySpan<byte> utf8, string pointer)
    {
        var target = Ptr.Split(pointer);
        var stack = new List<Frame>();
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        stack[^1].Property = reader.GetString();
                        continue;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        stack.RemoveAt(stack.Count - 1);
                        AfterValue(stack);
                        continue;
                }

                if (Matches(stack, target))
                    return Position(utf8, (int)reader.TokenStartIndex);
                if (reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
                    stack.Add(new Frame(reader.TokenType == JsonTokenType.StartArray));
                else
                    AfterValue(stack);
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static void AfterValue(List<Frame> stack)
    {
        if (stack.Count > 0 && stack[^1].IsArray)
            stack[^1].Index++;
    }

    private static bool Matches(List<Frame> stack, string[] target)
    {
        if (stack.Count != target.Length)
            return false;
        for (var i = 0; i < stack.Count; i++)
        {
            var frame = stack[i];
            var segment = frame.IsArray ? frame.Index.ToString(CultureInfo.InvariantCulture) : frame.Property;
            if (!string.Equals(segment, target[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static (int Line, int Column) Position(ReadOnlySpan<byte> utf8, int offset)
    {
        var line = 1;
        var column = 1;
        for (var i = 0; i < offset && i < utf8.Length; i++)
        {
            var b = utf8[i];
            if (b == (byte)'\n')
            {
                line++;
                column = 1;
            }
            else if ((b & 0xC0) != 0x80)
            {
                column++;
            }
        }

        return (line, column);
    }
}
