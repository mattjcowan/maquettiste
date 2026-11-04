using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The C# example pack's expression renderer, <c>expr_cs</c> in <c>packs/csharp-dapper/helpers.js</c>, against the engine's own answers
/// (next-phase-3-design.md section 3.1): Invoice gets one check per shape that is easy to get wrong in C# (nulls through functions and
/// nesting, integer overflow, literals beyond 2^53, strings beyond the basic plane, a check that folds to a constant), the generated
/// <c>Validate()</c> compiles with warnings as errors, and for every check and row it fails exactly when the preview, which runs the
/// expression runtime, says the check fails.
/// </summary>
public sealed class ExprCsTests
{
    private const string InvoiceId = "01J92P0V0FJ23CGSNKM7P1W5V7";
    private const string NumberId = "01J92P0V0R3VSP7D5238DTNZX1";

    private static readonly string[] Checks =
    [
        "dueOn between issuedOn and add_days(issuedOn, 30)",
        "status in (InvoiceStatus.Draft, InvoiceStatus.Paid) or notes is not null",
        "code not in ('A', 'B')",
        "qty is null implies notes is null",
        "coalesce(qty, 0) * 2 + 1 > qty",
        "case when active then qty > 0 when qty is null then true end",
        "code < 'M'",
        "length(code) <= 5",
        "trim(code) = code",
        "date_part('isodow', dueOn) between 1 and 5",
        "add_months(dueOn, 1) > dueOn",
        "date_of(paidAt) >= issuedOn",
        "date_part('month', seenAt) = date_part('month', paidAt)",
        "round(price, 1) >= 0",
        "nonnull_count(qty, price, notes) >= 1",
        "price * qty <= 1000000",
        "not (active = false)",
        "active is distinct from true",
        "matches(code, '^[A-Z]{2}-[0-9]+?$')",
        "qty between -5 and 5",
        "-qty <= 5",
        "big + 1 > big",
        "code || 'x' <> 'x'",
        "ratio >= double '0.5'",
        "paidAt >= datetime '2026-01-01T00:00'",
        "seenAt >= datetimeoffset '2026-01-01T00:00Z'",
        "date_part('hour', paidAt) < 12",
        "qty <> 3",
        "code >= 'Ａ'",
        "code = 'ab' or code is null",
        "status <> InvoiceStatus.Void",
        "qty + price > 1",
        "big * big > 0",
        "add_days(seenAt, 1) > seenAt",
        "add_months(seenAt, 1) > seenAt",
        "date_of(seenAt) = dueOn",
        "case when qty > 2 then 'big' when qty > 0 then 'small' else 'none' end = 'none'",
        "coalesce(code, 'z') > 'y'",
        "nonnull_count(code) = 1 or active",
        "(active or qty > 0) and (active or notes is null)",
        "big <= 9007199254740993",
        "qty in (1, -2, 3)",
        "ratio > ratio * double '0.5'",
        "date_part('year', issuedOn) >= 2000",
        "issuedOn <= coalesce(dueOn, issuedOn)",
        "length(trim(code)) = length(code)",
        "matches(notes, '^(a|)$')",
        "code is not distinct from 'A'",
        "add_months(paidAt, 1) > paidAt",
        "date_part('day', add_months(dueOn, 1)) = 31",
        "status = InvoiceStatus.Draft",
        "active",
        "active implies qty > 0",
        "price between 0.05 and 100.00",
        "qty * 2 between 0 and 10",
        "code <> 'A' and code <> 'B'",
        "number is not null",
        "big < 9223372036854775807",
        "-big - 2 < 0",
        "qty * qty * qty * qty * qty * qty * qty * big > 0",
        "length(trim(trim(trim(code)))) between 0 and 3",
        "code between 'A' and 'Ｚ'",
        "date_part('day', add_days(dueOn, qty)) > 10",
        "case when active then big * 2 else qty end > 0",
        "big * 2 > price",
        "ratio < big * 2",
        "qty is distinct from big + 0",
        "big in (5, 9223372036854775807)",
        "not (qty = null) or active",
        "coalesce(code, null) = 'A'",
        "case when qty > 0 then null else qty end > 0",
        "qty + null > 1",
        "nonnull_count(null, qty) = 1",
        "qty is distinct from null",
        "case when null then 1 else qty end > 0",
        "(null and active) or qty > 0",
        "issuedOn <= issuedOn",
        "issuedOn is not distinct from issuedOn",
    ];

    private sealed record Row(string Number, string IssuedOn, string Status, string? Notes, string? Qty, string? Big, string? Ratio, string? Price,
        string? DueOn, string? PaidAt, string? SeenAt, bool? Active, string? Code);

