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
        Check(BillingRepositories.All.Count == 5 && CatalogRepositories.All.Single().Implementation == typeof(ProductRepository), "registrations");
        Console.WriteLine("round trip ok");

        """;
}
