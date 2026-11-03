using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Tests.Integration;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>Billing copies whose files the suppression tests edit on disk before the store loads them (section 5.6).</summary>
internal static class SuppressionHarness
{
    public const string NotesPath = "model/databases/main/tables/notes.json";
    public const string NotesFile = ".maquettiste/" + NotesPath;
    public const string NotesId = "01K6BND0000000000000000001";
    public const string BodyColumnId = "01K6BND0000000000000000005";
    public const string InvoicePath = "model/entities/invoice.json";
    public const string InvoiceStatusAttributeId = "01J92P0V0VB1Z49SWERMAGR4TV";
    public const string InvoiceStatusPath = "model/enums/invoice-status.json";
    public const string AuditedPath = "model/vocabularies/stereotypes/audited.json";
    public const string AuditedUpdatedAtId = "01J92P0V27P8NW8KPEDG0RBZ4W";
    public const string RegisterPath = "model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json";
    public const string RegisterId = "01J92P0V1T0J6RH4MY9H81NYB4";

    /// <summary>The design's declaration (section 5.6): audited tables need an updated_at column.</summary>
    public const string AuditRule = """
        { "id": "audited-tables-have-updated-at", "severity": "error", "description": "Audited tables record when a row last changed.",
          "select": { "kind": "table", "stereotype": "audited" },
          "require": { "count": { "of": "columns", "where": { "equals": { "path": "name", "value": "updated_at" } }, "min": 1 } },
          "message": "Table {name} carries audited but has no updated_at column." }
        """;

    /// <summary>A declaration every table fails.</summary>
    public const string EveryTableRule = """
        { "id": "every-table", "select": { "kind": "table" }, "require": { "has": "properties.nothing" }, "message": "Table {name} fails." }
        """;

    /// <summary>A declaration every column fails.</summary>
    public const string EveryColumnRule = """
        { "id": "every-column", "select": { "kind": "column" }, "require": { "has": "properties.nothing" }, "message": "Column {name} fails." }
        """;

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A billing copy (no demo pack) with rule declarations, after the edits, its store restarted on them.</summary>
    public static async Task<E2ERepo> CreateAsync(IReadOnlyList<string> rules, Action<JsonObject>? settings = null, params (string Path, Action<JsonObject> Edit)[] edits)
    {
        var repo = E2ERepo.Create(settings: settings, demo: false);
        if (rules.Count > 0)
            Write(repo, "extensions/rules/rules.json", "{\n  \"rules\": [\n" + string.Join(",\n", rules) + "\n  ]\n}\n");
        foreach (var (path, edit) in edits)
            Edit(repo, path, edit);
        await repo.RestartAsync();
        return repo;
    }

    /// <summary>Edits a model file on disk (the caller restarts the store).</summary>
    public static void Edit(E2ERepo repo, string path, Action<JsonObject> edit)
    {
        var full = Path.Combine(repo.Repo.ModelRoot, path);
        var node = JsonNode.Parse(File.ReadAllText(full))!.AsObject();
        edit(node);
        File.WriteAllText(full, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    public static void Write(E2ERepo repo, string path, string text)
    {
        var full = Path.Combine(repo.Repo.ModelRoot, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    /// <summary>Sets an element's suppressions.</summary>
    public static Action<JsonObject> Suppress(params (string Rule, string? Member)[] entries) => node =>
    {
        var list = new JsonArray();
        foreach (var (rule, member) in entries)
        {
            var entry = new JsonObject { ["rule"] = rule };
            if (member is not null)
                entry["member"] = member;
            entry["reason"] = "Accepted: " + rule + ".";
            list.Add(entry);
        }

        node["suppressions"] = list;
    };

    /// <summary>Adds a stereotype key to an element.</summary>
    public static Action<JsonObject> Carry(string key) => node =>
    {
        var list = node["stereotypes"] as JsonArray ?? [];
        list.Add(key);
        node["stereotypes"] = list;
    };

    public static Action<JsonObject> Then(this Action<JsonObject> first, Action<JsonObject> second) => node =>
    {
        first(node);
        second(node);
    };

    public static Task<ValidationReport> ValidateAsync(E2ERepo repo) => repo.Store.ValidateAsync(ValidationScope.All, Ct);

    public static string Show(ValidationReport report) =>
        string.Join('\n', report.Diagnostics.Select(d => $"{d.Severity} {d.Rule} {d.FilePath} {d.JsonPointer} {d.ElementId} {d.Message}"));
}
