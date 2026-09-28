using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Api;

/// <summary>Public results serialize with plain <see cref="JsonSerializerDefaults.Web"/> options (host-contracts requirement 5).</summary>
public sealed class ResultSerializationTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Save_result_carries_the_whole_current_element()
    {
        var b = new ModelBuilder(seed: 9);
        var invoice = b.Entity("Invoice").Key("id", "uuid").Attr("number", "string", a => a.Length(32));
        var document = b.BuildDocuments().Single(d => d.Element.Id == invoice.Id);
        var result = new SaveResult(SaveOutcome.Conflict, invoice.Id, document.Hash, document, [], [], null);

        var node = JsonNode.Parse(JsonSerializer.Serialize(result, Web))!;

        var element = node["current"]!["element"]!;
        Assert.Equal("entity", element["kind"]!.GetValue<string>());
        Assert.Equal(2, element["attributes"]!.AsArray().Count);
        Assert.Equal(32, element["attributes"]![1]!["length"]!.GetValue<int>());
        Assert.Equal("uuid", element["attributes"]![0]!["type"]!.GetValue<string>());
        Assert.NotNull(element["key"]);
    }

    [Fact]
    public void Job_info_round_trips()
    {
        var plan = new GenerationPlan(
            "01JAX3K9V2Q7M4T8W1Z5C6B0DE",
            new GenerationRequest { Mode = GenerationMode.DryRun, Packs = ["sql-ddl"], HandEdits = HandEditPolicy.Skip },
            7,
            ["sql-ddl"],
            [new PlanUnit("sql-ddl/table:01JAX3KA1B2C3D4E5F6G7H8J9K", new string('a', 64), ["e:01JAX3KA1B2C3D4E5F6G7H8J9K"], false,
                [new PlanFile("db/t.sql", new string('b', 64), new string('b', 64), OutputMode.Overwrite, FileRole.Main, new OutputRootInfo("db", true), null)])],
            [new FileChange("db/t.sql", FileChangeKind.Added, "sql-ddl", "sql-ddl/table:01JAX3KA1B2C3D4E5F6G7H8J9K", null, new string('b', 64), null)],
            [RuleCatalog.Create("MQ6011", "Text outside file blocks.")]);
        var queued = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);
        var job = new JobInfo("01JAX3KB2C3D4E5F6G7H8J9KAM", JobKind.Plan, JobState.Succeeded, null,
            new ProgressUpdate(PipelineStage.Write, 1, 1, "db/t.sql", "sql-ddl"), new PlanResult(RunOutcome.Succeeded, plan), null, null,
            queued, queued.AddSeconds(1), queued.AddSeconds(2));

        var json = JsonSerializer.Serialize(job, Web);
        var back = JsonSerializer.Deserialize<JobInfo>(json, Web)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, Web));
        Assert.Equal(queued.AddSeconds(2), back.FinishedUtc);
        Assert.Equal(HandEditPolicy.Skip, back.PlanResult!.Plan!.Request.HandEdits);
        Assert.Contains("\"state\":\"succeeded\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("de-DE")]
    [InlineData("ar-SA")]
    public void Canonical_bytes_and_hashes_do_not_depend_on_the_current_culture(string culture)
    {
        // The engine runs inside hosts with any culture; the culture analyzers (CA1304…CA1311) are errors in src/, and this pins
        // the parts that already exist. Rendering determinism under these cultures is covered once W5 and W6 land (section 17).
        static (string Text, string Hash, string Kinds) Produce()
        {
            var b = new ModelBuilder(seed: 11);
            var money = b.ValueObject("Money").Attr("amount", "decimal", a => a.Precision(18).Scale(2).Default(1234.5)).Attr("rate", "double", a => a.Default(0.125));
            b.Entity("TITLE").Key("id", "int64", IdentityStrategy.DatabaseIdentity).Attr("total", money).Attr("İstanbul", "string", a => a.Default("ISTANBUL"));
            var documents = b.BuildDocuments();
            var text = string.Concat(documents.Select(d => d.Path + Encoding.UTF8.GetString(TestServices.Json.Serialize(d.Element, KindInfo.Get(d.Element.Kind).SchemaFile, d.Path))));
            var model = b.Build();
            return (text, Maquettiste.Engine.Hashing.HashBuilder.Of(text, 1234567L.ToString(CultureInfo.InvariantCulture)), model.KindSetHash(ElementKind.Entity));
        }

        var invariant = WithCulture(CultureInfo.InvariantCulture, Produce);
        var local = WithCulture(CultureInfo.GetCultureInfo(culture), Produce);

        Assert.Equal(invariant, local);
    }

    private static T WithCulture<T>(CultureInfo culture, Func<T> action)
    {
        var (current, ui) = (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture);
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
            return action();
        }
        finally
        {
            (CultureInfo.CurrentCulture, CultureInfo.CurrentUICulture) = (current, ui);
        }
    }
}
