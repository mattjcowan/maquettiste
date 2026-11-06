using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Scripting;
using Maquettiste.Engine.Writing;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// The null contract of the resolved model (next-phase-1-design.md section 5.5): a member backed by a schema property with a
/// default carries it; an absent value is <see langword="null"/>, never <c>""</c> or <see langword="false"/>; a derived member (a
/// fallback, an inference) is listed in <see cref="Derived"/> with its rule. Lists and maps are never absent: an absent one is
/// empty.
/// </summary>
public sealed class ResolvedNullContractTests
{
    /// <summary>
    /// Members whose value the resolver derives (a fallback, an inference, a computed text) rather than reads as the file wrote it,
    /// keyed by <c>&lt;declaring type&gt;.&lt;member&gt;</c>, with the rule templates can rely on.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> Derived = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Physical objects.
        ["RDatabase.ByConvention"] = "the file's byConvention, else all without convention packages and packages with them",
        ["RColumn.Key"] = "the attribute path of a synthesized column (its overlay entry's attribute), else the column's own key",
        ["RColumn.Type"] = "the file's type, else the column type keyword its attribute's type maps to",
        ["RColumn.NativeType"] = "the file's nativeType (a database type's native name), else the dialect's type map for the type and facets",
        ["RColumn.Identity"] = "the file's identity or generated: identity, else true for the key column of an identity key strategy",
        ["RPrimaryKey.Name"] = "the file's name, else the naming conventions'",
        ["RUnique.Name"] = "the file's name, else the naming conventions'",
        ["RForeignKey.Name"] = "the file's name, else the naming conventions'",
        ["RCheck.Name"] = "the file's name, else the naming conventions'",
        ["RIndex.Name"] = "the file's name, else the naming conventions'",
        ["RRoutine.Language"] = "the file's language, else the dialect's own (plpgsql, tsql, sql)",
        ["RRoutine.Volatility"] = "the file's volatility, else immutable when deterministic, else volatile",
        ["RRoutineParameter.NativeType"] = Slot,
        ["RRoutineColumn.NativeType"] = Slot,
        ["RDatabaseTypeField.NativeType"] = Slot,
        ["RQueryParameter.NativeType"] = Slot,
        ["RQuerySource.Alias"] = "the file's alias, else the table's or view's name",
        ["RQueryField.Name"] = "the file's name, else the attribute's name",
        ["RQueryField.Nullable"] = "the file's nullable, else inferred from the expression and the joins it reads through",
        ["RQueryExpression.Null"] = "true for the null literal, false for every other node (the schema allows only true)",
        ["RColumn.Sensitive"] = "the column's own sensitive, else a projected column's attribute's own, else the strongest own value of the attributes bindings map to it",

