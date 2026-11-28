using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>How a relation is stored in one database (section 7.4).</summary>
internal sealed class RelationPhysical(RelationShape shape)
{
    public RelationShape Shape { get; } = shape;

    /// <summary>The relation stored.</summary>
    public RRelation? Relation { get; init; }

    /// <summary>The relation mapping in this database; its foreign key is set once foreign keys are resolved.</summary>
    public RRelationMapping? Mapping { get; set; }

    /// <summary>The referenced end (foreign-key shape).</summary>
    public REnd? Principal { get; init; }

    /// <summary>The end whose table holds the foreign key (foreign-key shape).</summary>
    public REnd? Dependent { get; init; }

    /// <summary>Foreign keys of the foreign-key shape, one per host table of the dependent end.</summary>
    public List<ForeignKeySpec> Foreign { get; } = [];

    /// <summary>Junction table foreign keys, by end id.</summary>
    public Dictionary<string, ForeignKeySpec> EndKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>The junction table.</summary>
    public TableBuild? Junction { get; set; }

    /// <summary>
    /// For a relation with an end of several types whose mapping states storage (several-types-design.md section 19.8): what step 1
    /// (<c>CollectTypeStorage</c>) works out, step 2 (<c>FinishTypeStorage</c>) resolves and step 3 (<c>BuildJoins</c>) reads;
    /// <see langword="null"/> for every other relation.
    /// </summary>
    public TypeLinks? TypeLinks { get; set; }
}

/// <summary>
/// The storage of a relation with an end of several types in one database (several-types-design.md decisions 39 to 42 and section 19.8).
/// Step 1 records the ends, their targets and the pairs with their holding tables and pointers from placements and the mapping file; step 2
/// fills each pointer's <see cref="TypeLinkPointer.Result"/> and each pair's <see cref="TypeLink.Result"/> once keys and uniques are resolved;
/// step 3 builds a join path per pair from them.
/// </summary>
internal sealed class TypeLinks
{
    /// <summary>The relation mapping file in this database.</summary>
    public required Mapping Mapping { get; init; }

    /// <summary>The designed link table the mapping names (<c>junctionTable</c>), or <see langword="null"/>.</summary>
    public TableBuild? LinkTable { get; init; }

    /// <summary>One per relation end, in file order.</summary>
    public required IReadOnlyList<TypeLinkEnd> Ends { get; init; }

    /// <summary>
    /// One per pair of targets, one target per end, in target order (decision 39): by the first end's target, then the second's. A pair whose
    /// holding table step 1 couldn't settle (MQ4117) is left out.
    /// </summary>
    public List<TypeLink> Pairs { get; } = [];
}

/// <summary>One end of a relation stored as <see cref="TypeLinks"/>.</summary>
internal sealed class TypeLinkEnd
{
    /// <summary>The relation end.</summary>
    public required REnd End { get; init; }

    /// <summary>The mapping's entry for the end, or <see langword="null"/> when it has none (that end's rows then hold the link).</summary>
    public RelationEndMapping? Entry { get; init; }

    /// <summary>
    /// The end's targets in target order (decision 39): covered entities placed in the database, with a table, whose rows aren't in a covered
    /// base's table. Each type gives itself when it is a target, then its covered derived targets by qualified name, then id.
    /// </summary>
    public List<Placement> Targets { get; } = [];

    /// <summary>Covered entities not placed in the database, by qualified name, then id.</summary>
    public List<REntity> LeftOut { get; } = [];

    /// <summary>Targets no pointer of the entry reaches (MQ4120), in target order.</summary>
    public List<Placement> NotStored { get; } = [];
}

/// <summary>One pair of targets and the table that holds its links.</summary>
internal sealed class TypeLink
{
    /// <summary>The holding table: the link table, the table of the holding end's target, or a table per type (way 7).</summary>
    public required TableBuild Table { get; init; }

    /// <summary>One per relation end, in file order.</summary>
    public required IReadOnlyList<TypeLinkPointer> Ends { get; init; }

    /// <summary>The resolved link, which step 2 fills; <see langword="null"/> until then, or when step 2 refuses the pair (MQ4117).</summary>
    public RRelationLink? Result { get; set; }
}

/// <summary>
/// How a pair's holding table points at one end's target (decision 39): exactly one of <see cref="ForeignKey"/> and <see cref="IdColumn"/>,
/// or neither on the end whose rows hold the link (<see cref="IsHolder"/>).
/// </summary>
internal sealed class TypeLinkPointer
{
    /// <summary>The relation end.</summary>
    public required REnd End { get; init; }

    /// <summary>The target at this end.</summary>
    public required Placement Target { get; init; }

    /// <summary>Whether this end's rows hold the link: <see cref="TypeLink.Table"/> is the target's table.</summary>
    public bool IsHolder { get; init; }

