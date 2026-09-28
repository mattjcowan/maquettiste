using Maquettiste.Engine;

namespace Maquettiste.Testing;

/// <summary>
/// A deterministic <see cref="IIdGenerator"/>: valid ULIDs whose time part comes from the seed and whose random part is a
/// counter, so ids sort in creation order and the same seed always yields the same sequence. Thread-safe.
/// </summary>
public sealed class SequentialIdGenerator(int seed = 1) : IIdGenerator
{
    /// <summary>The time part of every id: 2024-01-01T00:00:00Z plus the seed in milliseconds.</summary>
    private readonly long _timestamp = 1_704_067_200_000L + seed;
    private long _counter;

    /// <inheritdoc/>
    public string NewId() => IdFormat.Format(_timestamp, 0, (ulong)Interlocked.Increment(ref _counter));
}