    private static readonly Row[] Rows =
    [
        new("INV-1", "2026-01-31", "Draft", null, null, null, null, null, null, null, null, null, null),
        new("INV-2", "2026-01-31", "Paid", "x", "3", "9223372036854775807", "0.5", "12.50", "2026-02-15", "2026-02-01T09:30:00", "2026-02-01T23:30:00+05:00", true, "AB-12"),
        new("INV-3", "2026-01-31", "Issued", null, "-7", "-1", "0.25", "-0.05", "2026-03-15", "2025-12-31T13:00:00", "2026-01-31T23:30:00Z", false, "\U0001F600"),
        new("INV-4", "2026-01-31", "Draft", "", "0", "5", "1.0", "0.04", "2026-01-31", "2026-01-31T00:00:00", "2026-01-01T00:00:00Z", null, " ab "),
        new("INV-5", "2026-01-31", "Void", "note", "5", "3037000500", null, null, null, null, null, true, "A"),
        new("INV-6", "2026-01-31", "Paid", "a", "1", "9007199254740993", null, "1.00", "2026-01-31", null, "2026-01-31T19:00:00-05:00", null, ""),
        new("INV-7", "2026-01-31", "Paid", "ab", "-2", "9007199254740992", "2.0", "100.00", "2026-02-28", "2026-02-28T11:59:59", "2026-02-28T23:30:00Z", false, "ab"),
    ];

    private static readonly Dictionary<string, string> Codes = new(StringComparer.Ordinal) { ["Draft"] = "D", ["Issued"] = "I", ["Paid"] = "P", ["Void"] = "V" };

    [Fact]
    public async Task Generated_validate_compiles_and_fails_a_row_exactly_where_the_preview_does()
    {
        var ct = TestContext.Current.CancellationToken;
        using var repo = PackRepo.Billing();
        AddAttributesAndChecks(repo);
        await using (var store = new ModelStore(repo.Repo.Options))
        {
            var parsed = store.ParseBatch("""{ "operations": [ { "op": "text-to-trees" } ] }"""u8);
            var applied = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Cli, ct);
            Assert.True(applied.Outcome == SaveOutcome.Saved, string.Join("; ", applied.Items.SelectMany(i => i.Diagnostics).Select(d => d.Rule + " " + d.Message)));
        }

        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var generated = repo.PathOf("src/Generated");
        var invoice = File.ReadAllText(Directory.GetFiles(generated, "Invoice.g.cs", SearchOption.AllDirectories).Single());
        var skipped = Checks.Where((_, i) => invoice.Contains("// Check " + Name(i) + " ", StringComparison.Ordinal)).ToList();
        Assert.True(skipped.Count == 0, "expr_cs left out: " + string.Join("; ", skipped));
        Assert.True(File.Exists(Path.Combine(generated, "CodePoints.g.cs")), "the code point order is written once, beside the entities");

