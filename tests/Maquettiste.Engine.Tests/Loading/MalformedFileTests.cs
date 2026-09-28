using System.Text;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Tests.Store;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Loading;

/// <summary>Files that parse as JSON under lenient options but are not well-formed model files: each one is left out, the rest loads.</summary>
public sealed class MalformedFileTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] WithDuplicateKey(string canonical, string key)
    {
        // Repeats the first "key": value line right after itself.
        var lines = canonical.Split('\n').ToList();
        var at = lines.FindIndex(l => l.TrimStart().StartsWith("\"" + key + "\":", StringComparison.Ordinal));
        Assert.True(at > 0);
        var line = lines[at];
        if (!line.EndsWith(','))
            lines[at] = line + ",";
        lines.Insert(at + 1, line);
        return Encoding.UTF8.GetBytes(string.Join('\n', lines));
    }

    [Fact]
    public async Task A_duplicated_property_name_is_MQ1001_with_a_position_and_the_rest_of_the_model_loads()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        var path = s.Harness.Model("model/entities/customer.json");
        File.WriteAllBytes(path, WithDuplicateKey(File.ReadAllText(path), "name"));

        await s.Store.LoadAsync(Ct);
        var model = s.Store.Current!;

        Assert.Equal(25, model.Documents.Count);
        Assert.DoesNotContain(model.Documents, d => d.Path == ".maquettiste/model/entities/customer.json");
        var d = Assert.Single(model.LoadDiagnostics, d => d.FilePath == ".maquettiste/model/entities/customer.json");
        Assert.Equal("MQ1001", d.Rule);
        Assert.NotNull(d.Line);
        Assert.NotNull(d.Column);
        Assert.Equal(25, (await s.Store.GetIndexAsync(Ct)).Count);
    }

    [Fact]
    public async Task Invalid_utf8_is_MQ1001_at_the_bad_byte_and_the_rest_of_the_model_loads()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        var path = s.Harness.Model("model/entities/customer.json");
        var bytes = File.ReadAllBytes(path);
        var at = bytes.AsSpan().IndexOf("\"Customer\""u8) + 1;
        bytes[at] = 0xFF;
        File.WriteAllBytes(path, bytes);

        await s.Store.LoadAsync(Ct);
        var model = s.Store.Current!;

        Assert.Equal(25, model.Documents.Count);
        var d = Assert.Single(model.LoadDiagnostics, d => d.FilePath == ".maquettiste/model/entities/customer.json");
        Assert.Equal("MQ1001", d.Rule);
        var text = Encoding.UTF8.GetString(bytes.AsSpan(0, at));
        Assert.Equal(text.Count(c => c == '\n') + 1, d.Line);
        Assert.Equal(at - text.LastIndexOf('\n'), d.Column);
    }

    [Fact]
    public async Task Malformed_settings_and_extensions_are_reported_and_left_out()
    {
        await using var s = await BillingStore.OpenAsync(load: false);
        var settings = s.Harness.Model("maquettiste.json");
        File.WriteAllBytes(settings, WithDuplicateKey(File.ReadAllText(settings), "name"));
        var extension = s.Harness.Model("extensions/retention.json");
        var bytes = File.ReadAllBytes(extension);
        bytes[bytes.AsSpan().IndexOf("\"retention\""u8) + 1] = 0xC3; // a lead byte with no continuation
        File.WriteAllBytes(extension, bytes);

        await s.Store.LoadAsync(Ct);
        var model = s.Store.Current!;

        Assert.Equal(26, model.Documents.Count);
        Assert.Null(model.Settings.Name);
        Assert.Contains(model.LoadDiagnostics, d => d.Rule == "MQ1001" && d.FilePath == ".maquettiste/maquettiste.json");
        Assert.Contains(model.LoadDiagnostics, d => d.Rule == "MQ5004" && d.FilePath == ".maquettiste/extensions/retention.json");
        Assert.Empty(model.Extensions);
    }

    [Fact]
    public void The_reader_refuses_duplicates_and_bad_utf8_and_the_canonical_check_does_not_throw()
    {
        var reader = new DocumentReader(TestServices.Schemas, TestServices.Json);

        Assert.False(reader.TryParse("{\"a\":1,\"a\":2}"u8.ToArray(), "x.json", "MQ1001", out _, out var duplicate));
        Assert.Equal("MQ1001", duplicate.Rule);
        Assert.Equal(1, duplicate.Line);

        byte[] bad = [.. "﻿{\n  \"a\": \""u8, 0xFF, .. "\"\n}"u8];
        Assert.False(reader.TryParse(bad, "x.json", "MQ1001", out _, out var utf8));
        Assert.Equal((2, 9), (utf8.Line, utf8.Column));

        Assert.False(TestServices.Json.IsCanonical("{\n  \"a\": 1,\n  \"a\": 1\n}\n"u8, "entity.json", "model/entities/x.json"));
        Assert.False(TestServices.Json.IsCanonical(bad, "entity.json", "model/entities/x.json"));
    }
}
