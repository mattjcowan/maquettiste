using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Resolution;
using Maquettiste.Engine.SchemaDiff;
using static Maquettiste.Engine.Tests.SchemaDiff.PhysicalBuilder;

namespace Maquettiste.Engine.Tests.SchemaDiff;

public sealed class SchemaDifferTests
{
    // Invoice's key sorts before customer's, so dependency order (customer first) is not key order.
    private const string InvoiceKey = "01JB2Q0M8X4T5V6W7Y8Z9A0B1A";
    private const string CustomerKey = "01JB2Q0M8X4T5V6W7Y8Z9A0B1B";
    private const string ViewId = "01JB2Q0M8X4T5V6W7Y8Z9A0B1D";
    private const string SequenceKey = InvoiceKey + ".sequence@" + DatabaseId;

    private readonly SchemaDiffer _differ = new();

    /// <summary>A fresh baseline database; <paramref name="change"/> edits it before it is returned.</summary>
    private static RDatabase Model(Action<RTable, RTable, List<RView>, List<RSequence>>? change = null, bool includeInvoice = true)
    {
        var customer = Table(CustomerKey, "customer",
            Column("id", "id", "uuid"),
            Column("a-name", "name", "string"),
            Column("a-email", "email", "string", nullable: true));
        customer.Uniques = [new RUnique { Name = "uq_customer_email", Columns = [customer.Col("a-email")] }];
        customer.Checks = [new RCheck { Name = "ck_customer_name", Expression = "length(name) > 0" }];

        var sequence = Sequence(SequenceKey, "invoice_seq");
        var invoice = Table(InvoiceKey, "invoice",
            Column("id", "id"),
            Column("end1.id", "customer_id", "uuid"),
            Column("a-total", "total", "decimal"));
        invoice.Col("id").Sequence = sequence;
        invoice.Col("a-total").Default = 0L;
        invoice.ForeignKeys = [ForeignKey(invoice, "end1.id", customer)];
        invoice.Indexes = [new RIndex { Name = "ix_invoice_customer_id", Columns = [new RIndexColumn { Column = invoice.Col("end1.id") }] }];

        var views = new List<RView> { View(ViewId, "v_invoice", "SELECT * FROM invoice") };
        var sequences = new List<RSequence> { sequence };
        change?.Invoke(customer, invoice, views, sequences);
        return Database("main", includeInvoice ? [invoice, customer] : [customer], views, sequences);
    }

    private PhysicalSnapshot Baseline(int revision = 3) => _differ.Capture(Model(), revision);

    [Fact]
    public void Capture_keys_and_sorts_everything()
    {
        var snapshot = Baseline();

        Assert.Equal(DatabaseId, snapshot.Database);
        Assert.Equal("main", snapshot.Name);
        Assert.Equal(Dialect.PostgreSql, snapshot.Dialect);
        Assert.Equal(3, snapshot.Revision);
        Assert.Equal([InvoiceKey, CustomerKey], snapshot.Tables.Select(t => t.Key));
        var invoice = snapshot.Tables[0];
        Assert.Equal(["id", "end1.id", "a-total"], invoice.Columns.Select(c => c.Key));
        Assert.Equal(SequenceKey, invoice.Columns[0].Sequence);
        Assert.Equal(0, invoice.Columns[2].Default!.Value.GetInt64());
        Assert.Equal("pk", invoice.PrimaryKey!.Key);
        Assert.Equal(["id"], invoice.PrimaryKey.Columns);
        var fk = Assert.Single(invoice.ForeignKeys);
        Assert.Equal(("fk:end1.id->" + CustomerKey + "(id)", CustomerKey, ReferentialAction.NoAction), (fk.Key, fk.ReferencedTable, fk.OnDelete));
        Assert.Equal(["id"], fk.ReferencedColumns);
        Assert.Equal("ix:end1.id", Assert.Single(invoice.Indexes).Key);
        var customer = snapshot.Tables[1];
        Assert.Equal("uq:a-email", Assert.Single(customer.Uniques).Key);
        Assert.Matches("^ck:[0-9a-f]{16}$", Assert.Single(customer.Checks).Key);
        Assert.Equal(ViewId, Assert.Single(snapshot.Views).Key);
        Assert.Equal(SequenceKey, Assert.Single(snapshot.Sequences).Key);
    }

