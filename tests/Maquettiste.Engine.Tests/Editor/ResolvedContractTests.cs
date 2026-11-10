using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Json.Schema;
using System.Collections.Immutable;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Tests.Resolution;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>
/// The resolved-model contract (docs/engineering/resolved-contract-design.md section 7): the checked-in schema and reference match what
/// the writer generates from the record types, every scope's real output from the fixture models validates against them, and the
/// descriptions read as plain words.
/// </summary>
public sealed partial class ResolvedContractTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static string SchemaPath => Path.Combine(Fixtures.RepoRoot, "schemas", "v1", ResolvedContract.SchemaFile);

    private static string ReferencePath => Path.Combine(Fixtures.RepoRoot, "docs", ResolvedContract.ReferenceFile);

    /// <summary>The reference app's copies (samples/reference-app/tools/check-model.mjs compares them in gate 2).</summary>
    private static string SampleFolder => Path.Combine(Fixtures.RepoRoot, "samples", "reference-app", ".maquettiste", ".schema", "v1");

    /// <summary>
    /// The record definitions no model below produces, each with the feature test that does: those tests call
    /// <see cref="ContractCheck.Credited"/> naming them, which fails unless their model produces them.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NotProduced = new Dictionary<string, string>(ContractCheck.Credited, StringComparer.Ordinal)
    {
        // Pages carry diagnostics, records never do: The_schema_states_the_release_and_every_page_names_it validates a page with two.
        ["diagnostic"] = "a page of a model with errors",
    };

    /// <summary>
    /// Writes, or checks, every generated copy: <c>schemas/v1/resolved-model.json</c>, <c>docs/resolved-model.md</c> and the reference
    /// app's two copies. With <c>MAQUETTISTE_UPDATE_GOLDEN=1</c> it writes them all in one run for the version this build has
    /// (<see cref="ContractCheck.Regenerate"/>, the release step); the next build embeds them.
    /// </summary>
    [Fact]
    public void The_checked_in_schema_and_reference_match_the_record_types()
    {
        var schema = ResolvedContractWriter.Write(Path.Combine(AppContext.BaseDirectory, "Maquettiste.Engine.xml"), EngineVersion.Product);
        var reference = ResolvedContractReference.Render(JsonNode.Parse(schema)!.AsObject());
        var copies = new[]
        {
            (SchemaPath, schema), (ReferencePath, reference),
            (Path.Combine(SampleFolder, ResolvedContract.SchemaFile), schema), (Path.Combine(SampleFolder, ResolvedContract.ReferenceFile), reference),
        };
        if (Environment.GetEnvironmentVariable(Golden.UpdateVariable) == "1")
        {
            foreach (var (path, text) in copies)
                File.WriteAllText(path, text, new UTF8Encoding(false));
            return;
        }

        foreach (var (path, text) in copies)
        {
            // A checkout that turns line ends into CRLF still compares equal.
            Assert.True(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal) == text,
                $"{Path.GetRelativePath(Fixtures.RepoRoot, path).Replace('\\', '/')} is out of date with the record types. Regenerate the copies, " +
                $"rebuild and commit: {ContractCheck.Regenerate}");
        }

        // The engine embeds what was checked in when it was built, and every answer states the version inside it.
        Assert.Equal(schema, Encoding.UTF8.GetString(ResolvedContract.SchemaBytes.Span).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(reference, Encoding.UTF8.GetString(ResolvedContract.ReferenceBytes.Span).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(EngineVersion.Product, ResolvedContract.Version);
    }

    [Fact]
    public void The_schema_states_the_release_and_every_page_names_it()
    {
        var schema = ResolvedContract.Schema(null);
        Assert.Equal(ResolvedContract.Version, schema["x-contract"]!.GetValue<string>());
        Assert.Equal(ResolvedContract.Version, schema["properties"]!["contract"]!["const"]!.GetValue<string>());
        var page = JsonSerializer.SerializeToNode(new ResolvedPage([], null, []), Web)!.AsObject();
        Assert.Equal(["contract", "schema", "items", "next", "diagnostics", "calculated"], page.Select(p => p.Key));
        Assert.Equal(ResolvedContract.Version, page["contract"]!.GetValue<string>());
        Assert.Equal("resolved-model.json", page["schema"]!.GetValue<string>());
        Assert.Equal([.. ResolvedRecords.Scopes.Keys], schema["x-scopes"]!.AsObject().Select(s => s.Key));

        // A model with errors answers no records and its errors.
        var errors = new ResolvedPage([], null, [new Diagnostics.Diagnostic("MQ2001", Diagnostics.DiagnosticSeverity.Error, "A reference names nothing.", "01J0", "model/x.json", "/a", 3, 7),
            new Diagnostics.Diagnostic("MQ1001", Diagnostics.DiagnosticSeverity.Error, "Not JSON.", null, null, null, null, null)]);
        var evaluated = JsonSchema.FromText(schema.ToJsonString()).Evaluate(JsonSerializer.SerializeToElement(errors, Web));
        Assert.True(evaluated.IsValid);
    }

    [Fact]
    public void Every_record_and_field_is_described_in_plain_words()
    {
        var problems = new List<string>();
        foreach (var (name, node) in ResolvedContract.Schema(null)["$defs"]!.AsObject())
        {
            var definition = node!.AsObject();
            Check(name, definition["description"]?.GetValue<string>(), false);
            if (definition["properties"] is not JsonObject properties)
                continue;
            var required = definition["required"]!.AsArray().Select(r => r!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
            foreach (var (field, property) in properties)
            {
                // An open value ({}) accepts null too, so its description must say when it is null.
                var optional = !required.Contains(field) || ResolvedContractReference.CanBeNull(property!.AsObject());
                Check(name + "." + field, property!["description"]?.GetValue<string>(), optional);
            }
        }

        Assert.True(problems.Count == 0, "Descriptions to fix in the XML docs of the records:\n" + string.Join('\n', problems));

        void Check(string where, string? text, bool optional)
        {
            if (string.IsNullOrWhiteSpace(text))
                problems.Add(where + ": no description");
            else if (Internal().Match(text) is { Success: true } match)
                problems.Add($"{where}: cites '{match.Value}': {text}");
            else if (optional && !NullWords().IsMatch(text))
                problems.Add($"{where}: does not say when it is null or left out: {text}");
        }
    }

    [Fact]
    public void A_scope_slice_holds_its_record_and_what_it_reaches()
    {
        var whole = ResolvedContract.Schema(null);
        foreach (var (scope, kind) in ResolvedRecords.Scopes)
        {
            var slice = ResolvedContract.Schema(scope);
            var definition = ResolvedContract.DefinitionOf(whole, kind);
            Assert.Equal("#/$defs/" + definition, slice["properties"]!["items"]!["items"]!["$ref"]!.GetValue<string>());
            Assert.Equal(scope, slice["x-scope"]!.GetValue<string>());
            Assert.Null(slice["$defs"]!["record"]);
            Assert.NotNull(slice["$defs"]![definition]);
            Assert.All(ResolvedContract.References(slice), r => Assert.NotNull(slice["$defs"]![r]));
            _ = JsonSchema.FromText(slice.ToJsonString());
        }

        Assert.Equal(whole.ToJsonString(), ResolvedContract.Schema("all").ToJsonString());
        Assert.Throws<ArgumentException>(() => ResolvedContract.Schema("nothing"));
    }

    public static TheoryData<string> Models() => [.. Directory.EnumerateDirectories(Fixtures.Path("models")).Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal),
        .. Built.Keys];

    /// <summary>Models other tests build, for what no fixture holds.</summary>
    private static readonly IReadOnlyDictionary<string, Func<ResolvedModel>> Built = new Dictionary<string, Func<ResolvedModel>>(StringComparer.Ordinal)
    {
        ["built:uncovered"] = () => ResolutionKit.Resolve(ResolvedNullContractTests.Uncovered()),
        ["built:approval"] = () => ResolutionKit.Resolve(ResolvedApprovalTests.Model()),
        ["built:rights"] = () => ResolutionKit.Resolve(ResolvedRightsTests.Model()),
        ["built:revisions"] = () => ResolutionKit.Resolve(ResolvedRevisionTests.Model()),
        ["built:trigger-points"] = () => ResolutionKit.Resolve(ResolvedTriggerTests.Model()),
        ["built:approval-part2"] = () => ResolutionKit.Resolve(ResolvedApprovalPart2Tests.Model()),
        ["built:billing-code"] = () => ResolutionKit.Resolve(BillingFixture.Create()),
        ["built:migrations"] = () => ResolutionKit.Resolve(Migrations()),
    };

    [Theory]
    [MemberData(nameof(Models))]
    public async Task Every_scope_of_a_real_model_validates_against_the_schema(string model)
    {
        var produced = ContractCheck.Validate(await ResolveAsync(model));
        Assert.NotEmpty(produced);
    }

    [Fact]
    public async Task Every_record_is_produced_by_some_model()
    {
        var produced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in Models())
            produced.UnionWith(ContractCheck.Validate(await ResolveAsync(model.Data)));
        var definitions = ResolvedContract.Schema(null)["$defs"]!.AsObject().Select(d => d.Key)
            .Where(d => d is not "record" and not "exportFile" and not "exportHeader").ToList();
        var missing = definitions.Where(d => !produced.Contains(d) && !NotProduced.ContainsKey(d)).Order(StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0, "Records no model produces (add them to a model, or to NotProduced with a reason): " + string.Join(", ", missing));
        var stale = NotProduced.Keys.Where(produced.Contains).ToList();
        Assert.True(stale.Count == 0, "Records NotProduced lists that a model now produces: " + string.Join(", ", stale));
    }

    private static async Task<ResolvedModel> ResolveAsync(string model) => Built.TryGetValue(model, out var build)
        ? build()
        : await ResolvedNullContractTests.ResolveAsync(Fixtures.Path("models", model));

    /// <summary>Two databases with migration facts: a rename and a type change of one column, and a raw fact.</summary>
    private static ModelSnapshot Migrations()
    {
        var model = new ModelBuilder(seed: 991);
        var main = model.Database("main", Maquettiste.Engine.Model.Dialect.PostgreSql);
        var audit = model.Database("audit", Maquettiste.Engine.Model.Dialect.PostgreSql);
        var notes = model.Table("notes", main).Column("id", "int64").Column("body", "text").PrimaryKey("id");
        var body = notes.ColumnId("body");
        var column = new MigrationObject { Kind = "column", Table = notes.Id, Key = body, Schema = "public", TableName = "notes", Name = "body" };
        var guard = ImmutableSortedDictionary<string, string>.Empty.Add("*", "select 1");
        var renamed = model.NewId();
        model.Add(new Migration
            {
                Id = model.NewId(), Database = main.Id, Fact = MigrationFact.ChangeType, After = [renamed], Object = column,
                From = new MigrationState { Type = "text", NativeType = "text" }, To = new MigrationState { Type = "string", NativeType = "varchar(20)" },
            })
            .Add(new Migration
            {
                Id = renamed, Database = main.Id, Fact = MigrationFact.Rename, Object = column with { Name = "text" },
                From = new MigrationState { Name = "text" }, To = new MigrationState { Name = "body" },
            })
            .Add(new Migration { Id = model.NewId(), Database = audit.Id, Fact = MigrationFact.Raw, Sql = guard, SkipWhen = guard });
        return model.Build();
    }

    // What a reader should never meet: a design file, a section, an erratum, a decision, a date, a resolver type name or a cref left over.
    [GeneratedRegex(@"\.md\b|\bsection \d|\berrat(?:um|a)\b|\bSPEC\b|\bdecisions? \d|\b\d{4}-\d{2}-\d{2}\b|\b(?:E|D|PD)\d{1,2}[a-z]?\b|`[Rr][A-Z][a-z]\w*[`.]|\b(?:W[1-9]|phase \d|requirement \d+)\b|\(see `")]
    private static partial Regex Internal();

    [GeneratedRegex(@"\bnull\b|\bleft out\b|\babsent\b|\bomitted\b", RegexOptions.IgnoreCase)]
    private static partial Regex NullWords();
}
