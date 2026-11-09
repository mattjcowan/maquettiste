using System.Collections.Immutable;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The rows a database receives (2026-10-07, table seeds; <see cref="RDatabase.SeedTables"/>): the rows of the reference types bound to
/// its table files (2026-10-11, source <c>list</c>, first), the seeds of its table files, and the
/// entity and relation seeds that reach one of its tables, through the entity's binding (the field map, the binding's constants, the
/// write table) or, for an entity the database projects, through its mapping (the attribute columns, the foreign keys of its to-one
/// ends, the discriminator), and a relation seed through its junction table. Cells are in database terms: an enum member by the
/// column's storage (its code or name in a text column, else its value), a to-one end as the referenced row's key, a stable id column
/// as the id derived from the table and the row key (next-phase-2-design.md section 3.4) unless the row's source writes it, and the
/// columns of a table seed's foreign-key entry as the values of the row its natural key names (section 3.4, decisions 1 to 3), which
/// <see cref="RDataRow.Refs"/> also carries. Tables come in foreign key order, referenced tables first; a cycle among seeded tables
/// keeps their name order and marks them. Within a table, a row that another of its rows names through the table's own foreign key,
/// by its values or by an entry's natural key, comes first, across the table's seeds. Built on first read per database, so a run
/// whose templates never read seed tables pays nothing; <see cref="RDatabase.DataRowsOf"/> builds one seed's rows with the same
/// builder (<see cref="DataBuild"/>).
/// </summary>
internal sealed partial class ResolveRun
{
    private static void PrepareSeedTables(RDatabase database, IReadOnlyList<RSeed> seeds, Dictionary<string, RSeedRow> seedRowsById, SeedOrdering ordering)
    {
        database.SeedTablesFactory = () => new DataBuild(database, seeds, seedRowsById, ordering).SeedTables();
        database.DataRowsFactory = seed => new DataBuild(database, seeds, seedRowsById, ordering).RowsOf(seed);
        database.SeedHashFactory = SeedHashOf;
    }

    private sealed class Pending(RTable table)
    {
        public RTable Table { get; } = table;
        public List<RSeed> Seeds { get; } = [];
        public List<RDataRow> Rows { get; } = [];
        public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// One build of a database's rows in database terms, for <see cref="RDatabase.SeedTables"/> or one seed
    /// (<see cref="RDatabase.DataRowsOf"/>). A seed row becomes a data row on first use; its values are computed on demand per row and
    /// column, recursively through the rows foreign-key entries name, and memoized for the build, so the result does not depend on the
    /// order tables or rows are built in: a column's value is the seed's cell, else the referenced row's value of the column its
    /// foreign-key entry references, else, for a stable id column, the id of the table and the row key in database terms. Every step
    /// keeps null for null, so a value that depends on itself through a cycle of lookups stays null wherever the walk starts. Natural
    /// keys are matched in seed terms (<see cref="SeedOrdering.Holders"/>), so a lookup reads only the seeds of the table it names.
    /// Not thread-safe: each read builds its own.
    /// </summary>
    private sealed class DataBuild(RDatabase database, IReadOnlyList<RSeed> seeds, Dictionary<string, RSeedRow> seedRowsById, SeedOrdering ordering)
    {
        private readonly HashSet<RTable> _tables = new(database.Tables, ReferenceEqualityComparer.Instance);
        private readonly Dictionary<RSeed, Source?> _sources = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<RSeedRow, Work?> _works = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<RDataRow, Work> _byRow = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<RTable, Facts> _facts = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<RSeed, IReadOnlyList<string>> _lookupKeys = new(ReferenceEqualityComparer.Instance);

        /// <summary>
        /// How a seed's rows reach a table of the database: the table, the source kind, the rows in the order the table takes them, their
        /// cells in database terms, a table seed's foreign-key entries, and the keys the rows depend on besides the seed's.
        /// </summary>
        private sealed record Source(RTable Table, string Kind, IReadOnlyList<RSeedRow> Rows, Func<RSeedRow, IReadOnlyDictionary<string, object?>> Cells,
            IReadOnlyList<RSeedColumn> Entries, IReadOnlyList<string> Extra);

        /// <summary>A table's row key, its stable id columns, and whether that key can derive their values.</summary>
        private sealed record Facts(IReadOnlyList<RColumn> Key, IReadOnlyList<RColumn> Stable, bool Derives);

