using System.Text.Json;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Generation;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.Validation;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Resolution;

/// <summary>
/// Table per type (retire-projection-design.md section 4): a derived entity's binding names the foreign key of its own table that references
/// its base's table (<c>baseForeignKey</c>). Its key is the foreign key's columns; its reads join the base's table, up the chain; each base's
/// table renders its insert, update and delete for the derived entity, with the derived entity's type code; MQ4128 to MQ4135 report what goes
/// wrong at the JSON pointer of the binding in the entity's file.
/// </summary>
public sealed class BindingPerTypeTests
{
    /// <summary>
    /// <c>payments (id identity, kind, amount, reference, voided)</c> holds every payment, with its type code in kind. <c>Payment</c> (code
    /// <c>payment</c>) and <c>BankTransfer</c> (<c>bank-transfer</c>, in the same table) bind to it; <c>CardPayment</c> (<c>card</c>) binds to
    /// <c>card_payments (payment_id, last4)</c>, joined by its foreign key to payments; <c>CorporateCardPayment</c> (<c>corporate-card</c>)
    /// binds to <c>corporate_card_payments (payment_id, cost_center)</c>, joined to card_payments.
    /// </summary>
    internal sealed class Payments
    {
        public Payments(Func<Payments, string, EntityBinding, EntityBinding>? edit = null, Dialect dialect = Dialect.PostgreSql, bool typeColumn = true,
            bool soft = false, bool compositeCardKey = false, bool bankTransfer = true, bool cascade = true)
        {
            B = new ModelBuilder(seed: 61);
            var db = B.Database("main", dialect);
            Main = db.Id;
            (PaymentsTable, Id, Kind, Amount, Reference, Voided) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = PaymentsTable, Name = "payments", Database = Main,
                Columns =
                [
                    new Column { Id = Id, Name = "id", Type = "int64", Nullable = false, Generated = ColumnGeneration.Identity },
                    new Column { Id = Kind, Name = "kind", Type = "string", Length = 32, Nullable = false },
                    new Column { Id = Amount, Name = "amount", Type = "decimal", Precision = 12, Scale = 2, Nullable = false },
                    new Column { Id = Reference, Name = "reference", Type = "string", Length = 64, Nullable = true },
                    new Column { Id = Voided, Name = "voided", Type = "bool", Nullable = false, Default = Json("false") },
                ],
                PrimaryKey = new PrimaryKey { Columns = [Id] },
            });
            (CardTable, CardId, Last4, CardKey) = (B.NewId(), B.NewId(), B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = CardTable, Name = "card_payments", Database = Main,
                Columns =
                [
                    new Column { Id = CardId, Name = "payment_id", Type = "int64", Nullable = false },
                    new Column { Id = Last4, Name = "last4", Type = "string", Length = 4, Nullable = false },
                ],
                PrimaryKey = new PrimaryKey { Columns = compositeCardKey ? [CardId, Last4] : [CardId] },
                ForeignKeys = [new ForeignKey { Id = CardKey, Name = "fk_card_payments_payment", Columns = [CardId], ReferencesTable = PaymentsTable, ReferencesColumns = [Id], OnDelete = cascade ? ReferentialAction.Cascade : ReferentialAction.NoAction }],
            });
            (CorporateTable, CorporateId, CostCenter, CorporateKey, CorporateToPayments) = (B.NewId(), B.NewId(), B.NewId(), B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = CorporateTable, Name = "corporate_card_payments", Database = Main,
                Columns =
                [
                    new Column { Id = CorporateId, Name = "payment_id", Type = "int64", Nullable = false },
                    new Column { Id = CostCenter, Name = "cost_center", Type = "string", Length = 32, Nullable = false },
                ],
                PrimaryKey = new PrimaryKey { Columns = [CorporateId] },
                ForeignKeys =
                [
                    new ForeignKey { Id = CorporateKey, Name = "fk_corporate_card_payments_card", Columns = [CorporateId], ReferencesTable = CardTable, ReferencesColumns = [CardId], OnDelete = cascade ? ReferentialAction.Cascade : ReferentialAction.NoAction },
                    new ForeignKey { Id = CorporateToPayments, Name = "fk_corporate_card_payments_payment", Columns = [CorporateId], ReferencesTable = PaymentsTable, ReferencesColumns = [Id] },
                ],
            });

            ListView = B.NewId();
            B.Add(new View
            {
                Id = ListView, Name = "payment_list", Database = Main, Body = new Dictionary<string, string> { ["*"] = "SELECT id AS \"Id\", amount AS \"Amount\" FROM payments" },
                Columns = [new ViewColumn { Name = "Id", Type = "int64", Nullable = false }, new ViewColumn { Name = "Amount", Type = "decimal", Nullable = false }],
            });

            Payment = B.Entity("Payment").Key("id", "int64", IdentityStrategy.DatabaseIdentity).Attr("amount", "decimal", a => a.Required());
            Payment.Bind(Edit(edit, "Payment", new EntityBinding
            {
                Id = "", Database = Main, Source = PaymentsTable, DiscriminatorColumn = typeColumn ? Kind : null, DiscriminatorValue = Json("\"payment\""),
                Fields = [Field(Payment, "id", Id), Field(Payment, "amount", Amount)],
                Columns = typeColumn
                    ? [new BindingColumn { Column = Reference, Status = BindingColumnStatus.Ignored }, .. soft ? Array.Empty<BindingColumn>() : [new BindingColumn { Column = Voided, Status = BindingColumnStatus.Ignored }]]
                    : [new BindingColumn { Column = Reference, Status = BindingColumnStatus.Ignored }, new BindingColumn { Column = Kind, Status = BindingColumnStatus.Ignored }, .. soft ? Array.Empty<BindingColumn>() : [new BindingColumn { Column = Voided, Status = BindingColumnStatus.Ignored }]],
                Delete = soft ? new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = Voided, Value = Json("true") } } : null,
            }));
            BankTransfer = B.Entity("BankTransfer").Base(Payment).Attr("reference", "string", a => a.Length(64).Required());
            if (typeColumn && bankTransfer)
            {
                BankTransfer.Bind(Edit(edit, "BankTransfer", new EntityBinding
                {
                    Id = "", Database = Main, Source = PaymentsTable, DiscriminatorValue = Json("\"bank-transfer\""),
                    Fields = [Field(Payment, "id", Id), Field(Payment, "amount", Amount), Field(BankTransfer, "reference", Reference)],
                    Columns = soft ? [] : [new BindingColumn { Column = Voided, Status = BindingColumnStatus.Ignored }],
                }));
            }

            CardPayment = B.Entity("CardPayment").Base(Payment).Attr("last4", "string", a => a.Length(4).Required());
            CardPayment.Bind(Edit(edit, "CardPayment", new EntityBinding
            {
                Id = "", Database = Main, Source = CardTable, BaseForeignKey = CardKey, DiscriminatorValue = Json("\"card\""),
                Fields = [Field(CardPayment, "last4", Last4)],
            }));
            CorporateCardPayment = B.Entity("CorporateCardPayment").Base(CardPayment).Attr("costCenter", "string", a => a.Length(32).Required());
            CorporateCardPayment.Bind(Edit(edit, "CorporateCardPayment", new EntityBinding
            {
                Id = "", Database = Main, Source = CorporateTable, BaseForeignKey = CorporateKey, DiscriminatorValue = Json("\"corporate-card\""),
                Fields = [Field(CorporateCardPayment, "costCenter", CostCenter)],
            }));
        }

        private EntityBinding Edit(Func<Payments, string, EntityBinding, EntityBinding>? edit, string entity, EntityBinding binding) =>
            edit is null ? binding : edit(this, entity, binding);

        public static BindingField Field(EntityBuilder declaring, string attribute, string column) => new() { Attribute = declaring.AttrId(attribute), Column = column };

        public static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

        public ModelBuilder B { get; }

        public string Main { get; }

        public string PaymentsTable { get; }

        public string Id { get; }

        public string Kind { get; }

        public string Amount { get; }

        public string Reference { get; }

        public string Voided { get; }

        public string ListView { get; }

        public string CardTable { get; }

        public string CardId { get; }

        public string Last4 { get; }

        public string CardKey { get; }

        public string CorporateTable { get; }

        public string CorporateId { get; }

        public string CostCenter { get; }

        public string CorporateKey { get; }

        public string CorporateToPayments { get; }

        public EntityBuilder Payment { get; }

        public EntityBuilder BankTransfer { get; }

        public EntityBuilder CardPayment { get; }

        public EntityBuilder CorporateCardPayment { get; }

        public ResolvedModel Resolve() => ResolutionKit.Resolve(B);
    }

    private static REntityBinding BindingOf(ResolvedModel model, string entity) => model.Entity(entity).Bindings["main"];

    private static string Sql(REntityBinding binding, string statement) => BindingSql.Render(binding, statement).Sql;

    private static string Sql(RBindingBase level, string statement) => BindingSql.Render(level, statement).Sql;

    private static void NoProblems(ResolvedModel model) =>
        Assert.DoesNotContain(model.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);

    [Fact]
    public void A_derived_binding_takes_its_key_from_the_base_foreign_key()
    {
        var payments = new Payments();
        var model = payments.Resolve();

        NoProblems(model);
        var card = BindingOf(model, "CardPayment");
        Assert.Equal("fk_card_payments_payment", card.BaseForeignKey!.Name);
        // The key is the foreign key's column, named after the base's key field; the field map holds only the entity's own.
        var key = Assert.Single(card.Key);
        Assert.Equal(("id", "payment_id", payments.Payment.AttrId("id")), (key.Name, key.ColumnName, key.Attribute!.Id));
        Assert.Equal((true, false, true, false), (key.IsKey, key.IsGenerated, key.InInsert, key.InUpdate));
        Assert.Same(key.SourceColumn, key.WriteColumn);
        Assert.Equal(["last4"], card.Fields.Select(f => f.Name));
        Assert.Empty(card.Generated);
        Assert.Equal(("field", "id"), (card.Columns[0].Status, card.Columns[0].Field!.Name));

        // Each base: the base's binding, the foreign key that joins it, and its type column with this entity's codes.
        var level = Assert.Single(card.Bases);
        Assert.Same(BindingOf(model, "Payment"), level.Binding);
        Assert.Same(card, level.Derived);
        Assert.Same(card.BaseForeignKey, level.ForeignKey);
        Assert.Equal(("kind", "card"), (level.DiscriminatorColumn!.ColumnName, level.DiscriminatorColumn.Value));
        Assert.Equal(new object[] { "card", "corporate-card" }, level.DiscriminatorColumn.Codes);
        Assert.Null(card.DiscriminatorColumn);

        // Up the chain: a corporate card payment's rows are in three tables.
        var corporate = BindingOf(model, "CorporateCardPayment");
        Assert.Equal(["CardPayment", "Payment"], corporate.Bases.Select(l => l.Binding.Entity.Name));
        Assert.Equal(["fk_corporate_card_payments_card", "fk_card_payments_payment"], corporate.Bases.Select(l => l.ForeignKey.Name));
        Assert.Null(corporate.Bases[0].DiscriminatorColumn);
        Assert.Equal(new object[] { "corporate-card" }, corporate.Bases[1].DiscriminatorColumn!.Codes);
        Assert.Contains("e:" + model.Entity("Payment").Id, corporate.Dependencies);
    }

    [Fact]
    public async Task Validation_accepts_a_table_per_type_hierarchy()
    {
        var result = await EngineServices.Create(ResolutionKit.Options).Validator.ValidateAsync(new Payments().B.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Reads_join_the_bases_up_the_chain_and_the_base_reads_every_row()
    {
        var model = new Payments().Resolve();

        Assert.Equal("SELECT b1.id AS id, b1.amount AS amount, t.last4 AS last4\nFROM public.card_payments t\nJOIN public.payments b1 ON b1.id = t.payment_id\nWHERE b1.kind IN ('card', 'corporate-card')",
            Sql(BindingOf(model, "CardPayment"), "select"));
        var corporate = BindingOf(model, "CorporateCardPayment");
        Assert.Equal("SELECT b2.id AS id, b2.amount AS amount, b1.last4 AS last4, t.cost_center AS costCenter\nFROM public.corporate_card_payments t\n"
            + "JOIN public.card_payments b1 ON b1.payment_id = t.payment_id\nJOIN public.payments b2 ON b2.id = b1.payment_id\n"
            + "WHERE b2.kind = 'corporate-card' AND t.payment_id = @id", Sql(corporate, "select-by-key"));
        Assert.Equal(["id"], BindingSql.Parameters(corporate, "select-by-key"));

        // The base reads every row of its table, the subtypes in their own tables included, without joining them.
        Assert.Equal("SELECT t.id AS id, t.amount AS amount\nFROM public.payments t\nWHERE t.kind IN ('payment', 'bank-transfer', 'card', 'corporate-card')",
            Sql(BindingOf(model, "Payment"), "select"));
        // The probe reads the entity's own table only.
        Assert.Equal("SELECT COUNT(*)\nFROM public.card_payments t\nWHERE t.payment_id = @id", Sql(BindingOf(model, "CardPayment"), "exists"));
    }

    [Fact]
    public void Each_base_table_renders_its_writes_for_the_derived_entity()
    {
        var corporate = BindingOf(new Payments().Resolve(), "CorporateCardPayment");
        var (card, payment) = (corporate.Bases[0], corporate.Bases[1]);

        // Insert: the root first (its generated key comes back), then each table down the chain, the key a parameter.
        Assert.Equal("INSERT INTO public.payments (amount, kind)\nVALUES (@amount, 'corporate-card')\nRETURNING id AS id", Sql(payment, "insert"));
        Assert.Equal("INSERT INTO public.card_payments (payment_id, last4)\nVALUES (@id, @last4)", Sql(card, "insert"));
        Assert.Equal("INSERT INTO public.corporate_card_payments (payment_id, cost_center)\nVALUES (@id, @costCenter)", Sql(corporate, "insert"));
        // Update: each table by key; the base's keeps the entity's codes.
        Assert.Equal("UPDATE public.payments SET amount = @amount\nWHERE id = @id AND kind = 'corporate-card'", Sql(payment, "update"));
        Assert.Equal("UPDATE public.card_payments SET last4 = @last4\nWHERE payment_id = @id", Sql(card, "update"));
        Assert.Equal("UPDATE public.corporate_card_payments SET cost_center = @costCenter\nWHERE payment_id = @id", Sql(corporate, "update"));
        // Delete: the entity's own row first, then up the chain.
        Assert.Equal("DELETE FROM public.corporate_card_payments\nWHERE payment_id = @id", Sql(corporate, "delete"));
        Assert.Equal("DELETE FROM public.card_payments\nWHERE payment_id = @id", Sql(card, "delete"));
        Assert.Equal("DELETE FROM public.payments\nWHERE id = @id AND kind = 'corporate-card'", Sql(payment, "delete"));
        // A base renders no select of its own: the derived binding's joins it.
        Assert.Equal("", Sql(payment, "select"));
        Assert.Equal("", Sql(payment, "exists"));
    }

    public static TheoryData<string, string> GeneratedKeys() => new()
    {
        { "postgresql", "INSERT INTO public.payments (amount, kind)\nVALUES (@amount, 'card')\nRETURNING id AS id" },
        { "sqlserver", "INSERT INTO dbo.payments (amount, kind)\nOUTPUT INSERTED.id AS id\nVALUES (@amount, N'card')" },
        { "mysql", "INSERT INTO payments (amount, kind)\nVALUES (@amount, 'card');\nSELECT LAST_INSERT_ID() AS id" },
        { "sqlite", "INSERT INTO payments (amount, kind)\nVALUES (@amount, 'card')\nRETURNING id AS id" },
        { "oracle", "INSERT INTO payments (amount, kind)\nVALUES (@amount, 'card')\nRETURNING id INTO @id" },
    };

    [Theory]
    [MemberData(nameof(GeneratedKeys))]
    public void The_base_insert_returns_the_generated_key_in_each_dialect(string dialect, string expected)
    {
        var parsed = dialect switch
        {
            "sqlserver" => Dialect.SqlServer,
            "mysql" => Dialect.MySql,
            "sqlite" => Dialect.Sqlite,
            "oracle" => Dialect.Oracle,
            _ => Dialect.PostgreSql,
        };
        var card = BindingOf(new Payments(dialect: parsed).Resolve(), "CardPayment");

        Assert.Equal(expected, Sql(card.Bases[0], "insert"));
        Assert.Equal(["amount"], BindingSql.Render(card.Bases[0], "insert").Parameters.Take(1));
        Assert.StartsWith("INSERT INTO ", Sql(card, "insert"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_soft_delete_is_the_bases()
    {
        var model = new Payments(soft: true).Resolve();
        var card = BindingOf(model, "CardPayment");

        NoProblems(model);
        Assert.Equal(("soft", "voided", true), (card.Delete, card.SoftDeleteColumn!.Name, card.SoftDeleteValue));
        // It marks the base's row and leaves the card row; reads leave the marked rows out.
        Assert.Equal("", Sql(card, "delete"));
        Assert.Equal("UPDATE public.payments SET voided = true\nWHERE id = @id AND kind IN ('card', 'corporate-card')", Sql(card.Bases[0], "delete"));
        Assert.EndsWith("WHERE b1.kind IN ('card', 'corporate-card') AND (b1.voided IS NULL OR b1.voided <> true)", Sql(card, "select"), StringComparison.Ordinal);
        var corporate = BindingOf(model, "CorporateCardPayment");
        Assert.Equal("soft", corporate.Delete);
        Assert.Equal(["", "", "UPDATE public.payments SET voided = true\nWHERE id = @id AND kind = 'corporate-card'"],
            new[] { Sql(corporate, "delete"), Sql(corporate.Bases[0], "delete"), Sql(corporate.Bases[1], "delete") });
    }

    [Fact]
    public void Without_a_type_column_the_base_reads_every_row_and_the_probe_tells_subtypes_apart()
    {
        var model = new Payments(typeColumn: false).Resolve();
        var card = BindingOf(model, "CardPayment");

        NoProblems(model);
        Assert.Equal("SELECT t.id AS id, t.amount AS amount\nFROM public.payments t", Sql(BindingOf(model, "Payment"), "select"));
        Assert.Null(card.Bases[0].DiscriminatorColumn);
        Assert.Equal("SELECT b1.id AS id, b1.amount AS amount, t.last4 AS last4\nFROM public.card_payments t\nJOIN public.payments b1 ON b1.id = t.payment_id", Sql(card, "select"));
        Assert.Equal("INSERT INTO public.payments (amount)\nVALUES (@amount)\nRETURNING id AS id", Sql(card.Bases[0], "insert"));
        Assert.Equal("SELECT COUNT(*)\nFROM public.card_payments t\nWHERE t.payment_id = @id", Sql(card, "exists"));
    }

    [Fact]
    public void Entities_in_their_own_tables_count_among_the_type_columns_members()
    {
        // Only table-per-type subtypes: the base's type column still tells rows apart (no MQ4123), and their codes are its codes.
        var model = new Payments(bankTransfer: false).Resolve();

        NoProblems(model);
        Assert.Equal(new object[] { "payment", "card", "corporate-card" }, BindingOf(model, "Payment").DiscriminatorColumn!.Codes);
    }

    [Fact]
    public void A_subtype_in_a_table_per_type_table_shares_its_bases()
    {
        // VirtualCardPayment keeps its rows in card_payments with CardPayment, without a foreign key of its own: it maps the key and the
        // card's columns there, and shares CardPayment's base, payments, where its type code tells its rows apart.
        var payments = new Payments();
        var virtualCard = payments.B.Entity("VirtualCardPayment").Base(payments.CardPayment);
        virtualCard.Bind(new EntityBinding
        {
            Id = "", Database = payments.Main, Source = payments.CardTable, DiscriminatorValue = Payments.Json("\"virtual-card\""),
            Fields = [Payments.Field(payments.Payment, "id", payments.CardId), Payments.Field(payments.CardPayment, "last4", payments.Last4)],
        });
        var model = payments.Resolve();

        NoProblems(model);
        var binding = BindingOf(model, "VirtualCardPayment");
        Assert.Null(binding.BaseForeignKey);
        Assert.Equal(["Payment"], binding.Bases.Select(l => l.Binding.Entity.Name));
        Assert.Equal("SELECT b1.amount AS amount, t.payment_id AS id, t.last4 AS last4\nFROM public.card_payments t\nJOIN public.payments b1 ON b1.id = t.payment_id\nWHERE b1.kind = 'virtual-card'",
            Sql(binding, "select"));
        Assert.Equal("INSERT INTO public.payments (amount, kind)\nVALUES (@amount, 'virtual-card')\nRETURNING id AS id", Sql(binding.Bases[0], "insert"));
        Assert.Equal("INSERT INTO public.card_payments (payment_id, last4)\nVALUES (@id, @last4)", Sql(binding, "insert"));
        Assert.Equal(new object[] { "card", "corporate-card", "virtual-card" }, BindingOf(model, "CardPayment").Bases[0].DiscriminatorColumn!.Codes);
    }

    [Fact]
    public void The_resolved_record_carries_the_key_and_the_bases()
    {
        var model = new Payments().Resolve();

        Editor.ContractCheck.Validate(model);
        var record = BindingViews.Record(BindingOf(model, "CorporateCardPayment"));
        Assert.NotNull(record.BaseForeignKey);
        var key = Assert.Single(record.Key!);
        Assert.Equal(("id", "payment_id", true), (key.Name, key.Column, key.IsKey));
        Assert.Equal([model.Entity("CardPayment").Id, model.Entity("Payment").Id], record.Bases!.Select(b => b.Entity));
        Assert.Equal("corporate-card", record.Bases![1].DiscriminatorColumn!.Value);
        Assert.Null(record.Bases[0].DiscriminatorColumn);
        // A binding without a base foreign key keeps its record.
        var payment = BindingViews.Record(BindingOf(model, "Payment"));
        Assert.Equal((null, null, null), (payment.BaseForeignKey, payment.Key, payment.Bases));
    }

    [Fact]
    public void Binding_an_entity_per_type_changes_no_table()
    {
        // Storage never points at the model: the tables resolve the same with the bindings as without them.
        static string Tables(ResolvedModel model) => string.Join("\n", model.Db("main").Tables.Where(t => t.Origin != "synthesized").Select(t =>
            t.Name + ":" + string.Join(",", t.Columns.Select(c => c.Name + " " + c.Type + (c.Nullable ? "?" : ""))) + ";" + string.Join(",", t.ForeignKeys.Select(f => f.Name)) + ";" + t.Checks.Count));
        var bound = new Payments().Resolve();
        var unbound = new Payments((s, entity, b) => b with { Database = s.B.NewId() }).Resolve();

        Assert.Equal(Tables(unbound), Tables(bound));
    }

    [Fact]
    public void The_drift_report_and_map_by_name_count_the_key_columns_as_named()
    {
        var payments = new Payments();
        var snapshot = payments.B.Build();
        var card = snapshot.Get<Entity>(payments.CardPayment.Id)!;

        Assert.Equal([payments.CardId], BindingDrift.BaseKeyColumnsOf(snapshot, card.Bindings[0]));
        Assert.Null(BindingDrift.TypeColumnOf(snapshot, card, card.Bindings[0]));
        Assert.DoesNotContain(BindingDrift.Of(snapshot, card, card.Bindings[0]).Added, a => a.Name == "payment_id");
    }

    public static TheoryData<string, string, string, string> Rules() => new()
    {
        { "fk-other-table", "CardPayment", "MQ4128", "/bindings/0/baseForeignKey" },
        { "fk-wrong-target", "CorporateCardPayment", "MQ4128", "/bindings/0/baseForeignKey" },
        { "partial-key", "CardPayment", "MQ4129", "/bindings/0/baseForeignKey" },
        { "no-base", "Payment", "MQ4130", "/bindings/0/baseForeignKey" },
        { "base-read-only", "CardPayment", "MQ4131", "/bindings/0/write" },
        { "inherited", "CardPayment", "MQ4132", "/bindings/0/fields/1/attribute" },
        { "same-table", "CardPayment", "MQ4133", "/bindings/0/baseForeignKey" },
        { "key-under-soft", "CardPayment", "MQ4134", "/bindings/0/delete" },
        { "soft-other-column", "CardPayment", "MQ4134", "/bindings/0/delete/soft/column" },
        { "soft-alone", "CardPayment", "MQ4134", "/bindings/0/delete" },
        { "none-under-soft", "CardPayment", "MQ4134", "/bindings/0/delete" },
        { "same-code", "CardPayment", "MQ4125", "/bindings/0/discriminatorValue" },
    };

    [Theory]
    [MemberData(nameof(Rules))]
    public void Each_table_per_type_rule_points_into_the_entity_file(string mistake, string entity, string rule, string pointer)
    {
        EntityBinding Break(Payments s, string name, EntityBinding b)
        {
            if (name != entity && !(mistake == "base-read-only" && name == "Payment"))
                return b;
            return mistake switch
            {
                "fk-other-table" => b with { BaseForeignKey = s.CorporateKey },
                "fk-wrong-target" => b with { BaseForeignKey = s.CorporateToPayments },
                "no-base" => b with { BaseForeignKey = s.CardKey },
                "base-read-only" when name == "Payment" => b with { Write = new BindingWrite { None = true } },
                "inherited" => b with { Fields = [.. b.Fields, Payments.Field(s.Payment, "amount", s.CardId)] },
                "same-table" => b with { Source = s.PaymentsTable },
                "key-under-soft" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.Key } },
                "none-under-soft" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.None } },
                "soft-other-column" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = s.Amount, Value = Payments.Json("0") } } },
                "soft-alone" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.Soft, Soft = new SoftDelete { Column = s.Voided, Value = Payments.Json("true") } } },
                "same-code" => b with { DiscriminatorValue = Payments.Json("\"bank-transfer\"") },
                _ => b,
            };
        }

        var payments = new Payments(Break, soft: mistake is "key-under-soft" or "soft-other-column" or "none-under-soft", compositeCardKey: mistake == "partial-key");
        var model = payments.Resolve();

        var id = entity switch { "Payment" => payments.Payment.Id, "CardPayment" => payments.CardPayment.Id, _ => payments.CorporateCardPayment.Id };
        var found = Assert.Single(model.Diagnostics, d => d.Rule == rule && d.ElementId == id);
        Assert.Equal((RuleCatalog.Get(rule).DefaultSeverity, pointer), (found.Severity, found.JsonPointer));
    }

    [Fact]
    public void A_binding_that_breaks_its_base_foreign_key_reports_no_missing_key_or_unmapped_attributes()
    {
        var model = new Payments((s, name, b) => name == "CardPayment" ? b with { BaseForeignKey = s.CorporateKey } : b).Resolve();

        var card = model.Entity("CardPayment").Id;
        Assert.DoesNotContain(model.Diagnostics, d => d.ElementId == card && d.Rule is "MQ4046" or "MQ4058");
        Assert.Empty(BindingOf(model, "CardPayment").Bases);
        Assert.Equal("", Sql(BindingOf(model, "CardPayment"), "select-by-key"));
    }

    [Fact]
    public async Task A_check_that_reads_a_bases_attribute_is_refused_and_one_on_its_own_is_lowered()
    {
        // The bindings fixture's CardPayment: a check that reads amount (in payments) and last4 (in card_payments), and one on last4 alone.
        using var repo = Expressions.ExpressionRuleTests.Repo();
        Expressions.ExpressionRuleTests.Edit(repo, ".maquettiste/model/entities/card-payment.json", e =>
        {
            e["checks"] = System.Text.Json.Nodes.JsonNode.Parse("""
                [ { "id": "01K9TPT0000000000000000C01", "name": "smallCards", "condition": { "op": "and", "args": [
                      { "op": "lt", "args": [{ "path": ["01K6BFX0000000000000000065"] }, { "value": 1000 }] },
                      { "op": "isNotNull", "args": [{ "path": ["01K6BFX0000000000000000071"] }] } ] } },
                  { "id": "01K9TPT0000000000000000C02", "name": "fourDigits", "condition": { "op": "isNotNull", "args": [{ "path": ["01K6BFX0000000000000000071"] }] } } ]
                """);
            ((System.Text.Json.Nodes.JsonObject)e["bindings"]![0]!)["checks"] = new System.Text.Json.Nodes.JsonArray("01K9TPT0000000000000000C01", "01K9TPT0000000000000000C02");
        });
        var store = new ModelStore(repo.Options);
        var diagnostics = (await store.ValidateAsync(ValidationScope.All, TestContext.Current.CancellationToken)).Diagnostics;

        var refused = Assert.Single(diagnostics, d => d.Rule == "MQ4135");
        Assert.Equal(("01K6BFX0000000000000000072", "/bindings/0/checks/0"), (refused.ElementId, refused.JsonPointer)); // the binding, as MQ3110
        Assert.DoesNotContain(diagnostics, d => d.Rule == "MQ3110");
        var resolved = await new ModelResolver(repo.Options).ResolveAsync(await store.GetSnapshotAsync(TestContext.Current.CancellationToken), null, TestContext.Current.CancellationToken);
        var cards = resolved.Db("main").Table("card_payments");
        Assert.Equal(["ck_card_payments_four_digits"], cards.Checks.Select(c => c.Name));
    }

    [Fact]
    public void A_relation_with_several_types_finds_a_table_per_type_subtype_in_its_bases_rows()
    {
        // A note attached to a Payment or a Refund, by a type and an id: card payments are payments, whose rows they share through
        // their base foreign key, so the payments table holds their links too, and they are no target of their own.
        var payments = new Payments();
        var b = payments.B;
        var (refunds, refundId, notes, noteId, targetType, targetId) = (b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId(), b.NewId());
        b.Add(new Table { Id = refunds, Name = "refunds", Database = payments.Main, Columns = [new Column { Id = refundId, Name = "id", Type = "int64", Nullable = false }], PrimaryKey = new PrimaryKey { Columns = [refundId] } });
        b.Add(new Table
        {
            Id = notes, Name = "payment_notes", Database = payments.Main,
            Columns =
            [
                new Column { Id = noteId, Name = "id", Type = "int64", Nullable = false },
                new Column { Id = targetType, Name = "target_type", Type = "string", Length = 32, Nullable = false },
                new Column { Id = targetId, Name = "target_id", Type = "int64", Nullable = false },
            ],
            PrimaryKey = new PrimaryKey { Columns = [noteId] },
        });
        var refund = b.Entity("Refund").Key("id", "int64");
        refund.Bind(new EntityBinding { Id = "", Database = payments.Main, Source = refunds, Fields = [Payments.Field(refund, "id", refundId)] });
        var note = b.Entity("PaymentNote").Key("id", "int64");
        var relation = b.Relation("AttachedTo", note, payments.Payment, toMax: MaxCardinality.One, toMin: 1, toRole: "attachedTo")
            .WithEnd(1, e => e with { Entity = null, Types = [new RelationEndType { Entity = payments.Payment.Id }, new RelationEndType { Entity = refund.Id }] });
        note.Bind(new EntityBinding
        {
            Id = "", Database = payments.Main, Source = notes,
            Fields =
            [
                Payments.Field(note, "id", noteId), new BindingField { Attribute = relation.EndIds[1], Column = targetId },
                new BindingField { Attribute = relation.EndIds[1] + ".type", Column = targetType },
            ],
        });
        b.Add(new Mapping
        {
            Id = b.NewId(), Name = "AttachedTo in main", Database = payments.Main, Relation = relation.Id,
            Ends = [new RelationEndMapping { End = relation.EndIds[1], TypeColumn = targetType, IdColumn = targetId }],
        });
        var model = payments.Resolve();

        Assert.DoesNotContain(model.Diagnostics, d => d.Rule is "MQ4117" or "MQ4120");
        var mapping = model.Relations.Single(r => r.Name == "AttachedTo").Mappings["main"];
        Assert.Equal(["Payment", "Refund"], mapping.Links.Select(l => l.Ends[1].Type.Name));
        Assert.Equal(["payments", "refunds"], mapping.Links.Select(l => l.Ends[1].ReferencedTable!.Name));
    }

    // ---- the review's fixes ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Under_a_soft_deleting_base_every_binding_reads_without_the_marked_rows()
    {
        // CardPayment opts out of deleting (MQ4134) and CorporateCardPayment follows the root, not CardPayment; a read-only subtype
        // reports nothing and keeps the filter.
        var payments = new Payments((s, name, b) => name switch
        {
            "CardPayment" => b with { Delete = new BindingDelete { Mode = BindingDeleteMode.None } },
            _ => b,
        }, soft: true);
        var readOnly = payments.B.Entity("VoidedCardPayment").Base(payments.Payment).Attr("last4", "string", a => a.Length(4).Required());
        readOnly.Bind(new EntityBinding
        {
            Id = "", Database = payments.Main, Source = payments.CardTable, BaseForeignKey = payments.CardKey, Write = new BindingWrite { None = true },
            Fields = [Payments.Field(readOnly, "last4", payments.Last4)],
        });
        var model = payments.Resolve();

        var refused = Assert.Single(model.Diagnostics, d => d.Rule == "MQ4134");
        Assert.Equal((model.Entity("CardPayment").Id, "/bindings/0/delete"), (refused.ElementId, refused.JsonPointer));
        const string Kept = "(b1.voided IS NULL OR b1.voided <> true)";
        var card = BindingOf(model, "CardPayment");
        Assert.Equal(("none", "voided"), (card.Delete, card.SoftDeleteColumn!.Name));
        Assert.EndsWith(Kept, Sql(card, "select"), StringComparison.Ordinal);
        Assert.Equal("", Sql(card.Bases[0], "delete"));
        var corporate = BindingOf(model, "CorporateCardPayment");
        Assert.Equal("soft", corporate.Delete);
        Assert.EndsWith("(b2.voided IS NULL OR b2.voided <> true)", Sql(corporate, "select"), StringComparison.Ordinal);
        var voided = BindingOf(model, "VoidedCardPayment");
        Assert.Equal(("none", false), (voided.Delete, voided.Writes));
        Assert.EndsWith(Kept, Sql(voided, "select"), StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>VirtualCardPayment</c> shares <c>card_payments</c> with <c>CardPayment</c>, without a base foreign key of its own (as in
    /// <see cref="A_subtype_in_a_table_per_type_table_shares_its_bases"/>), with an optional delete of its own.
    /// </summary>
    private static (Payments Payments, ResolvedModel Model) VirtualCard(Dialect dialect = Dialect.PostgreSql, bool soft = true, BindingDelete? delete = null, bool readOnly = false)
    {
        var payments = new Payments(dialect: dialect, soft: soft);
        var virtualCard = payments.B.Entity("VirtualCardPayment").Base(payments.CardPayment);
        virtualCard.Bind(new EntityBinding
        {
            Id = "", Database = payments.Main, Source = payments.CardTable, DiscriminatorValue = Payments.Json("\"virtual-card\""), Delete = delete,
            Write = readOnly ? new BindingWrite { None = true } : null,
            Fields = [Payments.Field(payments.Payment, "id", payments.CardId), Payments.Field(payments.CardPayment, "last4", payments.Last4)],
        });
        return (payments, payments.Resolve());
    }

    [Fact]
    public void A_subtype_sharing_a_table_per_type_table_follows_the_roots_soft_delete()
    {
        var (payments, model) = VirtualCard();

        NoProblems(model);
        var binding = BindingOf(model, "VirtualCardPayment");
        Assert.Equal(("soft", "voided", true), (binding.Delete, binding.SoftDeleteColumn!.Name, binding.SoftDeleteValue));
        // Its reads leave out the rows the root marked; its delete marks the root's row and removes none.
        Assert.Equal("SELECT b1.amount AS amount, t.payment_id AS id, t.last4 AS last4\nFROM public.card_payments t\nJOIN public.payments b1 ON b1.id = t.payment_id\n"
            + "WHERE b1.kind = 'virtual-card' AND (b1.voided IS NULL OR b1.voided <> true)", Sql(binding, "select"));
        Assert.EndsWith("AND (b1.voided IS NULL OR b1.voided <> true) AND t.payment_id = @id", Sql(binding, "select-by-key"), StringComparison.Ordinal);
        Assert.Equal("", Sql(binding, "delete"));
        Assert.Equal("UPDATE public.payments SET voided = true\nWHERE id = @id AND kind = 'virtual-card'", Sql(binding.Bases[0], "delete"));

        // A delete by key under the soft root is refused, as for a binding with its own base foreign key; a read-only one reads the same way.
        var (_, refusedModel) = VirtualCard(delete: new BindingDelete { Mode = BindingDeleteMode.Key });
        var refused = Assert.Single(refusedModel.Diagnostics, d => d.Rule == "MQ4134");
        Assert.Equal((refusedModel.Entity("VirtualCardPayment").Id, "/bindings/0/delete"), (refused.ElementId, refused.JsonPointer));
        var readOnly = BindingOf(VirtualCard(readOnly: true).Model, "VirtualCardPayment");
        Assert.Equal(("none", "voided"), (readOnly.Delete, readOnly.SoftDeleteColumn!.Name));
        Assert.EndsWith("(b1.voided IS NULL OR b1.voided <> true)", Sql(readOnly, "select"), StringComparison.Ordinal);
        Assert.Equal("", Sql(readOnly.Bases[0], "delete"));

        // Under a root that deletes by key, it deletes by key through each table, as before.
        var hard = BindingOf(VirtualCard(soft: false).Model, "VirtualCardPayment");
        Assert.Equal(("key", null), (hard.Delete, hard.SoftDeleteColumn));
        Assert.Equal("DELETE FROM public.payments\nWHERE id = @id AND kind = 'virtual-card'", Sql(hard.Bases[0], "delete"));
        Assert.Equal("DELETE FROM public.card_payments\nWHERE payment_id = @id", Sql(hard, "delete"));

        // It depends on whatever its base's binding read (the files and tables above it), so an edit there renders it again.
        var card = BindingOf(model, "CardPayment");
        Assert.Empty(card.Dependencies.Except(binding.Dependencies));
        Assert.Contains(binding.Dependencies, d => d.Contains(payments.PaymentsTable, StringComparison.Ordinal));
    }

    public static TheoryData<string, string, string> SharedSoftDeletes() => new()
    {
        { "postgresql", "UPDATE public.payments SET voided = true\nWHERE id = @id AND kind = 'virtual-card'", "WHERE b1.kind = 'virtual-card' AND (b1.voided IS NULL OR b1.voided <> true)" },
        { "sqlserver", "UPDATE dbo.payments SET voided = 1\nWHERE id = @id AND kind = N'virtual-card'", "WHERE b1.kind = N'virtual-card' AND (b1.voided IS NULL OR b1.voided <> 1)" },
        { "mysql", "UPDATE payments SET voided = 1\nWHERE id = @id AND kind = 'virtual-card'", "WHERE b1.kind = 'virtual-card' AND (b1.voided IS NULL OR b1.voided <> 1)" },
        { "sqlite", "UPDATE payments SET voided = 1\nWHERE id = @id AND kind = 'virtual-card'", "WHERE b1.kind = 'virtual-card' AND (b1.voided IS NULL OR b1.voided <> 1)" },
        { "oracle", "UPDATE payments SET voided = 1\nWHERE id = @id AND kind = 'virtual-card'", "WHERE b1.kind = 'virtual-card' AND (b1.voided IS NULL OR b1.voided <> 1)" },
    };

    [Theory]
    [MemberData(nameof(SharedSoftDeletes))]
    public void A_subtype_sharing_a_table_per_type_table_marks_the_roots_row_in_each_dialect(string dialect, string delete, string filter)
    {
        var parsed = dialect switch
        {
            "sqlserver" => Dialect.SqlServer,
            "mysql" => Dialect.MySql,
            "sqlite" => Dialect.Sqlite,
            "oracle" => Dialect.Oracle,
            _ => Dialect.PostgreSql,
        };
        var binding = BindingOf(VirtualCard(parsed).Model, "VirtualCardPayment");

        Assert.Equal(delete, Sql(binding.Bases[0], "delete"));
        Assert.Equal("", Sql(binding, "delete"));
        Assert.EndsWith(filter, Sql(binding, "select"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_base_foreign_key_that_does_not_cascade_under_a_hard_deleting_base_is_a_warning()
    {
        var model = new Payments(cascade: false).Resolve();

        var warned = model.Diagnostics.Where(d => d.Rule == "MQ4136").ToList();
        Assert.Equal([model.Entity("CardPayment").Id, model.Entity("CorporateCardPayment").Id], warned.Select(d => d.ElementId).Order(StringComparer.Ordinal).ToList()
            .OrderBy(id => id == model.Entity("CardPayment").Id ? 0 : 1));
        Assert.All(warned, d => Assert.Equal((DiagnosticSeverity.Warning, "/bindings/0/baseForeignKey"), (d.Severity, d.JsonPointer)));
        // A soft delete through the base removes no row, so no cascade is needed.
        Assert.DoesNotContain(new Payments(cascade: false, soft: true).Resolve().Diagnostics, d => d.Rule == "MQ4136");
    }

    [Fact]
    public void A_base_read_from_a_view_joins_on_the_views_column_names()
    {
        // Payment reads the view payment_list, whose columns are "Id" and "Amount", and writes payments.
        var payments = new Payments((s, name, b) => name == "Payment"
            ? b with { Source = s.ListView, Write = new BindingWrite { Table = s.PaymentsTable }, Fields = [Payments.Field(s.Payment, "id", "id"), Payments.Field(s.Payment, "amount", "amount")] }
            : b, typeColumn: false);
        var model = payments.Resolve();

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal("SELECT b1.Id AS id, b1.Amount AS amount, t.last4 AS last4\nFROM public.card_payments t\nJOIN public.payment_list b1 ON b1.Id = t.payment_id",
            Sql(BindingOf(model, "CardPayment"), "select"));
    }

    /// <summary>Payment reads the query PaymentRows and CardPayment the query CardRows, each with a named query <c>src</c> and a parameter.</summary>
    private static Payments QueryBases(Dialect dialect = Dialect.PostgreSql)
    {
        string? paymentRows = null, cardRows = null;
        var payments = new Payments((s, name, b) => name switch
        {
            "Payment" => b with { Source = paymentRows ??= Query(s, PaymentRows(s)), Write = new BindingWrite { Table = s.PaymentsTable }, Fields = [Payments.Field(s.Payment, "id", "id"), Payments.Field(s.Payment, "amount", "amount")] },
            "CardPayment" => b with { Source = cardRows ??= Query(s, CardRows(s)), Write = new BindingWrite { Table = s.CardTable }, Fields = [Payments.Field(s.CardPayment, "last4", "last4")] },
            _ => b,
        }, dialect: dialect, typeColumn: false);
        return payments;

        static string PaymentRows(Payments s) => $$"""
            { "name": "PaymentRows", "parameters": [ { "name": "minAmount", "type": "decimal" } ],
              "with": [ { "name": "src", "from": { "source": "{{s.PaymentsTable}}", "alias": "p" },
                "select": [ { "name": "id", "expression": { "column": "p.id" } }, { "name": "amount", "expression": { "column": "p.amount" } } ],
                "where": { "op": "ge", "left": { "column": "p.amount" }, "right": { "param": "minAmount" } } } ],
              "from": { "source": "src", "alias": "s" },
              "select": [ { "name": "id", "expression": { "column": "s.id" } }, { "name": "amount", "expression": { "column": "s.amount" } } ] }
            """;

        static string CardRows(Payments s) => $$"""
            { "name": "CardRows", "parameters": [ { "name": "skip", "type": "string" } ],
              "with": [ { "name": "src", "from": { "source": "{{s.CardTable}}", "alias": "c" },
                "select": [ { "name": "paymentId", "expression": { "column": "c.payment_id" } }, { "name": "last4", "expression": { "column": "c.last4" } } ],
                "where": { "op": "ne", "left": { "column": "c.last4" }, "right": { "param": "skip" } } } ],
              "from": { "source": "src", "alias": "s" },
              "select": [ { "name": "payment_id", "expression": { "column": "s.paymentId" } }, { "name": "last4", "expression": { "column": "s.last4" } } ] }
            """;
    }

    private static string Query(Payments s, string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var id = s.B.NewId();
        node["id"] = id;
        node["database"] = s.Main;
        s.B.Add(node.Deserialize<Query>(EngineJson.Options)!);
        return id;
    }

    public static TheoryData<string, string> Dialects() => new()
    {
        { "postgresql", "public." }, { "sqlserver", "[public]." }, { "mysql", "public." }, { "sqlite", "" }, { "oracle", "\"public\"." },
    };

    [Theory]
    [MemberData(nameof(Dialects))]
    public void Bases_read_from_queries_put_every_named_query_first_and_number_each_parameter_once(string dialect, string schema)
    {
        // Two levels' named queries are both called src: the root's becomes src_b2. Each level's parameter keeps its own number.
        var model = QueryBases().Resolve();
        var text = BindingSql.Render(BindingOf(model, "CorporateCardPayment"), "select-by-key", dialect, new QuerySqlOptions { Placeholder = "$" });

        Assert.DoesNotContain(model.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Equal($"""
            WITH src (paymentId, last4) AS (
                SELECT c.payment_id AS paymentId, c.last4 AS last4
                FROM {schema}card_payments c
                WHERE c.last4 <> $1
            ),
            src_b2 (id, amount) AS (
                SELECT p.id AS id, p.amount AS amount
                FROM {schema}payments p
                WHERE p.amount >= $2
            )
            SELECT b2.id AS id, b2.amount AS amount, b1.last4 AS last4, t.cost_center AS costCenter
            FROM {schema}corporate_card_payments t
            JOIN (
            SELECT s.paymentId AS payment_id, s.last4 AS last4
            FROM src s
            ) b1 ON b1.payment_id = t.payment_id
            JOIN (
            SELECT s.id AS id, s.amount AS amount
            FROM src_b2 s
            ) b2 ON b2.id = b1.payment_id
            WHERE t.payment_id = $3
            """.ReplaceLineEndings("\n"), text.Sql);
        Assert.Equal(["skip", "minAmount", "id"], text.Parameters);
        // Named placeholders keep their names.
        Assert.Contains("WHERE p.amount >= :minAmount", BindingSql.Render(BindingOf(model, "CorporateCardPayment"), "select", dialect, new QuerySqlOptions { Placeholder = ":" }).Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_preview_carries_the_probe_and_each_bases_statements_by_level()
    {
        var model = new Payments().Resolve();
        var (preview, diagnostics) = BindingViews.Preview(BindingOf(model, "CorporateCardPayment"), null, null);

        Assert.Empty(diagnostics);
        Assert.Equal("SELECT COUNT(*)\nFROM public.corporate_card_payments t\nWHERE t.payment_id = @id", preview.Exists!.Sql);
        Assert.Equal([(1, "CardPayment"), (2, "Payment")], preview.Bases!.Select(b => (b.Level, model.Entities.Single(e => e.Id == b.EntityId).Name)));
        Assert.Equal("INSERT INTO public.payments (amount, kind)\nVALUES (@amount, 'corporate-card')\nRETURNING id AS id", preview.Bases![1].Insert!.Sql);
        Assert.Equal(["id"], preview.Bases[1].Delete!.Parameters);
        Assert.Null(BindingViews.Preview(BindingOf(model, "Payment"), null, null).Preview.Bases);
    }

    [Fact]
    public void The_drift_report_and_map_by_name_count_a_relation_attribute_column_as_the_relations()
    {
        var invoices = new Invoices();
        var snapshot = invoices.B.Build();
        var invoice = snapshot.Get<Entity>(invoices.InvoiceEntity)!;

        Assert.Equal([invoices.TermsNote], BindingDrift.RelationAttributeColumnsOf(snapshot, snapshot.Get<Table>(invoices.InvoiceTable)!));
        Assert.DoesNotContain(BindingDrift.Of(snapshot, invoice, invoice.Bindings[0]).Added, a => a.Name == "terms_note");
    }

    [Fact]
    public async Task A_check_on_a_subtype_sharing_a_table_per_type_table_is_refused_when_it_reads_a_bases_attribute()
    {
        // VirtualCardPayment shares card_payments with CardPayment, without a base foreign key: its reads join payments all the same.
        var payments = new Payments();
        var (virtualId, bindingId, checkId) = (payments.B.NewId(), payments.B.NewId(), payments.B.NewId());
        payments.B.Add(new Entity
        {
            Id = virtualId, Name = "VirtualCardPayment", Base = payments.CardPayment.Id,
            Checks = [new EntityCheck { Id = checkId, Name = "small", Condition = System.Text.Json.JsonSerializer.Deserialize<Expr>(
                $$"""{ "op": "lt", "args": [{ "path": ["{{payments.Payment.AttrId("amount")}}"] }, { "value": 100 }] }""", EngineJson.Options)! }],
            Bindings =
            [
                new EntityBinding
                {
                    Id = bindingId, Database = payments.Main, Source = payments.CardTable, Checks = [checkId],
                    Fields = [Payments.Field(payments.Payment, "id", payments.CardId), Payments.Field(payments.CardPayment, "last4", payments.Last4)],
                },
            ],
        });
        var result = await EngineServices.Create(ResolutionKit.Options).Validator.ValidateAsync(payments.B.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);

        var refused = Assert.Single(result.Diagnostics, d => d.Rule == "MQ4135");
        Assert.Equal((bindingId, "/bindings/0/checks/0"), (refused.ElementId, refused.JsonPointer));
        Assert.DoesNotContain(result.Diagnostics, d => d.Rule == "MQ3110");
    }

    [Fact]
    public void The_type_column_read_from_files_stops_where_the_resolver_does()
    {
        // A binding with a base foreign key in its base's own table (MQ4133) is in that table, so it takes the base's column; a subtype in
        // its base's table that names another column (MQ4126) takes the base's too.
        var payments = new Payments((s, name, b) => name switch
        {
            "CardPayment" => b with { Source = s.PaymentsTable },
            "BankTransfer" => b with { DiscriminatorColumn = s.Reference },
            _ => b,
        });
        var snapshot = payments.B.Build();
        var model = payments.Resolve();
        foreach (var name in new[] { "CardPayment", "BankTransfer", "CorporateCardPayment" })
        {
            var entity = snapshot.Get<Entity>(model.Entity(name).Id)!;
            var expected = BindingOf(model, name).DiscriminatorColumn is { } d ? payments.Kind : null;
            Assert.Equal(expected, BindingDrift.TypeColumnOf(snapshot, entity, entity.Bindings[0]));
        }

        Assert.Equal(payments.Kind, BindingDrift.TypeColumnOf(snapshot, snapshot.Get<Entity>(payments.CardPayment.Id)!, snapshot.Get<Entity>(payments.CardPayment.Id)!.Bindings[0]));
        Assert.Null(BindingDrift.TypeColumnOf(snapshot, snapshot.Get<Entity>(payments.CorporateCardPayment.Id)!, snapshot.Get<Entity>(payments.CorporateCardPayment.Id)!.Bindings[0]));
    }

    // ---- relation attributes on a foreign key --------------------------------------------------------------------------------

    /// <summary>
    /// <c>customers (id)</c> and <c>invoices (id, customer_id, terms_note)</c> with the foreign key <c>fk_invoices_customer</c>; the relation
    /// CustomerInvoices (one customer, many invoices) has the attribute <c>termsNote</c>, which its mapping stores in <c>invoices.terms_note</c>.
    /// </summary>
    private sealed class Invoices
    {
        public Invoices(Func<Invoices, string?>? column = null)
        {
            B = new ModelBuilder(seed: 67);
            var db = B.Database("main", Dialect.PostgreSql);
            var (customers, customerId) = (B.NewId(), B.NewId());
            B.Add(new Table
            {
                Id = customers, Name = "customers", Database = db.Id,
                Columns = [new Column { Id = customerId, Name = "id", Type = "uuid", Nullable = false }],
                PrimaryKey = new PrimaryKey { Columns = [customerId] },
            });
            (InvoiceId, CustomerColumn, TermsNote, ForeignKey) = (B.NewId(), B.NewId(), B.NewId(), B.NewId());
            CustomersId = customerId;
            var invoices = B.NewId();
            InvoiceTable = invoices;
            B.Add(new Table
            {
                Id = invoices, Name = "invoices", Database = db.Id,
                Columns =
                [
                    new Column { Id = InvoiceId, Name = "id", Type = "uuid", Nullable = false },
                    new Column { Id = CustomerColumn, Name = "customer_id", Type = "uuid", Nullable = false },
                    new Column { Id = TermsNote, Name = "terms_note", Type = "string", Length = 200, Nullable = true },
                ],
                PrimaryKey = new PrimaryKey { Columns = [InvoiceId] },
                ForeignKeys = [new ForeignKey { Id = ForeignKey, Name = "fk_invoices_customer", Columns = [CustomerColumn], ReferencesTable = customers, ReferencesColumns = [customerId] }],
            });
            var customer = B.Entity("Customer").Key("id", "uuid");
            customer.Bind(new EntityBinding { Id = "", Database = db.Id, Source = customers, Fields = [new BindingField { Attribute = customer.AttrId("id"), Column = customerId }] });
            var invoice = B.Entity("Invoice").Key("id", "uuid");
            InvoiceEntity = invoice.Id;
            var relation = B.Relation("CustomerInvoices", customer, invoice, fromMax: MaxCardinality.One, fromMin: 1).Attr("termsNote", "string", a => a.Length(200));
            invoice.Bind(new EntityBinding
            {
                Id = "", Database = db.Id, Source = invoices,
                Fields = [new BindingField { Attribute = invoice.AttrId("id"), Column = InvoiceId }, new BindingField { Attribute = relation.EndIds[0], Column = CustomerColumn }],
            });
            Mapping = B.NewId();
            B.Add(new Mapping
            {
                Id = Mapping, Name = "CustomerInvoices in main", Database = db.Id, Relation = relation.Id, ForeignKey = ForeignKey,
                Attributes = [new AttributeMapping { Attribute = relation.AttrId("termsNote"), Column = column is null ? TermsNote : column(this) }],
            });
        }

        public ModelBuilder B { get; }

        public string Mapping { get; }

        public string InvoiceEntity { get; }

        public string InvoiceTable { get; }

        public string InvoiceId { get; }

        public string CustomersId { get; }

        public string CustomerColumn { get; }

        public string TermsNote { get; }

        public string ForeignKey { get; }
    }

    [Fact]
    public async Task A_relation_attribute_is_stored_on_the_foreign_keys_table()
    {
        var invoices = new Invoices();
        var model = ResolutionKit.Resolve(invoices.B);
        var validation = await EngineServices.Create(ResolutionKit.Options).Validator.ValidateAsync(invoices.B.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);

        NoProblems(model);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Severity is DiagnosticSeverity.Error or DiagnosticSeverity.Warning);
        var mapping = model.Relations.Single().Mappings["main"];
        Assert.Equal("fk_invoices_customer", mapping.ForeignKey!.Name);
        var stored = Assert.Single(mapping.Attributes);
        Assert.Equal(("termsNote", "terms_note", "invoices"), (stored.Attribute.Name, stored.Column.Name, stored.Column.Table.Name));
        // The column stays the table's; the invoice's binding counts it as the relation's.
        Assert.Null(stored.Column.Attribute);
        var binding = model.Entity("Invoice").Bindings["main"];
        Assert.Equal("relation", Assert.Single(binding.Columns, c => c.Name == "terms_note").Status);
        Assert.Contains("e:" + invoices.Mapping, binding.Dependencies);
    }

    [Theory]
    [InlineData("other-table")]
    [InlineData("key-column")]
    public async Task A_relation_attribute_column_off_the_foreign_keys_table_is_refused(string mistake)
    {
        var invoices = new Invoices(s => mistake == "other-table" ? s.CustomersId : s.CustomerColumn);
        var validation = await EngineServices.Create(ResolutionKit.Options).Validator.ValidateAsync(invoices.B.Build(), ValidationScope.All, null, TestContext.Current.CancellationToken);

        var found = Assert.Single(validation.Diagnostics, d => d.Rule == "MQ4009" && d.ElementId == invoices.Mapping);
        Assert.Equal("/attributes/0/column", found.JsonPointer);
        Assert.Empty(ResolutionKit.Resolve(invoices.B).Relations.Single().Mappings["main"].Attributes);
    }
}
