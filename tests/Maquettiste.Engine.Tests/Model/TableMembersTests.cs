using System.Text.Json;
using Maquettiste.Engine.Model;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Model;

/// <summary>
/// Tables as holders of contributed members (next-phase-2-design.md section 3.1, D6): the member set of a table file or of a table
/// key, keyed <c>&lt;tableKey&gt;.&lt;memberId&gt;</c> with every column list keyed too, cached per snapshot, table and skip, and the
/// table's link checks.
/// </summary>
public sealed class TableMembersTests
{
    /// <summary>
    /// The audited stereotype of the design's example (columns created_at and updated_at, an index on created_at), plus a primary key,
    /// a unique, a foreign key to a fixed table and a check, all over its own columns.
    /// </summary>
    private sealed record Fixture(ModelSnapshot Model, Stereotype Audited, Table Notes, Table Products, string ProductsKey, string Customer);

    private static Fixture Build(Func<Table, Table>? editNotes = null, Func<Table, Table>? editProducts = null)
    {
        var b = new ModelBuilder(seed: 41);
        var main = b.Database("main", Dialect.PostgreSql);
        var customers = b.Table("customers", main).Column("id", "uuid", nullable: false).PrimaryKey("id");
        string created = b.NewId(), updated = b.NewId(), rowId = b.NewId();
        var audited = new Stereotype
        {
            Id = b.NewId(), Key = "audited", Name = "Audited", AppliesTo = ["entity", "table", "column"],
            Columns =
            [
                new Column { Id = created, Name = "created_at", Type = "datetimeoffset", Nullable = false, DefaultSql = new Dictionary<string, string> { ["*"] = "CURRENT_TIMESTAMP" } },
                new Column { Id = updated, Name = "updated_at", Type = "datetimeoffset" },
                new Column { Id = rowId, Name = "row_id", Type = "uuid", Nullable = false },
            ],
            PrimaryKey = new PrimaryKey { Id = b.NewId(), Columns = [rowId] },
            Uniques = [new UniqueConstraint { Id = b.NewId(), Columns = [rowId, created] }],
            ForeignKeys = [new MemberForeignKey { Id = b.NewId(), Columns = [rowId], ReferencesTable = customers.Id, OnDeleteColumns = [rowId] }],
            Checks = [new CheckConstraint { Id = b.NewId(), Column = updated, Expression = new Dictionary<string, string> { ["*"] = "updated_at >= created_at" } }],
            Indexes = [new TableIndex { Id = b.NewId(), Columns = [new IndexColumn { Column = created }], Include = [updated] }],
        };
        b.Add(audited);
        var notes = b.Table("notes", main).Column("body", "text").Stereotype("audited").Edit(editNotes ?? (t => t));
        var product = b.Entity("Product").Key("id", "uuid");
        var products = b.Table("products", main).OverlayFor(product).Stereotype("audited").Edit(editProducts ?? (t => t));
        b.Mapping(main, product);
        var model = b.Build();
        return new Fixture(model, audited, model.Get<Table>(notes.Id)!, model.Get<Table>(products.Id)!, product.Id + "@" + main.Id, customers.Id);
    }

    private static string Keyed(string tableKey, string id) => tableKey + "." + id;