        /// <summary>One data row while it is built: its cells, the values computed so far and the rows its entries name.</summary>
        private sealed class Work(RSeedRow seedRow, RDataRow row, Source source, IReadOnlyDictionary<string, object?> cells)
        {
            public RSeedRow SeedRow { get; } = seedRow;
            public RDataRow Row { get; } = row;
            public Source Source { get; } = source;
            public IReadOnlyDictionary<string, object?> Cells { get; } = cells;
            public Dictionary<string, object?> Values { get; } = new(StringComparer.Ordinal);
            public HashSet<string> Busy { get; } = new(StringComparer.Ordinal);
            public Dictionary<string, Work?> Targets { get; } = new(StringComparer.Ordinal);
            public int State { get; set; } // 0 new, 1 completing, 2 done
        }

        /// <summary>Every row the database receives, per table in foreign key order (<see cref="RDatabase.SeedTables"/>).</summary>
        public RList<RSeedTable> SeedTables()
        {
            var pending = new Dictionary<RTable, Pending>(ReferenceEqualityComparer.Instance);
            var works = new List<Work>();

            // The rows of the reference types bound to a table of the database come first (2026-10-11), then the table seeds, in merge
            // order (the seeds come in SeedOrder: package, name, id), then the entity and relation seeds that reach a table.
            foreach (var kinds in (string[][])[["list"], ["table"], ["entity", "relation"]])
            {
                foreach (var seed in seeds)
                {
                    if (SourceOf(seed) is not { } source || !kinds.Contains(source.Kind, StringComparer.Ordinal))
                        continue;
                    if (!pending.TryGetValue(source.Table, out var p))
                        pending[source.Table] = p = new Pending(source.Table);
                    if (!p.Seeds.Contains(seed))
                        p.Seeds.Add(seed);
                    p.Dependencies.UnionWith(source.Extra);
                    foreach (var row in source.Rows)
                    {
                        var work = WorkOf(row)!;
                        p.Rows.Add(work.Row);
                        works.Add(work);
                    }
                }
            }

            foreach (var work in works)
                Complete(work);

            var ordered = Order(pending.Values.ToList());
            var result = new List<RSeedTable>(ordered.Count);
            foreach (var (p, cycle) in ordered)
            {
                OrderBySelfReferences(p);
                var used = new HashSet<string>(p.Rows.SelectMany(r => r.Values.Keys), StringComparer.Ordinal);
                p.Dependencies.UnionWith(p.Table.Dependencies);
                foreach (var seed in p.Seeds)
                    p.Dependencies.UnionWith(LookupKeys(seed));
                result.Add(new RSeedTable
                {
                    Id = p.Table.Key,
                    Table = p.Table,
                    KeyColumns = FactsOf(p.Table).Key,
                    Columns = [.. p.Table.Columns.Where(c => used.Contains(c.Name))],
                    Seeds = p.Seeds,
                    Rows = p.Rows,
                    InCycle = cycle,
                    Dependencies = [.. p.Dependencies.Order(StringComparer.Ordinal)],
                });
            }

            return new RList<RSeedTable>(result, ["k:seed", "k:table", "k:entity"]);
        }

        /// <summary>One seed's rows in this database, in the seed's own order; empty when they reach none of its tables.</summary>
        public IReadOnlyList<RDataRow> RowsOf(RSeed seed)
        {
            if (SourceOf(seed) is not { } source)
                return [];
            var works = source.Rows.Select(row => WorkOf(row)!).ToList();
            foreach (var work in works)
                Complete(work);
            return [.. works.Select(w => w.Row)];
        }

        private Source? SourceOf(RSeed seed)
        {
            if (_sources.TryGetValue(seed, out var known))
                return known;
            Source? source = null;
            switch (seed.Target)
            {
                case RReferenceType type when type.Bindings.TryGetValue(database.Name, out var binding) && _tables.Contains(binding.Table):
                    source = new Source(binding.Table, "list", seed.OrderedRows, row => ThroughReferenceBinding(seed, row, binding), [], []);
                    break;
                case RTable table when _tables.Contains(table):
                    source = new Source(table, "table", seed.Rows,
                        row => Cells(seed.Columns.Where(c => c.Column is not null).Select(c => (c.Column!, row.Values.GetValueOrDefault(c.Name)))),
                        [.. seed.Columns.Where(c => c.ForeignKey is not null)], []);
                    break;
                case REntity entity when entity.Bindings.TryGetValue(database.Name, out var binding):
                    if (binding.Writes && (binding.WriteTable ?? binding.SourceTable) is { } written && _tables.Contains(written))
                        source = new Source(written, "entity", seed.OrderedRows, row => ThroughBinding(seed, row, binding, seedRowsById), [], entity.Dependencies);
                    break;
                case REntity entity when entity.Mappings.TryGetValue(database.Name, out var mapping) && _tables.Contains(mapping.Table):
                    source = new Source(mapping.Table, "entity", seed.OrderedRows, row => ThroughMapping(seed, row, mapping, seedRowsById), [], []);
                    break;
                case RRelation relation when relation.Mappings.TryGetValue(database.Name, out var link) && link.JunctionTable is { } junction
                    && _tables.Contains(junction):
                    source = new Source(junction, "relation", seed.OrderedRows, row => ThroughJunction(seed, row, junction, seedRowsById), [], []);
                    break;
            }

            return _sources[seed] = source;
        }