    /// <summary>
    /// The pointer when it is a designed foreign key (an entry's <c>foreignKey</c>, the <c>foreignKeys</c> item the holding table owns, or the
    /// <c>types</c> item's); its <see cref="ForeignKeySpec.Result"/> is complete after <c>FinishTables</c>.
    /// </summary>
    public ForeignKeySpec? ForeignKey { get; init; }

    /// <summary>The pointer when it is an id column: the column's id in the holding table (<c>idColumn</c>).</summary>
    public string? IdColumn { get; init; }

    /// <summary>Beside <see cref="IdColumn"/>: the id of the column that holds the type code (<c>typeColumn</c>), or <see langword="null"/>.</summary>
    public string? TypeColumn { get; init; }

    /// <summary>
    /// Beside <see cref="IdColumn"/>: the column of the target's table the id refers to, as the file names it (the entry's or the
    /// <c>types</c> item's <c>referencesColumn</c>); <see langword="null"/> refers to the table's one-column primary key.
    /// </summary>
    public string? ReferencesColumn { get; init; }

    /// <summary>
    /// The resolved pointer, which step 2 fills: its columns, referenced table and columns, key, type column and codes; <see langword="null"/>
    /// until then, or when step 2 refuses it (MQ4117).
    /// </summary>
    public RRelationLinkEnd? Result { get; set; }
}

/// <summary>Relations: each stored by its relation mapping, on a designed foreign key or a designed junction table.</summary>
internal sealed partial class DatabaseRun
{
    private readonly Dictionary<Placement, IReadOnlyList<string>> _keyDeps = new(ReferenceEqualityComparer.Instance);

    /// <summary>One-type relations whose designed link table's mapping writes constants or attribute columns (answer 9), in relation order.</summary>
    private readonly List<(RRelation Relation, RelationPhysical Physical)> _linkTableLinks = [];

    private void ResolveRelation(RRelation rel)
    {
        var src = _run.RelationSource(rel.Id);
        if (src is null)
            return;
        // A relation with an end of several types is stored only as its mapping describes (several-types-design.md section 19.8, step 1);
        // every reader below sees one-type ends only.
        if (EndCoverage.HasSeveralTypes(src))
        {
            CollectTypeStorage(rel, src);
            return;
        }

        foreach (var e in rel.Ends.Items)
        {
            if (!_placements.ContainsKey(e.OneEntity.Id))
                return;
        }

        var m = _run.MappingOf(_db.Id, rel.Id);
        if (ShapeOf(rel, src, m) == RelationShape.ForeignKey)
            ResolveForeignKeyShape(rel, m);
        else
            ResolveJunctionShape(rel, m);
    }

    /// <summary>
    /// The relation's shape in the database: the mapping's <c>shape</c>; else a junction when it names a <c>junctionTable</c>, when the relation
    /// is many-to-many or n-ary, or when it carries attributes and names no foreign key; else a foreign key.
    /// </summary>
    private static RelationShape ShapeOf(RRelation rel, Relation src, Mapping? m)
    {
        var nary = rel.Ends.Count != 2 || src.RelationKind == RelationKind.NAry;
        var manyToMany = string.Equals(rel.Cardinality, "many-to-many", StringComparison.Ordinal);
        if (nary || manyToMany)
            return RelationShape.Junction;
        return m?.Shape ?? (m?.JunctionTable is not null || (rel.Attributes.Count > 0 && m?.ForeignKey is null) ? RelationShape.Junction : RelationShape.ForeignKey);
    }

    /// <summary>The principal (referenced) and dependent (holding the key) ends of a binary relation.</summary>
    private static (REnd Principal, REnd Dependent) PrincipalAndDependent(RRelation rel, Mapping? m)
    {
        var e0 = rel.Ends[0];
        var e1 = rel.Ends[1];
        if (e0.IsMany != e1.IsMany)
            return e0.IsMany ? (e1, e0) : (e0, e1);
        if ((e0.Min == 1) != (e1.Min == 1))
            return e0.Min == 1 ? (e0, e1) : (e1, e0);
        return string.Equals(m?.ForeignKeyEnd, e0.Id, StringComparison.Ordinal) ? (e1, e0) : (e0, e1);
    }

    /// <summary>
    /// A foreign-key relation: bound to the existing foreign key its mapping names in the dependent end's table (section 7.4); MQ4011 when
    /// the mapping names none, or one that no table of the database declares.
    /// </summary>
    private void ResolveForeignKeyShape(RRelation rel, Mapping? m)
    {
        var (principal, dependent) = PrincipalAndDependent(rel, m);
        var dp = _placements[dependent.OneEntity.Id];
        var phys = new RelationPhysical(RelationShape.ForeignKey) { Relation = rel, Principal = principal, Dependent = dependent };
        if (m?.ForeignKey is { } fkId && _foreignKeysById.TryGetValue(fkId, out var bound))
        {
            bound.Result.Relation = rel;
            bound.Result.End = dependent;
            bound.Host.Deps.AddRange(_run.DepsOf(rel).ToList());
            phys.Foreign.Add(bound);
        }
        else
        {
            _run.AddDiagnostic("MQ4011", m?.ForeignKey is { } missing
                ? $"Relation '{rel.Name}' names foreign key '{missing}', which no table of database '{_db.Name}' declares; the relation has no foreign key there."
                : $"Relation '{rel.Name}' has its dependent end '{dependent.Role}' bound to table '{dp.Table.Table.Name}' in database '{_db.Name}' but its relation mapping names no foreign key (foreignKey); the relation has no foreign key there.",
                rel.Id);
        }

        _relationPhysical[rel.Id] = phys;
        phys.Mapping = new RRelationMapping { Shape = "foreign-key" }; // ForeignKey: FinishRelationMappings, once keys are resolved
        _run.SetRelationMapping(rel, _db.Name, phys.Mapping);
    }

