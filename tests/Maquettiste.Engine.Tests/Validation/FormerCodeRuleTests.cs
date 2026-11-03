using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// Former codes of a reference type (next-phase-2-design.md section 3.5, D50): MQ7017 (a former code that is live, listed twice or
/// does not fit the code), MQ7018 (a <c>replacedBy</c> row of another type), MQ2001 for a <c>replacedBy</c> that names no row, and
/// MQ7019 where a default, an <c>allowedValues</c> entry, a seed cell or a typed column's default names a former code, in place of
/// MQ3019, MQ7011, MQ7009 and MQ7014.
/// </summary>
public sealed class FormerCodeRuleTests
{
    private static readonly string[] Rules = ["MQ2001", "MQ3019", "MQ7009", "MQ7011", "MQ7014", "MQ7017", "MQ7018", "MQ7019"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);

    private sealed record TypeId(string Id) : ITypeSource;

    /// <summary>
    /// The billing reference type <c>PaymentTerm</c> (a string code of length 8; rows <c>net-30</c> and <c>net60</c>; the former code
    /// <c>net30</c> replaced by the <c>net-30</c> row, as a rename leaves it), <c>CreditTerm</c> (row <c>n90</c>), the entity
    /// <c>Invoice</c> whose <c>paymentTerm</c> is typed by <c>PaymentTerm</c>, a seed <c>demoInvoices</c> with one cell of it, and a designed
    /// table <c>invoice_notes</c> whose <c>term</c> column is typed by it.
    /// </summary>
    private sealed class Fixture
    {
        public Fixture(Func<Fixture, IReadOnlyList<FormerCode>>? former = null, object? attributeDefault = null,
            string[]? allowed = null, string cell = "net-30", string? columnDefault = null)
        {
            B = new ModelBuilder(seed: 3501);
            var main = B.Database("main", Dialect.PostgreSql);
            TermId = B.NewId();
            Net30Row = B.NewId();
            Net60Row = B.NewId();
            CreditRow = B.NewId();
            var credit = new ReferenceType
            {
                Id = B.NewId(), Name = "CreditTerm",
                Code = new ReferenceCode { Id = B.NewId(), Length = 8 }, Label = new ReferenceLabel { Id = B.NewId(), Length = 40 },
            };
            B.Add(credit);
            B.Add(new Seed
            {
                Id = B.NewId(), Name = "creditTerms", Target = credit.Id, Columns = ["code", "label"],
                Rows = [new SeedRow { Id = CreditRow, Values = [J("n90"), J("Net 90")] }],
            });

            B.Add(new ReferenceType
            {
                Id = TermId, Name = "PaymentTerm",
                Code = new ReferenceCode { Id = B.NewId(), Length = 8 }, Label = new ReferenceLabel { Id = B.NewId(), Length = 40 },
                FormerCodes = former is null ? [Net30] : former(this),
            });
            B.Add(new Seed
            {
                Id = B.NewId(), Name = "paymentTerms", Target = TermId, Columns = ["code", "label"],
                Rows =
                [
                    new SeedRow { Id = Net30Row, Values = [J("net-30"), J("Net 30")] },
                    new SeedRow { Id = Net60Row, Values = [J("net60"), J("Net 60")] },
                ],
            });

            string? termAttribute = null;
            var invoice = B.Entity("Invoice").Key("id", "uuid", IdentityStrategy.UuidV7).Attr("paymentTerm", new TypeId(TermId), a =>
            {
                termAttribute = a.Id;
                if (attributeDefault is not null)
                    a.Default(attributeDefault);
                if (allowed is not null)
                    a.Validation(new AttributeValidation { AllowedValues = [.. allowed.Select(v => J(v))] });
            });
            InvoiceSeed = B.NewId();
            B.Add(new Seed
            {
                Id = InvoiceSeed, Name = "demoInvoices", Target = invoice.Id, Columns = [termAttribute!],
                Rows = [new SeedRow { Id = B.NewId(), Values = [J(cell)] }],
            });

            B.Table("invoice_notes", main).Column("id", "uuid", nullable: false).Column("term", "string", length: 8).PrimaryKey("id")
                .Edit(t => t with
                {
                    Columns = [.. t.Columns.Select(c => c.Name == "term"
                        ? c with { TypeRef = new TypeRef { Ref = TermId }, Default = columnDefault is null ? null : J(columnDefault) }
                        : c)],
                });
        }

