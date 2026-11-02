using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;

namespace Maquettiste.Engine.Tests.Editor;

/// <summary>E1: <see cref="GenerationService.GetDatabaseViewAsync"/> (phase2-design.md section 3.8).</summary>
public sealed class DatabaseViewTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task The_view_holds_the_resolved_tables_of_the_database()
    {
        await using var repo = EditorRepo.Create(packs: false);

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var view = Assert.IsType<DatabaseView>(result.View);
        Assert.Equal(EditorRepo.MainDatabaseId, view.Id);
        Assert.Equal("main", view.Name);
        Assert.Equal("postgresql", view.Dialect);

        // The customers table as the sql-ddl example in docs/api/openapi.yaml renders it.
        var customers = view.Tables.Single(t => t.Name == "customers");
        Assert.Equal("billing", customers.Schema);
        Assert.Equal("synthesized", customers.Origin);
        Assert.Equal(EditorRepo.CustomerId + "@" + EditorRepo.MainDatabaseId, customers.Key);
        Assert.Equal(EditorRepo.CustomerId, customers.EntityId);
        Assert.Equal(["id", "name", "email", "customer_since", "created_at", "updated_at"], customers.Columns.Select(c => c.Name));
        Assert.Equal(["uuid", "varchar(120)", "varchar(254)", "date", "timestamptz(6)", "timestamptz(6)"], customers.Columns.Select(c => c.NativeType));
        Assert.Equal(Enumerable.Range(0, customers.Columns.Count), customers.Columns.Select(c => c.Position));
        var id = customers.Columns[0];
        Assert.True(id.IsPrimaryKey);
        Assert.False(id.Nullable);
        Assert.Equal("01J92P0V0KGPC29TQQG8R57EBM", id.AttributeId);
        Assert.Equal("pk_customers", customers.PrimaryKey!.Name);
        Assert.Equal([id.Key], customers.PrimaryKey.Columns);
        Assert.Equal("uq_customers_email", Assert.Single(customers.Uniques).Name);

        // The overlay table keeps its file's id as key; the invoices table references customers through the places relation.
        var invoices = view.Tables.Single(t => t.Name == "invoices");
        Assert.Equal(EditorRepo.InvoiceId + "@" + EditorRepo.MainDatabaseId, invoices.Key);
        var toCustomer = invoices.ForeignKeys.Single(f => f.ReferencedTable == customers.Key);
        Assert.Equal(EditorRepo.PlacesRelationId, toCustomer.RelationId);
        Assert.Contains(toCustomer.EndId, new[] { "01J92P0V1EHF7PB28CZJG9C5SN", "01J92P0V1F439P4NH5F6HV5QM1" });
        Assert.Equal([id.Key], toCustomer.ReferencedColumns);
        Assert.All(toCustomer.Columns, key => Assert.True(invoices.Columns.Single(c => c.Key == key).IsForeignKey));
        Assert.Equal("restrict", toCustomer.OnDelete);
    }

    [Fact]
    public async Task Every_key_in_the_view_names_a_column_or_table_of_the_view()
    {
        await using var repo = EditorRepo.Create(packs: false);

        var view = (await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct)).View!;

        Assert.NotEmpty(view.Tables);
        var tables = view.Tables.ToDictionary(t => t.Key, StringComparer.Ordinal);
        Assert.Equal(view.Tables.Count, tables.Count);
        foreach (var table in view.Tables)
        {
            var columns = table.Columns.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(table.Columns.Count, columns.Count);
            Assert.All(table.PrimaryKey?.Columns ?? [], key => Assert.Contains(key, columns));
            Assert.All(table.Uniques.SelectMany(u => u.Columns), key => Assert.Contains(key, columns));
            Assert.All(table.Indexes.SelectMany(i => i.Columns), c => Assert.Contains(c.Column, columns));
            foreach (var foreignKey in table.ForeignKeys)
            {
                Assert.All(foreignKey.Columns, key => Assert.Contains(key, columns));
                var referenced = tables[foreignKey.ReferencedTable];
                Assert.All(foreignKey.ReferencedColumns, key => Assert.Contains(referenced.Columns, c => c.Key == key));
            }
        }

        // The designed overlay's descending index on issuedOn survives the projection.
        var invoices = view.Tables.Single(t => t.Name == "invoices");
        Assert.Contains(invoices.Indexes, i => i.Columns.Any(c => c.Descending));
    }

    [Fact]
    public async Task A_model_with_errors_has_no_view_and_returns_the_errors()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var payment = (await repo.Store.GetElementAsync(EditorRepo.PaymentId, EditorRepo.Ct))!;
        var node = JsonNode.Parse(payment.Json.GetRawText())!.AsObject();
        node.Remove("key");
        File.WriteAllText(repo.Repo.PathOf(payment.Path), node.ToJsonString(), new UTF8Encoding(false));

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.Null(result.View);
        Assert.Contains(result.Diagnostics, d => d.Rule == "MQ3005" && d.ElementId == EditorRepo.PaymentId && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task An_unknown_database_id_has_no_view_and_says_so()
    {
        await using var repo = EditorRepo.Create(packs: false);

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.InvoiceId, EditorRepo.Ct);

        Assert.Null(result.View);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("MQ6017", diagnostic.Rule);
        Assert.Equal(EditorRepo.InvoiceId, diagnostic.ElementId);
    }

    [Fact]
    public async Task Reading_the_view_writes_nothing_in_the_repo()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var before = repo.Files();

        await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.Equal(before, repo.Files());
    }

    [Fact]
    public async Task The_view_serializes_with_web_defaults_and_reads_back_equal()
    {
        await using var repo = EditorRepo.Create(packs: false);
        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        var json = JsonSerializer.Serialize(result, Web);
        var node = JsonNode.Parse(json)!;
        var back = JsonSerializer.Deserialize<DatabaseViewResult>(json, Web)!;

        Assert.Equal(json, JsonSerializer.Serialize(back, Web));
        var column = node["view"]!["tables"]![0]!["columns"]![0]!.AsObject();
        Assert.Equal(
            ["key", "name", "type", "nativeType", "length", "precision", "scale", "nullable", "defaultSql", "identity", "computed", "attributeId",
                "attributePath", "isPrimaryKey", "isForeignKey", "isDiscriminator", "position", "default", "computedStored", "sequenceId", "dbTypeId", "collation",
                "comment", "displayName", "pluralName", "description", "stereotypes", "tags", "category", "properties", "generation", "typeRef", "sensitive",
                "memberOrigin"],
            column.Select(p => p.Key));
        Assert.Null(column["memberOrigin"]);
        Assert.Null(column["typeRef"]); // next-phase-2-design.md section 3.2: no column of billing states a typeRef or a sensitivity yet
        Assert.Null(column["sensitive"]);
    }

    private const string Audited = ".maquettiste/model/vocabularies/stereotypes/audited.json";
    private const string MainDatabase = ".maquettiste/model/databases/main/database.json";
    private const string NotesTable = ".maquettiste/model/databases/main/tables/notes.json";
    private const string NotesTableId = "01K6BND0000000000000000001";
    private const string CreatedAt = "01JC7AVD1TC0000000000000C1", UpdatedAt = "01JC7AVD1TC0000000000000C2", CreatedIndex = "01JC7AVD1TC0000000000000X1";
    private const string OwnerId = "01JC7AVD1TC0000000000000C4", OwnerUnique = "01JC7AVD1TC0000000000000Q4", OwnerKey = "01JC7AVD1TC0000000000000F4";

    /// <summary>Rewrites a model file on disk, behind the store's back.</summary>
    private static void Edit(EditorRepo repo, string path, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(File.ReadAllText(repo.Repo.PathOf(path)))!.AsObject();
        change(node);
        File.WriteAllText(repo.Repo.PathOf(path), node.ToJsonString(), new UTF8Encoding(false));
    }

    /// <summary>Schema billing of database main gives every table <c>audited</c> by default.</summary>
    private static void DefaultAudited(EditorRepo repo) =>
        Edit(repo, MainDatabase, d => d["schemas"]![0]!["defaultStereotypes"] = new JsonArray("audited"));

    [Fact]
    public async Task A_typed_designed_column_carries_its_type_ref()
    {
        // next-phase-2-design.md section 3.2 (D21): the view's column carries the type its own entry names, as an attribute record
        // carries its type; type stays the physical keyword. The body column is typed: entity_type takes the bindings' constants
        // invoice and customer, which are no stored codes of the enum (MQ7014, D22).
        await using var repo = EditorRepo.Create(packs: false);
        const string invoiceStatus = "01J92P0V05WQDH2GZG539D9VG1";
        Edit(repo, NotesTable, t => t["columns"]![3]!["typeRef"] = new JsonObject { ["ref"] = invoiceStatus });

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        var notes = Assert.IsType<DatabaseView>(result.View).Tables.Single(t => t.Name == "notes");
        var body = notes.Columns.Single(c => c.Name == "body");
        Assert.Equal(("text", (int?)null), (body.Type, body.Length));
        Assert.Equal(new TypeRecord("enum", "InvoiceStatus", null, invoiceStatus, null, null, null), body.TypeRef);
        Assert.All(notes.Columns.Where(c => c.Name != "body"), c => Assert.Null(c.TypeRef));
    }

    [Fact]
    public async Task A_column_shows_its_own_or_its_bound_attributes_sensitivity()
    {
        // next-phase-2-design.md section 3.2 (D23): body states pii; entity_id states none, and shows none since no bound attribute does.
        await using var repo = EditorRepo.Create(packs: false);
        Edit(repo, NotesTable, t => t["columns"]![3]!["sensitive"] = "pii");

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        var notes = Assert.IsType<DatabaseView>(result.View).Tables.Single(t => t.Name == "notes");
        Assert.Equal("pii", notes.Columns.Single(c => c.Name == "body").Sensitive);
        Assert.All(notes.Columns.Where(c => c.Name != "body"), c => Assert.Null(c.Sensitive));
    }

    [Fact]
    public async Task Member_origin_is_null_for_own_members_and_set_for_inherited()
    {
        // next-phase-2-design.md section 3.1: audited contributes two columns and an index to every table of schema billing; notes keeps
        // its own created_at (and so leaves out the index on it), redefines updated_at's comment, and carries owned, whose column, unique and
        // foreign key it inherits.
        await using var repo = EditorRepo.Create(packs: false);
        Edit(repo, Audited, s =>
        {
            s["columns"] = JsonNode.Parse($$"""
                [{"id": "{{CreatedAt}}", "name": "created_at", "type": "datetimeoffset", "nullable": false, "defaultSql": {"*": "CURRENT_TIMESTAMP"} },
                 {"id": "{{UpdatedAt}}", "name": "updated_at", "type": "datetimeoffset"}]
                """);
            s["indexes"] = JsonNode.Parse($$"""[{"id": "{{CreatedIndex}}", "columns": [{"column": "{{CreatedAt}}"}]}]""");
        });
        File.WriteAllText(repo.Repo.PathOf(".maquettiste/model/vocabularies/stereotypes/owned.json"), $$"""
            {"kind": "stereotype", "id": "01JC7AVD1TC0000000000000S4", "key": "owned", "name": "Owned", "appliesTo": ["table"],
             "columns": [{"id": "{{OwnerId}}", "name": "owner_id", "type": "uuid"}],
             "uniques": [{"id": "{{OwnerUnique}}", "columns": ["{{OwnerId}}"]}],
             "foreignKeys": [{"id": "{{OwnerKey}}", "columns": ["{{OwnerId}}"], "referencesTable": "{{EditorRepo.CustomerId}}@{{EditorRepo.MainDatabaseId}}"}]}
            """, new UTF8Encoding(false));
        DefaultAudited(repo);
        Edit(repo, NotesTable, t =>
        {
            t["stereotypes"] = new JsonArray("owned");
            t["redefines"] = JsonNode.Parse($$"""[{"member": "{{UpdatedAt}}", "comment": "When the note last changed."}]""");
            t["excludes"] = new JsonArray(CreatedAt, CreatedIndex);
        });

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        var view = Assert.IsType<DatabaseView>(result.View);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        // products inherits both columns and the index from the schema default, after its own members, keyed by its table key.
        var products = view.Tables.Single(t => t.Name == "products");
        Assert.Equal(["audited"], products.Stereotypes);
        var inherited = products.Columns.Where(c => c.MemberOrigin is not null).ToList();
        Assert.Equal(["created_at", "updated_at"], inherited.Select(c => c.Name));
        Assert.Equal([products.Key + "." + CreatedAt, products.Key + "." + UpdatedAt], inherited.Select(c => c.Key));
        var origin = inherited[0].MemberOrigin!;
        Assert.Equal(("schema-default", "audited", CreatedAt), (origin.Source, origin.Stereotype, origin.MemberId));
        Assert.Empty(origin.Redefined);
        Assert.All(products.Columns.Take(products.Columns.Count - 2), c => Assert.Null(c.MemberOrigin));
        Assert.Null(products.PrimaryKey!.MemberOrigin);
        var index = products.Indexes.Single(i => i.MemberOrigin is not null);
        Assert.Equal(("schema-default", "audited", CreatedIndex), (index.MemberOrigin!.Source, index.MemberOrigin.Stereotype, index.MemberOrigin.MemberId));
        Assert.Equal([inherited[0].Key], index.Columns.Select(c => c.Column));
        // customers stores audited's attributes as columns, so it inherits nothing; its own members carry no origin.
        var customers = view.Tables.Single(t => t.Name == "customers");
        Assert.All(customers.Columns, c => Assert.Null(c.MemberOrigin));
        Assert.All(customers.Uniques, u => Assert.Null(u.MemberOrigin));
        Assert.All(view.Tables.Single(t => t.Name == "invoices").ForeignKeys, f => Assert.Null(f.MemberOrigin));

        // notes: its own created_at, updated_at redefined, and owned's column, unique and foreign key by its own entry.
        var notes = view.Tables.Single(t => t.Key == NotesTableId);
        Assert.Equal(["audited", "owned"], notes.Stereotypes); // the schema's defaults first, then the own list
        Assert.Null(notes.Columns.Single(c => c.Name == "created_at").MemberOrigin);
        var updated = notes.Columns.Single(c => c.Name == "updated_at");
        Assert.Equal((NotesTableId + "." + UpdatedAt, "schema-default", "When the note last changed."), (updated.Key, updated.MemberOrigin!.Source, updated.Comment));
        Assert.Equal(["comment"], updated.MemberOrigin.Redefined);
        var owner = notes.Columns.Single(c => c.Name == "owner_id").MemberOrigin!;
        Assert.Equal(("stereotype", "owned", OwnerId), (owner.Source, owner.Stereotype, owner.MemberId));
        var unique = Assert.Single(notes.Uniques);
        Assert.Equal(OwnerUnique, unique.MemberOrigin!.MemberId);
        Assert.Equal([NotesTableId + "." + OwnerId], unique.Columns);
        var foreignKey = Assert.Single(notes.ForeignKeys);
        Assert.Equal(("stereotype", OwnerKey, customers.Key), (foreignKey.MemberOrigin!.Source, foreignKey.MemberOrigin.MemberId, foreignKey.ReferencedTable));
        Assert.DoesNotContain(notes.Indexes, i => i.MemberOrigin is not null);
        Assert.Null(notes.PrimaryKey!.MemberOrigin);

        // The origin reaches the wire under web defaults: null for own members, an object for inherited ones.
        var node = JsonNode.Parse(JsonSerializer.Serialize(result, Web))!["view"]!["tables"]!.AsArray().Single(t => (string)t!["key"]! == NotesTableId)!;
        Assert.Null(node["columns"]![0]!["memberOrigin"]);
        Assert.Equal("""{"source":"schema-default","stereotype":"audited","memberId":"01JC7AVD1TC0000000000000C2","redefined":["comment"]}""",
            node["columns"]!.AsArray().Single(c => (string)c!["name"]! == "updated_at")!["memberOrigin"]!.ToJsonString());
    }

    [Fact]
    public async Task A_table_without_a_file_shows_its_default_stereotypes()
    {
        // next-phase-2-design.md section 3.1: a synthesized table without an overlay carries the stereotypes its schema default gives
        // it, with their default properties; the view and the sequence, which audited does not apply to, take none.
        await using var repo = EditorRepo.Create(packs: false);
        var before = (await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct)).View!;
        DefaultAudited(repo);

        var result = await repo.Service.GetDatabaseViewAsync(EditorRepo.MainDatabaseId, EditorRepo.Ct);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        var view = Assert.IsType<DatabaseView>(result.View);
        Assert.Empty(before.Tables.Single(t => t.Name == "customers").Stereotypes);
        foreach (var name in new[] { "customers", "products" })
        {
            var table = view.Tables.Single(t => t.Name == name);
            Assert.Equal("synthesized", table.Origin);
            Assert.Equal(["audited"], table.Stereotypes);
            Assert.Equal(2555L, Convert.ToInt64(table.Properties["retentionDays"], System.Globalization.CultureInfo.InvariantCulture));
            Assert.Null(table.DisplayName);
            Assert.All(table.Columns, c => Assert.Null(c.MemberOrigin)); // audited declares no members
        }

        Assert.All(view.Views, v => Assert.Empty(v.Stereotypes));
        Assert.All(view.Sequences, s => Assert.Empty(s.Stereotypes));
    }
}