        // One line per row: the codes of the checks Validate() reports. The project compiles with warnings as errors.
        var project = Path.Combine(repo.Repo.Root, "exprcs");
        CompileTests.WriteProject(project, generated, Program());
        await CompileTests.BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("validate ok", StringComparison.Ordinal), run.Output);
        var failed = run.Output.Split('\n').Where(l => l.StartsWith("row", StringComparison.Ordinal))
            .ToDictionary(l => int.Parse(l[3..l.IndexOf(':', StringComparison.Ordinal)], CultureInfo.InvariantCulture),
                l => l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim().Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal));

        var mismatches = new StringBuilder();
        await using var preview = new ModelStore(repo.Repo.Options);
        for (var i = 0; i < Rows.Length; i++)
        {
            var values = JsonSerializer.SerializeToElement(Values(Rows[i]));
            for (var j = 0; j < Checks.Length; j++)
            {
                var result = await preview.PreviewExpressionAsync(new ExpressionPreviewRequest { Element = InvoiceId, At = "/checks/" + j.ToString(CultureInfo.InvariantCulture), Values = values }, ct);
                var passes = !failed[i].Contains(Name(j));
                if (result.Preview is null || result.Diagnostics.Count > 0)
                {
                    mismatches.Append("row ").Append(i).Append(" [").Append(Checks[j]).Append("] previews no answer: ")
                        .AppendJoin("; ", result.Diagnostics.Select(d => d.Rule + " " + d.Message)).Append('\n');
                }
                else if (result.Preview.Answer is true != passes)
                {
                    mismatches.Append("row ").Append(i).Append(" [").Append(Checks[j]).Append("] the preview ").Append(passes ? "fails" : "passes").Append(" it, C# does not\n");
                }
            }
        }

        Assert.True(mismatches.Length == 0, "expr_cs disagrees with the runtime:\n" + mismatches);
    }

    private static string Name(int index) => "c" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>Gives the billing fixture's Invoice one optional attribute of each type the checks read, and the checks, as text.</summary>
    private static void AddAttributesAndChecks(PackRepo repo) => repo.EditJson(".maquettiste/model/entities/invoice.json", invoice =>
    {
        var attributes = invoice["attributes"]!.AsArray();
        (string Name, string Type, JsonObject? Extra)[] added =
        [
            ("qty", "int32", null), ("big", "int64", null), ("ratio", "double", null), ("price", "decimal", new JsonObject { ["precision"] = 14, ["scale"] = 2 }),
            ("dueOn", "date", null), ("paidAt", "datetime", null), ("seenAt", "datetimeoffset", null), ("active", "bool", null),
            ("code", "string", new JsonObject { ["length"] = 10 }),
        ];
        for (var i = 0; i < added.Length; i++)
        {
            var a = new JsonObject { ["id"] = "01K9EXPRCS00000000000A" + (i + 1).ToString("D4", CultureInfo.InvariantCulture), ["name"] = added[i].Name, ["type"] = added[i].Type };
            if (added[i].Extra is { } extra)
            {
                foreach (var (key, value) in extra)
                    a[key] = value!.DeepClone();
            }

            attributes.Add(a);
        }

        invoice["checks"] = new JsonArray([.. Checks.Select((text, i) => (JsonNode)new JsonObject
        {
            ["id"] = "01K9EXPRCS00000000000C" + (i + 1).ToString("D4", CultureInfo.InvariantCulture),
            ["name"] = Name(i),
            ["attribute"] = NumberId,
            ["condition"] = new JsonObject { ["text"] = text },
        })]);
    });

    /// <summary>The row as the API carries it: numbers as text, the enum member as its code.</summary>
    private static JsonObject Values(Row r) => new()
    {
        ["number"] = r.Number, ["issuedOn"] = r.IssuedOn, ["status"] = Codes[r.Status], ["notes"] = r.Notes, ["qty"] = r.Qty, ["big"] = r.Big, ["ratio"] = r.Ratio,
        ["price"] = r.Price, ["dueOn"] = r.DueOn, ["paidAt"] = r.PaidAt, ["seenAt"] = r.SeenAt, ["active"] = r.Active, ["code"] = r.Code,
    };

    /// <summary>A program that builds the rows as Invoices and prints the codes of the checks each one fails.</summary>
    private static string Program()
    {
        var program = new StringBuilder("""
            using System;
            using System.Globalization;
            using System.Linq;
            using App.Model.Billing;

            Invoice[] rows =
            [

            """);
        foreach (var r in Rows)
        {
            program.Append("    new Invoice { Number = ").Append(Json(r.Number)).Append(", IssuedOn = ").Append(Date(r.IssuedOn)).Append(", Status = InvoiceStatus.").Append(r.Status)
                .Append(", Notes = ").Append(r.Notes is null ? "null" : Json(r.Notes))
                .Append(", Qty = ").Append(r.Qty ?? "null")
                .Append(", Big = ").Append(r.Big is null ? "null" : r.Big + "L")
                .Append(", Ratio = ").Append(r.Ratio is null ? "null" : r.Ratio + "d")
                .Append(", Price = ").Append(r.Price is null ? "null" : r.Price + "m")
                .Append(", DueOn = ").Append(r.DueOn is null ? "null" : Date(r.DueOn))
                .Append(", PaidAt = ").Append(r.PaidAt is null ? "null" : "DateTime.Parse(" + Json(r.PaidAt) + ", CultureInfo.InvariantCulture, DateTimeStyles.None)")
                .Append(", SeenAt = ").Append(r.SeenAt is null ? "null" : "DateTimeOffset.Parse(" + Json(r.SeenAt) + ", CultureInfo.InvariantCulture)")
                .Append(", Active = ").Append(r.Active is null ? "null" : r.Active.Value ? "true" : "false")
                .Append(", Code = ").Append(r.Code is null ? "null" : Json(r.Code))
                .Append(" },\n");
        }

        program.Append("""
            ];
            for (var i = 0; i < rows.Length; i++)
                Console.WriteLine("row" + i + ": " + string.Join(",", rows[i].Validate().Select(e => e.Code)));
            Console.WriteLine("validate ok");

            """);
        return program.ToString();
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    private static string Date(string s) =>
        "new DateOnly(" + string.Join(", ", s.Split('-').Select(p => int.Parse(p, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture))) + ")";
}
