using System.Collections.Concurrent;

namespace Maquettiste.Engine.Model;

/// <summary>
/// One snapshot's shape hashes (<see cref="ColumnShapeHash.Of"/>) by column entry. Each value costs two SHA-256 digests, and a model at
/// benchmark scale has well over a hundred thousand bound columns, so a resolve after an edit reads the previous snapshot's values for the
/// entries the edit kept: an unchanged file keeps its entry instances from one snapshot to the next, and an entry is immutable, so a value
/// found by instance is the value. Only the previous snapshot's values are read: a new cache cuts its predecessor's own link, so the values
/// of entries an edit replaced live one snapshot at most.
/// </summary>
internal sealed class ColumnShapeCache
{
    private readonly ConcurrentDictionary<Column, string> _shapes = new(ReferenceEqualityComparer.Instance);
    private ColumnShapeCache? _previous;

    /// <summary>Creates the cache of a snapshot.</summary>
    /// <param name="previous">The previous snapshot's cache in the same store, or none.</param>
    public ColumnShapeCache(ColumnShapeCache? previous)
    {
        if (previous is not null)
            Volatile.Write(ref previous._previous, null);
        _previous = previous;
    }

    /// <summary>The shape hash of a column entry.</summary>
    /// <param name="column">A column entry of this snapshot (or one built from it, such as an inherited member with its redefinition).</param>
    /// <returns>The value <see cref="ColumnShapeHash.Of"/> gives.</returns>
    public string Of(Column column)
    {
        if (_shapes.TryGetValue(column, out var shape))
            return shape;
        shape = Volatile.Read(ref _previous) is { } previous && previous._shapes.TryGetValue(column, out var known) ? known : ColumnShapeHash.Of(column);
        return _shapes.GetOrAdd(column, shape);
    }
}
