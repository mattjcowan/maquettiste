using System.Reflection;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Nothing a model file holds is left out of the resolved model: for every document kind of engine-design.md section 2.2 (and the
/// parts of a file that resolve to objects of their own: columns, schemas, constraints, attributes, enum members, relation ends,
/// process parts), each property its JSON schema declares must be a member of the resolved type, which is what templates, scripts
/// and <c>get_template_context</c> read (the member catalog lists the same public properties). A property is excused only by name,
/// with the reason next to it: <see cref="NotResolved"/> for what is deliberately not resolved, <see cref="Renamed"/> for a member
/// with another name, and <see cref="Pending"/> for gaps this test found that a later round closes. A pending entry that becomes a
/// member fails the test until it is removed from the list.
/// </summary>
public sealed class ResolvedCoverageTests
{
    /// <summary>A schema object (a file's top level, or a part of it) and the resolved type it becomes.</summary>
    /// <param name="Label">How the findings name it.</param>
    /// <param name="File">The schema file.</param>
    /// <param name="Path">Steps from the file's root: a property name, <c>[]</c> for an array's items, <c>$defs/&lt;name&gt;</c>.</param>
    /// <param name="Type">The resolved type, or <see langword="null"/> when the kind has none (every property is then pending).</param>
    public sealed record Target(string Label, string File, string Path, Type? Type);

    public static readonly IReadOnlyList<Target> Targets =
    [
        // Physical kinds: fully resolved.
        new("database", "database.json", "", typeof(RDatabase)),
        new("database schema", "database.json", "schemas/[]", typeof(RSchema)),
        new("table", "table.json", "", typeof(RTable)),
        new("column", "table.json", "columns/[]", typeof(RColumn)),
        new("primary key", "table.json", "primaryKey", typeof(RPrimaryKey)),
        new("unique", "table.json", "uniques/[]", typeof(RUnique)),
        new("foreign key", "table.json", "foreignKeys/[]", typeof(RForeignKey)),
        new("check", "table.json", "checks/[]", typeof(RCheck)),
        new("index", "table.json", "indexes/[]", typeof(RIndex)),
        new("index column", "table.json", "indexes/[]/columns/[]", typeof(RIndexColumn)),
        new("view", "view.json", "", typeof(RView)),
        new("view column", "view.json", "columns/[]", typeof(RViewColumn)),
        new("sequence", "sequence.json", "", typeof(RSequence)),
        new("routine", "routine.json", "", typeof(RRoutine)),
        new("routine parameter", "routine.json", "parameters/[]", typeof(RRoutineParameter)),
        new("routine result", "routine.json", "returns", typeof(RRoutineReturns)),
        new("routine result column", "routine.json", "returns/table/[]", typeof(RRoutineColumn)),
        new("database type", "database-type.json", "", typeof(RDatabaseType)),
        new("database type field", "database-type.json", "fields/[]", typeof(RDatabaseTypeField)),
        new("sql object", "sql-object.json", "", typeof(RSqlObject)),
        new("query", "query.json", "", typeof(RQuery)),
        new("query parameter", "query.json", "$defs/parameter", typeof(RQueryParameter)),
        new("query source", "query.json", "$defs/from", typeof(RQuerySource)),
        new("query join", "query.json", "$defs/join", typeof(RQuerySource)),
        new("query field", "query.json", "$defs/field", typeof(RQueryField)),
        new("query order", "query.json", "$defs/order", typeof(RQueryOrder)),
        new("query paging", "query.json", "$defs/paging", typeof(RQueryPaging)),
        new("query collection", "query.json", "$defs/collection", typeof(RQueryCollection)),
        new("nested query", "query.json", "$defs/subquery", typeof(RQuery)),
        new("case branch", "query.json", "$defs/when", typeof(RQueryWhen)),
        new("expression", "query.json", "$defs/expression", typeof(RQueryExpression)),
        new("predicate", "query.json", "$defs/predicate", typeof(RQueryPredicate)),

        // Conceptual kinds and their parts.
        new("package", "package.json", "", typeof(RPackage)),
        new("entity", "entity.json", "", typeof(REntity)),
        new("attribute", "entity.json", "attributes/[]", typeof(RAttribute)),
        new("derived value", "entity.json", "attributes/[]/derived", typeof(RDerived)),
        new("entity check", "entity.json", "checks/[]", typeof(REntityCheck)),
        new("value object", "value-object.json", "", typeof(RValueObject)),
        new("scalar type", "scalar-type.json", "", typeof(RScalarType)),
        new("enum", "enum.json", "", typeof(REnum)),
        new("enum member", "enum.json", "members/[]", typeof(REnumMember)),
        new("relation", "relation.json", "", typeof(RRelation)),
        new("relation end", "relation.json", "ends/[]", typeof(REnd)),

        // Reference data.
        new("reference type", "reference-type.json", "", typeof(RReferenceType)),
        new("former code", "reference-type.json", "formerCodes/[]", typeof(RFormerCode)),
        new("seed", "seed.json", "", typeof(RSeed)),
        new("seed row", "seed.json", "rows/[]", typeof(RSeedRow)),

        // Processes, actors and scenarios.
        new("process", "process.json", "", typeof(RProcess)),
        new("state", "process.json", "$defs/state", typeof(RState)),
        new("subject", "process.json", "$defs/subject", typeof(RSubject)),
        new("transition", "process.json", "$defs/transition", typeof(RTransition)),
        new("event", "process.json", "$defs/event", typeof(REvent)),
        new("guard", "process.json", "$defs/guard", typeof(RGuard)),
        new("action", "process.json", "$defs/action", typeof(RAction)),
        new("set", "process.json", "$defs/set", typeof(RSet)),
        new("invoke", "process.json", "$defs/invoke", typeof(RInvoke)),
        new("gate", "process.json", "$defs/gate", typeof(RGate)),
        new("meaning", "process.json", "$defs/meaning", typeof(RMeaning)),
        new("actor", "actor.json", "", typeof(RActor)),
        new("scenario", "scenario.json", "", typeof(RScenario)),
        new("step", "scenario.json", "$defs/step", typeof(RStep)),
        new("scenario object", "scenario.json", "$defs/object", typeof(RScenarioObject)),
        new("scenario link", "scenario.json", "$defs/link", typeof(RScenarioLink)),

        // Rights (rights-design.md section 6): a named right resolves as an RRight; an entry of a lifecycle's rights as its RRightUse and, for an
        // inline right, as the RRight that shares its id.
        new("right", "right.json", "", typeof(RRight)),
        new("process right", "process.json", "$defs/right", typeof(RRightUse)),
        new("inline right", "process.json", "$defs/right", typeof(RRight)),

        // Vocabularies, mappings and diagrams.
        new("stereotype", "stereotype.json", "", typeof(RStereotypeDefinition)),
        new("category", "category-tree.json", "categories/[]", typeof(RCategory)),
        new("category tree", "category-tree.json", "", typeof(RCategoryTree)),
        new("tag vocabulary", "tag-vocabulary.json", "", typeof(RTagVocabulary)),
        new("verb vocabulary", "verb-vocabulary.json", "", null),
        new("own verb", "verb-vocabulary.json", "verbs/[]", typeof(RVerb)),
        new("mapping", "mapping.json", "", null),
        new("diagram", "diagram.json", "", null),

        // Migration facts (next-phase-1-design.md section 3.2): each database's open facts in applied order.
        new("migration", "migration.json", "", typeof(RMigration)),
        new("migration object", "migration.json", "$defs/migrationObject", typeof(RMigrationObject)),
        new("migration state", "migration.json", "$defs/migrationState", typeof(RMigrationState)),
        new("migration key", "migration.json", "keys/[]", typeof(RMigrationRekey)),
        new("migration value", "migration.json", "values/[]", typeof(RMigrationValue)),
        new("migration collection", "migration.json", "collections/[]", typeof(RMigrationCollection)),
        new("migration conversion", "migration.json", "conversion", typeof(RConversion)),
        new("migration data check", "migration.json", "dataCheck", typeof(RMigrationDataCheck)),

        // Operations (next-phase-2-design.md section 4.1).
        new("operation", "operation.json", "", typeof(ROperation)),
        new("operation member", "operation.json", "$defs/member", typeof(ROperationMember)),
        new("outcome", "operation.json", "$defs/outcome", typeof(ROperationOutcome)),
        new("outcome returns", "operation.json", "$defs/returns", typeof(ROperationReturns)),
        new("error", "common.json", "$defs/operationError", typeof(ROperationError)),
        new("stereotype error", "stereotype.json", "errors/[]", typeof(ROperationError)),
        new("operation guard", "operation.json", "$defs/guard", typeof(ROperationGuard)),

        // Custom elements (next-phase-2-design.md section 4.2).
        new("custom", "custom.json", "", typeof(RCustom)),

        // Expressions (next-phase-3-design.md section 3.8).
        new("constraint", "constraint.json", "", typeof(RConstraint)),
        new("constraint parameter", "constraint.json", "$defs/parameter", typeof(RConstraintParameter)),
    ];

    /// <summary>Properties no resolved object carries, anywhere, and why.</summary>
    internal static readonly Dictionary<string, string> NotResolvedAnywhere = new(StringComparer.Ordinal)
    {
        // The relative path to the file's JSON schema: editor completion for the file, not part of the model.
        ["$schema"] = "file layout",
        // Import provenance (format, source name, location, fingerprint): read from the file by the importers that reconcile a
        // re-import; templates describe the model, not where an element came from.
        ["source"] = "import provenance",
        // Findings an element accepts, each with its reason (next-phase-2-design.md section 5.6).
        ["suppressions"] = "accepted findings: validation reads them, templates describe the model",
    };

    /// <summary>Properties of one target that are deliberately not resolved, and why.</summary>
    private static readonly Dictionary<(string Target, string Property), string> NotResolved = WithDefaultLists(new()
    {
        // A stereotype's storage reaches templates merged under a table's own (table.storage), as its defaultProperties do on each
        // carrier; model.stereotypes lists the definition without it (next-phase-1-design.md section 5.5).
        [("stereotype", "storage")] = "merged into table.storage",
        // A category is the one summary every annotated object in it shares (id, name, path, parent, children, order, description,
        // rationale, decision and status, next-phase-1-design.md sections 4.5 and 5.5); its own names and other annotations describe
        // the vocabulary, not the model.
        [("category", "displayName")] = "category summary",
        [("category", "pluralName")] = "category summary",
        [("category", "stereotypes")] = "category summary",
        [("category", "tags")] = "category summary",
        [("category", "category")] = "category summary",
        [("category", "properties")] = "category summary",
        [("category", "generation")] = "category summary",
        // A named right and an inline right are one shape, RRight (rights-design.md section 6): its name, display name, package and
        // description, as a process part's; the annotations only an element carries are not part of it.
        [("right", "pluralName")] = "RRight: one shape for named and inline rights",
        [("right", "rationale")] = "RRight: one shape for named and inline rights",
        [("right", "decision")] = "RRight: one shape for named and inline rights",
        [("right", "status")] = "RRight: one shape for named and inline rights",
        [("right", "stereotypes")] = "RRight: one shape for named and inline rights",
        [("right", "tags")] = "RRight: one shape for named and inline rights",
        [("right", "category")] = "RRight: one shape for named and inline rights",
        [("right", "properties")] = "RRight: one shape for named and inline rights",
        [("right", "generation")] = "RRight: one shape for named and inline rights",
        // An entry of a lifecycle's rights: what an inline right says is on use.right, the "inline right" target; where it holds is on the use.
        [("process right", "name")] = "on use.right (the inline right target)",
        [("process right", "displayName")] = "on use.right (the inline right target)",
        [("process right", "access")] = "on use.right (the inline right target)",
        [("process right", "actors")] = "on use.right (the inline right target)",
        [("process right", "except")] = "on use.right (the inline right target)",
        [("process right", "verbs")] = "on use.right (the inline right target)",
        [("process right", "ends")] = "on use.right (the inline right target)",
        [("process right", "description")] = "on use.right (the inline right target)",
        [("process right", "attributes")] = "on use.right (the inline right target)",
        [("process right", "whileFrozen")] = "on use.right (the inline right target)",
        [("inline right", "right")] = "a use's named right: RRightUse.Right (the process right target)",
        [("inline right", "states")] = "where the right holds: RRightUse.States (the process right target)",
    });

    /// <summary>
    /// Default stereotype lists, exclusions and contributed members (next-phase-2-design.md section 3.1): a database schema's and a
    /// package's <c>defaultStereotypes</c> and every top-level <c>excludeStereotypes</c> (a migration fact has none) shape the effective
    /// set of the elements they reach, and a stereotype's members, with a table's <c>redefines</c> and <c>excludes</c>, resolve only on
    /// each carrying table as its inherited members, rather than as members of their own.
    /// </summary>
    private static Dictionary<(string Target, string Property), string> WithDefaultLists(Dictionary<(string Target, string Property), string> map)
    {
        const string reason = "applied through the effective set and inherited members (MemberOrigin, RStereotype.Source)";
        foreach (var target in Targets.Where(t => t.Path.Length == 0 && t.File != "migration.json"))
            map[(target.Label, "excludeStereotypes")] = reason;
        map[("package", "defaultStereotypes")] = reason;
        map[("database schema", "defaultStereotypes")] = reason;
        foreach (var member in new[] { "columns", "primaryKey", "uniques", "foreignKeys", "checks", "indexes" })
            map[("stereotype", member)] = reason;
        map[("table", "redefines")] = reason;
        map[("table", "excludes")] = reason;
        // An operation's redefines and excludes shape the errors it inherits (next-phase-2-design.md section 4.1).
        map[("operation", "redefines")] = "applied to Errors (MemberOrigin)";
        map[("operation", "excludes")] = "applied to Errors (MemberOrigin)";
        return map;
    }

    /// <summary>Properties resolved under another member name.</summary>
    internal static readonly Dictionary<(string Target, string Property), string> Renamed = new()
    {
        [("transition", "gatePolicy")] = "Gate",
        // A subject's owner field is an attribute or a to-one end (actors-design.md section 6): OwnerAttribute or OwnerEnd.
        [("subject", "owner")] = "OwnerAttribute",
        // The overlay entry's synthesized column key is the column's key.
        [("column", "attribute")] = "Key",
        // A join's kind is the source's join kind (from for the first source).
        [("query join", "kind")] = "JoinKind",
        // identity or sequence: Identity says the first, Sequence names the second.
        [("column", "generated")] = "Identity",
        [("foreign key", "referencesTable")] = "ReferencedTable",
        [("foreign key", "referencesColumns")] = "ReferencedColumns",
        [("entity", "abstract")] = "IsAbstract",
        // A state's nested states are its children (the tree).
        [("state", "states")] = "Children",
        // A returns' list flag is IsList (next-phase-2-design.md section 4.1).
        [("outcome returns", "list")] = "IsList",
        [("outcome returns", "file")] = "IsFile",
        [("operation member", "file")] = "IsFile",
        // A right's condition as text from the current names; the typed tree is each use's (RRightUse.Condition).
        [("right", "condition")] = "ConditionText",
        [("inline right", "condition")] = "ConditionText",
    };

    /// <summary>
    /// Gaps this test found outside the physical model on 2026-10-01, left for a later round: each is a property of the file that no
    /// resolved member carries yet. The physical kinds (database, schema, table, column, constraints, view, sequence, routine, database type, SQL object, query) have none.
    /// </summary>
    private static readonly HashSet<(string Target, string Property)> Pending =
    [
        // An enum member resolves its name, display name, value, code, description, rationale, decision, status, order and
        // properties only.
        ("enum member", "pluralName"), ("enum member", "stereotypes"), ("enum member", "tags"), ("enum member", "category"),
        ("enum member", "generation"),

        // A relation end resolves its role, entity, cardinality and navigation, not its own names and description.
        ("relation end", "displayName"), ("relation end", "pluralName"), ("relation end", "description"),

        // A stereotype's group, requires, conflicts, implies and parameters (next-phase-1-design.md section 4.2) are judged by
        // validation and read by templates through the effective set (each object's stereotypes, implied ones with implied_by, and
        // the merged properties), not as members of the definition model.stereotypes lists (section 5.5).
        ("stereotype", "group"), ("stereotype", "requires"), ("stereotype", "conflicts"), ("stereotype", "implies"), ("stereotype", "parameters"),
    ];

    /// <summary>
    /// Kinds with no resolved object at all (found 2026-10-01, a later round): every property of their files is pending. A mapping
    /// resolves into <c>entity.mappings</c> and <c>relation.mappings</c> (<see cref="REntityMapping"/>, <see cref="RRelationMapping"/>)
    /// rather than an object of its own, so its annotations and its own fields are not reachable; a diagram is not resolved. The tag
    /// vocabularies and category trees resolve into <c>model.tag_vocabularies</c> and <c>model.category_trees</c>
    /// (next-phase-1-design.md section 5.5). The verb vocabulary resolves into <c>model.verbs</c> (verbs-design.md section 6): each own verb an
    /// <see cref="RVerb"/>, and its hidden built-in verbs left out unless named, so the file itself is no object.
    /// </summary>
    private static readonly HashSet<string> Unresolved = new(StringComparer.Ordinal) { "mapping", "diagram", "verb vocabulary" };

    [Fact]
    public void Every_property_of_every_model_file_is_a_member_of_its_resolved_object()
    {
        var missing = new List<string>();
        foreach (var target in Targets)
        {
            foreach (var property in Properties(target))
            {
                if (NotResolvedAnywhere.ContainsKey(property) || NotResolved.ContainsKey((target.Label, property)) || Pending.Contains((target.Label, property))
                    || Unresolved.Contains(target.Label))
                    continue;
                if (target.Type is null || Member(target.Type, Renamed.GetValueOrDefault((target.Label, property)) ?? property) is null)
                    missing.Add($"{target.Label}.{property}");
            }
        }

        Assert.True(missing.Count == 0, "Resolved without these file properties: " + string.Join(", ", missing));
    }

    [Fact]
    public void Excused_properties_exist_in_the_schemas_and_pending_ones_are_still_missing()
    {
        var byLabel = Targets.ToDictionary(t => t.Label, StringComparer.Ordinal);
        foreach (var (label, property) in NotResolved.Keys.Concat(Renamed.Keys).Concat(Pending))
        {
            Assert.True(byLabel.ContainsKey(label), $"No target '{label}'.");
            Assert.Contains(property, Properties(byLabel[label]));
        }

        foreach (var ((label, property), member) in Renamed)
            Assert.True(Member(byLabel[label].Type!, member) is not null, $"{label}.{property}: {byLabel[label].Type!.Name} has no {member}.");
        var fixedOnes = Pending.Where(p => byLabel[p.Target].Type is { } type && Member(type, p.Property) is not null).ToList();
        Assert.True(fixedOnes.Count == 0, "Resolved now, remove from Pending: " + string.Join(", ", fixedOnes.Select(p => p.Target + "." + p.Property)));
        foreach (var label in Unresolved)
            Assert.True(byLabel[label].Type is null, $"'{label}' has a resolved type now: list what it still lacks in Pending instead.");
    }

    [Fact]
    public void Every_document_kind_has_a_target()
    {
        var files = Targets.Select(t => t.File).ToHashSet(StringComparer.Ordinal);
        foreach (var kind in Maquettiste.Engine.Model.KindInfo.All)
            Assert.Contains(kind.SchemaFile, files);
    }

    /// <summary>The resolved member a target's schema property maps to (through <see cref="Renamed"/>), or <see langword="null"/>.</summary>
    internal static PropertyInfo? MemberOf(Target target, string property) =>
        target.Type is null ? null : Member(target.Type, Renamed.GetValueOrDefault((target.Label, property)) ?? property);

    private static PropertyInfo? Member(Type type, string property) =>
        type.GetProperty(char.ToUpperInvariant(property[0]) + property[1..], BindingFlags.Public | BindingFlags.Instance);

    /// <summary>The property names the schema object declares, in schema order.</summary>
    private static IReadOnlyList<string> Properties(Target target) => [.. SchemaObject(target).Node["properties"]!.AsObject().Select(p => p.Key)];

    /// <summary>The schema object a target names, with the file its relative references resolve against.</summary>
    internal static (JsonNode Node, string File) SchemaObject(Target target)
    {
        var (node, file) = Resolve(Load(target.File), target.File);
        foreach (var step in target.Path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (step == "$defs")
            {
                node = Load(file)["$defs"]!;
                continue;
            }

            node = step == "[]" ? node["items"]! : node["properties"]?[step] ?? node[step]!;
            (node, file) = Resolve(node, file);
        }

        return (node, file);
    }

    /// <summary>Follows <c>$ref</c> from a node that declares no properties of its own.</summary>
    internal static (JsonNode Node, string File) Resolve(JsonNode node, string file)
    {
        while (node["$ref"]?.GetValue<string>() is { } reference && node["properties"] is null)
        {
            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            if (hash > 0)
                file = reference[..hash];
            node = Load(file);
            foreach (var part in reference[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                node = node[part]!;
        }

        return (node, file);
    }

    internal static JsonNode Load(string file) => JsonNode.Parse(TestServices.Schemas.GetFileBytes(file).Span)!;
}