    [Fact]
    public void A_designed_table_inherits_columns_and_keys_with_keyed_ids()
    {
        var f = Build();
        var set = TableMembers.Of(f.Model, f.Notes);
        var audited = f.Audited;

        Assert.Equal(f.Notes.Id, set.TableKey);
        Assert.False(set.IsEmpty);
        Assert.Equal(audited.Columns.Select(c => Keyed(f.Notes.Id, c.Id)), set.Columns.Select(c => c.Key));
        Assert.Equal(set.Columns.Select(c => c.Key), set.Columns.Select(c => c.Member.Id));
        Assert.Equal(audited.Columns.Select(c => c.Id), set.Columns.Select(c => c.MemberId));
        Assert.Equal(["created_at", "updated_at", "row_id"], set.Columns.Select(c => c.Member.Name));
        Assert.All(set.Columns, c => Assert.Equal(StereotypeSource.Own, c.From.Source));
        Assert.Equal(Keyed(f.Notes.Id, audited.PrimaryKey!.Id!), Assert.Single(set.PrimaryKeys).Member.Id);
        Assert.Equal(Keyed(f.Notes.Id, audited.Uniques[0].Id), Assert.Single(set.Uniques).Member.Id);
        Assert.Equal(Keyed(f.Notes.Id, audited.ForeignKeys[0].Id), Assert.Single(set.ForeignKeys).Member.Id);
        Assert.Equal(Keyed(f.Notes.Id, audited.Checks[0].Id), Assert.Single(set.Checks).Member.Id);
        Assert.Equal(Keyed(f.Notes.Id, audited.Indexes[0].Id), Assert.Single(set.Indexes).Member.Id);
        Assert.True(set.HasColumn(Keyed(f.Notes.Id, audited.Columns[0].Id)));
        Assert.False(set.HasColumn(audited.Columns[0].Id));
        Assert.False(set.HasColumn(f.Notes.Columns[0].Id));
        // Every member before excludes, by member id, for the link checks.
        Assert.Equal(
            audited.Columns.Select(c => c.Id).Concat([audited.PrimaryKey.Id!, audited.Uniques[0].Id, audited.ForeignKeys[0].Id, audited.Indexes[0].Id, audited.Checks[0].Id])
                .Order(StringComparer.Ordinal),
            set.Inheritable.Keys.Order(StringComparer.Ordinal));

        // A table without the stereotype inherits nothing.
        var customers = f.Model.Get<Table>(f.Customer)!;
        Assert.True(TableMembers.Of(f.Model, customers).IsEmpty);
    }

    [Fact]
    public void Member_column_lists_become_keyed_ids()
    {
        var f = Build(editNotes: t => t with { Excludes = [], Redefines = [] });
        var set = TableMembers.Of(f.Model, f.Notes);
        var (created, updated, rowId) = (f.Audited.Columns[0].Id, f.Audited.Columns[1].Id, f.Audited.Columns[2].Id);
        string K(string id) => Keyed(f.Notes.Id, id);

        Assert.Equal([K(rowId)], set.PrimaryKeys[0].Member.Columns);
        Assert.Equal([K(rowId), K(created)], set.Uniques[0].Member.Columns);
        Assert.Equal([K(rowId)], set.ForeignKeys[0].Member.Columns);
        Assert.Equal([K(rowId)], set.ForeignKeys[0].Member.OnDeleteColumns);
        // The referenced table's columns are not the stereotype's: the target stays as written.
        Assert.Equal(f.Customer, set.ForeignKeys[0].Member.ReferencesTable);
        Assert.Equal(K(updated), set.Checks[0].Member.Column);
        Assert.Equal([K(created)], set.Indexes[0].Member.Columns.Select(c => c.Column));
        Assert.Equal([K(updated)], set.Indexes[0].Member.Include);

        // A redefined key column list is the table's own whole list, taken as written: own ids and inherited keyed ids.
        var own = f.Notes.Columns[0].Id;
        var primaryKey = f.Audited.PrimaryKey!.Id!;
        f = Build(editNotes: t => t with
        {
            Redefines = [new TableRedefinition { Member = primaryKey, Columns = [K(rowId), t.Columns[0].Id] }],
        });
        set = TableMembers.Of(f.Model, f.Notes);
        Assert.Equal([K(rowId), own], set.PrimaryKeys[0].Member.Columns);
        Assert.Equal(["columns"], set.PrimaryKeys[0].Redefined);
    }

    [Fact]
    public void An_overlay_uses_the_synthesized_table_key()
    {
        // The builder's ids repeat from one build to the next, so a first build names the member the second one redefines.
        var created = Build().Audited.Columns[0];
        var f = Build(editProducts: t => t with { Redefines = [new TableRedefinition { Member = created.Id, Nullable = true }] });

        Assert.Equal(f.ProductsKey, TableMembers.TableKeyOf(f.Products));
        Assert.Equal(f.Notes.Id, TableMembers.TableKeyOf(f.Notes));
        var set = TableMembers.Of(f.Model, f.Products);
        Assert.Equal(f.ProductsKey, set.TableKey);
        var column = set.Columns[0];
        Assert.Equal(f.ProductsKey + "." + created.Id, column.Key);
        Assert.Equal(column.Key, column.Member.Id);
        Assert.True(column.Member.Nullable);
        Assert.Equal(["nullable"], column.Redefined);
        Assert.Equal("CURRENT_TIMESTAMP", column.Member.DefaultSql["*"]);
        Assert.Equal([f.ProductsKey + "." + created.Id], set.Indexes[0].Member.Columns.Select(c => c.Column));

        // The resolver's form with the same inputs gives the same set; with a skip the overlay's set loses that stereotype.
        var effective = f.Model.EffectiveStereotypes(f.Products);
        Assert.Same(set, TableMembers.Of(f.Model, f.ProductsKey, effective, f.Products.Redefines, f.Products.Excludes, new HashSet<string>()));
        Assert.True(TableMembers.Of(f.Model, f.ProductsKey, effective, f.Products.Redefines, f.Products.Excludes, new HashSet<string>(["audited"])).IsEmpty);
    }