    /// <summary>What the key columns of a referenced entity depend on: the entity, its hierarchy root and its table's file.</summary>
    private IReadOnlyList<string> KeyDeps(Placement p)
    {
        if (_keyDeps.TryGetValue(p, out var cached))
            return cached;
        var deps = new DependencySet(_run.Keys).AddRange(_run.DepsOf(p.Entity).ToList()).AddRange(_run.DepsOf(p.Root.Entity).ToList());
        deps.Element(p.Table.Source.Id);
        cached = deps.ToList();
        _keyDeps[p] = cached;
        return cached;
    }

    /// <summary>
    /// The one link of a one-type relation whose designed link table's mapping writes constants or attribute columns (several-types-design.md
    /// section 19.8 and answer 9), once keys are resolved: each end's key from this relation's own entries. Several relations can share such a
    /// table and its keys, and a key's <see cref="RForeignKey.End"/> names the end of only one of them, so a reader that writes this relation's
    /// links (its seed rows) takes the keys from here. Left empty when an end has no resolved key (MQ4009 or MQ4011 reports it).
    /// </summary>
    private void FinishLinkTableLinks()
    {
        foreach (var (rel, phys) in _linkTableLinks)
        {
            if (phys.Mapping is not { JunctionTable: { } table } mapping)
                continue;
            var ends = new List<RRelationLinkEnd>(rel.Ends.Count);
            foreach (var end in rel.Ends)
            {
                if (!phys.EndKeys.TryGetValue(end.Id, out var spec) || !spec.Resolved || end.Entity is not { } entity)
                    break;
                var key = spec.Result;
                ends.Add(new RRelationLinkEnd
                {
                    End = end, Type = entity, Columns = key.Columns, ReferencedTable = key.ReferencedTable, ReferencedColumns = key.ReferencedColumns,
                    ForeignKey = key,
                });
            }

            if (ends.Count == rel.Ends.Count)
                mapping.Links = [new RRelationLink { Table = table, Ends = ends }];
        }
    }

    /// <summary>
    /// A relation stored in a junction table: bound to the designed table its mapping names, each end through its <c>ends</c> entry
    /// (section 7.4); MQ4011 when the mapping names no designed junction table of the database.
    /// </summary>
    private void ResolveJunctionShape(RRelation rel, Mapping? m)
    {
        var phys = new RelationPhysical(RelationShape.Junction);
        _relationPhysical[rel.Id] = phys;
        if (m?.JunctionTable is not { } junctionId || !_tables.TryGetValue(junctionId, out var designed))
        {
            _run.AddDiagnostic("MQ4011", m?.JunctionTable is { } missing
                ? $"Relation '{rel.Name}' names junction table '{missing}', which is not a table of database '{_db.Name}'; the relation has no storage there."
                : $"Relation '{rel.Name}' is stored in a junction table in database '{_db.Name}', where both ends are bound, but {(m is null ? "it has no relation mapping there" : "its relation mapping names no junctionTable")}; the relation has no storage there.",
                rel.Id);
            return;
        }

        // A designed junction binds each end through Mapping.Ends (section 7.4). An entry with no foreignKey is skipped: MQ4009 reports it
        // (several-types-design.md decision 38). Its constants and attribute columns are finished with the storage run (answer 9).
        foreach (var endMapping in m.Ends)
        {
            var end = rel.Ends.FirstOrDefault(e => string.Equals(e.Id, endMapping.End, StringComparison.Ordinal));
            if (end is null || endMapping.ForeignKey is not { } foreignKeyId || !_foreignKeysById.TryGetValue(foreignKeyId, out var spec))
                continue;
            spec.Result.Relation = rel;
            spec.Result.End = end;
            phys.EndKeys[end.Id] = spec;
        }

        designed.Table.Relation ??= rel;
        designed.Deps.AddRange(_run.DepsOf(rel).ToList());
        phys.Junction = designed;
        phys.Mapping = new RRelationMapping { Shape = "junction", JunctionTable = designed.Table };
        _run.SetRelationMapping(rel, _db.Name, phys.Mapping);
        CollectLinkTableExtras(rel, m, designed, phys.Mapping);
        if (m.Constants.Count > 0 || m.Attributes.Any(a => a.Column is not null))
            _linkTableLinks.Add((rel, phys));
    }
}
