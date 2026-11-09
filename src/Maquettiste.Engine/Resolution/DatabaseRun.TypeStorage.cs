using System.Globalization;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Rendering;

namespace Maquettiste.Engine.Resolution;

/// <summary>
/// The storage of a relation with an end of several types in the team's designed tables (several-types-design.md section 19, decisions 37 to
/// 49): step 1 (<see cref="CollectTypeStorage"/>, in <c>ResolveRelation</c>) works out the targets, the tables that hold the links and the
/// pointers; step 2 (<see cref="FinishTypeStorage"/>, once keys and uniques are resolved) resolves the pointers, checks them (MQ4117), works
/// out the type codes, what the database can't enforce (MQ4118), the targets no pointer reaches (MQ4120) and the constants (MQ4051), and fills
/// <see cref="RRelationMapping"/> and the database's <see cref="RDatabase.RelationStorage"/>. The same step finishes a one-type relation's
/// designed link table when its mapping writes constants or attribute columns (answer 9).
/// </summary>
internal sealed partial class DatabaseRun
{
    /// <summary>A relation with an end of several types placed in this database, in relation order (step 1 records, step 2 finishes).</summary>
    private sealed record TypeStorageEntry(RRelation Relation, Relation Source, Mapping? Mapping, IReadOnlyList<TypeLinkEnd> Ends,
        IReadOnlyList<HashSet<string>> Covered, TypeLinks? Links, RRelationMapping? Result);

    private readonly List<TypeStorageEntry> _typeStorage = [];

    /// <summary>The designed link tables relation mappings name, for the shared-table check (decision 44), in relation order.</summary>
    private readonly List<(RRelation Relation, Mapping Mapping, TableBuild Table, RRelationMapping? Result, bool PartTwo)> _linkTables = [];

    private readonly HashSet<(string, string?, string)> _typeStorageRefusals = [];

    // ---- step 1 ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Step 1 for a relation with an end of several types (section 19.8): its ends' targets and left-out entities; and, when its mapping in
    /// this database states storage (a <c>junctionTable</c> or an <c>ends</c> entry, decision 49), the pairs of targets with their holding
    /// tables and pointers (decision 39), the flags on the named designed objects, and the relation mapping, filled in step 2.
    /// </summary>
    private void CollectTypeStorage(RRelation rel, Relation src)
    {
        if (rel.Ends.Count != 2 || src.Ends.Count != 2)
            return; // n-ary: MQ3041
        var m = _run.MappingOf(_db.Id, rel.Id);
        var ends = new List<TypeLinkEnd>(2);
        var covered = new List<HashSet<string>>(2);
        for (var i = 0; i < 2; i++)
        {
            var end = rel.Ends[i];
            var set = new HashSet<string>(EndCoverage.CoveredBy(_run.Model, src.Ends[i]), StringComparer.Ordinal);
            var e = new TypeLinkEnd { End = end, Entry = m?.Ends.FirstOrDefault(x => string.Equals(x.End, end.Id, StringComparison.Ordinal)) };
            CollectTargets(end, set, e.Targets);
            foreach (var id in set)
            {
                if (!_placements.ContainsKey(id) && _run.EntityById(id) is { } left)
                    e.LeftOut.Add(left);
            }

            e.LeftOut.Sort(ByQualifiedName);
            ends.Add(e);
            covered.Add(set);
        }

        // Listed for the database view when every end has a covered entity placed here, as MQ4115 counts.
        if (!covered.TrueForAll(set => set.Any(_placements.ContainsKey)))
            return;
        TypeLinks? links = null;
        RRelationMapping? result = null;
        if (m is { Ignore: false } && (m.JunctionTable is not null || m.Ends.Count > 0))
        {
            var linkTable = m.JunctionTable is { } junctionId && _tables.TryGetValue(junctionId, out var designed) && designed.IsDesigned ? designed : null;
            if ((m.JunctionTable is null || linkTable is not null) && TypeItemsFit(m, ends, covered))
            {
                links = new TypeLinks { Mapping = m, LinkTable = linkTable, Ends = ends };
                var inLinkTables = CollectPairs(rel, src, links);
                result = new RRelationMapping { Shape = inLinkTables ? "junction" : "foreign-key", JunctionTable = linkTable?.Table };
                // The physical shape stays junction with no junction table, so a reader of one-type storage finds nothing to read here.
                _relationPhysical[rel.Id] = new RelationPhysical(RelationShape.Junction) { Relation = rel, Mapping = result, TypeLinks = links };
                _run.SetRelationMapping(rel, _db.Name, result);
            }
        }

        _typeStorage.Add(new TypeStorageEntry(rel, src, m, ends, covered, links, result));
    }

    /// <summary>
    /// MQ4117 for a <c>types</c> item that names an entity the end doesn't cover, or a covered entity whose rows are in a covered base's table
    /// (decision 39); checked by the run, since carriers change. Whether every item fits.
    /// </summary>
    private bool TypeItemsFit(Mapping m, List<TypeLinkEnd> ends, List<HashSet<string>> covered)
    {
        var fits = true;
        for (var i = 0; i < ends.Count; i++)
        {
            if (ends[i].Entry is not { } entry)
                continue;
            for (var k = 0; k < entry.Types.Count; k++)
            {
                var id = entry.Types[k].Entity;
                var at = Ptr(EntryPointer(m, ends[i].End.Id) + "/types", k) + "/entity";
                var name = _run.EntityById(id)?.Name ?? id;
                if (!covered[i].Contains(id))
                {
                    Refuse(m, at, $"Type '{name}' of a types item isn't covered by end '{ends[i].End.Role}'; name one of the end's types.");
                    fits = false;
                }
                else if (_placements.TryGetValue(id, out var p) && p.Table is not null && RowsInCoveredBase(p, covered[i]) is { } holder)
                {
                    Refuse(m, at, $"Type '{name}' of end '{ends[i].End.Role}' keeps its rows in the table of '{holder.Entity.Name}', which the end also covers, so it isn't a target of its own; name '{holder.Entity.Name}' instead.");
                    fits = false;
                }
            }
        }

        return fits;
    }

    /// <summary>
    /// An end's targets in target order (decision 39): each type gives itself when it is a target, then its covered derived targets by
    /// qualified name, then id. A target is placed here, has a table, and its rows aren't in the table of a covered base.
    /// </summary>
    private void CollectTargets(REnd end, HashSet<string> covered, List<Placement> targets)
    {
        foreach (var type in end.Types)
        {
            if (IsTarget(type.Id, covered) is { } own && !targets.Contains(own))
                targets.Add(own);
            var derived = covered.Where(id => !string.Equals(id, type.Id, StringComparison.Ordinal) && DerivesFrom(id, type))
                .Select(id => IsTarget(id, covered)).OfType<Placement>().Where(p => !targets.Contains(p)).ToList();
            derived.Sort((a, b) => ByQualifiedName(a.Entity, b.Entity));
            targets.AddRange(derived);
        }
    }