    [Fact]
    public void Capture_derives_index_keys_from_every_distinguishing_property()
    {
        var db = Model((customer, _, _, _) => customer.Indexes =
        [
            new RIndex { Name = "ix_b", Columns = [new RIndexColumn { Column = customer.Col("a-name"), Descending = true }] },
            new RIndex { Name = "ix_a", Columns = [new RIndexColumn { Column = customer.Col("a-name"), Descending = true }], Unique = true },
            new RIndex
            {
                Name = "ix_c",
                Columns = [new RIndexColumn { Column = customer.Col("a-name") }],
                Include = [customer.Col("a-email")],
                Where = "email is not null",
                Method = "hash",
            },
        ]);

        var indexes = _differ.Capture(db, 0).Tables.Single(t => t.Key == CustomerKey).Indexes;

        Assert.Equal(
        [
            ("ix:a-name desc", "ix_b"),
            ("ix:a-name desc;unique", "ix_a"),
            ("ix:a-name;using=hash;include=a-email;where=" + Maquettiste.Engine.Hashing.ContentHash.Of("email is not null")[..16], "ix_c"),
        ], indexes.Select(i => (i.Key, i.Name)));
    }

    [Fact]
    public void Capture_disambiguates_identical_definitions_by_name()
    {
        var db = Model((customer, _, _, _) => customer.Indexes =
        [
            new RIndex { Name = "ix_b", Columns = [new RIndexColumn { Column = customer.Col("a-name"), Descending = true }] },
            new RIndex { Name = "ix_a", Columns = [new RIndexColumn { Column = customer.Col("a-name"), Descending = true }] },
        ]);

        var indexes = _differ.Capture(db, 0).Tables.Single(t => t.Key == CustomerKey).Indexes;

        Assert.Equal([("ix:a-name desc", "ix_a"), ("ix:a-name desc#2", "ix_b")], indexes.Select(i => (i.Key, i.Name)));
    }

    [Fact]
    public void Renaming_one_of_two_partial_indexes_on_the_same_columns_is_one_rename()
    {
        static Action<RTable, RTable, List<RView>, List<RSequence>> Partial(string deletedName) => (customer, _, _, _) => customer.Indexes =
        [
            new RIndex { Name = "ix_b_active", Columns = [new RIndexColumn { Column = customer.Col("a-email") }], Where = "active" },
            new RIndex { Name = deletedName, Columns = [new RIndexColumn { Column = customer.Col("a-email") }], Where = "deleted" },
        ];

        var diff = _differ.Diff(_differ.Capture(Model(Partial("ix_c_deleted")), 1), Model(Partial("ix_a_deleted")));

        var index = Assert.Single(Assert.Single(diff.Tables).Indexes);
        Assert.Equal((ChangeKind.Renamed, "ix_c_deleted", "ix_a_deleted"), (index.Kind, index.OldName, index.NewName));
        Assert.Empty(index.Changes);
    }

    [Fact]
    public void Two_foreign_keys_on_the_same_columns_keep_their_keys_by_target()
    {
        static Action<RTable, RTable, List<RView>, List<RSequence>> Fks(string toInvoiceName) => (customer, invoice, _, _) => customer.ForeignKeys =
        [
            ForeignKey(customer, "a-email", invoice, toInvoiceName),
            ForeignKey(customer, "a-email", customer, "fk_b_self"),
        ];

        var diff = _differ.Diff(_differ.Capture(Model(Fks("fk_c_invoice")), 1), Model(Fks("fk_a_invoice")));

        var fk = Assert.Single(diff.Tables.Single(t => t.Key == CustomerKey).ForeignKeys);
        Assert.Equal((ChangeKind.Renamed, "fk_c_invoice", "fk_a_invoice"), (fk.Kind, fk.OldName, fk.NewName));
        Assert.Empty(fk.Changes);
    }

