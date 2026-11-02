using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// The shape hash a binding's provenance records per column (next-phase-2-design.md section 3.3, D30): a name part over the loose name
/// and a type part over eight lines of the table file's own values, 8 hex digits each.
/// </summary>
public sealed class ColumnShapeHashTests
{
    private static Column Number(Func<Column, Column>? change = null)
    {
        var column = new Column { Id = "01K6SHA0000000000000000001", Name = "invoice_number", Type = "string", Length = 32, Nullable = false };
        return change is null ? column : change(column);
    }

    [Fact]
    public void Name_part_ignores_case_and_separators()
    {
        var part = ColumnShapeHash.NamePart("invoice_number");

        Assert.Equal(part, ColumnShapeHash.NamePart("InvoiceNumber"));
        Assert.Equal(part, ColumnShapeHash.NamePart("INVOICE-NUMBER"));
        Assert.Equal(part, ColumnShapeHash.NamePart("invoice number"));
        Assert.Equal(part, ColumnShapeHash.NamePart("invoiceNumber"));
        Assert.NotEqual(part, ColumnShapeHash.NamePart("invoice_no"));
        Assert.Equal("invoicenumber", ColumnShapeHash.Loose("Invoice_Number"));
        Assert.Equal("customerid2", ColumnShapeHash.Loose("customer.ID-2"));
        Assert.Matches("^[0-9a-f]{8}$", part);
    }

    [Fact]
    public void Name_and_type_parts_change_independently()
    {
        var before = ColumnShapeHash.Of(Number());
        var renamed = ColumnShapeHash.Of(Number(c => c with { Name = "invoice_no" }));
        var retyped = ColumnShapeHash.Of(Number(c => c with { Length = 64 }));

        Assert.Matches("^[0-9a-f]{8}:[0-9a-f]{8}$", before);
        Assert.NotEqual(ColumnShapeHash.NamePartOf(before), ColumnShapeHash.NamePartOf(renamed));
        Assert.Equal(ColumnShapeHash.TypePartOf(before), ColumnShapeHash.TypePartOf(renamed));
        Assert.Equal(ColumnShapeHash.NamePartOf(before), ColumnShapeHash.NamePartOf(retyped));
        Assert.NotEqual(ColumnShapeHash.TypePartOf(before), ColumnShapeHash.TypePartOf(retyped));

        // Every line of the type part moves it; what is not a line (a comment, a default, the id) does not.
        foreach (var change in new Func<Column, Column>[]
        {
            c => c with { Type = "text" },
            c => c with { Type = "range", Subtype = "int32" },
            c => c with { Precision = 10 },
            c => c with { Scale = 2 },
            c => c with { NativeType = "varchar(32)" },
            c => c with { Nullable = true },
        })
        {
            Assert.NotEqual(ColumnShapeHash.TypePart(Number()), ColumnShapeHash.TypePart(Number(change)));
        }

        Assert.Equal(before, ColumnShapeHash.Of(Number(c => c with { Comment = "The invoice's number.", Id = "01K6SHA0000000000000000002", Collation = "C" })));
    }

    [Fact]
    public void Absent_nullable_reads_as_true()
    {
        Assert.Equal(ColumnShapeHash.TypePart(Number(c => c with { Nullable = true })), ColumnShapeHash.TypePart(Number(c => c with { Nullable = null })));
        Assert.NotEqual(ColumnShapeHash.TypePart(Number(c => c with { Nullable = false })), ColumnShapeHash.TypePart(Number(c => c with { Nullable = null })));
    }

    [Fact]
    public void A_typeRef_change_moves_the_type_part()
    {
        var plain = Number();
        var typed = Number(c => c with { TypeRef = new TypeRef { Ref = "01K6SHA0000000000000000003" } });
        var other = Number(c => c with { TypeRef = new TypeRef { Ref = "01K6SHA0000000000000000004" } });

        Assert.NotEqual(ColumnShapeHash.TypePart(plain), ColumnShapeHash.TypePart(typed));
        Assert.NotEqual(ColumnShapeHash.TypePart(typed), ColumnShapeHash.TypePart(other));
        Assert.Equal(ColumnShapeHash.NamePart(plain.Name), ColumnShapeHash.NamePart(typed.Name));
    }

    [Fact]
    public void An_inherited_column_hashes_its_redefined_entry()
    {
        var b = new ModelBuilder(seed: 43);
        var main = b.Database("main", Dialect.PostgreSql);
        var updated = new Column { Id = b.NewId(), Name = "updated_at", Type = "datetimeoffset" };
        b.Add(new Stereotype { Id = b.NewId(), Key = "audited", Name = "Audited", AppliesTo = ["table"], Columns = [updated] });
        var notes = b.Table("notes", main).Column("id", "uuid", nullable: false).PrimaryKey("id").Stereotype("audited")
            .Edit(t => t with { Redefines = [new TableRedefinition { Member = updated.Id, Nullable = false }] });
        var model = b.Build();

        var columns = BindingDrift.ColumnsOf(model, model.Get<Table>(notes.Id)!);

        Assert.Equal(["id", "updated_at"], columns.Select(c => c.Name));
        var inherited = columns[1];
        Assert.True(inherited.Inherited);
        Assert.Equal(notes.Id + "." + updated.Id, inherited.Id);
        Assert.Equal(ColumnShapeHash.Of(updated with { Nullable = false }), inherited.Shape);
        Assert.NotEqual(ColumnShapeHash.Of(updated), inherited.Shape);
    }

    [Fact]
    public void Pinned_value_for_a_known_column()
    {
        // invoice_number: the name part hashes "invoicenumber"; the type part hashes "string\n\n\n32\n\n\n\nfalse".
        Assert.Equal("5d1d1b79:461bd2c5", ColumnShapeHash.Of(Number()));
    }
}
