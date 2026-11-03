using System.Text;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Hashing;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Tests.Store;
using Maquettiste.Engine.Tests.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.CustomKinds;

/// <summary>
/// Custom kinds over the billing fixture (next-phase-2-design.md section 4.2): the invented kinds <c>setting</c> (scalar fields and a
/// row code), <c>message</c> (parts as members) and <c>flag</c>, and the setting <c>due_days</c> in package Billing.
/// </summary>
internal static class CustomFixture
{
    public const string Billing = "01J92P0V01KDRN8GX5PGYCNKSX";
    public const string Invoice = "01J92P0V0FJ23CGSNKM7P1W5V7";
    public const string DueDays = "01JR6CK000000000000000000A";

    public const string SettingKind = """
        {
          "type": "setting",
          "displayName": "Setting",
          "pluralName": "Settings",
          "properties": {
            "code": { "type": "string", "x-ref": { "kind": "row" } },
            "unit": { "type": "string" },
            "value": { "type": ["integer", "string", "boolean"] }
          }
        }
        """;

    public const string MessageKind = """
        {
          "type": "message",
          "properties": {
            "parts": {
              "type": "array",
              "x-member": true,
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "text": { "type": "string", "x-translatable": true }
                },
                "required": ["name"]
              }
            }
          }
        }
        """;

    /// <summary>Sections that hold lines, both members; a line's field names an attribute, and pick a member of the same element.</summary>
    public const string LayoutKind = """
        {
          "type": "layout",
          "properties": {
            "sections": {
              "type": "array",
              "x-member": true,
              "items": {
                "type": "object",
                "properties": {
                  "name": { "type": "string" },
                  "lines": {
                    "type": "array",
                    "x-member": true,
                    "items": {
                      "type": "object",
                      "properties": {
                        "name": { "type": "string" },
                        "field": { "type": "string", "x-ref": { "kind": "attribute", "optional": true } }
                      }
                    }
                  }
                }
              }
            },
            "pick": { "type": "string", "x-ref": { "kind": "member", "of": "$element" } }
          }
        }
        """;

    /// <summary>A reply names a part of any message (required) and another one (optional).</summary>
    public const string ReplyKind = """
        {
          "type": "reply",
          "properties": {
            "part": { "type": "string", "x-ref": { "kind": "member" } },
            "also": { "type": "string", "x-ref": { "kind": "member", "optional": true } }
          }
        }
        """;

    public const string FlagKind = """{ "type": "flag", "properties": { "on": { "type": "boolean" } } }""";

    /// <summary>The setting <c>due_days</c>, without <c>$schema</c>.</summary>
    public static string DueDaysJson(int value = 30) => $$"""
        { "kind": "custom", "id": "{{DueDays}}", "name": "due_days", "package": "{{Billing}}", "type": "setting",
          "content": { "unit": "days", "value": {{value}} } }
        """;

    /// <summary>
    /// Opens billing with the real validator after writing the given custom kind files under <c>extensions/kinds/</c> (type to JSON), and
    /// creates <c>due_days</c> when asked.
    /// </summary>
    public static async Task<BillingStore> OpenAsync(bool withDueDays = true, params (string Type, string Json)[] kinds)
    {
        var s = await BillingStore.OpenAsync(load: false, validate: true);
        foreach (var (type, json) in kinds.Length == 0 ? [("setting", SettingKind)] : kinds)
            s.Harness.Write("extensions/kinds/" + type + ".json", json);
        await s.Store.LoadAsync(TestContext.Current.CancellationToken);
        if (withDueDays)
            await CreateAsync(s, DueDaysJson());
        return s;
    }

    /// <summary>Creates an element and asserts it saved.</summary>
    public static async Task<SaveResult> CreateAsync(BillingStore s, string json)
    {
        var result = await s.Store.CreateAsync(Encoding.UTF8.GetBytes(json), ChangeSource.Cli, TestContext.Current.CancellationToken);
        Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", result.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return result;
    }

    /// <summary>Validates the whole current model with the engine's validator.</summary>
    public static async Task<IReadOnlyList<Diagnostic>> ValidateAsync(BillingStore s) =>
        (await EngineServices.Create(s.Harness.Options).Validator.ValidateAsync(s.Model, ValidationScope.All, null, TestContext.Current.CancellationToken)).Diagnostics;

    /// <summary>A custom kind file from its JSON, at <c>extensions/kinds/&lt;stem&gt;.json</c> (the type by default).</summary>
    public static CustomKindDocument KindDocument(string json, string? stem = null)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var kind = new CustomKind
        {
            Type = root.GetProperty("type").GetString()!,
            DisplayName = root.TryGetProperty("displayName", out var display) ? display.GetString() : null,
            PluralName = root.TryGetProperty("pluralName", out var plural) ? plural.GetString() : null,
            Properties = root.GetProperty("properties").Clone(),
            Required = root.TryGetProperty("required", out var required) ? [.. required.EnumerateArray().Select(r => r.GetString()!)] : [],
        };
        return new CustomKindDocument(kind, ".maquettiste/extensions/kinds/" + (stem ?? kind.Type) + ".json", ContentHash.Of(json));
    }

    /// <summary>A custom element record.</summary>
    public static global::Maquettiste.Engine.Model.Custom Element(string id, string name, string type, string content = "{}", string? package = null)
    {
        using var document = JsonDocument.Parse(content);
        return new global::Maquettiste.Engine.Model.Custom
        {
            Id = id, Name = name, Type = type, Package = package,
            Content = document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal),
        };
    }

    /// <summary>A snapshot of the builder's elements with custom kind files and extension files.</summary>
    public static ModelSnapshot Snapshot(ModelBuilder b, IReadOnlyList<CustomKindDocument> kinds, params ExtensionDocument[] extensions) =>
        ModelSnapshot.Create(b.BuildDocuments(), new ProjectSettings { FormatVersion = EngineVersion.FormatVersion }, "", extensions, [], 1, customKinds: kinds);

    /// <summary>Validates a whole snapshot; returns the findings of the given rules (all when none is given).</summary>
    public static async Task<IReadOnlyList<Diagnostic>> FindingsAsync(ModelSnapshot model, params string[] rules)
    {
        var report = await ValidationFixture.Validator().ValidateAsync(model, ValidationScope.All, null, TestContext.Current.CancellationToken);
        return [.. report.Diagnostics.Where(d => rules.Length == 0 || rules.Contains(d.Rule))];
    }

    /// <summary>Applies a batch (<c>{ "operations": [...] }</c>).</summary>
    public static async Task<BatchResult> ApplyAsync(BillingStore s, string json)
    {
        var parsed = s.Store.ParseBatch(Encoding.UTF8.GetBytes(json));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return await s.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Cli, TestContext.Current.CancellationToken);
    }

    /// <summary>The diagnostics of a batch, one per line.</summary>
    public static string Explain(BatchResult result) =>
        string.Join("\n", result.Items.SelectMany(i => i.Diagnostics).Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}"));

    /// <summary>Loads the folder afresh with a new loader.</summary>
    public static async Task<ModelSnapshot> ReloadAsync(BillingStore s) =>
        (await s.Harness.NewLoader().LoadAsync(new LoadRequest(null, null, false), null, TestContext.Current.CancellationToken)).Snapshot;
}
