using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Validation;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// Reference types, their storage choices, seeds and rows (reference-types-seeds-localization.md sections 1.4, 1.5 and 2.5). The
/// resolver resolves intent only: a storage choice is a project key or none (template-defined), never a table or a constraint.
/// </summary>
internal sealed partial class ResolveRun
{
    /// <summary>The settings key of reference data: strategy declarations and every <c>referenceStorage</c> choice.</summary>
    public const string ReferenceDataKey = "s:referenceData";

    private static readonly string[] BuiltinColumns = ["code", "label", "description"];

    private readonly Dictionary<string, RReferenceType> _referenceTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Type, string Database), RStorageChoice> _storageChoices = [];
    private List<RReferenceType> _referenceTypeOrder = [];
    private List<RSeed> _seedOrder = [];
    private Func<RList<RSeed>>? _seedsInOrder;
    private IReadOnlyList<string> _seedsInOrderKeys = [];

    /// <summary>The effective storage choice of a reference type in a database.</summary>
    public RStorageChoice? StorageChoice(string typeId, string databaseId) => _storageChoices.GetValueOrDefault((typeId, databaseId));

    /// <summary>Creates every reference type with its fields and storage choices, then resolves their attributes.</summary>
    private void ResolveReferenceTypes()
    {
        var databases = Model.All<Database>().OrderBy(d => d.Name, StringComparer.Ordinal).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        var sources = Model.All<ReferenceType>();
        foreach (var type in sources)
        {
            var r = new RReferenceType();
            var deps = DepsOf(r);
            FillCommon(r, type, deps);
            r.Package = PackageOf(type.Package);
            r.Code = new RReferenceField
            {
                Id = type.Code.Id, Type = type.Code.Type, Length = type.Code.Length, Pattern = type.Code.Pattern,
                DisplayName = type.Code.DisplayName ?? "Code", Description = type.Code.Description?.Text,
            };
            r.Label = new RReferenceField
            {
                Id = type.Label.Id, Type = "string", Length = type.Label.Length,
                DisplayName = type.Label.DisplayName ?? "Label", Description = type.Label.Description?.Text,
            };
            var storage = new SortedDictionary<string, RStorageChoice>(StringComparer.Ordinal);
            foreach (var database in databases)
            {
                var choice = MakeStorageChoice(type, database);
                storage[database.Name] = choice;
                _storageChoices[(type.Id, database.Id)] = choice;
            }

            r.Storage = SortedMap(storage);
            _referenceTypes[type.Id] = r;
            Register(r);
        }

        // Fields may be typed by another reference type (or the same one): every type exists before any field resolves.
        foreach (var type in sources)
        {
            var r = _referenceTypes[type.Id];
            var raw = type.Attributes.Select(a => (Attribute: a, Stereotype: (Stereotype?)null)).ToList();
            foreach (var stereotype in StereotypesOf(type))
                raw.AddRange(stereotype.Attributes.Select(a => (Attribute: a, Stereotype: (Stereotype?)stereotype)));
            var attributes = StableByOrder(raw.DistinctBy(x => x.Attribute.Id, StringComparer.Ordinal), x => x.Attribute.Order)
                .Select(x => MakeAttribute(x.Attribute, r, r.Package, null, false, x.Stereotype, x.Stereotype?.Id ?? type.Id))
                .ToList();
            SetOrder(attributes);
            r.Attributes = new RList<RAttribute>(attributes, DepsOf(r).ToList());
            foreach (var attribute in attributes)
                Register(attribute);
        }

        _referenceTypeOrder = [.. _referenceTypes.Values.OrderBy(t => t.Name, StringComparer.Ordinal).ThenBy(t => t.Id, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The effective choice (section 1.4): the type's entry for the database, the type's <c>*</c> entry, the database's
    /// <c>referenceStorage</c>, the project's, else none (template-defined).
    /// </summary>
    private RStorageChoice MakeStorageChoice(ReferenceType type, Database database)
    {
        StorageChoice? choice;
        string? source;
        if (type.Storage.TryGetValue(database.Id, out var own) || type.Storage.TryGetValue("*", out own))
            (choice, source) = (own, "type");
        else if (Settings.Databases.TryGetValue(database.Name, out var conventions) && conventions.ReferenceStorage is { } byDatabase)
            (choice, source) = (byDatabase, "database");
        else if (Settings.Conventions.ReferenceStorage is { } project)
            (choice, source) = (project, "project");
        else
            (choice, source) = (null, null);

        var result = new RStorageChoice
        {
            Id = type.Id + "@" + database.Id,
            Strategy = choice?.Strategy,
            Options = choice is null || choice.Options.Count == 0 ? ImmutableSortedDictionary<string, object?>.Empty : ResolutionValues.PlainMap(choice.Options),
            Source = source,
        };
        if (result.Strategy is { } strategy && Settings.ReferenceData.Strategies.TryGetValue(strategy, out var declaration))
        {
            result.Description = declaration.Description;
            result.Collections = declaration.Collections.For(DialectInfo.Name(database.Dialect));
        }

        DepsOf(result).Add(ReferenceDataKey).Element(type.Id).Element(database.Id);
        return result;
    }

    /// <summary>
    /// Resolves seeds, their rows and the rows of each reference type, the reference usages of attributes and the seed lists of
    /// every target. Runs after the conceptual layer is finished (it reads flattened attributes and ends).
    /// </summary>
    private void ResolveSeedsAndUsages(IReadOnlyList<RDatabase> databases)
    {
        // Table seeds target a table file of a database (2026-10-07): the tables by key, designed and imported ones only.
        var tablesByKey = new Dictionary<string, RTable>(StringComparer.Ordinal);
        foreach (var database in databases)
        {
            foreach (var table in database.Tables.Where(t => t.Origin != "synthesized"))
                tablesByKey.TryAdd(table.Key, table);
        }

        ResolveReferenceBindings(tablesByKey);

        var endOwners = new Dictionary<string, (REnd End, RRelation Relation)>(StringComparer.Ordinal);
        foreach (var relation in _relations.Values)
        {
            foreach (var end in relation.Ends)
                endOwners.TryAdd(end.Id, (end, relation));
        }

        var seedRowsById = new Dictionary<string, RSeedRow>(StringComparer.Ordinal);
        // Every target's seeds merge in one order (next-phase-1-design.md section 4.3): package qualified name with none first, then
        // name, then id; without packages that is the (name, id) order.
        var order = new SeedOrder(Model);
        var sources = Model.All<Seed>().Order(order).ToList();
        var seedsByTarget = new Dictionary<string, List<RSeed>>(StringComparer.Ordinal);
        var rowsByType = new Dictionary<string, List<RRow>>(StringComparer.Ordinal);
        // The packages whose names order each target's seeds: no seed file holds them, so they join the lists' membership keys.
        var orderKeysByTarget = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var seed in sources)
        {
            Ct.ThrowIfCancellationRequested();
            var s = new RSeed();
            var deps = DepsOf(s);
            FillCommon(s, seed, deps);
            deps.Element(seed.Target);
            // The merge order reads the qualified name of the seed's package: renaming or moving a package of its chain can reorder the
            // target's seeds (its rows, the first table seed's key) without touching the seed's file.
            foreach (var package in order.ChainOf(seed))
            {
                deps.Element(package);
                if (!orderKeysByTarget.TryGetValue(seed.Target, out var orderKeys))
                    orderKeysByTarget[seed.Target] = orderKeys = new SortedSet<string>(StringComparer.Ordinal);
                orderKeys.Add(Keys.Element(package));
            }

            IResolvedObject? target = _referenceTypes.TryGetValue(seed.Target, out var rt) ? rt
                : _entities.TryGetValue(seed.Target, out var entity) && entity.Id.Length > 0 ? entity
                : _relations.TryGetValue(seed.Target, out var relation) ? relation
                : tablesByKey.GetValueOrDefault(seed.Target);
            s.Target = target;
            s.Package = PackageOf(seed.Package) ?? (target as RAnnotated)?.Package; // its own file's package, else its target's
            s.Environments = seed.Environments;
            s.Apply = seed.Apply == SeedApply.Converge ? "converge" : "once";
            s.Delete = seed.Delete;
            s.Key = seed.Key;
            s.RowsFrom = seed.RowsFrom?.File;
            s.Columns = SeedColumns(seed, target, endOwners, deps);
            var seedKeys = deps.ToList();
            var rows = new List<RSeedRow>(seed.Rows.Count);
            for (var i = 0; i < seed.Rows.Count; i++)
            {
                // A reference type's rows feed its codes and every usage now; any other seed's cells are converted on first read.
                var source = seed.Rows[i];
                var row = new RSeedRow { Id = source.Id, Seed = s, Order = i };
                if (rt is not null)
                    row.Values = CellValues(s.Columns, source);
                else
                    row.SetValues(() => CellValues(s.Columns, source));
                row.Dependencies = seedKeys;
                rows.Add(row);
                seedRowsById.TryAdd(row.Id, row);
            }

            s.Rows = new RList<RSeedRow>(rows, seedKeys);
            if (rt is not null)
            {
                var typeRows = ListOf(rowsByType, rt.Id);
                foreach (var row in rows)
                {
                    var r = new RRow
                    {
                        Id = row.Id,
                        Seed = s,
                        Type = rt,
                        Order = typeRows.Count,
                        Code = row.Values.GetValueOrDefault("code"),
                        Label = row.Values.GetValueOrDefault("label") as string,
                        Description = row.Values.GetValueOrDefault("description") as string,
                        Values = FieldValues(s.Columns, row.Values),
                    };
                    r.Dependencies = seedKeys;
                    typeRows.Add(r);
                    Register(r);
                }
            }
            else
            {
                foreach (var row in rows)
                    Register(row);
            }

            ListOf(seedsByTarget, seed.Target).Add(s);
            _seedOrder.Add(s);
            Register(s);
        }

        // Rows by code, then the lazy refs (they read any type's rows).
        foreach (var type in _referenceTypeOrder)
        {
            var rows = rowsByType.GetValueOrDefault(type.Id) ?? [];
            var byCode = new Dictionary<string, RRow>(StringComparer.Ordinal);
            foreach (var row in rows)
                byCode.TryAdd(row.CodeKey, row); // a duplicate code is MQ7001: the first row wins
            type.RowsByCode = byCode;
            var seeds = seedsByTarget.GetValueOrDefault(type.Id) ?? [];
            var membership = new DependencySet(Keys).Add("k:seed").Referrers(type.Id).AddRange(OrderKeys(type.Id));
            foreach (var seed in seeds)
                membership.Element(seed.Id);
            type.Rows = new RList<RRow>(rows, membership.ToList());
            type.Seeds = new RList<RSeed>(seeds, [Keys.Referrers(type.Id), "k:seed", .. OrderKeys(type.Id)]);
            var fields = type.Attributes.Where(a => a.Type.ReferenceType is not null).ToList();
            foreach (var row in rows)
                row.SetRefs(r => RowRefs(fields, r.Values));
        }

        foreach (var seed in _seedOrder)
        {
            foreach (var row in seed.Rows)
                row.SetRefs(r => SeedRowRefs(r.Seed.Columns, r.Values, seedRowsById));
        }

        foreach (var entity in _entities.Values.Concat(_promoted.Values).Where(e => e.Id.Length > 0))
            entity.Seeds = new RList<RSeed>(seedsByTarget.GetValueOrDefault(entity.Id) ?? [], [Keys.Referrers(entity.Id), "k:seed", .. OrderKeys(entity.Id)]);
        foreach (var relation in _relations.Values.Concat(_promotedRelations))
            relation.Seeds = new RList<RSeed>(seedsByTarget.GetValueOrDefault(relation.Id) ?? [], [Keys.Referrers(relation.Id), "k:seed", .. OrderKeys(relation.Id)]);
        foreach (var table in tablesByKey.Values)
            table.Seeds = new RList<RSeed>(seedsByTarget.GetValueOrDefault(table.Key) ?? [], [Keys.Referrers(table.Key), "k:seed", .. OrderKeys(table.Key)]);

        OrderSeeds(seedRowsById);
        foreach (var database in databases)
            PrepareSeedTables(database, [.. _seedOrder], seedRowsById);
        ResolveUsages();

        IEnumerable<string> OrderKeys(string target) => orderKeysByTarget.GetValueOrDefault(target) ?? (IEnumerable<string>)[];
    }

    /// <summary>
    /// Resolves each reference type's bindings (2026-10-11): the first binding per database whose table is a table file of that
    /// database, its fields and constants by column id or physical name (what does not resolve is left out; MQ7117 and MQ7118 report
    /// it); sets <see cref="RReferenceType.Bindings"/> and each table's <see cref="RTable.ReferenceBindings"/>.
    /// </summary>
    private void ResolveReferenceBindings(Dictionary<string, RTable> tablesByKey)
    {
        var byTable = new Dictionary<RTable, List<RReferenceTypeBinding>>(ReferenceEqualityComparer.Instance);
        foreach (var r in _referenceTypeOrder)
        {
            if (Model.GetDocument(r.Id)?.Element is not ReferenceType { Bindings.Count: > 0 } type)
                continue;
            var bindings = new SortedDictionary<string, RReferenceTypeBinding>(StringComparer.Ordinal);
            foreach (var binding in type.Bindings)
            {
                if (!tablesByKey.TryGetValue(binding.Table, out var table) || !string.Equals(table.Database.Id, binding.Database, StringComparison.Ordinal)
                    || bindings.ContainsKey(table.Database.Name))
                    continue;
                RColumn? ColumnOf(string reference) =>
                    table.Columns.FirstOrDefault(c => string.Equals(c.Key, reference, StringComparison.Ordinal))
                    ?? table.Columns.FirstOrDefault(c => string.Equals(c.Name, reference, StringComparison.Ordinal));
                var fields = new List<RReferenceBindingField>();
                foreach (var field in binding.Fields)
                {
                    if (ColumnOf(field.Column) is not { } column || fields.Any(f => ReferenceEquals(f.Column, column)))
                        continue;
                    if (BuiltinColumns.Contains(field.Attribute, StringComparer.Ordinal))
                    {
                        if (!fields.Any(f => f.Attribute is null && f.Name == field.Attribute))
                            fields.Add(new RReferenceBindingField { Name = field.Attribute, Column = column });
                    }
                    else if (r.Attributes.FirstOrDefault(a => string.Equals(a.Id, field.Attribute, StringComparison.Ordinal)) is { Collection: false } attribute
                        && !fields.Any(f => ReferenceEquals(f.Attribute, attribute)))
                    {
                        fields.Add(new RReferenceBindingField { Name = attribute.Name, Attribute = attribute, Column = column });
                    }
                }

                var constants = new List<RBindingConstant>();
                foreach (var constant in binding.Constants)
                {
                    if (ColumnOf(constant.Column) is not { } column || fields.Any(f => ReferenceEquals(f.Column, column)) || constants.Any(c => ReferenceEquals(c.SourceColumn, column)))
                        continue;
                    constants.Add(new RBindingConstant
                    {
                        Column = constant.Column, ColumnName = column.Name, SourceColumn = column, WriteColumn = column,
                        Value = constant.Value is { } value ? ResolutionValues.Plain(value) : null,
                    });
                }

                var resolved = new RReferenceTypeBinding
                {
                    Id = binding.Id, Type = r, Database = table.Database, Table = table, Fields = fields, Constants = constants,
                    CodeColumn = fields.FirstOrDefault(f => f.Attribute is null && f.Name == "code")?.Column,
                    Description = binding.Description is { } description
                        ? description.Text ?? (description.File is null ? null : Model.GetDocument(type.Id)?.SidecarText)
                        : null,
                };
                DepsOf(resolved).Element(type.Id).Element(binding.Table);
                bindings[table.Database.Name] = resolved;
                if (!byTable.TryGetValue(table, out var list))
                    byTable[table] = list = [];
                list.Add(resolved);
                Register(resolved);
            }

            r.Bindings = SortedMap(bindings);
        }

        foreach (var (table, list) in byTable)
            table.ReferenceBindings = new RList<RReferenceTypeBinding>(list, [Keys.Referrers(table.Key)]);
    }

    private List<RSeedColumn> SeedColumns(Seed seed, IResolvedObject? target, Dictionary<string, (REnd End, RRelation Relation)> endOwners, DependencySet deps)
    {
        var attributes = target switch
        {
            RReferenceType t => t.Attributes,
            REntity e => e.Attributes,
            RRelation r => r.Attributes,
            _ => RList<RAttribute>.Empty,
        };
        var columns = new List<RSeedColumn>(seed.Columns.Count);
        if (target is RTable table)
        {
            foreach (var column in seed.Columns)
            {
                columns.Add(table.Columns.FirstOrDefault(c => string.Equals(c.Key, column, StringComparison.Ordinal)) is { } found
                    ? new RSeedColumn { Name = found.Name, Kind = "column", Column = found }
                    : new RSeedColumn { Name = column, Kind = "column" }); // an unknown column is MQ7107
            }

            return columns;
        }

        foreach (var column in seed.Columns)
        {
            if (target is RReferenceType && BuiltinColumns.Contains(column, StringComparer.Ordinal))
            {
                columns.Add(new RSeedColumn { Name = column, Kind = "builtin" });
                continue;
            }

            if (attributes.FirstOrDefault(a => string.Equals(a.Id, column, StringComparison.Ordinal)) is { } attribute)
            {
                columns.Add(new RSeedColumn { Name = attribute.Name, Kind = "attribute", Attribute = attribute });
                if (attribute.Type.Enum is { } e)
                    deps.Element(e.Id);
                continue;
            }

            if (endOwners.TryGetValue(column, out var owner))
            {
                columns.Add(new RSeedColumn { Name = owner.End.Role, Kind = "end", End = owner.End });
                deps.Element(owner.Relation.Id);
                continue;
            }

            columns.Add(new RSeedColumn { Name = column, Kind = "builtin" }); // an unknown column is MQ7005
        }

        return columns;
    }

    /// <summary>The cells of a row by column name (the first column of a name wins; a missing cell is null).</summary>
    private static IReadOnlyDictionary<string, object?> CellValues(IReadOnlyList<RSeedColumn> columns, SeedRow row)
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            if (values.ContainsKey(column.Name))
                continue;
            var cell = i < row.Values.Count ? row.Values[i] : default;
            values[column.Name] = column.Attribute?.Type.Enum is { } e ? EnumCell(e, cell) : Cell(cell);
        }

        return values.ToImmutable();
    }

    /// <summary>A reference row's user fields: its attribute cells by field name.</summary>
    private static IReadOnlyDictionary<string, object?> FieldValues(IReadOnlyList<RSeedColumn> columns, IReadOnlyDictionary<string, object?> cells)
    {
        var values = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (column.Kind == "attribute" && !values.ContainsKey(column.Name))
                values[column.Name] = cells.GetValueOrDefault(column.Name);
        }

        return values.ToImmutable();
    }

    /// <summary>
    /// A cell as a plain value. Numbers keep their decimal text (an integer as a long, else a decimal, never a double when the
    /// decimal holds it), so <c>0.36</c> reaches <c>sql_literal</c> as written.
    /// </summary>
    private static object? Cell(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Undefined or JsonValueKind.Null => null,
        JsonValueKind.Number => cell.TryGetInt64(out var l) ? l : cell.TryGetDecimal(out var m) ? m : cell.GetDouble(),
        JsonValueKind.Array => cell.EnumerateArray().Select(Cell).ToImmutableArray(),
        JsonValueKind.Object => cell.EnumerateObject().ToImmutableSortedDictionary(p => p.Name, p => Cell(p.Value), StringComparer.Ordinal),
        _ => ResolutionValues.Plain(cell),
    };

    private static object? EnumCell(REnum e, JsonElement cell)
    {
        if (cell.ValueKind == JsonValueKind.Array)
            return cell.EnumerateArray().Select(c => EnumCell(e, c)).ToImmutableArray();
        if (cell.ValueKind != JsonValueKind.String)
            return Cell(cell);
        var text = cell.GetString();
        return e.Members.FirstOrDefault(m => string.Equals(m.Name, text, StringComparison.Ordinal))
            ?? e.Members.FirstOrDefault(m => string.Equals(m.Code, text, StringComparison.Ordinal))
            ?? (object?)text;
    }

    private static IReadOnlyDictionary<string, object?> RowRefs(IReadOnlyList<RAttribute> fields, IReadOnlyDictionary<string, object?> values)
    {
        var refs = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var field in fields)
            refs.TryAdd(field.Name, Resolve(field.Type.ReferenceType!, values.GetValueOrDefault(field.Name)));
        return refs.ToImmutable();
    }

    private static IReadOnlyDictionary<string, object?> SeedRowRefs(IReadOnlyList<RSeedColumn> columns, IReadOnlyDictionary<string, object?> values,
        Dictionary<string, RSeedRow> seedRowsById)
    {
        var refs = ImmutableSortedDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var column in columns)
        {
            if (refs.ContainsKey(column.Name))
                continue;
            if (column.Attribute?.Type.ReferenceType is { } type)
                refs[column.Name] = Resolve(type, values.GetValueOrDefault(column.Name));
            else if (column.End is not null)
                refs[column.Name] = values.GetValueOrDefault(column.Name) is string id ? seedRowsById.GetValueOrDefault(id) : null;
        }

        return refs.ToImmutable();
    }

    private static object? Resolve(RReferenceType type, object? value) => value switch
    {
        null => null,
        ImmutableArray<object?> codes => codes.Select(c => (object?)type.RowOf(c)).ToImmutableArray(),
        _ => type.RowOf(value),
    };

    /// <summary>The ids of the rows a seed row's cells name (reference codes to their rows, end cells to their row ids).</summary>
    private static IEnumerable<(string Id, RSeed? Seed)> Named(RSeedRow row, Dictionary<string, RSeedRow> seedRowsById)
    {
        foreach (var column in row.Seed.Columns)
        {
            var value = row.Values.GetValueOrDefault(column.Name);
            if (column.Attribute?.Type.ReferenceType is { } type)
            {
                foreach (var code in value is ImmutableArray<object?> list ? list : [value])
                {
                    if (type.RowOf(code) is { } target)
                        yield return (target.Id, target.Seed);
                }
            }
            else if (column.End is not null && value is string id && seedRowsById.TryGetValue(id, out var target))
            {
                yield return (target.Id, target.Seed);
            }
        }
    }

    /// <summary>
    /// <see cref="RSeed.OrderedRows"/> by Kahn's algorithm (ties by file order; a cycle, which MQ7103 allows through optional cells,
    /// is broken at its first row in file order), and <see cref="ResolvedModel.SeedsInOrder"/> by dependency depth, then
    /// seed order (<see cref="SeedOrder"/>). Both
    /// are computed on first read (<see cref="SeedOrdering"/>), so the rows of a seed nothing reads are never ordered; only the
    /// dependency keys of the insert order are set here, since they do not depend on the rows.
    /// </summary>
    private void OrderSeeds(Dictionary<string, RSeedRow> seedRowsById)
    {
        var ordering = new SeedOrdering([.. _seedOrder], seedRowsById);
        for (var i = 0; i < _seedOrder.Count; i++)
        {
            var at = i;
            _seedOrder[i].SetOrderedRows(() => ordering.Of(at).Ordered);
        }

        var keys = new DependencySet(Keys).Add("k:seed");
        foreach (var seed in _seedOrder)
        {
            keys.Element(seed.Id);
            if (seed.Target is RTable table)
                keys.Element(table.Key); // its foreign keys order table seeds
        }

        foreach (var type in _referenceTypeOrder)
        {
            keys.Element(type.Id);
            foreach (var binding in type.Bindings.Values)
                keys.Element(binding.Table.Key);
        }

        _seedsInOrderKeys = keys.ToList();
        var frozen = _seedsInOrderKeys;
        _seedsInOrder = () => new RList<RSeed>(ordering.InsertOrder(), frozen);
    }

    /// <summary>
    /// The row and seed order of one resolution, computed on demand and shared by every reader (thread-safe). It holds only what
    /// ordering needs (the seeds and the row index), never the resolve run.
    /// </summary>
    private sealed class SeedOrdering(IReadOnlyList<RSeed> seeds, Dictionary<string, RSeedRow> seedRowsById)
    {
        private readonly Lazy<(RList<RSeedRow> Ordered, SortedSet<int> Across)>?[] _perSeed = new Lazy<(RList<RSeedRow>, SortedSet<int>)>?[seeds.Count];
        private Dictionary<RSeed, int>? _index;
        private readonly Dictionary<string, Dictionary<string, List<RSeedRow>>> _rowsByKey = new(StringComparer.Ordinal);
        private Dictionary<RTable, List<RSeed>>? _seedsByTable;

        private Dictionary<RSeed, int> Index => LazyInitializer.EnsureInitialized(ref _index, () =>
        {
            var index = new Dictionary<RSeed, int>(ReferenceEqualityComparer.Instance);
            for (var i = 0; i < seeds.Count; i++)
                index[seeds[i]] = i;
            return index;
        });

        /// <summary>One seed's ordered rows and the seeds (by position) its rows name.</summary>
        public (RList<RSeedRow> Ordered, SortedSet<int> Across) Of(int i)
        {
            var slot = Volatile.Read(ref _perSeed[i]);
            if (slot is null)
            {
                var created = new Lazy<(RList<RSeedRow>, SortedSet<int>)>(() => Order(seeds[i]), LazyThreadSafetyMode.ExecutionAndPublication);
                slot = Interlocked.CompareExchange(ref _perSeed[i], created, null) ?? created;
            }

            return slot.Value;
        }

        private (RList<RSeedRow> Ordered, SortedSet<int> Across) Order(RSeed seed)
        {
            var rows = seed.Rows;
            var foreignKeys = ForeignKeysGiven(seed);
            // A seed whose columns name no other row (no end, no reference type, no foreign key) keeps its file order and depends on no
            // seed: the general pass below would find no edge, so its cells are not read.
            if (foreignKeys.Count == 0 && !seed.Columns.Any(c => c.End is not null || c.Attribute?.Type.ReferenceType is not null))
                return (new RList<RSeedRow>(rows, rows.MembershipKeys), []);
            var position = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++)
                position.TryAdd(rows[i].Id, i);
            var dependents = new List<int>?[rows.Count];
            var pending = new int[rows.Count];
            var across = new SortedSet<int>();
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var (id, owner) in Named(rows[i], seedRowsById).Concat(NamedByKeys(seed, rows[i], foreignKeys)).Distinct())
                {
                    if (ReferenceEquals(owner, seed) && position.TryGetValue(id, out var p))
                    {
                        if (p == i)
                            continue;
                        (dependents[p] ??= []).Add(i);
                        pending[i]++;
                    }
                    else if (owner is not null && Index.TryGetValue(owner, out var other))
                    {
                        across.Add(other);
                    }
                }
            }

            var ordered = new List<RSeedRow>(rows.Count);
            var ready = new SortedSet<int>();
            var remaining = new SortedSet<int>(Enumerable.Range(0, rows.Count));
            for (var i = 0; i < rows.Count; i++)
            {
                if (pending[i] == 0)
                    ready.Add(i);
            }

            while (remaining.Count > 0)
            {
                var next = ready.Count > 0 ? ready.Min : remaining.Min; // a cycle: its first remaining row in file order
                ready.Remove(next);
                remaining.Remove(next);
                ordered.Add(rows[next]);
                foreach (var dependent in dependents[next] ?? [])
                {
                    if (--pending[dependent] <= 0 && remaining.Contains(dependent))
                        ready.Add(dependent);
                }
            }

            return (new RList<RSeedRow>(ordered, rows.MembershipKeys), across);
        }

        /// <summary>
        /// The foreign keys whose every column a seed gives, with how to read each column's cell: those of a table seed's table, and for
        /// a reference type's seed those of the tables its bindings name (through the field map and the constants).
        /// </summary>
        private static List<(RForeignKey Key, Func<RSeedRow, object?>[] Cells)> ForeignKeysGiven(RSeed seed)
        {
            var result = new List<(RForeignKey, Func<RSeedRow, object?>[])>();
            IEnumerable<RTable> tables = seed.Target switch
            {
                RTable table => [table],
                RReferenceType type => type.Bindings.Values.Select(b => b.Table),
                _ => [],
            };
            foreach (var table in tables)
            {
                foreach (var fk in table.ForeignKeys)
                {
                    if (fk.Columns.Count == 0 || fk.ReferencedColumns.Count != fk.Columns.Count || CellsOf(seed, table, fk.Columns) is not { } cells)
                        continue;
                    result.Add((fk, cells));
                }
            }

            return result;
        }

        /// <summary>
        /// How a seed's row gives each of some columns of a table: a table seed's cell, or for a seed of a reference type bound to the
        /// table its field's cell or a constant (2026-10-11); <see langword="null"/> when the seed gives not every column.
        /// </summary>
        private static Func<RSeedRow, object?>[]? CellsOf(RSeed seed, RTable table, IReadOnlyList<RColumn> columns)
        {
            var binding = seed.Target is RReferenceType type && type.Bindings.TryGetValue(table.Database.Name, out var b) && ReferenceEquals(b.Table, table) ? b : null;
            if (binding is null && !ReferenceEquals(seed.Target, table))
                return null;
            var cells = new Func<RSeedRow, object?>[columns.Count];
            for (var i = 0; i < columns.Count; i++)
            {
                var column = columns[i];
                if (binding is null)
                {
                    if (seed.Columns.FirstOrDefault(c => ReferenceEquals(c.Column, column)) is not { } cell)
                        return null;
                    var name = cell.Name;
                    cells[i] = row => row.Values.GetValueOrDefault(name);
                }
                else if (binding.Fields.FirstOrDefault(f => ReferenceEquals(f.Column, column)) is { } field)
                {
                    var name = field.Name;
                    cells[i] = row => row.Values.GetValueOrDefault(name);
                }
                else if (binding.Constants.FirstOrDefault(c => ReferenceEquals(c.SourceColumn, column)) is { } constant)
                {
                    var value = constant.Value;
                    cells[i] = _ => value;
                }
                else
                {
                    return null;
                }
            }

            return cells;
        }

        /// <summary>A row's key over some cells (codes as <see cref="RRow.KeyOf"/> reads them), or <see langword="null"/> when a cell is null.</summary>
        private static string? KeyOf(RSeedRow row, Func<RSeedRow, object?>[] cells)
        {
            var parts = new string[cells.Length];
            for (var i = 0; i < cells.Length; i++)
            {
                if (cells[i](row) is not { } value)
                    return null;
                parts[i] = RRow.KeyOf(value);
            }

            return string.Join("\u0000", parts);
        }

        /// <summary>
        /// The rows of the referenced tables that a seed row's foreign keys name: every row of that key whose seed is loaded with
        /// <paramref name="seed"/> somewhere (seeds of different environments may hold the same key; each is a parent where it loads).
        /// </summary>
        private IEnumerable<(string Id, RSeed? Seed)> NamedByKeys(RSeed seed, RSeedRow row, List<(RForeignKey Key, Func<RSeedRow, object?>[] Cells)> foreignKeys)
        {
            foreach (var (fk, cells) in foreignKeys)
            {
                if (KeyOf(row, cells) is not { } key || !RowsByKey(fk.ReferencedTable, fk.ReferencedColumns).TryGetValue(key, out var targets))
                    continue;
                foreach (var target in targets)
                {
                    if (target.Seed is not { } holder || LoadedTogether(seed, holder))
                        yield return (target.Id, target.Seed);
                }
            }
        }

        /// <summary>
        /// The rows a table holds by their key over some columns: its table seeds' rows and the rows of the reference types bound to it,
        /// every holder of a key in seed order (seeds of different environments may share keys; MQ7112 reports a repeat where they meet).
        /// </summary>
        private Dictionary<string, List<RSeedRow>> RowsByKey(RTable table, IReadOnlyList<RColumn> columns)
        {
            var name = table.Key + "\u0000" + string.Join("\u0000", columns.Select(c => c.Name));
            lock (_rowsByKey)
            {
                if (_rowsByKey.TryGetValue(name, out var found))
                    return found;
                var rows = new Dictionary<string, List<RSeedRow>>(StringComparer.Ordinal);
                foreach (var seed in SeedsOf(table))
                {
                    if (CellsOf(seed, table, columns) is not { } cells)
                        continue;
                    foreach (var row in seed.Rows)
                    {
                        if (KeyOf(row, cells) is { } key)
                            ListOf(rows, key).Add(row);
                    }
                }

                return _rowsByKey[name] = rows;
            }
        }

        /// <summary>The seeds that put rows in a table, in seed order: its table seeds and the seeds of the reference types bound to it (called under the lock).</summary>
        private List<RSeed> SeedsOf(RTable table)
        {
            if (_seedsByTable is null)
            {
                _seedsByTable = new Dictionary<RTable, List<RSeed>>(ReferenceEqualityComparer.Instance);
                foreach (var seed in seeds)
                {
                    IEnumerable<RTable> targets = seed.Target switch
                    {
                        RTable t => [t],
                        RReferenceType type => type.Bindings.Values.Select(b => b.Table),
                        _ => [],
                    };
                    foreach (var target in targets.Distinct(ReferenceEqualityComparer.Instance).Cast<RTable>())
                    {
                        if (!_seedsByTable.TryGetValue(target, out var list))
                            _seedsByTable[target] = list = [];
                        list.Add(seed);
                    }
                }
            }

            return _seedsByTable.GetValueOrDefault(table) ?? [];
        }

        /// <summary>Every seed by dependency depth, then merge order: depth 0 without dependencies, else one more than the deepest seed it names; a back edge of a cycle counts as none.</summary>
        public List<RSeed> InsertOrder()
        {
            var depth = new int[seeds.Count];
            var state = new byte[seeds.Count]; // 0 new, 1 on the path, 2 done
            int Depth(int i)
            {
                if (state[i] == 2)
                    return depth[i];
                if (state[i] == 1)
                    return -1;
                state[i] = 1;
                var d = 0;
                foreach (var other in Of(i).Across)
                    d = Math.Max(d, Depth(other) + 1);
                state[i] = 2;
                return depth[i] = d;
            }

            for (var i = 0; i < seeds.Count; i++)
                Depth(i);
            return [.. Enumerable.Range(0, seeds.Count).OrderBy(i => depth[i]).ThenBy(i => i).Select(i => seeds[i])];
        }
    }

    /// <summary>Sets <see cref="RAttribute.Reference"/> on every attribute typed by a reference type, and each type's <c>used_by</c>.</summary>
    private void ResolveUsages()
    {
        var owners = new List<IEnumerable<RAttribute>>();
        foreach (var entity in _entities.Values.Concat(_promoted.Values).Where(e => e.Id.Length > 0))
            owners.Add(entity.Attributes);
        foreach (var valueObject in _valueObjects.Values.Where(v => v.Id.Length > 0))
            owners.Add(valueObject.Attributes);
        foreach (var relation in _relations.Values.Concat(_promotedRelations))
            owners.Add(relation.Attributes);
        foreach (var type in _referenceTypeOrder)
            owners.Add(type.Attributes);

        var usedBy = new Dictionary<string, List<RAttribute>>(StringComparer.Ordinal);
        var seen = new HashSet<RAttribute>(ReferenceEqualityComparer.Instance);
        foreach (var attributes in owners)
        {
            foreach (var attribute in attributes)
            {
                if (attribute.Type.ReferenceType is not { } type || !seen.Add(attribute))
                    continue;
                attribute.Reference = Usage(attribute, type);
                if (!attribute.IsInherited && !(attribute.Owner is REntity { IsPromoted: true }))
                    ListOf(usedBy, type.Id).Add(attribute);
            }
        }

        foreach (var type in _referenceTypeOrder)
        {
            var list = usedBy.GetValueOrDefault(type.Id) ?? [];
            list.Sort((a, b) =>
            {
                var c = string.CompareOrdinal(a.Owner.Id, b.Owner.Id);
                if (c == 0)
                    c = a.Order.CompareTo(b.Order);
                return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
            });
            type.UsedBy = new RList<RAttribute>(list, [Keys.Referrers(type.Id)]);
        }
    }

    private RReferenceUsage Usage(RAttribute attribute, RReferenceType type)
    {
        var usage = new RReferenceUsage
        {
            Id = attribute.Owner.Id + "/" + attribute.Id + "/reference",
            Type = type,
            IsCollection = attribute.Collection,
            Required = attribute.Required,
            Storage = type.Storage,
        };
        var deps = DepsOf(usage).AddRange(DepsOf(attribute).ToList()).AddRange(type.Rows.MembershipKeys);
        var allowed = attribute.Validation?.AllowedValues is { Count: > 0 } codes
            ? codes.Select(c => RRow.KeyOf(c)).ToHashSet(StringComparer.Ordinal)
            : null;
        usage.Allowed = new RList<RRow>(allowed is null ? type.Rows : type.Rows.Where(r => allowed.Contains(r.CodeKey)), deps.ToList());
        usage.DefaultRow = type.RowOf(attribute.Default is ImmutableArray<object?> list ? list.FirstOrDefault() : attribute.Default);
        return usage;
    }

    /// <summary>
    /// The collection attributes typed by a reference type that a mapping leaves to the templates (an ignored attribute excluded).
    /// </summary>
    private IReadOnlyList<RAttribute> TemplateDefinedOf(IEnumerable<RAttribute> attributes, string databaseName, string elementId)
    {
        List<RAttribute>? result = null;
        Mapping? mapping = null;
        var looked = false;
        foreach (var attribute in attributes)
        {
            if (attribute.Type.ReferenceType is null || !attribute.Collection)
                continue;
            if (!looked)
            {
                looked = true;
                var database = Model.All<Database>().FirstOrDefault(d => string.Equals(d.Name, databaseName, StringComparison.Ordinal));
                mapping = database is null ? null : MappingOf(database.Id, elementId);
            }

            if (mapping?.Attributes.Any(m => string.Equals(m.Attribute, attribute.Id, StringComparison.Ordinal) && m.Ignore) == true)
                continue;
            (result ??= []).Add(attribute);
        }

        return result is null ? [] : result;
    }

    /// <summary>Invariant text of a code, for messages.</summary>
    internal static string CodeText(object? code) => Convert.ToString(code, CultureInfo.InvariantCulture) ?? "";
}
