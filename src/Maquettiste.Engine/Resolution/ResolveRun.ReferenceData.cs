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
    private List<RSeed> _seedsInOrder = [];
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
            // A reference type lives in the reference data scope, in no package.
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
    private void ResolveSeedsAndUsages()
    {
        var endOwners = new Dictionary<string, (REnd End, RRelation Relation)>(StringComparer.Ordinal);
        foreach (var relation in _relations.Values)
        {
            foreach (var end in relation.Ends)
                endOwners.TryAdd(end.Id, (end, relation));
        }

        var seedRowsById = new Dictionary<string, RSeedRow>(StringComparer.Ordinal);
        var sources = Model.All<Seed>().OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id, StringComparer.Ordinal).ToList();
        var seedsByTarget = new Dictionary<string, List<RSeed>>(StringComparer.Ordinal);
        var rowsByType = new Dictionary<string, List<RRow>>(StringComparer.Ordinal);
        foreach (var seed in sources)
        {
            Ct.ThrowIfCancellationRequested();
            var s = new RSeed();
            var deps = DepsOf(s);
            FillCommon(s, seed, deps);
            deps.Element(seed.Target);
            IResolvedObject? target = _referenceTypes.TryGetValue(seed.Target, out var rt) ? rt
                : _entities.TryGetValue(seed.Target, out var entity) && entity.Id.Length > 0 ? entity
                : _relations.GetValueOrDefault(seed.Target);
            s.Target = target;
            s.Package = (target as RElement)?.Package; // a seed of an entity or relation stays in its target's package
            s.Columns = SeedColumns(seed, target, endOwners, deps);
            var seedKeys = deps.ToList();
            var rows = new List<RSeedRow>(seed.Rows.Count);
            for (var i = 0; i < seed.Rows.Count; i++)
            {
                var row = new RSeedRow { Id = seed.Rows[i].Id, Seed = s, Order = i, Values = CellValues(s.Columns, seed.Rows[i]) };
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
            var membership = new DependencySet(Keys).Add("k:seed").Referrers(type.Id);
            foreach (var seed in seeds)
                membership.Element(seed.Id);
            type.Rows = new RList<RRow>(rows, membership.ToList());
            type.Seeds = new RList<RSeed>(seeds, [Keys.Referrers(type.Id), "k:seed"]);
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
            entity.Seeds = new RList<RSeed>(seedsByTarget.GetValueOrDefault(entity.Id) ?? [], [Keys.Referrers(entity.Id), "k:seed"]);
        foreach (var relation in _relations.Values.Concat(_promotedRelations))
            relation.Seeds = new RList<RSeed>(seedsByTarget.GetValueOrDefault(relation.Id) ?? [], [Keys.Referrers(relation.Id), "k:seed"]);

        OrderSeeds(seedRowsById);
        ResolveUsages();
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
    /// is broken at its first row in file order), and <see cref="ResolvedModel.SeedsInOrder"/> by (dependency depth, name, id).
    /// </summary>
    private void OrderSeeds(Dictionary<string, RSeedRow> seedRowsById)
    {
        var seedDeps = new Dictionary<RSeed, SortedSet<int>>(ReferenceEqualityComparer.Instance);
        var index = new Dictionary<RSeed, int>(ReferenceEqualityComparer.Instance);
        for (var i = 0; i < _seedOrder.Count; i++)
            index[_seedOrder[i]] = i;
        foreach (var seed in _seedOrder)
        {
            Ct.ThrowIfCancellationRequested();
            var rows = seed.Rows;
            var position = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
            for (var i = 0; i < rows.Count; i++)
                position.TryAdd(rows[i].Id, i);
            var dependents = new List<int>?[rows.Count];
            var pending = new int[rows.Count];
            var across = new SortedSet<int>();
            for (var i = 0; i < rows.Count; i++)
            {
                foreach (var (id, owner) in Named(rows[i], seedRowsById).Distinct())
                {
                    if (ReferenceEquals(owner, seed) && position.TryGetValue(id, out var p))
                    {
                        if (p == i)
                            continue;
                        (dependents[p] ??= []).Add(i);
                        pending[i]++;
                    }
                    else if (owner is not null && index.TryGetValue(owner, out var other))
                    {
                        across.Add(other);
                    }
                }
            }

            seedDeps[seed] = across;
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

            seed.OrderedRows = new RList<RSeedRow>(ordered, seed.Rows.MembershipKeys);
        }

        // Depth: 0 without dependencies, else one more than the deepest seed it names; a back edge of a cycle counts as none.
        var depth = new int[_seedOrder.Count];
        var state = new byte[_seedOrder.Count]; // 0 new, 1 on the path, 2 done
        int Depth(int i)
        {
            if (state[i] == 2)
                return depth[i];
            if (state[i] == 1)
                return -1;
            state[i] = 1;
            var d = 0;
            foreach (var other in seedDeps[_seedOrder[i]])
                d = Math.Max(d, Depth(other) + 1);
            state[i] = 2;
            return depth[i] = d;
        }

        for (var i = 0; i < _seedOrder.Count; i++)
            Depth(i);
        _seedsInOrder = [.. Enumerable.Range(0, _seedOrder.Count).OrderBy(i => depth[i]).ThenBy(i => i).Select(i => _seedOrder[i])];
        var keys = new DependencySet(Keys).Add("k:seed");
        foreach (var seed in _seedOrder)
            keys.Element(seed.Id);
        foreach (var type in _referenceTypeOrder)
            keys.Element(type.Id);
        _seedsInOrderKeys = keys.ToList();
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
