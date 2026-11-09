using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Resolution;

/// <summary>How a relation is stored in one database (section 7.4).</summary>
internal sealed class RelationPhysical(RelationShape shape)
{
    public RelationShape Shape { get; } = shape;

    /// <summary>The relation (or promotion relation) stored.</summary>
    public RRelation? Relation { get; init; }

    /// <summary>The relation mapping in this database; its foreign key is set once foreign keys are resolved.</summary>
    public RRelationMapping? Mapping { get; set; }

    /// <summary>The referenced end (foreign-key shape).</summary>
    public REnd? Principal { get; init; }

    /// <summary>The end whose table holds the foreign key (foreign-key shape).</summary>
    public REnd? Dependent { get; init; }

    /// <summary>Foreign keys of the foreign-key shape, one per host table of the dependent end.</summary>
    public List<ForeignKeySpec> Foreign { get; } = [];

    /// <summary>Junction or promoted table foreign keys, by end id.</summary>
    public Dictionary<string, ForeignKeySpec> EndKeys { get; } = new(StringComparer.Ordinal);

    /// <summary>The junction or promoted table.</summary>
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

/// <summary>Relations: foreign keys, junction tables, promoted entities and bindings to existing foreign keys.</summary>
internal sealed partial class DatabaseRun
{
    private readonly List<(REntity Entity, TableBuild Table)> _promotedTables = [];

    /// <summary>
    /// Whether a binding field, a query field or a query collection names any end of a relation with an end of several types, or a path
    /// under one: MQ4116 refuses it (several-types-design.md decision 22), since part 1 stores nothing for such a
    /// relation, so resolution reports nothing more about it.
    /// </summary>
    /// <param name="model">The model.</param>
    /// <param name="reference">The field's or collection's <c>attribute</c>.</param>
    /// <returns>Whether it goes through such a relation.</returns>
    internal static bool NamesSeveralTypesEnd(ModelSnapshot model, string reference) =>
        EndCoverage.Of(model).Any && Validation.RelationTypeRules.ThroughSeveralTypes(model, reference) is not null;

    private void ResolveRelation(RRelation rel)
    {
        var src = _run.RelationSource(rel.Id);
        // Part 1 stores nothing for a relation with an end of several types (several-types-design.md decision 22), as for a relation whose
        // end entity isn't placed; every reader below sees one-type ends only.
        // several-types-2: storage — CollectTypeStorage for a mapping that states storage (section 19.8, step 1); FinishTypeStorage after
        // FinishTables (step 2), which FinishRelationMappings must skip.
        if (src is null || EndCoverage.HasSeveralTypes(src) || rel.Ends.Any(e => !_placements.ContainsKey(e.OneEntity.Id)))
            return;
        var m = _run.MappingOf(_db.Id, rel.Id);
        if (m is { Ignore: true })
            return;
        switch (ShapeOf(rel, src, m))
        {
            case RelationShape.ForeignKey:
                ResolveForeignKeyShape(rel, src, m);
                break;
            case RelationShape.Promoted:
                ResolveJunctionShape(rel, src, m, promote: true);
                break;
            default:
                ResolveJunctionShape(rel, src, m, promote: false);
                break;
        }
    }

