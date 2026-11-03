using System.Text.Json;

namespace Maquettiste.Engine.Tests.Snapshots;

/// <summary>The review form of a model comparison (next-phase-2-design.md section 6.2, decision 6).</summary>
public sealed class ModelDiffFormatTests
{
    private const string Billing = ModelDiffTests.Billing;

    [Fact]
    public void Markdown_lists_groups_elements_fields_facts_and_files_in_order()
    {
        var markdown = ModelDiffFormat.Markdown(Review());

        Assert.Equal("""
            # Model diff: `before-review-20261103-120000` to `working`

            2 added, 1 removed, 1 changed.

            ## Billing

            - changed entity `Invoice` (`01J92P0V0FJ23CGSNKM7P1W5V7`)
              - attribute `code`, `/name` from `"number"` to `"code"`
              - `/description` added: `"Issued to a customer."`
            - added table `payment_method` (`01K9REV0000000000000000001`)

            ## (no package)

            - removed sequence `credit_note_seq` (`01K9REV0000000000000000002`)

            ## migrations of main

            - added migration `rename column billing.invoices.number` (`01K9REV0000000000000000003`)

            ## Other files

            - changed `extensions/rules/billing.json`

            """.ReplaceLineEndings("\n"), markdown);
    }

    [Fact]
    public void Long_values_and_many_fields_are_cut()
    {
        var fields = Enumerable.Range(0, 25)
            .Select(i => new SnapshotFieldChange($"/tags/{i}", "added", null, Value(i == 0 ? new string('x', 100) : "t" + i)))
            .ToList();
        var comparison = Review() with
        {
            Elements = [Review().Elements[0] with { Fields = fields }, .. Review().Elements.Skip(1)],
        };

        var lines = ModelDiffFormat.Markdown(comparison).Split('\n');

        Assert.Contains("  - `/tags/0` added: `\"" + new string('x', 79) + "...`", lines);
        Assert.Equal(20, lines.Count(l => l.StartsWith("  - `/tags/", StringComparison.Ordinal)));
        Assert.Contains("  - and 5 more fields", lines);
        var truncated = comparison with { Elements = [comparison.Elements[0] with { FieldsTruncated = true }, .. comparison.Elements.Skip(1)] };
        Assert.Contains("  - and over 5 more fields", ModelDiffFormat.Markdown(truncated).Split('\n'));
    }

    [Fact]
    public void Backticks_in_names_stay_inside_code_spans()
    {
        var comparison = Review() with
        {
            Elements = [Review().Elements[0] with { Name = "In`voice", Fields = [] }, .. Review().Elements.Skip(1)],
        };

        var markdown = ModelDiffFormat.Markdown(comparison);

        Assert.Contains("- changed entity `` In`voice `` (`01J92P0V0FJ23CGSNKM7P1W5V7`)\n", markdown, StringComparison.Ordinal);
        Assert.Contains("- changed entity In`voice (01J92P0V0FJ23CGSNKM7P1W5V7)", ModelDiffFormat.Text(comparison).Replace("  changed", "- changed", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Text_and_markdown_carry_the_same_counts()
    {
        var markdown = ModelDiffFormat.Markdown(Review()).Split('\n');
        var text = ModelDiffFormat.Text(Review()).Split('\n');

        Assert.Equal(markdown.Length, text.Length);
        Assert.Equal("2 added, 1 removed, 1 changed.", markdown[2]);
        Assert.Equal(markdown[2], text[2]);
        Assert.Equal("Model diff: before-review-20261103-120000 to working", text[0]);
        Assert.Contains("  changed entity Invoice (01J92P0V0FJ23CGSNKM7P1W5V7)", text);
        Assert.Contains("    attribute code, /name from \"number\" to \"code\"", text);
        Assert.DoesNotContain(text, l => l.StartsWith('#') || l.Contains('`', StringComparison.Ordinal) || l.TrimStart().StartsWith("- ", StringComparison.Ordinal));

        // Only a comparison made by package and with field changes renders.
        Assert.Throws<ArgumentException>(() => ModelDiffFormat.Markdown(Review() with { Packages = null }));
        Assert.Throws<ArgumentException>(() => ModelDiffFormat.Text(Review() with { Elements = [Review().Elements[0] with { Fields = null }, .. Review().Elements.Skip(1)] }));
    }

    /// <summary>A whole comparison as the engine makes it with ByPackage and FieldChanges.</summary>
    private static SnapshotComparison Review()
    {
        SnapshotElementChange[] elements =
        [
            new(ModelDiffTests.Invoice, "entity", "Invoice", "changed", "model/entities/invoice.json")
            {
                Package = Billing,
                Fields =
                [
                    new("/attributes/1/name", "changed", Value("number"), Value("code"))
                    {
                        SubId = ModelDiffTests.InvoiceNumber, SubKind = "attribute", SubName = "code", SubPointer = "/name",
                    },
                    new("/description", "added", null, Value("Issued to a customer.")),
                ],
            },
            new("01K9REV0000000000000000001", "table", "payment_method", "added", "model/databases/main/tables/payment-method.json") { Package = Billing },
            new("01K9REV0000000000000000002", "sequence", "credit_note_seq", "removed", "model/databases/main/sequences/credit-note-seq.json"),
            new("01K9REV0000000000000000003", "migration", "", "added", "model/databases/main/migrations/open/01k9rev0000000000000000003.json")
            {
                Database = ModelDiffTests.Main, Label = "rename column billing.invoices.number",
            },
        ];
        return new SnapshotComparison("before-review-20261103-120000", SnapshotLibrary.Working, 2, 1, 1,
            [new("entity", 0, 0, 1), new("migration", 1, 0, 0), new("sequence", 0, 1, 0), new("table", 1, 0, 0)], elements, null,
            [new SnapshotFileChange("extensions/rules/billing.json", "changed")], false, false)
        {
            Packages = [new(Billing, "Billing", 1, 0, 1), new(null, "(no package)", 0, 1, 0), new(null, "migrations of main", 1, 0, 0)],
        };
    }

    private static JsonElement Value(string text) => JsonSerializer.SerializeToElement(text);
}
