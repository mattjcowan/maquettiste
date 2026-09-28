using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;

namespace Maquettiste.Engine.SchemaDiff;

/// <summary>
/// Turns a resolved database into a <see cref="PhysicalSnapshot"/> (engine-design.md section 14). Tables, columns, views and
/// sequences keep their section 7.3 keys (<see cref="RTable.Key"/>, <see cref="RColumn.Key"/>, and the <see cref="RObject.Id"/> of
/// views and sequences, which is the view's ULID or the synthesized sequence key).
/// </summary>
/// <remarks>
/// The resolved constraint and index types carry no key, so their snapshot keys are derived from every property that tells two
/// objects of a table apart and stays stable across a rename (never from the name):
/// <list type="bullet">
/// <item><description>the primary key is <c>pk</c>;</description></item>
/// <item><description>a unique constraint is <c>uq:</c> + its column keys;</description></item>
/// <item><description>a foreign key is <c>fk:</c> + its column keys + <c>-&gt;</c> + the referenced table key + the referenced
/// column keys in parentheses;</description></item>
/// <item><description>an index is <c>ix:</c> + its column keys (<c> desc</c> for descending), followed only when they differ from
/// the default by <c>;unique</c>, <c>;using=&lt;method&gt;</c>, <c>;include=&lt;column keys&gt;</c> and
/// <c>;where=&lt;16 hex of the SHA-256 of the predicate&gt;</c>;</description></item>
/// <item><description>a check is <c>ck:</c> + the first 16 hex characters of the SHA-256 of its expression.</description></item>
/// </list>
/// Column keys are joined with <c>,</c>. Only objects whose definitions are otherwise identical can derive the same key; those
/// are ordered by their remaining properties (a foreign key's actions), then by name (ordinal), and the second and later get
/// <c>#2</c>, <c>#3</c>… So renaming a constraint or index is <see cref="ChangeKind.Renamed"/>, and two partial indexes on the
/// same columns keep their own keys when one is renamed. Changing a key-forming property (columns, a foreign key's target, an
/// index's uniqueness, method, included columns or predicate, a check's expression) is a drop plus an add.
/// </remarks>
internal static class SnapshotCapture
{
    /// <summary>Captures a database.</summary>
    /// <param name="database">The resolved database.</param>
    /// <param name="revision">The revision to stamp.</param>
    /// <returns>The snapshot, every list sorted by key (columns by position).</returns>
    /// <exception cref="ArgumentException">The database's dialect is empty or not one of the snapshot dialects (validation
    /// requires one, so this is a broken invariant, never guessed).</exception>
    public static PhysicalSnapshot Capture(RDatabase database, int revision)
    {
        ArgumentNullException.ThrowIfNull(database);
        return new PhysicalSnapshot
        {
            Database = database.Id,
            Name = database.Name,
            Dialect = ParseDialect(database),
            Revision = revision,
            Tables = [.. database.Tables.Select(CaptureTable).OrderBy(t => t.Key, StringComparer.Ordinal)],
            Views = [.. database.Views.Select(v => new SnapshotView { Key = v.Id, Name = v.Name, Schema = v.Schema, Body = v.Body })
                .OrderBy(v => v.Key, StringComparer.Ordinal)],
            Sequences = [.. database.Sequences.Select(CaptureSequence).OrderBy(s => s.Key, StringComparer.Ordinal)],
        };
    }

    /// <summary>Sorts every keyed list of a snapshot by key (columns keep their order), as the canonical file holds it.</summary>
    /// <param name="snapshot">The snapshot.</param>
    /// <returns>The sorted snapshot.</returns>
    public static PhysicalSnapshot Sorted(PhysicalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot with
        {
            Tables = [.. snapshot.Tables.Select(t => t with
            {
                Uniques = [.. t.Uniques.OrderBy(c => c.Key, StringComparer.Ordinal)],
                ForeignKeys = [.. t.ForeignKeys.OrderBy(c => c.Key, StringComparer.Ordinal)],
                Checks = [.. t.Checks.OrderBy(c => c.Key, StringComparer.Ordinal)],
                Indexes = [.. t.Indexes.OrderBy(c => c.Key, StringComparer.Ordinal)],
            }).OrderBy(t => t.Key, StringComparer.Ordinal)],
            Views = [.. snapshot.Views.OrderBy(v => v.Key, StringComparer.Ordinal)],
            Sequences = [.. snapshot.Sequences.OrderBy(s => s.Key, StringComparer.Ordinal)],
        };
    }

    /// <summary>The database's dialect; an empty or unknown name throws instead of falling back to a default.</summary>
    private static Dialect ParseDialect(RDatabase database)
    {
        foreach (var dialect in Enum.GetValues<Dialect>())
        {
            if (string.Equals(PlainValues.Kebab(dialect), database.Dialect, StringComparison.Ordinal))
                return dialect;
        }

        throw new ArgumentException(
            $"Database '{database.Name}' has dialect '{database.Dialect}', which is not one of the snapshot dialects; it cannot be captured.",
            nameof(database));
    }

