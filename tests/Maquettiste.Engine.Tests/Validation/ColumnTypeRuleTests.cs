using System.Collections.Immutable;
using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Validation;

/// <summary>
/// The column type rules (next-phase-2-design.md section 3.2, D22): MQ4082 (a misplaced <c>typeRef</c>), MQ4083 (a typed column that cannot
/// hold its type's values), MQ4084 (a declared foreign key from a column typed by a reference type that does not point at the type's bound
/// table), MQ4085 (a column and an attribute that state different sensitivities) and MQ7014 (a value written into a typed column that is
/// not one of its type's stored values or codes), each with a failing and a passing case.
/// </summary>
public sealed class ColumnTypeRuleTests
{
    private static readonly string[] Rules = ["MQ4082", "MQ4083", "MQ4084", "MQ4085", "MQ7014"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement J(object? value) => JsonSerializer.SerializeToElement(value);

    /// <summary>
    /// A billing-like model: the reference type <c>InvoiceState</c> (a string code of the given length, rows D Draft and I
    /// Issued, a demo seed adding P Paid), the enum <c>Status</c> (Draft 0 D, Issued 1 I, Void 2 without a code), the scalar type
    /// <c>Money</c> (decimal(12,2)) and a designed table <c>invoice_events</c> whose <c>state</c>, <c>status</c> and <c>amount</c> columns
    /// they type.
    /// </summary>
    private sealed class Fixture
    {
        public Fixture(int codeLength = 1, Func<Table, Table>? editEvents = null, Action<Fixture>? more = null)
        {
            B = new ModelBuilder(seed: 3201);
            Main = B.Database("main", Dialect.PostgreSql);
            State = new ReferenceType
            {
                Id = B.NewId(), Name = "InvoiceState",
                Code = new ReferenceCode { Id = B.NewId(), Length = codeLength },
                Label = new ReferenceLabel { Id = B.NewId(), Length = 40 },
            };
            StateSeed = B.NewId();
            DemoSeed = B.NewId();
            Status = B.Enum("Status").Member("Draft", 0, "D").Member("Issued", 1, "I").Member("Void", 2);
            Money = B.ScalarType("Money", "decimal").Decimal(12, 2);
            Address = B.ValueObject("Address").Attr("street", "string");
            Events = B.Table("invoice_events", Main).Column("id", "uuid", nullable: false).Column("state", "string", nullable: false, length: 1)
                .Column("status", "int32").Column("amount", "decimal").PrimaryKey("id")
                .Edit(t => Typed(t, ("state", State.Id), ("status", Status.Id), ("amount", Money.Id)))
                .Edit(editEvents ?? (t => t));
            more?.Invoke(this);
            B.Add(State);
            B.Add(new Seed
            {
                Id = StateSeed, Name = "invoiceStates", Target = State.Id, Columns = ["code", "label"],
                Rows = [new SeedRow { Id = B.NewId(), Values = [J("D"), J("Draft")] }, new SeedRow { Id = B.NewId(), Values = [J("I"), J("Issued")] }],
            });
            B.Add(new Seed
            {
                Id = DemoSeed, Name = "demoStates", Target = State.Id, Columns = ["code", "label"], Environments = ["demo"],
                Rows = [new SeedRow { Id = B.NewId(), Values = [J("P"), J("Paid")] }],
            });
        }

        public ModelBuilder B { get; }

        public DatabaseBuilder Main { get; }

        public ReferenceType State { get; set; }

        public string StateSeed { get; }

        public string DemoSeed { get; }

        public EnumBuilder Status { get; }

        public ScalarTypeBuilder Money { get; }

        public ValueObjectBuilder Address { get; }

        public TableBuilder Events { get; }

        public async Task<List<Diagnostic>> FindingsAsync()
        {
            var report = await ValidationFixture.Validator().ValidateAsync(B.Build(), ValidationScope.All, null, Ct);
            return [.. report.Diagnostics.Where(d => Rules.Contains(d.Rule))];
        }
    }

    /// <summary>Gives a table's columns, by name, the typeRef of a type id.</summary>
    private static Table Typed(Table table, params (string Column, string TypeId)[] types) => table with
    {
        Columns = [.. table.Columns.Select(c => types.FirstOrDefault(t => t.Column == c.Name) is { Column: not null } t ? c with { TypeRef = new TypeRef { Ref = t.TypeId } } : c)],
    };

    private static Table EditColumn(Table table, string name, Func<Column, Column> edit) => table with
    {
        Columns = [.. table.Columns.Select(c => c.Name == name ? edit(c) : c)],
    };

    private static IEnumerable<(string Rule, string Pointer)> At(IEnumerable<Diagnostic> findings) => findings.Select(d => (d.Rule, d.JsonPointer!)).Order();

    [Fact]
    public async Task Columns_typed_by_an_enum_a_reference_type_and_a_scalar_type_are_clean()
    {
        // The passing case of MQ4082, MQ4083 and MQ7014: each column fits its type, and its default is a stored value or code.
        var f = new Fixture(editEvents: t => EditColumn(EditColumn(t, "state", c => c with { Default = J("D") }), "status", c => c with { Default = J(2) }));
        Assert.Empty(await f.FindingsAsync());
    }

    [Fact]
    public async Task A_flags_enum_column_takes_a_combination_of_its_values()
    {
        static async Task<List<Diagnostic>> WithDefault(long value) => await new Fixture(more: x =>
        {
            var access = x.B.Enum("Access").Flags().Member("Read", 1).Member("Write", 2).Member("Delete", 4);
            x.Events.Column("access", "int32").Edit(t => EditColumn(Typed(t, ("access", access.Id)), "access", c => c with { Default = J(value) }));
        }).FindingsAsync();

        Assert.Empty(await WithDefault(3));
        Assert.Contains("8 is not a value of a member of enum Access nor a combination of them", Assert.Single(await WithDefault(8)).Message, StringComparison.Ordinal);
    }

    // ---- MQ4082 ----

    [Fact]
    public async Task A_typeRef_naming_a_value_object_or_on_an_overlay_entry_is_MQ4082()
    {
        var f = new Fixture(more: x =>
        {
            x.Events.Column("address", "json").Edit(t => Typed(t, ("address", x.Address.Id)));
            var invoice = x.B.Entity("Invoice").Key("id", "uuid").Attr("number", "string");
            x.B.Table("invoices", x.Main).OverlayFor(invoice).Overlay(invoice.AttrId("number"), "invoice_number")
                .Edit(t => t with { Columns = [t.Columns[0] with { TypeRef = new TypeRef { Ref = x.State.Id } }] });
        });

        var found = await f.FindingsAsync();

        Assert.Equal([("MQ4082", "/columns/0/typeRef"), ("MQ4082", "/columns/4/typeRef/ref")], At(found));
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("value object 'Address'", found.Single(d => d.JsonPointer!.EndsWith("/ref", StringComparison.Ordinal)).Message, StringComparison.Ordinal);
        Assert.Contains("overrides a projected column", found.Single(d => d.JsonPointer == "/columns/0/typeRef").Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MQ4082_on_a_binding_field_is_reported_on_the_reference_type()
    {
        // The table invoice_states holds the rows of InvoiceState; its code column also names the type, which the binding already says.
        var f = new Fixture(more: x =>
        {
            var table = x.B.Table("invoice_states", x.Main).Column("code", "string", nullable: false, length: 1).Column("label", "string", nullable: false, length: 40)
                .PrimaryKey("code").Edit(t => Typed(t, ("code", x.State.Id)));
            x.State = x.State with
            {
                Bindings =
                [
                    new ReferenceTypeBinding
                    {
                        Id = x.B.NewId(), Database = x.Main.Id, Table = table.Id,
                        Fields = [new BindingField { Attribute = "code", Column = table.ColumnId("code") }, new BindingField { Attribute = "label", Column = table.ColumnId("label") }],
                    },
                ],
            };
        });

        var finding = Assert.Single(await f.FindingsAsync());

        Assert.Equal(("MQ4082", "/bindings/0/fields/0/column", f.State.Bindings[0].Id), (finding.Rule, finding.JsonPointer, finding.ElementId));
        Assert.Equal(f.B.Build().GetDocument(f.State.Id)!.Path, finding.FilePath);
        Assert.Contains("the binding says what the column holds", finding.Message, StringComparison.Ordinal);
    }

    // ---- MQ4083 ----

    [Fact]
    public async Task A_column_that_cannot_hold_its_types_values_is_MQ4083()
    {
        var f = new Fixture(codeLength: 2, more: x =>
        {
            var wide = x.B.Enum("Wide").Member("Small", 1).Member("Huge", 70000);
            var named = x.B.Enum("Named").Member("Draft", 0, "draft").Member("Issued", 1);
            x.Events.Column("code_number", "int32").Column("wide", "int16").Column("named", "string", length: 3).Column("stamp", "uuid")
                .Column("price", "int64").Column("narrow", "decimal")
                .Edit(t => Typed(t, ("code_number", x.State.Id), ("wide", wide.Id), ("named", named.Id), ("stamp", x.Status.Id), ("price", x.Money.Id),
                    ("narrow", x.Money.Id)))
                .Edit(t => EditColumn(t, "narrow", c => c with { Precision = 10 }));
        });

        var found = await f.FindingsAsync();

        Assert.Equal(
            [
                ("MQ4083", "/columns/1/length"), // a code of 2 in a column of 1
                ("MQ4083", "/columns/4/type"),   // a text code in an int32 column
                ("MQ4083", "/columns/5/type"),   // 70000 outside int16
                ("MQ4083", "/columns/6/length"), // the stored codes draft and Issued in 3 characters
                ("MQ4083", "/columns/7/type"),   // an enum in a uuid column
                ("MQ4083", "/columns/8/type"),   // decimal in an int64 column
                ("MQ4083", "/columns/9/precision"),
            ],
            At(found));
        Assert.Contains("codes of up to 2 characters, more than its length 1", found.Single(d => d.JsonPointer == "/columns/1/length").Message, StringComparison.Ordinal);
        Assert.Contains("member 'Huge' has value 70000, outside the range of int16", found.Single(d => d.JsonPointer == "/columns/5/type").Message, StringComparison.Ordinal);
        Assert.Contains("its precision 10 is smaller than the type's 12", found.Single(d => d.JsonPointer == "/columns/9/precision").Message, StringComparison.Ordinal);

        // Leaving the facet out lets the column take it from its type.
        var fixedUp = new Fixture(codeLength: 2, editEvents: t => EditColumn(t, "state", c => c with { Length = null }));
        Assert.Empty(await fixedUp.FindingsAsync());
    }

    [Fact]
    public async Task An_inherited_typed_column_is_judged_on_the_table()
    {
        // A stereotype's column typed by InvoiceState, one character long while the codes have two: the table that carries the stereotype
        // gets the finding where 3.1 places member findings (its stereotypes entry).
        var f = new Fixture(codeLength: 2, editEvents: t => EditColumn(t, "state", c => c with { Length = null }), more: x =>
        {
            x.B.Add(new Stereotype
            {
                Id = x.B.NewId(), Key = "stateful", Name = "Stateful", AppliesTo = ["table"],
                Columns = [new Column { Id = x.B.NewId(), Name = "lifecycle", Type = "string", Length = 1, TypeRef = new TypeRef { Ref = x.State.Id }, Default = J("Z") }],
            });
            x.Events.Stereotype("stateful");
        });

        var found = await f.FindingsAsync();

        Assert.Equal([("MQ4083", "/stereotypes/0"), ("MQ7014", "/stereotypes/0")], At(found));
        Assert.All(found, d => Assert.Equal(f.Events.Id, d.ElementId));
        Assert.Contains("Column 'lifecycle' holds codes of reference type 'InvoiceState'", found.Single(d => d.Rule == "MQ4083").Message, StringComparison.Ordinal);
    }

    // ---- MQ4084 ----

    /// <summary>
    /// The fixture with InvoiceState bound to <c>invoice_states</c> (code, label) in main, a <c>notes</c> table, and the events table's
    /// foreign key from <c>state</c> to the table and columns <paramref name="target"/> picks.
    /// </summary>
    private static Fixture KeyFixture(Func<Fixture, TableBuilder, TableBuilder, (string Table, IReadOnlyList<string> Columns)>? target, bool bind = true) =>
        new(more: x =>
        {
            var states = x.B.Table("invoice_states", x.Main).Column("code", "string", nullable: false, length: 1).Column("label", "string", nullable: false, length: 40)
                .PrimaryKey("code").Edit(t => t with { Uniques = [new UniqueConstraint { Id = x.B.NewId(), Columns = [t.Columns[1].Id] }] });
            var notes = x.B.Table("notes", x.Main).Column("id", "string", nullable: false, length: 1).PrimaryKey("id");
            if (bind)
            {
                x.State = x.State with
                {
                    Bindings =
                    [
                        new ReferenceTypeBinding
                        {
                            Id = x.B.NewId(), Database = x.Main.Id, Table = states.Id,
                            Fields = [new BindingField { Attribute = "code", Column = states.ColumnId("code") }, new BindingField { Attribute = "label", Column = states.ColumnId("label") }],
                        },
                    ],
                };
            }

            if (target is not null)
            {
                var (table, columns) = target(x, states, notes);
                x.Events.Edit(t => t with
                {
                    ForeignKeys = [new ForeignKey { Id = "01K9X3C0000000000000000099", Name = "fk_event_state", Columns = [t.Columns[1].Id], ReferencesTable = table, ReferencesColumns = columns }],
                });
            }
        });

    [Fact]
    public async Task A_declared_key_into_the_bound_table_passes_MQ4084()
    {
        Assert.Empty(await KeyFixture((x, states, _) => (states.Id, [states.ColumnId("code")])).FindingsAsync());
        Assert.Empty(await KeyFixture((x, states, _) => (states.Id, [])).FindingsAsync()); // the primary key is the code column
        Assert.Empty(await KeyFixture(null).FindingsAsync());
    }

    [Fact]
    public async Task A_key_that_does_not_point_at_the_types_rows_is_MQ4084()
    {
        var elsewhere = Assert.Single(await KeyFixture((x, _, notes) => (notes.Id, [])).FindingsAsync());
        Assert.Equal(("MQ4084", "/foreignKeys/0", DiagnosticSeverity.Error), (elsewhere.Rule, elsewhere.JsonPointer, elsewhere.Severity));
        Assert.Contains("points at table 'notes': the type's rows live in table 'invoice_states'", elsewhere.Message, StringComparison.Ordinal);

        var label = Assert.Single(await KeyFixture((x, states, _) => (states.Id, [states.ColumnId("label")])).FindingsAsync());
        Assert.Contains("with column 'label' of table 'invoice_states', not with the type's code column 'code'", label.Message, StringComparison.Ordinal);

        var unbound = Assert.Single(await KeyFixture((x, states, _) => (states.Id, [states.ColumnId("code")]), bind: false).FindingsAsync());
        Assert.Contains("has no binding in database 'main'", unbound.Message, StringComparison.Ordinal);
    }

    // ---- MQ4085 ----

    /// <summary>
    /// The fixture with a <c>notes</c> table (id, body) whose body states <paramref name="column"/>, the entity <c>InvoiceNote</c> bound to
    /// it with a body attribute that states <paramref name="attribute"/>, and the entity <c>Invoice</c> projected with an overlay entry for
    /// its <c>email</c> (stating <paramref name="overlay"/>, its attribute <paramref name="attribute"/>).
    /// </summary>
    private static Fixture SensitivityFixture(Sensitivity? column, Sensitivity? attribute, Sensitivity? overlay, Func<ProjectSettings, ProjectSettings>? settings = null) =>
        new(more: x =>
        {
            var notes = x.B.Table("notes", x.Main).Column("id", "uuid", nullable: false).Column("body", "text").PrimaryKey("id")
                .Edit(t => EditColumn(t, "body", c => c with { Sensitive = column }));
            var note = x.B.Entity("InvoiceNote").Key("id", "uuid").Attr("body", "text", a => { if (attribute is { } s) a.Sensitive(s); });
            note.Bind(new EntityBinding
            {
                Id = "", Database = x.Main.Id, Source = notes.Id,
                Fields = [new BindingField { Attribute = note.AttrId("id"), Column = notes.ColumnId("id") }, new BindingField { Attribute = note.AttrId("body"), Column = "body" }],
            });
            var invoice = x.B.Entity("Invoice").Key("id", "uuid").Attr("email", "string", a => { if (attribute is { } s) a.Sensitive(s); });
            x.B.Table("invoices", x.Main).OverlayFor(invoice).Overlay(invoice.AttrId("email"))
                .Edit(t => t with { Columns = [t.Columns[0] with { Sensitive = overlay }] });
            if (settings is not null)
                x.B.Settings(settings);
        });

    [Fact]
    public async Task Different_sensitivities_on_a_column_and_its_attribute_are_MQ4085()
    {
        var found = await SensitivityFixture(Sensitivity.Pii, Sensitivity.Secret, Sensitivity.Pii).FindingsAsync();

        Assert.Equal([("MQ4085", "/bindings/0/fields/1"), ("MQ4085", "/columns/0/sensitive")], At(found));
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.Contains("Attribute 'body' of entity 'InvoiceNote' states sensitive 'secret', but column 'body'", found.Single(d => d.JsonPointer!.StartsWith("/bindings", StringComparison.Ordinal)).Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_side_or_equal_sensitivities_pass_MQ4085()
    {
        Assert.Empty(await SensitivityFixture(Sensitivity.Pii, null, null).FindingsAsync());
        Assert.Empty(await SensitivityFixture(null, Sensitivity.Secret, null).FindingsAsync());
        Assert.Empty(await SensitivityFixture(Sensitivity.Secret, Sensitivity.Secret, Sensitivity.Secret).FindingsAsync());
    }

    [Fact]
    public async Task MQ4085_takes_the_project_severity()
    {
        var found = await SensitivityFixture(Sensitivity.Pii, Sensitivity.Secret, null,
            s => s with { Validation = s.Validation with { Rules = ImmutableDictionary<string, string>.Empty.Add("MQ4085", "error") } }).FindingsAsync();

        var finding = Assert.Single(found);
        Assert.Equal(("MQ4085", DiagnosticSeverity.Error), (finding.Rule, finding.Severity));
    }

    // ---- MQ7014 ----

    [Fact]
    public async Task A_value_that_is_not_a_stored_value_or_code_is_MQ7014()
    {
        string? states = null;
        var f = new Fixture(editEvents: t => EditColumn(EditColumn(t, "state", c => c with { Default = J("Z") }), "status", c => c with { Default = J(5) }), more: x =>
        {
            // A table seed of the events, an entity bound to them with a constant, and a type bound to a table that holds a status.
            x.B.Add(new Seed
            {
                Id = x.B.NewId(), Name = "events", Target = x.Events.Id, Columns = [x.Events.ColumnId("id"), x.Events.ColumnId("state"), x.Events.ColumnId("status")],
                Rows =
                [
                    new SeedRow { Id = "01K9X3C0000000000000000101", Values = [J("6f9619ff-8b86-d011-b42d-00c04fc964ff"), J("D"), J(1)] },
                    new SeedRow { Id = "01K9X3C0000000000000000102", Values = [J("7f9619ff-8b86-d011-b42d-00c04fc964ff"), J("Z"), J(1)] },
                    new SeedRow { Id = "01K9X3C0000000000000000103", Values = [J("8f9619ff-8b86-d011-b42d-00c04fc964ff"), J("P"), J(9)] },
                ],
            });
            var invoiceEvent = x.B.Entity("InvoiceEvent").Key("id", "uuid");
            invoiceEvent.Bind(new EntityBinding
            {
                Id = "", Database = x.Main.Id, Source = x.Events.Id,
                Constants = [new BindingConstant { Column = "state", Value = J("X") }],
                Fields = [new BindingField { Attribute = invoiceEvent.AttrId("id"), Column = "id" }],
            });
            var kinds = x.B.Table("event_kinds", x.Main).Column("code", "string", nullable: false, length: 8).Column("label", "string", nullable: false, length: 40)
                .Column("status", "string", length: 8).PrimaryKey("code").Edit(t => Typed(t, ("status", x.Status.Id)));
            states = kinds.Id;
            var kind = new ReferenceType
            {
                Id = x.B.NewId(), Name = "EventKind",
                Code = new ReferenceCode { Id = x.B.NewId(), Length = 8 },
                Label = new ReferenceLabel { Id = x.B.NewId(), Length = 40 },
                Bindings =
                [
                    new ReferenceTypeBinding
                    {
                        Id = x.B.NewId(), Database = x.Main.Id, Table = kinds.Id,
                        Fields = [new BindingField { Attribute = "code", Column = "code" }, new BindingField { Attribute = "label", Column = "label" }],
                        Constants = [new BindingConstant { Column = "status", Value = J("Draft") }],
                    },
                ],
            };
            x.B.Add(kind);
        });

        var found = await f.FindingsAsync();

        Assert.Equal(
            [
                ("MQ7014", "/bindings/0/constants/0"), // InvoiceEvent's constant X
                ("MQ7014", "/bindings/0/constants/0"), // EventKind's constant Draft (a name, the member's code is D)
                ("MQ7014", "/columns/1/default"),
                ("MQ7014", "/columns/2/default"),
                ("MQ7014", "/rows/1/values/1"),       // Z
                ("MQ7014", "/rows/2/values/1"),       // P, a demo row only
                ("MQ7014", "/rows/2/values/2"),       // 9
            ],
            At(found));
        Assert.All(found, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.Contains("'Z' is not a code of its rows (reference type InvoiceState)", found.Single(d => d.JsonPointer == "/columns/1/default").Message, StringComparison.Ordinal);
        Assert.Contains("5 is not a value of a member of enum Status", found.Single(d => d.JsonPointer == "/columns/2/default").Message, StringComparison.Ordinal);
        Assert.Contains("that its seeds do not hold in every environment", found.Single(d => d.JsonPointer == "/rows/2/values/1").Message, StringComparison.Ordinal);
        Assert.NotNull(states);
    }
}