        // Conceptual objects and their parts.
        ["RAttribute.Order"] = "the file's order, else the attribute's position among its owner's",
        ["RAttribute.Sensitive"] = "the attribute's own sensitive, else the strongest own value of the columns its entity's bindings map it to",
        ["REnumMember.DisplayName"] = "the file's displayName, else the name",
        ["RProcessNode.DisplayName"] = "the file's displayName, else the name (a transition's label)",
        ["RScenario.Start"] = "the file's start, else a start with its fields' defaults (no context values, the default instant)",
        ["ROperationError.Code"] = "the file's code, else the error's name (next-phase-2-design.md section 4.1)",
    };

    private const string Slot =
        "the file's nativeType, else its database type's native name, else the type map's; empty only when the database type has no native name for the dialect";

    /// <summary>Resolved view types neither a fixture model nor <see cref="Uncovered"/> reaches, and why.</summary>
    private static readonly IReadOnlyDictionary<string, string> Unreached = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // What a failed calculated value throws (next-phase-2-design.md section 5.5): an exception, not a view; no object holds one.
        ["CalculatedValueException"] = "an exception that reads of calculated values throw: CalculationEvaluationTests",
        // Migration facts need a baseline and a recorded change; MigrationResolutionTests resolves them.
        ["RMigration"] = "migration facts: MigrationResolutionTests",
        ["RMigrationObject"] = "migration facts: MigrationResolutionTests",
        ["RMigrationState"] = "migration facts: MigrationResolutionTests",
        ["RMigrationRekey"] = "migration facts: MigrationResolutionTests",
        ["RMigrationValue"] = "migration facts: MigrationResolutionTests",
        ["RMigrationDataCheck"] = "migration facts: MigrationResolutionTests",
        ["RConversion"] = "migration facts: MigrationResolutionTests",
        // A value map's collections (next-phase-2-design.md section 3.5); MigrationResolutionTests resolves them.
        ["RMigrationCollection"] = "value map collections: MigrationResolutionTests",
        // A reference type's former codes (section 3.5); ReferenceDataResolutionTests resolves them.
        ["RFormerCode"] = "former codes: ReferenceDataResolutionTests",
        // Actors worked out from the object (actors-design.md section 6); RelativeActorTests resolves them.
        ["RRelative"] = "actors from the object: RelativeActorTests",
        // A query's case branches; QueryTests resolves them.
        ["RQueryWhen"] = "case expressions: QueryTests",
        // A scenario's objects and their links (lifecycle-design.md decision 22); no fixture has them, ScenarioObjectTests resolves them.
        ["RScenarioObject"] = "scenario objects: ScenarioObjectTests",
        ["RScenarioLink"] = "scenario objects: ScenarioObjectTests",
        // A scenario's participants and their holdings (actors-design.md decision 14); no fixture has them, ParticipantTests resolves them.
        ["RParticipant"] = "scenario participants: ParticipantTests",
        ["RHolding"] = "scenario participants: ParticipantTests",
        // A reference type bound to a table; ReferenceBindingTests resolves it.
        ["RReferenceTypeBinding"] = "reference type bindings: ReferenceBindingTests",
        ["RReferenceBindingField"] = "reference type bindings: ReferenceBindingTests",
        // A table seed's foreign-key entry (next-phase-2-design.md section 3.4); TableSeedTests and ReferenceDataResolutionTests resolve it.
        ["RDataRef"] = "foreign-key entries of table seeds: TableSeedTests",
        // Expressions (next-phase-3-design.md section 3.1): no fixture model holds one, so that their golden output stays as it was;
        // ExpressionResolutionTests resolves them.
        ["RExpression"] = "expression trees: ExpressionResolutionTests",
        ["REntityCheck"] = "entity checks: ExpressionResolutionTests",
        ["RConstraint"] = "named constraints: ExpressionResolutionTests",
        ["RConstraintParameter"] = "named constraints: ExpressionResolutionTests",
        ["ROperationGuard"] = "operation guards: OperationGuardTests",
        ["ExprType"] = "the type of an expression node: ExpressionResolutionTests",
    };

    [Fact]
    public void Every_optional_value_without_a_default_is_nullable()
    {
        var nullability = new NullabilityInfoContext();
        var findings = new List<string>();
        foreach (var target in ResolvedCoverageTests.Targets)
        {
            if (target.Type is null)
                continue;
            var (node, file) = ResolvedCoverageTests.SchemaObject(target);
            var required = (node["required"] as JsonArray)?.Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (property, schema) in node["properties"]!.AsObject())
            {
                if (required.Contains(property) || ResolvedCoverageTests.NotResolvedAnywhere.ContainsKey(property) || HasDefault(schema!, file))
                    continue;
                if (ResolvedCoverageTests.MemberOf(target, property) is not { } member || !IsValue(member.PropertyType))
                    continue;
                if (Derived.ContainsKey(Key(member)) || IsNullable(nullability, member))
                    continue;
                findings.Add($"{target.Label}.{property} ({Key(member)}: {member.PropertyType.Name})");
            }
        }

        Assert.True(findings.Count == 0, "Optional values without a default that are not nullable: " + string.Join(", ", findings.Distinct(StringComparer.Ordinal)));
        var stale = Derived.Keys.Where(k => !ResolvedTypes().Any(t => t.GetProperty(k[(k.IndexOf('.', StringComparison.Ordinal) + 1)..]) is { } p
            && string.Equals(p.DeclaringType!.Name + "." + p.Name, k, StringComparison.Ordinal))).ToList();
        Assert.True(stale.Count == 0, "Derived names no member: " + string.Join(", ", stale));
    }

    [Fact]
    public async Task No_string_is_empty_unless_its_default_is()
    {
        var findings = new SortedSet<string>(StringComparer.Ordinal);
        var reached = new HashSet<Type>();
        foreach (var folder in Directory.EnumerateDirectories(Fixtures.Path("models")).Order(StringComparer.Ordinal))
        {
            var model = await ResolveAsync(folder);
            Walk(model, reached, (type, member) => findings.Add($"{Path.GetFileName(folder)}: {type.Name}.{member.Name}"));
        }

        var uncovered = ResolutionKit.Resolve(Uncovered());
        Walk(uncovered, reached, (type, member) => findings.Add($"uncovered: {type.Name}.{member.Name}"));

        Assert.True(findings.Count == 0, "Empty strings outside an empty default or a derived member: " + string.Join(", ", findings));

        // What the contract changed reads as null where the file has nothing for the dialect.
        var local = uncovered.Db("local");
        var routine = Assert.Single(local.Routines);
        Assert.Equal((null, false, null), (routine.Body, routine.HasBody, routine.Returns!.NativeType));
        Assert.Null(Assert.Single(local.Types).NativeName);
        Assert.Equal((null, false), (Assert.Single(local.Objects).Body, local.Objects[0].HasBody));
        Assert.Equal((null, null), (local.DisplayName, local.PluralName));

        var unreached = ResolvedTypes().Where(t => !reached.Any(t.IsAssignableFrom) && !Unreached.ContainsKey(t.Name))
            .Select(t => t.Name).Order(StringComparer.Ordinal).ToList();
        Assert.True(unreached.Count == 0, "View types no fixture reaches (add a fixture or list them with a reason): " + string.Join(", ", unreached));
        foreach (var name in Unreached.Keys)
            Assert.DoesNotContain(reached, t => string.Equals(t.Name, name, StringComparison.Ordinal));
    }

    /// <summary>The view types of the resolved model (<see cref="MemberCatalog.IsProxyType"/>).</summary>
    private static IEnumerable<Type> ResolvedTypes() => typeof(ResolvedModel).Assembly.GetTypes().Where(MemberCatalog.IsProxyType);

    /// <summary>
    /// What no fixture model holds: a routine, a database type and a SQL object on SQLite whose files have no SQLite text (so no body
    /// and no native name), a routine that returns a table, routine settings, a partitioned table with an exclusion constraint and
    /// storage parameters, a derived attribute, a view's generation hints and an operation with members, outcomes and an error.
    /// </summary>
    private static ModelSnapshot Uncovered()
    {
        var b = new ModelBuilder(seed: 557);
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("total", "decimal").Attr("doubled", "decimal", a => a.Derived("total * 2"));
        var main = b.Database("main", Dialect.PostgreSql);
        var local = b.Database("local", Dialect.Sqlite);
        b.Table("bookings", main).Column("id", "int64", nullable: false).Column("during", "string").PrimaryKey("id").Edit(t => t with
        {
            Exclusions = [new ExclusionConstraint { Id = b.NewId(), Elements = [new ExclusionElement { Column = t.Columns[1].Id, Operator = "&&" }] }],
            PartitionBy = new PartitionBy { Strategy = PartitionStrategy.Hash, Columns = [t.Columns[0].Id] },
            Partitions = [new TablePartition { Id = b.NewId(), Name = "bookings_rest", Default = true }],
            Storage = new Dictionary<string, IReadOnlyDictionary<string, JsonElement>>
            {
                ["postgresql"] = new Dictionary<string, JsonElement> { ["fillfactor"] = JsonSerializer.SerializeToElement(70) },
            },
        });
        var postgresOnly = new Dictionary<string, string> { ["postgresql"] = "select 1" };
        b.Add(new Routine
        {
            Id = b.NewId(), Name = "booking_count", Database = local.Id, Body = postgresOnly,
            Parameters = [new RoutineParameter { Name = "since", Type = "date" }],
            Returns = new RoutineReturns { Table = [new RoutineColumn { Name = "n", Type = "int64" }] },
            Settings = new Dictionary<string, string> { ["work_mem"] = "'64MB'" },
        });
        b.Add(new DatabaseType
        {
            Id = b.NewId(), Name = "address", Database = local.Id, TypeKind = DatabaseTypeKind.Composite,
            Fields = [new DatabaseTypeField { Name = "street", Type = "string", Length = 200 }],
        });
        b.Add(new SqlObject { Id = b.NewId(), Name = "bookings_audit", Database = local.Id, ObjectKind = "trigger", Body = postgresOnly });
        b.Add(new View
        {
            Id = b.NewId(), Name = "booking_totals", Database = main.Id, Body = new Dictionary<string, string> { ["*"] = "select 1 as one" },
            Generation = new Dictionary<string, GenerationHints> { ["sql-ddl"] = new GenerationHints { Rename = "totals" } },
        });
        var recipient = b.NewId();
        b.Add(new Operation
        {
            Id = b.NewId(), Name = "send", Subject = invoice.Id, Effect = OperationEffect.Change,
            Input = [new OperationMember { Id = recipient, Name = "recipient", Type = new TypeRef { Builtin = "string" } }, new OperationMember { Id = b.NewId(), Name = "copy", KeyOf = invoice.Id }],
            Outcomes =
            [
                new OperationOutcome { Id = b.NewId(), Name = "applied", Returns = new OperationReturns { Entity = invoice.Id } },
                new OperationOutcome { Id = b.NewId(), Name = "waiting", Returns = new OperationReturns { Members = [new OperationMember { Id = b.NewId(), Name = "trackingId", Type = new TypeRef { Builtin = "ulid" } }] } },
            ],
            Errors = [new OperationError { Id = b.NewId(), Name = "missing_recipient", Input = recipient }],
        });

        // A custom element with a member (next-phase-2-design.md section 4.2).
        b.Add(CustomKinds.CustomFixture.Element(b.NewId(), "invoice_sent", "message", $$"""{ "parts": [ { "id": "{{b.NewId()}}", "name": "subject" } ] }"""));
        return CustomKinds.CustomFixture.Snapshot(b, [CustomKinds.CustomFixture.KindDocument(CustomKinds.CustomFixture.MessageKind)]);
    }

    /// <summary>Whether a member is a single value (a scalar or an object), as opposed to a list or a map, which is empty when absent.</summary>
    private static bool IsValue(Type type) => type == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(type);

    private static bool IsNullable(NullabilityInfoContext context, PropertyInfo member) =>
        member.PropertyType.IsValueType ? Nullable.GetUnderlyingType(member.PropertyType) is not null : context.Create(member).ReadState == NullabilityState.Nullable;

    private static string Key(PropertyInfo member) => member.DeclaringType!.Name + "." + member.Name;

    /// <summary>The schema's default, following <c>$ref</c>; <see langword="null"/> when it declares none.</summary>
    private static JsonNode? DefaultOf(JsonNode schema, string file, out bool declared)
    {
        while (true)
        {
            if (schema is JsonObject o && o.TryGetPropertyValue("default", out var value))
            {
                declared = true;
                return value;
            }

            if (schema["$ref"]?.GetValue<string>() is not { } reference)
            {
                declared = false;
                return null;
            }

            var hash = reference.IndexOf('#', StringComparison.Ordinal);
            if (hash > 0)
                file = reference[..hash];
            schema = ResolvedCoverageTests.Load(file);
            foreach (var part in reference[(hash + 1)..].Split('/', StringSplitOptions.RemoveEmptyEntries))
                schema = schema[part]!;
        }
    }

    private static bool HasDefault(JsonNode schema, string file)
    {
        DefaultOf(schema, file, out var declared);
        return declared;
    }

    /// <summary>Whether a target of the type declares the member's property with the default <c>""</c>.</summary>
    private static bool DefaultsToEmpty(Type type, PropertyInfo member)
    {
        foreach (var target in ResolvedCoverageTests.Targets.Where(t => t.Type == type))
        {
            var (node, file) = ResolvedCoverageTests.SchemaObject(target);
            foreach (var (property, schema) in node["properties"]!.AsObject())
            {
                if (ResolvedCoverageTests.MemberOf(target, property) is { } mapped && mapped.Name == member.Name
                    && DefaultOf(schema!, file, out _) is JsonValue value && value.TryGetValue<string>(out var text) && text.Length == 0)
                    return true;
            }
        }

        return false;
    }

    /// <summary>Visits every resolved object reachable from the model and reports each <c>""</c> the contract does not allow.</summary>
    private static void Walk(ResolvedModel model, HashSet<Type> reached, Action<Type, PropertyInfo> report)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        void Enqueue(object? value)
        {
            switch (value)
            {
                case null or string:
                    return;
                case IEnumerable items:
                    foreach (var item in items)
                        Enqueue(item);
                    return;
            }

            var type = value.GetType();
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                Enqueue(type.GetProperty("Key")!.GetValue(value));
                Enqueue(type.GetProperty("Value")!.GetValue(value));
                return;
            }

            if (MemberCatalog.IsProxyType(type) && seen.Add(value))
                queue.Enqueue(value);
        }

        Enqueue(model);
        while (queue.TryDequeue(out var current))
        {
            var type = current.GetType();
            reached.Add(type);
            foreach (var member in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (member.GetIndexParameters().Length > 0)
                    continue;
                var value = member.GetValue(current);
                if (member.PropertyType == typeof(string))
                {
                    if (value is "" && !Derived.ContainsKey(Key(member)) && !Derived.ContainsKey(type.Name + "." + member.Name) && !DefaultsToEmpty(type, member))
                        report(type, member);
                    continue;
                }

                Enqueue(value);
            }
        }
    }

    private static async Task<ResolvedModel> ResolveAsync(string root)
    {
        var cache = Path.Combine(Path.GetTempPath(), "maquettiste-tests", "null-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cache);
        try
        {
            var options = new EngineOptions { RepoRoot = root, CacheDirectory = cache, MaxDegreeOfParallelism = 2 };
            var loader = new ModelLoader(options, TestServices.Schemas, TestServices.Json, new OutputPathPolicy(options, null));
            var loaded = await loader.LoadAsync(new LoadRequest(null, null, false), null, TestContext.Current.CancellationToken);
            return await new ModelResolver(options).ResolveAsync(loaded.Snapshot, null, TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public void Calculated_is_never_null()
    {
        var kit = new CalculationKit();
        var without = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing()));
        var with = kit.Resolve(CalculationKit.Snapshot(CalculationKit.Billing(),
            CalculationKit.File("a.js", "maquettiste.calculated({ name: 'a', kinds: ['entity'], schema: {}, value() { return 1; } });")));

        foreach (var model in new[] { without, with })
        {
            foreach (var value in ResolvedKinds.ObjectsOf(model, ResolvedKinds.All))
                Assert.NotNull(value.Calculated);
        }

        Assert.Same(RCalculated.Empty, without.Entities[0].Calculated);
        Assert.NotSame(RCalculated.Empty, with.Entities[0].Calculated);
        Assert.Contains("entity", ResolvedKinds.All);
        Assert.Contains("migration", ResolvedKinds.All);
        Assert.DoesNotContain("diagram", ResolvedKinds.All);
        Assert.Equal(typeof(REntity), ResolvedKinds.TypeOf("entity"));
    }
}