    private Placement? IsTarget(string entityId, HashSet<string> covered)
    {
        if (!_placements.TryGetValue(entityId, out var p) || p.Table is null)
            return null;
        return RowsInCoveredBase(p, covered) is null ? p : null;
    }

    /// <summary>The covered base whose table holds a placement's rows (TPH or TPT, or a base bound to the same table), or <see langword="null"/>.</summary>
    private static Placement? RowsInCoveredBase(Placement p, HashSet<string> covered)
    {
        for (var b = p.Base; b is not null && !ReferenceEquals(b, p); b = b.Base)
        {
            if (covered.Contains(b.Entity.Id) && b.Table is not null
                && (ReferenceEquals(b.Table, p.Table) || SharesBaseRows(p)))
                return b;
        }

        return null;
    }

    /// <summary>
    /// Whether a placement's rows are also in its base's table: TPT (the base holds the base row, also for designed tables bound with it), or
    /// TPH for a table the engine makes; a bound TPH entity shares its base's rows only through the same table.
    /// </summary>
    private static bool SharesBaseRows(Placement p) => p.Strategy == InheritanceStrategy.Tpt || (!p.Bound && p.Strategy == InheritanceStrategy.Tph);

    private bool DerivesFrom(string entityId, REntity type)
    {
        for (var e = _run.EntityById(entityId)?.Base; e is not null; e = e.Base)
        {
            if (ReferenceEquals(e, type))
                return true;
        }

        return false;
    }

    private static int ByQualifiedName(REntity a, REntity b)
    {
        // The qualified name: the package's, then the entity's name (as the model's entity order).
        var c = string.CompareOrdinal(a.Package?.QualifiedName ?? "", b.Package?.QualifiedName ?? "");
        if (c == 0)
            c = string.CompareOrdinal(a.Name, b.Name);
        return c != 0 ? c : string.CompareOrdinal(a.Id, b.Id);
    }

