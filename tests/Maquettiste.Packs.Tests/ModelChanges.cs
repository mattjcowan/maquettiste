using System.Text.Json.Nodes;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// Model edits the migration tests apply to the billing fixture between generate runs. Since 0.29.0 a table holds what it declares, so
/// each edit changes the entity and, in every database the entity is bound to, its table, its binding and the relation mappings as a
/// team would: a migration follows the tables.
/// </summary>
internal static class ModelChanges
{
    private const string CustomerPath = ".maquettiste/model/entities/customer.json";
    private const string InvoicePath = ".maquettiste/model/entities/invoice.json";

    /// <summary>A fresh id for the n-th column, key or index an edit adds in the i-th binding of an entity.</summary>
    private static string NewId(int n, int i) => "01J92P0V2B" + (n * 10 + i).ToString("D16", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The repo-relative path of a table file by its id.</summary>
    private static string TablePath(PackRepo repo, string id) =>
        Path.GetRelativePath(repo.Repo.RepoRoot, Directory.EnumerateFiles(repo.PathOf(".maquettiste/model/databases"), "*.json", SearchOption.AllDirectories)
            .Single(f => f.Contains($"{Path.DirectorySeparatorChar}tables{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && (string?)JsonNode.Parse(File.ReadAllText(f))!["id"] == id));

    /// <summary>An entity's bindings, each with its database id and table file path, in file order.</summary>
    private static List<(string Database, string Table, string TablePath)> Bindings(PackRepo repo, string entityPath) =>
        [.. JsonNode.Parse(repo.Read(entityPath))!["bindings"]!.AsArray()
            .Select(b => ((string)b!["database"]!, (string)b["source"]!, TablePath(repo, (string)b["source"]!)))];

    /// <summary>The column a binding of an entity maps to an attribute, by the attribute's id.</summary>
    private static string ColumnOf(PackRepo repo, string entityPath, string database, string attribute) =>
        (string)JsonNode.Parse(repo.Read(entityPath))!["bindings"]!.AsArray().Single(b => (string?)b!["database"] == database)!["fields"]!.AsArray()
            .Single(f => (string?)f!["attribute"] == attribute)!["column"]!;

    /// <summary>Adds or removes a binding field of an entity in a database.</summary>
    private static void EditFields(PackRepo repo, string entityPath, string database, Action<JsonArray> edit) =>
        repo.EditJson(entityPath, entity => edit(entity["bindings"]!.AsArray().Single(b => (string?)b!["database"] == database)!["fields"]!.AsArray()));

    /// <summary>Removes an attribute's column from each table the entity is bound to, with its field.</summary>
    private static void DropStored(PackRepo repo, string entityPath, string attribute)
    {
        foreach (var (database, _, tablePath) in Bindings(repo, entityPath))
        {
            var column = ColumnOf(repo, entityPath, database, attribute);
            EditFields(repo, entityPath, database, fields => fields.Remove(fields.Single(f => (string?)f!["attribute"] == attribute)));
            repo.EditJson(tablePath, table =>
            {
                var columns = table["columns"]!.AsArray();
                columns.Remove(columns.Single(c => (string?)c!["id"] == column));
            });
        }
    }

    /// <summary>Adds a column for a new attribute to each table the entity is bound to, with its field.</summary>
    private static void AddStored(PackRepo repo, string entityPath, string attribute, int n, Func<string, JsonObject> column, Action<JsonNode, string>? more = null)
    {
        var i = 0;
        foreach (var (database, _, tablePath) in Bindings(repo, entityPath))
        {
            var id = NewId(n, i++);
            repo.EditJson(tablePath, table =>
            {
                table["columns"]!.AsArray().Add(column(id));
                more?.Invoke(table, id);
            });
            EditFields(repo, entityPath, database, fields => fields.Add(new JsonObject { ["attribute"] = attribute, ["column"] = id }));
        }
    }

    /// <summary>
    /// Customer changes: drop <c>customerSince</c>, add <c>phone</c>, rename <c>name</c> to <c>fullName</c> and widen it, add an
    /// indexed <c>region</c> and a required <c>priority</c> with a default; and <c>refers to</c> cascades on delete. Each customers table
    /// changes the same way (customer_since dropped; phone, region with ix_customers_region and priority added; name renamed full_name
    /// and widened), and the foreign key each database's mapping of <c>refers to</c> names cascades.
    /// </summary>
    /// <param name="repo">The repo.</param>
    public static void ChangeCustomer(PackRepo repo)
    {
        const string since = "01J92P0V0P4MBJRM6QN6H0RSVP", nameAttribute = "01J92P0V0MS09YFZHX07JQ3KMN";
        DropStored(repo, CustomerPath, since);
        foreach (var (database, _, tablePath) in Bindings(repo, CustomerPath))
        {
            var name = ColumnOf(repo, CustomerPath, database, nameAttribute);
            repo.EditJson(tablePath, table =>
            {
                var column = table["columns"]!.AsArray().Single(c => (string?)c!["id"] == name)!;
                column["name"] = "full_name";
                column["length"] = 200;
            });
        }

        AddStored(repo, CustomerPath, "01J92P0V2B0000000000000001", 1,
            id => new JsonObject { ["id"] = id, ["name"] = "phone", ["type"] = "string", ["length"] = 30 });
        var region = 0;
        AddStored(repo, CustomerPath, "01J92P0V2B0000000000000002", 2,
            id => new JsonObject { ["id"] = id, ["name"] = "region", ["type"] = "string", ["length"] = 40 },
            (table, id) =>
            {
                var indexes = table["indexes"] as JsonArray ?? [];
                indexes.Add(new JsonObject { ["id"] = NewId(7, region++), ["name"] = "ix_customers_region", ["columns"] = new JsonArray(new JsonObject { ["column"] = id }) });
                table["indexes"] = indexes;
            });
        AddStored(repo, CustomerPath, "01J92P0V2B0000000000000003", 3,
            id => new JsonObject { ["id"] = id, ["name"] = "priority", ["type"] = "int32", ["nullable"] = false, ["default"] = 0 });
        repo.EditJson(CustomerPath, customer =>
        {
            var attributes = customer["attributes"]!.AsArray();
            attributes.Remove(attributes.First(a => (string?)a!["name"] == "customerSince"));
            attributes.Add(new JsonObject { ["id"] = "01J92P0V2B0000000000000001", ["name"] = "phone", ["type"] = "string", ["length"] = 30 });
            attributes.Add(new JsonObject { ["id"] = "01J92P0V2B0000000000000002", ["name"] = "region", ["type"] = "string", ["length"] = 40, ["indexed"] = true });
            attributes.Add(new JsonObject { ["id"] = "01J92P0V2B0000000000000003", ["name"] = "priority", ["type"] = "int32", ["required"] = true, ["default"] = 0 });
            var name = attributes.First(a => (string?)a!["name"] == "name")!;
            name["name"] = "fullName";
            name["length"] = 200;
        });
        repo.EditJson(".maquettiste/model/relations/refers-to.json", relation => relation["ends"]![1]!["onDelete"] = "cascade");
        var refersTo = (string)JsonNode.Parse(repo.Read(".maquettiste/model/relations/refers-to.json"))!["id"]!;
        foreach (var file in Directory.EnumerateFiles(repo.PathOf(".maquettiste/model/mappings"), "*.json", SearchOption.AllDirectories))
        {
            var mapping = JsonNode.Parse(File.ReadAllText(file))!;
            if ((string?)mapping["relation"] != refersTo || (string?)mapping["foreignKey"] is not { } key)
                continue;
            var tablePath = Path.GetRelativePath(repo.Repo.RepoRoot, Directory.EnumerateFiles(repo.PathOf(".maquettiste/model/databases"), "*.json", SearchOption.AllDirectories)
                .Single(f => JsonNode.Parse(File.ReadAllText(f))!["foreignKeys"] is JsonArray keys && keys.Any(k => (string?)k!["id"] == key)));
            repo.EditJson(tablePath, table => table["foreignKeys"]!.AsArray().Single(k => (string?)k!["id"] == key)!["onDelete"] = "cascade");
        }
    }

    /// <summary>Edits the column each table Customer is bound to holds an attribute in.</summary>
    /// <param name="repo">The repo.</param>
    /// <param name="attribute">The attribute's id.</param>
    /// <param name="edit">The edit of the column.</param>
    public static void EditCustomerColumn(PackRepo repo, string attribute, Action<JsonNode> edit)
    {
        foreach (var (database, _, tablePath) in Bindings(repo, CustomerPath))
        {
            var column = ColumnOf(repo, CustomerPath, database, attribute);
            repo.EditJson(tablePath, table => edit(table["columns"]!.AsArray().Single(c => (string?)c!["id"] == column)!));
        }
    }

    /// <summary>
    /// Edits the column each table Customer is bound to holds <c>customerSince</c> in (and, with <paramref name="newId"/>, gives it that
    /// id, its binding field following).
    /// </summary>
    /// <param name="repo">The repo.</param>
    /// <param name="edit">The edit of the column.</param>
    /// <param name="newId">A new id for the column in each table (suffixed by the binding's position), or <see langword="null"/>.</param>
    public static void EditCustomerSince(PackRepo repo, Action<JsonNode> edit, string? newId = null)
    {
        const string since = "01J92P0V0P4MBJRM6QN6H0RSVP";
        var i = 0;
        foreach (var (database, _, tablePath) in Bindings(repo, CustomerPath))
        {
            var column = ColumnOf(repo, CustomerPath, database, since);
            var id = newId is null ? null : newId[..^1] + i++.ToString(System.Globalization.CultureInfo.InvariantCulture);
            repo.EditJson(tablePath, table =>
            {
                var entry = table["columns"]!.AsArray().Single(c => (string?)c!["id"] == column)!;
                edit(entry);
                if (id is not null)
                    entry["id"] = id;
            });
            if (id is not null)
                EditFields(repo, CustomerPath, database, fields => fields.Single(f => (string?)f!["attribute"] == since)!["column"] = id);
        }
    }

    /// <summary>Removes the <c>priority</c> attribute <see cref="ChangeCustomer"/> added (a column with a default), and its columns.</summary>
    /// <param name="repo">The repo.</param>
    public static void RemovePriority(PackRepo repo)
    {
        DropStored(repo, CustomerPath, "01J92P0V2B0000000000000003");
        repo.EditJson(CustomerPath, customer =>
        {
            var attributes = customer["attributes"]!.AsArray();
            attributes.Remove(attributes.First(a => (string?)a!["name"] == "priority"));
        });
    }

    /// <summary>
    /// An optional relation from Customer to Invoice (a featured invoice). Invoice already references Customer, so the two tables
    /// form a foreign-key cycle. In each database both are bound to, the customers table gets <c>featured_invoice_id</c> with
    /// <c>fk_customers_featured_invoice_id</c> to the invoices table, and a relation mapping names that key.
    /// </summary>
    /// <param name="repo">The repo.</param>
    public static void AddFeaturedInvoice(PackRepo repo)
    {
        WriteFeatures(repo);
        var invoices = Bindings(repo, InvoicePath).ToDictionary(b => b.Database, b => b.Table, StringComparer.Ordinal);
        var i = 0;
        foreach (var (database, _, tablePath) in Bindings(repo, CustomerPath))
        {
            var (column, key) = (NewId(4, i), NewId(5, i));
            i++;
            if (!invoices.TryGetValue(database, out var target))
                continue;
            repo.EditJson(tablePath, table =>
            {
                table["columns"]!.AsArray().Add(new JsonObject { ["id"] = column, ["name"] = "featured_invoice_id", ["type"] = "uuid" });
                var keys = table["foreignKeys"] as JsonArray ?? [];
                keys.Add(new JsonObject { ["id"] = key, ["name"] = "fk_customers_featured_invoice_id", ["columns"] = new JsonArray(column), ["referencesTable"] = target });
                table["foreignKeys"] = keys;
            });
            repo.Write($".maquettiste/model/mappings/features-{i}.json", $$"""
                { "kind": "mapping", "id": "{{NewId(6, i)}}", "name": "features {{i}}", "database": "{{database}}", "relation": "01J92P0V2D0000000000000001", "foreignKey": "{{key}}" }
                """);
        }
    }

    private static void WriteFeatures(PackRepo repo) => repo.Write(".maquettiste/model/relations/features.json", """
        {
          "$schema": "../../.schema/v1/relation.json",
          "kind": "relation",
          "id": "01J92P0V2D0000000000000001",
          "name": "features",
          "package": "01J92P0V01KDRN8GX5PGYCNKSX",
          "ends": [
            {
              "id": "01J92P0V2D0000000000000002",
              "entity": "01J92P0V0ETQKXXP951CMMNHH3",
              "role": "featuringCustomers"
            },
            {
              "id": "01J92P0V2D0000000000000003",
              "entity": "01J92P0V0FJ23CGSNKM7P1W5V7",
              "role": "featuredInvoice",
              "navigation": "featuredInvoice",
              "min": 0,
              "max": 1
            }
          ]
        }

        """);

    /// <summary>A new InvoiceStatus member, <c>Disputed</c> (4, code X), of an enum stored as its codes.</summary>
    /// <param name="repo">The repo.</param>
    public static void AddDisputedStatus(PackRepo repo) =>
        repo.EditJson(".maquettiste/model/enums/invoice-status.json", status =>
            status["members"]!.AsArray().Add(new JsonObject { ["id"] = "01J92P0V2E0000000000000001", ["name"] = "Disputed", ["value"] = 4, ["code"] = "X" }));

    /// <summary>A new PaymentMethod row, <c>voucher</c>: a new row of the lookup table and a new code in the CHECKs.</summary>
    /// <param name="repo">The repo.</param>
    public static void AddVoucherMethod(PackRepo repo) =>
        repo.EditJson(".maquettiste/model/seeds/payment-method/payment-method.json", seed =>
            seed["rows"]!.AsArray().Add(new JsonObject { ["id"] = "01J92P0V2F0000000000000014", ["values"] = new JsonArray("voucher", "Voucher") }));
}