    private RelationShape ShapeOf(RRelation rel, Relation src, Mapping? m)
    {
        var nary = rel.Ends.Count != 2 || src.RelationKind == RelationKind.NAry;
        var manyToMany = string.Equals(rel.Cardinality, "many-to-many", StringComparison.Ordinal);
        if (m?.Shape == RelationShape.Promoted)
            return RelationShape.Promoted;
        if (nary)
            return RelationShape.Junction;
        var shape = m?.Shape ?? (m?.JunctionTable is not null ? RelationShape.Junction : rel.Attributes.Count > 0 ? _conv.RelationsWithAttributes : manyToMany ? RelationShape.Junction : RelationShape.ForeignKey);
        return shape == RelationShape.ForeignKey && manyToMany ? RelationShape.Junction : shape;
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

    /// <summary>The referential action of the principal end's intent (composition cascades by default).</summary>
    private static string PrincipalAction(REnd principal, Relation src) => principal.OnDelete switch
    {
        "cascade" => "cascade",
        "restrict" => "restrict",
        "set-null" => "set-null",
        _ => src.RelationKind == RelationKind.Composition ? "cascade" : "no-action",
    };

    private void ResolveForeignKeyShape(RRelation rel, Relation src, Mapping? m)
    {
        var (principal, dependent) = PrincipalAndDependent(rel, m);
        var pp = _placements[principal.OneEntity.Id];
        var dp = _placements[dependent.OneEntity.Id];
        var phys = new RelationPhysical(RelationShape.ForeignKey) { Relation = rel, Principal = principal, Dependent = dependent };
        var relationDeps = _run.DepsOf(rel).ToList();
        if (dp.Bound)
        {
            // Binding to an existing foreign key of the dependent's designed or imported table (section 7.4).
            if (m?.ForeignKey is { } fkId && _foreignKeysById.TryGetValue(fkId, out var bound))
            {
                bound.Result.Relation = rel;
                bound.Result.End = dependent;
                bound.Host.Deps.AddRange(relationDeps);
                phys.Foreign.Add(bound);
            }
            else if (m?.ForeignKey is not null || !pp.Bound || dp.ViaBinding || pp.ViaBinding)
            {
                // Both ends bound to designed tables with no foreign key named is W2's MQ4011; the resolver reports the cases W2 cannot
                // see: a bound dependent whose principal is synthesized, a named foreign key that is not in this database, and an end
                // bound by an entity binding (erratum E43), which W2 does not read.
                _run.AddDiagnostic("MQ4011", m?.ForeignKey is { } missing
                    ? $"Relation '{rel.Name}' names foreign key '{missing}', which no table of database '{_db.Name}' declares; the relation has no foreign key there."
                    : $"Relation '{rel.Name}' has its dependent end '{dependent.Role}' bound to table '{dp.Table?.Table.Name}' in database '{_db.Name}' but names no foreign key (Mapping.ForeignKey); the relation has no foreign key there.",
                    rel.Id);
            }
        }
        else if (pp.Root.Entity.Key is { Attributes.Count: > 0 } key)
        {
            var target = pp.Table;
            var targetKeys = target?.PrimaryKey ?? [];
            var onDelete = PrincipalAction(principal, src);
            var oneToOne = string.Equals(rel.Cardinality, "one-to-one", StringComparison.Ordinal);
            foreach (var host in HostsOf(dp))
            {
                var forceNull = principal.Min == 0 || (dp.Strategy == InheritanceStrategy.Tph && !ReferenceEquals(dp.Root, dp));
                var keys = AddReferenceColumns(host, principal, rel, key, target, targetKeys, forceNull);
                host.Deps.AddRange(relationDeps).AddRange(KeyDeps(pp)).Referrers(rel.Id);
                if (target is not null && keys.Count > 0)
                {
                    var spec = new ForeignKeySpec(host, keys, target, null, [], onDelete, "no-action", null);
                    spec.Result.Relation = rel;
                    spec.Result.End = dependent;
                    host.ForeignKeys.Add(spec);
                    phys.Foreign.Add(spec);
                }

                if (oneToOne && keys.Count > 0)
                    host.Uniques.Add(new UniqueSpec(keys, null, null));
                foreach (var a in rel.Attributes)
                    AddAttributeColumns(host, a, RelationAttributeMapping(m, a), false, true, rel.Name);
                foreach (var action in host.Deferred.ToList())
                    action();
                host.Deferred.Clear();
            }
        }

        _relationPhysical[rel.Id] = phys;
        phys.Mapping = new RRelationMapping { Shape = "foreign-key" }; // ForeignKey: FinishRelationMappings, once keys are resolved
        _run.SetRelationMapping(rel, _db.Name, phys.Mapping);
    }

    /// <summary>The foreign key columns that reference an end's entity key, added to a table (keys <c>&lt;endId&gt;.&lt;keyAttrId&gt;</c>).</summary>
    private List<string> AddReferenceColumns(TableBuild host, REnd end, RRelation rel, RKey key, TableBuild? target, IReadOnlyList<string> targetKeys,
        bool nullable)
    {
        var keys = new List<string>();
        for (var i = 0; i < key.Attributes.Count; i++)
        {
            var ka = key.Attributes[i];
            var referenced = target is not null && i < targetKeys.Count ? target.Resolve(targetKeys[i]) : null;
            var columnKey = end.Id + "." + ka.Id;
            var name = Render(_conv.ForeignKeyColumn, ("role", end.Role), ("key", ka.Name), ("entity", end.OneEntity.Name), ("relation", rel.Name),
                ("table", host.Table.Name));
            AddColumn(host, columnKey, name, referenced?.Type ?? ka.Type.Builtin ?? "string", referenced?.Length ?? ka.Length,
                referenced?.Precision ?? ka.Precision, referenced?.Scale ?? ka.Scale, nullable, null, null, null,
                scalar: referenced is not null ? ScalarOf(referenced) : ka.Type.Scalar, follows: referenced);
            keys.Add(columnKey);
        }

        return keys;
    }

    /// <summary>What the key columns of a referenced entity depend on: the entity and the overlay or file of its table.</summary>
    private IReadOnlyList<string> KeyDeps(Placement p)
    {
        if (_keyDeps.TryGetValue(p, out var cached))
            return cached;
        var deps = new DependencySet(_run.Keys).AddRange(_run.DepsOf(p.Entity).ToList()).AddRange(_run.DepsOf(p.Root.Entity).ToList());
        if (p.Table?.Overlay is { } overlay)
            deps.Element(overlay.Id);
        if (p.Table?.Source is { } source)
            deps.Element(source.Id);
        cached = deps.ToList();
        _keyDeps[p] = cached;
        return cached;
    }

    private static AttributeMapping? RelationAttributeMapping(Mapping? m, RAttribute a) =>
        m?.Attributes.FirstOrDefault(x => string.Equals(x.Attribute, a.Id, StringComparison.Ordinal));

    private void ResolveJunctionShape(RRelation rel, Relation src, Mapping? m, bool promote)
    {
        var phys = new RelationPhysical(promote ? RelationShape.Promoted : RelationShape.Junction);
        _relationPhysical[rel.Id] = phys;
        var relationDeps = _run.DepsOf(rel).ToList();

        if (!promote && m?.JunctionTable is { } junctionId && _tables.TryGetValue(junctionId, out var designed) && designed.IsDesigned)
        {
            // A designed junction binds each end through Mapping.Ends (section 7.4). An entry with no foreignKey is skipped: MQ4009 reports it
            // (several-types-design.md decision 38).
            // several-types-2: storage — the mapping's constants and attribute columns on a designed junction (decisions 44 and 45, answer 9).
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
            designed.Table.IsJunction = true;
            designed.Deps.AddRange(relationDeps);
            phys.Junction = designed;
            _run.SetRelationMapping(rel, _db.Name, new RRelationMapping { Shape = "junction", JunctionTable = designed.Table });
            return;
        }

        var promoted = promote ? _run.Promote(rel, m?.PromotedName) : null;
        var overlay = _overlays.GetValueOrDefault("relation:" + rel.Id);
        var key = rel.Id + "@" + _db.Id;
        var r = new RTable
        {
            Id = key,
            Key = key,
            Name = overlay is { Name.Length: > 0 } ? overlay.Name : promoted is not null ? TableNameFor(promoted.Name) : JunctionName(rel),
            Schema = SchemaName(overlay?.Schema),
            Database = _rdb,
            Origin = "synthesized",
            Entity = promoted,
            Relation = rel,
            Comment = overlay?.Comment,
            IsJunction = promoted is null,
        };
        var t = new TableBuild(_run.Keys, r, null, overlay, rel.Id);
        t.Deps.AddRange(relationDeps).Element(_db.Id).Add(Conventions).Add(TypeMaps).Referrers(rel.Id);
        if (promoted is not null)
            t.Deps.AddRange(_run.DepsOf(promoted).ToList()).Add(InflectionKey);
        if (overlay is not null)
        {
            t.Deps.Element(overlay.Id);
            AddConstraintFiles(t, overlay);
        }

        AddTable(t);
        phys.Junction = t;
        var surrogate = src.AllowDuplicates || src.Ends.Any(e => e.Ordered);
        if (surrogate)
        {
            var id = AddColumn(t, "id", Render(_conv.KeyColumn, ("attribute", "id"), ("entity", promoted?.Name ?? rel.Name), ("table", r.Name)),
                "int64", null, null, null, false, null, null, null);
            if (id.Sequence is null)
                id.Identity = true;
        }

        var allKeys = new List<string>();
        foreach (var end in rel.Ends)
        {
            var ep = _placements[end.OneEntity.Id];
            t.Deps.AddRange(KeyDeps(ep)).Referrers(end.OneEntity.Id);
            if (ep.Root.Entity.Key is not { Attributes.Count: > 0 } endKey)
                continue;
            var keys = AddReferenceColumns(t, end, rel, endKey, ep.Table, ep.Table?.PrimaryKey ?? [], false);
            allKeys.AddRange(keys);
            if (ep.Table is null || keys.Count == 0)
                continue;
            var spec = new ForeignKeySpec(t, keys, ep.Table, null, [], string.Equals(end.OnDelete, "restrict", StringComparison.Ordinal) ? "restrict" : "cascade",
                "no-action", null);
            if (promoted is not null)
            {
                var promotion = _run.PromotionRelation(rel, end);
                spec.Result.Relation = promotion;
                spec.Result.End = promotion.Ends[1];
                var promotionPhysical = new RelationPhysical(RelationShape.ForeignKey)
                {
                    Relation = promotion, Principal = promotion.Ends[0], Dependent = promotion.Ends[1], Mapping = new RRelationMapping { Shape = "foreign-key" },
                };
                promotionPhysical.Foreign.Add(spec);
                _relationPhysical[promotion.Id] = promotionPhysical;
                _run.SetRelationMapping(promotion, _db.Name, promotionPhysical.Mapping);
            }
            else
            {
                spec.Result.Relation = rel;
                spec.Result.End = end;
            }

            t.ForeignKeys.Add(spec);
            phys.EndKeys[end.Id] = spec;
        }

        if (src.Ends.Any(e => e.Ordered))
            AddColumn(t, "position", Render(_conv.OrderColumn), "int32", null, null, null, false, null, null, null);
        var promotedKeys = promoted is null ? [] : _run.PromotedKeys(promoted.Id);
        foreach (var a in promoted?.Attributes ?? rel.Attributes)
        {
            if (!promotedKeys.Any(k => ReferenceEquals(k.Attribute, a)))
                AddAttributeColumns(t, a, RelationAttributeMapping(m, a), false, false, promoted?.Name ?? rel.Name);
        }

        // A promoted entity's key attributes map to the surrogate id or to the foreign key columns of the composite key.
        foreach (var (attribute, columnKey) in promotedKeys)
        {
            if (t.Resolve(columnKey) is { Attribute: null } column)
            {
                column.Attribute = attribute;
                column.AttributePath = attribute.Id;
            }
        }

        if (t.PrimaryKey.Count == 0)
            t.PrimaryKey.AddRange(surrogate ? ["id"] : allKeys);
        if (surrogate && !src.AllowDuplicates && allKeys.Count > 0)
            t.Uniques.Add(new UniqueSpec(allKeys, null, null));
        foreach (var action in t.Deferred.ToList())
            action();
        t.Deferred.Clear();

        if (promoted is not null)
        {
            _promotedTables.Add((promoted, t));
            _run.SetRelationMapping(rel, _db.Name, new RRelationMapping { Shape = "promoted", PromotedEntity = promoted });
        }
        else
        {
            _run.SetRelationMapping(rel, _db.Name, new RRelationMapping { Shape = "junction", JunctionTable = r });
        }
    }

    /// <summary>The <c>junctionTable</c> pattern; an n-ary relation's <c>{entity2}</c> joins the names of every end after the first.</summary>
    private string JunctionName(RRelation rel) =>
        NamePattern.Render(_conv.JunctionTable, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["entity1"] = rel.Ends[0].OneEntity.Name,
            ["entity2"] = string.Join('_', rel.Ends.Skip(1).Select(e => e.OneEntity.Name)),
            ["relation"] = rel.Name,
        }, _conv.TableCase);
}