    [Fact]
    public void Member_sets_are_cached_per_snapshot_table_and_skip()
    {
        var f = Build();
        var first = TableMembers.Of(f.Model, f.Notes);

        Assert.Same(first, TableMembers.Of(f.Model, f.Notes));
        var effective = f.Model.EffectiveStereotypes(f.Notes);
        Assert.Same(first, TableMembers.Of(f.Model, f.Notes.Id, effective, f.Notes.Redefines, f.Notes.Excludes, new HashSet<string>()));
        var skipping = TableMembers.Of(f.Model, f.Notes.Id, effective, f.Notes.Redefines, f.Notes.Excludes, new HashSet<string>(["other"]));
        Assert.NotSame(first, skipping);
        Assert.Equal(first.Columns.Select(c => c.Key), skipping.Columns.Select(c => c.Key));
        Assert.Same(skipping, TableMembers.Of(f.Model, f.Notes.Id, effective, f.Notes.Redefines, f.Notes.Excludes, new HashSet<string>(["other"])));

        // Another table, another snapshot, or a candidate table (not the snapshot's own instance) gets a set of its own.
        Assert.NotSame(first, TableMembers.Of(f.Model, f.Products));
        var again = Build();
        Assert.NotSame(first, TableMembers.Of(again.Model, again.Notes));
        var candidate = f.Notes with { Excludes = [f.Audited.Columns[1].Id] };
        var judged = TableMembers.Of(f.Model, candidate);
        Assert.Equal(2, judged.Columns.Count);
        Assert.Same(first, TableMembers.Of(f.Model, f.Notes));
        // Other inputs for a cached key are never answered from the cache.
        Assert.Equal(2, TableMembers.Of(f.Model, f.Notes.Id, effective, [], candidate.Excludes, new HashSet<string>()).Columns.Count);
    }

    [Fact]
    public void The_design_examples_validate_against_the_schemas()
    {
        // next-phase-2-design.md section 3.1: the audited and child-row stereotypes, and the overlay and table entries of the examples.
        const string audited = """
            { "kind": "stereotype", "id": "01J92P0V25BX5TMQ8N9F6AJE61", "key": "audited", "name": "Audited",
              "appliesTo": ["entity", "table", "column"],
              "columns": [
                { "id": "01JC7AVD1TC0000000000000C1", "name": "created_at", "type": "datetimeoffset", "nullable": false,
                  "defaultSql": { "*": "CURRENT_TIMESTAMP" } },
                { "id": "01JC7AVD1TC0000000000000C2", "name": "updated_at", "type": "datetimeoffset" } ],
              "indexes": [ { "id": "01JC7AVD1TC0000000000000X1", "columns": [ { "column": "01JC7AVD1TC0000000000000C1" } ] } ] }
            """;
        const string childRow = """
            { "kind": "stereotype", "id": "01JC7AVD1TC0000000000000S1", "key": "child-row", "name": "Child row",
              "appliesTo": ["table"],
              "parameters": { "properties": { "parentTable": { "type": "string",
                "x-ref": { "kind": "table", "sameDatabase": true } } }, "required": ["parentTable"] },
              "columns": [ { "id": "01JC7AVD1TC0000000000000C3", "name": "parent_id", "type": "uuid", "nullable": false } ],
              "primaryKey": { "id": "01JC7AVD1TC0000000000000K1", "columns": ["01JC7AVD1TC0000000000000C3"] },
              "foreignKeys": [ { "id": "01JC7AVD1TC0000000000000F1", "columns": ["01JC7AVD1TC0000000000000C3"],
                "referencesParameter": "parentTable", "onDelete": "cascade" } ] }
            """;
        const string notes = """
            { "kind": "table", "id": "01K6BND0000000000000000001", "name": "notes", "database": "01J92P0V1QRN2181XM2ZWE02W4",
              "stereotypes": ["audited"],
              "columns": [ { "id": "01K6BND0000000000000000002", "name": "created_at", "type": "datetimeoffset" } ],
              "redefines": [ { "member": "01JC7AVD1TC0000000000000C2", "nullable": false, "comment": "Last change" } ],
              "excludes": ["01JC7AVD1TC0000000000000C1", "01JC7AVD1TC0000000000000X1"] }
            """;
        Assert.Empty(Evaluate("stereotype.json", audited));
        Assert.Empty(Evaluate("stereotype.json", childRow));
        Assert.Empty(Evaluate("table.json", notes));

        // The schemas refuse a named contributed foreign key, a primary key without an id, a redefinition that states nothing, and an
        // unknown facet.
        Assert.Contains(Evaluate("stereotype.json", childRow.Replace("\"referencesParameter\"", "\"name\": \"fk\", \"referencesParameter\"", StringComparison.Ordinal)),
            d => d.JsonPointer!.StartsWith("/foreignKeys/0", StringComparison.Ordinal));
        Assert.Contains(Evaluate("stereotype.json", childRow.Replace("\"id\": \"01JC7AVD1TC0000000000000K1\", ", "", StringComparison.Ordinal)),
            d => d.JsonPointer!.StartsWith("/primaryKey", StringComparison.Ordinal));
        Assert.Contains(Evaluate("table.json", notes.Replace(", \"nullable\": false, \"comment\": \"Last change\"", "", StringComparison.Ordinal)),
            d => d.JsonPointer!.StartsWith("/redefines/0", StringComparison.Ordinal));
        Assert.Contains(Evaluate("table.json", notes.Replace("\"comment\"", "\"type\"", StringComparison.Ordinal)),
            d => d.JsonPointer!.StartsWith("/redefines/0", StringComparison.Ordinal));

        static IReadOnlyList<Maquettiste.Engine.Diagnostics.Diagnostic> Evaluate(string schema, string json)
        {
            using var document = JsonDocument.Parse(json);
            return TestServices.Schemas.Evaluate(schema, document.RootElement, "model/x.json");
        }
    }

