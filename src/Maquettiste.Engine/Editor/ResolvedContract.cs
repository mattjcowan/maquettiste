using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Maquettiste.Engine;

/// <summary>
/// The contract of the resolved model (docs/engineering/resolved-contract-design.md): what <see cref="GenerationService.GetResolvedAsync"/>
/// answers, as the JSON Schema <c>schemas/v1/resolved-model.json</c> and the reference <c>docs/resolved-model.md</c>. Both are generated
/// from the record types and their XML docs by the engine tests (<c>ResolvedContractWriter</c>) and checked in; the engine embeds both,
/// so a project's <c>.schema/v1/</c> copy follows the release. Until version 1 the contract version is the release and may change in any
/// release.
/// </summary>
public static class ResolvedContract
{
    /// <summary>The schema's file name, which every answer names in <c>schema</c>.</summary>
    public const string SchemaFile = "resolved-model.json";

    /// <summary>The reference's file name in <c>.schema/v1/</c> (and <c>docs/</c>).</summary>
    public const string ReferenceFile = "resolved-model.md";

    /// <summary>The name <c>get_schema</c> takes for the whole schema; <c>resolved-model:&lt;scope&gt;</c> takes one scope's slice.</summary>
    public const string SchemaName = "resolved-model";

    /// <summary>The prefix of a scope slice's name in <c>get_schema</c>.</summary>
    public const string ScopePrefix = SchemaName + ":";

    private const string SchemaResource = "Maquettiste.Engine.Schemas.v1." + SchemaFile;
    private const string ReferenceResource = "Maquettiste.Engine.Contract." + ReferenceFile;

    private static readonly Lazy<byte[]> Schema_ = new(() => Read(SchemaResource), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<byte[]> Reference = new(() => Read(ReferenceResource), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<string> ContractVersion = new(() => Whole()["x-contract"]!.GetValue<string>(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly ConcurrentDictionary<string, string> Texts = new(StringComparer.Ordinal);

    /// <summary>
    /// The contract version every answer states in <c>contract</c>: the <c>x-contract</c> of the schema this build embeds, so an answer
    /// always names the version of the schema shipped beside it. Until version 1 it is the version number of the release the files were
    /// generated for (the engine tests keep it equal to the repository's release).
    /// </summary>
    public static string Version => ContractVersion.Value;

    /// <summary>The record type of each record kind a page may hold, in scope order, then the kinds only <c>all</c> lists.</summary>
    public static IReadOnlyList<KeyValuePair<string, Type>> RecordTypes { get; } =
    [
        new("package", typeof(PackageRecord)),
        new("entity", typeof(EntityRecord)),
        new("relation", typeof(RelationRecord)),
        new("enum", typeof(EnumRecord)),
        new("value-object", typeof(ValueObjectRecord)),
        new("scalar-type", typeof(ScalarTypeRecord)),
        new("reference-type", typeof(ReferenceTypeRecord)),
        new("seed", typeof(SeedRecord)),
        new("process", typeof(ProcessRecord)),
        new("actor", typeof(ActorRecord)),
        new("scenario", typeof(ScenarioRecord)),
        new("operation", typeof(OperationRecord)),
        new("custom", typeof(CustomRecord)),
        new("constraint", typeof(ConstraintRecord)),
        new("database", typeof(DatabaseRecord)),
        new("table", typeof(TableRecord)),
        new("routine", typeof(RoutineRecord)),
        new("database-type", typeof(DatabaseTypeRecord)),
        new("sql-object", typeof(SqlObjectRecord)),
        new("query", typeof(QueryRecord)),
        new("migration", typeof(MigrationRecord)),
        new("verb", typeof(VerbRecord)),
        new("right", typeof(RightRecord)),
        new("trigger-point", typeof(NamedTriggerPointRecord)),
    ];

    /// <summary>The embedded schema's bytes, as the project copy holds them.</summary>
    public static ReadOnlyMemory<byte> SchemaBytes => Schema_.Value;

    /// <summary>The embedded reference's bytes, as the project copy and <c>docs/</c> hold them.</summary>
    public static ReadOnlyMemory<byte> ReferenceBytes => Reference.Value;

    /// <summary>
    /// The schema of one scope's pages, or of every page (<c>all</c> or <see langword="null"/>): the envelope with <c>items</c> narrowed to
    /// the scope's record, and only the definitions it reaches. Each call returns a new node.
    /// </summary>
    /// <param name="scope">A scope of <see cref="ResolvedRecords.ScopeNames"/>, or <see langword="null"/> for the whole schema.</param>
    /// <returns>The schema.</returns>
    /// <exception cref="ArgumentException">An unknown scope.</exception>
    public static JsonObject Schema(string? scope) => JsonNode.Parse(SchemaText(scope))!.AsObject();

    /// <summary>
    /// <see cref="Schema"/> as compact JSON text, built once per scope and kept (the schema never changes in a process).
    /// </summary>
    /// <param name="scope">A scope of <see cref="ResolvedRecords.ScopeNames"/>, or <see langword="null"/> for the whole schema.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentException">An unknown scope.</exception>
    public static string SchemaText(string? scope)
    {
        scope ??= "all";
        if (scope != "all" && !ResolvedRecords.Scopes.ContainsKey(scope))
            throw new ArgumentException($"scope must be one of {string.Join(", ", ResolvedRecords.ScopeNames)}, not '{scope}'.", nameof(scope));
        return Texts.GetOrAdd(scope, s => (s == "all" ? Whole() : Slice(s)).ToJsonString());
    }

    private static JsonObject Slice(string scope)
    {
        var whole = Whole();
        var definition = DefinitionOf(whole, ResolvedRecords.Scopes[scope]);
        var definitions = whole["$defs"]!.AsObject();
        var slice = new JsonObject();
        foreach (var (key, value) in whole)
        {
            if (key is "$defs" or "x-scopes" or "x-kinds")
                continue;
            slice[key] = value?.DeepClone();
        }

        slice["x-scope"] = scope;
        slice["properties"]!["items"]!["items"] = new JsonObject { ["$ref"] = "#/$defs/" + definition };
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var pending = new Queue<JsonNode?>([slice]);
        while (pending.TryDequeue(out var node))
        {
            foreach (var name in References(node))
            {
                if (reached.Add(name))
                    pending.Enqueue(definitions[name]);
            }
        }

        var kept = new JsonObject();
        foreach (var name in reached)
            kept[name] = definitions[name]!.DeepClone();
        slice["$defs"] = kept;
        return slice;
    }

    /// <summary>The <c>$defs</c> name of a record kind's definition.</summary>
    internal static string DefinitionOf(JsonObject whole, string kind) => whole["x-kinds"]![kind]!.GetValue<string>();

    /// <summary>The whole embedded schema as a new node.</summary>
    internal static JsonObject Whole() => JsonNode.Parse(SchemaBytes.Span)!.AsObject();

    /// <summary>The <c>$defs</c> names a node's <c>$ref</c>s name, at any depth.</summary>
    internal static IEnumerable<string> References(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == "$ref" && value is JsonValue v && v.TryGetValue<string>(out var target) && target.StartsWith("#/$defs/", StringComparison.Ordinal))
                        yield return target["#/$defs/".Length..];
                    else
                    {
                        foreach (var name in References(value))
                            yield return name;
                    }
                }

                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    foreach (var name in References(item))
                        yield return name;
                }

                break;
        }
    }

    private static byte[] Read(string resource)
    {
        using var stream = typeof(ResolvedContract).Assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing resource {resource}.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
