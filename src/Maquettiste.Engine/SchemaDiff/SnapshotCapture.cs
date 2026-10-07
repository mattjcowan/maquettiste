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
/// <item><description>an index is <c>ix:</c> + its column keys (an expression: <c>expr:</c> + 16 hex of its SHA-256; a space and the
/// operator class when it has one; <c> desc</c> for descending), followed only when they differ from
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
    /// <summary>Tables below this count are captured on the calling thread whatever the parallelism.</summary>
    private const int ParallelThreshold = 512;

    /// <summary>Captures a database.</summary>
    /// <param name="database">The resolved database.</param>
    /// <param name="revision">The revision to stamp.</param>
    /// <param name="parallelism">Threads for capturing tables (each table is captured independently into its own slot, so the result
    /// is the same for any value); 1 captures on the calling thread.</param>
    /// <param name="ct">Cancellation, observed between tables of a parallel capture.</param>
    /// <returns>The snapshot, every list sorted by key (columns by position).</returns>
    /// <exception cref="ArgumentException">The database's dialect is empty or not one of the snapshot dialects (validation
    /// requires one, so this is a broken invariant, never guessed).</exception>
    public static PhysicalSnapshot Capture(RDatabase database, int revision, int parallelism = 1, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        var dialect = ParseDialect(database);
        var source = database.Tables;
        var captured = new SnapshotTable[source.Count];
        if (parallelism > 1 && captured.Length >= ParallelThreshold)
            Parallel.For(0, captured.Length, new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = ct }, i => captured[i] = CaptureTable(source[i]));
        else
        {
            for (var i = 0; i < captured.Length; i++)
                captured[i] = CaptureTable(source[i]);
        }

        return new PhysicalSnapshot
        {
            Database = database.Id,
            Name = database.Name,
            Dialect = dialect,
            Revision = revision,
            Schemas = [.. database.Schemas.Where(s => s.IsDeclared).Select(s => new SnapshotSchema { Key = s.Id, Name = s.Name }).OrderBy(s => s.Key, StringComparer.Ordinal)],
            Tables = [.. captured.OrderBy(t => t.Key, StringComparer.Ordinal)],
            Views = [.. database.Views.Select(v => new SnapshotView
                {
                    Key = v.Id,
                    Name = v.Name,
                    Schema = v.Schema,
                    Body = v.Body,
                    Columns = v.ColumnList ? [.. v.Columns.Select(c => c.Name)] : [],
                    WithCheckOption = v.WithCheckOption,
                    Materialized = v.Materialized,
                    SecurityInvoker = v.SecurityInvoker,
                    SecurityBarrier = v.SecurityBarrier,
                    DependsOn = [.. v.DependsOn.OfType<RView>().Select(d => d.Id)],
                    Comment = v.Comment,
                })
                .OrderBy(v => v.Key, StringComparer.Ordinal)],
            Sequences = [.. database.Sequences.Select(CaptureSequence).OrderBy(s => s.Key, StringComparer.Ordinal)],
            Types = [.. database.Types.Select(CaptureType).OrderBy(s => s.Key, StringComparer.Ordinal)],
            Routines = [.. database.Routines.Select(CaptureRoutine).OrderBy(s => s.Key, StringComparer.Ordinal)],
            Objects = [.. database.Objects.Select(CaptureObject).OrderBy(s => s.Key, StringComparer.Ordinal)],
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
            Schemas = snapshot.Schemas is null ? null : [.. snapshot.Schemas.OrderBy(s => s.Key, StringComparer.Ordinal)],
            Tables = [.. snapshot.Tables.Select(t => t with
            {
                Uniques = [.. t.Uniques.OrderBy(c => c.Key, StringComparer.Ordinal)],
                ForeignKeys = [.. t.ForeignKeys.OrderBy(c => c.Key, StringComparer.Ordinal)],
                Checks = [.. t.Checks.OrderBy(c => c.Key, StringComparer.Ordinal)],
                Exclusions = [.. t.Exclusions.OrderBy(c => c.Key, StringComparer.Ordinal)],
                Partitions = [.. t.Partitions.OrderBy(c => c.Key, StringComparer.Ordinal)],
                Indexes = [.. t.Indexes.OrderBy(c => c.Key, StringComparer.Ordinal)],
            }).OrderBy(t => t.Key, StringComparer.Ordinal)],
            Views = [.. snapshot.Views.OrderBy(v => v.Key, StringComparer.Ordinal)],
            Sequences = [.. snapshot.Sequences.OrderBy(s => s.Key, StringComparer.Ordinal)],
            Types = [.. snapshot.Types.OrderBy(s => s.Key, StringComparer.Ordinal)],
            Routines = [.. snapshot.Routines.OrderBy(s => s.Key, StringComparer.Ordinal)],
            Objects = [.. snapshot.Objects.OrderBy(s => s.Key, StringComparer.Ordinal)],
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
            ? new SnapshotConstraint { Key = "pk", Name = pk.Name, Columns = Keys(pk.Columns), Clustered = pk.Clustered, WithoutOverlaps = pk.WithoutOverlaps }
            : null;
        var uniques = AssignKeys(table.Uniques, u => "uq:" + JoinKeys(u.Columns), _ => "", u => u.Name,
            (u, key) => new SnapshotConstraint { Key = key, Name = u.Name, Columns = Keys(u.Columns), NullsNotDistinct = u.NullsNotDistinct, WithoutOverlaps = u.WithoutOverlaps });
        var foreignKeys = AssignKeys(table.ForeignKeys, ForeignKeyKey, f => (f.OnDelete ?? "") + "|" + JoinKeys(f.OnDeleteColumns) + "|" + (f.OnUpdate ?? ""), f => f.Name,
            (f, key) => new SnapshotForeignKey
            {
                Key = key,
                Name = f.Name,
                Columns = Keys(f.Columns),
                ReferencedTable = f.ReferencedTable?.Key ?? "",
                ReferencedColumns = Keys(f.ReferencedColumns),
                Period = f.Period,
                OnDelete = PlainValues.Parse(f.OnDelete, ReferentialAction.NoAction),
                OnDeleteColumns = Keys(f.OnDeleteColumns),
                OnUpdate = PlainValues.Parse(f.OnUpdate, ReferentialAction.NoAction),
                Deferrable = PlainValues.Parse(f.Deferrable, Deferrability.NotDeferrable),
            });
        var exclusions = AssignKeys(table.Exclusions, x => "ex:" + ContentHash.Of(ExclusionDefinition(x))[..16], _ => "", x => x.Name,
            (x, key) => new SnapshotExclusion
            {
                Key = key,
                Name = x.Name,
                Method = x.Method,
                Elements = [.. x.Elements.Select(e => new SnapshotExclusionElement { Column = e.Column?.Key, Expression = e.Expression, OperatorClass = e.OperatorClass, Operator = e.Operator })],
                Where = x.Where,
                Deferrable = PlainValues.Parse(x.Deferrable, Deferrability.NotDeferrable),
            });
        var checks = AssignKeys(table.Checks, c => "ck:" + ContentHash.Of(c.Expression)[..16], _ => "", c => c.Name,
            (c, key) => new SnapshotCheck { Key = key, Name = c.Name, Expression = c.Expression });
        var indexes = AssignKeys(table.Indexes, IndexKey, _ => "", i => i.Name,
            (i, key) => new SnapshotIndex
            {
                Key = key,
                Name = i.Name,
                Columns = [.. i.Columns.Select(c => new SnapshotIndexColumn
                {
                    Column = c.Column?.Key, Expression = c.Column is null ? c.Expression : null, OperatorClass = c.OperatorClass, Descending = c.Descending, Length = c.Length,
                })],
                Include = Keys(i.Include),
                Where = i.Where,
                Unique = i.Unique,
                Method = PlainValues.Parse(i.Method, IndexMethod.Default),
                Storage = CaptureStorage(i.Storage),
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
            Exclusions = exclusions,
            PartitionBy = table.PartitionBy is { } by ? new SnapshotPartitionBy { Strategy = PlainValues.Parse(by.Strategy, PartitionStrategy.Range), Columns = Keys(by.Columns) } : null,
            Partitions = [.. table.Partitions.Select(p => new SnapshotPartition { Key = p.Id, Name = p.Name, Bounds = p.Bounds, Default = p.IsDefault }).OrderBy(p => p.Key, StringComparer.Ordinal)],
            Storage = CaptureStorage(table.Storage),
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
        DefaultName = column.DefaultName,
        Identity = column.Identity,
        IdentitySeed = column.Identity ? column.IdentitySeed : null,
        IdentityIncrement = column.Identity ? column.IdentityIncrement : null,
        IdentityAlways = column.Identity && column.IdentityAlways,
        Sequence = column.Sequence?.Id,
        Computed = column.Computed,
        ComputedStored = column.ComputedStored,
        Collation = column.Collation,
        Comment = column.Comment,
        ReferenceType = column.Type == "reference" ? column.ReferenceType?.Id : null,
        Strategy = column.Type == "reference" ? column.Strategy : null,
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

    /// <summary>
    /// A database type's definition text: its definition for the dialect, or its structured form (base and facets, check, members,
    /// fields, subtype). The name is not part of it, so a rename is a rename and not a change of definition; what columns write for the
    /// type reaches the diff through their native types.
    /// </summary>
    private static SnapshotDefinition CaptureType(RDatabaseType type)
    {
        var text = new System.Text.StringBuilder();
        if (type.Definition is { } definition)
            text.Append("definition: ").Append(definition).Append('\n');
        else
        {
            if (type.BaseNativeType is { } baseNative)
                text.Append("base: ").Append(baseNative).Append('\n');
            if (type.Check is { } check)
                text.Append("check: ").Append(check).Append('\n');
            foreach (var member in type.Members)
                text.Append("member: ").Append(member).Append('\n');
            foreach (var field in type.Fields)
                text.Append("field: ").Append(field.Name).Append(' ').Append(field.NativeType).Append('\n');
            if (type.SubtypeNativeType is { } subtype)
                text.Append("subtype: ").Append(subtype).Append('\n');
        }

        return new SnapshotDefinition { Key = type.Id, Name = type.Name, Schema = type.Schema, Kind = type.TypeKind, Definition = text.ToString() };
    }

    /// <summary>A routine's definition text: its signature (parameters, result, language, attributes) and its body for the dialect.</summary>
    private static SnapshotDefinition CaptureRoutine(RRoutine routine)
    {
        var text = new System.Text.StringBuilder();
        foreach (var p in routine.Parameters)
            text.Append("parameter: ").Append(p.Mode).Append(' ').Append(p.Name).Append(' ').Append(p.NativeType).Append(p.Default is null ? "" : " = " + p.Default).Append('\n');
        if (routine.Returns is { } returns)
        {
            if (returns.Table is { } table)
            {
                foreach (var c in table)
                    text.Append("returns column: ").Append(c.Name).Append(' ').Append(c.NativeType).Append(c.Nullable ? "" : " not null").Append('\n');
            }
            else
            {
                text.Append("returns: ").Append(returns.NativeType).Append('\n');
            }
        }

        text.Append("language: ").Append(routine.Language).Append('\n');
        text.Append("deterministic: ").Append(routine.Deterministic ? "true" : "false").Append('\n');
        // Only a volatility other than the one deterministic implies, and settings when there are some: a routine without them keeps
        // the definition text (and the snapshot) it had before they existed.
        if (routine.Volatility != (routine.Deterministic ? "immutable" : "volatile"))
            text.Append("volatility: ").Append(routine.Volatility).Append('\n');
        text.Append("security: ").Append(routine.Security).Append('\n');
        foreach (var setting in routine.Settings)
            text.Append("set: ").Append(setting.Name).Append(" = ").Append(setting.Value).Append('\n');
        text.Append("body: ").Append(routine.Body);
        return new SnapshotDefinition { Key = routine.Id, Name = routine.Name, Schema = routine.Schema, Kind = routine.RoutineKind, Definition = text.ToString() };
    }

    /// <summary>A SQL object's definition text: its phase and its statements for the dialect.</summary>
    private static SnapshotDefinition CaptureObject(RSqlObject obj) => new()
    {
        Key = obj.Id,
        Name = obj.Name,
        Schema = obj.Schema,
        Kind = obj.ObjectKind,
        Definition = "phase: " + obj.Phase + "\nbody: " + obj.Body,
    };

    /// <summary>What sets an exclusion constraint apart (its key's source): method, each element and operator, predicate.</summary>
    private static string ExclusionDefinition(RExclusion x) =>
        x.Method + "(" + string.Join(", ", x.Elements.Select(e => (e.Column?.Key ?? "(" + e.Expression + ")") + (e.OperatorClass is { } oc ? " " + oc : "") + " WITH " + e.Operator)) + ")"
        + (x.Where is { } where ? " WHERE " + where : "");

    private static IReadOnlyList<SnapshotStorageParameter> CaptureStorage(IReadOnlyList<RStorageParameter> storage) =>
        storage.Count == 0 ? [] : [.. storage.Select(p => new SnapshotStorageParameter { Name = p.Name, Value = p.Value })];

    private static IReadOnlyList<string> Keys(IEnumerable<RColumn> columns) => [.. columns.Select(c => c.Key)];

    private static string JoinKeys(IEnumerable<RColumn> columns) => string.Join(',', columns.Select(c => c.Key));

    /// <summary>A foreign key's derived key: its columns and what they reference.</summary>
    private static string ForeignKeyKey(RForeignKey fk) =>
        "fk:" + JoinKeys(fk.Columns) + "->" + (fk.ReferencedTable?.Key ?? "") + "(" + JoinKeys(fk.ReferencedColumns) + ")";

    /// <summary>An index's derived key: its columns, then every non-default property that sets it apart from another index.</summary>
    private static string IndexKey(RIndex index)
    {
        var key = new System.Text.StringBuilder("ix:");
        key.Append(string.Join(',', index.Columns.Select(c => IndexColumnKey(c.Column?.Key, c.Expression)
            + (c.OperatorClass is { } opclass ? " " + opclass : "") + (c.Descending ? " desc" : ""))));
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

    /// <summary>The key part of an index column: its column key, or <c>expr:</c> and 16 hex of the SHA-256 of its expression.</summary>
    internal static string IndexColumnKey(string? column, string? expression) => column ?? "expr:" + ContentHash.Of(expression ?? "")[..16];

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