        /// <summary>The data row of a seed row in this database, created on first use; <see langword="null"/> when it reaches none of its tables.</summary>
        private Work? WorkOf(RSeedRow seedRow)
        {
            if (_works.TryGetValue(seedRow, out var known))
                return known;
            Work? work = null;
            if (SourceOf(seedRow.Seed) is { } source)
            {
                var row = new RDataRow { Id = seedRow.Id, Seed = seedRow.Seed, Source = source.Kind, Dependencies = LookupKeys(seedRow.Seed) };
                work = new Work(seedRow, row, source, source.Cells(seedRow));
                _byRow[row] = work;
            }

            return _works[seedRow] = work;
        }

        private Facts FactsOf(RTable table)
        {
            if (_facts.TryGetValue(table, out var facts))
                return facts;
            var key = ordering.NaturalKeyOf(table);
            // A stable id on a column of another type or one the database fills is MQ7124; such a column gets no value.
            var stable = table.Columns.Where(c => c is { StableId: true, Type: "uuid", Identity: false, Sequence: null, Computed: null }).ToList();
            var derives = stable.Count > 0 && key.Count > 0 && !key.Any(c => c.StableId) && Maquettiste.Engine.Model.StableIds.IsNamespace(table.Id);
            return _facts[table] = new Facts(key, stable, derives);
        }

        /// <summary>
        /// Sets a row's values, key and refs (once; a row met again while it completes is left to finish), then completes the rows its
        /// entries name, so every <see cref="RDataRef.Row"/> has its values.
        /// </summary>
        private void Complete(Work work)
        {
            if (work.State != 0)
                return;
            work.State = 1;
            var table = work.Source.Table;
            var facts = FactsOf(table);
            var values = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
            values.AddRange(work.Cells);
            foreach (var entry in work.Source.Entries)
            {
                foreach (var column in entry.ForeignKey!.Columns)
                {
                    if (!values.ContainsKey(column.Name))
                        values[column.Name] = Value(work, column);
                }
            }

            foreach (var column in facts.Stable)
            {
                if (!values.ContainsKey(column.Name) && Value(work, column) is { } id)
                    values[column.Name] = id;
            }

            var row = work.Row;
            row.Values = values.ToImmutable();
            row.Key = [.. facts.Key.Select(c => row.Values.GetValueOrDefault(c.Name))];

            var refs = ImmutableSortedDictionary.CreateBuilder<string, RDataRef>(StringComparer.Ordinal);
            var targets = new List<Work>();
            foreach (var entry in work.Source.Entries)
            {
                var fk = entry.ForeignKey!;
                if (refs.ContainsKey(entry.Name) || work.SeedRow.Values.GetValueOrDefault(entry.Name) is not { } cell)
                    continue;
                var target = Referenced(work, entry);
                var key = ordering.NaturalKeyOf(fk.ReferencedTable);
                var unknown = new List<RColumn>();
                for (var i = 0; target is not null && i < fk.Columns.Count; i++)
                {
                    if (row.Values.GetValueOrDefault(fk.Columns[i].Name) is null && IsUnknown(target, fk.ReferencedColumns[i], 0))
                        unknown.Add(fk.Columns[i]);
                }

                refs[entry.Name] = new RDataRef
                {
                    Id = row.Id + "/" + entry.Name,
                    ForeignKey = fk,
                    Table = fk.ReferencedTable,
                    KeyColumns = key,
                    Key = key.Count != 1 && cell is ImmutableArray<object?> parts ? parts : [cell],
                    Row = target?.Row,
                    Unknown = unknown,
                    Dependencies = row.Dependencies,
                };
                if (target is not null)
                    targets.Add(target);
            }

            if (refs.Count > 0)
                row.Refs = refs.ToImmutable();
            work.State = 2;
            foreach (var target in targets)
                Complete(target);
        }

