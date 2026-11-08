using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The csharp-dapper output compiles (warnings as errors, nullable on) in a temporary project that references Dapper from NuGet,
/// and its repositories round-trip rows through an in-memory SQLite database built from the sql-ddl output. The tests skip
/// with a message when NuGet cannot be reached and the packages are not in the local cache.
/// </summary>
public sealed class CompileTests
{
    private const string DapperVersion = "2.1.66";
    private const string SqliteVersion = "10.0.1";

    [Fact]
    public async Task Csharp_dapper_output_for_postgresql_compiles_against_dapper()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var project = Path.Combine(repo.Repo.Root, "compile");
        WriteProject(project, repo.PathOf("src/Generated"), program: null);

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Csharp_dapper_operation_output_compiles_with_the_web_framework()
    {
        // send on Invoice without the lifecycle, attach (files) and browse (a paged list), with endpointsFolder outside the generated
        // folder: the contracts and the endpoints build together against the ASP.NET Core shared framework. archive's file members carry
        // the names the endpoint's own locals had before they took the __ prefix (form, fields, result, file).
        using var repo = PackRepo.Billing();
        repo.AddSendOperation(lifecycle: false);
        OperationPackTests.AddAttachAndBrowse(repo);
        repo.Write(".maquettiste/model/operations/customer/archive.json", """
            {
              "kind": "operation",
              "id": "01JR5NH0000000000000000031",
              "name": "archive",
              "package": "01J92P0V01KDRN8GX5PGYCNKSX",
              "subject": "01J92P0V0ETQKXXP951CMMNHH3",
              "effect": "change",
              "input": [
                { "id": "01JR5NH0000000000000000032", "name": "form", "file": true, "required": true },
                { "id": "01JR5NH0000000000000000033", "name": "fields", "file": true },
                { "id": "01JR5NH0000000000000000034", "name": "result", "file": true },
                { "id": "01JR5NH0000000000000000035", "name": "file", "file": true },
                { "id": "01JR5NH0000000000000000036", "name": "note", "type": "string", "length": 200 }
              ],
              "outcomes": [ { "id": "01JR5NH0000000000000000037", "name": "stored", "returns": { "file": true } } ]
            }

            """);
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"]!["allow"]!.AsArray().Add(new System.Text.Json.Nodes.JsonObject { ["path"] = "src/Api" });
            settings["packs"]!["csharp-dapper"]!["output"] = "src";
            settings["packs"]!["csharp-dapper"]!["parameters"] = new System.Text.Json.Nodes.JsonObject { ["generatedFolder"] = "Generated", ["partialFolder"] = "Generated", ["endpointsFolder"] = "Api" };
        });
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.True(File.Exists(repo.PathOf("src/Api/Operations/Billing/SendInvoiceEndpoint.g.cs")));
        var project = Path.Combine(repo.Repo.Root, "compile");
        WriteProject(project, repo.PathOf("src/Generated"), program: null, web: true, second: repo.PathOf("src/Api"));

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Csharp_dapper_operation_contracts_compile()
    {
        // send on Invoice without the lifecycle: the contracts refer to the entity class, so they build with the rest of the output.
        using var repo = PackRepo.Billing();
        repo.AddSendOperation(lifecycle: false);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.True(File.Exists(repo.PathOf("src/Generated/Operations/Billing/SendInvoice.g.cs")));
        var project = Path.Combine(repo.Repo.Root, "compile");
        WriteProject(project, repo.PathOf("src/Generated"), program: null);

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Query_rows_with_typed_columns_compile()
    {
        // next-phase-2-design.md section 3.2 (D26): a row record field that reads a column typed by an enum is the enum (a stored code
        // parsed, a stored number cast), and one that reads a column typed by a reference type keeps the code's type, the record's
        // summary naming the type's record in a cref; the output compiles with warnings as errors.
        using var repo = PackRepo.Billing();
        const string events = "01K9X3C0000000000000000001", main = "01J92P0V1QRN2181XM2ZWE02W4", billing = "01J92P0V01KDRN8GX5PGYCNKSX";
        repo.Write(".maquettiste/model/reference-types/invoice-state.json", $$"""
            {"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"01K9X3A0000000000000000001","name":"InvoiceState",
             "package":"{{billing}}","code":{"id":"01K9X3A0000000000000000002","length":1},"label":{"id":"01K9X3A0000000000000000003","length":40} }
            """);
        repo.Write(".maquettiste/model/seeds/invoice-state/invoice-state.json", """
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K9X3A0000000000000000010","name":"invoiceStates","target":"01K9X3A0000000000000000001",
             "columns":["code","label"],"rows":[{"id":"01K9X3A0000000000000000011","values":["D","Draft"]},{"id":"01K9X3A0000000000000000012","values":["I","Issued"]},
               {"id":"01K9X3A0000000000000000013","values":["P","Paid"]},{"id":"01K9X3A0000000000000000014","values":["V","Void"]}]}
            """);
        repo.Write(".maquettiste/model/databases/main/tables/invoice-events.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{events}}","name":"invoice_events","database":"{{main}}",
             "schema":"01J92P0V1RC04SKQ5353EAKHG2","columns":[
              {"id":"01K9X3C0000000000000000002","name":"id","type":"uuid","nullable":false},
              {"id":"01K9X3C0000000000000000003","name":"status","type":"string","typeRef":{"ref":"01K9X3A0000000000000000001"},"length":1,"nullable":false},
              {"id":"01K9X3C0000000000000000004","name":"invoice_status","type":"string","typeRef":{"ref":"01J92P0V05WQDH2GZG539D9VG1"},"nullable":false},
              {"id":"01K9X3C0000000000000000005","name":"stage","type":"int32","typeRef":{"ref":"01J92P0V05WQDH2GZG539D9VG1"} }],
             "primaryKey":{"columns":["01K9X3C0000000000000000002"]} }
            """);
        repo.Write(".maquettiste/model/databases/main/queries/events-by-status.json", $$"""
            {"$schema":"../../../../.schema/v1/query.json","kind":"query","id":"01K9X3F0000000000000000001","name":"events-by-status","database":"{{main}}",
             "from":{"source":"{{events}}","alias":"e"},
             "select":[{"name":"id","expression":{"column":"e.01K9X3C0000000000000000002"} },{"name":"status","expression":{"column":"e.01K9X3C0000000000000000003"} },
               {"name":"invoiceStatus","expression":{"column":"e.01K9X3C0000000000000000004"} },{"name":"stage","expression":{"column":"e.01K9X3C0000000000000000005"} }]}
            """);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var query = File.ReadAllText(Directory.GetFiles(repo.PathOf("src/Generated"), "EventsByStatusQuery*.cs", SearchOption.AllDirectories).Single());
        Assert.Matches(@"/// <summary>One row of query events-by-status\. Status holds a code of <see cref=""global::[\w.]+\.InvoiceState""/>\.</summary>", query);
        Assert.Matches(@"public sealed partial record EventsByStatusRow\(Guid Id, string Status, global::[\w.]+\.InvoiceStatus InvoiceStatus, global::[\w.]+\.InvoiceStatus\? Stage\);", query);
        Assert.Matches(@"new\(id, status, global::[\w.]+\.InvoiceStatusCodes\.Parse\(invoiceStatus\), \(global::[\w.]+\.InvoiceStatus\?\)stage\)", query);
        var project = Path.Combine(repo.Repo.Root, "compile");
        WriteProject(project, repo.PathOf("src/Generated"), program: null);

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Csharp_dapper_entity_checks_compile_and_validate_as_the_language_answers()
    {
        // next-phase-3-design.md section 3.1: Invoice's checks in the expression language, written as text and turned into trees, become
        // Validate() through expr_cs; the output compiles with warnings as errors and a program judges two invoices. A check the renderer
        // leaves out (a value object member) is a comment.
        using var repo = PackRepo.Billing();
        await AddInvoiceChecksAsync(repo);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var invoice = File.ReadAllText(Directory.GetFiles(repo.PathOf("src/Generated"), "Invoice.g.cs", SearchOption.AllDirectories).Single());
        Assert.Contains("public virtual IReadOnlyList<(string Pointer, string Code, string Message)> Validate()", invoice, StringComparison.Ordinal);
        Assert.Contains("RegexOptions.NonBacktracking", invoice, StringComparison.Ordinal);
        Assert.Contains("// Check totalNotNegative (total.amount >= 0) is outside what expr_cs renders: validate it in Invoice.cs.", invoice, StringComparison.Ordinal);
        var project = Path.Combine(repo.Repo.Root, "checks");
        WriteProject(project, repo.PathOf("src/Generated"), ChecksProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("checks ok", StringComparison.Ordinal), run.Output);
    }

    /// <summary>Gives the billing fixture's Invoice nine checks, written as text, then converted to trees by <c>text-to-trees</c>.</summary>
    internal static async Task AddInvoiceChecksAsync(PackRepo repo)
    {
        const string number = "01J92P0V0R3VSP7D5238DTNZX1", issuedOn = "01J92P0V0SNXS6PZ42VDK42HP9", total = "01J92P0V0T6EA0XM025XQX8GBW", notes = "01J92P0V0WKRGKH7YBKA2V30NC";
        (string Name, string Attribute, string Text)[] checks =
        [
            ("issuedAfter2000", issuedOn, "issuedOn >= date '2000-01-01'"),
            ("voidNeedsNotes", notes, "status <> InvoiceStatus.Void or notes is not null"),
            ("numberLength", number, "length(trim(number)) between 3 and 32"),
            ("numberFormat", number, "matches(number, '^[A-Z]+-[0-9]+$')"),
            ("notesShort", notes, "coalesce(length(notes), 0) <= 2000"),
            ("issuedWeekday", issuedOn, "date_part('isodow', add_months(issuedOn, 1)) between 1 and 7"),
            ("totalNotNegative", total, "total.amount >= 0"),
            ("paidHasNotes", notes, "case when status = InvoiceStatus.Paid then notes is not null else true end"),
            ("somethingGiven", number, "nonnull_count(notes, number) >= 1"),
        ];
        repo.EditJson(".maquettiste/model/entities/invoice.json", invoice => invoice["checks"] = new System.Text.Json.Nodes.JsonArray([.. checks.Select((c, i) =>
            (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject
            {
                ["id"] = "01K9CHK00000000000000000" + (i + 1).ToString("D2", System.Globalization.CultureInfo.InvariantCulture),
                ["name"] = c.Name,
                ["attribute"] = c.Attribute,
                ["condition"] = new System.Text.Json.Nodes.JsonObject { ["text"] = c.Text },
            })]));
        await using var store = new ModelStore(repo.Repo.Options);
        var parsed = store.ParseBatch("""{ "operations": [ { "op": "text-to-trees" } ] }"""u8);
        var applied = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Cli, TestContext.Current.CancellationToken);
        Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
    }

    private const string ChecksProgram = """
        using System;
        using System.Linq;
        using App.Model.Billing;

        // Two invoices judged by the generated Validate(): a valid one, and one that fails four checks, each at its attribute's pointer.
        // A number with a final newline fails the pattern, whose $ anchors the end of the string as in PostgreSQL.
        static void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Failed: " + what);
        }

        var valid = new Invoice { Number = "INV-12", IssuedOn = new DateOnly(2026, 1, 31), Status = InvoiceStatus.Paid, Notes = "paid by card" };
        Check(valid.Validate().Count == 0, "a valid invoice: " + string.Join(", ", valid.Validate().Select(e => e.Code)));
        var voided = new Invoice { Number = "x", IssuedOn = new DateOnly(1999, 12, 31), Status = InvoiceStatus.Void };
        var codes = voided.Validate().Select(e => e.Pointer + " " + e.Code).ToArray();
        Check(codes.SequenceEqual(["/issuedOn issuedAfter2000", "/notes voidNeedsNotes", "/number numberLength", "/number numberFormat"]), string.Join(", ", codes));
        var newline = new Invoice { Number = "INV-12\n", IssuedOn = new DateOnly(2026, 1, 31), Status = InvoiceStatus.Draft };
        Check(newline.Validate().Select(e => e.Code).SequenceEqual(["numberFormat"]), "a final newline");
        Console.WriteLine("checks ok");
        """;

    [Fact]
    public async Task Csharp_dapper_repositories_round_trip_through_the_sql_ddl_schema_on_sqlite()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();
        var project = Path.Combine(repo.Repo.Root, "roundtrip");
        WriteProject(project, repo.PathOf("src/Generated"), RoundTripProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet,
            [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), repo.PathOf("db/local/schema.sql"), repo.PathOf("db/local/seed.sql")],
            project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("round trip ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task Csharp_dapper_binding_repositories_compile_for_postgresql_and_round_trip_on_sqlite()
    {
        // Repositories from bindings (erratum E43): the PostgreSQL output compiles, and on SQLite every repository operation runs.
        using (var postgres = PackRepo.Bindings())
        {
            await postgres.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
            var library = Path.Combine(postgres.Repo.Root, "compile");
            WriteProject(library, postgres.PathOf("src/Generated"), program: null);
            await BuildOrSkipAsync(library);
        }

        using var repo = PackRepo.Bindings(sqlite: true);
        await repo.GenerateCleanlyAsync();
        var project = Path.Combine(repo.Repo.Root, "roundtrip");
        WriteProject(project, repo.PathOf("src/Generated"), BindingRoundTripProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet,
            [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), repo.PathOf("db/main/schema.sql")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("bindings round trip ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task Lifecycle_codes_compile_and_map_configurations_to_codes_and_back()
    {
        // lifecycle-design.md section 9: the lifecycle-codes case's whole output builds with warnings as errors, and a program maps
        // configurations to string and integer codes and back, reads the moves and each subject's status.
        using var repo = PackRepo.LifecycleCodes();
        await repo.GenerateCleanlyAsync();
        var project = Path.Combine(repo.Repo.Root, "codes");
        WriteProject(project, repo.PathOf("out"), LifecycleCodesProgram, web: true);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("codes ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task Actor_types_and_membership_compile_and_let_members_act()
    {
        // actors-design.md decisions 25 and 26: the actors case's output (a group, an organization, the system actor and membership
        // three levels deep) builds with warnings as errors, an enum member named System included, and members act for their holders.
        using var repo = PackRepo.Actors();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "actors", "expected"), repo.PathOf("out"));
        Directory.Delete(repo.PathOf("out/Tests"), recursive: true); // the scenario tests need a test host; see the next test
        var project = Path.Combine(repo.Repo.Root, "actors");
        WriteProject(project, repo.PathOf("out"), ActorsProgram, web: true);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("actors ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task Verbs_compile_with_the_rest_of_the_output()
    {
        // verbs-design.md section 8: the verbs case's output (Verbs.cs, the events' and the automatic move's verbs, an operation's Verb
        // constant) matches its expected files and builds with warnings as errors.
        using var repo = PackRepo.Verbs();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "verbs", "expected"), repo.PathOf("out"));
        Assert.Contains("public enum Verb", File.ReadAllText(repo.PathOf("out/Verbs.cs")), StringComparison.Ordinal);
        var project = Path.Combine(repo.Repo.Root, "verbs");
        WriteProject(project, repo.PathOf("out"), program: null, web: true);

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Object_effects_compile_with_the_rest_of_the_output()
    {
        // object-effects-design.md section 9: an action that sets data calls Record<Action>, which the companion implements. One with a
        // translated expression becomes an instance method that records, then returns the context; one with only sets, or with an expression
        // outside the translated subset, stays the companion's stub, and the dispatch runs Run<Action>, which records, then runs it. Guards
        // that read current_actor() or in_state are left to a person. The output matches its expected files and builds with warnings as errors.
        // process-docs shows the sets and the guards' conditions.
        using var repo = PackRepo.ObjectEffects();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "object-effects", "expected"), repo.PathOf("out"));
        var handlers = File.ReadAllText(Path.Combine(repo.PathOf("out"), "Processes", "RequestLifecycle", "RequestLifecycleHandlers.g.cs"));
        Assert.Contains("private partial void RecordRecordSubmission(", handlers, StringComparison.Ordinal);
        Assert.Contains("private partial bool GuardNotTheSubmitter(", handlers, StringComparison.Ordinal);
        Assert.Contains("private partial bool GuardEmployeeActive(", handlers, StringComparison.Ordinal);
        Assert.Contains("private partial RequestLifecycleContext ActionRecordSubmission(", handlers, StringComparison.Ordinal);
        Assert.Contains("\"recordSubmission\" => RunRecordSubmission(call),", handlers, StringComparison.Ordinal);
        Assert.Contains("private partial RequestLifecycleContext ActionStamp(", handlers, StringComparison.Ordinal);
        Assert.Contains("\"stamp\" => RunStamp(call),", handlers, StringComparison.Ordinal);
        Assert.Contains("\"bump\" => ActionBump(call),", handlers, StringComparison.Ordinal);

        var page = File.ReadAllText(Path.Combine(repo.PathOf("docs"), "processes", "request-lifecycle.md"));
        Assert.Contains("| Action | Expression | Sets | Raises | Used by | Description |", page, StringComparison.Ordinal);
        Assert.Contains("`submittedBy ← current_actor()`, `submittedOn ← now()`", page, StringComparison.Ordinal);
        Assert.Contains("`in_state(subject.employee, Employment.Active)`", page, StringComparison.Ordinal);
        Assert.Contains("`subject.submittedBy <> current_actor()`", page, StringComparison.Ordinal);
        var employment = File.ReadAllText(Path.Combine(repo.PathOf("docs"), "processes", "employment.md"));
        Assert.DoesNotContain("Sets", employment, StringComparison.Ordinal);

        var project = Path.Combine(repo.Repo.Root, "effects");
        WriteProject(project, repo.PathOf("out"), program: null, web: true);
        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Rights_compile_and_refuse_as_the_engine_does()
    {
        // rights-design.md section 9: Rights.cs, the conditions (a named right used by two lifecycles has one), the StatesIn hook (only on
        // the lifecycle whose event names a verb another lifecycle could refuse), the CallerHoldings stub and the interpreter's checks build
        // with warnings as errors. The team's half here answers the conditions, the owner and the records lifecycle's states; the host
        // registers ScenarioHoldings, and the scenario test and the host's checks pass.
        using var repo = PackRepo.Rights();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "rights", "expected"), repo.PathOf("out"));
        Assert.Contains("private partial IReadOnlyList<string>? StatesInRecords(", repo.Read("out/Processes/DocumentLifecycle/DocumentLifecycleHandlers.g.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("StatesIn", repo.Read("out/Processes/Records/RecordsHandlers.g.cs"), StringComparison.Ordinal);
        Assert.Contains("return ValueTask.FromResult<IReadOnlyList<string>>([]);", repo.Read("out/Dispatch/CallerHoldings.cs"), StringComparison.Ordinal);

        // Only a step whose participant holds an actor besides the one it acts as asserts that the handlers asked ScenarioHoldings: Dan,
        // who acts as Approver and holds Intern. Bea's verb steps resolve her as the document's owner, as the engine's replay did.
        var editing = repo.Read("out/Tests/DocumentLifecycle/EditingTests.cs");
        Assert.Equal(["asked7"], System.Text.RegularExpressions.Regex.Matches(editing, @"Assert\.True\((asked\d+)").Select(m => m.Groups[1].Value));
        Assert.Contains("Resolves = actor => actor is Actors.DocumentOwner,", editing, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(editing, "Resolves = ").Count); // her modify and her delete

        Replace("out/Docs/RightConditions.cs", "public static partial bool SecretOnlyOwner(global::App.Model.Processes.RightRequest request) =>\n        throw new NotImplementedException(\"The condition of named right SecretOnlyOwner is not implemented yet.\");",
            "public static partial bool SecretOnlyOwner(global::App.Model.Processes.RightRequest request) => Secret;\n\n    public static bool Secret { get; set; } = true;");
        Replace("out/Processes/Records/RecordsHandlers.cs", "public static partial bool HoldOnLegal(global::App.Model.Processes.RightRequest request) =>\n        throw new NotImplementedException(\"The condition of right HoldOnLegal of lifecycle Records is not implemented yet.\");",
            "public static partial bool HoldOnLegal(global::App.Model.Processes.RightRequest request) => false;");
        Replace("out/Processes/DocumentLifecycle/DocumentLifecycleHandlers.cs", "private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInRecords(StatechartCall<DocumentLifecycleContext> call) =>\n        throw new NotImplementedException(\"The states of the object in lifecycle Records are not read yet.\");",
            "private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInRecords(StatechartCall<DocumentLifecycleContext> call) => RecordsStates;\n\n    internal static global::System.Collections.Generic.IReadOnlyList<string> RecordsStates { get; set; } = [];");
        Replace("out/Processes/DocumentLifecycle/DocumentLifecycleHandlers.cs", "private partial bool IsDocumentOwner(ProcessSnapshot<DocumentLifecycleContext> snapshot, ProcessEnvelope envelope) => false;",
            "private partial bool IsDocumentOwner(ProcessSnapshot<DocumentLifecycleContext> snapshot, ProcessEnvelope envelope) => envelope.Signer == \"bea\";");
        Replace("out/Dispatch/Pipeline.cs", "throw new NotImplementedException(\"Map the caller's principal to the envelope's actor in Pipeline.cs (Authorize).\")", "true");

        var project = Path.Combine(repo.Repo.Root, "rights");
        WriteProject(project, repo.PathOf("out"), program: null, web: true);
        var csproj = Path.Combine(project, "RoundTrip.csproj");
        File.WriteAllText(csproj, File.ReadAllText(csproj)
            .Replace("<OutputType>Library</OutputType>", "<OutputType>Exe</OutputType>\n    <IsTestProject>true</IsTestProject>", StringComparison.Ordinal)
            .Replace("<Compile Include=\"Program.cs\" Condition=\"Exists('Program.cs')\" />",
                "<Compile Include=\"Host/*.cs\" />\n    <PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(project, "Host"));
        File.WriteAllText(Path.Combine(project, "Host", "ScenarioHost.cs"), RightsScenarioHost);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), "-noColor"], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("Total: 2, Errors: 0, Failed: 0,", StringComparison.Ordinal), run.Output);

        void Replace(string path, string stub, string with)
        {
            var text = repo.Read(path);
            Assert.Contains(stub, text, StringComparison.Ordinal);
            repo.Write(path, text.Replace(stub, with, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Trigger_points_compile_and_run_in_the_order_the_design_gives()
    {
        // trigger-points-design.md section 15: TriggerPoints.cs, the step methods, the three static classes (points named CheckModify, X
        // beside XWhen, and DocumentTriggerPoints in lifecycle Document), the interpreter's rows, DocumentVerbs and DocumentVerbDone, the
        // outbox messages with stable ids, the depth guard and the scenario test build with warnings as errors and run. The project has no
        // rights, so RefusedBy is written for trigger points alone. A second lifecycle, Legal, governs the document with a check on modify
        // narrowed to a state and a with-change action with sets on delete, so the runner and Document's event that names delete run its
        // points too (decision 11). The team's half here stands in for the object with static values.
        using var repo = PackRepo.TriggerPoints();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "trigger-points", "expected"), repo.PathOf("out"));
        Assert.False(File.Exists(repo.PathOf("out/Processes/Rights.cs")));
        var steps = repo.Read("out/Processes/Document/DocumentTriggerPoints.g.cs");
        Assert.Contains("public void CheckModify(TriggerPointCall call, TriggerPointOutcome outcome)", steps, StringComparison.Ordinal);
        Assert.Contains("public static partial bool DocumentTriggerPointsValue(TriggerPointCall call);", steps, StringComparison.Ordinal);
        Assert.Contains("public static partial bool X(TriggerPointCall call);", steps, StringComparison.Ordinal);
        Assert.Contains("public static partial void XWhen(TriggerPointCall call);", steps, StringComparison.Ordinal);
        Assert.Contains("triggerSteps: [new DocumentTriggerSteps(), new global::App.Model.Docs.LegalTriggerSteps()]", repo.Read("out/Processes/Document/DocumentDefinition.cs"), StringComparison.Ordinal);
        Assert.Contains("triggerSteps: [new LegalTriggerSteps()]", repo.Read("out/Processes/Legal/LegalDefinition.cs"), StringComparison.Ordinal);
        Assert.Contains("ILifecycleStates<DocumentContext>", repo.Read("out/Processes/Document/DocumentHandlers.g.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("ILifecycleStates", repo.Read("out/Processes/Legal/LegalHandlers.g.cs"), StringComparison.Ordinal);
        var release = repo.Read("out/Tests/Document/ReleaseTests.cs");
        Assert.Contains("var outcome2 = await new global::App.Model.Docs.DocumentVerbs(", release, StringComparison.Ordinal);
        Assert.Contains("Proposed = new Dictionary<string, object?>(StringComparer.Ordinal) { [\"price\"] = 80m, [\"title\"] = \"Pump spec\" },", release, StringComparison.Ordinal);
        Assert.Contains("ScenarioTriggers.Assumed = new Dictionary<string, string>(StringComparer.Ordinal) { [\"01JTP000000000000000000X10\"] = \"error\" };", release, StringComparison.Ordinal);
        Assert.Contains("public sealed class ScenarioTriggers : TriggerPointStandIn", repo.Read("out/Tests/ScenarioTriggers.cs"), StringComparison.Ordinal);

        repo.Write("out/Processes/Document/DocumentTriggerPoints.cs", TriggerPointsTeamCode);
        repo.Write("out/Processes/Legal/LegalTriggerPoints.cs", """
            using System.Collections.Generic;
            using App.Model.Processes;

            namespace App.Model.Docs;

            public static partial class LegalTriggerPoints
            {
                public static bool Refuse { get; set; }
                public static List<string> Cleared { get; } = [];

                public static partial bool LockedOnceCleared(TriggerPointCall call) => !Refuse;
                public static partial void ClearPrice(TriggerPointCall call) => Cleared.Add(call.Transition ?? "runner");
            }

            """);
        repo.Write("out/Docs/DocumentVerbs.cs", """
            using System.Collections.Generic;
            using App.Model.Processes;

            namespace App.Model.Docs;

            public sealed partial class DocumentVerbs
            {
                private static partial IReadOnlyList<string>? StatesInDocument(TriggerPointCall call) => ["Draft"];
                private static partial IReadOnlyList<string>? StatesInLegal(TriggerPointCall call) => ["Pending"];
            }

            """);
        repo.Write("out/Processes/Document/DocumentHandlers.cs", """
            using System.Collections.Generic;
            using App.Model.Runtime;

            namespace App.Model.Docs;

            public sealed partial class DocumentHandlers
            {
                private partial IReadOnlyList<string>? StatesInLegal(StatechartCall<DocumentContext> call) => [];
            }

            """);
        await BuildAndRunScenarioTestsAsync(repo, "trigger-points", TriggerPointsScenarioHost, 2);
    }

    // The team's half of the trigger-points case: static values stand in for the document, and switches let the host's tests refuse,
    // throw and count.
    private const string TriggerPointsTeamCode = """
        using System;
        using System.Collections.Generic;
        using System.Threading.Tasks;
        using App.Model.Processes;

        namespace App.Model.Docs;

        public static partial class DocumentTriggerPoints
        {
            public static string Title { get; set; } = "";
            public static List<string> Written { get; } = [];
            public static List<int> Published { get; } = [];
            public static bool Refuse { get; set; }
            public static bool Throw { get; set; }
            public static bool RefuseSign { get; set; }

            public static partial void AssignNumber(TriggerPointCall call) => call.Proposed["title"] = "Pump spec";
            public static partial bool TitleBeforeReview(TriggerPointCall call) => Title.Length > 0;
            public static partial bool X(TriggerPointCall call) => false;
            public static partial void XWhen(TriggerPointCall call) => Written.Add("price list");
            public static partial bool CheckModify(TriggerPointCall call) => !Refuse;
            public static partial void StampRelease(TriggerPointCall call)
            {
                if (Throw)
                    throw new InvalidOperationException("The stamp failed.");
                Written.Add("stamped");
            }
            public static partial Task PublishToPortal(TriggerPointCall call)
            {
                Published.Add(TriggerPointDepth.Current);
                return Task.CompletedTask;
            }
            public static bool ArchiveWhen { get; set; } = true;
            public static partial void Archive(TriggerPointCall call) => Written.Add("archived");
            public static partial bool DocumentTriggerPointsValue(TriggerPointCall call) => !RefuseSign;
            public static partial bool AskRegistry(TriggerPointCall call) => throw new InvalidOperationException("The registry was reached.");
        }

        public static partial class DocumentTriggerConditions
        {
            public static partial bool X(TriggerPointCall call) => true;
            public static partial bool Archive(TriggerPointCall call) => DocumentTriggerPoints.ArchiveWhen;
        }

        public static partial class DocumentTriggerRecords
        {
            public static partial void AssignNumber(TriggerPointCall call)
            {
                DocumentTriggerPoints.Title = (string)call.Proposed["title"]!;
                DocumentTriggerPoints.Written.Add("number");
            }
        }

        """;

    // The test host of the trigger-points case: the dispatcher over the lifecycle with an in-memory outbox, and the checks the scenario
    // can't make: a refused check writes nothing from a before action, a with-change body that throws fails the command, after-commit
    // messages reach the outbox only from a command that completes, the runner refuses promote, and the relay starts the depth guard.
    private const string TriggerPointsScenarioHost = """
        using System;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Dispatch;
        using App.Model.Docs;
        using App.Model.Processes;
        using App.Model.Runtime;
        using Xunit;

        [assembly: CollectionBehavior(DisableTestParallelization = true)]

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher, InMemoryProcessOutbox outbox) => (Dispatcher, Outbox) = (dispatcher, outbox);

            public Dispatcher Dispatcher { get; }

            public InMemoryProcessOutbox Outbox { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var outbox = new InMemoryProcessOutbox();
                var documents = new DocumentCommandHandler(new DocumentMachine(new DocumentHandlers(), host, host, host), new DocumentStore());
                var legal = new LegalCommandHandler(new LegalMachine(new LegalHandlers(), host, host, host), new LegalStore());
                TriggerPointStandIn.Current = new ScenarioTriggers();
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(documents, legal), new Pipeline(outbox)), outbox));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class TriggerPointTests
        {
            [Fact]
            public async Task Checks_writes_messages_and_the_depth_guard_behave_as_the_design_says()
            {
                var ct = TestContext.Current.CancellationToken;
                var clock = new ManualClock(DateTimeOffset.UnixEpoch);

                // The runner: a refusing check leaves the before action's records unwritten and nothing in the outbox; promote is refused.
                var outbox = new InMemoryProcessOutbox();
                var verbs = new DocumentVerbs(outbox, clock);
                DocumentTriggerPoints.Written.Clear();
                DocumentTriggerPoints.Refuse = true;
                var refused = await verbs.RunAsync(Verb.Modify, new TriggerPointCall { Key = "d-1", States = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>> { ["Document"] = ["Draft"] } }, (_, _) => Task.CompletedTask, ct);
                DocumentTriggerPoints.Refuse = false;
                Assert.Equal((Refusals.Check, "01JTP000000000000000000X05"), (refused.Refusal, refused.RefusedBy));
                Assert.DoesNotContain("number", DocumentTriggerPoints.Written);
                Assert.Empty(outbox.Messages);
                var promote = await verbs.RunAsync(Verb.Promote, new TriggerPointCall(), (_, _) => Task.CompletedTask, ct);
                Assert.Equal(Refusals.NoTransition, promote.Refusal);
                LegalTriggerPoints.Cleared.Clear();
                var deleted = await verbs.RunAsync(Verb.Delete, new TriggerPointCall { Key = "d-1", CommandId = "c-1" }, (_, _) => throw new InvalidOperationException("replaced"), ct);
                Assert.Equal("01JTP000000000000000000X08", deleted.Replaced);
                Assert.Equal("01JTP000000000000000000X08", Assert.IsType<DocumentVerbDone>(Assert.Single(outbox.Messages)).Replaced);
                Assert.Equal<string>(["runner"], LegalTriggerPoints.Cleared);

                // Every governing lifecycle's points run on an object verb, each reading the object's state there; a delete with no replace
                // skips Legal's with-change sets, since the object is gone.
                TriggerPointStandIn.Current = new ScenarioTriggers();
                ScenarioTriggers.Assumed = new System.Collections.Generic.Dictionary<string, string> { ["01JTP000000000000000000X10"] = "pass" };
                LegalTriggerPoints.Refuse = true;
                var locked = await verbs.RunAsync(Verb.Modify, new TriggerPointCall { Key = "d-1", States = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IReadOnlyList<string>> { ["Legal"] = ["Cleared"] } }, (_, _) => Task.CompletedTask, ct);
                var pending = await verbs.RunAsync(Verb.Modify, new TriggerPointCall { Key = "d-1" }, (_, _) => Task.CompletedTask, ct);
                LegalTriggerPoints.Refuse = false;
                ScenarioTriggers.Assumed = null;
                Assert.Equal((Refusals.Check, "01JTP000000000000000000X11"), (locked.Refusal, locked.RefusedBy));
                Assert.True(pending.Passed, pending.Message);
                DocumentTriggerPoints.ArchiveWhen = false;
                var removed = 0;
                var gone = await verbs.RunAsync(Verb.Delete, new TriggerPointCall { Key = "d-1" }, (_, _) => { removed++; return Task.CompletedTask; }, ct);
                DocumentTriggerPoints.ArchiveWhen = true;
                Assert.Equal((true, null, 1), (gone.Passed, gone.Replaced, removed));
                Assert.Equal<string>(["runner"], LegalTriggerPoints.Cleared);

                // A process command: the first approval is refused by the sign check, uncounted and audited as refused, and the refused
                // result still commits its audit; the throwing with-change body fails the next command, and no message reaches the outbox.
                await using var host = await ScenarioHost.StartAsync(clock, ct);
                DocumentTriggerPoints.Title = "Pump spec";
                var envelope = new ProcessEnvelope("d-2", Actors.Author, Signer: "bea");
                Assert.True((await host.Dispatcher.SendAsync(new DocumentStartControl(new ProcessEnvelope("d-2")) { Context = new DocumentContext() }, ct)).Accepted);
                Assert.True((await host.Dispatcher.SendAsync(new DocumentSubmitCommand(envelope), ct)).Accepted);
                DocumentTriggerPoints.RefuseSign = true;
                var first = await host.Dispatcher.SendAsync(new DocumentApproveCommand(envelope), ct);
                DocumentTriggerPoints.RefuseSign = false;
                Assert.Equal((Refusals.Check, "01JTP000000000000000000X09"), (first.Refusal, first.RefusedBy));
                Assert.Equal<string>(["refused"], [.. first.Audit.Select(a => a.OutcomeText)]);
                DocumentTriggerPoints.Throw = true;
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Dispatcher.SendAsync(new DocumentApproveCommand(envelope), ct));
                DocumentTriggerPoints.Throw = false;
                Assert.DoesNotContain(host.Outbox.Messages, m => m is TriggerPointMessage);

                // Accepted: the after-commit message joins the command's events with a stable id, and the relay starts the depth guard at
                // the message's depth.
                await using var again = await ScenarioHost.StartAsync(clock, ct);
                Assert.True((await again.Dispatcher.SendAsync(new DocumentStartControl(new ProcessEnvelope("d-3")) { Context = new DocumentContext() }, ct)).Accepted);
                Assert.True((await again.Dispatcher.SendAsync(new DocumentSubmitCommand(envelope with { Instance = "d-3" }), ct)).Accepted);
                var released = await again.Dispatcher.SendAsync(new DocumentApproveCommand(envelope with { Instance = "d-3" }), ct);
                Assert.True(released.Accepted, released.Refusal);
                var message = Assert.Single(again.Outbox.Messages.OfType<TriggerPointMessage>());
                Assert.Equal("d-3/3/01JTP000000000000000000X07/0", message.MessageId);
                Assert.True(message.Outside);
                DocumentTriggerPoints.Published.Clear();
                await TriggerPointRelay.DeliverAsync(message with { Depth = 3 });
                Assert.Equal<int>([4], DocumentTriggerPoints.Published);
                await Assert.ThrowsAsync<InvalidOperationException>(() => TriggerPointRelay.DeliverAsync(message with { Depth = TriggerPointDepth.Max }));
                using (TriggerPointDepth.StartAt(TriggerPointDepth.Max))
                    Assert.Throws<InvalidOperationException>(() => TriggerPointDepth.Enter());

                // An event that names delete still does its move, so no replace runs, and Legal's with-change sets run: the object stays.
                DocumentTriggerPoints.Written.Clear();
                LegalTriggerPoints.Cleared.Clear();
                Assert.True((await again.Dispatcher.SendAsync(new DocumentRetireCommand(envelope with { Instance = "d-3" }), ct)).Accepted);
                Assert.DoesNotContain("archived", DocumentTriggerPoints.Written);
                Assert.Equal<string>(["01JTP000000000000000000T03"], LegalTriggerPoints.Cleared);

                DocumentTriggerPoints.Title = "";
            }
        }

        """;

    [Fact]
    public async Task Named_trigger_points_compile_and_run_with_the_bypass_and_the_dry_run()
    {
        // trigger-points-design.md section 15, part 2: per-package TriggerPoints, TriggerConditions and TriggerRecords (a named Stamp beside
        // lifecycle Document's Stamp), a runner for a customer with no lifecycle that gets its number on create, exceptEntities tested on
        // the object's type (in a process, the type the handlers give), named points before the lifecycle's in a process command, two
        // actions narrowed by attributes on one verb, noBypass, the bypass stub refusing, a permitted skip logged, and a dry run that writes
        // nothing, logs no skip, returns the warnings and records the outside point.
        using var repo = PackRepo.NamedTriggerPoints();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "named-trigger-points", "expected"), repo.PathOf("out"));
        Assert.False(File.Exists(repo.PathOf("out/Sales/CustomerVerbs.cs")));
        Assert.Contains("public static partial void Stamp(TriggerPointCall call);", repo.Read("out/TriggerPoints/Docs/TriggerPoints.g.cs"), StringComparison.Ordinal);
        Assert.Contains("public static partial void Stamp(TriggerPointCall call);", repo.Read("out/Processes/Document/DocumentTriggerPoints.g.cs"), StringComparison.Ordinal);
        Assert.Contains("typeSteps: new NamedTriggerSteps()", repo.Read("out/Processes/Document/DocumentDefinition.cs"), StringComparison.Ordinal);
        Assert.Contains("return false;", repo.Read("out/Dispatch/TriggerBypass.cs"), StringComparison.Ordinal);

        repo.Write("out/TriggerPoints/Sales/TriggerPoints.cs", """
            using System.Collections.Generic;
            using System.Threading.Tasks;
            using App.Model.Processes;

            namespace App.Model.Sales;

            public static partial class TriggerPoints
            {
                public static List<string> Called { get; } = [];
                public static string Name { get; set; } = "Ada";
                public static string? Number { get; set; }

                public static partial bool CreditCheck(TriggerPointCall call)
                {
                    Called.Add("credit");
                    return true;
                }

                public static partial void CustomerNumber(TriggerPointCall call)
                {
                    Called.Add("number");
                    call.Proposed["number"] = "C-00042";
                }

                public static partial bool NameLength(TriggerPointCall call) => Name.Length <= 3;

                public static partial bool NameRequired(TriggerPointCall call) => Name.Length > 0;

                public static partial Task Welcome(TriggerPointCall call)
                {
                    Called.Add("welcome");
                    return Task.CompletedTask;
                }
            }

            public static partial class TriggerRecords
            {
                public static partial void CustomerNumber(TriggerPointCall call) => TriggerPoints.Number = (string?)call.Proposed["number"];
            }

            """);
        repo.Write("out/TriggerPoints/Docs/TriggerPoints.cs", """
            using System.Collections.Generic;
            using App.Model.Processes;

            namespace App.Model.Docs;

            public static partial class TriggerPoints
            {
                public static List<string> Stamped { get; } = [];

                public static partial void Stamp(TriggerPointCall call) => Stamped.Add("named");

                public static partial bool TitleWarning(TriggerPointCall call) => false;

                public static partial void TitleLog(TriggerPointCall call) => Stamped.Add("title log");

                public static partial void TitleStamp(TriggerPointCall call) => Stamped.Add("title stamp");
            }

            """);
        repo.Write("out/TriggerPoints/TriggerPoints.cs", """
            using System;
            using App.Model.Processes;

            namespace App.Model;

            public static partial class TriggerPoints
            {
                public static partial bool Portal(TriggerPointCall call) => throw new InvalidOperationException("The portal was reached.");
            }

            """);
        repo.Write("out/Processes/Document/DocumentTriggerPoints.cs", """
            using App.Model.Processes;

            namespace App.Model.Docs;

            public static partial class DocumentTriggerPoints
            {
                public static partial void Stamp(TriggerPointCall call) => TriggerPoints.Stamped.Add("lifecycle");

                public static partial void MarkRevised(TriggerPointCall call) => TriggerPoints.Stamped.Add("revised");

                public static partial void NoteRevision(TriggerPointCall call) => TriggerPoints.Stamped.Add("revision");
            }

            """);
        repo.Write("out/Processes/Document/DocumentHandlers.cs", """
            using System;
            using App.Model.Runtime;

            namespace App.Model.Docs;

            public sealed partial class DocumentHandlers
            {
                public partial Type EntityOf(StatechartCall<DocumentContext> call) => call.Instance.StartsWith("m-", StringComparison.Ordinal) ? typeof(Memo) : typeof(Document);
            }

            """);
        repo.Write("out/Docs/DocumentVerbs.cs", """
            using System.Collections.Generic;
            using App.Model.Processes;

            namespace App.Model.Docs;

            public sealed partial class DocumentVerbs
            {
                private static partial IReadOnlyList<string>? StatesInDocument(TriggerPointCall call) => ["Draft"];
            }

            """);
        // Orchestration Revising runs the named points on modify, an object verb, and none on promote, a move verb (owner's answer A5).
        var revising = repo.Read("out/Processes/Revising/RevisingDefinition.cs");
        Assert.Contains("public bool Speaks(global::App.Model.Verb verb) => verb is global::App.Model.Verb.Modify;", revising, StringComparison.Ordinal);
        Assert.DoesNotContain("01JNT000000000000000000N06", revising, StringComparison.Ordinal);
        Assert.DoesNotContain("01JNT000000000000000000N07", revising, StringComparison.Ordinal);
        repo.Write("out/Processes/Revising/RevisingHandlers.cs", """
            using System;
            using System.Collections.Generic;
            using App.Model.Runtime;

            namespace App.Model.Docs;

            public sealed partial class RevisingHandlers
            {
                private partial IReadOnlyList<string>? StatesInDocument(StatechartCall<RevisingContext> call) => ["Draft"];

                public partial Type EntityOf(StatechartCall<RevisingContext> call) => call.Instance.StartsWith("m-", StringComparison.Ordinal) ? typeof(Memo) : typeof(Document);
            }

            """);
        await BuildAndRunScenarioTestsAsync(repo, "named-trigger-points", NamedTriggerPointsScenarioHost, 2);
    }

    [Fact]
    public async Task Named_trigger_points_build_with_the_bypass_and_the_dry_run_off()
    {
        // Both pack parameters off: no ITriggerBypass, TriggerBypass.cs, SkipOptional or DryRun, the static outside run of part 1, and the
        // project still builds with warnings as errors.
        using var repo = PackRepo.NamedTriggerPointsOff();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "named-trigger-points-off", "expected"), repo.PathOf("out"));
        Assert.False(File.Exists(repo.PathOf("out/Dispatch/TriggerBypass.cs")));
        var shared = repo.Read("out/Processes/TriggerPoints.cs");
        Assert.DoesNotContain("ITriggerBypass", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("DryRun", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("Skips(", repo.Read("out/Sales/CustomerVerbs.g.cs"), StringComparison.Ordinal);
        Assert.Contains("internal static void Run(string id, TriggerPointCall call, Action body)", shared, StringComparison.Ordinal);
        var project = Path.Combine(repo.Repo.Root, "named-trigger-points-off");
        WriteProject(project, repo.PathOf("out"), program: null, web: true);
        await BuildOrSkipAsync(project);
    }

    // The test host of the named-trigger-points case: the dispatcher over the document lifecycle with an in-memory outbox, and the checks
    // the scenario can't make: the customer's runner, the bypass, the dry run, exceptEntities and the order in a process command.
    private const string NamedTriggerPointsScenarioHost = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Dispatch;
        using App.Model.Docs;
        using App.Model.Processes;
        using App.Model.Runtime;
        using App.Model.Sales;
        using Xunit;

        [assembly: CollectionBehavior(DisableTestParallelization = true)]

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher) => Dispatcher = dispatcher;

            public Dispatcher Dispatcher { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var documents = new DocumentCommandHandler(new DocumentMachine(new DocumentHandlers(), host, host, host), new DocumentStore());
                var revising = new RevisingCommandHandler(new RevisingMachine(new RevisingHandlers(), host, host, host), new RevisingStore());
                TriggerPointStandIn.Current = new ScenarioTriggers();
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(documents, revising), new Pipeline(new InMemoryProcessOutbox()))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class NamedTriggerPointTests
        {
            private const string Number = "01JNT000000000000000000N01", Required = "01JNT000000000000000000N02", Length = "01JNT000000000000000000N03";
            private const string Credit = "01JNT000000000000000000N04", Welcome = "01JNT000000000000000000N05";

            [Fact]
            public async Task Named_points_run_through_the_runner_and_the_interpreter_with_the_bypass_and_the_dry_run()
            {
                var ct = TestContext.Current.CancellationToken;
                var clock = new ManualClock(DateTimeOffset.UnixEpoch);
                static Task Nothing(TriggerPointCall call, CancellationToken token) => Task.CompletedTask;

                // A customer has no lifecycle: its runner runs the named points, gives it its number once the checks pass, and queues the
                // welcome, which the relay delivers.
                var outbox = new InMemoryProcessOutbox();
                Sales.TriggerPoints.Number = null;
                var performed = 0;
                var created = await new CustomerVerbs(outbox, clock).RunAsync(Verb.Create, new TriggerPointCall { Key = "c-1", CommandId = "load-1" }, (_, _) => { performed++; return Task.CompletedTask; }, ct);
                Assert.True(created.Passed, created.Message);
                Assert.Equal(("C-00042", 1), (Sales.TriggerPoints.Number, performed));
                Assert.Equal<string>([Number, Credit, Length, Required, Welcome], created.Ran);
                Assert.Empty(created.Warnings);
                var welcome = Assert.Single(outbox.Messages.OfType<TriggerPointMessage>());
                Assert.Equal(("load-1/c-1/" + Welcome + "/0", ""), (welcome.MessageId, welcome.Lifecycle));
                Assert.IsType<CustomerVerbDone>(outbox.Messages.Last());
                await TriggerPointRelay.DeliverAsync(welcome);
                Assert.Contains("welcome", Sales.TriggerPoints.Called);

                // The bypass stub refuses, so a skip asked for is not honoured: every point runs and nothing is skipped.
                var stubbed = await new CustomerVerbs(new InMemoryProcessOutbox(), clock).RunAsync(Verb.Create, new TriggerPointCall { Key = "c-2", CommandId = "load-2", SkipOptional = true }, Nothing, ct);
                Assert.Equal((5, 0), (stubbed.Ran.Count, stubbed.Skipped.Count));

                // A permitted skip: each skipped point is logged through the hook, and the noBypass check still runs, and still refuses.
                var bypass = new LoggingBypass();
                Sales.TriggerPoints.Number = null;
                var bulk = await new CustomerVerbs(new InMemoryProcessOutbox(), clock, bypass: bypass).RunAsync(Verb.Create, new TriggerPointCall { Key = "c-3", SkipOptional = true }, Nothing, ct);
                Assert.True(bulk.Passed, bulk.Message);
                Assert.Equal<string>([Required], bulk.Ran);
                Assert.Equal<string>([Number, Credit, Length, Welcome], bulk.Skipped);
                Assert.Equal<string>(bulk.Skipped, bypass.Logged);
                Assert.Null(Sales.TriggerPoints.Number);
                Sales.TriggerPoints.Name = "";
                var nameless = await new CustomerVerbs(new InMemoryProcessOutbox(), clock, bypass: bypass).RunAsync(Verb.Create, new TriggerPointCall { Key = "c-4", SkipOptional = true }, Nothing, ct);
                Assert.Equal((Refusals.Check, Required), (nameless.Refusal, nameless.RefusedBy));

                // A dry run: the before action and the checks run and the warning comes back, the outside check is recorded, not asked, and
                // nothing is written: no records, no effect, nothing in the outbox.
                Sales.TriggerPoints.Name = "Adelaide";
                Sales.TriggerPoints.Called.Clear();
                Sales.TriggerPoints.Number = null;
                var dryOutbox = new InMemoryProcessOutbox();
                var dry = await new CustomerVerbs(dryOutbox, clock).RunAsync(Verb.Create, new TriggerPointCall { Key = "c-5", DryRun = true }, (_, _) => throw new InvalidOperationException("A dry run never performs."), ct);
                Sales.TriggerPoints.Name = "Ada";
                Assert.True(dry.Passed, dry.Message);
                Assert.Equal<string>([Length], [.. dry.Warnings.Select(w => w.Id)]);
                Assert.Equal<string>([Credit], dry.Recorded);
                Assert.Equal<string>(["number"], Sales.TriggerPoints.Called);
                Assert.Null(Sales.TriggerPoints.Number);
                Assert.Empty(dryOutbox.Messages);

                // exceptEntities is tested on the object's type: a memo skips TitleWarning.
                var document = await new DocumentVerbs(new InMemoryProcessOutbox(), clock).RunAsync(Verb.Modify, new TriggerPointCall { Key = "d-1" }, Nothing, ct);
                var memo = await new DocumentVerbs(new InMemoryProcessOutbox(), clock).RunAsync(Verb.Modify, new TriggerPointCall { Key = "m-1", Entity = typeof(Memo) }, Nothing, ct);
                Assert.Equal<string>(["01JNT000000000000000000N08"], document.Ran);
                Assert.Empty(memo.Ran);

                // A process command: at each moment the named points run before the lifecycle's, the outside one answered by the stand-in.
                await using var host = await ScenarioHost.StartAsync(clock, ct);
                Assert.True((await host.Dispatcher.SendAsync(new DocumentStartControl(new ProcessEnvelope("d-9")) { Context = new DocumentContext() }, ct)).Accepted);
                Docs.TriggerPoints.Stamped.Clear();
                ScenarioTriggers.Assumed = new Dictionary<string, string> { ["01JNT000000000000000000N07"] = "warning" };
                var released = await host.Dispatcher.SendAsync(new DocumentReleaseCommand(new ProcessEnvelope("d-9", Actors.Author)), ct);
                ScenarioTriggers.Assumed = null;
                Assert.True(released.Accepted, released.Refusal);
                Assert.Equal<string>(["01JNT000000000000000000N07", "01JNT000000000000000000N06", "01JNT000000000000000000X01"], released.TriggerPoints);
                Assert.Equal<string>(["01JNT000000000000000000N07"], [.. released.CheckWarnings.Select(w => w.Id)]);
                Assert.Equal<string>(["named", "lifecycle"], Docs.TriggerPoints.Stamped);

                // A memo's process: the handlers give its own entity, so Stamp, which skips memos, doesn't run.
                Assert.True((await host.Dispatcher.SendAsync(new DocumentStartControl(new ProcessEnvelope("m-9")) { Context = new DocumentContext() }, ct)).Accepted);
                Docs.TriggerPoints.Stamped.Clear();
                ScenarioTriggers.Assumed = new Dictionary<string, string> { ["01JNT000000000000000000N07"] = "pass" };
                var memoReleased = await host.Dispatcher.SendAsync(new DocumentReleaseCommand(new ProcessEnvelope("m-9", Actors.Author)), ct);
                ScenarioTriggers.Assumed = null;
                Assert.True(memoReleased.Accepted, memoReleased.Refusal);
                Assert.Equal<string>(["01JNT000000000000000000N07", "01JNT000000000000000000X01"], memoReleased.TriggerPoints);
                Assert.Equal<string>(["lifecycle"], Docs.TriggerPoints.Stamped);

                // An orchestration: its event that names modify, an object verb, runs the named check on modify, which a memo skips; its event
                // that names promote, a move verb, runs no named point, not even the outside one, which the stand-in would answer.
                foreach (var key in new[] { "r-1", "m-r-1" })
                {
                    Assert.True((await host.Dispatcher.SendAsync(new RevisingStartControl(new ProcessEnvelope(key)) { Context = new RevisingContext() }, ct)).Accepted);
                    var touched = await host.Dispatcher.SendAsync(new RevisingTouchCommand(new ProcessEnvelope(key, Actors.Author)), ct);
                    Assert.True(touched.Accepted, touched.Refusal);
                    Assert.Equal<string>(key == "r-1" ? ["01JNT000000000000000000N08"] : [], touched.TriggerPoints);
                    Assert.Equal<string>(key == "r-1" ? ["01JNT000000000000000000N08"] : [], [.. touched.CheckWarnings.Select(w => w.Id)]);
                    Docs.TriggerPoints.Stamped.Clear();
                    var pushed = await host.Dispatcher.SendAsync(new RevisingPushCommand(new ProcessEnvelope(key, Actors.Author)), ct);
                    Assert.True(pushed.Accepted, pushed.Refusal);
                    Assert.Empty(pushed.TriggerPoints);
                    Assert.Empty(Docs.TriggerPoints.Stamped);
                }

                // Two actions narrowed by attributes on one verb, named and lifecycle, run when the title changes, named first.
                Docs.TriggerPoints.Stamped.Clear();
                var retitled = await new DocumentVerbs(new InMemoryProcessOutbox(), clock).RunAsync(Verb.Modify, new TriggerPointCall { Key = "d-2", Attributes = ["title"] }, Nothing, ct);
                Assert.True(retitled.Passed, retitled.Message);
                Assert.Equal<string>(["title log", "title stamp", "revised", "revision"], Docs.TriggerPoints.Stamped);

                // A dry run with a permitted skip logs nothing through the hook; the outcome still lists what it would skip.
                var quiet = new LoggingBypass();
                var dryBulk = await new CustomerVerbs(new InMemoryProcessOutbox(), clock, bypass: quiet).RunAsync(Verb.Create, new TriggerPointCall { Key = "c-6", SkipOptional = true, DryRun = true }, Nothing, ct);
                Assert.Equal<string>([Number, Credit, Length], dryBulk.Skipped);
                Assert.Empty(quiet.Logged);
            }

            private sealed class LoggingBypass : ITriggerBypass
            {
                public List<string> Logged { get; } = [];

                public bool Allows(TriggerPointCall call) => true;

                public void Skipped(TriggerPointCall call, string triggerPoint) => Logged.Add(triggerPoint);
            }
        }

        """;

    [Fact]
    public async Task Freezing_and_attribute_rights_compile_and_refuse_as_the_engine_does()
    {
        // rights-design.md section 9, part 2: the project names freeze, so Freezing.cs, Refusals.Frozen, the interpreter's frozen check and
        // the request's frozen flags are written; a right names attributes, so the request and the rights table carry them. The frozen
        // state is a host interface, IFrozenState, whose stub the team owns (Dispatch/FrozenState.cs) and answers "not frozen", so the
        // project builds with warnings as errors and nothing is refused for freezing until the team writes it. ScenarioHost registers
        // ScenarioFrozen, so the scenario test's event step refused as frozen is refused by the dispatcher. Its verb step that changes a
        // name and a salary is refused by the salary right, the first deny in file order, as the engine names it.
        using var repo = PackRepo.Freezing();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "freezing", "expected"), repo.PathOf("out"));
        Assert.Contains("public interface IFrozenState", repo.Read("out/Processes/Freezing.cs"), StringComparison.Ordinal);
        Assert.Contains("public sealed class FrozenState : IFrozenState", repo.Read("out/Dispatch/FrozenState.cs"), StringComparison.Ordinal);
        Assert.Contains("public const string Frozen = \"frozen\";", repo.Read("out/Runtime/Statechart.g.cs"), StringComparison.Ordinal);
        var test = repo.Read("out/Tests/EmployeeRecord/FreezingTests.cs");
        Assert.Contains("Attributes = [\"name\", \"salary\"],", test, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(\"01JFZ000000000000000000Y01\", decision3.RefusedBy);", test, StringComparison.Ordinal);
        Assert.Contains("Frozen = true,", test, StringComparison.Ordinal);
        Assert.Contains("ScenarioFrozen.Current = (true, false);", test, StringComparison.Ordinal);
        Assert.DoesNotContain("new FrozenFlags(", test, StringComparison.Ordinal);
        await BuildAndRunScenarioTestsAsync(repo, "freezing", FreezingScenarioHost.Replace("$Held", "new ScenarioHoldings(), ", StringComparison.Ordinal), 2);
    }

    [Fact]
    public async Task Freezing_without_rights_refuses_as_frozen_through_the_dispatcher()
    {
        // The freezing case with its rights taken out (rights-design.md decision 28): the project still names freeze, so the interpreter
        // checks frozen and the command handlers take IFrozenState; nothing is written for rights. The scenario's event step refused as
        // frozen goes through the dispatcher, and its verb step refused as frozen is checked with the replay's frozen flags.
        using var repo = PackRepo.Freezing();
        var process = JsonNode.Parse(repo.Read(".maquettiste/model/processes/employee-record.json"))!.AsObject();
        Assert.True(process.Remove("rights"));
        repo.Write(".maquettiste/model/processes/employee-record.json", process.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        File.Delete(repo.PathOf(".maquettiste/model/rights/owner-kept-while-frozen.json"));
        const string Z = "01JFZ000000000000000000";
        repo.Write(".maquettiste/model/scenarios/employee-record/freezing.json", $$$"""
            {"kind":"scenario","id":"{{{Z}}}C01","name":"Freezing","process":"{{{Z}}}Q01",
             "objects":[{"id":"{{{Z}}}J01","name":"ada","entity":"{{{Z}}}E01","values":{"{{{Z}}}A02":"Ada"}}],"subject":"{{{Z}}}J01",
             "steps":[{"id":"{{{Z}}}011","event":"{{{Z}}}V02","expect":{"states":["{{{Z}}}S01"]}},
               {"id":"{{{Z}}}012","event":"{{{Z}}}V01","expect":{"accepted":false,"refusal":"frozen"}},
               {"id":"{{{Z}}}013","input":"verb","verb":"modify","values":{"{{{Z}}}A02":"Ada K"},"expect":{"accepted":false,"refusal":"frozen"}},
               {"id":"{{{Z}}}014","event":"{{{Z}}}V03","expect":{"states":["{{{Z}}}S01"]}},
               {"id":"{{{Z}}}015","event":"{{{Z}}}V01","expect":{"states":["{{{Z}}}S01"]}},
               {"id":"{{{Z}}}016","event":"{{{Z}}}V04","expect":{"states":["{{{Z}}}S02"]}}],
             "outcome":"final"}

            """);
        await repo.GenerateCleanlyAsync();
        Assert.False(File.Exists(repo.PathOf("out/Processes/Rights.cs")));
        Assert.False(File.Exists(repo.PathOf("out/Tests/ScenarioHoldings.cs")));
        Assert.Contains("IFrozenState? frozen = null) :", repo.Read("out/Processes/EmployeeRecord/EmployeeRecordHandlers.g.cs"), StringComparison.Ordinal);
        var test = repo.Read("out/Tests/EmployeeRecord/FreezingTests.cs");
        Assert.Contains("ScenarioFrozen.Current = (true, false);", test, StringComparison.Ordinal);
        Assert.Contains("Assert.Equal(\"frozen\", result.Refusal);", test, StringComparison.Ordinal);
        Assert.Contains("Assert.True(new FrozenFlags(", test, StringComparison.Ordinal);
        await BuildAndRunScenarioTestsAsync(repo, "freezing-without-rights", FreezingScenarioHost[..FreezingScenarioHost.IndexOf("public sealed class FreezingTests", StringComparison.Ordinal)].Replace("$Held", "", StringComparison.Ordinal), 1);
    }

    // Builds the freezing case's output with a test host and runs its scenario tests, which must all pass.
    [Fact]
    public async Task Approvals_compile_and_respond_as_the_engine_does()
    {
        // signoff-design.md section 15: the interpreter's approvals, the envelope's Response and Signature, ApprovalProgress and ActRecord
        // in the snapshot, ReleaseApprovalConditions (FinanceCheck's method, ApprovalConditions' named like its class, and Check's and check's,
        // which clash with FinanceCheck's and take their ids' last eight characters, the ULIDs' random ends), reached through
        // IApprovalConditions, a deny on reject, a check on reject, and endpoints whose request carries Response and Signature build with
        // warnings as errors. The scenario test agrees with the engine's replay, and the host's checks pass.
        using var repo = PackRepo.Approvals();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "approvals", "expected"), repo.PathOf("out"));
        var handlers = repo.Read("out/Processes/Release/ReleaseHandlers.g.cs");
        Assert.Contains("public static partial bool ReleaseFinanceCheck(ApprovalCall<ReleaseContext> call);", handlers, StringComparison.Ordinal);
        Assert.Contains("public static partial bool ReleaseApprovalConditionsValue(ApprovalCall<ReleaseContext> call);", handlers, StringComparison.Ordinal);
        Assert.Contains("public static partial bool ReleaseFinanceCheck000000N5(ApprovalCall<ReleaseContext> call);", handlers, StringComparison.Ordinal);
        Assert.Contains("public static partial bool ReleaseFinanceCheck000000N8(ApprovalCall<ReleaseContext> call);", handlers, StringComparison.Ordinal);
        Assert.Contains("{ Response = request.Response, Signature = request.Signature }", repo.Read("out/Api/Processes/Release/Endpoints/ReleaseEndpoints.cs"), StringComparison.Ordinal);
        Assert.Contains("{ RecordsActs = true };", repo.Read("out/Processes/Release/ReleaseDefinition.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("RecordsActs", repo.Read("out/Processes/MemoLifecycle/MemoLifecycleDefinition.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("IApprovalConditions", repo.Read("out/Processes/MemoLifecycle/MemoLifecycleHandlers.g.cs"), StringComparison.Ordinal);

        // The team's half: the conditions read the typed context, and the check on reject lets every veto through.
        Replace("out/Processes/Release/ReleaseHandlers.cs", "public static partial bool ReleaseFinanceCheck(ApprovalCall<ReleaseContext> call) =>\n        throw new NotImplementedException(\"The condition of signature FinanceCheck of gate Release of Release is not implemented yet.\");",
            "public static partial bool ReleaseFinanceCheck(ApprovalCall<ReleaseContext> call) => call.Context.Cost > 10000m;");
        Replace("out/Processes/Release/ReleaseHandlers.cs", "public static partial bool ReleaseApprovalConditionsValue(ApprovalCall<ReleaseContext> call) =>\n        throw new NotImplementedException(\"The condition of signature ApprovalConditions of gate Release of Release is not implemented yet.\");",
            "public static partial bool ReleaseApprovalConditionsValue(ApprovalCall<ReleaseContext> call) => call.Context.Cost > 100000m;");
        Replace("out/Processes/Release/ReleaseHandlers.cs", "public static partial bool ReleaseFinanceCheck000000N5(ApprovalCall<ReleaseContext> call) =>\n        throw new NotImplementedException(\"The condition of signature Check of gate ReleaseFinance of Release is not implemented yet.\");",
            "public static partial bool ReleaseFinanceCheck000000N5(ApprovalCall<ReleaseContext> call) => call.Context.Cost > 50000m;");
        Replace("out/Processes/Release/ReleaseHandlers.cs", "public static partial bool ReleaseFinanceCheck000000N8(ApprovalCall<ReleaseContext> call) =>\n        throw new NotImplementedException(\"The condition of signature check of gate ReleaseFinance of Release is not implemented yet.\");",
            "public static partial bool ReleaseFinanceCheck000000N8(ApprovalCall<ReleaseContext> call) => call.Context.Cost > 75000m;");
        Replace("out/Processes/Release/ReleaseTriggerPoints.cs", "public static partial bool VetoCheck(TriggerPointCall call) =>\n        throw new NotImplementedException(\"Trigger point VetoCheck of lifecycle Release is not implemented yet.\");",
            "public static partial bool VetoCheck(TriggerPointCall call) => true;");
        await BuildAndRunScenarioTestsAsync(repo, "approvals", ApprovalsScenarioHost, 5);

        void Replace(string path, string stub, string with)
        {
            var text = repo.Read(path);
            Assert.Contains(stub, text, StringComparison.Ordinal);
            repo.Write(path, text.Replace(stub, with, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task Approval_rounds_compile_and_respond_as_the_engine_does()
    {
        // signoff-design.md section 10, part 2: rounds with skipped stages and DealApprovalConditions' stage methods (reached through
        // IApprovalConditions.StageNeeded), delegation checked through IActorResolver, ApprovalNotices.Due, and Reauthenticated build with
        // warnings as errors. The scenario test agrees with the engine's replay, and the host's checks pass.
        using var repo = PackRepo.ApprovalRounds();
        await repo.GenerateCleanlyAsync();
        Golden.AssertMatches(Path.Combine(Fixtures.RepoRoot, "packs", "csharp-dapper", "tests", "approval-rounds", "expected"), repo.PathOf("out"));
        var handlers = repo.Read("out/Processes/Deal/DealHandlers.g.cs");
        Assert.Contains("public static partial bool SignoffLegalStage(ApprovalCall<DealContext> call);", handlers, StringComparison.Ordinal);
        Assert.Contains("public bool StageNeeded(string stage, ApprovalCall<DealContext> call) => stage switch", handlers, StringComparison.Ordinal);
        Assert.Contains("IActorResolver<DealContext>", handlers, StringComparison.Ordinal);
        var test = repo.Read("out/Tests/Deal/RoundsTests.cs");
        Assert.Contains("DelegateTo = \"dora\", Reauthenticated = true", test, StringComparison.Ordinal);
        Assert.Contains("OnBehalfOf = \"carl\", Reauthenticated = true", test, StringComparison.Ordinal);

        Replace("out/Processes/Deal/DealHandlers.cs", "public static partial bool SignoffLegalStage(ApprovalCall<DealContext> call) =>\n        throw new NotImplementedException(\"The condition of stage Legal of gate Signoff of Deal is not implemented yet.\");",
            "public static partial bool SignoffLegalStage(ApprovalCall<DealContext> call) => call.Context.Cost > 50000m;");
        Replace("out/Processes/Deal/DealHandlers.cs", "public static partial bool SignoffBoardStage(ApprovalCall<DealContext> call) =>\n        throw new NotImplementedException(\"The condition of stage Board of gate Signoff of Deal is not implemented yet.\");",
            "public static partial bool SignoffBoardStage(ApprovalCall<DealContext> call) => call.Context.Cost > 100000m;");
        Replace("out/Processes/Deal/DealHandlers.cs", "private partial bool IsDeputyController(ProcessSnapshot<DealContext> snapshot, ProcessEnvelope envelope) => false;",
            "private partial bool IsDeputyController(ProcessSnapshot<DealContext> snapshot, ProcessEnvelope envelope) => envelope.Signer == \"dora\";");
        await BuildAndRunScenarioTestsAsync(repo, "approval-rounds", ApprovalRoundsScenarioHost, 5);

        void Replace(string path, string stub, string with)
        {
            var text = repo.Read(path);
            Assert.Contains(stub, text, StringComparison.Ordinal);
            repo.Write(path, text.Replace(stub, with, StringComparison.Ordinal));
        }
    }

    // The test host of the approval-rounds case: the dispatcher over the lifecycle, and the checks the scenario can't make: a round's
    // completion moves nothing and skips a stage, ApprovalNotices.Due lists stable ids at the round's start plus each delay, a response
    // without Reauthenticated is refused, a delegation can't be passed on, and an answer on behalf counts as the delegator's actor.
    private const string ApprovalRoundsScenarioHost = """
        using System;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Deals;
        using App.Model.Dispatch;
        using App.Model.Processes;
        using App.Model.Runtime;
        using Xunit;

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher) => Dispatcher = dispatcher;

            public Dispatcher Dispatcher { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var deals = new DealCommandHandler(new DealMachine(new DealHandlers(), host, host, host), new DealStore());
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(deals), new Pipeline(new InMemoryProcessOutbox()))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class ApprovalRoundTests
        {
            private const string Approve = "01JAE0000000000000000000T2";
            private static readonly DateTimeOffset Start = new(2026, 11, 9, 9, 0, 0, TimeSpan.Zero);

            private static (DealMachine Machine, StepResult<DealContext> Review) InRoundTwo(string instance)
            {
                var host = new ProcessHost(new ManualClock(Start));
                var machine = new DealMachine(new DealHandlers(), host, host, host);
                var review = machine.Submit(machine.Start(instance, new DealContext { Cost = 20000m }).Snapshot, new DealSubmitCommand(new ProcessEnvelope(instance, Actors.Engineer, "ana")));
                var signed = machine.Approve(review.Snapshot, new DealApproveCommand(new ProcessEnvelope(instance, Actors.Engineer, "ben")));
                return (machine, signed);
            }

            [Fact]
            public void A_round_completes_without_a_move_and_skips_a_stage()
            {
                var (_, signed) = InRoundTwo("d1");
                Assert.True(signed.Accepted, signed.Refusal);
                Assert.Equal<string>(["Review"], signed.States);
                Assert.Equal([GateOutcome.Signed], signed.Audit.Select(a => a.Outcome));
                var approval = signed.Snapshot.Approvals[Approve];
                Assert.Equal(2, approval.Round);
                Assert.Equal<string>(["01JAE0000000000000000000N2"], approval.Needed);
                Assert.Equal<string>(["01JAE0000000000000000000H3"], approval.Skipped);
                Assert.Equal(Start, approval.Started);
            }

            [Fact]
            public void Notices_fall_due_at_the_round_start_plus_each_delay_with_stable_ids()
            {
                var (_, signed) = InRoundTwo("d2");
                Assert.Empty(ApprovalNotices.Due(signed.Snapshot, Start.AddDays(1)));
                var remind = Assert.Single(ApprovalNotices.Due(signed.Snapshot, Start.AddDays(2)));
                Assert.Equal("d2/01JAE0000000000000000000T2/2026-11-09T09:00:00.0000000+00:00/01JAE0000000000000000000N2/remind", remind.Id);
                Assert.Equal((NoticeKind.Remind, "Deal", "Signoff", "Finance", "Controller"), (remind.Kind, remind.Lifecycle, remind.Gate, remind.Stage, remind.Signature));
                Assert.Equal<string>([Actors.Controller], remind.Recipients);
                Assert.Equal(Start.AddDays(2), remind.Due);
                var both = ApprovalNotices.Due(signed.Snapshot, Start.AddDays(5));
                Assert.Equal([NoticeKind.Remind, NoticeKind.Escalate], both.Select(n => n.Kind));
                Assert.Equal(remind.Id, both[0].Id);
                Assert.Equal<string>([Actors.FinanceDirector], both[1].Recipients);
            }

            [Fact]
            public void A_response_without_reauthentication_is_refused()
            {
                var (machine, signed) = InRoundTwo("d3");
                var refused = machine.Approve(signed.Snapshot, new DealApproveCommand(new ProcessEnvelope("d3", Actors.Controller, "carl")));
                Assert.Equal(Refusals.GateReauthenticate, refused.Refusal);
                Assert.Equal([GateOutcome.Refused], refused.Audit.Select(a => a.Outcome));
                var delegated = machine.Approve(refused.Snapshot, new DealApproveCommand(new ProcessEnvelope("d3", Actors.Controller, "carl") { Response = SignatureResponse.Delegate, DelegateTo = "dora" }));
                Assert.Equal(Refusals.GateReauthenticate, delegated.Refusal);
            }

            [Fact]
            public void An_answer_on_behalf_counts_as_the_delegator_and_cannot_be_passed_on()
            {
                var (machine, signed) = InRoundTwo("d4");
                var delegated = machine.Approve(signed.Snapshot, new DealApproveCommand(new ProcessEnvelope("d4", Actors.Controller, "carl") { Response = SignatureResponse.Delegate, DelegateTo = "dora", Reauthenticated = true }));
                Assert.True(delegated.Accepted, delegated.Refusal);
                var record = Assert.Single(delegated.Audit);
                Assert.Equal((GateOutcome.Delegated, "carl", "dora"), (record.Outcome, record.Signer, record.DelegateTo));
                Assert.Equal<string>(["carl"], Assert.Single(ApprovalNotices.Due(delegated.Snapshot, Start.AddDays(2))).Finished);

                var passed = machine.Approve(delegated.Snapshot, new DealApproveCommand(new ProcessEnvelope("d4", Actors.DeputyController, "dora") { Response = SignatureResponse.Delegate, DelegateTo = "dora", OnBehalfOf = "carl", Reauthenticated = true }));
                Assert.Equal(Refusals.GateSigner, passed.Refusal);
                var stranger = machine.Approve(passed.Snapshot, new DealApproveCommand(new ProcessEnvelope("d4", Actors.DeputyController, "finn") { OnBehalfOf = "carl", Reauthenticated = true }));
                Assert.Equal(Refusals.GateSigner, stranger.Refusal);

                var behalf = machine.Approve(stranger.Snapshot, new DealApproveCommand(new ProcessEnvelope("d4", Actors.DeputyController, "dora") { OnBehalfOf = "carl", Reauthenticated = true }));
                Assert.True(behalf.Accepted, behalf.Refusal);
                Assert.Equal([GateOutcome.Signed, GateOutcome.Completed], behalf.Audit.Select(a => a.Outcome));
                Assert.Equal(("dora", "carl", "01JAE0000000000000000000R2"), (behalf.Audit[0].Signer, behalf.Audit[0].OnBehalfOf, behalf.Audit[0].Actor));
                Assert.Equal<string>(["Approved"], behalf.States);
                Assert.Empty(behalf.Snapshot.Approvals);
            }
        }
        """;

    // The test host of the approvals case: the dispatcher over both lifecycles, and the checks the scenario can't make: a rejection clears
    // the snapshot's progress and makes no move, a refused response saves only its audit, notBy reads the envelope's Signer, the conditions
    // are reached through IApprovalConditions with a typed context, Acts stays empty in a lifecycle without a notBy, and signing a complete
    // signature again is a repeat.
    private const string ApprovalsScenarioHost = """
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Dispatch;
        using App.Model.Docs;
        using App.Model.Processes;
        using App.Model.Runtime;
        using Xunit;

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher) => Dispatcher = dispatcher;

            public Dispatcher Dispatcher { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var releases = new ReleaseCommandHandler(new ReleaseMachine(new ReleaseHandlers(), host, host, host), new ReleaseStore());
                var memos = new MemoLifecycleCommandHandler(new MemoLifecycleMachine(new MemoLifecycleHandlers(), host, host, host), new MemoLifecycleStore());
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(memos, releases), new Pipeline(new InMemoryProcessOutbox()))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class ApprovalTests
        {
            private const string Approve = "01JAD0000000000000000000T2";

            [Fact]
            public void A_rejection_clears_the_progress_and_notBy_reads_the_signer()
            {
                var host = new ProcessHost(new ManualClock(DateTimeOffset.UnixEpoch));
                var machine = new ReleaseMachine(new ReleaseHandlers(), host, host, host);
                var started = machine.Start("r1", new ReleaseContext { Cost = 500m });
                var review = machine.Submit(started.Snapshot, new ReleaseSubmitCommand(new ProcessEnvelope("r1", Actors.Engineer, "ana")));
                Assert.Equal<string>(["Review"], review.States);
                Assert.Equal<string>(["01JAD0000000000000000000N1", "01JAD0000000000000000000N2"], review.Snapshot.Approvals[Approve].Needed);
                Assert.Contains(review.Snapshot.Acts, a => a.Signer == "ana" && a.Verb == "promote");

                var excluded = machine.Approve(review.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r1", Actors.Engineer, "ana")));
                Assert.Equal(Refusals.GateExcluded, excluded.Refusal);
                Assert.Equal("01JAD0000000000000000000N1", excluded.Audit.Single().Signature);

                var signed = machine.Approve(excluded.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r1", Actors.Engineer, "ben")));
                Assert.Single(signed.Snapshot.Approvals[Approve].Signed);
                var vetoed = machine.Approve(signed.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r1", Actors.Engineer, "ben", Reason: "No.") { Response = SignatureResponse.Reject }));
                Assert.True(vetoed.Accepted, vetoed.Refusal);
                Assert.False(vetoed.Moved);
                Assert.Empty(vetoed.Snapshot.Approvals[Approve].Signed);
                Assert.Equal([GateOutcome.Rejected], vetoed.Audit.Select(a => a.Outcome));
                Assert.Equal("01JAD0000000000000000000H1", vetoed.Audit.Single().Stage);

                var nothing = machine.Approve(vetoed.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r1", Actors.Engineer, "ben") { Response = SignatureResponse.Withdraw }));
                Assert.Equal(Refusals.GateNotSigned, nothing.Refusal);
            }

            [Fact]
            public void Signing_a_complete_signature_again_is_a_repeat()
            {
                var host = new ProcessHost(new ManualClock(DateTimeOffset.UnixEpoch));
                var machine = new ReleaseMachine(new ReleaseHandlers(), host, host, host);
                var review = machine.Submit(machine.Start("r4", new ReleaseContext { Cost = 500m }).Snapshot, new ReleaseSubmitCommand(new ProcessEnvelope("r4", Actors.Engineer, "ana")));
                var signed = machine.Approve(review.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r4", Actors.QualityLead, "quinn")));
                Assert.True(signed.Accepted, signed.Refusal);
                Assert.Equal("01JAD0000000000000000000N2", signed.Audit.Single().Signature);

                // Quality needs one signature and has it: the same person signing again is refused as a repeat on it, by pick or by name.
                var again = machine.Approve(signed.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r4", Actors.QualityLead, "quinn")));
                Assert.Equal(Refusals.GateRepeat, again.Refusal);
                Assert.Equal("01JAD0000000000000000000N2", again.Audit.Single().Signature);
                var named = machine.Approve(again.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r4", Actors.QualityLead, "quinn") { Signature = "Quality" }));
                Assert.Equal(Refusals.GateRepeat, named.Refusal);

                // An ignore has nothing left to answer on a complete signature: with no open one the person meets, it is refused as no signer.
                var ignored = machine.Approve(named.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r4", Actors.QualityLead, "quinn") { Response = SignatureResponse.Ignore }));
                Assert.Equal(Refusals.GateSigner, ignored.Refusal);
                Assert.DoesNotContain(ignored.Audit, a => a.Outcome == GateOutcome.Ignored);
                Assert.Single(ignored.Snapshot.Approvals[Approve].Signed);
                var other = machine.Approve(ignored.Snapshot, new ReleaseApproveCommand(new ProcessEnvelope("r4", Actors.QualityLead, "quentin") { Response = SignatureResponse.Ignore }));
                Assert.Equal(Refusals.GateSigner, other.Refusal);
                Assert.DoesNotContain(other.Audit, a => a.Outcome == GateOutcome.Ignored);
            }

            [Fact]
            public async Task A_refused_response_saves_only_its_audit()
            {
                var ct = TestContext.Current.CancellationToken;
                var host = new ProcessHost(new ManualClock(DateTimeOffset.UnixEpoch));
                var store = new ReleaseStore();
                var handler = new ReleaseCommandHandler(new ReleaseMachine(new ReleaseHandlers(), host, host, host), store);
                await handler.HandleAsync(new ReleaseStartControl(new ProcessEnvelope("r2")) { Context = new ReleaseContext() }, ct);
                await handler.HandleAsync(new ReleaseSubmitCommand(new ProcessEnvelope("r2", Actors.Engineer, "ana")), ct);
                var before = (await store.LoadAsync("r2", ct))!;
                var refused = await handler.HandleAsync(new ReleaseApproveCommand(new ProcessEnvelope("r2", Actors.Engineer, "ana")), ct);
                Assert.Equal(Refusals.GateExcluded, refused.Refusal);
                var after = (await store.LoadAsync("r2", ct))!;
                Assert.Equal(before.Version + 1, after.Version);
                Assert.Equal(before.AuditSequence + 1, after.AuditSequence);
                Assert.Empty(after.Approvals[Approve].Signed);
                Assert.Equal(before.Acts.Count, after.Acts.Count);
            }

            [Fact]
            public void The_conditions_are_asked_with_a_typed_context_and_a_memo_records_no_acts()
            {
                IApprovalConditions<ReleaseContext> conditions = new ReleaseHandlers();
                ApprovalCall<ReleaseContext> Call(decimal cost) => new("r3", new ReleaseContext { Cost = cost }, "Release", "Release", "Review", "FinanceCheck", ["Review"]);
                Assert.True(conditions.Needed("01JAD0000000000000000000N3", Call(20000m)));
                Assert.False(conditions.Needed("01JAD0000000000000000000N3", Call(500m)));
                Assert.True(conditions.Needed("01JAD0000000000000000000N1", Call(500m)));

                var host = new ProcessHost(new ManualClock(DateTimeOffset.UnixEpoch));
                var machine = new ReleaseMachine(new ReleaseHandlers(), host, host, host);
                var costly = machine.Submit(machine.Start("r3", new ReleaseContext { Cost = 20000m }).Snapshot, new ReleaseSubmitCommand(new ProcessEnvelope("r3", Actors.Engineer, "ana")));
                Assert.Contains("01JAD0000000000000000000N3", costly.Snapshot.Approvals[Approve].Needed);

                Assert.False(MemoLifecycleDefinition.Chart.RecordsActs);
                var memos = new MemoLifecycleMachine(new MemoLifecycleHandlers(), host, host, host);
                var memo = memos.Approve(memos.Start("m1", new MemoLifecycleContext()).Snapshot, new MemoLifecycleApproveCommand(new ProcessEnvelope("m1", Actors.Engineer, "ben")));
                Assert.Equal<string>(["Closed"], memo.States);
                Assert.Empty(memo.Snapshot.Acts);
            }
        }
        """;

    private static async Task BuildAndRunScenarioTestsAsync(PackRepo repo, string folder, string host, int total)
    {
        var pipeline = repo.Read("out/Dispatch/Pipeline.cs");
        const string Stub = "throw new NotImplementedException(\"Map the caller's principal to the envelope's actor in Pipeline.cs (Authorize).\")";
        Assert.Contains(Stub, pipeline, StringComparison.Ordinal);
        repo.Write("out/Dispatch/Pipeline.cs", pipeline.Replace(Stub, "true", StringComparison.Ordinal));

        var project = Path.Combine(repo.Repo.Root, folder);
        WriteProject(project, repo.PathOf("out"), program: null, web: true);
        var csproj = Path.Combine(project, "RoundTrip.csproj");
        File.WriteAllText(csproj, File.ReadAllText(csproj)
            .Replace("<OutputType>Library</OutputType>", "<OutputType>Exe</OutputType>\n    <IsTestProject>true</IsTestProject>", StringComparison.Ordinal)
            .Replace("<Compile Include=\"Program.cs\" Condition=\"Exists('Program.cs')\" />",
                "<Compile Include=\"Host/*.cs\" />\n    <PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(project, "Host"));
        File.WriteAllText(Path.Combine(project, "Host", "ScenarioHost.cs"), host);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), "-noColor"], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains($"Total: {total}, Errors: 0, Failed: 0,", StringComparison.Ordinal), run.Output);
    }

    // The test host of the freezing case: the dispatcher over the lifecycle, with ScenarioFrozen in place of the FrozenState stub (and,
    // with rights, ScenarioHoldings: $Held), and the checks the scenario can't make: the stub answers "not frozen", a frozen flag the
    // request gives decides, and one refused attribute refuses.
    private const string FreezingScenarioHost = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Dispatch;
        using App.Model.Processes;
        using App.Model.Runtime;
        using App.Model.Staff;
        using Xunit;

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher) => Dispatcher = dispatcher;

            public Dispatcher Dispatcher { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var records = new EmployeeRecordCommandHandler(new EmployeeRecordMachine(new EmployeeRecordHandlers(), host, host, host),
                    new EmployeeRecordStore(), $Heldnew ScenarioFrozen());
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(records), new Pipeline(new InMemoryProcessOutbox()))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class FreezingTests
        {
            [Fact]
            public void The_unwritten_hook_answers_not_frozen_and_a_given_flag_decides()
            {
                Assert.False(new FrozenState().IsFrozen(new FrozenQuery { Process = "EmployeeRecord" }));
                var edit = new RightRequest { Verb = Verb.Modify, Lifecycle = "EmployeeRecord", Subject = typeof(Employee), Actor = Actors.HrStaff, Attributes = ["name"] };
                Assert.True(Rights.Check(edit).Allowed);
                var frozen = Rights.Check(edit with { Frozen = true });
                Assert.Equal((false, Refusals.Frozen, (string?)null), (frozen.Allowed, frozen.Refusal, frozen.RefusedBy));
                var both = Rights.Check(edit with { Actor = Actors.Clerk, Attributes = ["name", "salary"] });
                Assert.Equal((Refusals.Right, "01JFZ000000000000000000Y01"), (both.Refusal, both.RefusedBy));
                Assert.Equal(Refusals.Right, Rights.Check(edit with { Actor = Actors.Clerk, Attributes = null }).Refusal);
                Assert.True(Rights.Check(edit with { Actor = Actors.HrStaff, Attributes = ["salary"] }).Allowed);
                var owner = edit with { Verb = Verb.ChangeOwner, Attributes = null };
                Assert.True(Rights.Check(owner).Allowed);
                Assert.Equal(Refusals.Right, Rights.Check(owner with { Frozen = true }).Refusal);
            }
        }
        """;

    [Fact]
    public async Task A_lifecycle_that_governs_only_some_subjects_is_asked_only_for_its_objects()
    {
        // rights-design.md decision 10: Filing governs Alpha and Beta, Guard only Alpha and denies by default. For a Beta object the engine
        // doesn't ask Guard and allows edit; the team's StatesInGuard answers null there, so the generated check doesn't ask it either. For an
        // Alpha object Guard's default refuses.
        using var repo = PackRepo.SeveralSubjects();
        await repo.GenerateCleanlyAsync();
        const string Stub = "private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInGuard(StatechartCall<FilingContext> call) =>\n        throw new NotImplementedException(\"The states of the object in lifecycle Guard are not read yet.\");";
        var handlers = repo.Read("out/Processes/Filing/FilingHandlers.cs");
        Assert.Contains(Stub, handlers, StringComparison.Ordinal);
        repo.Write("out/Processes/Filing/FilingHandlers.cs", handlers.Replace(Stub,
            "private partial global::System.Collections.Generic.IReadOnlyList<string>? StatesInGuard(StatechartCall<FilingContext> call) => GuardStates;\n\n    internal static global::System.Collections.Generic.IReadOnlyList<string>? GuardStates { get; set; }",
            StringComparison.Ordinal));

        var project = Path.Combine(repo.Repo.Root, "subjects");
        WriteProject(project, repo.PathOf("out"), program: """
            using System;
            using App.Model.Docs;
            using App.Model.Processes;
            using App.Model.Runtime;

            var machine = new FilingMachine(new FilingHandlers(), new ManualClock(DateTimeOffset.UnixEpoch));
            var start = machine.Start("f-1").Snapshot;
            var edit = new FilingEditCommand(new ProcessEnvelope("f-1", Actors.Clerk));
            FilingHandlers.GuardStates = null; // a Beta object: Guard doesn't govern it
            var beta = machine.Edit(start, edit);
            FilingHandlers.GuardStates = []; // an Alpha object, in Guard's initial state
            var alpha = machine.Edit(start, edit);
            Console.WriteLine($"beta {beta.Accepted}; alpha {alpha.Accepted} {alpha.Refusal} {alpha.RefusedBy == GuardDefinition.ProcessId}");

            """);
        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(1));
        Assert.True(run.ExitCode == 0, run.Output);
        Assert.Contains("beta True; alpha False default True", run.Output, StringComparison.Ordinal);
    }

    // The test host of the rights case: the dispatcher over both lifecycles, with ScenarioHoldings registered in place of the
    // CallerHoldings stub, and the checks the scenario can't make yet: rights before guards, at signatures and outside a run.
    private const string RightsScenarioHost = """
        using System;
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Dispatch;
        using App.Model.Docs;
        using App.Model.Processes;
        using App.Model.Runtime;
        using Xunit;

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher) => Dispatcher = dispatcher;

            public Dispatcher Dispatcher { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var documents = new DocumentLifecycleCommandHandler(new DocumentLifecycleMachine(new DocumentLifecycleHandlers(), host, host, host),
                    new DocumentLifecycleStore(), new ScenarioHoldings());
                var records = new RecordsCommandHandler(new RecordsMachine(new RecordsHandlers(), host, host, host), new RecordsStore(), new ScenarioHoldings());
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(documents, records), new Pipeline(new InMemoryProcessOutbox()))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class RightsTests
        {
            [Fact]
            public void Rights_refuse_before_guards_at_signatures_and_outside_a_run()
            {
                var machine = new DocumentLifecycleMachine(new DocumentLifecycleHandlers(), new ManualClock(DateTimeOffset.UnixEpoch));
                var start = machine.Start("d-1").Snapshot;
                DocumentLifecycleEditCommand Edit(string who) => new(new ProcessEnvelope("d-1", Actors.Author, Signer: who));

                // Carl may not change the secret draft: the named right denies, and a deny wins over AuthorsEditDrafts.
                var carl = machine.Edit(start, Edit("carl"), [Actors.Author]);
                Assert.Equal((Refusals.Right, "01JRT000000000000000000R01"), (carl.Refusal, carl.RefusedBy));

                // Bea is the owner: the named right says nothing for her, in either lifecycle, and AuthorsEditDrafts allows.
                Assert.True(machine.Edit(start, Edit("bea")).Accepted);

                // Archived in Records, the document may not be changed: the records lifecycle answers too, from the StatesIn hook.
                DocumentLifecycleHandlers.RecordsStates = ["Archived"];
                var archived = machine.Edit(start, Edit("bea"));
                DocumentLifecycleHandlers.RecordsStates = [];
                Assert.Equal((Refusals.Right, "01JRT000000000000000000Y02"), (archived.Refusal, archived.RefusedBy));

                // Dan signs as an approver and holds Intern: NoInternSigns refuses at the signature. With no held actors (the stub) he is
                // checked as Approver only, and signs; a request body can't name held actors.
                var review = machine.Submit(start, new DocumentLifecycleSubmitCommand(new ProcessEnvelope("d-1", Actors.Author, Signer: "carl"))).Snapshot;
                var approve = new DocumentLifecycleApproveCommand(new ProcessEnvelope("d-1", Actors.Approver, Signer: "dan"));
                var dan = machine.Approve(review, approve, [Actors.Approver, Actors.Intern]);
                Assert.Equal((Refusals.Right, "01JRT000000000000000000X04"), (dan.Refusal, dan.RefusedBy));
                Assert.True(machine.Approve(review, approve).Accepted);

                // Outside a run: in Released, delete is denied by ReleasedIsReadOnly, read is allowed, and with the document not secret, a
                // change of owner is denied by the lifecycle's default.
                var released = new RightRequest
                {
                    Lifecycle = "DocumentLifecycle",
                    Subject = typeof(Document),
                    Actor = Actors.Author,
                    States = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal) { ["DocumentLifecycle"] = ["Released"] },
                };
                Assert.Equal((Refusals.Right, "01JRT000000000000000000X03"), (Rights.CanDelete(released).Refusal, Rights.CanDelete(released).RefusedBy));
                Assert.True(Rights.CanRead(released).Allowed);
                RightConditions.Secret = false;
                Assert.Equal((Refusals.Default, DocumentLifecycleDefinition.ProcessId), (Rights.CanChangeOwner(released).Refusal, Rights.CanChangeOwner(released).RefusedBy));
                RightConditions.Secret = true;
            }
        }

        """;

    [Fact]
    public async Task A_team_file_that_already_implements_an_action_still_builds_when_the_action_gains_sets()
    {
        // object-effects-design.md decision 12: the companion is written once. A team that implemented recordSubmission before it set data
        // keeps its method; the generated half only declares it and adds the Record hook, which is the one method the team adds.
        using var repo = PackRepo.ObjectEffects();
        var processPath = repo.PathOf(".maquettiste/model/processes/request-lifecycle.json");
        var original = File.ReadAllText(processPath);
        var process = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        process["actions"]!.AsArray().Single(a => (string)a!["name"]! == "recordSubmission")!.AsObject().Remove("sets");
        File.WriteAllText(processPath, process.ToJsonString());
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var teamPath = Path.Combine(repo.PathOf("out"), "Processes", "RequestLifecycle", "RequestLifecycleHandlers.cs");
        var stub = """
                private partial RequestLifecycleContext ActionRecordSubmission(StatechartCall<RequestLifecycleContext> call) =>
                    throw new NotImplementedException("Action recordSubmission of process RequestLifecycle is not implemented yet.");
            """;
        var team = File.ReadAllText(teamPath);
        Assert.Contains(stub, team, StringComparison.Ordinal);
        Assert.DoesNotContain("RecordRecordSubmission", team, StringComparison.Ordinal);
        team = team.Replace(stub, """
                private partial RequestLifecycleContext ActionRecordSubmission(StatechartCall<RequestLifecycleContext> call) => call.Context;
            """, StringComparison.Ordinal);
        File.WriteAllText(teamPath, team);

        File.WriteAllText(processPath, original);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        Assert.Equal(team, File.ReadAllText(teamPath));
        var handlers = File.ReadAllText(Path.Combine(repo.PathOf("out"), "Processes", "RequestLifecycle", "RequestLifecycleHandlers.g.cs"));
        Assert.Contains("private partial RequestLifecycleContext ActionRecordSubmission(", handlers, StringComparison.Ordinal);
        Assert.Contains("private partial void RecordRecordSubmission(", handlers, StringComparison.Ordinal);

        // The build names the missing Record method; the team adds it, and its own action stays as written.
        var last = team.LastIndexOf('}');
        File.WriteAllText(teamPath, team[..last] + """

                private partial void RecordRecordSubmission(StatechartCall<RequestLifecycleContext> call)
                {
                }
            }

            """);
        var project = Path.Combine(repo.Repo.Root, "effects");
        WriteProject(project, repo.PathOf("out"), program: null, web: true);
        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Actors_named_like_the_members_of_the_actors_class_compile()
    {
        // An actor named Accepts or All would clash with the class's own members: only those constants get "Actor" appended, the
        // others keep their names, and the checks still read the names envelopes carry.
        using var repo = PackRepo.Actors();
        repo.Write(".maquettiste/model/actors/accepts.json",
            """{ "kind": "actor", "id": "01JAC000000000000000000K91", "name": "Accepts", "type": "person", "memberOf": ["01JAC000000000000000000K04"] }""");
        repo.Write(".maquettiste/model/actors/all.json", """{ "kind": "actor", "id": "01JAC000000000000000000K92", "name": "All", "type": "role" }""");
        await repo.GenerateCleanlyAsync();
        var actors = File.ReadAllText(Path.Combine(repo.PathOf("out"), "Processes", "Actors.cs"));
        Assert.Contains("public const string AcceptsActor = \"Accepts\";", actors, StringComparison.Ordinal);
        Assert.Contains("public const string AllActor = \"All\";", actors, StringComparison.Ordinal);
        Assert.Contains("public const string Pat = \"Pat\";", actors, StringComparison.Ordinal);
        Directory.Delete(repo.PathOf("out/Tests"), recursive: true);
        var project = Path.Combine(repo.Repo.Root, "clash");
        WriteProject(project, repo.PathOf("out"), """
            using System;
            using System.Linq;
            using App.Model.Processes;

            Check(Actors.AcceptsActor == "Accepts" && Actors.AllActor == "All", "the names");
            Check(Actors.Accepts(Actors.Approver, Actors.AcceptsActor), "Accepts acts for Approver through Seniors");
            Check(Actors.All.Contains(Actors.AllActor) && Actors.TypeOf(Actors.AllActor) == ActorType.Role, "All is a role");
            Console.WriteLine("clash ok");

            static void Check(bool ok, string what)
            {
                if (!ok)
                    throw new InvalidOperationException("Failed: " + what);
            }

            """, web: true);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("clash ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task Actors_taken_from_the_object_ask_the_handlers_and_the_scenario_tests_pass()
    {
        // actors-design.md decisions 15 and 27: the generated interpreter accepts an actor taken from the object only when the
        // process's Handlers says the caller is that actor, and the generated scenario test acts as RStep.ActsAs with the participant's
        // identity as signer. The team's half here answers as the scenario's objects say: the order's owner is bea, its account manager
        // max, and Ann (identity 7) an approver on its project. The project builds with warnings as errors, and both tests pass.
        using var repo = PackRepo.Actors();
        await repo.GenerateCleanlyAsync();
        var handlers = repo.Read("out/Processes/OrderFlow/OrderFlowHandlers.cs");
        foreach (var (actor, who) in new[] { ("AccountManager", "max"), ("Owner", "bea"), ("ProjectApprover", "7") })
        {
            var stub = $"private partial bool Is{actor}(ProcessSnapshot<OrderFlowContext> snapshot, ProcessEnvelope envelope) => false;";
            Assert.Contains(stub, handlers, StringComparison.Ordinal);
            handlers = handlers.Replace(stub, stub.Replace("=> false;", $"=> envelope.Signer == \"{who}\";", StringComparison.Ordinal), StringComparison.Ordinal);
        }

        repo.Write("out/Processes/OrderFlow/OrderFlowHandlers.cs", handlers);
        const string Authorize = "throw new NotImplementedException(\"Map the caller's principal to the envelope's actor in Pipeline.cs (Authorize).\")";
        var pipeline = repo.Read("out/Dispatch/Pipeline.cs");
        Assert.Contains(Authorize, pipeline, StringComparison.Ordinal);
        repo.Write("out/Dispatch/Pipeline.cs", pipeline.Replace(Authorize, "true", StringComparison.Ordinal));

        var project = Path.Combine(repo.Repo.Root, "scenarios");
        WriteProject(project, repo.PathOf("out"), program: null, web: true);
        var csproj = Path.Combine(project, "RoundTrip.csproj");
        File.WriteAllText(csproj, File.ReadAllText(csproj)
            .Replace("<OutputType>Library</OutputType>", "<OutputType>Exe</OutputType>\n    <IsTestProject>true</IsTestProject>", StringComparison.Ordinal)
            .Replace("<Compile Include=\"Program.cs\" Condition=\"Exists('Program.cs')\" />",
                "<Compile Include=\"Host/*.cs\" />\n    <PackageReference Include=\"xunit.v3\" Version=\"3.2.2\" />", StringComparison.Ordinal));
        Directory.CreateDirectory(Path.Combine(project, "Host"));
        File.WriteAllText(Path.Combine(project, "Host", "ScenarioHost.cs"), ActorsScenarioHost);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), "-noColor"], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("Total: 2, Errors: 0, Failed: 0,", StringComparison.Ordinal), run.Output);
    }

    // The test host of the actors case's generated scenario tests: the dispatcher over both processes with the in-memory stores the
    // companions start as, and a check that the owner's actor is refused to anyone else and to a behaviour that resolves no one.
    private const string ActorsScenarioHost = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using App.Model.Dispatch;
        using App.Model.Ops;
        using App.Model.Processes;
        using App.Model.Runtime;
        using Xunit;

        namespace App.Model;

        internal sealed class ScenarioHost : IAsyncDisposable
        {
            private ScenarioHost(Dispatcher dispatcher) => Dispatcher = dispatcher;

            public Dispatcher Dispatcher { get; }

            public static Task<ScenarioHost> StartAsync(ManualClock clock, CancellationToken cancellationToken)
            {
                var host = new ProcessHost(clock);
                var orders = new OrderFlowCommandHandler(new OrderFlowMachine(new OrderFlowHandlers(), host, host, host), new OrderFlowStore());
                var requests = new RequestFlowCommandHandler(new RequestFlowMachine(new RequestFlowHandlers(), host, host, host), new RequestFlowStore());
                return Task.FromResult(new ScenarioHost(new Dispatcher(new HandlerRegistry(orders, requests), new Pipeline(new InMemoryProcessOutbox()))));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }

        public sealed class ResolverTests
        {
            [Fact]
            public void Only_the_resolver_lets_a_caller_act_as_an_actor_taken_from_the_object()
            {
                var clock = new ManualClock(DateTimeOffset.UnixEpoch);
                var machine = new OrderFlowMachine(new OrderFlowHandlers(), clock);
                var start = machine.Start("o-1").Snapshot;
                var refused = machine.Submit(start, new OrderFlowSubmitCommand(new ProcessEnvelope("o-1", Actors.Owner, Signer: "max")));
                Assert.False(refused.Accepted);
                Assert.Equal(Refusals.Actor, refused.Refusal);
                Assert.True(machine.Submit(start, new OrderFlowSubmitCommand(new ProcessEnvelope("o-1", Actors.Owner, Signer: "bea"))).Accepted);

                // A behaviour that is not a resolver resolves no one, so the owner's actor is refused even to bea.
                var chart = new Statechart<OrderFlowContext>(OrderFlowDefinition.Chart, new Plain(), clock);
                var plain = chart.Send(chart.Start("o-2", new OrderFlowContext()).Snapshot, "submit", new ProcessEnvelope("o-2", Actors.Owner, Signer: "bea"));
                Assert.Equal(Refusals.Actor, plain.Refusal);
            }

            private sealed class Plain : IStatechartBehaviour<OrderFlowContext>
            {
                public bool Guard(string guard, StatechartCall<OrderFlowContext> call) => throw new InvalidOperationException(guard);

                public OrderFlowContext Action(string action, StatechartCall<OrderFlowContext> call) => throw new InvalidOperationException(action);

                public System.Collections.Generic.IReadOnlyList<string> Changed(OrderFlowContext before, OrderFlowContext after) => [];
            }
        }

        """;

    private const string ActorsProgram = """
        using System;
        using System.Linq;
        using App.Model.Ops;
        using App.Model.Processes;
        using App.Model.Runtime;

        // Membership at any depth: Pat is in Seniors, Seniors in Approver, Approver in Plant; the system actor is in Scheduler.
        Check(Actors.Allows([Actors.Plant], Actors.Pat), "Pat acts for Plant");
        Check(Actors.Accepts(Actors.Approver, Actors.Seniors), "Seniors act for Approver");
        Check(!Actors.Allows([Actors.Seniors], Actors.Approver), "a holder does not act for its members");
        Check(!Actors.Allows([Actors.Plant, Actors.Scheduler], Actors.Clerk), "Clerk is a member of nothing");
        Check(Actors.Accepts(Actors.Scheduler, Actors.Automatic), "the system actor holds Scheduler");
        Check(Actors.TypeOf(Actors.Automatic) == ActorType.System, "System");
        Check(Actors.TypeOf(Actors.Plant) == ActorType.Organization, "Organization");
        Check(Actors.TypeOf(Actors.Seniors) == ActorType.Group, "Group");

        // The interpreter follows membership for a human task, a gate's signers and its required actor, and an event.
        var clock = new ManualClock(DateTimeOffset.UnixEpoch);
        var machine = new RequestFlowMachine(new RequestFlowHandlers(), clock);
        var step = machine.Submit(machine.Start("r-1").Snapshot, new RequestFlowSubmitCommand(new ProcessEnvelope("r-1", Actors.Clerk)));
        Check(step.Accepted && step.States.SequenceEqual([RequestFlowStates.Reviewing]), "submit");
        var refused = machine.Complete(step.Snapshot, new RequestFlowInvokeResultControl(new ProcessEnvelope("r-1", Actors.Clerk), "review"));
        Check(!refused.Accepted && refused.Refusal == Refusals.Actor, "Clerk may not review");
        step = machine.Complete(step.Snapshot, new RequestFlowInvokeResultControl(new ProcessEnvelope("r-1", Actors.Pat), "review"));
        Check(step.Accepted && step.States.SequenceEqual([RequestFlowStates.Pending]), "Pat reviews as an Approver");
        var approved = machine.Approve(step.Snapshot, new RequestFlowApproveCommand(new ProcessEnvelope("r-1", Actors.Pat, Signer: "pat", Meaning: "approved")));
        Check(approved.Accepted && approved.States.SequenceEqual([RequestFlowStates.Approved]), "Pat signs for Plant and counts as the required Approver");

        // A second request waits in Pending until its timer escalates it; the system actor may then cancel it as a Scheduler.
        step = machine.Submit(machine.Start("r-2").Snapshot, new RequestFlowSubmitCommand(new ProcessEnvelope("r-2", Actors.Clerk)));
        step = machine.Complete(step.Snapshot, new RequestFlowInvokeResultControl(new ProcessEnvelope("r-2", Actors.Seniors), "review"));
        Check(step.Accepted && step.States.SequenceEqual([RequestFlowStates.Pending]), "Seniors review as an Approver");
        clock.Advance(TimeSpan.FromDays(2));
        var escalated = machine.Tick(step.Snapshot);
        Check(escalated.States.SequenceEqual([RequestFlowStates.Escalated]), "the timer");
        var cancelled = machine.Cancel(escalated.Snapshot, new RequestFlowCancelCommand(new ProcessEnvelope("r-2", Actors.Automatic)));
        Check(cancelled.Accepted && cancelled.States.SequenceEqual([RequestFlowStates.Cancelled]), "the system actor cancels as Scheduler");
        Console.WriteLine("actors ok");

        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Failed: " + what);
        }

        """;

    private const string LifecycleCodesProgram = """
        using System;
        using System.Linq;
        using App.Model.Orders;
        using App.Model.Runtime;
        using App.Model.Sales;

        // String codes: a coded state, nested states taking their coded ancestor's code, a state without one.
        Check(OrderFlowStateCodes.ToCode([OrderFlowStates.Draft]) == "DRF", "Draft");
        Check(OrderFlowStateCodes.ToCode([OrderFlowStates.ReviewApproving]) == OrderFlowStateCodes.Review, "Review.Approving takes Review's code");
        Check(OrderFlowStateCodes.ToCode([OrderFlowStates.OnHold]) is null, "OnHold has no code");
        Check(OrderFlowStateCodes.ToCode([]) is null, "no active state");

        // FromCode gives the leaves a snapshot holds: REV enters Review's initial state, so the generated runtime restores it, the code
        // comes back, and check moves on to Approving.
        Check(OrderFlowStateCodes.FromCode("REV").SequenceEqual([OrderFlowStates.ReviewChecking]), "REV enters Review.Checking");
        var orders = new OrderFlowMachine(new OrderFlowHandlers(), new ManualClock(DateTimeOffset.UnixEpoch));
        foreach (var code in new[] { OrderFlowStateCodes.Draft, OrderFlowStateCodes.Review, OrderFlowStateCodes.Done })
        {
            var snapshot = orders.Start("o-" + code).Snapshot;
            snapshot.States = [.. OrderFlowStateCodes.FromCode(code)];
            Check(OrderFlowStateCodes.ToCode(orders.Restore(snapshot).States) == code, "round trip " + code);
        }

        var review = orders.Start("o-check").Snapshot;
        review.States = [.. OrderFlowStateCodes.FromCode(OrderFlowStateCodes.Review)];
        var checkedStep = orders.Check(orders.Restore(review), new OrderFlowCheckCommand(new ProcessEnvelope("o-check")));
        Check(checkedStep.Accepted && checkedStep.States.SequenceEqual([OrderFlowStates.ReviewApproving]), "check after restoring REV");

        try
        {
            OrderFlowStateCodes.FromCode("XYZ");
            Check(false, "XYZ is no state's code");
        }
        catch (ArgumentOutOfRangeException)
        {
        }

        // Integer codes on nested states under a compound state without one, each restored through the runtime.
        Check(TicketFlowStateCodes.Assigned == 20L, "Assigned");
        Check(TicketFlowStateCodes.ToCode([TicketFlowStates.OpenAssigned]) == 20L, "Open.Assigned");
        Check(TicketFlowStateCodes.ToCode([TicketFlowStates.Open]) is null, "Open has no code");
        Check(TicketFlowStateCodes.FromCode(10).SequenceEqual([TicketFlowStates.OpenNew]), "10");
        var tickets = new TicketFlowMachine(new TicketFlowHandlers(), new ManualClock(DateTimeOffset.UnixEpoch));
        foreach (var code in new[] { TicketFlowStateCodes.New, TicketFlowStateCodes.Assigned, TicketFlowStateCodes.Resolved, TicketFlowStateCodes.Closed })
        {
            var snapshot = tickets.Start("t-" + code).Snapshot;
            snapshot.States = [.. TicketFlowStateCodes.FromCode(code)];
            Check(TicketFlowStateCodes.ToCode(tickets.Restore(snapshot).States) == code, "round trip " + code);
        }

        // The moves: within one coded state, into a state without a code, into a history state inside Review, a timer; into a choice,
        // into a compound state whose default has a code, eventless, from Open (no code) once per coded state inside it, targetless.
        Check(OrderFlowStateCodes.Moves.Count == 6, "six moves");
        Check(OrderFlowStateCodes.Moves[1] == ("REV", "REV", "event", "check"), "within Review");
        Check(OrderFlowStateCodes.Moves[3] == ("REV", null, "event", "hold"), "into OnHold");
        Check(OrderFlowStateCodes.Moves[4] == (null, "REV", "event", "resume"), "into history inside Review");
        Check(OrderFlowStateCodes.Moves[5] == (null, "DON", "after", null), "a timer");
        Check(TicketFlowStateCodes.Moves.Count == 8, "eight moves");
        Check(TicketFlowStateCodes.Moves[1] == (20L, null, "event", "resolve"), "into a choice");
        Check(TicketFlowStateCodes.Moves[2] == (null, 10L, "always", null), "into Open, entering New");
        Check(TicketFlowStateCodes.Moves[5] == (10L, 90L, "event", "cancel"), "cancel from Open.New");
        Check(TicketFlowStateCodes.Moves[6] == (20L, 90L, "event", "cancel"), "cancel from Open.Assigned");
        Check(TicketFlowStateCodes.Moves[7] == (30L, 30L, "event", "note"), "targetless");

        // Each subject's status; the two entities named Item are qualified by package.
        Check(OrderFlowStates.ToOrderStatus([OrderFlowStates.ReviewChecking]) == OrderStatus.Review, "Order");
        Check(OrderFlowStates.ToOrdersItemStatus([OrderFlowStates.Done]) == OrderStatus.Done, "Orders.Item");
        Check(OrderFlowStates.ToSalesItemStatus([OrderFlowStates.OnHold]) == ItemPhase.OnHold, "Sales.Item");
        Check(OrderFlowStates.Parse(OrderFlowStates.ReviewResume) == OrderFlowState.ReviewResume, "Parse");
        Console.WriteLine("codes ok");

        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Failed: " + what);
        }

        """;

    [Theory]
    [InlineData("Orders")]
    [InlineData("Flows")]
    public async Task Lifecycle_statuses_name_two_enums_called_Status_in_full(string package)
    {
        // Two subjects' enums are both named Status, in packages Orders and Sales. With the lifecycle in Orders, its own Status must not
        // stand in for Sales.Status; in a third package, Flows, the two must not be ambiguous (CS0104). Each To<Entity>Status names its
        // enum in full, so the output builds and returns the right enum.
        using var repo = PackRepo.LifecycleCodes();
        repo.EditJson(".maquettiste/model/enums/order-status.json", e => e["name"] = "Status");
        repo.EditJson(".maquettiste/model/enums/item-phase.json", e => e["name"] = "Status");
        if (package == "Flows")
        {
            repo.Write(".maquettiste/model/packages/flows.json", """
                {
                  "kind": "package",
                  "id": "01JMC000000000000000PKG003",
                  "name": "Flows"
                }

                """);
            repo.EditJson(".maquettiste/model/processes/order-flow.json", p => p["package"] = "01JMC000000000000000PKG003");
        }

        await repo.GenerateCleanlyAsync();
        var project = Path.Combine(repo.Repo.Root, "statuses");
        WriteProject(project, repo.PathOf("out"), StatusesProgram.Replace("App.Model.Flows", "App.Model." + package, StringComparison.Ordinal), web: true);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("statuses ok", StringComparison.Ordinal), run.Output);
    }

    private const string StatusesProgram = """
        using System;
        using App.Model.Flows;

        Check(OrderFlowStates.ToOrderStatus([OrderFlowStates.ReviewChecking]) == App.Model.Orders.Status.Review, "Order");
        Check(OrderFlowStates.ToOrdersItemStatus([OrderFlowStates.Done]) == App.Model.Orders.Status.Done, "Orders.Item");
        Check(OrderFlowStates.ToSalesItemStatus([OrderFlowStates.OnHold]) == App.Model.Sales.Status.OnHold, "Sales.Item");
        Console.WriteLine("statuses ok");

        static void Check(bool ok, string what)
        {
            if (!ok)
                throw new InvalidOperationException("Failed: " + what);
        }

        """;

    [Fact]
    public async Task The_seed_data_loader_applies_the_seed_data_files_to_sqlite_once_and_converging()
    {
        // The seed-data pack's CSVs for the SQLite database, loaded by csharp-dapper's SeedDataLoader (seedLoader on).
        using var repo = TableSeedSqlTests.DataRepo("csv");
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["csharp-dapper"]!["parameters"] = new System.Text.Json.Nodes.JsonObject { ["seedLoader"] = true });
        await repo.GenerateCleanlyAsync();
        Assert.True(File.Exists(repo.PathOf("src/Generated/SeedDataLoader.g.cs")));
        var project = Path.Combine(repo.Repo.Root, "loader");
        WriteProject(project, repo.PathOf("src/Generated"), SeedLoaderProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet,
            [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), repo.PathOf("db/local/schema.sql"), repo.PathOf("data/local")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("seed loader ok", StringComparison.Ordinal), run.Output);
    }

    private const string SeedLoaderProgram = """
        using System;
        using System.IO;
        using System.Linq;
        using Dapper;
        using Microsoft.Data.Sqlite;
        using App.Model;

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(args[0]);
            command.ExecuteNonQuery();
        }

        void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Seed loader failed: " + what);
        }

        var loader = new SeedDataLoader(connection, SeedDialect.Sqlite, args[1]);
        Check(loader.SeedHash.Length == 64, "the seed hash");
        Check(await loader.LoadAsync() >= 2, "the shared rows are written");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM loc_currencies") == 2, "the shared rows: two currencies");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM loc_price_lists") == 0, "dev rows wait for dev");
        await loader.LoadAsync("dev");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM loc_price_lists") == 2, "the dev rows");
        await connection.ExecuteAsync("UPDATE loc_currencies SET name = 'Changed' WHERE code = 'EUR'");
        await connection.ExecuteAsync("INSERT INTO loc_currencies (code, name) VALUES ('XXX', 'Stray')");
        await connection.ExecuteAsync("UPDATE loc_price_lists SET label = 'Mine' WHERE id = 1");
        await loader.LoadAsync("dev");
        var names = (await connection.QueryAsync<string>("SELECT code || ':' || name FROM loc_currencies ORDER BY code")).ToList();
        Check(names.SequenceEqual(["EUR:Euro", "USD:US dollar"]), "converge restores the name and deletes the stray row: " + string.Join(",", names));
        Check(await connection.ExecuteScalarAsync<string>("SELECT label FROM loc_price_lists WHERE id = 1") == "Mine", "once leaves an edited row alone");
        Console.WriteLine("seed loader ok");
        """;

    private const string BindingRoundTripProgram = """
        using System;
        using System.IO;
        using System.Linq;
        using Dapper;
        using Microsoft.Data.Sqlite;
        using Shop.Model;
        using Shop.Model.Shop;

        // One to one with an identity key, a soft delete, two entities sharing a table through a constant, and a read-only entity
        // over a query, against an in-memory SQLite database built from the sql-ddl schema.
        DapperTypeHandlers.Register();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(args[0]);
            command.ExecuteNonQuery();
        }

        void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Round trip failed: " + what);
        }

        var products = new ProductRepository(connection);
        var product = new Product { Sku = "SKU-1", Name = "Widget", Price = 9.99m };
        await products.InsertAsync(product);
        Check(product.Id > 0, "identity key returned by the insert");
        product.Price = 12.5m;
        Check(await products.UpdateAsync(product), "update by key");
        Check((await products.GetAsync(product.Id))!.Price == 12.5m, "get after update");
        Check((await products.ListAsync()).Count == 1, "list");

        var customers = new CustomerRepository(connection);
        var ada = new Customer { Name = "Ada", Email = "ada@example.com" };
        var bob = new Customer { Name = "Bob" };
        await customers.InsertAsync(ada);
        await customers.InsertAsync(bob);
        Check(ada.Id != Guid.Empty, "uuid-v7 key assigned on insert");
        Check(await customers.DeleteAsync(bob.Id), "soft delete");
        Check(await customers.GetAsync(bob.Id) is null, "a soft-deleted row is not read");
        Check((await customers.ListAsync()).Select(c => c.Name).SequenceEqual(["Ada"]), "list leaves soft-deleted rows out");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM customers") == 2, "the soft-deleted row stays");

        var productNotes = new ProductNoteRepository(connection);
        var customerNotes = new CustomerNoteRepository(connection);
        var first = new ProductNote { Sku = "SKU-1", Body = "Popular" };
        var second = new CustomerNote { CustomerId = ada.Id.ToString(), Body = "Pays on time" };
        await productNotes.InsertAsync(first);
        await customerNotes.InsertAsync(second);
        Check(await connection.ExecuteScalarAsync<string>("SELECT entity_type FROM notes WHERE id = @id", new { id = first.Id }) == "product", "the constant is set on insert");
        Check((await productNotes.ListAsync()).Single().Body == "Popular", "a shared table reads its own rows only");
        Check(await productNotes.GetAsync(second.Id) is null, "the constant filters reads by key");
        Check((await customerNotes.GetAsync(second.Id))!.CreatedAt is not null, "the database fills created_at");
        Check(!await productNotes.DeleteAsync(second.Id), "the constant filters deletes");
        Check(await customerNotes.DeleteAsync(second.Id), "delete by key");

        var summaries = new CustomerSummaryRepository(connection);
        var active = await summaries.ListAsync("%");
        Check(active.Select(s => s.DisplayName).SequenceEqual(["Ada"]), "a read-only entity over a query");
        Check((await summaries.GetAsync("A%", ada.Id))!.Email == "ada@example.com", "get by key over a query");
        Console.WriteLine("bindings round trip ok");
        """;

    internal static void WriteProject(string folder, string generated, string? program, bool web = false, string? second = null)
    {
        Directory.CreateDirectory(folder);
        var framework = web ? """    <FrameworkReference Include="Microsoft.AspNetCore.App" />""" : "";
        var secondFolder = second is null ? "" : $"""    <Compile Include="{second}/**/*.cs" />""";
        var sqlite = program is null ? "" : $"""    <PackageReference Include="Microsoft.Data.Sqlite" Version="{SqliteVersion}" />""";
        File.WriteAllText(Path.Combine(folder, "RoundTrip.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>{(program is null ? "Library" : "Exe")}</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <NuGetAudit>false</NuGetAudit>
                <UseSharedCompilation>false</UseSharedCompilation>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{generated}/**/*.cs" />
            {secondFolder}
            {framework}
                <Compile Include="Program.cs" Condition="Exists('Program.cs')" />
                <PackageReference Include="Dapper" Version="{DapperVersion}" />
            {sqlite}
              </ItemGroup>
            </Project>

            """);
        // Keep the repo's global.json, Directory.Build.props and central package versions out of the temporary project.
        File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(folder, "Directory.Packages.props"), "<Project />\n");
        if (program is not null)
            File.WriteAllText(Path.Combine(folder, "Program.cs"), program);
    }

    internal static async Task BuildOrSkipAsync(string project)
    {
        var restore = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, ["restore", "RoundTrip.csproj"], project, TimeSpan.FromMinutes(5));
        if (restore.ExitCode != 0)
        {
            if (IsNuGetUnreachable(restore.Output))
                Assert.Skip("NuGet is unreachable and Dapper " + DapperVersion + " is not in the local package cache, so the generated C# was not compiled:\n" + restore.Output);
            Assert.Fail("dotnet restore failed:\n" + restore.Output);
        }

        var build = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, ["build", "RoundTrip.csproj", "--no-restore", "-nodeReuse:false"], project, TimeSpan.FromMinutes(5));
        Assert.True(build.ExitCode == 0, "The generated C# does not compile:\n" + build.Output);
    }

    private static bool IsNuGetUnreachable(string output) =>
        output.Contains("NU1301", StringComparison.Ordinal)
        || output.Contains("NU1801", StringComparison.Ordinal)
        || output.Contains("Unable to load the service index", StringComparison.Ordinal)
        || output.Contains("No such host", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase);

    private const string RoundTripProgram = """
        using System;
        using System.IO;
        using System.Linq;
        using Microsoft.Data.Sqlite;
        using App.Model;
        using App.Model.Billing;
        using App.Model.Billing.Catalog;

        // Builds an in-memory SQLite database from the sql-ddl schema and seed scripts, then drives every generated repository
        // operation: insert (with a database identity key, a generated uuid key and a generated ulid key), get, list, update and delete,
        // and a generated query with a list parameter, paging and a collection.
        DapperTypeHandlers.Register();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(args[0]) + "\n" + File.ReadAllText(args[1]);
            command.ExecuteNonQuery();
        }

        void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Round trip failed: " + what);
        }

        var customers = new CustomerRepository(connection);
        var customer = new Customer { Name = "Ada", Email = "ada@example.com", CustomerSince = new DateOnly(2024, 1, 2), CreatedAt = DateTimeOffset.UnixEpoch };
        await customers.InsertAsync(customer);
        Check(customer.Id != Guid.Empty, "uuid-v7 key assigned on insert");

        var products = new ProductRepository(connection);
        var product = new Product { Sku = "SKU-1", Name = "Widget", ListPrice = new Money { Amount = 9.99m, Currency = "EUR" } };
        await products.InsertAsync(product);

        var invoices = new InvoiceRepository(connection);
        var invoice = new Invoice
        {
            Number = "INV-1", IssuedOn = new DateOnly(2024, 3, 4), Total = new Money { Amount = 12.5m, Currency = "EUR" },
            Status = InvoiceStatus.Issued, Notes = "first", CreatedAt = DateTimeOffset.UnixEpoch, CustomerId = customer.Id,
        };
        await invoices.InsertAsync(invoice);
        var read = await invoices.GetAsync(invoice.Id);
        Check(read is not null, "get after insert");
        Check(read!.Total == invoice.Total && read.Status == InvoiceStatus.Issued && read.IssuedOn == invoice.IssuedOn, "values after insert");
        Check(read.CustomerId == customer.Id && read.Notes == "first", "foreign key and text after insert");

        var lines = new InvoiceLineRepository(connection);
        var line = new InvoiceLine { Quantity = 2, UnitPrice = new Money { Amount = 1.5m, Currency = "EUR" }, InvoiceId = invoice.Id, ProductId = product.Id };
        await lines.InsertAsync(line);
        Check(line.Id is { Length: 26 } && line.Id.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)), "ulid key assigned on insert");
        Check((await lines.GetAsync(line.Id))?.Quantity == 2, "invoice line");

        // The generated query: the customer's issued invoices, each with its lines from the second statement.
        var found = await new App.Model.Queries.LocalInvoicesByCustomerQuery(connection).ExecuteAsync(customer.Id, [1, 2]);
        Check(found.Count == 1 && found[0].Item.Id == invoice.Id && found[0].Item.Status == InvoiceStatus.Issued, "query rows");
        Check(found[0].Lines.Count == 1 && found[0].Lines[0].Id == line.Id && found[0].Lines[0].Quantity == 2, "query collection");
        Check(found[0].Item.CustomerId == customer.Id && found[0].Item.Total == invoice.Total, "query foreign key and value object members");
        Check(found[0].SameLines.Count == 1 && found[0].SameLines[0].Id == line.Id && found[0].SameLines[0].Quantity == 2, "query collection with two keys");
        Check((await new App.Model.Queries.LocalInvoicesByCustomerQuery(connection).ExecuteAsync(customer.Id, [3])).Count == 0, "query filter");

        read.Status = InvoiceStatus.Paid;
        read.Total = null;
        Check(await invoices.UpdateAsync(read), "update");
        var updated = await invoices.GetAsync(invoice.Id);
        Check(updated!.Status == InvoiceStatus.Paid && updated.Total is null, "values after update");

        var payments = new PaymentRepository(connection);
        var first = new Payment { Amount = new Money { Amount = 5m, Currency = "EUR" }, ReceivedAt = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch };
        var second = new Payment { Amount = new Money { Amount = 6m, Currency = "EUR" }, ReceivedAt = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch };
        await payments.InsertAsync(first);
        await payments.InsertAsync(second);
        Check(first.Id > 0 && second.Id > first.Id, "identity keys returned by insert");
        var page = await payments.ListAsync(skip: 1, take: 10);
        Check(page.Count == 1 && page[0].Id == second.Id && page[0].Amount.Amount == 6m, "list paging");

        var notes = new CreditNoteRepository(connection);
        var note = new CreditNote
        {
            Number = "CN-1", IssuedOn = new DateOnly(2024, 5, 6), Status = InvoiceStatus.Draft, CreatedAt = DateTimeOffset.UnixEpoch,
            CustomerId = customer.Id, Reason = "damaged",
        };
        await notes.InsertAsync(note);
        Check((await notes.GetAsync(note.Id))?.Reason == "damaged", "derived entity (table per hierarchy)");
        Check(await invoices.GetAsync(note.Id) is null && (await invoices.ListAsync()).All(i => i.Id != note.Id), "discriminator filter");

        Check(await lines.DeleteAsync(line.Id), "delete line");
        Check(await invoices.DeleteAsync(invoice.Id), "delete");
        Check(await invoices.GetAsync(invoice.Id) is null, "get after delete");
        Check(!await invoices.DeleteAsync(invoice.Id), "second delete");
        Check(BillingRepositories.All.Count == 8 && CatalogRepositories.All.Single().Implementation == typeof(ProductRepository), "registrations");
        Console.WriteLine("round trip ok");

        """;
}