    /// <summary>The pairs of targets with their holding tables and pointers (decision 39); whether the links live in link tables.</summary>
    private bool CollectPairs(RRelation rel, Relation src, TypeLinks links)
    {
        var m = links.Mapping;
        var (a, b) = (links.Ends[0], links.Ends[1]);
        // An any-entity end: no storage that grows with the model (decision 43).
        foreach (var e in links.Ends)
        {
            if (!e.End.AnyEntity)
                continue;
            var other = ReferenceEquals(e, a) ? b : a;
            if (e.Entry is { } entry && entry.Types.Any(t => t.ForeignKey is not null))
            {
                Refuse(m, EntryPointer(m, e.End.Id) + "/types",
                    $"End '{e.End.Role}' is any entity, so it can't have a key per type: every new entity would need a column. Use a type and an id, or an id alone.");
                return false;
            }

            if (e.Entry is null && links.LinkTable is null && other.Entry is { ForeignKeys.Count: > 0 })
            {
                Refuse(m, EntryPointer(m, other.End.Id) + "/foreignKeys",
                    $"End '{e.End.Role}' is any entity, so its types' tables can't each hold a key: every new entity would need a column. Use a type and an id, or an id alone.");
                return false;
            }
        }

        // Two items of foreignKeys owned by one table.
        foreach (var e in links.Ends)
        {
            if (e.Entry is not { ForeignKeys.Count: > 1 } entry)
                continue;
            var hosts = new Dictionary<TableBuild, string>(ReferenceEqualityComparer.Instance);
            for (var k = 0; k < entry.ForeignKeys.Count; k++)
            {
                if (!_foreignKeysById.TryGetValue(entry.ForeignKeys[k], out var spec))
                    continue;
                if (hosts.TryGetValue(spec.Host, out var first))
                {
                    Refuse(m, Ptr(EntryPointer(m, e.End.Id) + "/foreignKeys", k),
                        $"Foreign keys '{first}' and '{spec.Name ?? spec.Id}' of end '{e.End.Role}' both belong to table '{spec.Host.Table.Name}'; name one key per table that holds links.");
                    return false;
                }

                hosts[spec.Host] = spec.Name ?? spec.Id ?? "";
            }
        }

        if (links.LinkTable is { } linkTable)
        {
            if (links.Ends.Any(e => e.Entry is null))
                return true; // MQ4011
            MarkLinkTable(rel, links.Mapping, linkTable);
            foreach (var ta in a.Targets)
            {
                foreach (var tb in b.Targets)
                    AddPair(rel, links, linkTable, (a, ta), (b, tb), holder: null, link: true);
            }

            return true;
        }

        var noEntry = links.Ends.Where(e => e.Entry is null).ToList();
        if (noEntry.Count == 1)
        {
            var h = noEntry[0];
            var o = ReferenceEquals(h, a) ? b : a;
            if (o.End.IsMany)
            {
                Refuse(m, EntryPointer(m, o.End.Id),
                    $"The rows of end '{h.End.Role}' would hold the links of relation '{rel.Name}' in database '{_db.Name}', but a row holds one link and end '{o.End.Role}' is to-many; keep the links in a link table ('junctionTable').");
                return false;
            }

            var holderTables = h.Targets.Select(t => t.Table!).Distinct().ToList();
            if (holderTables.Count > 1 && (o.Entry!.ForeignKey is not null || o.Entry.IdColumn is not null))
            {
                Refuse(m, EntryPointer(m, o.End.Id),
                    $"The targets of end '{h.End.Role}' live in tables {Names(holderTables.Select(t => t.Table.Name))}, but end '{o.End.Role}' is pointed at by one column; name a key per table with 'foreignKeys', or keep the links in a link table. Column pointers spread over several tables wait.");
                return false;
            }

            foreach (var ta in a.Targets)
            {
                foreach (var tb in b.Targets)
                {
                    var holding = (ReferenceEquals(h, a) ? ta : tb).Table!;
                    AddPair(rel, links, holding, (a, ta), (b, tb), holder: h, link: false);
                }
            }

            return false;
        }

        if (noEntry.Count == 0)
        {
            var typed = links.Ends.Where(e => e.Entry!.Types.Any(t => t.ForeignKey is not null)).ToList();
            if (typed.Count == 0)
            {
                Refuse(m, "/ends", a.End.IsMany && b.End.IsMany
                    ? $"Relation '{rel.Name}' is many to many, so its links need a link table: name 'junctionTable', or a key per type of one end in tables of their own."
                    : $"Every end of relation '{rel.Name}' has an entry and there is no 'junctionTable', so no table holds its links in database '{_db.Name}'; name a link table, or leave out the entry of the end whose rows hold the links.");
                return false;
            }

            if (typed.Count == 2)
            {
                Refuse(m, "/ends",
                    $"Both ends of relation '{rel.Name}' have a key per type and there is no 'junctionTable'; a link table per pair of types waits. Name one link table.");
                return false;
            }

            var p = typed[0];
            foreach (var ta in a.Targets)
            {
                foreach (var tb in b.Targets)
                {
                    var pt = ReferenceEquals(p, a) ? ta : tb;
                    var item = p.Entry!.Types.FirstOrDefault(t => string.Equals(t.Entity, pt.Entity.Id, StringComparison.Ordinal) && t.ForeignKey is not null);
                    if (item is null || !_foreignKeysById.TryGetValue(item.ForeignKey!, out var spec))
                    {
                        NotStored(p, pt);
                        continue;
                    }

                    MarkLinkTable(rel, links.Mapping, spec.Host);
                    AddPair(rel, links, spec.Host, (a, ta), (b, tb), holder: null, link: true);
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>Sets the flags a designed link table and its relation get (section 19.8), and lists it for the shared-table check.</summary>
    private void MarkLinkTable(RRelation rel, Mapping mapping, TableBuild table)
    {
        if (_linkTables.Exists(x => ReferenceEquals(x.Table, table) && ReferenceEquals(x.Relation, rel)))
            return;
        table.Table.Relation ??= rel;
        table.Table.IsJunction = true;
        table.Deps.AddRange(_run.DepsOf(rel).ToList());
        _linkTables.Add((rel, mapping, table, null, true));
    }

    private void AddPair(RRelation rel, TypeLinks links, TableBuild holding, (TypeLinkEnd End, Placement Target) a, (TypeLinkEnd End, Placement Target) b,
        TypeLinkEnd? holder, bool link)
    {
        var pa = PointerOf(rel, links, holding, a.End, a.Target, b, holder, link);
        var pb = PointerOf(rel, links, holding, b.End, b.Target, a, holder, link);
        if (pa is not null && pb is not null)
            links.Pairs.Add(new TypeLink { Table = holding, Ends = [pa, pb] });
    }

    /// <summary>
    /// How the holding table points at one end's target (decision 39); <see langword="null"/> when nothing points at it (MQ4120 lists the
    /// target) or the entry contradicts itself (MQ4117).
    /// </summary>
    private TypeLinkPointer? PointerOf(RRelation rel, TypeLinks links, TableBuild holding, TypeLinkEnd e, Placement target,
        (TypeLinkEnd End, Placement Target) other, TypeLinkEnd? holder, bool link)
    {
        if (ReferenceEquals(e, holder))
            return new TypeLinkPointer { End = e.End, Target = target, IsHolder = true };
        var m = links.Mapping;
        var entry = e.Entry!;
        var at = EntryPointer(m, e.End.Id);
        ForeignKeySpec? spec = null;
        string? specPointer = null;
        if (entry.ForeignKey is { } foreignKeyId)
        {
            if (!_foreignKeysById.TryGetValue(foreignKeyId, out spec))
                return null; // MQ4009
            specPointer = at + "/foreignKey";
        }
        else if (entry.ForeignKeys.Count > 0)
        {
            if (links.LinkTable is not null)
                return null; // MQ4009
            var k = -1;
            for (var j = 0; j < entry.ForeignKeys.Count && k < 0; j++)
            {
                if (_foreignKeysById.TryGetValue(entry.ForeignKeys[j], out var item) && ReferenceEquals(item.Host, holding))
                    (k, spec) = (j, item);
            }

            if (spec is null)
            {
                // No key of the list is in the table: the target whose table (or link table) it is isn't reached.
                NotStored(other.End, other.Target);
                return null;
            }

            specPointer = Ptr(at + "/foreignKeys", k);
        }
        else if (entry.Types.Any(t => t.ForeignKey is not null))
        {
            var k = IndexOfType(entry, target.Entity.Id, key: true);
            if (k < 0 || !_foreignKeysById.TryGetValue(entry.Types[k].ForeignKey!, out spec))
            {
                NotStored(e, target);
                return null;
            }

            specPointer = Ptr(at + "/types", k) + "/foreignKey";
        }

        if (spec is not null)
        {
            if (!ReferenceEquals(spec.Host, holding))
            {
                Refuse(m, specPointer,
                    $"Foreign key '{spec.Name ?? spec.Id}' of table '{spec.Host.Table.Name}' points at end '{e.End.Role}', but the links of '{target.Entity.Name}' there are held in table '{holding.Table.Name}'; name a key of that table.");
                return null;
            }

            spec.Result.Relation ??= rel;
            spec.Result.End ??= link ? e.End : holder?.End ?? other.End.End;
            spec.Host.Deps.AddRange(_run.DepsOf(rel).ToList());
            return new TypeLinkPointer { End = e.End, Target = target, ForeignKey = spec };
        }

        if (entry.IdColumn is not { } idColumn)
            return null; // MQ4009: no way
        if (holding.Resolve(idColumn) is null)
        {
            Refuse(m, at + "/idColumn", $"Column '{NamedColumn(idColumn)}' named by end '{e.End.Role}' isn't in table '{holding.Table.Name}', which holds the links of '{target.Entity.Name}'.");
            return null;
        }

        if (entry.TypeColumn is { } typeColumn && holding.Resolve(typeColumn) is null)
        {
            Refuse(m, at + "/typeColumn", $"Column '{NamedColumn(typeColumn)}' named by end '{e.End.Role}' isn't in table '{holding.Table.Name}', which holds the links of '{target.Entity.Name}'.");
            return null;
        }

        var refers = IndexOfType(entry, target.Entity.Id, key: false);
        return new TypeLinkPointer
        {
            End = e.End, Target = target, IdColumn = idColumn, TypeColumn = entry.TypeColumn,
            ReferencesColumn = refers >= 0 ? entry.Types[refers].ReferencesColumn : entry.ReferencesColumn,
        };
    }

    /// <summary>A column's name as its table file writes it, for a column the mapping names by id; the id when no file declares it.</summary>
    private string NamedColumn(string columnId) =>
        _run.Model.GetDocument(columnId)?.Element is Table table && table.Columns.FirstOrDefault(c => string.Equals(c.Id, columnId, StringComparison.Ordinal)) is { } column
            ? column.Name
            : columnId;

    private static int IndexOfType(RelationEndMapping entry, string entityId, bool key)
    {
        for (var k = 0; k < entry.Types.Count; k++)
        {
            var item = entry.Types[k];
            if (string.Equals(item.Entity, entityId, StringComparison.Ordinal) && (key ? item.ForeignKey is not null : item.ReferencesColumn is not null))
                return k;
        }

        return -1;
    }

    private static void NotStored(TypeLinkEnd e, Placement target)
    {
        if (!e.NotStored.Contains(target))
            e.NotStored.Add(target);
    }

    private static string EntryPointer(Mapping m, string endId)
    {
        for (var i = 0; i < m.Ends.Count; i++)
        {
            if (string.Equals(m.Ends[i].End, endId, StringComparison.Ordinal))
                return Ptr("/ends", i);
        }

        return "/ends";
    }

    private static string Ptr(string pointer, int index) => pointer + "/" + index.ToString(CultureInfo.InvariantCulture);

    private static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => "'" + n + "'"));

    /// <summary>MQ4117 on a relation mapping, once per pointer and text.</summary>
    private void Refuse(Mapping m, string? pointer, string message)
    {
        if (_typeStorageRefusals.Add((m.Id, pointer, message)))
            _run.AddDiagnostic("MQ4117", message, m.Id, pointer);
    }

    /// <summary>A one-type relation's designed link table whose mapping writes constants or attribute columns (answer 9), finished in step 2.</summary>
    private void CollectLinkTableExtras(RRelation rel, Mapping m, TableBuild designed, RRelationMapping result)
    {
        var index = _linkTables.FindIndex(x => ReferenceEquals(x.Table, designed) && ReferenceEquals(x.Relation, rel));
        if (index >= 0)
            _linkTables[index] = (rel, m, designed, result, _linkTables[index].PartTwo);
        else
            _linkTables.Add((rel, m, designed, result, m.Constants.Count > 0));
    }

    // ---- step 2 ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Step 2 (section 19.8), after <c>FinishTables</c>: the pointers' referenced tables and columns and their checks (MQ4117), the codes
    /// (decision 42), the constants (MQ4051) and attribute columns, what the database can't enforce (MQ4118), the targets no pointer reaches
    /// (MQ4120), the relation mappings' members and dependency keys, and the database's <see cref="RDatabase.RelationStorage"/>.
    /// </summary>
    private void FinishTypeStorage()
    {
        // One-type link tables: their constants and attribute columns (answer 9).
        foreach (var (rel, m, table, result, _) in _linkTables)
        {
            if (result is null || _typeStorage.Exists(s => ReferenceEquals(s.Relation, rel)))
                continue;
            result.Constants = Constants(rel, m, table);
            result.Attributes = AttributeColumns(rel, m, [table], link: true);
        }

        CheckSharedLinkTables();
        if (_typeStorage.Count == 0)
            return;
        var notes = new List<RelationStorageNote>(_typeStorage.Count);
        foreach (var s in _typeStorage)
            notes.Add(FinishOne(s));
        _rdb.RelationStorage = notes;
    }

    private RelationStorageNote FinishOne(TypeStorageEntry s)
    {
        var leftOut = new List<REndEntity>();
        foreach (var e in s.Ends)
            leftOut.AddRange(e.LeftOut.Select(x => new REndEntity { End = e.End, Entity = x }));
        if (s.Links is not { } links || s.Result is not { } result)
            return new RelationStorageNote { Relation = s.Relation, MappingId = s.Mapping?.Id, LeftOut = leftOut };

        var m = links.Mapping;
        var resolved = new List<RRelationLink>();
        foreach (var pair in links.Pairs)
        {
            var ends = new RRelationLinkEnd[pair.Ends.Count];
            var ok = true;
            for (var i = 0; i < pair.Ends.Count && ok; i++)
            {
                pair.Ends[i].Result = ResolvePointer(links, pair, pair.Ends[i]);
                ok = pair.Ends[i].Result is not null;
                if (ok)
                    ends[i] = pair.Ends[i].Result!;
            }

            if (!ok)
                continue;
            pair.Result = new RRelationLink { Table = pair.Table.Table, Ends = ends };
            resolved.Add(pair.Result);
        }

        var typeCodes = FillCodes(s, links);
        foreach (var e in s.Ends)
            e.NotStored.Sort((x, y) => e.Targets.IndexOf(x).CompareTo(e.Targets.IndexOf(y)));
        var notStored = new List<REndEntity>();
        foreach (var e in s.Ends)
        {
            foreach (var target in e.NotStored)
            {
                notStored.Add(new REndEntity { End = e.End, Entity = target.Entity });
                _run.AddDiagnostic("MQ4120",
                    $"Relation '{s.Relation.Name}' covers entity '{target.Entity.Name}' in database '{_db.Name}', but its mapping names nothing that points at it there, so links to a {target.Entity.Name} aren't stored.",
                    m.Id, e.Entry is null ? "/ends" : EntryPointer(m, e.End.Id));
            }
        }

        var holding = links.Pairs.Where(p => p.Result is not null).Select(p => p.Table).Distinct().ToList();
        result.Links = resolved;
        if (links.LinkTable is { } linkTable)
            result.Constants = Constants(s.Relation, m, linkTable);
        result.Attributes = AttributeColumns(s.Relation, m, links.LinkTable is { } lt ? [lt] : holding, link: links.LinkTable is not null);
        result.LeftOut = leftOut;
        result.NotStored = notStored;
        result.TypeCodes = typeCodes;
        var gaps = Gaps(s, links, result);
        AddTypeStorageDeps(s, links);
        return new RelationStorageNote
        {
            Relation = s.Relation, MappingId = m.Id, Mapping = result, Tables = [.. holding.Select(t => t.Table)], LeftOut = leftOut, NotStored = notStored,
            Gaps = gaps,
        };
    }

    /// <summary>A pointer resolved against the finished tables (decisions 41 and 43), or <see langword="null"/> with MQ4117.</summary>
    private RRelationLinkEnd? ResolvePointer(TypeLinks links, TypeLink pair, TypeLinkPointer p)
    {
        var r = new RRelationLinkEnd { End = p.End, Type = p.Target.Entity, IsHolder = p.IsHolder };
        if (p.IsHolder)
            return r;
        var m = links.Mapping;
        var at = EntryPointer(m, p.End.Id);
        var target = p.Target;
        if (p.ForeignKey is { } spec)
        {
            if (!spec.Resolved)
                return null; // MQ4008
            var fk = spec.Result;
            if (!HoldsRowsOf(fk.ReferencedTable, target))
            {
                Refuse(m, at, p.End.HasSeveralTypes && links.Ends.First(e => ReferenceEquals(e.End, p.End)).Entry?.ForeignKey is not null
                    ? $"Foreign key '{fk.Name}' of table '{spec.Host.Table.Name}' points at end '{p.End.Role}', but it refers to table '{fk.ReferencedTable.Name}', which doesn't hold {target.Entity.Name} rows; a single key at an end with several types refers to a table that holds the rows of every type."
                    : $"Foreign key '{fk.Name}' of table '{spec.Host.Table.Name}' is named for type '{target.Entity.Name}' of end '{p.End.Role}', but it refers to table '{fk.ReferencedTable.Name}', which doesn't hold {target.Entity.Name} rows.");
                return null;
            }

            r.Columns = fk.Columns;
            r.ReferencedTable = fk.ReferencedTable;
            r.ReferencedColumns = fk.ReferencedColumns;
            r.ForeignKey = fk;
            return r;
        }

        var column = pair.Table.Resolve(p.IdColumn!)!;
        var targetTable = target.Table!;
        var entry = links.Ends.First(e => ReferenceEquals(e.End, p.End));
        if (entry.Entry?.ReferencesColumn is not null && entry.Targets.Select(t => t.Table).Distinct().Count() > 1)
        {
            Refuse(m, at + "/referencesColumn",
                $"End '{p.End.Role}' names one 'referencesColumn', but its targets live in tables {Names(entry.Targets.Select(t => t.Table!.Table.Name).Distinct())}; name the column per type in 'types' items.");
            return null;
        }

        RColumn? referenced;
        if (p.ReferencesColumn is { } named)
        {
            referenced = targetTable.Resolve(named);
            if (referenced is null)
            {
                Refuse(m, at, $"Column '{NamedColumn(named)}' that end '{p.End.Role}' refers to for type '{target.Entity.Name}' isn't a column of table '{targetTable.Table.Name}'.");
                return null;
            }
        }
        else if (targetTable.Table.PrimaryKey is { Columns.Count: 1 } pk)
        {
            referenced = pk.Columns[0];
        }
        else
        {
            var what = targetTable.Table.PrimaryKey is { } several
                ? $"a primary key of {several.Columns.Count.ToString(CultureInfo.InvariantCulture)} columns"
                : "no primary key";
            Refuse(m, at + "/idColumn",
                $"Table '{targetTable.Table.Name}' of type '{target.Entity.Name}' has {what}, so column '{column.Name}' of end '{p.End.Role}' needs 'referencesColumn' naming the unique column it refers to.");
            return null;
        }

        if (!IsUniqueColumn(targetTable.Table, referenced))
        {
            Refuse(m, at, $"Column '{column.Name}' of table '{pair.Table.Table.Name}' refers to column '{referenced.Name}' of table '{targetTable.Table.Name}', which is neither its primary key nor a unique key.");
            return null;
        }

        if (!SamePhysicalType(column, referenced))
        {
            Refuse(m, at + "/idColumn",
                $"Column '{column.Name}' of table '{pair.Table.Table.Name}' is {DescribeColumn(column)}, but it refers to column '{referenced.Name}' of table '{targetTable.Table.Name}', which is {DescribeColumn(referenced)}; make the types match.");
            return null;
        }

        r.Columns = [column];
        r.ReferencedTable = targetTable.Table;
        r.ReferencedColumns = [referenced];
        r.TypeColumn = p.TypeColumn is { } typeColumn ? pair.Table.Resolve(typeColumn) : null;
        return r;
    }

    private static string DescribeColumn(RColumn c) =>
        c.Length is { } length ? $"{c.Type}({length.ToString(CultureInfo.InvariantCulture)})"
        : string.IsNullOrEmpty(c.NativeType) || string.Equals(c.NativeType, c.Type, StringComparison.OrdinalIgnoreCase) ? c.Type : c.Type + " (" + c.NativeType + ")";

    /// <summary>Whether a table holds a target's rows: its own table, or the table of a base that holds them (decision 41).</summary>
    private static bool HoldsRowsOf(RTable table, Placement target)
    {
        if (ReferenceEquals(target.Table?.Table, table))
            return true;
        for (var b = target.Base; b is not null && !ReferenceEquals(b, target); b = b.Base)
        {
            if (ReferenceEquals(b.Table?.Table, table) && (ReferenceEquals(b.Table, target.Table) || SharesBaseRows(target)))
                return true;
        }

        return false;
    }

    /// <summary>Whether a column is unique in its table: the one-column primary key, a unique key, or a unique index without a filter (not on Oracle).</summary>
    private bool IsUniqueColumn(RTable table, RColumn column) =>
        (table.PrimaryKey is { Columns.Count: 1 } pk && ReferenceEquals(pk.Columns[0], column))
        || table.Uniques.Any(u => u.Columns.Count == 1 && ReferenceEquals(u.Columns[0], column))
        || (_db.Dialect != Dialect.Oracle && table.Indexes.Any(i => i.Unique && i.Where is null && i.Columns.Count == 1 && ReferenceEquals(i.Columns[0].Column, column)));

    // ---- codes -------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The type code of an entity in this database (decision 42): its binding's <c>discriminatorValue</c>, else its mapping's, else its name;
    /// read from the files. A number is an integer code.
    /// </summary>
    private object CodeOf(REntity entity)
    {
        System.Text.Json.JsonElement? value = _bindingByEntity.TryGetValue(entity.Id, out var bound) ? bound.Binding.DiscriminatorValue : _run.MappingOf(_db.Id, entity.Id)?.DiscriminatorValue;
        return ResolutionValues.Plain(value) switch
        {
            string text => text,
            long number => number,
            null => entity.Name,
            var other => Convert.ToString(other, CultureInfo.InvariantCulture) ?? entity.Name,
        };
    }

    private static string CodeText(object code) => code is long l ? l.ToString(CultureInfo.InvariantCulture) : (string)code;

    /// <summary>
    /// The entities a target stands for at an end: itself and the covered placed entities whose rows are in its table (decision 48), in
    /// placement order.
    /// </summary>
    private List<Placement> StandsFor(TypeLinkEnd e, HashSet<string> covered, Placement target)
    {
        var list = new List<Placement> { target };
        foreach (var p in _placementOrder)
        {
            if (ReferenceEquals(p, target) || !covered.Contains(p.Entity.Id) || e.Targets.Contains(p))
                continue;
            var b = RowsInCoveredBase(p, covered);
            while (b is not null && !e.Targets.Contains(b))
                b = RowsInCoveredBase(b, covered);
            if (ReferenceEquals(b, target))
                list.Add(p);
        }

        return list;
    }

    /// <summary>The codes each type column holds (decision 42), with MQ4117 for collisions and codes the column can't hold; the mapping's type codes.</summary>
    private List<RTypeCode> FillCodes(TypeStorageEntry s, TypeLinks links)
    {
        var all = new Dictionary<string, RTypeCode>(StringComparer.Ordinal);
        var m = links.Mapping;
        for (var i = 0; i < s.Ends.Count; i++)
        {
            var e = s.Ends[i];
            var held = new Dictionary<RColumn, Dictionary<string, REntity>>(ReferenceEqualityComparer.Instance);
            foreach (var pair in links.Pairs)
            {
                if (pair.Result is null || pair.Ends[i].Result is not { TypeColumn: { } typeColumn } r)
                    continue;
                var integer = PhysicalTypeOf(typeColumn) is "int16" or "int32" or "int64";
                var codes = new List<object>();
                foreach (var p in StandsFor(e, s.Covered[i], pair.Ends[i].Target))
                {
                    if (p.Entity.IsAbstract)
                        continue;
                    var code = CodeOf(p.Entity);
                    all.TryAdd(p.Entity.Id, new RTypeCode { Entity = p.Entity, Code = code });
                    codes.Add(integer ? code : CodeText(code));
                    if (!held.TryGetValue(typeColumn, out var byCode))
                        held[typeColumn] = byCode = new Dictionary<string, REntity>(StringComparer.Ordinal);
                    var text = CodeText(code);
                    if (byCode.TryGetValue(text, out var first) && !ReferenceEquals(first, p.Entity))
                    {
                        Refuse(m, EntryPointer(m, e.End.Id) + "/typeColumn",
                            $"Types '{first.Name}' and '{p.Entity.Name}' both have code '{text}' in database '{_db.Name}', so column '{typeColumn.Name}' can't tell them apart; set discriminatorValue on one.");
                    }

                    byCode.TryAdd(text, p.Entity);
                    if (CodeMisfit(typeColumn, code) is { } misfit)
                        Refuse(m, EntryPointer(m, e.End.Id) + "/typeColumn", $"Column '{typeColumn.Name}' of table '{typeColumn.Table.Name}' holds the type codes of end '{e.End.Role}', but {misfit}.");
                }

                codes.Sort((x, y) => string.CompareOrdinal(CodeText(x), CodeText(y)));
                r.Codes = codes.Distinct().ToList();
            }
        }

        var list = all.Values.ToList();
        list.Sort((x, y) => ByQualifiedName(x.Entity, y.Entity));
        return list;
    }

    private static string PhysicalTypeOf(RColumn c) => c.Type == "reference" && c.CodeType is { } code ? code : c.Type;

    private static string? CodeMisfit(RColumn column, object code)
    {
        var type = PhysicalTypeOf(column);
        if (type is "int16" or "int32" or "int64")
            return code is long ? null : $"code '{CodeText(code)}' isn't an integer; set an integer discriminatorValue";
        if (type is not ("string" or "text"))
            return $"it is a {type} column; a type column holds text or integers";
        var text = CodeText(code);
        return column.Length is { } length && text.Length > length
            ? $"code '{text}' is {text.Length.ToString(CultureInfo.InvariantCulture)} characters long, over its {length.ToString(CultureInfo.InvariantCulture)}"
            : null;
    }

    // ---- constants and attribute columns ----------------------------------------------------------------------------------

    /// <summary>A link table's constants (decision 44), each with its one value, with MQ4051 for a value its column can't hold.</summary>
    private List<RColumnCondition> Constants(RRelation rel, Mapping m, TableBuild table)
    {
        var list = new List<RColumnCondition>();
        for (var i = 0; i < m.Constants.Count; i++)
        {
            var constant = m.Constants[i];
            if (table.Resolve(constant.Column) is not { } column)
                continue; // MQ4009
            var value = ResolutionValues.Plain(constant.Value);
            if (FitProblem(value, PhysicalTypeOf(column), column.Length, column.Nullable) is { } problem)
            {
                _run.AddDiagnostic("MQ4051",
                    $"The constant for column '{column.Name}' of the mapping of relation '{rel.Name}' to database '{_db.Name}' {problem} (the column is {PhysicalTypeOf(column)}).",
                    m.Id, Ptr("/constants", i) + "/value");
            }

            list.Add(new RColumnCondition { Column = column, Values = [value] });
        }

        return list;
    }

    /// <summary>
    /// The relation attributes' columns (decision 45): in the link table, where the column gets the attribute; or in the one end's table that
    /// holds every link, where it keeps its meaning. MQ4117 when the column isn't there or the links live in several tables.
    /// </summary>
    private List<RRelationAttributeColumn> AttributeColumns(RRelation rel, Mapping m, IReadOnlyList<TableBuild> tables, bool link)
    {
        var list = new List<RRelationAttributeColumn>();
        for (var i = 0; i < m.Attributes.Count; i++)
        {
            var item = m.Attributes[i];
            if (item.Column is not { } columnId || rel.Attributes.FirstOrDefault(a => string.Equals(a.Id, item.Attribute, StringComparison.Ordinal)) is not { } attribute)
                continue;
            var at = Ptr("/attributes", i) + "/column";
            if (tables.Count > 1)
            {
                Refuse(m, at, $"The links of relation '{rel.Name}' live in tables {Names(tables.Select(t => t.Table.Name))}, so one column can't hold attribute '{attribute.Name}'; a column per table waits.");
                continue;
            }

            if (tables.Count == 0)
                continue;
            if (tables[0].Resolve(columnId) is not { } column)
            {
                if (!link)
                    Refuse(m, at, $"Column '{NamedColumn(columnId)}' for attribute '{attribute.Name}' isn't in table '{tables[0].Table.Name}', which holds the links of relation '{rel.Name}'.");
                continue; // MQ4009 for a link table
            }

            if (link && column.Attribute is null)
            {
                column.Attribute = attribute;
                column.AttributePath = attribute.Id;
            }

            list.Add(new RRelationAttributeColumn { Attribute = attribute, Column = column });
        }

        return list;
    }

    /// <summary>
    /// MQ4117 when two relation mappings keep their links in one table with the same constants, so each would read the other's (decision 44);
    /// only when one of them writes constants or describes a relation with an end of several types.
    /// </summary>
    private void CheckSharedLinkTables()
    {
        for (var j = 1; j < _linkTables.Count; j++)
        {
            var later = _linkTables[j];
            for (var i = 0; i < j; i++)
            {
                var earlier = _linkTables[i];
                if (!ReferenceEquals(earlier.Table, later.Table) || ReferenceEquals(earlier.Relation, later.Relation)
                    || !(earlier.PartTwo || later.PartTwo || earlier.Mapping.Constants.Count > 0 || later.Mapping.Constants.Count > 0)
                    || !string.Equals(ConstantsKey(earlier.Mapping), ConstantsKey(later.Mapping), StringComparison.Ordinal))
                    continue;
                Refuse(later.Mapping, later.Mapping.Constants.Count > 0 ? "/constants" : "/junctionTable",
                    $"Relations '{earlier.Relation.Name}' and '{later.Relation.Name}' both keep their links in table '{later.Table.Table.Name}' of database '{_db.Name}' with the same constants, so each would read the other's links; give each its own 'constants'.");
                break;
            }
        }
    }

    private static string ConstantsKey(Mapping m) =>
        string.Join('\n', m.Constants.Select(c => c.Column + "=" + (c.Value is { } v ? v.GetRawText() : "null")).Order(StringComparer.Ordinal));

    // ---- what the database can't enforce (MQ4118) -----------------------------------------------------------------------

    /// <summary>The gaps of each end the holding tables point at (decision 47), with one MQ4118 per end that has some.</summary>
    private List<StorageGap> Gaps(TypeStorageEntry s, TypeLinks links, RRelationMapping result)
    {
        var gaps = new List<StorageGap>();
        var m = links.Mapping;
        var oneToOne = !s.Relation.Ends[0].IsMany && !s.Relation.Ends[1].IsMany;
        var linkTables = new HashSet<TableBuild>(links.Pairs.Where(p => p.Ends.All(x => !x.IsHolder)).Select(p => p.Table), ReferenceEqualityComparer.Instance);
        var linkUniqueDone = false;
        for (var i = 0; i < s.Ends.Count; i++)
        {
            var e = s.Ends[i];
            var pointers = links.Pairs.Where(p => p.Result is not null && !p.Ends[i].IsHolder).Select(p => (Pair: p, Pointer: p.Ends[i].Result!)).ToList();
            var clauses = new List<string>();
            var texts = new List<string>();
            var kinds = new List<StorageGap>();
            void Gap(string kind, string clause, string? text = null, string? lead = null)
            {
                kinds.Add(new StorageGap(e.End, kind, text));
                clauses.Add(clause);
                if (text is not null)
                    texts.Add(lead + ", add: " + text);
            }

            if (pointers.Count > 0)
            {
                var targets = Names(pointers.Select(x => x.Pointer.Type.Name).Distinct());
                if (pointers.Exists(x => x.Pointer.ForeignKey is null))
                {
                    var rule = e.End.OnDelete != "none" ? $", nor apply the delete rule '{e.End.OnDelete}'" : "";
                    var cascade = s.Relation.RelationKind == "composition" ? ", nor a composition's cascade" : "";
                    Gap(StorageGapKinds.NoForeignKey, $"it can't refuse a link to a missing {targets.Replace("', '", "' or '", StringComparison.Ordinal)}{rule}{cascade}");
                }

                if (e.End.HasSeveralTypes && e.Entry?.ForeignKey is not null && pointers.Count > 0)
                    Gap(StorageGapKinds.SharedTable, $"it can't refuse a key to a row of table '{pointers[0].Pointer.ReferencedTable!.Name}' whose type end '{e.End.Role}' doesn't cover");
                if (e.End.HasSeveralTypes && pointers.Exists(x => x.Pointer.ForeignKey is null && x.Pointer.TypeColumn is null))
                    Gap(StorageGapKinds.IdOnly, "it can't check the type an id points at");

                foreach (var (column, codes) in pointers.Where(x => x.Pointer.TypeColumn is not null).GroupBy(x => x.Pointer.TypeColumn!)
                    .Select(g => (g.Key, g.SelectMany(x => x.Pointer.Codes).Distinct().OrderBy(CodeText, StringComparer.Ordinal).ToList())))
                {
                    if (HasCheckOn(column.Table, [column]))
                        continue;
                    var name = Render(_conv.CheckName, ("table", column.Table.Name), ("name", e.End.Role));
                    var text = $"CONSTRAINT {Q(name)} CHECK ({Q(column.Name)} IN ({string.Join(", ", codes.Select(c => SqlDialects.Literal(c, _db.Dialect)))}))";
                    Gap(StorageGapKinds.TypeCheck, $"it finds no check that names column '{column.Name}' of table '{column.Table.Name}'", text, "To check the codes");
                }

                var keyed = e.Entry?.Types.Any(t => t.ForeignKey is not null) == true;
                if (keyed)
                {
                    foreach (var group in pointers.GroupBy(x => x.Pair.Table).Where(g => g.Select(x => x.Pointer.ForeignKey).Distinct().Count() > 1))
                    {
                        var table = group.Key.Table;
                        var firsts = group.Select(x => x.Pointer.Columns[0]).Distinct().ToList();
                        if (HasCheckOn(table, firsts))
                            continue;
                        var name = Render(_conv.CheckName, ("table", table.Name), ("name", e.End.Role));
                        var sum = string.Join(" + ", firsts.Select(c => $"CASE WHEN {Q(c.Name)} IS NOT NULL THEN 1 ELSE 0 END"));
                        var text = $"CONSTRAINT {Q(name)} CHECK (({sum}) {(e.End.Min == 1 ? "= 1" : "<= 1")})";
                        Gap(StorageGapKinds.CountCheck, $"it finds no check on how many of columns {Names(firsts.Select(c => c.Name))} of table '{table.Name}' are set", text, "To check how many are set");
                    }
                }

                if (oneToOne)
                {
                    foreach (var x in pointers.Where(x => !linkTables.Contains(x.Pair.Table)).DistinctBy(x => x.Pointer.Columns.Count > 0 ? x.Pointer.Columns[0] : null))
                    {
                        List<RColumn> columns = x.Pointer.TypeColumn is { } tc ? [tc, .. x.Pointer.Columns] : [.. x.Pointer.Columns];
                        if (HasUniqueOn(x.Pair.Table.Table, columns))
                            continue;
                        Gap(StorageGapKinds.OneToOneUnique, $"it finds no unique on {Names(columns.Select(c => c.Name))} of table '{x.Pair.Table.Table.Name}', so two rows can link to one object",
                            UniqueText(x.Pair.Table.Table, columns, filtered: true), "To keep the links one to one");
                    }
                }

                if (keyed)
                {
                    foreach (var table in pointers.Select(x => x.Pair.Table).Distinct().Where(t => links.LinkTable is not null && ReferenceEquals(t, links.LinkTable)))
                        Gap(StorageGapKinds.PerTypeDuplicates, $"it can't refuse duplicate links in table '{table.Table.Name}', since a unique over nullable key columns doesn't stop them in most databases");
                }

                if (!linkUniqueDone && e.End.HasSeveralTypes && !s.Source.AllowDuplicates && !s.Ends.Any(x => x.Entry?.Types.Any(t => t.ForeignKey is not null) == true && links.LinkTable is not null))
                {
                    foreach (var pair in links.Pairs.Where(p => p.Result is not null && linkTables.Contains(p.Table)).DistinctBy(p => p.Table))
                    {
                        linkUniqueDone = true;
                        var columns = new List<RColumn>();
                        foreach (var end in pair.Result!.Ends)
                        {
                            if (end.TypeColumn is { } tc)
                                columns.Add(tc);
                            columns.AddRange(end.Columns);
                        }

                        columns.AddRange(result.Constants.Select(c => c.Column));
                        columns = columns.Distinct().ToList();
                        if (HasUniqueOn(pair.Table.Table, columns))
                            continue;
                        Gap(StorageGapKinds.LinkUnique, $"it finds no unique over columns {Names(columns.Select(c => c.Name))} of table '{pair.Table.Table.Name}', so it can't refuse a duplicate link",
                            UniqueText(pair.Table.Table, columns, filtered: false), "To refuse duplicate links");
                    }
                }
            }

            foreach (var target in links.Pairs.Where(p => p.Result is not null).Select(p => p.Ends[i].Target).Distinct())
            {
                if (RowsNotToldApart(e, s.Covered[i], target))
                    Gap(StorageGapKinds.RowsNotToldApart, $"nothing tells the rows of '{target.Entity.Name}' apart from the other rows of table '{target.Table!.Table.Name}'");
            }

            if (kinds.Count == 0)
                continue;
            gaps.AddRange(kinds);
            var tables = pointers.Select(x => x.Pair.Table.Table.Name).Distinct().ToList();
            var how = pointers.Count == 0 ? "the rows of its types"
                : pointers[0].Pointer.ForeignKey is null ? pointers[0].Pointer.TypeColumn is null ? "an id" : "a type and an id"
                : e.Entry?.ForeignKeys.Count > 0 ? "keys in each table" : e.Entry?.Types.Any(t => t.ForeignKey is not null) == true ? "a key per type" : "a key";
            var where = tables.Count == 0 ? "" : tables.Count == 1 ? $" in table '{tables[0]}'" : $" in tables {Names(tables)}";
            var message = $"Database '{_db.Name}' holds end '{e.End.Role}' of relation '{s.Relation.Name}' as {how}{where}, so {string.Join("; and ", clauses)}."
                + string.Concat(texts.Select(t => " " + t));
            _run.AddDiagnostic("MQ4118", message, m.Id, e.Entry is null ? "/ends" : EntryPointer(m, e.End.Id));
        }

        return gaps;
    }

    /// <summary>Whether a target's table holds rows of entities it doesn't stand for, with no discriminator and no binding constants to tell them apart.</summary>
    private bool RowsNotToldApart(TypeLinkEnd e, HashSet<string> covered, Placement target)
    {
        var table = target.Table!;
        var stands = StandsFor(e, covered, target);
        if (!_placementOrder.Exists(p => ReferenceEquals(p.Table, table) && !stands.Contains(p)))
            return false;
        if (target.Strategy == InheritanceStrategy.Tph && !target.Bound)
            return false; // the discriminator
        return !(_bindingByEntity.TryGetValue(target.Entity.Id, out var bound) && bound.Binding.Constants.Count > 0);
    }

    /// <summary>Whether some check of the table names every one of the columns: a column check on it, or its text names it as a word.</summary>
    private static bool HasCheckOn(RTable table, IReadOnlyList<RColumn> columns) =>
        table.Checks.Any(c => columns.All(column => ReferenceEquals(c.Column, column)
            || NamesWord(c.Expression, column.Name)));

    /// <summary>Whether a text names a word, ignoring case: the word with no letter, number or underscore on either side.</summary>
    private static bool NamesWord(string text, string word)
    {
        for (var at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            var end = at + word.Length;
            if ((at == 0 || !IsWordChar(text[at - 1])) && (end >= text.Length || !IsWordChar(text[end])))
                return true;
        }

        return false;

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }

    /// <summary>Why a constant's value doesn't fit its column (type, length, range, null), or <see langword="null"/> when it fits (MQ4051).</summary>
    private static string? FitProblem(object? value, string type, int? length, bool nullable) => value switch
    {
        null => nullable ? null : "is NULL, and the column is not nullable",
        bool => type == "bool" ? null : "is a boolean",
        long l => type switch
        {
            "int16" when l is < short.MinValue or > short.MaxValue => "does not fit a 16-bit integer",
            "int32" when l is < int.MinValue or > int.MaxValue => "does not fit a 32-bit integer",
            "int16" or "int32" or "int64" or "decimal" or "float" or "double" => null,
            _ => "is a number",
        },
        decimal or double => type is "decimal" or "float" or "double" ? null : "is a number with a fraction",
        string text => type switch
        {
            "int16" or "int32" or "int64" or "decimal" or "float" or "double" or "bool" => "is a text",
            "string" when length is { } max && text.Length > max => $"is {text.Length.ToString(CultureInfo.InvariantCulture)} characters long, over the column's {max.ToString(CultureInfo.InvariantCulture)}",
            _ => null,
        },
        _ => null,
    };

    /// <summary>Whether a unique key, a unique index without a filter or the primary key is over exactly the columns.</summary>
    private static bool HasUniqueOn(RTable table, IReadOnlyList<RColumn> columns)
    {
        bool Same(IEnumerable<RColumn?> other) => other.Count() == columns.Count && columns.All(c => other.Contains(c));
        return (table.PrimaryKey is { } pk && Same(pk.Columns))
            || table.Uniques.Any(u => Same(u.Columns))
            || table.Indexes.Any(i => i.Unique && i.Where is null && Same(i.Columns.Select(c => c.Column)));
    }

    /// <summary>The unique to copy (decisions 31 and 47): a unique key, or on SQL Server for a one-to-one a unique index filtered to rows where it is set.</summary>
    private string UniqueText(RTable table, IReadOnlyList<RColumn> columns, bool filtered)
    {
        var list = string.Join(", ", columns.Select(c => Q(c.Name)));
        if (filtered && _db.Dialect == Dialect.SqlServer)
        {
            var index = Render(_conv.IndexName, ("table", table.Name), ("columns", Joined(columns)));
            var where = string.Join(" AND ", columns.Select(c => Q(c.Name) + " IS NOT NULL"));
            var qualified = table.Schema is { } schema ? Q(schema) + "." + Q(table.Name) : Q(table.Name);
            return $"CREATE UNIQUE INDEX {Q(index)} ON {qualified} ({list}) WHERE {where}";
        }

        var name = Render(_conv.UniqueName, ("table", table.Name), ("columns", Joined(columns)));
        return $"CONSTRAINT {Q(name)} UNIQUE ({list})";
    }

    private string Q(string name) => SqlDialects.Quote(name, _db.Dialect, _db.Quoting);

    // ---- dependency keys ------------------------------------------------------------------------------------------------

    /// <summary>
    /// What a stored relation's members depend on (section 19.8): the mapping file, the named tables' files, each target's key, the files
    /// that set the codes, the ends' types keys, and every covered entity's own and referrers' keys (a covered entity can gain or lose its
    /// placement while no other key moves).
    /// </summary>
    private void AddTypeStorageDeps(TypeStorageEntry s, TypeLinks links)
    {
        var deps = _run.DepsOf(s.Relation);
        deps.Add(Conventions).Element(_db.Id).Element(links.Mapping.Id);
        foreach (var pair in links.Pairs)
        {
            if (pair.Table.Source is { } source)
                deps.Element(source.Id);
            foreach (var pointer in pair.Ends)
            {
                deps.AddRange(KeyDeps(pointer.Target));
                if (pointer.Target.Table?.Source is { } targetSource)
                    deps.Element(targetSource.Id);
                if (pointer.ForeignKey?.Target?.Source is { } referenced)
                    deps.Element(referenced.Id);
            }
        }

        for (var i = 0; i < s.Ends.Count; i++)
        {
            if (!s.Ends[i].End.HasSeveralTypes)
                continue;
            deps.Add(EndCoverage.TypesKey(s.Ends[i].End.Id));
            foreach (var id in s.Covered[i].Order(StringComparer.Ordinal))
            {
                deps.Element(id).Referrers(id);
                if (_run.MappingOf(_db.Id, id) is { } entityMapping)
                    deps.Element(entityMapping.Id);
            }
        }
    }
}
