using System.Text;
using System.Text.Json.Nodes;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Model;
using Maquettiste.Engine.Tests.Editor;
using Maquettiste.Testing;

namespace Maquettiste.Engine.Tests.Store;

/// <summary>
/// The batch operation <c>names-to-ids</c> (next-phase-1-design.md section 4.1): a custom property value an <c>x-ref</c> types that is
/// a name becomes the one id it finds among the in-scope candidates, all or nothing, with one MQ2010 per value that finds nothing or
/// several.
/// </summary>
public sealed class NamesToIdsTests
{
    private const string AuditedId = "01J92P0V25BX5TMQ8N9F6AJE61";
    private const string IssuedOnId = "01J92P0V0SNXS6PZ42VDK42HP9";
    private const string CatalogId = "01J92P0V025DRFTXKMS240G2NG";
    private const string TwinId = "01K9N2D0000000000000000001";
    private const string TwinKeyId = "01K9N2D0000000000000000002";
    private const string InvoicePath = ".maquettiste/model/entities/invoice.json";
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";
    private const string AuditedPath = ".maquettiste/model/vocabularies/stereotypes/audited.json";
    private const string Convert = """{"op":"names-to-ids"}""";

    private const string Links = """
        { "name": "links", "appliesTo": { "kinds": ["entity"] },
          "properties": {
            "owner": { "type": "string", "x-ref": { "kind": "entity" } },
            "defaultSort": { "type": "string", "x-ref": { "kind": "attribute", "of": "$element" } },
            "related": { "type": "array", "items": { "type": "string", "x-ref": { "kind": ["entity", "value-object"] } } },
            "marker": { "type": "string", "x-ref": { "kind": "stereotype" } } } }
        """;

    private const string Review = """
        { "name": "review", "appliesTo": { "kinds": ["entity"], "stereotypes": ["audited"] },
          "properties": { "reviewer": { "type": "string", "x-ref": { "kind": "entity" } } } }
        """;

    /// <summary>An entity named Customer in Catalog (a child of Billing), so the bare name finds two.</summary>
    private const string Twin = $$"""
        { "kind": "entity", "id": "{{TwinId}}", "name": "Customer", "package": "{{CatalogId}}",
          "key": { "attributes": ["{{TwinKeyId}}"], "strategy": "uuid-v7" },
          "attributes": [ { "id": "{{TwinKeyId}}", "name": "id", "type": "uuid", "required": true } ] }
        """;

    private static CancellationToken Ct => EditorRepo.Ct;

    /// <summary>The billing fixture with the two extension files and the documents edited on disk before the store loads.</summary>
    private static EditorRepo Repo(Action<JsonObject> invoice, Action<JsonObject>? customer = null, bool twin = false, Action<JsonObject>? audited = null)
    {
        var r = EditorRepo.Create(packs: false);
        Write(r, ".maquettiste/extensions/links.json", "extension.json", JsonNode.Parse(Links)!);
        Write(r, ".maquettiste/extensions/review.json", "extension.json", JsonNode.Parse(Review)!);
        Edit(r, InvoicePath, "entity.json", invoice);
        if (customer is not null)
            Edit(r, CustomerPath, "entity.json", customer);
        if (audited is not null)
            Edit(r, AuditedPath, "stereotype.json", audited);
        if (twin)
            Write(r, ".maquettiste/model/entities/catalog-customer.json", "entity.json", JsonNode.Parse(Twin)!);
        return r;
    }

    private static void Write(EditorRepo r, string path, string schema, JsonNode node) => r.Repo.WriteBytes(path, TestServices.Json.Write(node, schema, path));

    private static void Edit(EditorRepo r, string path, string schema, Action<JsonObject> change)
    {
        var node = JsonNode.Parse(r.Repo.ReadFile(path))!.AsObject();
        change(node);
        Write(r, path, schema, node);
    }

    private static void Property(JsonObject element, string name, JsonNode value, string member = "properties")
    {
        if (element[member] is not JsonObject properties)
            element[member] = properties = [];
        properties[name] = value;
    }