        public ModelBuilder B { get; }

        public string TermId { get; }

        public string Net30Row { get; }

        public string Net60Row { get; }

        public string CreditRow { get; }

        public string InvoiceSeed { get; }

        /// <summary>The former code a rename of <c>net30</c> to <c>net-30</c> leaves.</summary>
        public FormerCode Net30 => new() { Code = "net30", ReplacedBy = Net30Row };

        public async Task<List<Diagnostic>> FindingsAsync()
        {
            var report = await ValidationFixture.Validator().ValidateAsync(B.Build(), ValidationScope.All, null, Ct);
            return [.. report.Diagnostics.Where(d => Rules.Contains(d.Rule))];
        }
    }

    private static (string Rule, string Pointer) At(Diagnostic d) => (d.Rule, d.JsonPointer!);

    [Fact]
    public async Task Former_codes_and_uses_of_the_new_code_are_clean()
    {
        Assert.Empty(await new Fixture(attributeDefault: "net-30", allowed: ["net-30", "net60"], columnDefault: "net60").FindingsAsync());
    }

    [Fact]
    public async Task A_former_code_that_is_live_is_MQ7017()
    {
        var findings = await new Fixture(former: f => [f.Net30, new FormerCode { Code = "net60", ReplacedBy = f.Net30Row }]).FindingsAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(("MQ7017", "/formerCodes/1/code"), At(finding));
        Assert.Contains("'net60'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("live code", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_former_code_listed_twice_is_MQ7017()
    {
        var findings = await new Fixture(former: f => [f.Net30, f.Net30]).FindingsAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(("MQ7017", "/formerCodes/1/code"), At(finding));
        Assert.Contains("listed twice", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_former_code_that_does_not_fit_the_code_is_MQ7017()
    {
        // Longer than the code's length 8.
        var findings = await new Fixture(former: f => [f.Net30, new FormerCode { Code = "net-thirty", ReplacedBy = f.Net30Row }]).FindingsAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(("MQ7017", "/formerCodes/1/code"), At(finding));
        Assert.Contains("does not fit", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replaced_by_row_of_another_type_is_MQ7018()
    {
        var f = new Fixture(former: x => [new FormerCode { Code = "net30", ReplacedBy = x.CreditRow }]);

        var finding = Assert.Single(await f.FindingsAsync());
        Assert.Equal(("MQ7018", "/formerCodes/0/replacedBy"), At(finding));
        Assert.Contains("creditTerms", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_dangling_replaced_by_is_MQ2001()
    {
        var findings = await new Fixture(former: _ => [new FormerCode { Code = "net30", ReplacedBy = "01JZZZZZZZZZZZZZZZZZZZZZZZ" }]).FindingsAsync();

        var finding = Assert.Single(findings);
        Assert.Equal(("MQ2001", "/formerCodes/0/replacedBy"), At(finding));
    }

    [Fact]
    public async Task A_default_naming_a_former_code_is_MQ7019_and_not_MQ3019()
    {
        var finding = Assert.Single(await new Fixture(attributeDefault: "net30").FindingsAsync());

        Assert.Equal(("MQ7019", "/attributes/1/default"), At(finding));
        Assert.Contains("write 'net-30'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cell_naming_a_former_code_is_MQ7019_and_not_MQ7009()
    {
        var f = new Fixture(cell: "net30");

        var finding = Assert.Single(await f.FindingsAsync());
        Assert.Equal(("MQ7019", "/rows/0/values/0"), At(finding));
        Assert.Equal(f.B.Build().GetDocument(f.InvoiceSeed)!.Path, finding.FilePath);
        Assert.Contains("write 'net-30'", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_allowed_value_naming_a_former_code_is_MQ7019_and_not_MQ7011()
    {
        var finding = Assert.Single(await new Fixture(allowed: ["net60", "net30"]).FindingsAsync());

        Assert.Equal(("MQ7019", "/attributes/1/validation/allowedValues/1"), At(finding));
    }

    [Fact]
    public async Task A_typed_column_default_naming_a_former_code_is_MQ7019_and_not_MQ7014()
    {
        var finding = Assert.Single(await new Fixture(columnDefault: "net30").FindingsAsync());

        Assert.Equal("MQ7019", finding.Rule);
        Assert.Contains("write 'net-30'", finding.Message, StringComparison.Ordinal);
    }
}