        /// <summary>
        /// A column's value in database terms (see <see cref="DataBuild"/>): the cell, else the value an entry's referenced row gives the
        /// referenced column, else a derived stable id; null when none, or when the value depends on itself.
        /// </summary>
        private object? Value(Work work, RColumn column)
        {
            var name = column.Name;
            if (work.Cells.TryGetValue(name, out var cell))
                return cell;
            if (work.Values.TryGetValue(name, out var known))
                return known;
            if (!work.Busy.Add(name))
                return null; // a cycle of lookups: the value depends on itself
            object? value = null;
            if (EntryOf(work, column) is ({ } entry, var at))
            {
                if (Referenced(work, entry) is { } target)
                    value = Value(target, entry.ForeignKey!.ReferencedColumns[at]);
            }
            else if (column.StableId && FactsOf(work.Source.Table) is { Derives: true } facts && facts.Stable.Contains(column))
            {
                var parts = new object?[facts.Key.Count];
                for (var i = 0; i < parts.Length; i++)
                    parts[i] = StableKeyPart(facts.Key[i], Value(work, facts.Key[i]));
                if (parts.All(v => v is not null))
                    value = Maquettiste.Engine.Model.StableIds.Of(work.Source.Table.Id, parts);
            }

            work.Busy.Remove(name);
            work.Values[name] = value;
            return value;
        }

        /// <summary>The foreign-key entry that fills a column of a table seed's row, with the column's position in the key.</summary>
        private static (RSeedColumn? Entry, int At) EntryOf(Work work, RColumn column)
        {
            foreach (var entry in work.Source.Entries)
            {
                var at = IndexOf(entry.ForeignKey!.Columns, column);
                if (at >= 0)
                    return (entry, at);
            }

            return (null, -1);
        }

        private static int IndexOf(IReadOnlyList<RColumn> columns, RColumn column)
        {
            for (var i = 0; i < columns.Count; i++)
            {
                if (ReferenceEquals(columns[i], column))
                    return i;
            }

            return -1;
        }

        /// <summary>
        /// The row an entry's cell names in the referenced table (decision 2): the first holder of the key in seed order whose seed is loaded
        /// with the row's somewhere, else the first holder (MQ7111 reports the environments); <see langword="null"/> when none holds it.
        /// </summary>
        private Work? Referenced(Work work, RSeedColumn entry)
        {
            if (work.Targets.TryGetValue(entry.Name, out var known))
                return known;
            var holders = ordering.Holders(entry.ForeignKey!.ReferencedTable, work.SeedRow.Values.GetValueOrDefault(entry.Name));
            var chosen = holders.FirstOrDefault(h => LoadedTogether(work.Row.Seed, h.Seed)) ?? holders.FirstOrDefault();
            var target = chosen is null ? null : WorkOf(chosen);
            work.Targets[entry.Name] = target;
            return target;
        }

        /// <summary>
        /// Whether the database fills a referenced row's value of a column (identity, sequence, computed, a default it does not give), or
        /// the row's own entry fills it from a row whose value the database fills: a pack then looks the value up.
        /// </summary>
        private bool IsUnknown(Work target, RColumn column, int depth)
        {
            if (depth > 64 || target.Cells.ContainsKey(column.Name) || Value(target, column) is not null)
                return false;
            if (EntryOf(target, column) is ({ } entry, var at))
                return Referenced(target, entry) is { } next && IsUnknown(next, entry.ForeignKey!.ReferencedColumns[at], depth + 1);
            if (column.StableId && FactsOf(target.Source.Table).Stable.Contains(column))
                return false;
            return column.Identity || column.Sequence is not null || column.Computed is not null || column.Default is not null || column.DefaultSql is not null;
        }

        /// <summary>
        /// The keys a seed's rows depend on: the seed's own, and for each foreign-key entry the referenced table's and those of every seed
        /// that puts rows in it, transitively, since a lookup reads them all.
        /// </summary>
        private IReadOnlyList<string> LookupKeys(RSeed seed)
        {
            if (_lookupKeys.TryGetValue(seed, out var known))
                return known;
            if (!seed.Columns.Any(c => c.ForeignKey is not null))
                return _lookupKeys[seed] = seed.Dependencies;
            var keys = new SortedSet<string>(seed.Dependencies, StringComparer.Ordinal);
            var visited = new HashSet<RSeed>(ReferenceEqualityComparer.Instance) { seed };
            var stack = new Stack<RSeed>([seed]);
            while (stack.Count > 0)
            {
                foreach (var entry in stack.Pop().Columns.Where(c => c.ForeignKey is not null))
                {
                    keys.UnionWith(entry.ForeignKey!.ReferencedTable.Dependencies);
                    foreach (var holder in ordering.HolderSeeds(entry.ForeignKey.ReferencedTable))
                    {
                        if (!visited.Add(holder))
                            continue;
                        keys.UnionWith(holder.Dependencies);
                        stack.Push(holder);
                    }
                }
            }

            return _lookupKeys[seed] = [.. keys];
        }

