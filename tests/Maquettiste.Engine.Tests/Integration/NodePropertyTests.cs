using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Loading;
using Maquettiste.Engine.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Custom properties and stereotypes on a process's guards, actions, invokes and gates, and custom properties on checks and constraints,
/// on a copy of the billing fixture (<see cref="E2ERepo"/>): an invented process with one of each node, an invented constraint, an entity
/// check and the fixture's table check, with an extension that types them and links them to an invented kind <c>policy</c>.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class NodePropertyTests
{
    private const string Policy = "01JRNP00000000000000000001";
    private const string ProcessId = "01JRNP00000000000000000002";
    private const string Actor = "01JRNP00000000000000000003";
    private const string Open = "01JRNP00000000000000000004";
    private const string Done = "01JRNP00000000000000000005";
    private const string Approve = "01JRNP00000000000000000006";
    private const string Approved = "01JRNP00000000000000000007";
    private const string Guard = "01JRNP00000000000000000008";
    private const string Action = "01JRNP00000000000000000009";
    private const string Invoke = "01JRNP0000000000000000000A";
    private const string Gate = "01JRNP0000000000000000000B";
    private const string Meaning = "01JRNP0000000000000000000C";
    private const string Constraint = "01JRNP0000000000000000000D";
    private const string Parameter = "01JRNP0000000000000000000E";
    private const string EntityCheck = "01JRNP0000000000000000000F";
    private const string Marked = "01JRNP0000000000000000000G";
    private const string Elsewhere = "01JRNP0000000000000000000H";
    private const string OperationOutcome = "01JRNP0000000000000000000J";
    private const string InvoiceTable = "01J92P0V1T0J6RH4MY9H81NYB4";
    private const string TableCheck = "01J92P0V1W3BP53NMDVMSQSRQV";
    private const string InvoiceNumber = "01J92P0V0R3VSP7D5238DTNZX1";
    private const string ProcessPath = ".maquettiste/model/processes/approval.json";
    private const string InvoicePath = ".maquettiste/model/entities/invoice.json";
    private const string TablePath = ".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json";

    private const string TypedProperties = $$"""{ "owner": "finance", "meets": "{{Policy}}" }""";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Process(string guard = "", string action = "", string invoke = "", string gate = "") => $$"""
        {
          "kind": "process", "id": "{{ProcessId}}", "name": "Approval", "use": "orchestration",
          "states": [
            { "id": "{{Open}}", "name": "Open", "invoke": [ { "id": "{{Invoke}}", "name": "review", "type": "service"{{invoke}} } ] },
            { "id": "{{Done}}", "name": "Done", "type": "final" }
          ],
          "initial": "{{Open}}",
          "events": [ { "id": "{{Approve}}", "name": "approve", "actors": ["{{Actor}}"] } ],
          "guards": [ { "id": "{{Guard}}", "name": "is_ready"{{guard}} } ],
          "actions": [ { "id": "{{Action}}", "name": "notify"{{action}} } ],
          "transitions": [
            { "id": "{{Approved}}", "source": "{{Open}}", "trigger": "event", "event": "{{Approve}}", "guard": "{{Guard}}", "actions": ["{{Action}}"], "targets": ["{{Done}}"],
              "gate": { "id": "{{Gate}}", "name": "sign_off", "signers": ["{{Actor}}"], "meanings": [ { "id": "{{Meaning}}", "name": "approved" } ]{{gate}} } }
          ]
        }
        """;

    private static string Props(string values = TypedProperties) => $""", "properties": {values}""";

    /// <summary>
    /// A copy with the invented kind, the extension, the process, the constraint and, when given, a stereotype, checks that hold the
    /// values, and a pack with one page per policy, per process and per table. All of it is written to disk, as a hand edit is, so one that
    /// does not validate still loads.
    /// </summary>
    private static async Task<E2ERepo> CopyAsync(string process, string? template = null, string constraintProperties = TypedProperties,
        string appliesTo = """["guard", "action", "invoke", "gate", "check", "constraint"]""", string? stereotype = null, string? checkValues = null, string entityCheckMarks = "", string? operation = null)
    {
        var repo = E2ERepo.Create(demo: false);
        repo.Repo.WriteFile(".maquettiste/extensions/kinds/policy.json", """{ "type": "policy", "properties": { "text": { "type": "string" } } }""");
        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", $$"""
            { "name": "traces", "appliesTo": { "kinds": {{appliesTo}} },
              "properties": { "owner": { "type": "string" }, "meets": { "type": "string", "x-ref": { "kind": "custom:policy", "optional": true } } } }
            """);
        if (template is not null)
        {
            var pack = Path.Combine(repo.Repo.ModelRoot, "templates", "links");
            Directory.CreateDirectory(pack);
            await File.WriteAllTextAsync(Path.Combine(pack, "pack.json"), """
                {"name": "links", "version": "1.0.0", "engine": ">=1.0", "units": [
                  {"id": "page", "template": "page.scriban", "for": "each custom:policy", "output": "src/Generated/links/{{ custom.name }}.txt"},
                  {"id": "process", "template": "process.scriban", "for": "each process", "output": "src/Generated/links/process-{{ process.name }}.txt"},
                  {"id": "table", "template": "table.scriban", "for": "each table", "output": "src/Generated/links/table-{{ table.name }}.txt"}]}
                """, Ct);
            await File.WriteAllTextAsync(Path.Combine(pack, "page.scriban"), template, Ct);
            await File.WriteAllTextAsync(Path.Combine(pack, "process.scriban"), """
                {{ for g in process.guards }}guard {{ g.properties.owner }} {{ g.stereotypes.size }}
                {{ end }}{{ for a in process.actions }}action {{ a.properties.owner }}
                {{ end }}{{ for i in process.invokes }}invoke {{ i.properties.owner }}
                {{ end }}{{ for g in process.gates }}gate {{ g.properties.owner }}
                {{ end }}
                """, Ct);
            await File.WriteAllTextAsync(Path.Combine(pack, "table.scriban"), "{{ for c in table.checks }}{{ c.name }}={{ c.properties.owner }}\n{{ end }}", Ct);
        }

        repo.Repo.WriteFile(".maquettiste/model/custom/policy/keeps-lines.json",
            $$"""{ "kind": "custom", "id": "{{Policy}}", "name": "keeps_lines", "type": "policy", "content": { "text": "An issued invoice keeps its lines." } }""");
        repo.Repo.WriteFile(".maquettiste/model/actors/controller.json", $$"""{ "kind": "actor", "id": "{{Actor}}", "name": "Controller", "type": "person" }""");
        repo.Repo.WriteFile(".maquettiste/model/processes/approval.json", process);
        repo.Repo.WriteFile(".maquettiste/model/constraints/positive-amount.json", $$"""
            { "kind": "constraint", "id": "{{Constraint}}", "name": "PositiveAmount",
              "parameters": [ { "id": "{{Parameter}}", "name": "amount", "type": "decimal" } ],
              "condition": { "op": "gt", "args": [{ "path": ["{{Parameter}}"] }, { "value": 0 }] },
              "properties": {{constraintProperties}} }
            """);
        if (stereotype is not null)
            repo.Repo.WriteFile(".maquettiste/model/vocabularies/stereotypes/marked.json", stereotype);
        if (operation is not null)
            repo.Repo.WriteFile(".maquettiste/model/operations/settle.json", operation);
        if (checkValues is not null || entityCheckMarks.Length > 0)
        {
            var values = checkValues ?? "{}";
            EditOnDisk(repo, InvoicePath, invoice => invoice["checks"] = new JsonArray(JsonNode.Parse($$"""
                { "id": "{{EntityCheck}}", "name": "hasNumber"{{entityCheckMarks}}, "condition": { "op": "ne", "args": [{ "path": ["{{InvoiceNumber}}"] }, { "value": "" }] }, "properties": {{values}} }
                """)));
            EditOnDisk(repo, TablePath, table => table["checks"]![0]!["properties"] = JsonNode.Parse(values));
        }

        await repo.RestartAsync();
        return repo;
    }

    private static void EditOnDisk(E2ERepo repo, string path, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(repo.Repo.ReadFile(path))!.AsObject();
        edit(node);
        repo.Repo.WriteFile(path, node.ToJsonString());
    }

    private static async Task<IReadOnlyList<Diagnostic>> FindingsAsync(E2ERepo repo) =>
        (await repo.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics;

    [Fact]
    public async Task Each_new_holder_can_link_and_a_page_lists_it_by_kind()
    {
        await using var repo = await CopyAsync(Process(Props(), Props(), Props(), Props()), "{{ for l in (referenced_by custom) }}{{ l.kind }} {{ l.id }} {{ l.field }}\n{{ end }}", checkValues: TypedProperties);

        await repo.ApplyAsync();

        // A check is a key in the index, as its table's other constraints are.
        string[] rows =
        [
            $"action {Action} meets",
            $"constraint {Constraint} meets",
            $"gate {Gate} meets",
            $"guard {Guard} meets",
            $"invoke {Invoke} meets",
            $"key {TableCheck} meets",
            $"key {EntityCheck} meets",
        ];
        Assert.Equal(rows, repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt").TrimEnd('\n').Split('\n').Order(StringComparer.Ordinal));
        Assert.DoesNotContain(await FindingsAsync(repo), d => d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task Templates_read_the_properties_of_each_node_and_check()
    {
        await using var repo = await CopyAsync(Process(Props(), Props(), Props(), Props()), "page", checkValues: TypedProperties);

        await repo.ApplyAsync();

        Assert.Equal("guard finance 0\naction finance\ninvoke finance\ngate finance\n", repo.Repo.ReadFile("src/Generated/links/process-Approval.txt"));
        Assert.Contains("ck_invoice_status=finance\n", repo.Repo.ReadFile("src/Generated/links/table-invoices.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_that_does_not_fit_is_MQ5001_on_each_holder()
    {
        var wrong = Props("""{ "owner": 3 }""");
        await using var repo = await CopyAsync(Process(wrong, wrong, wrong, wrong), constraintProperties: """{ "owner": 3 }""", checkValues: """{ "owner": 3 }""");

        var found = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ5001").Select(d => (d.ElementId, d.JsonPointer)).ToHashSet();

        (string, string)[] expected =
        [
            (Guard, "/guards/0/properties/owner"), (Action, "/actions/0/properties/owner"), (Invoke, "/states/0/invoke/0/properties/owner"),
            (Gate, "/transitions/0/gate/properties/owner"), (Constraint, "/properties/owner"), (EntityCheck, "/checks/0/properties/owner"),
            (TableCheck, "/checks/0/properties/owner"),
        ];
        Assert.All(expected, e => Assert.Contains(((string?)e.Item1, (string?)e.Item2), found));
    }

    [Fact]
    public async Task An_extension_for_guards_leaves_the_other_holders_alone()
    {
        var process = Process(Props("""{ "owner": "finance" }"""), Props("""{ "owner": 3 }"""));
        await using var repo = await CopyAsync(process, constraintProperties: """{ "owner": 3 }""", appliesTo: """["guard"]""");

        Assert.DoesNotContain(await FindingsAsync(repo), d => d.Rule == "MQ5001");
    }

    [Fact]
    public async Task Stereotypes_on_the_four_nodes_are_judged_and_give_their_defaults()
    {
        var marks = """, "stereotypes": ["sensitive"]""";
        await using var repo = await CopyAsync(Process(marks, marks, marks, marks), "page", stereotype: $$"""
            { "kind": "stereotype", "id": "{{Marked}}", "key": "sensitive", "name": "Sensitive", "appliesTo": ["guard", "action", "invoke", "gate"],
              "parameters": { "properties": { "owner": { "type": "string" } } }, "defaultProperties": { "owner": "security" } }
            """);

        Assert.DoesNotContain(await FindingsAsync(repo), d => d.Rule is "MQ2003" or "MQ2004");
        await repo.ApplyAsync();
        Assert.Equal("guard security 1\naction security\ninvoke security\ngate security\n", repo.Repo.ReadFile("src/Generated/links/process-Approval.txt"));

        // A stereotype for the gate alone misfits the other three, each at its own place, and an unknown key is MQ2003 wherever it sits.
        EditOnDisk(repo, ".maquettiste/model/vocabularies/stereotypes/marked.json", stereotype => stereotype["appliesTo"] = new JsonArray("gate"));
        EditOnDisk(repo, ".maquettiste/model/processes/approval.json", process => process["guards"]![0]!["stereotypes"] = new JsonArray("sensitive", "unknown-key"));
        await repo.RestartAsync();
        var found = await FindingsAsync(repo);
        Assert.Equal(["/actions/0/stereotypes/0", "/guards/0/stereotypes/0", "/states/0/invoke/0/stereotypes/0"],
            found.Where(d => d.Rule == "MQ2004").Select(d => d.JsonPointer!).Order(StringComparer.Ordinal));
        Assert.Equal("/guards/0/stereotypes/1", Assert.Single(found, d => d.Rule == "MQ2003").JsonPointer);
    }

    [Fact]
    public async Task A_stereotype_that_applies_to_checks_types_an_entity_check_and_one_for_other_kinds_stays_unjudged_as_before()
    {
        await using var repo = await CopyAsync(Process(), entityCheckMarks: """, "stereotypes": ["for-checks"]""", checkValues: """{ "level": 3 }""",
            stereotype: $$"""
                { "kind": "stereotype", "id": "{{Marked}}", "key": "for-checks", "name": "For checks", "appliesTo": ["check"],
                  "parameters": { "properties": { "level": { "type": "string" } } } }
                """);
        Assert.Equal("/checks/0/properties/level", Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ2018").JsonPointer);

        EditOnDisk(repo, ".maquettiste/model/vocabularies/stereotypes/marked.json", stereotype => stereotype["appliesTo"] = new JsonArray("entity"));
        await repo.RestartAsync();

        Assert.DoesNotContain(await FindingsAsync(repo), d => d.Rule is "MQ2003" or "MQ2004" or "MQ2018");
    }

    [Fact]
    public async Task An_extension_with_no_kinds_and_a_required_property_does_not_start_to_bind_the_new_holders()
    {
        // Such a file reaches every kind, and it did not reach these before they took properties; a project that uses none of this sees no new finding.
        var repo = E2ERepo.Create(demo: false);
        await using var _ = repo;
        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", """{ "name": "traces", "appliesTo": { "kinds": ["table"] }, "properties": { "owner": { "type": "string" } }, "required": ["owner"] }""");
        repo.Repo.WriteFile(".maquettiste/model/actors/controller.json", $$"""{ "kind": "actor", "id": "{{Actor}}", "name": "Controller", "type": "person" }""");
        repo.Repo.WriteFile(".maquettiste/model/processes/approval.json", Process());
        EditOnDisk(repo, InvoicePath, invoice => invoice["checks"] = new JsonArray(JsonNode.Parse($$"""
            { "id": "{{EntityCheck}}", "name": "hasNumber", "condition": { "op": "ne", "args": [{ "path": ["{{InvoiceNumber}}"] }, { "value": "" }] } }
            """)));
        await repo.RestartAsync();
        var named = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ5001").Select(d => d.ElementId).ToHashSet();

        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", """{ "name": "traces", "appliesTo": {}, "properties": { "owner": { "type": "string" } }, "required": ["owner"] }""");
        await repo.RestartAsync();
        var every = (await FindingsAsync(repo)).Where(d => d.Rule == "MQ5001").Select(d => d.ElementId).ToHashSet();

        Assert.Contains(InvoiceTable, named);
        Assert.Contains(ProcessId, every);
        Assert.Contains(Open, every);
        Assert.DoesNotContain(Guard, every);
        Assert.DoesNotContain(Action, every);
        Assert.DoesNotContain(Invoke, every);
        Assert.DoesNotContain(Gate, every);
        Assert.DoesNotContain(EntityCheck, every);
        Assert.DoesNotContain(TableCheck, every);
    }

    [Fact]
    public async Task An_extension_with_no_kinds_reaches_a_guard_that_carries_its_stereotype()
    {
        await using var repo = await CopyAsync(Process(""", "stereotypes": ["sensitive"]"""), stereotype: $$"""
            { "kind": "stereotype", "id": "{{Marked}}", "key": "sensitive", "name": "Sensitive", "appliesTo": ["guard"] }
            """);
        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", """{ "name": "traces", "appliesTo": { "stereotypes": ["sensitive"] }, "properties": { "owner": { "type": "string" } }, "required": ["owner"] }""");
        await repo.RestartAsync();

        Assert.Equal(Guard, Assert.Single(await FindingsAsync(repo), d => d.Rule == "MQ5001").ElementId);
    }

    [Fact]
    public async Task Importing_over_a_process_keeps_the_properties_and_stereotypes_of_its_nodes()
    {
        var marks = """, "stereotypes": ["sensitive"], "properties": { "owner": "finance" }""";
        await using var repo = await CopyAsync(Process(marks, marks, marks, marks), stereotype: $$"""
            { "kind": "stereotype", "id": "{{Marked}}", "key": "sensitive", "name": "Sensitive", "appliesTo": ["guard", "action", "invoke", "gate"] }
            """);
        var config = JsonDocument.Parse("""
            { "id": "Approval", "initial": "Open", "states": {
                "Open": { "invoke": { "id": "review", "src": "review" }, "on": { "approve": { "target": "Done", "guard": "is_ready", "actions": "notify" } } },
                "Done": { "type": "final" } } }
            """).RootElement.Clone();

        var imported = await repo.Store.ImportProcessAsync(new ProcessImportRequest { Config = config, Into = ProcessId }, dryRun: false, ChangeSource.Editor, Ct);

        Assert.True(imported.Status == ProcessCallStatus.Ok, imported.Detail);
        var saved = (await repo.Store.GetElementAsync(ProcessId, Ct))!.Json;
        foreach (var node in new[] { saved.GetProperty("guards")[0], saved.GetProperty("actions")[0], saved.GetProperty("states")[0].GetProperty("invoke")[0], saved.GetProperty("transitions")[0].GetProperty("gate") })
        {
            Assert.Equal("finance", node.GetProperty("properties").GetProperty("owner").GetString());
            Assert.Equal("sensitive", node.GetProperty("stereotypes")[0].GetString());
        }
    }

    [Fact]
    public async Task A_stereotype_used_only_by_a_guard_cannot_be_deleted_and_a_linked_policy_is_refused_too()
    {
        await using var repo = await CopyAsync(Process(""", "stereotypes": ["sensitive"], "properties": { "meets": "POLICY" }""".Replace("POLICY", Policy, StringComparison.Ordinal)),
            stereotype: $$"""{ "kind": "stereotype", "id": "{{Marked}}", "key": "sensitive", "name": "Sensitive", "appliesTo": ["guard"] }""");

        foreach (var id in new[] { Marked, Policy })
        {
            var document = await repo.Store.GetElementAsync(id, Ct);
            var refused = await repo.Store.DeleteAsync(id, document!.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct);
            Assert.Equal(SaveOutcome.Referenced, refused.Outcome);
        }

        // Clearing the link goes through the holder: the guard keeps its stereotype and loses the property.
        var policy = await repo.Store.GetElementAsync(Policy, Ct);
        Assert.Equal(SaveOutcome.Saved, (await repo.Store.DeleteAsync(Policy, policy!.Hash, DeleteResolution.RemoveReferences, ChangeSource.Editor, Ct)).Outcome);
        var guard = (await repo.Store.GetElementAsync(ProcessId, Ct))!.Json.GetProperty("guards")[0];
        Assert.Equal("sensitive", guard.GetProperty("stereotypes")[0].GetString());
        Assert.False(guard.TryGetProperty("properties", out var properties) && properties.TryGetProperty("meets", out _));
    }

    [Fact]
    public async Task A_guard_of_an_operation_is_a_holder_too()
    {
        await using var repo = await CopyAsync(Process(), "{{ for l in (referenced_by custom) }}{{ l.kind }} {{ l.id }} {{ l.field }}\n{{ end }}", operation: $$"""
            { "kind": "operation", "id": "{{Elsewhere}}", "name": "settle", "subject": "{{E2ERepo.InvoiceId}}", "level": "record", "effect": "change",
              "outcomes": [ { "id": "{{OperationOutcome}}", "name": "settled" } ],
              "guards": [ { "id": "{{Marked}}", "name": "is_open", "condition": { "value": true }, "properties": { "meets": "{{Policy}}" } } ] }
            """);

        await repo.ApplyAsync();

        Assert.Contains($"guard {Marked} meets\n", repo.Repo.ReadFile("src/Generated/links/keeps_lines.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Properties_on_a_tables_check_do_not_change_its_DDL_or_its_migration()
    {
        await using var repo = E2ERepo.Create(demo: true, migrations: true);
        repo.Repo.WriteFile(".maquettiste/extensions/traces.json", """{ "name": "traces", "appliesTo": { "kinds": ["check"] }, "properties": { "owner": { "type": "string" } } }""");
        await repo.RestartAsync();
        await repo.ApplyAsync();
        var before = Output(repo);

        await repo.EditAsync(InvoiceTable, table => table["checks"]![0]!["properties"] = JsonNode.Parse("""{ "owner": "finance" }"""));
        await repo.ApplyAsync();

        Assert.NotEmpty(before);
        Assert.Equal(before, Output(repo));
    }

    /// <summary>Every generated file, by path; manifests and the model are not output.</summary>
    private static SortedDictionary<string, string> Output(E2ERepo repo) =>
        new(repo.Repo.ListFiles().Where(f => !f.StartsWith(".maquettiste/", StringComparison.Ordinal)).ToDictionary(f => f, repo.Repo.ReadFile, StringComparer.Ordinal), StringComparer.Ordinal);

    [Fact]
    public async Task A_project_that_uses_none_of_this_keeps_its_files_byte_for_byte()
    {
        await using var repo = await CopyAsync(Process());
        await repo.EditAsync(ProcessId, _ => { });
        var process = repo.Repo.ReadFile(ProcessPath);
        Assert.DoesNotContain("\"properties\"", process, StringComparison.Ordinal);
        Assert.DoesNotContain("\"stereotypes\"", process, StringComparison.Ordinal);

        // An empty list or object says the same as nothing, so the save drops it.
        await repo.EditAsync(ProcessId, node =>
        {
            node["guards"]![0]!["stereotypes"] = new JsonArray();
            node["guards"]![0]!["properties"] = new JsonObject();
        });
        Assert.Equal(process, repo.Repo.ReadFile(ProcessPath));
    }
}