    private static SnapshotTable CaptureTable(RTable table)
    {
        var columns = table.Columns.OrderBy(c => c.Position).ThenBy(c => c.Key, StringComparer.Ordinal).Select(CaptureColumn).ToList();
        SnapshotConstraint? primaryKey = table.PrimaryKey is { } pk
            ? new SnapshotConstraint { Key = "pk", Name = pk.Name, Columns = Keys(pk.Columns) }
            : null;
        var uniques = AssignKeys(table.Uniques, u => "uq:" + JoinKeys(u.Columns), _ => "", u => u.Name,
            (u, key) => new SnapshotConstraint { Key = key, Name = u.Name, Columns = Keys(u.Columns) });
        var foreignKeys = AssignKeys(table.ForeignKeys, ForeignKeyKey, f => (f.OnDelete ?? "") + "|" + (f.OnUpdate ?? ""), f => f.Name,
            (f, key) => new SnapshotForeignKey
            {
                Key = key,
                Name = f.Name,
                Columns = Keys(f.Columns),
                ReferencedTable = f.ReferencedTable?.Key ?? "",
                ReferencedColumns = Keys(f.ReferencedColumns),
                OnDelete = PlainValues.Parse(f.OnDelete, ReferentialAction.NoAction),
                OnUpdate = PlainValues.Parse(f.OnUpdate, ReferentialAction.NoAction),
            });
        var checks = AssignKeys(table.Checks, c => "ck:" + ContentHash.Of(c.Expression)[..16], _ => "", c => c.Name,
            (c, key) => new SnapshotCheck { Key = key, Name = c.Name, Expression = c.Expression });
        var indexes = AssignKeys(table.Indexes, IndexKey, _ => "", i => i.Name,
            (i, key) => new SnapshotIndex
            {
                Key = key,
                Name = i.Name,
                Columns = [.. i.Columns.Select(c => new IndexColumn { Column = c.Column.Key, Descending = c.Descending })],
                Include = Keys(i.Include),
                Where = i.Where,
                Unique = i.Unique,
                Method = PlainValues.Parse(i.Method, IndexMethod.Default),
            });
        return new SnapshotTable
        {
            Key = table.Key,
            Name = table.Name,
            Schema = table.Schema,
            Columns = columns,
            PrimaryKey = primaryKey,
            Uniques = uniques,
            ForeignKeys = foreignKeys,
            Checks = checks,
            Indexes = indexes,
            Comment = table.Comment,
        };
    }

    private static SnapshotColumn CaptureColumn(RColumn column) => new()
    {
        Key = column.Key,
        Name = column.Name,
        Type = column.Type,
        Length = column.Length,
        Precision = column.Precision,
        Scale = column.Scale,
        NativeType = string.IsNullOrEmpty(column.NativeType) ? null : column.NativeType,
        Nullable = column.Nullable,
        Default = PlainValues.ToElement(column.Default),
        DefaultSql = column.DefaultSql,
        Identity = column.Identity,
        Sequence = column.Sequence?.Id,
        Computed = column.Computed,
        ComputedStored = column.ComputedStored,
        Collation = column.Collation,
        Comment = column.Comment,
    };

    private static SnapshotSequence CaptureSequence(RSequence sequence) => new()
    {
        Key = sequence.Id,
        Name = sequence.Name,
        Schema = sequence.Schema,
        Type = sequence.Type,
        Start = sequence.Start,
        Increment = sequence.Increment,
        Min = sequence.Min,
        Max = sequence.Max,
        Cycle = sequence.Cycle,
        Cache = sequence.Cache,
    };

    private static IReadOnlyList<string> Keys(IEnumerable<RColumn> columns) => [.. columns.Select(c => c.Key)];

    private static string JoinKeys(IEnumerable<RColumn> columns) => string.Join(',', columns.Select(c => c.Key));

    /// <summary>A foreign key's derived key: its columns and what they reference.</summary>
    private static string ForeignKeyKey(RForeignKey fk) =>
        "fk:" + JoinKeys(fk.Columns) + "->" + (fk.ReferencedTable?.Key ?? "") + "(" + JoinKeys(fk.ReferencedColumns) + ")";

    /// <summary>An index's derived key: its columns, then every non-default property that sets it apart from another index.</summary>
    private static string IndexKey(RIndex index)
    {
        var key = new System.Text.StringBuilder("ix:");
        key.Append(string.Join(',', index.Columns.Select(c => c.Descending ? c.Column.Key + " desc" : c.Column.Key)));
        if (index.Unique)
            key.Append(";unique");
        var method = PlainValues.Parse(index.Method, IndexMethod.Default);
        if (method != IndexMethod.Default)
            key.Append(";using=").Append(PlainValues.Kebab(method));
        if (index.Include.Count > 0)
            key.Append(";include=").Append(JoinKeys(index.Include));
        if (!string.IsNullOrEmpty(index.Where))
            key.Append(";where=").Append(ContentHash.Of(index.Where)[..16]);
        return key.ToString();
    }

    /// <summary>
    /// Derives keys, disambiguates repeats with <c>#n</c> in the order of their remaining properties, then name, then list
    /// position, and sorts by key.
    /// </summary>
    private static IReadOnlyList<TOut> AssignKeys<TIn, TOut>(IReadOnlyList<TIn> items, Func<TIn, string> baseKey, Func<TIn, string> rest,
        Func<TIn, string> name, Func<TIn, string, TOut> build)
    {
        var keyed = new List<(string Key, TOut Value)>(items.Count);
        foreach (var group in items.Select((item, index) => (Item: item, Index: index, Base: baseKey(item)))
                     .GroupBy(x => x.Base, StringComparer.Ordinal))
        {
            var n = 0;
            foreach (var x in group.OrderBy(x => rest(x.Item), StringComparer.Ordinal).ThenBy(x => name(x.Item), StringComparer.Ordinal).ThenBy(x => x.Index))
            {
                n++;
                var key = n == 1 ? x.Base : x.Base + "#" + n.ToString(System.Globalization.CultureInfo.InvariantCulture);
                keyed.Add((key, build(x.Item, key)));
            }
        }

        return [.. keyed.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Value)];
    }
}