        /// <summary>
        /// A table's rows with the rows its own foreign keys name first. A seed's rows stay together (packs apply them seed by seed): the
        /// table's seeds come in the order their rows name each other, then each seed's rows in that order (Kahn's algorithm, ties by the
        /// order before; a cycle, which only nullable or deferred keys allow, is broken at its first remaining seed or row). A row names
        /// a parent by its foreign key's values, or, for a foreign-key entry, by the entry's natural key in seed terms (decision 2): every
        /// row of the table that holds the key, since seeds of different environments may hold the same one.
        /// </summary>
        private void OrderBySelfReferences(Pending p)
        {
            var rows = p.Rows;
            var keys = p.Table.ForeignKeys.Where(f => ReferenceEquals(f.ReferencedTable, p.Table) && f.Columns.Count > 0 && f.Columns.Count == f.ReferencedColumns.Count).ToList();
            if (keys.Count == 0 || rows.Count < 2)
                return;
            var seedOf = rows.Select(r => p.Seeds.IndexOf(r.Seed)).ToArray();
            var rowDependents = new List<int>?[rows.Count];
            var rowPending = new int[rows.Count];
            var seedDependents = new HashSet<int>?[p.Seeds.Count];
            var seedPending = new int[p.Seeds.Count];
            var any = false;
            Dictionary<RSeedRow, int>? positions = null;
            foreach (var fk in keys)
            {
                // Every row of a key: seeds of different environments may hold the same one, and each is a parent where it loads.
                Dictionary<string, List<int>>? byKey = null;
                for (var i = 0; i < rows.Count; i++)
                {
                    var work = _byRow[rows[i]];
                    IEnumerable<int> parents;
                    if (work.Source.Entries.FirstOrDefault(e => ReferenceEquals(e.ForeignKey, fk)) is { } entry)
                    {
                        positions ??= Positions(rows);
                        parents = ordering.Holders(p.Table, work.SeedRow.Values.GetValueOrDefault(entry.Name))
                            .Select(h => positions.TryGetValue(h, out var at) ? at : -1).Where(at => at >= 0);
                    }
                    else
                    {
                        byKey ??= ByKey(rows, fk.ReferencedColumns);
                        if (KeyOfCells(rows[i].Values, fk.Columns) is not { } key || !byKey.TryGetValue(key, out var list))
                            continue;
                        parents = list;
                    }

                    foreach (var parent in parents)
                    {
                        if (parent == i || !LoadedTogether(rows[parent].Seed, rows[i].Seed))
                            continue;
                        any = true;
                        if (seedOf[parent] == seedOf[i])
                        {
                            (rowDependents[parent] ??= []).Add(i);
                            rowPending[i]++;
                        }
                        else if ((seedDependents[seedOf[parent]] ??= []).Add(seedOf[i]))
                        {
                            seedPending[seedOf[i]]++;
                        }
                    }
                }
            }

            if (!any)
                return;
            var seedOrder = Kahn(p.Seeds.Count, seedDependents, seedPending);
            var ordered = new List<RDataRow>(rows.Count);
            foreach (var s in seedOrder)
            {
                var mine = Enumerable.Range(0, rows.Count).Where(i => seedOf[i] == s).ToList();
                var at = new Dictionary<int, int>();
                for (var k = 0; k < mine.Count; k++)
                    at[mine[k]] = k;
                var dependents = new List<int>?[mine.Count];
                var pending = new int[mine.Count];
                for (var k = 0; k < mine.Count; k++)
                {
                    foreach (var dependent in rowDependents[mine[k]] ?? [])
                    {
                        (dependents[k] ??= []).Add(at[dependent]);
                        pending[at[dependent]]++;
                    }
                }

                ordered.AddRange(Kahn(mine.Count, dependents, pending).Select(k => rows[mine[k]]));
            }

            var reordered = seedOrder.Select(s => p.Seeds[s]).ToList();
            p.Seeds.Clear();
            p.Seeds.AddRange(reordered);
            rows.Clear();
            rows.AddRange(ordered);

            Dictionary<RSeedRow, int> Positions(List<RDataRow> list)
            {
                var map = new Dictionary<RSeedRow, int>(ReferenceEqualityComparer.Instance);
                for (var i = 0; i < list.Count; i++)
                    map.TryAdd(_byRow[list[i]].SeedRow, i);
                return map;
            }

            static Dictionary<string, List<int>> ByKey(List<RDataRow> list, IReadOnlyList<RColumn> columns)
            {
                var map = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                for (var i = 0; i < list.Count; i++)
                {
                    if (KeyOfCells(list[i].Values, columns) is { } key)
                        ListOf(map, key).Add(i);
                }

                return map;
            }
        }
    }