    [Fact]
    public void First_diff_adds_everything_in_dependency_order()
    {
        var diff = _differ.Diff(null, Model());

        Assert.Equal("main", diff.Database);
        Assert.False(diff.IsEmpty);
        Assert.Equal((0, 1), (diff.FromRevision, diff.ToRevision));
        Assert.Equal([(ChangeKind.Added, CustomerKey), (ChangeKind.Added, InvoiceKey)], diff.Tables.Select(t => (t.Kind, t.Key)));
        Assert.Equal("customer", diff.Tables[0].Table!.Name);
        Assert.Null(diff.Tables[0].OldName);
        Assert.Equal("customer", diff.Tables[0].NewName);
        Assert.Equal(ChangeKind.Added, Assert.Single(diff.Views).Kind);
        Assert.Equal(ChangeKind.Added, Assert.Single(diff.Sequences).Kind);
    }

    [Fact]
    public void Unchanged_model_gives_an_empty_diff_with_a_stable_hash()
    {
        var first = _differ.Diff(Baseline(), Model());
        var second = _differ.Diff(Baseline(), Model());

        Assert.True(first.IsEmpty);
        Assert.Equal((3, 3), (first.FromRevision, first.ToRevision));
        Assert.Empty(first.Tables);
        Assert.Empty(first.Views);
        Assert.Empty(first.Sequences);
        Assert.Equal(first.Hash, second.Hash);
        Assert.Matches("^[0-9a-f]{64}$", first.Hash);
        Assert.NotEqual(first.Hash, _differ.Diff(Baseline(4), Model()).Hash);
    }

