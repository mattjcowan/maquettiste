using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;

namespace Maquettiste.Engine.Tests.Model;

public sealed class CatalogTests
{
    [Fact]
    public void Kind_info_covers_every_element_kind_in_order()
    {
        Assert.Equal(Enum.GetValues<ElementKind>(), KindInfo.All.Select(k => k.Kind));
        foreach (var info in KindInfo.All)
        {
            var element = (Element)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(info.ClrType);
            Assert.Equal(info.Kind, element.Kind);
            Assert.True(KindInfo.TryGet(info.Name, out var byName));
            Assert.Same(info, byName);
            Assert.Equal(info.Name, JsonSerializer.Serialize(info.Kind).Trim('"'));
        }

        Assert.False(KindInfo.TryGet("operation", out _));
    }

    [Fact]
    public void Builtin_types_are_the_spec_keywords()
    {
        Assert.Equal(18, BuiltinTypes.All.Count);
        Assert.True(BuiltinTypes.IsBuiltin("datetimeoffset"));
        Assert.False(BuiltinTypes.IsBuiltin("decimal(18,2)"));
        Assert.False(BuiltinTypes.IsBuiltin("UUID"));
        Assert.False(BuiltinTypes.IsBuiltin(null));
    }

    [Fact]
    public void Rule_catalog_ids_are_unique_sorted_and_well_formed()
    {
        var ids = RuleCatalog.All.Select(r => r.Id).ToList();

        Assert.Equal(ids.Distinct(StringComparer.Ordinal), ids);
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
        Assert.All(ids, id => Assert.Matches("^MQ[1-9][0-9]{3}$", id));
        Assert.All(RuleCatalog.All.Where(r => r.Id.StartsWith("MQ1", StringComparison.Ordinal)), r => Assert.False(r.CanBeDisabled));
        Assert.Equal(DiagnosticSeverity.Warning, RuleCatalog.Get("MQ1003").DefaultSeverity);
        Assert.Equal(DiagnosticSeverity.Info, RuleCatalog.Get("MQ2006").DefaultSeverity);
        Assert.Equal(196, RuleCatalog.All.Count);
    }

    [Fact]
    public void Validation_report_sorts_counts_and_groups()
    {
        var report = ValidationReport.From(
        [
            new Diagnostic("MQ2001", DiagnosticSeverity.Error, "b", "E2", "b.json", "/x", 3, 1),
            new Diagnostic("MQ1003", DiagnosticSeverity.Warning, "a", null, "a.json", null, null, null),
            new Diagnostic("MQ2006", DiagnosticSeverity.Info, "c", "E2", "b.json", "/y", 2, 5),
        ]);

        Assert.Equal(["a.json", "b.json", "b.json"], report.Diagnostics.Select(d => d.FilePath));
        Assert.Equal([2, 3], report.Diagnostics.Skip(1).Select(d => d.Line!.Value));
        Assert.Equal((1, 1, 1), (report.Errors, report.Warnings, report.Infos));
        Assert.True(report.HasErrors);
        Assert.Equal(["a.json", "E2"], report.ByElement().Keys);
        Assert.Equal(2, report.ByElement()["E2"].Count);
    }

    [Fact]
    public void Enums_serialize_as_kebab_case_strings_with_web_defaults()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Equal("\"uuid-v7\"", JsonSerializer.Serialize(IdentityStrategy.UuidV7, options));
        Assert.Equal("\"n-ary\"", JsonSerializer.Serialize(RelationKind.NAry, options));
        Assert.Equal("\"postgresql\"", JsonSerializer.Serialize(Dialect.PostgreSql, options));
        Assert.Equal("\"set-null\"", JsonSerializer.Serialize(ReferentialIntent.SetNull, options));
        Assert.Equal("\"upper-snake\"", JsonSerializer.Serialize(CaseStyle.UpperSnake, options));
        Assert.Equal("1", JsonSerializer.Serialize(MaxCardinality.One, options));
        Assert.Equal("\"*\"", JsonSerializer.Serialize(MaxCardinality.Many, options));
        Assert.Equal("\"not-found\"", JsonSerializer.Serialize(SaveOutcome.NotFound, options));
    }

    [Fact]
    public void Change_set_truncates_to_a_json_size()
    {
        var changed = Enumerable.Range(0, 200).Select(i => new ElementChange($"id{i:000}", "entity", $"model/entities/e{i}.json", new string('a', 64))).ToList();
        var set = new ChangeSet(changed, ["gone"], ChangeSource.Disk, false);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Same(set, set.TruncateTo(1_000_000));
        var cut = set.TruncateTo(4096);

        Assert.True(cut.Truncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(cut, options).Length <= 4096);
        Assert.True(cut.Changed.Count is > 0 and < 200);
        Assert.Equal(changed.Take(cut.Changed.Count), cut.Changed);
        Assert.False(ChangeSet.Empty(ChangeSource.Cli).Truncated);
        Assert.True(ChangeSet.Empty(ChangeSource.Cli).IsEmpty);
    }
    [Fact]
    public void Validation_report_truncates_to_a_json_size_and_keeps_the_counts()
    {
        var diagnostics = Enumerable.Range(0, 300)
            .Select(i => new Diagnostic("MQ2001", i % 3 == 0 ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error, $"Dangling reference number {i}.", $"id{i:000}", $"model/entities/e{i:000}.json", "/x", 1, 1));
        var report = ValidationReport.From(diagnostics);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);

        Assert.Same(report, report.TruncateTo(10_000_000));
        Assert.False(report.Truncated);
        var cut = report.TruncateTo(4096);

        Assert.True(cut.Truncated);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(cut, options).Length <= 4096);
        Assert.True(cut.Diagnostics.Count is > 0 and < 300);
        Assert.Equal(report.Diagnostics.Take(cut.Diagnostics.Count), cut.Diagnostics);
        Assert.Equal((report.Errors, report.Warnings, report.Infos), (cut.Errors, cut.Warnings, cut.Infos));
        Assert.Equal(200, cut.Errors);
    }
}