    /// <summary>A key part as its stable id name takes it: a uuid as a <see cref="Guid"/> (lowercase), a double in a decimal column as a decimal.</summary>
    private static object? StableKeyPart(RColumn column, object? value) => (column.Type, value) switch
    {
        ("uuid", string text) when Guid.TryParse(text, out var uuid) => uuid,
        ("decimal", double d) when double.IsFinite(d) && Math.Abs(d) < 7.9e28 => (decimal)d,
        _ => value,
    };

    /// <summary>A table seed's row key: the unique constraint it names, else the table's primary key.</summary>
    private static IReadOnlyList<RColumn>? KeyOfTableSeed(RTable table, RSeed seed) =>
        seed.Key is { } id && table.Uniques.FirstOrDefault(u => u.Id == id) is { } unique ? unique.Columns : table.PrimaryKey?.Columns;

    /// <summary>
    /// The row key of a table that only reference types fill: its primary key when the binding fills every column of it, else the
    /// first unique constraint the binding fills that holds the code column (MQ7119 asks for one of the two), else none.
    /// </summary>
    private static IReadOnlyList<RColumn>? KeyOfReferenceBinding(RReferenceTypeBinding binding)
    {
        var filled = new HashSet<RColumn>(binding.Fields.Select(f => f.Column).Concat(binding.Constants.Select(c => c.SourceColumn).OfType<RColumn>()),
            ReferenceEqualityComparer.Instance);
        if (binding.Table.PrimaryKey is { Columns.Count: > 0 } pk && pk.Columns.All(filled.Contains))
            return pk.Columns;
        return binding.Table.Uniques.FirstOrDefault(u => u.Columns.Count > 0 && u.Columns.All(filled.Contains)
            && (binding.CodeColumn is null || u.Columns.Contains(binding.CodeColumn)))?.Columns;
    }

    /// <summary>Whether two seeds are loaded together somewhere: either is for every environment, or they share one.</summary>
    private static bool LoadedTogether(RSeed a, RSeed b) =>
        a.Environments.Count == 0 || b.Environments.Count == 0 || a.Environments.Intersect(b.Environments, StringComparer.Ordinal).Any();