    [Fact]
    public void Table_rename_keeps_its_key_while_a_new_key_is_drop_and_add()
    {
        var renamed = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Name = "client"));
        var change = Assert.Single(renamed.Tables);
        Assert.Equal((ChangeKind.Renamed, CustomerKey, "customer", "client"), (change.Kind, change.Key, change.OldName, change.NewName));
        Assert.Equal("client", change.Table!.Name);
        Assert.Empty(change.Columns);
        Assert.Equal((3, 4), (renamed.FromRevision, renamed.ToRevision));

        const string newKey = "01JB2Q0M8X4T5V6W7Y8Z9A0B1Z";
        var replaced = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Key = newKey));
        Assert.Contains(replaced.Tables, t => t is { Kind: ChangeKind.Added, Key: newKey, NewName: "customer" });
        Assert.Contains(replaced.Tables, t => t is { Kind: ChangeKind.Dropped, Key: CustomerKey, OldName: "customer", NewName: null, Table: null });
        // The invoice's FK now points at the new key; the target is part of an FK's key, so it is a new FK (drop plus add).
        var invoice = Assert.Single(replaced.Tables, t => t.Kind == ChangeKind.Altered && t.Key == InvoiceKey);
        Assert.Equal([(ChangeKind.Added, "fk:end1.id->" + newKey + "(id)"), (ChangeKind.Dropped, "fk:end1.id->" + CustomerKey + "(id)")],
            invoice.ForeignKeys.Select(f => (f.Kind, f.Key)));
        Assert.Equal([ChangeKind.Added, ChangeKind.Altered, ChangeKind.Dropped], replaced.Tables.Select(t => t.Kind));
    }

    [Fact]
    public void Column_add_drop_and_rename_versus_drop_and_add()
    {
        var diff = _differ.Diff(Baseline(), Model((customer, _, _, _) =>
        {
            var columns = customer.Columns.ToList();
            columns.RemoveAt(2);                                         // drop email
            columns[1].Name = "full_name";                               // rename name (same key)
            columns.Add(Column("a-phone", "phone", "string", true));     // add phone
            columns.Add(Column("a-email2", "email", "string", true));    // same name, new key: drop + add
            for (var i = 0; i < columns.Count; i++)
            {
                columns[i].Table = customer;
                columns[i].Position = i;
            }

            customer.Columns = new RList<RColumn>(columns, []);
            customer.Uniques = [];
        }));

        var table = Assert.Single(diff.Tables);
        Assert.Equal(ChangeKind.Altered, table.Kind);
        Assert.Equal(
            [(ChangeKind.Added, "a-phone", null, "phone"), (ChangeKind.Added, "a-email2", null, "email"),
             (ChangeKind.Renamed, "a-name", "name", "full_name"), (ChangeKind.Dropped, "a-email", "email", null)],
            table.Columns.Select(c => (c.Kind, c.Key, c.OldName, c.NewName)));
        Assert.Equal("phone", table.Columns[0].Column!.Name);
        Assert.Null(table.Columns[3].Column);
        Assert.Empty(table.Columns[2].Changes);
        Assert.Equal((ChangeKind.Dropped, "uq:a-email"), (Assert.Single(table.Uniques).Kind, table.Uniques[0].Key));
    }

    [Fact]
    public void Column_alterations_list_every_changed_property()
    {
        var sequence2 = Sequence("01JB2Q0M8X4T5V6W7Y8Z9A0B1E", "other_seq");
        var diff = _differ.Diff(Baseline(), Model((customer, invoice, _, sequences) =>
        {
            var email = customer.Col("a-email");
            email.Nullable = false;
            email.Length = 320;
            email.NativeType = "varchar(320)";
            email.Comment = "Primary contact";
            email.Collation = "C";
            var total = invoice.Col("a-total");
            total.Default = 10L;
            total.Computed = "price * quantity";
            total.ComputedStored = true;
            var id = invoice.Col("id");
            id.Sequence = null;
            id.Identity = true;
            invoice.Col("end1.id").DefaultSql = "gen_random_uuid()";
            sequences.Add(sequence2);
        }));

        var customerChange = diff.Tables.Single(t => t.Key == CustomerKey);
        var email = Assert.Single(customerChange.Columns);
        Assert.Equal((ChangeKind.Altered, "email", "email"), (email.Kind, email.OldName, email.NewName));
        Assert.Equal(
            [("length", null, 320), ("nativeType", "text", "varchar(320)"), ("nullable", true, false), ("collation", null, "C"), ("comment", null, "Primary contact")],
            email.Changes.Select(c => (c.Property, c.Old, c.New)));

        var invoiceChange = diff.Tables.Single(t => t.Key == InvoiceKey);
        Assert.Equal(["id", "end1.id", "a-total"], invoiceChange.Columns.Select(c => c.Key));
        Assert.Equal([("identity", false, true), ("sequence", SequenceKey, null)],
            invoiceChange.Columns[0].Changes.Select(c => (c.Property, c.Old, c.New)));
        Assert.Equal([("defaultSql", null, "gen_random_uuid()")], invoiceChange.Columns[1].Changes.Select(c => (c.Property, c.Old, c.New)));
        Assert.Equal([("default", 0L, 10L), ("computed", null, "price * quantity"), ("computedStored", false, true)],
            invoiceChange.Columns[2].Changes.Select(c => (c.Property, c.Old, c.New)));
        Assert.Same(invoiceChange.Table!.Col("a-total"), invoiceChange.Columns[2].Column);
        Assert.Equal(ChangeKind.Added, Assert.Single(diff.Sequences).Kind);
    }

    [Fact]
    public void Keys_constraints_and_indexes()
    {
        var diff = _differ.Diff(Baseline(), Model((customer, invoice, _, _) =>
        {
            customer.PrimaryKey = new RPrimaryKey { Name = "pk_customer", Columns = [customer.Col("id"), customer.Col("a-name")] };
            customer.Uniques = [new RUnique { Name = "uq_customer_mail", Columns = [customer.Col("a-email")] },
                new RUnique { Name = "uq_customer_name", Columns = [customer.Col("a-name")] }];
            customer.Checks = [new RCheck { Name = "ck_customer_name", Expression = "length(name) > 1" }];
            var fk = invoice.ForeignKeys[0];
            fk.OnDelete = "cascade";
            invoice.Indexes = [new RIndex
            {
                Name = "ix_invoice_customer_id",
                Columns = [new RIndexColumn { Column = invoice.Col("end1.id") }],
                Include = [invoice.Col("a-total")],
                Where = "total > 0",
                Unique = true,
                Method = "btree",
            }];
        }));

        var customerChange = diff.Tables.Single(t => t.Key == CustomerKey);
        var pk = Assert.Single(customerChange.PrimaryKey);
        Assert.Equal((ChangeKind.Altered, "pk"), (pk.Kind, pk.Key));
        var pkColumns = Assert.Single(pk.Changes);
        Assert.Equal("columns", pkColumns.Property);
        Assert.Equal(["id"], (IEnumerable<string>)pkColumns.Old!);
        Assert.Equal(["id", "a-name"], (IEnumerable<string>)pkColumns.New!);
        Assert.Equal([(ChangeKind.Added, "uq:a-name", null, "uq_customer_name"), (ChangeKind.Renamed, "uq:a-email", "uq_customer_email", "uq_customer_mail")],
            customerChange.Uniques.Select(u => (u.Kind, u.Key, u.OldName, u.NewName)));
        // A changed check expression is a new check: drop plus add.
        Assert.Equal([ChangeKind.Added, ChangeKind.Dropped], customerChange.Checks.Select(c => c.Kind));

        var invoiceChange = diff.Tables.Single(t => t.Key == InvoiceKey);
        var fkChange = Assert.Single(invoiceChange.ForeignKeys);
        Assert.Equal([("onDelete", "no-action", "cascade")], fkChange.Changes.Select(c => (c.Property, c.Old, c.New)));
        // Uniqueness, method, included columns and the predicate form an index's key: changing them is a drop plus an add.
        Assert.Equal([(ChangeKind.Added, "ix_invoice_customer_id"), (ChangeKind.Dropped, "ix_invoice_customer_id")],
            invoiceChange.Indexes.Select(i => (i.Kind, i.NewName ?? i.OldName)));
        Assert.StartsWith("ix:end1.id;unique;using=btree;include=a-total;where=", invoiceChange.Indexes[0].Key, StringComparison.Ordinal);
        Assert.Equal("ix:end1.id", invoiceChange.Indexes[1].Key);
    }

    [Fact]
    public void Check_rename_keeps_its_key_and_a_new_fk_is_added()
    {
        var diff = _differ.Diff(Baseline(), Model((customer, invoice, _, _) =>
        {
            customer.Checks = [new RCheck { Name = "ck_client_name", Expression = "length(name) > 0" }];
            invoice.ForeignKeys = [.. invoice.ForeignKeys, ForeignKey(invoice, "a-total", invoice, "fk_self")];
        }));

        var check = Assert.Single(diff.Tables.Single(t => t.Key == CustomerKey).Checks);
        Assert.Equal((ChangeKind.Renamed, "ck_customer_name", "ck_client_name"), (check.Kind, check.OldName, check.NewName));
        var fk = Assert.Single(diff.Tables.Single(t => t.Key == InvoiceKey).ForeignKeys);
        Assert.Equal((ChangeKind.Added, "fk:a-total->" + InvoiceKey + "(id)", "fk_self"), (fk.Kind, fk.Key, fk.NewName));
    }

    [Fact]
    public void Dropped_tables_come_last_in_reverse_dependency_order()
    {
        var empty = Database("main", [], [], []);

        var diff = _differ.Diff(Baseline(), empty);

        Assert.Equal([(ChangeKind.Dropped, InvoiceKey), (ChangeKind.Dropped, CustomerKey)], diff.Tables.Select(t => (t.Kind, t.Key)));
        Assert.Equal(ChangeKind.Dropped, Assert.Single(diff.Views).Kind);
        Assert.Equal(ChangeKind.Dropped, Assert.Single(diff.Sequences).Kind);
        Assert.Equal("v_invoice", diff.Views[0].OldName);
    }

    [Fact]
    public void Added_tables_with_a_cycle_order_deterministically()
    {
        var a = Table("A", "a", Column("id", "id"), Column("b_id", "b_id"));
        var b = Table("B", "b", Column("id", "id"), Column("a_id", "a_id"));
        var c = Table("C", "c", Column("id", "id"), Column("a_id", "a_id"));
        a.ForeignKeys = [ForeignKey(a, "b_id", b)];
        b.ForeignKeys = [ForeignKey(b, "a_id", a)];
        c.ForeignKeys = [ForeignKey(c, "a_id", a)];

        var diff = _differ.Diff(null, Database("main", [c, b, a]));

        Assert.Equal(["A", "B", "C"], diff.Tables.Select(t => t.Key));
    }

    [Fact]
    public void Views_and_sequences_added_altered_renamed_and_dropped()
    {
        const string newView = "01JB2Q0M8X4T5V6W7Y8Z9A0B1F";
        var diff = _differ.Diff(Baseline(), Model((_, _, views, sequences) =>
        {
            views[0].Body = "SELECT id FROM invoice";
            views[0].Name = "invoice_view";
            views.Add(View(newView, "v_customer", "SELECT * FROM customer"));
            sequences[0].Start = 1000;
            sequences[0].Cache = 20;
        }));

        Assert.Equal([(ChangeKind.Added, newView), (ChangeKind.Renamed, ViewId)], diff.Views.Select(v => (v.Kind, v.Key)));
        Assert.Equal([("body", "SELECT * FROM invoice", "SELECT id FROM invoice")], diff.Views[1].Changes.Select(c => (c.Property, c.Old, c.New)));
        var sequence = Assert.Single(diff.Sequences);
        Assert.Equal(ChangeKind.Altered, sequence.Kind);
        Assert.Equal([("start", 1L, 1000L), ("cache", null, 20)], sequence.Changes.Select(c => (c.Property, c.Old, c.New)));
        Assert.Empty(diff.Tables);
    }

    [Fact]
    public void Table_comment_or_schema_change_is_altered()
    {
        var comment = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Comment = "People who buy"));
        var otherComment = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Comment = "People who pay"));
        var schema = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Schema = "sales"));

        foreach (var diff in new[] { comment, otherComment, schema })
        {
            var change = Assert.Single(diff.Tables);
            Assert.Equal((ChangeKind.Altered, CustomerKey), (change.Kind, change.Key));
            Assert.False(diff.IsEmpty);
            Assert.Equal(4, diff.ToRevision);
        }

        // TableChange has no property list for them, but the values still reach the hash.
        Assert.Equal("sales", schema.Tables[0].Table!.Schema);
        Assert.NotEqual(comment.Hash, otherComment.Hash);
        Assert.NotEqual(comment.Hash, schema.Hash);
    }

    [Fact]
    public void Database_dialect_or_name_change_makes_the_diff_non_empty()
    {
        var sameModel = _differ.Diff(Baseline(), Model());
        var baseline = Baseline();
        var sqlServer = Model();
        sqlServer.Dialect = "sqlserver";
        var dialect = _differ.Diff(baseline, sqlServer);
        var renamed = _differ.Diff(baseline with { Name = "old_main" }, Model());

        Assert.True(sameModel.IsEmpty);
        foreach (var diff in new[] { dialect, renamed })
        {
            Assert.False(diff.IsEmpty);
            Assert.Equal(4, diff.ToRevision);
            Assert.Empty(diff.Tables);
            Assert.NotEqual(sameModel.Hash, diff.Hash);
        }

        Assert.NotEqual(dialect.Hash, renamed.Hash);
    }

    [Theory]
    [InlineData("")]
    [InlineData("db2")]
    [InlineData("PostgreSQL")]
    public void Capture_refuses_an_unknown_dialect(string dialect)
    {
        var database = Database("main", [], dialect: dialect);

        var error = Assert.Throws<ArgumentException>(() => _differ.Capture(database, 0));
        Assert.Contains("'" + dialect + "'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Diff_hash_follows_the_changes()
    {
        var a = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Name = "client"));
        var b = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Name = "client"));
        var c = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Name = "buyer"));
        var d = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Col("a-name").Default = "x"));
        var e = _differ.Diff(Baseline(), Model((customer, _, _, _) => customer.Col("a-name").Default = "y"));

        Assert.Equal(a.Hash, b.Hash);
        Assert.NotEqual(a.Hash, c.Hash);
        Assert.NotEqual(d.Hash, e.Hash);
    }

    [Fact]
    public void Diff_against_a_snapshot_read_back_from_json_is_empty()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(Baseline(), EngineJson.Options);
        var read = JsonSerializer.Deserialize<PhysicalSnapshot>(bytes, EngineJson.Options);

        Assert.True(_differ.Diff(read, Model()).IsEmpty);
    }

    [Fact]
    public void Structured_default_values_reach_templates_as_plain_values()
    {
        var diff = _differ.Diff(Baseline(), Model((customer, _, _, _) =>
            customer.Col("a-name").Default = JsonDocument.Parse("""{"b": [1, 2.5], "a": true}""").RootElement));

        var change = Assert.Single(Assert.Single(diff.Tables).Columns).Changes.Single();
        Assert.Equal("default", change.Property);
        var map = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(change.New);
        Assert.Equal(["a", "b"], map.Keys);
        Assert.Equal(true, map["a"]);
        Assert.Equal([1L, 2.5m], (IEnumerable<object?>)map["b"]!);
    }

    [Fact]
    public void Capture_records_default_names_deferral_clustering_view_comments_and_declared_schemas()
    {
        var db = Model((customer, invoice, views, _) =>
        {
            invoice.Col("a-total").DefaultName = "total_default";
            invoice.ForeignKeys[0].Deferrable = "initially-deferred";
            customer.PrimaryKey!.Clustered = true;
            views[0].Comment = "Open invoices.";
        });
        db.Schemas = new RList<RSchema>([new RSchema { Id = "s1", Name = "public", IsDeclared = true }, new RSchema { Id = "s0", Name = "implicit" }], []);

        var snapshot = _differ.Capture(db, 1);

        var invoice = snapshot.Tables[0];
        Assert.Equal("total_default", invoice.Columns[2].DefaultName);
        Assert.Equal(Deferrability.InitiallyDeferred, Assert.Single(invoice.ForeignKeys).Deferrable);
        Assert.True(snapshot.Tables[1].PrimaryKey!.Clustered);
        Assert.Equal("Open invoices.", Assert.Single(snapshot.Views).Comment);
        Assert.Equal(["s1"], snapshot.Schemas!.Select(s => s.Key));
    }

    [Fact]
    public void Default_name_deferral_and_view_comment_changes_are_reported_with_what_they_were()
    {
        var before = _differ.Capture(Model((_, invoice, _, _) => invoice.Col("a-total").DefaultName = "total_default"), 1);

        var diff = _differ.Diff(before, Model((customer, invoice, views, _) =>
        {
            invoice.Col("a-total").DefaultName = "total_dflt";
            invoice.ForeignKeys[0].Deferrable = "initially-immediate";
            customer.Comment = "Who we bill.";
            customer.Schema = "billing";
            views[0].Comment = "Open invoices.";
        }));

        var invoice = diff.Tables.Single(t => t.Key == InvoiceKey);
        var column = Assert.Single(invoice.Columns);
        Assert.Equal(("defaultName", "total_default", "total_dflt"), (column.Changes[0].Property, column.Changes[0].Old, column.Changes[0].New));
        Assert.Equal("total_default", column.OldDefaultName);
        var fk = Assert.Single(invoice.ForeignKeys);
        Assert.Equal(("deferrable", "not-deferrable", "initially-immediate"), (fk.Changes[0].Property, fk.Changes[0].Old, fk.Changes[0].New));
        var customer = diff.Tables.Single(t => t.Key == CustomerKey);
        Assert.Equal(("public", null), (customer.OldSchema, customer.OldComment));
        var view = Assert.Single(diff.Views);
        Assert.Equal(("comment", "public"), (Assert.Single(view.Changes).Property, view.OldSchema));
    }

    [Fact]
    public void Schemas_are_compared_only_when_both_snapshots_record_them()
    {
        static RDatabase With(RDatabase db, params (string Id, string Name)[] schemas)
        {
            db.Schemas = new RList<RSchema>([.. schemas.Select(s => new RSchema { Id = s.Id, Name = s.Name, IsDeclared = true })], []);
            return db;
        }

        var before = _differ.Capture(With(Model(), ("s1", "sales"), ("s2", "archive")), 1);
        var diff = _differ.Diff(before, With(Model(), ("s1", "shop"), ("s3", "audit")));

        Assert.False(diff.IsEmpty);
        Assert.Equal([(ChangeKind.Added, "s3"), (ChangeKind.Renamed, "s1"), (ChangeKind.Dropped, "s2")], diff.Schemas.Select(c => (c.Kind, c.Key)));
        Assert.Equal(("sales", "shop"), (diff.Schemas[1].OldName, diff.Schemas[1].NewName));
        Assert.NotEqual(_differ.Diff(before, With(Model(), ("s1", "sales"), ("s2", "archive"))).Hash, diff.Hash);

        // A snapshot written before schemas were recorded (or no snapshot at all) reports none.
        Assert.True(_differ.Diff(before with { Schemas = null }, With(Model(), ("s1", "shop"))).IsEmpty);
        Assert.Empty(_differ.Diff(null, With(Model(), ("s1", "shop"))).Schemas);
    }
}
