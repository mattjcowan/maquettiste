using System.Collections.Immutable;
using Maquettiste.Engine.Planning;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The rows a database receives (2026-10-07, table seeds; <see cref="RDatabase.SeedTables"/>): the seeds of its table files, and the
/// entity and relation seeds that reach one of its tables, through the entity's binding (the field map, the binding's constants, the
/// write table) or, for an entity the database projects, through its mapping (the attribute columns, the foreign keys of its to-one
/// ends, the discriminator), and a relation seed through its junction table. Cells are in database terms: an enum member by the
/// column's storage (its code or name in a text column, else its value), a to-one end as the referenced row's key. Tables come in
/// foreign key order, referenced tables first; a cycle among seeded tables keeps their name order and marks them. Built on first read
/// per database, so a run whose templates never read seed tables pays nothing.
/// </summary>
internal sealed partial class ResolveRun
{
    private static void PrepareSeedTables(RDatabase database, IReadOnlyList<RSeed> seeds, Dictionary<string, RSeedRow> seedRowsById)
    {
        database.SeedTablesFactory = () => BuildSeedTables(database, seeds, seedRowsById);
        database.SeedHashFactory = SeedHashOf;
    }

    private sealed class Pending(RTable table)
    {
        public RTable Table { get; } = table;
        public List<RSeed> Seeds { get; } = [];
        public List<RDataRow> Rows { get; } = [];
        public IReadOnlyList<RColumn>? Key { get; set; }
        public HashSet<string> Dependencies { get; } = new(StringComparer.Ordinal);
    }

    private static RList<RSeedTable> BuildSeedTables(RDatabase database, IReadOnlyList<RSeed> seeds, Dictionary<string, RSeedRow> seedRowsById)
    {
        var tables = new HashSet<RTable>(database.Tables, ReferenceEqualityComparer.Instance);
        var pending = new Dictionary<RTable, Pending>(ReferenceEqualityComparer.Instance);
        Pending Of(RTable table) => pending.TryGetValue(table, out var p) ? p : pending[table] = new Pending(table);

        // Table seeds first, by seed name (the seeds come in name order).
        foreach (var seed in seeds)
        {
            if (seed.Target is not RTable table || !tables.Contains(table))
                continue;
            var p = Of(table);
            p.Key ??= KeyOfTableSeed(table, seed);
            Add(p, seed, "table", seed.Rows.Select(row => Cells(seed.Columns
                .Where(c => c.Column is not null)
                .Select(c => (c.Column!, row.Values.GetValueOrDefault(c.Name))))));
        }

        // Entity and relation seeds that reach a table of the database.
        foreach (var seed in seeds)
        {
            switch (seed.Target)
            {
                case REntity entity when entity.Bindings.TryGetValue(database.Name, out var binding):
                    if (!binding.Writes || (binding.WriteTable ?? binding.SourceTable) is not { } written || !tables.Contains(written))
                        break;
                    {
                        var p = Of(written);
                        p.Dependencies.UnionWith(entity.Dependencies);
                        Add(p, seed, "entity", seed.OrderedRows.Select(row => ThroughBinding(seed, row, binding, seedRowsById)));
                    }

                    break;
                case REntity entity when entity.Mappings.TryGetValue(database.Name, out var mapping) && tables.Contains(mapping.Table):
                    {
                        var p = Of(mapping.Table);
                        Add(p, seed, "entity", seed.OrderedRows.Select(row => ThroughMapping(seed, row, mapping, seedRowsById)));
                    }

                    break;
                case RRelation relation when relation.Mappings.TryGetValue(database.Name, out var link) && link.JunctionTable is { } junction
                    && tables.Contains(junction):
                    {
                        var p = Of(junction);
                        Add(p, seed, "relation", seed.OrderedRows.Select(row => ThroughJunction(seed, row, junction, seedRowsById)));
                    }

                    break;
            }
        }

        var ordered = Order(pending.Values.ToList());
        var result = new List<RSeedTable>(ordered.Count);
        foreach (var (p, cycle) in ordered)
        {
            var key = p.Key ?? (IReadOnlyList<RColumn>?)p.Table.PrimaryKey?.Columns ?? [];
            var used = new HashSet<string>(p.Rows.SelectMany(r => r.Values.Keys), StringComparer.Ordinal);
            foreach (var row in p.Rows)
                row.Key = [.. key.Select(c => row.Values.GetValueOrDefault(c.Name))];
            p.Dependencies.UnionWith(p.Table.Dependencies);
            foreach (var seed in p.Seeds)
                p.Dependencies.UnionWith(seed.Dependencies);
            result.Add(new RSeedTable
            {
                Id = p.Table.Key,
                Table = p.Table,
                KeyColumns = key,
                Columns = [.. p.Table.Columns.Where(c => used.Contains(c.Name))],
                Seeds = p.Seeds,
                Rows = p.Rows,
                InCycle = cycle,
                Dependencies = [.. p.Dependencies.Order(StringComparer.Ordinal)],
            });
        }

        return new RList<RSeedTable>(result, ["k:seed", "k:table", "k:entity"]);

        static void Add(Pending p, RSeed seed, string source, IEnumerable<IReadOnlyDictionary<string, object?>> rows)
        {
            if (!p.Seeds.Contains(seed))
                p.Seeds.Add(seed);
            var i = 0;
            var ids = source == "table" ? seed.Rows : seed.OrderedRows;
            foreach (var values in rows)
            {
                p.Rows.Add(new RDataRow { Id = ids[i].Id, Seed = seed, Source = source, Values = values, Dependencies = seed.Dependencies });
                i++;
            }
        }
    }

    /// <summary>A table seed's row key: the unique constraint it names, else the table's primary key.</summary>
    private static IReadOnlyList<RColumn>? KeyOfTableSeed(RTable table, RSeed seed) =>
        seed.Key is { } id && table.Uniques.FirstOrDefault(u => u.Id == id) is { } unique ? unique.Columns : table.PrimaryKey?.Columns;

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
        var keyAttributes = end.Entity.Key?.Attributes;
        if (keyAttributes is null || index >= keyAttributes.Count)
            return null;
        var name = keyAttributes[index].Name;
        return referenced.Values.GetValueOrDefault(name);
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
        }).ToList(),
    }).ToList());
}
