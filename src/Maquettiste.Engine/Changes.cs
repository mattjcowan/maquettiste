using System.Text.Json;
using System.Text.Json.Serialization;

namespace Maquettiste.Engine;

/// <summary>What caused a model change.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ChangeSource>))]
public enum ChangeSource
{
    /// <summary>A save from the editor: <c>editor</c>.</summary>
    [JsonStringEnumMemberName("editor")] Editor,

    /// <summary>A change seen on disk (git, a text editor): <c>disk</c>.</summary>
    [JsonStringEnumMemberName("disk")] Disk,

    /// <summary>A CLI command: <c>cli</c>.</summary>
    [JsonStringEnumMemberName("cli")] Cli,

    /// <summary>The engine itself: <c>engine</c>.</summary>
    [JsonStringEnumMemberName("engine")] Engine,
}

/// <summary>One changed or added element.</summary>
/// <param name="Id">The element id.</param>
/// <param name="Kind">The kind name.</param>
/// <param name="Path">The repo-relative file path.</param>
/// <param name="Hash">The new file hash (ETag).</param>
public sealed record ElementChange(string Id, string Kind, string Path, string Hash);

/// <summary>What changed in the model (host-contracts requirements 17 and 20).</summary>
/// <param name="Changed">Changed and added elements.</param>
/// <param name="Deleted">Ids of deleted elements.</param>
/// <param name="Source">What caused the change.</param>
/// <param name="Truncated">Whether items were dropped to fit a size limit; the receiver must refetch.</param>
public sealed record ChangeSet(IReadOnlyList<ElementChange> Changed, IReadOnlyList<string> Deleted, ChangeSource Source, bool Truncated)
{
    private static readonly JsonSerializerOptions SizeOptions = CreateSizeOptions();

    /// <summary>Whether nothing changed.</summary>
    public bool IsEmpty => Changed.Count == 0 && Deleted.Count == 0;

    /// <summary>An empty change set.</summary>
    /// <param name="source">The source.</param>
    /// <returns>The change set.</returns>
    public static ChangeSet Empty(ChangeSource source) => new([], [], source, false);

    /// <summary>
    /// Returns a change set whose <see cref="JsonSerializerDefaults.Web"/> JSON fits in <paramref name="maxJsonBytes"/>, dropping
    /// trailing items and setting <see cref="Truncated"/> when it had to.
    /// </summary>
    /// <param name="maxJsonBytes">The size limit, for example 256 KB for a realtime payload.</param>
    /// <returns>This change set when it fits, else a truncated copy.</returns>
    public ChangeSet TruncateTo(int maxJsonBytes)
    {
        if (Size(this) <= maxJsonBytes)
            return this;

        var total = Changed.Count + Deleted.Count;
        int lo = 0, hi = total;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Size(Take(mid)) <= maxJsonBytes)
                lo = mid;
            else
                hi = mid - 1;
        }

        return Take(lo);
    }

    private ChangeSet Take(int count)
    {
        var changed = Changed.Take(Math.Min(count, Changed.Count)).ToArray();
        var deleted = Deleted.Take(Math.Max(0, count - Changed.Count)).ToArray();
        return new ChangeSet(changed, deleted, Source, true);
    }

    private static int Size(ChangeSet set) => JsonSerializer.SerializeToUtf8Bytes(set, SizeOptions).Length;

    private static JsonSerializerOptions CreateSizeOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
