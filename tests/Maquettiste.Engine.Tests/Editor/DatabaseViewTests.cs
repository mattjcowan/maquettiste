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
                "attributePath", "isPrimaryKey", "isForeignKey", "isDiscriminator", "position", "default", "computedStored", "sequenceId", "collation",
                "comment", "displayName", "pluralName", "description", "stereotypes", "tags", "category", "properties", "generation"],
            column.Select(p => p.Key));
    }
}