    [Fact]
    public void CheckLinks_refuses_a_column_name_facet()
    {
        var plain = Build();
        var (created, updated) = (plain.Audited.Columns[0].Id, plain.Audited.Columns[1].Id);
        var (index, check) = (plain.Audited.Indexes[0].Id, plain.Audited.Checks[0].Id);
        var f = Build(editNotes: t => t with
        {
            Redefines =
            [
                new TableRedefinition { Member = created, Name = "made_at", Nullable = false },
                new TableRedefinition { Member = index, Where = "created_at IS NOT NULL", Method = IndexMethod.Default },
            ],
        });

        var findings = TableMembers.CheckLinks(TableMembers.Of(f.Model, f.Notes), f.Notes);

        // The name facet is refused (MQ2027, with the exclude-and-declare hint); nullable false equals the inherited value (MQ4078),
        // and so does the index method.
        Assert.Equal(
            [("MQ2027", "/redefines/0/name"), ("MQ4078", "/redefines/0/nullable"), ("MQ4078", "/redefines/1/method")],
            findings.Select(d => (d.Rule, d.JsonPointer!)));
        Assert.Contains("exclude the member and declare an own column", findings[0].Message, StringComparison.Ordinal);
        Assert.Equal(Maquettiste.Engine.Diagnostics.DiagnosticSeverity.Error, findings[0].Severity);
        Assert.Equal(Maquettiste.Engine.Diagnostics.DiagnosticSeverity.Warning, findings[1].Severity);
        Assert.All(findings, d => Assert.Equal(f.Notes.Id, d.ElementId));

        // An excluded column a kept member uses is MQ2028; excluding the index too is clean, and so are facets that differ.
        f = Build(editNotes: t => t with { Excludes = [created] });
        Assert.Equal([("MQ2028", "/excludes/0")], TableMembers.CheckLinks(TableMembers.Of(f.Model, f.Notes), f.Notes).Select(d => (d.Rule, d.JsonPointer!)));
        Assert.Contains("unique", TableMembers.CheckLinks(TableMembers.Of(f.Model, f.Notes), f.Notes)[0].Message, StringComparison.Ordinal);
        f = Build(editNotes: t => t with
        {
            Excludes = [updated, check, index],
            Redefines = [new TableRedefinition { Member = created, Nullable = true, Default = JsonDocument.Parse("\"2026-01-01\"").RootElement.Clone() }],
        });
        Assert.Empty(TableMembers.CheckLinks(TableMembers.Of(f.Model, f.Notes), f.Notes));
    }
}