    private static async Task<BatchResult> ApplyAsync(EditorRepo r, string operation)
    {
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + operation + "]}"));
        Assert.True(parsed.Batch is not null, string.Join("; ", parsed.Diagnostics.Select(d => $"{d.Rule} {d.JsonPointer} {d.Message}")));
        return await r.Store.ApplyBatchAsync(parsed.Batch, ChangeSource.Editor, Ct);
    }

    private static string? Text(EditorRepo r, string id, string name) =>
        r.Store.Current!.Get<Entity>(id)!.Properties.TryGetValue(name, out var value) ? value.GetString() : null;

    private static List<Diagnostic> Refusals(BatchResult result) => [.. result.Items.SelectMany(i => i.Diagnostics)];

    [Fact]
    public async Task Names_become_ids_all_or_nothing_and_an_ambiguous_name_refuses()
    {
        await using var r = Repo(n =>
            {
                Property(n, "owner", "Customer");
                Property(n, "defaultSort", "issuedOn");
                Property(n, "related", new JsonArray("Payment", "Billing.Customer"));
            },
            twin: true,
            audited: n => Property(n, "reviewer", "Payment", "defaultProperties"));
        var before = r.Files();

        // The bare name Customer finds two entities: the batch is refused at that value, and nothing is written.
        var refused = await ApplyAsync(r, Convert);

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var ambiguous = Assert.Single(Refusals(refused));
        Assert.Equal(("MQ2010", EditorRepo.InvoiceId, InvoicePath, "/properties/owner"), (ambiguous.Rule, ambiguous.ElementId, ambiguous.FilePath, ambiguous.JsonPointer));
        Assert.Contains("'owner' holds 'Customer', which finds 2 of kind entity", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("@entity:Billing.Catalog.Customer (" + TwinId + ")", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal(before, r.Files());

        // Without the twin every name finds one target, and one write converts them all, the stereotype's default included.
        var twin = (await r.Store.GetElementAsync(TwinId, Ct))!;
        Assert.Equal(SaveOutcome.Saved, (await r.Store.DeleteAsync(TwinId, twin.Hash, DeleteResolution.Refuse, ChangeSource.Editor, Ct)).Outcome);
        Assert.Contains((await r.Store.ValidateAsync(ValidationScope.All, Ct)).Diagnostics, d => d.Rule == "MQ2010");

        var saved = await ApplyAsync(r, Convert);

        Assert.True(saved.Outcome == SaveOutcome.Saved, string.Join("; ", Refusals(saved).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal(EditorRepo.CustomerId, Text(r, EditorRepo.InvoiceId, "owner"));
        Assert.Equal(IssuedOnId, Text(r, EditorRepo.InvoiceId, "defaultSort"));
        Assert.Equal([EditorRepo.PaymentId, EditorRepo.CustomerId],
            r.Store.Current!.Get<Entity>(EditorRepo.InvoiceId)!.Properties["related"].EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(EditorRepo.PaymentId, r.Store.Current!.Get<Stereotype>(AuditedId)!.DefaultProperties["reviewer"].GetString());
        Assert.Equal([InvoicePath, AuditedPath], saved.Changes!.Changed.Select(c => c.Path).Order(StringComparer.Ordinal));
        var validation = await r.Store.ValidateAsync(ValidationScope.All, Ct);
        Assert.DoesNotContain(validation.Diagnostics, d => d.Rule is "MQ2001" or "MQ2002" or "MQ2010" or "MQ2011");
    }

    [Fact]
    public async Task A_dry_run_writes_nothing()
    {
        await using var r = Repo(n => Property(n, "owner", "Customer"));
        var before = r.Files();
        var parsed = r.Store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + Convert + "]}"));

        var preview = await r.Store.PreviewBatchAsync(parsed.Batch!, Ct);

        Assert.Equal(SaveOutcome.Saved, preview.Outcome);
        var file = Assert.Single(preview.Files);
        Assert.Equal((InvoicePath, "changed", EditorRepo.InvoiceId), (file.Path, file.Action, file.Id));
        Assert.Contains("+    \"owner\": \"" + EditorRepo.CustomerId + "\"", file.Diff, StringComparison.Ordinal);
        Assert.Contains("-    \"owner\": \"Customer\"", file.Diff, StringComparison.Ordinal);
        Assert.Contains(preview.DiagnosticsRemoved, d => d.Rule == "MQ2010" && d.JsonPointer == "/properties/owner");
        Assert.Equal(before, r.Files());
        Assert.Equal("Customer", Text(r, EditorRepo.InvoiceId, "owner"));
    }

    [Fact]
    public async Task A_second_run_changes_nothing()
    {
        await using var r = Repo(n => Property(n, "owner", "Customer"));
        Assert.Equal(SaveOutcome.Saved, (await ApplyAsync(r, Convert)).Outcome);
        var after = r.Files();

        var again = await ApplyAsync(r, Convert);

        Assert.Equal(SaveOutcome.Saved, again.Outcome);
        Assert.Empty(again.Items);
        Assert.True(again.Changes!.IsEmpty);
        Assert.Equal(after, r.Files());
        Assert.Equal(EditorRepo.CustomerId, Text(r, EditorRepo.InvoiceId, "owner"));
    }

    [Fact]
    public async Task Each_unmatched_value_is_its_own_MQ2010()
    {
        await using var r = Repo(n =>
        {
            Property(n, "owner", "Nobody");
            Property(n, "defaultSort", "total");
            Property(n, "related", new JsonArray("Payment", "Ghost", "Also missing"));
        });
        var before = r.Files();

        var refused = await ApplyAsync(r, Convert);

        Assert.Equal(SaveOutcome.Invalid, refused.Outcome);
        var found = Refusals(refused);
        Assert.Equal(["/properties/owner", "/properties/related/1", "/properties/related/2"], found.Select(d => d.JsonPointer!).Order(StringComparer.Ordinal));
        Assert.All(found, d => Assert.Equal(("MQ2010", EditorRepo.InvoiceId, InvoicePath), (d.Rule, d.ElementId, d.FilePath)));
        Assert.Contains("'related' holds 'Ghost', which finds no entity or value-object in scope", found.Single(d => d.JsonPointer == "/properties/related/1").Message,
            StringComparison.Ordinal);
        Assert.Equal(before, r.Files());
    }

    [Fact]
    public async Task Element_ids_take_names()
    {
        await using var r = Repo(n => Property(n, "owner", "Customer"), customer: n => Property(n, "owner", "Invoice"));

        var result = await ApplyAsync(r, """{"op":"names-to-ids","elementIds":["@entity:Invoice"]}""");

        Assert.Equal(SaveOutcome.Saved, result.Outcome);
        Assert.Equal(EditorRepo.InvoiceId, result.Ids.Names["@entity:Invoice"]);
        Assert.Equal(EditorRepo.CustomerId, Text(r, EditorRepo.InvoiceId, "owner"));
        Assert.Equal("Invoice", Text(r, EditorRepo.CustomerId, "owner"));

        // A name in elementIds that finds nothing fails the batch at the item, as any id field.
        var unknown = await ApplyAsync(r, """{"op":"names-to-ids","elementIds":["@entity:Nobody"]}""");
        var missing = Assert.Single(Refusals(unknown));
        Assert.Equal(("MQ1012", "/operations/0/elementIds/0"), (missing.Rule, missing.JsonPointer));

        // An id that is no element refuses the operation.
        var sub = await ApplyAsync(r, $$"""{"op":"names-to-ids","elementIds":["{{IssuedOnId}}"]}""");
        Assert.Equal(("MQ1002", "/operations/0/elementIds/0"), (Assert.Single(Refusals(sub)).Rule, Refusals(sub)[0].JsonPointer));
    }

    [Fact]
    public async Task A_column_matches_a_sibling_by_physical_name_and_a_row_its_code()
    {
        const string NotesPath = ".maquettiste/model/databases/main/tables/notes.json";
        const string EntityType = "01K6BND0000000000000000003";
        await using (var r = Repo(_ => { }))
        {
            Write(r, ".maquettiste/extensions/pairs.json", "extension.json", JsonNode.Parse("""
                { "name": "pairs", "appliesTo": { "kinds": ["column"] },
                  "properties": { "pairedWith": { "type": "string", "x-ref": { "kind": "column", "of": "$element" } } } }
                """)!);
            Edit(r, NotesPath, "table.json", n => Property(n["columns"]![2]!.AsObject(), "pairedWith", "entity_type"));

            var result = await ApplyAsync(r, Convert);

            Assert.True(result.Outcome == SaveOutcome.Saved, string.Join("; ", Refusals(result).Select(d => d.Rule + " " + d.Message)));
            var notes = r.Store.Current!.Get<Table>("01K6BND0000000000000000001")!;
            Assert.Equal(EntityType, notes.Columns[2].Properties["pairedWith"].GetString());
        }

        // Rows of a list by code: the reference-data fixture's allergens.
        using var repo = new TempRepo();
        Integration.E2ERepo.CopyTree(Fixtures.Path("models", "reference-data"), repo.RepoRoot);
        const string IngredientPath = ".maquettiste/model/entities/ingredient.json";
        repo.WriteBytes(".maquettiste/extensions/allergens.json", TestServices.Json.Write(JsonNode.Parse("""
            { "name": "allergens", "appliesTo": { "kinds": ["entity"] },
              "properties": { "mainAllergen": { "type": "string", "x-ref": { "kind": "row", "of": "01JRDA00000000000000000001" } } } }
            """)!, "extension.json", ".maquettiste/extensions/allergens.json"));
        var ingredient = JsonNode.Parse(repo.ReadFile(IngredientPath))!.AsObject();
        Property(ingredient, "mainAllergen", "gluten");
        repo.WriteBytes(IngredientPath, TestServices.Json.Write(ingredient, "entity.json", IngredientPath));
        await using var store = new ModelStore(repo.Options);
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + Convert + "]}"));

        var rows = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);

        Assert.True(rows.Outcome == SaveOutcome.Saved, string.Join("; ", Refusals(rows).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal("01JRDA00000000000000000011", store.Current!.Get<Entity>("01JRDE00000000000000000002")!.Properties["mainAllergen"].GetString());
    }

    [Fact]
    public async Task A_former_code_converts_to_the_replacing_row()
    {
        // The reference-data fixture's allergens, where 'peanut' is a former code of the 'nut' row (next-phase-2-design.md section 3.5).
        using var repo = new TempRepo();
        Integration.E2ERepo.CopyTree(Fixtures.Path("models", "reference-data"), repo.RepoRoot);
        const string AllergenPath = ".maquettiste/model/reference-types/allergen.json";
        const string IngredientPath = ".maquettiste/model/entities/ingredient.json";
        var allergen = JsonNode.Parse(repo.ReadFile(AllergenPath))!.AsObject();
        allergen["formerCodes"] = new JsonArray(new JsonObject { ["code"] = "peanut", ["replacedBy"] = "01JRDA00000000000000000012" });
        repo.WriteBytes(AllergenPath, TestServices.Json.Write(allergen, "reference-type.json", AllergenPath));
        repo.WriteBytes(".maquettiste/extensions/allergens.json", TestServices.Json.Write(JsonNode.Parse("""
            { "name": "allergens", "appliesTo": { "kinds": ["entity"] },
              "properties": { "mainAllergen": { "type": "string", "x-ref": { "kind": "row", "of": "01JRDA00000000000000000001" } } } }
            """)!, "extension.json", ".maquettiste/extensions/allergens.json"));
        var ingredient = JsonNode.Parse(repo.ReadFile(IngredientPath))!.AsObject();
        Property(ingredient, "mainAllergen", "peanut");
        repo.WriteBytes(IngredientPath, TestServices.Json.Write(ingredient, "entity.json", IngredientPath));
        await using var store = new ModelStore(repo.Options);
        var parsed = store.ParseBatch(Encoding.UTF8.GetBytes("{\"operations\":[" + Convert + "]}"));

        var rows = await store.ApplyBatchAsync(parsed.Batch!, ChangeSource.Editor, Ct);

        Assert.True(rows.Outcome == SaveOutcome.Saved, string.Join("; ", Refusals(rows).Select(d => d.Rule + " " + d.Message)));
        Assert.Equal("01JRDA00000000000000000012", store.Current!.Get<Entity>("01JRDE00000000000000000002")!.Properties["mainAllergen"].GetString());
    }

    [Fact]
    public async Task A_stereotype_is_matched_by_key_only()
    {
        await using var r = Repo(n => Property(n, "marker", "Audited"), customer: n => Property(n, "marker", "audited"));

        var byName = await ApplyAsync(r, $$"""{"op":"names-to-ids","elementIds":["{{EditorRepo.InvoiceId}}"]}""");
        var byKey = await ApplyAsync(r, $$"""{"op":"names-to-ids","elementIds":["{{EditorRepo.CustomerId}}"]}""");

        var refusal = Assert.Single(Refusals(byName));
        Assert.Equal(("MQ2010", "/properties/marker"), (refusal.Rule, refusal.JsonPointer));
        Assert.Contains("which finds no stereotype in scope", refusal.Message, StringComparison.Ordinal);
        Assert.Equal(SaveOutcome.Saved, byKey.Outcome);
        Assert.Equal(AuditedId, Text(r, EditorRepo.CustomerId, "marker"));
        Assert.Equal("Audited", Text(r, EditorRepo.InvoiceId, "marker"));
    }
}