    /// <summary>The cells of a row by column name in database terms, in column order (the first value of a column wins).</summary>
    private static IReadOnlyDictionary<string, object?> Cells(IEnumerable<(RColumn Column, object? Value)> cells)
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var (column, value) in cells)
            values.TryAdd(column.Name, Stored(value, column));
        return values.ToImmutable();
    }

    /// <summary>A value as the column stores it: an enum member by the column's type (text: its code or name; else its value).</summary>
    private static object? Stored(object? value, RColumn column) => value switch
    {
        REnumMember member when column.Type is "string" or "text" => member.Code ?? member.Name,
        REnumMember member => member.Value,
        ImmutableArray<object?> items => items.Select(v => Stored(v, column)).ToImmutableArray(),
        _ => value,
    };

    /// <summary>A reference type's row through its binding: each field the seed gives a value for, then the binding's constants.</summary>
    private static IReadOnlyDictionary<string, object?> ThroughReferenceBinding(RSeed seed, RSeedRow row, RReferenceTypeBinding binding)
    {
        var given = new HashSet<string>(seed.Columns.Select(c => c.Name), StringComparer.Ordinal);
        var cells = new List<(RColumn, object?)>();
        foreach (var field in binding.Fields)
        {
            if (given.Contains(field.Name))
                cells.Add((field.Column, row.Values.GetValueOrDefault(field.Name)));
        }

        foreach (var constant in binding.Constants)
        {
            if (constant.WriteColumn is { } column)
                cells.Add((column, constant.Value));
        }

        return Cells(cells);
    }

    /// <summary>An entity row through its binding: each field the seed gives a value for, then the binding's constants.</summary>
    private static IReadOnlyDictionary<string, object?> ThroughBinding(RSeed seed, RSeedRow row, REntityBinding binding, Dictionary<string, RSeedRow> seedRowsById)
    {
        var given = new HashSet<string>(seed.Columns.Select(c => c.Name), StringComparer.Ordinal);
        var cells = new List<(RColumn, object?)>();
        foreach (var field in binding.Fields)
        {
            if (field.WriteColumn is not { } column)
                continue;
            object? value;
            if (field.End is { } end)
            {
                if (!given.Contains(end.Role))
                    continue;
                value = KeyOfReferenced(row, end.Role, end, 0, seedRowsById);
            }
            else if (field.Attribute is { } attribute)
            {
                if (!given.Contains(attribute.Name))
                    continue;
                var cell = row.Values.GetValueOrDefault(attribute.Name);
                value = field.Member is { } member ? (cell as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault(member.Name) : cell;
            }
            else
            {
                continue;
            }

            if (value is null && field.IsGenerated)
                continue;
            cells.Add((column, value));
        }

        foreach (var constant in binding.Constants)
        {
            if (constant.WriteColumn is { } column)
                cells.Add((column, constant.Value));
        }

        return Cells(cells);
    }

    /// <summary>An entity row through the mapping of a database that projects the entity: its attribute columns, its to-one keys, the discriminator.</summary>
    private static IReadOnlyDictionary<string, object?> ThroughMapping(RSeed seed, RSeedRow row, REntityMapping mapping, Dictionary<string, RSeedRow> seedRowsById)
    {
        var table = mapping.Table;
        var cells = new List<(RColumn, object?)>();
        foreach (var sc in seed.Columns)
        {
            if (sc.Kind == "attribute" && sc.Attribute is { Collection: false } attribute)
            {
                var cell = row.Values.GetValueOrDefault(sc.Name);
                foreach (var column in table.Columns)
                {
                    if (column.AttributePath == attribute.Id)
                    {
                        cells.Add((column, cell));
                    }
                    else if (column.AttributePath is { } path && path.StartsWith(attribute.Id + ".", StringComparison.Ordinal) && column.Attribute is { } member)
                    {
                        cells.Add((column, (cell as IReadOnlyDictionary<string, object?>)?.GetValueOrDefault(member.Name)));
                    }
                }
            }
            else if (sc.Kind == "end" && sc.End is { } end)
            {
                var key = table.ForeignKeys.FirstOrDefault(f => f.End?.Id == end.Id) ?? table.ForeignKeys.FirstOrDefault(f => f.End?.Opposite?.Id == end.Id);
                for (var i = 0; key is not null && i < key.Columns.Count; i++)
                    cells.Add((key.Columns[i], KeyOfReferenced(row, sc.Name, end, i, seedRowsById)));
            }
        }

        if (mapping.DiscriminatorColumn is { } discriminator)
            cells.Add((discriminator, mapping.DiscriminatorValue));
        return Cells(cells);
    }

    /// <summary>A relation row into its junction table: each end's key columns and the relation's attribute columns.</summary>
    private static IReadOnlyDictionary<string, object?> ThroughJunction(RSeed seed, RSeedRow row, RTable junction, Dictionary<string, RSeedRow> seedRowsById)
    {
        var cells = new List<(RColumn, object?)>();
        foreach (var sc in seed.Columns)
        {
            if (sc.Kind == "end" && sc.End is { } end && junction.ForeignKeys.FirstOrDefault(f => f.End?.Id == end.Id) is { } key)
            {
                for (var i = 0; i < key.Columns.Count; i++)
                    cells.Add((key.Columns[i], KeyOfReferenced(row, sc.Name, end, i, seedRowsById)));
            }
            else if (sc.Kind == "attribute" && sc.Attribute is { } attribute && junction.Columns.FirstOrDefault(c => c.AttributePath == attribute.Id) is { } column)
            {
                cells.Add((column, row.Values.GetValueOrDefault(sc.Name)));
            }
        }

        return Cells(cells);
    }

    /// <summary>The key value (part <paramref name="index"/>) of the row an end cell names: the referenced row's key attribute.</summary>
    private static object? KeyOfReferenced(RSeedRow row, string cellName, REnd end, int index, Dictionary<string, RSeedRow> seedRowsById)
    {
        if (row.Values.GetValueOrDefault(cellName) is not string id || !seedRowsById.TryGetValue(id, out var referenced))
            return null;
        var keyAttributes = end.Entity?.Key?.Attributes;
        if (keyAttributes is null || index >= keyAttributes.Count)
            return null;
        var name = keyAttributes[index].Name;
        return referenced.Values.GetValueOrDefault(name);
    }

    /// <summary>Positions 0 to <paramref name="count"/> - 1 in dependency order (Kahn's algorithm, ties by position; a cycle is broken at its first remaining position).</summary>
    private static List<int> Kahn(int count, IReadOnlyList<IEnumerable<int>?> dependents, int[] pending)
    {
        var result = new List<int>(count);
        var ready = new SortedSet<int>(Enumerable.Range(0, count).Where(i => pending[i] == 0));
        var remaining = new SortedSet<int>(Enumerable.Range(0, count));
        while (remaining.Count > 0)
        {
            var next = ready.Count > 0 ? ready.Min : remaining.Min;
            ready.Remove(next);
            remaining.Remove(next);
            result.Add(next);
            foreach (var dependent in dependents[next] ?? [])
            {
                if (--pending[dependent] <= 0 && remaining.Contains(dependent))
                    ready.Add(dependent);
            }
        }

        return result;
    }

    /// <summary>A row's key over some columns (codes as <see cref="RRow.KeyOf"/> reads them), or <see langword="null"/> when a cell is null.</summary>
    private static string? KeyOfCells(IReadOnlyDictionary<string, object?> values, IReadOnlyList<RColumn> columns)
    {
        var parts = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            if (values.GetValueOrDefault(columns[i].Name) is not { } value)
                return null;
            parts[i] = RRow.KeyOf(value);
        }

        return string.Join("\u0000", parts);
    }

    /// <summary>The seeded tables in foreign key order (Kahn's algorithm, ties by schema and name); a cycle's tables follow by name, marked.</summary>
    private static List<(Pending Table, bool Cycle)> Order(List<Pending> tables)
    {
        static string Name(RTable t) => (t.Schema ?? "") + "\u0000" + t.Name + "\u0000" + t.Key;
        var set = new HashSet<RTable>(tables.Select(t => t.Table), ReferenceEqualityComparer.Instance);
        var byTable = new Dictionary<RTable, Pending>(ReferenceEqualityComparer.Instance);
        var needs = new Dictionary<RTable, HashSet<RTable>>(ReferenceEqualityComparer.Instance);
        foreach (var t in tables)
        {
            byTable[t.Table] = t;
            needs[t.Table] = new HashSet<RTable>(t.Table.ForeignKeys.Select(f => f.ReferencedTable).Where(r => set.Contains(r) && !ReferenceEquals(r, t.Table)),
                ReferenceEqualityComparer.Instance);
        }
        var result = new List<(Pending, bool)>();
        var done = new HashSet<RTable>(ReferenceEqualityComparer.Instance);
        while (done.Count < tables.Count)
        {
            var ready = tables.Select(t => t.Table).Where(t => !done.Contains(t) && needs[t].All(done.Contains)).OrderBy(Name, StringComparer.Ordinal).FirstOrDefault();
            if (ready is null)
            {
                foreach (var rest in tables.Select(t => t.Table).Where(t => !done.Contains(t)).OrderBy(Name, StringComparer.Ordinal))
                {
                    result.Add((byTable[rest], true));
                    done.Add(rest);
                }

                break;
            }

            result.Add((byTable[ready], false));
            done.Add(ready);
        }

        return result;
    }

    /// <summary>The hash of a database's seed tables in canonical form.</summary>
    private static string SeedHashOf(RList<RSeedTable> tables) => CanonicalForm.Hash("seed-data", tables.Select(t => new
    {
        Table = t.Table.Key,
        t.Table.Name,
        t.Table.Schema,
        Key = t.KeyColumns.Select(c => c.Name).ToList(),
        Rows = t.Rows.Select(r => new
        {
            r.Id,
            r.Source,
            r.Apply,
            r.Delete,
            r.Environments,
            Values = new SortedDictionary<string, object?>(r.Values.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal), StringComparer.Ordinal),
            // The rows foreign-key entries name: a lookup value is null in Values, so its natural key changes the hash here. Absent
            // (and so not hashed) without entries, which keeps the hash of every other row as it was.
            Refs = r.Refs.Count == 0 ? null : new SortedDictionary<string, object?>(r.Refs.ToDictionary(p => p.Key,
                p => (object?)new { Table = p.Value.Table.Key, p.Value.Key, Row = p.Value.Row?.Id, Unknown = p.Value.Unknown.Select(c => c.Name).ToList() },
                StringComparer.Ordinal), StringComparer.Ordinal),
        }).ToList(),
    }).ToList());
}
