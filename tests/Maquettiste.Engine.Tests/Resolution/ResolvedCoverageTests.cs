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
        new("value object", "value-object.json", "", typeof(RValueObject)),
        new("scalar type", "scalar-type.json", "", typeof(RScalarType)),
        new("enum", "enum.json", "", typeof(REnum)),
        new("enum member", "enum.json", "members/[]", typeof(REnumMember)),
        new("relation", "relation.json", "", typeof(RRelation)),
        new("relation end", "relation.json", "ends/[]", typeof(REnd)),

        // Reference data.
        new("reference type", "reference-type.json", "", typeof(RReferenceType)),
        new("seed", "seed.json", "", typeof(RSeed)),
        new("seed row", "seed.json", "rows/[]", typeof(RSeedRow)),

        // Processes, actors and scenarios.
        new("process", "process.json", "", typeof(RProcess)),
        new("state", "process.json", "$defs/state", typeof(RState)),
        new("transition", "process.json", "$defs/transition", typeof(RTransition)),
        new("event", "process.json", "$defs/event", typeof(REvent)),
        new("guard", "process.json", "$defs/guard", typeof(RGuard)),
        new("action", "process.json", "$defs/action", typeof(RAction)),
        new("invoke", "process.json", "$defs/invoke", typeof(RInvoke)),
        new("gate", "process.json", "$defs/gate", typeof(RGate)),
        new("meaning", "process.json", "$defs/meaning", typeof(RMeaning)),
        new("actor", "actor.json", "", typeof(RActor)),
        new("scenario", "scenario.json", "", typeof(RScenario)),
        new("step", "scenario.json", "$defs/step", typeof(RStep)),

        // Vocabularies, mappings and diagrams.
        new("stereotype", "stereotype.json", "", typeof(RStereotype)),
        new("category", "category-tree.json", "categories/[]", typeof(RCategory)),
        new("category tree", "category-tree.json", "", null),
        new("tag vocabulary", "tag-vocabulary.json", "", null),
        new("mapping", "mapping.json", "", null),
        new("diagram", "diagram.json", "", null),
    ];

    /// <summary>Properties no resolved object carries, anywhere, and why.</summary>
    private static readonly Dictionary<string, string> NotResolvedAnywhere = new(StringComparer.Ordinal)
    {
        // The relative path to the file's JSON schema: editor completion for the file, not part of the model.
        ["$schema"] = "file layout",
        // Import provenance (format, source name, location, fingerprint): read from the file by the importers that reconcile a
        // re-import; templates describe the model, not where an element came from.
        ["source"] = "import provenance",
    };

    /// <summary>Properties of one target that are deliberately not resolved, and why.</summary>
    private static readonly Dictionary<(string Target, string Property), string> NotResolved = new()
    {
    };

    /// <summary>Properties resolved under another member name.</summary>
    private static readonly Dictionary<(string Target, string Property), string> Renamed = new()
    {
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
    };

    /// <summary>
    /// Gaps this test found outside the physical model on 2026-10-01, left for a later round: each is a property of the file that no
    /// resolved member carries yet. The physical kinds (database, schema, table, column, constraints, view, sequence, routine, database type, SQL object, query) have none.
    /// </summary>
    private static readonly HashSet<(string Target, string Property)> Pending =
    [
        // An enum member resolves its name, display name, value, code, description, order and properties only.
        ("enum member", "pluralName"), ("enum member", "stereotypes"), ("enum member", "tags"), ("enum member", "category"),
        ("enum member", "generation"),

        // A relation end resolves its role, entity, cardinality and navigation, not its own names and description.
        ("relation end", "displayName"), ("relation end", "pluralName"), ("relation end", "description"),

        // A stereotype reaches templates only as the summary an annotated object carries (key, name, icon, color).
        ("stereotype", "kind"), ("stereotype", "id"), ("stereotype", "displayName"), ("stereotype", "pluralName"), ("stereotype", "description"),
        ("stereotype", "stereotypes"), ("stereotype", "tags"), ("stereotype", "category"), ("stereotype", "appliesTo"), ("stereotype", "attributes"),
        ("stereotype", "defaultProperties"), ("stereotype", "properties"), ("stereotype", "generation"),
        // A stereotype's storage reaches templates merged under a table's own (table.storage), as its defaultProperties do.
        ("stereotype", "storage"),

        // A category reaches templates only as the summary an annotated object carries (id, name, path).
        ("category", "displayName"), ("category", "pluralName"), ("category", "parent"), ("category", "order"), ("category", "description"),
        ("category", "stereotypes"), ("category", "tags"), ("category", "category"), ("category", "properties"), ("category", "generation"),
    ];

    /// <summary>
    /// Kinds with no resolved object at all (found 2026-10-01, a later round): every property of their files is pending. A mapping
    /// resolves into <c>entity.mappings</c> and <c>relation.mappings</c> (<see cref="REntityMapping"/>, <see cref="RRelationMapping"/>)
    /// rather than an object of its own, so its annotations and its own fields are not reachable; a diagram, the tag vocabulary and
    /// the category tree are not resolved (tags reach templates as keys, categories as the summary above).
    /// </summary>
    private static readonly HashSet<string> Unresolved = new(StringComparer.Ordinal) { "category tree", "tag vocabulary", "mapping", "diagram" };

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

    private static PropertyInfo? Member(Type type, string property) =>
        type.GetProperty(char.ToUpperInvariant(property[0]) + property[1..], BindingFlags.Public | BindingFlags.Instance);

    /// <summary>The property names the schema object declares, in schema order.</summary>
    private static IReadOnlyList<string> Properties(Target target)
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

        return [.. node["properties"]!.AsObject().Select(p => p.Key)];
    }

    private static (JsonNode Node, string File) Resolve(JsonNode node, string file)
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

    private static JsonNode Load(string file) => JsonNode.Parse(TestServices.Schemas.GetFileBytes(file).Span)!;
}
