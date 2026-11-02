using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// The csharp-dapper output compiles (warnings as errors, nullable on) in a temporary project that references Dapper from NuGet,
/// and its repositories round-trip rows through an in-memory SQLite database built from the sql-ddl output. The tests skip
/// with a message when NuGet cannot be reached and the packages are not in the local cache.
/// </summary>
public sealed class CompileTests
{
    private const string DapperVersion = "2.1.66";
    private const string SqliteVersion = "10.0.1";

    [Fact]
    public async Task Csharp_dapper_output_for_postgresql_compiles_against_dapper()
    {
        using var repo = PackRepo.Billing();
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
        var project = Path.Combine(repo.Repo.Root, "compile");
        WriteProject(project, repo.PathOf("src/Generated"), program: null);

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Query_rows_with_typed_columns_compile()
    {
        // next-phase-2-design.md section 3.2 (D26): a row record field that reads a column typed by an enum is the enum (a stored code
        // parsed, a stored number cast), and one that reads a column typed by a reference type keeps the code's type, the record's
        // summary naming the type's record in a cref; the output compiles with warnings as errors.
        using var repo = PackRepo.Billing();
        const string events = "01K9X3C0000000000000000001", main = "01J92P0V1QRN2181XM2ZWE02W4", billing = "01J92P0V01KDRN8GX5PGYCNKSX";
        repo.Write(".maquettiste/model/reference-types/invoice-state.json", $$"""
            {"$schema":"../../.schema/v1/reference-type.json","kind":"reference-type","id":"01K9X3A0000000000000000001","name":"InvoiceState",
             "package":"{{billing}}","code":{"id":"01K9X3A0000000000000000002","length":1},"label":{"id":"01K9X3A0000000000000000003","length":40} }
            """);
        repo.Write(".maquettiste/model/seeds/invoice-state/invoice-state.json", """
            {"$schema":"../../../.schema/v1/seed.json","kind":"seed","id":"01K9X3A0000000000000000010","name":"invoiceStates","target":"01K9X3A0000000000000000001",
             "columns":["code","label"],"rows":[{"id":"01K9X3A0000000000000000011","values":["D","Draft"]},{"id":"01K9X3A0000000000000000012","values":["I","Issued"]},
               {"id":"01K9X3A0000000000000000013","values":["P","Paid"]},{"id":"01K9X3A0000000000000000014","values":["V","Void"]}]}
            """);
        repo.Write(".maquettiste/model/databases/main/tables/invoice-events.json", $$"""
            {"$schema":"../../../../.schema/v1/table.json","kind":"table","id":"{{events}}","name":"invoice_events","database":"{{main}}",
             "schema":"01J92P0V1RC04SKQ5353EAKHG2","columns":[
              {"id":"01K9X3C0000000000000000002","name":"id","type":"uuid","nullable":false},
              {"id":"01K9X3C0000000000000000003","name":"status","type":"string","typeRef":{"ref":"01K9X3A0000000000000000001"},"length":1,"nullable":false},
              {"id":"01K9X3C0000000000000000004","name":"invoice_status","type":"string","typeRef":{"ref":"01J92P0V05WQDH2GZG539D9VG1"},"nullable":false},
              {"id":"01K9X3C0000000000000000005","name":"stage","type":"int32","typeRef":{"ref":"01J92P0V05WQDH2GZG539D9VG1"} }],
             "primaryKey":{"columns":["01K9X3C0000000000000000002"]} }
            """);
        repo.Write(".maquettiste/model/databases/main/queries/events-by-status.json", $$"""
            {"$schema":"../../../../.schema/v1/query.json","kind":"query","id":"01K9X3F0000000000000000001","name":"events-by-status","database":"{{main}}",
             "from":{"source":"{{events}}","alias":"e"},
             "select":[{"name":"id","expression":{"column":"e.01K9X3C0000000000000000002"} },{"name":"status","expression":{"column":"e.01K9X3C0000000000000000003"} },
               {"name":"invoiceStatus","expression":{"column":"e.01K9X3C0000000000000000004"} },{"name":"stage","expression":{"column":"e.01K9X3C0000000000000000005"} }]}
            """);
        await repo.GenerateCleanlyAsync(packs: ["csharp-dapper"]);

        var query = File.ReadAllText(Directory.GetFiles(repo.PathOf("src/Generated"), "EventsByStatusQuery*.cs", SearchOption.AllDirectories).Single());
        Assert.Matches(@"/// <summary>One row of query events-by-status\. Status holds a code of <see cref=""global::[\w.]+\.InvoiceState""/>\.</summary>", query);
        Assert.Matches(@"public sealed partial record EventsByStatusRow\(Guid Id, string Status, global::[\w.]+\.InvoiceStatus InvoiceStatus, global::[\w.]+\.InvoiceStatus\? Stage\);", query);
        Assert.Matches(@"new\(id, status, global::[\w.]+\.InvoiceStatusCodes\.Parse\(invoiceStatus\), \(global::[\w.]+\.InvoiceStatus\?\)stage\)", query);
        var project = Path.Combine(repo.Repo.Root, "compile");
        WriteProject(project, repo.PathOf("src/Generated"), program: null);

        await BuildOrSkipAsync(project);
    }

    [Fact]
    public async Task Csharp_dapper_repositories_round_trip_through_the_sql_ddl_schema_on_sqlite()
    {
        using var repo = PackRepo.BillingDialects();
        await repo.GenerateCleanlyAsync();
        var project = Path.Combine(repo.Repo.Root, "roundtrip");
        WriteProject(project, repo.PathOf("src/Generated"), RoundTripProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet,
            [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), repo.PathOf("db/local/schema.sql"), repo.PathOf("db/local/seed.sql")],
            project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("round trip ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task Csharp_dapper_binding_repositories_compile_for_postgresql_and_round_trip_on_sqlite()
    {
        // Repositories from bindings (erratum E43): the PostgreSQL output compiles, and on SQLite every repository operation runs.
        using (var postgres = PackRepo.Bindings())
        {
            await postgres.GenerateCleanlyAsync(packs: ["csharp-dapper"]);
            var library = Path.Combine(postgres.Repo.Root, "compile");
            WriteProject(library, postgres.PathOf("src/Generated"), program: null);
            await BuildOrSkipAsync(library);
        }

        using var repo = PackRepo.Bindings(sqlite: true);
        await repo.GenerateCleanlyAsync();
        var project = Path.Combine(repo.Repo.Root, "roundtrip");
        WriteProject(project, repo.PathOf("src/Generated"), BindingRoundTripProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet,
            [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), repo.PathOf("db/main/schema.sql")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("bindings round trip ok", StringComparison.Ordinal), run.Output);
    }

    [Fact]
    public async Task The_seed_data_loader_applies_the_seed_data_files_to_sqlite_once_and_converging()
    {
        // The seed-data pack's CSVs for the SQLite database, loaded by csharp-dapper's SeedDataLoader (seedLoader on).
        using var repo = TableSeedSqlTests.DataRepo("csv");
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
            settings["packs"]!["csharp-dapper"]!["parameters"] = new System.Text.Json.Nodes.JsonObject { ["seedLoader"] = true });
        await repo.GenerateCleanlyAsync();
        Assert.True(File.Exists(repo.PathOf("src/Generated/SeedDataLoader.g.cs")));
        var project = Path.Combine(repo.Repo.Root, "loader");
        WriteProject(project, repo.PathOf("src/Generated"), SeedLoaderProgram);

        await BuildOrSkipAsync(project);
        var run = await ProcessRunner.RunAsync(ProcessRunner.Dotnet,
            [Path.Combine(project, "bin", "Debug", "net10.0", "RoundTrip.dll"), repo.PathOf("db/local/schema.sql"), repo.PathOf("data/local")], project, TimeSpan.FromMinutes(2));
        Assert.True(run.ExitCode == 0 && run.Output.Contains("seed loader ok", StringComparison.Ordinal), run.Output);
    }

    private const string SeedLoaderProgram = """
        using System;
        using System.IO;
        using System.Linq;
        using Dapper;
        using Microsoft.Data.Sqlite;
        using App.Model;

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(args[0]);
            command.ExecuteNonQuery();
        }

        void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Seed loader failed: " + what);
        }

        var loader = new SeedDataLoader(connection, SeedDialect.Sqlite, args[1]);
        Check(loader.SeedHash.Length == 64, "the seed hash");
        Check(await loader.LoadAsync() >= 2, "the shared rows are written");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM loc_currencies") == 2, "the shared rows: two currencies");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM loc_price_lists") == 0, "dev rows wait for dev");
        await loader.LoadAsync("dev");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM loc_price_lists") == 2, "the dev rows");
        await connection.ExecuteAsync("UPDATE loc_currencies SET name = 'Changed' WHERE code = 'EUR'");
        await connection.ExecuteAsync("INSERT INTO loc_currencies (code, name) VALUES ('XXX', 'Stray')");
        await connection.ExecuteAsync("UPDATE loc_price_lists SET label = 'Mine' WHERE id = 1");
        await loader.LoadAsync("dev");
        var names = (await connection.QueryAsync<string>("SELECT code || ':' || name FROM loc_currencies ORDER BY code")).ToList();
        Check(names.SequenceEqual(["EUR:Euro", "USD:US dollar"]), "converge restores the name and deletes the stray row: " + string.Join(",", names));
        Check(await connection.ExecuteScalarAsync<string>("SELECT label FROM loc_price_lists WHERE id = 1") == "Mine", "once leaves an edited row alone");
        Console.WriteLine("seed loader ok");
        """;

    private const string BindingRoundTripProgram = """
        using System;
        using System.IO;
        using System.Linq;
        using Dapper;
        using Microsoft.Data.Sqlite;
        using Shop.Model;
        using Shop.Model.Shop;

        // One to one with an identity key, a soft delete, two entities sharing a table through a constant, and a read-only entity
        // over a query, against an in-memory SQLite database built from the sql-ddl schema.
        DapperTypeHandlers.Register();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(args[0]);
            command.ExecuteNonQuery();
        }

        void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Round trip failed: " + what);
        }

        var products = new ProductRepository(connection);
        var product = new Product { Sku = "SKU-1", Name = "Widget", Price = 9.99m };
        await products.InsertAsync(product);
        Check(product.Id > 0, "identity key returned by the insert");
        product.Price = 12.5m;
        Check(await products.UpdateAsync(product), "update by key");
        Check((await products.GetAsync(product.Id))!.Price == 12.5m, "get after update");
        Check((await products.ListAsync()).Count == 1, "list");

        var customers = new CustomerRepository(connection);
        var ada = new Customer { Name = "Ada", Email = "ada@example.com" };
        var bob = new Customer { Name = "Bob" };
        await customers.InsertAsync(ada);
        await customers.InsertAsync(bob);
        Check(ada.Id != Guid.Empty, "uuid-v7 key assigned on insert");
        Check(await customers.DeleteAsync(bob.Id), "soft delete");
        Check(await customers.GetAsync(bob.Id) is null, "a soft-deleted row is not read");
        Check((await customers.ListAsync()).Select(c => c.Name).SequenceEqual(["Ada"]), "list leaves soft-deleted rows out");
        Check(await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM customers") == 2, "the soft-deleted row stays");

        var productNotes = new ProductNoteRepository(connection);
        var customerNotes = new CustomerNoteRepository(connection);
        var first = new ProductNote { Sku = "SKU-1", Body = "Popular" };
        var second = new CustomerNote { CustomerId = ada.Id.ToString(), Body = "Pays on time" };
        await productNotes.InsertAsync(first);
        await customerNotes.InsertAsync(second);
        Check(await connection.ExecuteScalarAsync<string>("SELECT entity_type FROM notes WHERE id = @id", new { id = first.Id }) == "product", "the constant is set on insert");
        Check((await productNotes.ListAsync()).Single().Body == "Popular", "a shared table reads its own rows only");
        Check(await productNotes.GetAsync(second.Id) is null, "the constant filters reads by key");
        Check((await customerNotes.GetAsync(second.Id))!.CreatedAt is not null, "the database fills created_at");
        Check(!await productNotes.DeleteAsync(second.Id), "the constant filters deletes");
        Check(await customerNotes.DeleteAsync(second.Id), "delete by key");

        var summaries = new CustomerSummaryRepository(connection);
        var active = await summaries.ListAsync("%");
        Check(active.Select(s => s.DisplayName).SequenceEqual(["Ada"]), "a read-only entity over a query");
        Check((await summaries.GetAsync("A%", ada.Id))!.Email == "ada@example.com", "get by key over a query");
        Console.WriteLine("bindings round trip ok");
        """;

    private static void WriteProject(string folder, string generated, string? program)
    {
        Directory.CreateDirectory(folder);
        var sqlite = program is null ? "" : $"""    <PackageReference Include="Microsoft.Data.Sqlite" Version="{SqliteVersion}" />""";
        File.WriteAllText(Path.Combine(folder, "RoundTrip.csproj"), $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>{(program is null ? "Library" : "Exe")}</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <Nullable>enable</Nullable>
                <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
                <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                <NuGetAudit>false</NuGetAudit>
                <UseSharedCompilation>false</UseSharedCompilation>
              </PropertyGroup>
              <ItemGroup>
                <Compile Include="{generated}/**/*.cs" />
                <Compile Include="Program.cs" Condition="Exists('Program.cs')" />
                <PackageReference Include="Dapper" Version="{DapperVersion}" />
            {sqlite}
              </ItemGroup>
            </Project>

            """);
        // Keep the repo's global.json, Directory.Build.props and central package versions out of the temporary project.
        File.WriteAllText(Path.Combine(folder, "Directory.Build.props"), "<Project />\n");
        File.WriteAllText(Path.Combine(folder, "Directory.Packages.props"), "<Project />\n");
        if (program is not null)
            File.WriteAllText(Path.Combine(folder, "Program.cs"), program);
    }

    private static async Task BuildOrSkipAsync(string project)
    {
        var restore = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, ["restore", "RoundTrip.csproj"], project, TimeSpan.FromMinutes(5));
        if (restore.ExitCode != 0)
        {
            if (IsNuGetUnreachable(restore.Output))
                Assert.Skip("NuGet is unreachable and Dapper " + DapperVersion + " is not in the local package cache, so the generated C# was not compiled:\n" + restore.Output);
            Assert.Fail("dotnet restore failed:\n" + restore.Output);
        }

        var build = await ProcessRunner.RunAsync(ProcessRunner.Dotnet, ["build", "RoundTrip.csproj", "--no-restore", "-nodeReuse:false"], project, TimeSpan.FromMinutes(5));
        Assert.True(build.ExitCode == 0, "The generated C# does not compile:\n" + build.Output);
    }

    private static bool IsNuGetUnreachable(string output) =>
        output.Contains("NU1301", StringComparison.Ordinal)
        || output.Contains("NU1801", StringComparison.Ordinal)
        || output.Contains("Unable to load the service index", StringComparison.Ordinal)
        || output.Contains("No such host", StringComparison.OrdinalIgnoreCase)
        || output.Contains("Name or service not known", StringComparison.OrdinalIgnoreCase);

    private const string RoundTripProgram = """
        using System;
        using System.IO;
        using System.Linq;
        using Microsoft.Data.Sqlite;
        using App.Model;
        using App.Model.Billing;
        using App.Model.Billing.Catalog;

        // Builds an in-memory SQLite database from the sql-ddl schema and seed scripts, then drives every generated repository
        // operation: insert (with a database identity key, a generated uuid key and a generated ulid key), get, list, update and delete,
        // and a generated query with a list parameter, paging and a collection.
        DapperTypeHandlers.Register();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = File.ReadAllText(args[0]) + "\n" + File.ReadAllText(args[1]);
            command.ExecuteNonQuery();
        }

        void Check(bool condition, string what)
        {
            if (!condition)
                throw new InvalidOperationException("Round trip failed: " + what);
        }

        var customers = new CustomerRepository(connection);
        var customer = new Customer { Name = "Ada", Email = "ada@example.com", CustomerSince = new DateOnly(2024, 1, 2), CreatedAt = DateTimeOffset.UnixEpoch };
        await customers.InsertAsync(customer);
        Check(customer.Id != Guid.Empty, "uuid-v7 key assigned on insert");

        var products = new ProductRepository(connection);
        var product = new Product { Sku = "SKU-1", Name = "Widget", ListPrice = new Money { Amount = 9.99m, Currency = "EUR" } };
        await products.InsertAsync(product);

        var invoices = new InvoiceRepository(connection);
        var invoice = new Invoice
        {
            Number = "INV-1", IssuedOn = new DateOnly(2024, 3, 4), Total = new Money { Amount = 12.5m, Currency = "EUR" },
            Status = InvoiceStatus.Issued, Notes = "first", CreatedAt = DateTimeOffset.UnixEpoch, CustomerId = customer.Id,
        };
        await invoices.InsertAsync(invoice);
        var read = await invoices.GetAsync(invoice.Id);
        Check(read is not null, "get after insert");
        Check(read!.Total == invoice.Total && read.Status == InvoiceStatus.Issued && read.IssuedOn == invoice.IssuedOn, "values after insert");
        Check(read.CustomerId == customer.Id && read.Notes == "first", "foreign key and text after insert");

        var lines = new InvoiceLineRepository(connection);
        var line = new InvoiceLine { Quantity = 2, UnitPrice = new Money { Amount = 1.5m, Currency = "EUR" }, InvoiceId = invoice.Id, ProductId = product.Id };
        await lines.InsertAsync(line);
        Check(line.Id is { Length: 26 } && line.Id.All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)), "ulid key assigned on insert");
        Check((await lines.GetAsync(line.Id))?.Quantity == 2, "invoice line");

        // The generated query: the customer's issued invoices, each with its lines from the second statement.
        var found = await new App.Model.Queries.LocalInvoicesByCustomerQuery(connection).ExecuteAsync(customer.Id, [1, 2]);
        Check(found.Count == 1 && found[0].Item.Id == invoice.Id && found[0].Item.Status == InvoiceStatus.Issued, "query rows");
        Check(found[0].Lines.Count == 1 && found[0].Lines[0].Id == line.Id && found[0].Lines[0].Quantity == 2, "query collection");
        Check(found[0].Item.CustomerId == customer.Id && found[0].Item.Total == invoice.Total, "query foreign key and value object members");
        Check(found[0].SameLines.Count == 1 && found[0].SameLines[0].Id == line.Id && found[0].SameLines[0].Quantity == 2, "query collection with two keys");
        Check((await new App.Model.Queries.LocalInvoicesByCustomerQuery(connection).ExecuteAsync(customer.Id, [3])).Count == 0, "query filter");

        read.Status = InvoiceStatus.Paid;
        read.Total = null;
        Check(await invoices.UpdateAsync(read), "update");
        var updated = await invoices.GetAsync(invoice.Id);
        Check(updated!.Status == InvoiceStatus.Paid && updated.Total is null, "values after update");

        var payments = new PaymentRepository(connection);
        var first = new Payment { Amount = new Money { Amount = 5m, Currency = "EUR" }, ReceivedAt = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch };
        var second = new Payment { Amount = new Money { Amount = 6m, Currency = "EUR" }, ReceivedAt = DateTimeOffset.UnixEpoch, CreatedAt = DateTimeOffset.UnixEpoch };
        await payments.InsertAsync(first);
        await payments.InsertAsync(second);
        Check(first.Id > 0 && second.Id > first.Id, "identity keys returned by insert");
        var page = await payments.ListAsync(skip: 1, take: 10);
        Check(page.Count == 1 && page[0].Id == second.Id && page[0].Amount.Amount == 6m, "list paging");

        var notes = new CreditNoteRepository(connection);
        var note = new CreditNote
        {
            Number = "CN-1", IssuedOn = new DateOnly(2024, 5, 6), Status = InvoiceStatus.Draft, CreatedAt = DateTimeOffset.UnixEpoch,
            CustomerId = customer.Id, Reason = "damaged",
        };
        await notes.InsertAsync(note);
        Check((await notes.GetAsync(note.Id))?.Reason == "damaged", "derived entity (table per hierarchy)");
        Check(await invoices.GetAsync(note.Id) is null && (await invoices.ListAsync()).All(i => i.Id != note.Id), "discriminator filter");

        Check(await lines.DeleteAsync(line.Id), "delete line");
        Check(await invoices.DeleteAsync(invoice.Id), "delete");
        Check(await invoices.GetAsync(invoice.Id) is null, "get after delete");
        Check(!await invoices.DeleteAsync(invoice.Id), "second delete");
        Check(BillingRepositories.All.Count == 8 && CatalogRepositories.All.Single().Implementation == typeof(ProductRepository), "registrations");
        Console.WriteLine("round trip ok");

        """;
}
