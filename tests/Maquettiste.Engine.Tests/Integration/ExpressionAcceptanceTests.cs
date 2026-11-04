using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using Maquettiste.Engine.Processes;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Integration;

/// <summary>
/// Acceptance for scope 3.8 (next-phase-3-design.md section 3.1, "Tests"): one check, written once as text, reaches the DDL, the
/// generated API code, the engine's own evaluation and a process guard, and a rename follows it. On a copy of the billing fixture with
/// the sql-ddl and csharp-dapper packs: <c>Invoice</c> is bound to a designed <c>invoices</c> table, gains an optional <c>dueOn</c> date
/// with its <c>due_on</c> column and field, and the check <c>dueAfterIssue</c> (<c>issuedOn &lt;= dueOn</c>, pointing at issuedOn)
/// listed by the binding; the invented lifecycle <c>InvoiceReview</c> of Invoice has <c>issuedOn</c> and <c>dueOn</c> in its context and
/// a guard of the same text. The shared fixture does not change. The PostgreSQL step runs last and reports the test skipped without
/// <c>MAQUETTISTE_TEST_POSTGRES_CONTAINER</c>, once every other step has passed.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ExpressionAcceptanceTests
{
    private const string Main = E2ERepo.MainDatabaseId;
    private const string Invoice = E2ERepo.InvoiceId;
    private const string IssuedOn = "01J92P0V0SNXS6PZ42VDK42HP9";
    private const string DueOn = "01K9ACC0000000000000000A01";
    private const string DueAfterIssue = "01K9ACC0000000000000000C01";
    private const string Review = "01K9ACC0000000000000000P01";
    private const string ReviewIssuedOn = "01K9ACC0000000000000000P02";
    private const string ReviewDueOn = "01K9ACC0000000000000000P03";
    private const string Approve = "01K9ACC0000000000000000P04";
    private const string DatesInOrder = "01K9ACC0000000000000000P05";
    private const string Open = "01K9ACC0000000000000000P06";
    private const string Approved = "01K9ACC0000000000000000P07";
    private const string SnapshotPath = ".maquettiste/snapshots/main.json";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Issued_on_before_due_on_is_written_once()
    {
        await using var repo = E2ERepo.Create(demo: false);
        foreach (var pack in new[] { "sql-ddl", "csharp-dapper" })
            E2ERepo.CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", pack), Path.Combine(repo.Repo.ModelRoot, "templates", pack));

        // Invoice reads and writes a designed invoices table, and gains the optional dueOn with its due_on column and field.
        await ApplyAsync(repo, $$"""{ "op": "materialize-tables", "database": "{{Main}}", "entities": ["{{Invoice}}"] }""");
        await repo.EditAsync(Invoice, e => e["attributes"]!.AsArray().Add(new JsonObject { ["id"] = DueOn, ["name"] = "dueOn", ["type"] = "date" }));
        await ApplyAsync(repo, $$"""{ "op": "materialize-columns", "database": "{{Main}}", "entities": ["{{Invoice}}"], "attributes": ["{{DueOn}}"] }""");

        // The check, written once as text and listed by the binding, is stored as a tree of ids.
        await repo.EditAsync(Invoice, e =>
        {
            e["checks"] = JsonNode.Parse($$"""
                [ { "id": "{{DueAfterIssue}}", "name": "dueAfterIssue", "attribute": "{{IssuedOn}}", "condition": { "text": "issuedOn <= dueOn" },
                    "message": "An invoice is due on or after the day it is issued." } ]
                """);
            e["bindings"]![0]!["checks"] = new JsonArray(DueAfterIssue);
        });
        var tree = $$"""{"op":"le","args":[{"path":["{{IssuedOn}}"]},{"path":["{{DueOn}}"]}]}""";
        Assert.Equal(tree, await StoredConditionAsync(repo));

        // The lifecycle InvoiceReview: from open, approve needs the same text, read over its context.
        var invoice = (await repo.Store.GetElementAsync(Invoice, Ct))!;
        var withLifecycle = JsonNode.Parse(invoice.Json.GetRawText())!.AsObject();
        withLifecycle["lifecycle"] = Review;
        await ApplyAsync(repo, $$"""
            { "op": "create", "element": {
                "kind": "process", "id": "{{Review}}", "name": "InvoiceReview", "use": "lifecycle", "subject": "{{Invoice}}",
                "context": [ { "id": "{{ReviewIssuedOn}}", "name": "issuedOn", "type": "date" }, { "id": "{{ReviewDueOn}}", "name": "dueOn", "type": "date" } ],
                "events": [ { "id": "{{Approve}}", "name": "approve" } ],
                "guards": [ { "id": "{{DatesInOrder}}", "name": "datesInOrder", "condition": { "text": "issuedOn <= dueOn" } } ],
                "states": [ { "id": "{{Open}}", "name": "open" }, { "id": "{{Approved}}", "name": "approved", "type": "final" } ],
                "transitions": [ { "id": "01K9ACC0000000000000000P08", "source": "{{Open}}", "event": "{{Approve}}", "guard": "{{DatesInOrder}}",
                  "targets": ["{{Approved}}"] } ] } }
            """, $$"""{ "op": "update", "id": "{{Invoice}}", "expectedHash": "{{invoice.Hash}}", "element": {{withLifecycle.ToJsonString()}} }""");

        var report = await repo.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.True(!report.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error || d.Rule.StartsWith("MQ31", StringComparison.Ordinal)),
            string.Join("\n", report.Diagnostics.Select(d => $"{d.Severity} {d.Rule} {d.FilePath} {d.JsonPointer} {d.Message}")));
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, await repo.RunAsync(GenerationMode.Apply, packs: ["sql-ddl", "csharp-dapper"]));

        // sql-ddl writes the CHECK into invoices.sql, named by the naming convention from the check's name.
        var tablePath = Assert.Single(repo.Repo.ListFiles(), p => p.StartsWith("db/main/", StringComparison.Ordinal) && p.EndsWith("/tables/invoices.sql", StringComparison.Ordinal));
        var table = repo.Repo.ReadFile(tablePath);
        Assert.Contains("CONSTRAINT ck_invoices_due_after_issue CHECK (issued_on <= due_on)", table, StringComparison.Ordinal);

        // csharp-dapper's Validate() reports a failure at /issuedOn, with the check's name as its code; issuedOn is required, so only
        // a null dueOn makes the answer unknown, and unknown passes.
        var code = repo.Repo.ReadFile(Assert.Single(repo.Repo.ListFiles(),
            p => p.StartsWith("src/Generated/", StringComparison.Ordinal) && p.EndsWith("/Invoice.g.cs", StringComparison.Ordinal))).ReplaceLineEndings("\n");
        Assert.True(code.Contains("""
                    bool? ok = DueOn switch { { } v1 => (bool?)(IssuedOn <= v1), _ => null };
                    if (ok == false)
                        errors.Add(("/issuedOn", "dueAfterIssue", "An invoice is due on or after the day it is issued."));
            """, StringComparison.Ordinal), code);

        // The lowered check: with dueOn null it passes, as the CHECK does; with dueOn before issuedOn it fails.
        var passes = await PreviewAsync(repo, """{ "issuedOn": "2026-01-31", "dueOn": null }""");
        Assert.Equal(("issuedOn <= dueOn", "issued_on <= due_on", (object?)true), (passes.Text, passes.Sql, passes.Answer));
        Assert.Equal(false, (await PreviewAsync(repo, """{ "issuedOn": "2026-01-31", "dueOn": "2026-01-30" }""")).Answer);

        // The same text as a guard: only true passes, so simulation refuses approve while dueOn is null.
        var refused = await SimulateAsync(repo, "null");
        Assert.False(refused.Accepted);
        var guard = Assert.Single(refused.Guards);
        Assert.Equal((DatesInOrder, (bool?)false, GuardSource.Expression), (guard.Guard, guard.Result, guard.Source));
        Assert.True((await SimulateAsync(repo, "\"2026-02-28\"")).Accepted);

        // Renaming dueOn to dueDate changes the printed text and nothing else: the stored tree, the CHECK, its shape in the committed
        // snapshot and every generated SQL file stay as they are.
        var snapshot = repo.Repo.ReadFile(SnapshotPath);
        var shape = Shape(snapshot);
        await repo.EditAsync(Invoice, e => e["attributes"]!.AsArray().Single(a => (string)a!["id"]! == DueOn)!["name"] = "dueDate");
        Assert.Equal("issuedOn <= dueDate", (await PreviewAsync(repo, """{ "issuedOn": "2026-01-31", "dueDate": null }""")).Text);
        Assert.Equal(tree, await StoredConditionAsync(repo));
        var resolved = await repo.Store.Services.Resolver.ResolveAsync(await repo.Store.GetSnapshotAsync(Ct), null, Ct);
        var check = resolved.Databases.Single(d => d.Id == Main).Tables.Single(t => t.Name == "invoices").Checks.Single(c => c.Name == "ck_invoices_due_after_issue");
        Assert.Equal(("issued_on <= due_on", shape), (check.Expression, check.Shape));
        var again = await repo.RunAsync(GenerationMode.Apply, packs: ["sql-ddl"]);
        E2ERepo.AssertOutcome(RunOutcome.Succeeded, again);
        Assert.DoesNotContain(again.Changes, c => c.Kind is FileChangeKind.Added or FileChangeKind.Modified or FileChangeKind.Deleted);
        Assert.Equal((table, snapshot), (repo.Repo.ReadFile(tablePath), repo.Repo.ReadFile(SnapshotPath)));

        // PostgreSQL runs the generated schema, accepts the invoice whose due_on is null, and refuses one due before it is issued.
        var container = Environment.GetEnvironmentVariable("MAQUETTISTE_TEST_POSTGRES_CONTAINER");
        if (string.IsNullOrEmpty(container))
            Assert.Skip("Every step but the database's passed; name a PostgreSQL container in MAQUETTISTE_TEST_POSTGRES_CONTAINER to run the DDL on it.");
        var database = "mq_accept_" + Guid.NewGuid().ToString("N")[..8];
        Assert.Equal(0, (await PsqlAsync(container, "postgres", "CREATE DATABASE " + database + ";")).ExitCode);
        try
        {
            var accepted = await PsqlAsync(container, database, repo.Repo.ReadFile("db/main/schema.sql") + "\n" + Insert("01", "NULL"));
            Assert.True(accepted.ExitCode == 0, accepted.Output);
            var refusedRow = await PsqlAsync(container, database, Insert("02", "'2026-01-30'"));
            Assert.True(refusedRow.ExitCode != 0 && refusedRow.Output.Contains("violates check constraint \"ck_invoices_due_after_issue\"", StringComparison.Ordinal),
                refusedRow.Output);
        }
        finally
        {
            await PsqlAsync(container, "postgres", "DROP DATABASE IF EXISTS " + database + ";");
        }
    }

    /// <summary>Applies batch operations and asserts they were saved.</summary>
    private static async Task ApplyAsync(E2ERepo repo, params string[] operations)
    {
        var parsed = repo.Store.ParseBatch(Encoding.UTF8.GetBytes("""{ "operations": [ """ + string.Join(", ", operations) + " ] }"));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => d.Rule + " " + d.JsonPointer + " " + d.Message)));
        var result = await repo.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
        Assert.True(result.Outcome == SaveOutcome.Saved, result.Outcome + ": "
            + string.Join("; ", result.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.FilePath + " " + d.JsonPointer + " " + d.Message)));
    }

    /// <summary>The condition of the check as Invoice's file holds it.</summary>
    private static async Task<string> StoredConditionAsync(E2ERepo repo) =>
        JsonNode.Parse((await repo.Store.GetElementAsync(Invoice, Ct))!.Json.GetRawText())!["checks"]![0]!["condition"]!.ToJsonString();

    /// <summary>Previews the check dueAfterIssue with values by attribute name, as <c>preview_expression</c> does.</summary>
    private static async Task<ExpressionPreviewDetail> PreviewAsync(E2ERepo repo, string values)
    {
        var preview = await repo.Store.PreviewExpressionAsync(new ExpressionPreviewRequest
        {
            Element = DueAfterIssue,
            Values = JsonDocument.Parse(values).RootElement.Clone(),
        }, Ct);
        Assert.True(preview.Preview is not null, string.Join("\n", preview.Diagnostics.Select(d => d.Rule + " " + d.Message)));
        return preview.Preview;
    }

    /// <summary>
    /// Simulates approve from open with issuedOn 2026-01-31 and the given dueOn (a JSON value), as <c>simulate_process</c> does; the
    /// step's trace.
    /// </summary>
    private static async Task<StepTrace> SimulateAsync(E2ERepo repo, string dueOn)
    {
        var call = await repo.Store.SimulateProcessAsync(Review, new SimulateRequest
        {
            Start = JsonDocument.Parse($$"""{ "context": { "{{ReviewIssuedOn}}": "2026-01-31", "{{ReviewDueOn}}": {{dueOn}} }, "at": "2026-02-01T00:00:00Z" }""")
                .RootElement.Clone(),
            Steps = [JsonDocument.Parse($$"""{ "input": "event", "event": "{{Approve}}" }""").RootElement.Clone()],
        }, Ct);
        Assert.True(call.Status == ProcessCallStatus.Ok, call.Detail);
        return call.Value!.Trace[^1];
    }

    /// <summary>The shape the committed snapshot records for the check of invoices.</summary>
    private static string Shape(string snapshot)
    {
        var invoices = JsonNode.Parse(snapshot)!["tables"]!.AsArray().Single(t => (string)t!["name"]! == "invoices")!;
        var check = invoices["checks"]!.AsArray().Single(c => (string)c!["name"]! == "ck_invoices_due_after_issue")!;
        Assert.Matches("^ck:[0-9a-f]{16}$", (string)check["key"]!);
        return (string)check["shape"]!;
    }

    /// <summary>A customer, then an invoice of it issued on 2026-01-31 and due on <paramref name="dueOn"/> (SQL).</summary>
    private static string Insert(string n, string dueOn) => $"""
        INSERT INTO billing.customers (id, name, email, created_at)
        VALUES ('0192f0c4-0000-7000-8000-0000000000{n}', 'Customer {n}', 'customer{n}@example.com', now());
        INSERT INTO billing.invoices (id, number, issued_on, due_on, status, created_at, customer_id)
        VALUES ('0192f0c4-0000-7000-8000-0000000001{n}', 'INV-{n}', '2026-01-31', {dueOn}, 'D', now(), '0192f0c4-0000-7000-8000-0000000000{n}');
        """;

    /// <summary>Runs a script with psql in the container, stopping at the first error; its exit code and output.</summary>
    private static async Task<(int ExitCode, string Output)> PsqlAsync(string container, string database, string script)
    {
        var start = new ProcessStartInfo("docker")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in new[] { "exec", "-i", container, "psql", "-U", "postgres", "-X", "-q", "-v", "ON_ERROR_STOP=1", "-d", database })
            start.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Ct);
        var stderr = process.StandardError.ReadToEndAsync(Ct);
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(cts.Token);
        return (process.ExitCode, await stdout + "\n" + await stderr);
    }
}
