using Maquettiste.Engine;
using Maquettiste.Engine.Diagnostics;
using Maquettiste.Engine.Pipeline;
using System.Text.Json.Nodes;
using Maquettiste.Testing;

namespace Maquettiste.Packs.Tests;

/// <summary>
/// A temporary repo holding the billing fixture model and the example packs from <c>packs/</c>, as <c>maquettiste init</c> would
/// lay them out (<c>db</c> committed, <c>src/Generated</c> built). The <c>dialects</c> variant adds a SQL Server database
/// (<c>reporting</c>) and a SQLite database (<c>local</c>) next to the PostgreSQL one, so every dialect branch of the packs runs,
/// a <c>PaymentMethod</c> reference type used by <c>Payment.method</c> (a CHECK, and a lookup table in <c>local</c>), and a
/// <c>CreditNote</c> entity derived from <c>Invoice</c> (table per hierarchy, so a
/// discriminator column), and a custom type <c>Lsn</c> (binary, length 8) with its own native types, <c>pg_lsn</c> on PostgreSQL and
/// <c>binary(8)</c> on SQL Server, used by <c>Payment.ledgerPosition</c>; and in each database a routine, a domain type that
/// <c>customers.email</c> uses and a trigger (see <see cref="AddDatabaseObjects"/>).
/// </summary>
internal sealed class PackRepo : IDisposable
{
    /// <summary>The example packs, in the order the engine runs them.</summary>
    public static readonly IReadOnlyList<string> Packs = ["csharp-dapper", "sql-ddl"];

    private const string DialectSettings = """
        {
          "$schema": ".schema/v1/maquettiste.json",
          "formatVersion": 1,
          "name": "billing",
          "outputs": {
            "allow": [
              {
                "path": "db"
              },
              {
                "path": "src/Generated"
              }
            ]
          },
          "conventions": {
            "referenceStorage": {
              "strategy": "check"
            }
          },
          "databases": {
            "local": {
              "referenceStorage": {
                "strategy": "lookup-table"
              }
            }
          },
          "referenceData": {
            "strategies": {
              "check": {
                "description": "CHECK (col IN (...codes))"
              },
              "lookup-table": {
                "description": "Table keyed by code, FK from each column",
                "collections": true
              }
            }
          },
          "packs": {
            "csharp-dapper": {
              "output": "src/Generated"
            },
            "sql-ddl": {
              "output": "db"
            }
          }
        }

        """;

    private const string PaymentMethod = """
        {
          "$schema": "../../.schema/v1/reference-type.json",
          "kind": "reference-type",
          "id": "01J92P0V2F0000000000000001",
          "name": "PaymentMethod",
          "description": "How a payment was made.",
          "code": {
            "id": "01J92P0V2F0000000000000002",
            "length": 16
          },
          "label": {
            "id": "01J92P0V2F0000000000000003",
            "length": 64
          }
        }

        """;

    private const string PaymentMethodSeed = """
        {
          "$schema": "../../../.schema/v1/seed.json",
          "kind": "seed",
          "id": "01J92P0V2F0000000000000010",
          "name": "PaymentMethod",
          "target": "01J92P0V2F0000000000000001",
          "columns": ["code", "label"],
          "rows": [
            { "id": "01J92P0V2F0000000000000011", "values": ["card", "Card"] },
            { "id": "01J92P0V2F0000000000000012", "values": ["transfer", "Bank transfer"] },
            { "id": "01J92P0V2F0000000000000013", "values": ["cash", "Cash"] }
          ]
        }

        """;

    private const string CreditNote = """
        {
          "$schema": "../../.schema/v1/entity.json",
          "kind": "entity",
          "id": "01J92P0V2C0000000000000001",
          "name": "CreditNote",
          "package": "01J92P0V01KDRN8GX5PGYCNKSX",
          "base": "01J92P0V0FJ23CGSNKM7P1W5V7",
          "attributes": [
            {
              "id": "01J92P0V2C0000000000000002",
              "name": "reason",
              "type": "string",
              "length": 200,
              "required": true
            }
          ]
        }

        """;

    private const string Lsn = """
        {
          "$schema": "../../.schema/v1/scalar-type.json",
          "kind": "scalar-type",
          "id": "01J92P0V2G0000000000000001",
          "name": "Lsn",
          "package": "01J92P0V01KDRN8GX5PGYCNKSX",
          "description": "A position in the ledger's write-ahead log.",
          "base": "binary",
          "length": 8,
          "nativeTypes": {
            "postgresql": "pg_lsn",
            "sqlserver": "binary(8)"
          }
        }

        """;

    private PackRepo(bool dialects)
    {
        Repo = new TempRepo();
        CopyTree(Fixtures.Path("models", "billing", ".maquettiste"), Repo.ModelRoot);
        foreach (var pack in Packs)
            CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", pack), Path.Combine(Repo.ModelRoot, "templates", pack));
        // The shared fixture's overlays compare the status column with enum values, but the column stores the member codes
        // (varchar(1)), which PostgreSQL rejects; compare with the codes so the generated DDL runs.
        Patch(".maquettiste/model/databases/main/tables/01j92p0v1t0j6rh4my9h81nyb4.json", "status in (0, 1, 2, 3)", "status in ('D', 'I', 'P', 'V')");
        Patch(".maquettiste/model/databases/main/views/outstanding-invoices.json", "where status = 1", "where status = 'I'");
        if (dialects)
        {
            Repo.WriteFile(".maquettiste/maquettiste.json", DialectSettings);
            Repo.WriteFile(".maquettiste/model/databases/reporting/database.json", Database("01J92P0V2A0000000000000001", "reporting", "sqlserver"));
            Repo.WriteFile(".maquettiste/model/databases/local/database.json", Database("01J92P0V2A0000000000000002", "local", "sqlite"));
            Repo.WriteFile(".maquettiste/model/entities/credit-note.json", CreditNote);
            // Reference data: Payment.method names a PaymentMethod row, stored as a CHECK by the project convention and as a
            // lookup table in the SQLite database (the database's referenceStorage), where enums once used a lookup table.
            Repo.WriteFile(".maquettiste/model/reference-types/payment-method.json", PaymentMethod);
            Repo.WriteFile(".maquettiste/model/seeds/payment-method/payment-method.json", PaymentMethodSeed);
            EditJson(".maquettiste/model/entities/payment.json", payment => payment["attributes"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "01J92P0V2F0000000000000020",
                ["name"] = "method",
                ["type"] = new JsonObject { ["ref"] = "01J92P0V2F0000000000000001" },
            }));
            // A custom type with native types of its own: pg_lsn on PostgreSQL, binary(8) on SQL Server, the base's blob on SQLite.
            Repo.WriteFile(".maquettiste/model/types/lsn.json", Lsn);
            EditJson(".maquettiste/model/entities/payment.json", payment => payment["attributes"]!.AsArray().Add(new JsonObject
            {
                ["id"] = "01J92P0V2G0000000000000002",
                ["name"] = "ledgerPosition",
                ["type"] = new JsonObject { ["ref"] = "01J92P0V2G0000000000000001" },
            }));
            AddDatabaseObjects();
            AddLocalQuery();
        }
    }

    /// <summary>
    /// A copy of the billing fixture's InvoicesByCustomer query on the SQLite database, which CompileTests runs: its sources are the
    /// synthesized tables' keys there, its columns the same column keys, its statuses the stored member values. It also fills the
    /// invoice's customer foreign key by its relation end and its Money total by its members, filters on a decimal parameter with a
    /// default (cast, since SQLite binds a decimal as text) and a typed decimal literal, and has a second collection keyed twice (the several-key form of the statement).
    /// </summary>
    private void AddLocalQuery()
    {
        var query = JsonNode.Parse(File.ReadAllText(Path.Combine(Repo.ModelRoot, "model", "databases", "main", "queries", "invoices-by-customer.json")))!.AsObject();
        query["id"] = "01J92P0V2Q0000000000000001";
        query["name"] = "LocalInvoicesByCustomer";
        query["database"] = LocalId;
        query["from"]!["source"] = "01J92P0V0FJ23CGSNKM7P1W5V7@" + LocalId;
        query["collections"]![0]!["query"]!["from"]!["source"] = "01J92P0V0GWFR78HZH0P8Z3GY7@" + LocalId;
        // The SQLite database stores the status as the member value (no mapping there stores it as text).
        query["parameters"]![1] = new JsonObject { ["name"] = "statuses", ["type"] = "int32", ["collection"] = true };
        const string Total = "01J92P0V0T6EA0XM025XQX8GBW", Amount = "01J92P0V08P9BVJAPQ793XYNTQ", Currency = "01J92P0V093A7BE6Q8AS477H7D";
        var select = query["select"]!.AsArray();
        select.Add(JsonNode.Parse("""{ "attribute": "01J92P0V1EHF7PB28CZJG9C5SN", "expression": { "column": "i.01J92P0V1EHF7PB28CZJG9C5SN.01J92P0V0KGPC29TQQG8R57EBM" } }"""));
        select.Add(JsonNode.Parse($$"""{ "attribute": "{{Total}}.{{Amount}}", "expression": { "column": "i.{{Total}}.{{Amount}}" } }"""));
        select.Add(JsonNode.Parse($$"""{ "attribute": "{{Total}}.{{Currency}}", "expression": { "column": "i.{{Total}}.{{Currency}}" } }"""));
        query["parameters"]!.AsArray().Add(JsonNode.Parse("""{ "name": "minimum", "type": "decimal", "default": 0.5 }"""));
        query["where"]!["and"]!.AsArray().Add(JsonNode.Parse($$"""
            { "op": "ge", "left": { "op": "+", "args": [{ "column": "i.{{Total}}.{{Amount}}" }, { "value": "0.0", "type": "decimal" }] }, "right": { "cast": { "param": "minimum" }, "type": "double" } }
            """));
        var twice = query["collections"]![0]!.DeepClone().AsObject();
        twice["attribute"] = "sameLines";
        // The quantity read as an int64 fills the int32 attribute through a cast in the generated code.
        twice["query"]!["select"]![1]!["expression"] = JsonNode.Parse("""{ "cast": { "column": "l.01J92P0V0Y049452AH0K8CDC9N" }, "type": "int64" }""");
        var on = twice["query"]!["where"]!.DeepClone();
        twice["query"]!["where"] = new JsonObject { ["and"] = new JsonArray(on, on.DeepClone()) };
        query["collections"]!.AsArray().Add(twice);
        Repo.WriteFile(".maquettiste/model/databases/local/queries/local-invoices-by-customer.json", query.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>The PostgreSQL database <c>main</c>.</summary>
    private const string MainId = "01J92P0V1QRN2181XM2ZWE02W4";

    /// <summary>The SQL Server database <c>reporting</c>.</summary>
    private const string ReportingId = "01J92P0V2A0000000000000001";

    /// <summary>The SQLite database <c>local</c>.</summary>
    private const string LocalId = "01J92P0V2A0000000000000002";

    /// <summary>
    /// One routine, one domain type used by a column and one trigger in each database of the dialects variant: on PostgreSQL the
    /// domain <c>email_address</c> (named by id from the customers overlay), the trigger function <c>keep_invoice_number</c> and the
    /// trigger <c>invoices_keep_number</c> that depends on it; on SQL Server the alias type <c>email_address</c> (named by name), the
    /// scalar function <c>invoice_total</c> and an <c>AFTER UPDATE</c> trigger; on SQLite the same type stored as its base, a function
    /// SQLite cannot create, and a <c>BEFORE UPDATE</c> trigger. The triggers refuse a change of an invoice's number, which no test makes.
    /// </summary>
    private void AddDatabaseObjects()
    {
        const string customer = "01J92P0V0ETQKXXP951CMMNHH3";
        const string email = "01J92P0V0NRX99014KNVAZGPEW";
        foreach (var (db, folder, n) in new[] { (MainId, "main", 1), (ReportingId, "reporting", 2), (LocalId, "local", 3) })
        {
            var type = $"01J92P0V2H000000000000000{n}";
            Repo.WriteFile($".maquettiste/model/databases/{folder}/types/email-address.json", $$"""
                {
                  "$schema": "../../../../.schema/v1/database-type.json",
                  "kind": "database-type",
                  "id": "{{type}}",
                  "name": "email_address",
                  "database": "{{db}}",
                  "description": "An email address, checked where the dialect can.",
                  "typeKind": "domain",
                  "base": "string",
                  "length": 254,
                  "check": "VALUE LIKE '%_@_%'"
                }

                """);
            var overlay = $"01J92P0V2J000000000000000{n}";
            Repo.WriteFile($".maquettiste/model/databases/{folder}/tables/{overlay.ToLowerInvariant()}.json", $$"""
                {
                  "$schema": "../../../../.schema/v1/table.json",
                  "kind": "table",
                  "id": "{{overlay}}",
                  "database": "{{db}}",
                  "origin": "synthesized",
                  "entity": "{{customer}}",
                  "columns": [
                    {
                      "id": "01J92P0V2K000000000000000{{n}}",
                      "attribute": "{{email}}",
                      "nativeType": "{{(folder == "main" ? type : "email_address")}}"
                    }
                  ]
                }

                """);
        }

        Repo.WriteFile(".maquettiste/model/databases/main/routines/keep-invoice-number.json", """
            {
              "$schema": "../../../../.schema/v1/routine.json",
              "kind": "routine",
              "id": "01J92P0V2M0000000000000001",
              "name": "keep_invoice_number",
              "database": "01J92P0V1QRN2181XM2ZWE02W4",
              "description": "Refuses to change an invoice's number.",
              "returns": {
                "nativeType": "trigger"
              },
              "body": {
                "postgresql": "BEGIN\n    IF NEW.number <> OLD.number THEN\n        RAISE EXCEPTION 'invoice % keeps its number', OLD.number;\n    END IF;\n    RETURN NEW;\nEND;"
              },
              "comment": "Trigger function of invoices_keep_number."
            }

            """);
        Repo.WriteFile(".maquettiste/model/databases/main/objects/invoices-keep-number.json", """
            {
              "$schema": "../../../../.schema/v1/sql-object.json",
              "kind": "sql-object",
              "id": "01J92P0V2N0000000000000001",
              "name": "invoices_keep_number",
              "database": "01J92P0V1QRN2181XM2ZWE02W4",
              "objectKind": "trigger",
              "dependsOn": [
                "01J92P0V2M0000000000000001",
                "01J92P0V1T0J6RH4MY9H81NYB4"
              ],
              "body": {
                "postgresql": "CREATE TRIGGER invoices_keep_number BEFORE UPDATE OF number ON billing.invoices FOR EACH ROW EXECUTE FUNCTION billing.keep_invoice_number();"
              }
            }

            """);
        Repo.WriteFile(".maquettiste/model/databases/reporting/routines/invoice-total.json", """
            {
              "$schema": "../../../../.schema/v1/routine.json",
              "kind": "routine",
              "id": "01J92P0V2M0000000000000002",
              "name": "invoice_total",
              "database": "01J92P0V2A0000000000000001",
              "description": "The sum of an invoice's lines.",
              "parameters": [
                {
                  "name": "invoice_id",
                  "type": "uuid"
                }
              ],
              "returns": {
                "type": "decimal",
                "precision": 18,
                "scale": 2
              },
              "body": {
                "sqlserver": "BEGIN\n    RETURN (SELECT COALESCE(SUM(quantity * unit_price_amount), 0) FROM dbo.invoice_lines WHERE invoice_id = @invoice_id);\nEND"
              }
            }

            """);
        Repo.WriteFile(".maquettiste/model/databases/reporting/objects/invoices-keep-number.json", """
            {
              "$schema": "../../../../.schema/v1/sql-object.json",
              "kind": "sql-object",
              "id": "01J92P0V2N0000000000000002",
              "name": "invoices_keep_number",
              "database": "01J92P0V2A0000000000000001",
              "objectKind": "trigger",
              "body": {
                "sqlserver": "CREATE TRIGGER dbo.invoices_keep_number ON dbo.invoices AFTER UPDATE AS\nBEGIN\n    IF EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON d.id = i.id WHERE i.number <> d.number)\n        THROW 50001, 'An invoice keeps its number.', 1;\nEND"
              }
            }

            """);
        Repo.WriteFile(".maquettiste/model/databases/local/routines/invoice-total.json", """
            {
              "$schema": "../../../../.schema/v1/routine.json",
              "kind": "routine",
              "id": "01J92P0V2M0000000000000003",
              "name": "invoice_total",
              "database": "01J92P0V2A0000000000000002",
              "parameters": [
                {
                  "name": "invoice_id",
                  "type": "uuid"
                }
              ],
              "returns": {
                "type": "decimal",
                "precision": 18,
                "scale": 2
              },
              "body": {
                "*": "select coalesce(sum(quantity * unit_price_amount), 0) from invoice_lines where invoice_id = :invoice_id"
              }
            }

            """);
        Repo.WriteFile(".maquettiste/model/databases/local/objects/invoices-keep-number.json", """
            {
              "$schema": "../../../../.schema/v1/sql-object.json",
              "kind": "sql-object",
              "id": "01J92P0V2N0000000000000003",
              "name": "invoices_keep_number",
              "database": "01J92P0V2A0000000000000002",
              "objectKind": "trigger",
              "body": {
                "sqlite": "CREATE TRIGGER invoices_keep_number BEFORE UPDATE OF number ON invoices FOR EACH ROW WHEN NEW.number <> OLD.number\nBEGIN\n    SELECT RAISE(ABORT, 'an invoice keeps its number');\nEND;"
              }
            }

            """);
    }

    private PackRepo(string strategy)
    {
        Repo = new TempRepo();
        CopyTree(Fixtures.Path("models", "reference-data", ".maquettiste"), Repo.ModelRoot);
        foreach (var pack in Packs)
            CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", pack), Path.Combine(Repo.ModelRoot, "templates", pack));
        const string model = ".maquettiste/model/";
        Repo.WriteFile(model + "databases/main/database.json", Database(MainDatabaseId, "main", "postgresql"));
        Repo.WriteFile(model + "databases/reporting/database.json", Database("01JRDD00000000000000000002", "reporting", "sqlserver"));
        Repo.WriteFile(model + "databases/local/database.json", Database("01JRDD00000000000000000003", "local", "sqlite"));
        EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"] = JsonNode.Parse("""{ "allow": [ { "path": "db" }, { "path": "src/Generated" } ] }""");
            settings["packs"] = JsonNode.Parse("""{ "csharp-dapper": { "output": "src/Generated" }, "sql-ddl": { "output": "db" } }""");
            settings["conventions"]!["referenceStorage"]!["strategy"] = strategy;
        });
        // The project convention chooses the strategy of UnitOfMeasure.
        EditJson(model + "reference-types/unit-of-measure.json", type => type.AsObject().Remove("storage"));
        if (strategy == "lookup-table")
            return;
        // CHECK, and native outside PostgreSQL, store single values only (collections: false): Ingredient drops its packUnits
        // collection, and Allergen keeps lookup tables for Ingredient.allergens (native on PostgreSQL in the native variant).
        EditJson(model + "entities/ingredient.json", entity =>
        {
            var attributes = entity["attributes"]!.AsArray();
            attributes.Remove(attributes.Single(a => (string?)a!["name"] == "packUnits"));
        });
        EditJson(model + "seeds/ingredient/ingredient.json", seed =>
        {
            seed["columns"]!.AsArray().RemoveAt(5);
            foreach (var row in seed["rows"]!.AsArray())
                row!["values"]!.AsArray().RemoveAt(5);
        });
        var storage = strategy == "native"
            ? $$"""{ "*": { "strategy": "lookup-table" }, "{{MainDatabaseId}}": { "strategy": "native" } }"""
            : """{ "*": { "strategy": "lookup-table" } }""";
        EditJson(model + "reference-types/allergen.json", type => type["storage"] = JsonNode.Parse(storage));
    }

    private PackRepo() => Repo = new TempRepo();

    /// <summary>An empty repo: no model, no packs (the caller lays them out, see <see cref="DdlCoverage"/>).</summary>
    /// <returns>The repo.</returns>
    public static PackRepo Blank() => new();

    /// <summary>
    /// The schemas fixture (erratum E26) with the sql-ddl pack: PostgreSQL database <c>main</c> with schemas <c>sales</c> (the
    /// default), <c>ops</c> and <c>audit</c>; package Sales by convention without a schema, package Ops into <c>ops</c>, and entity
    /// Audit (in Ops) mapped into <c>audit</c>.
    /// </summary>
    public static PackRepo Schemas()
    {
        var repo = new PackRepo();
        CopyTree(Fixtures.Path("models", "schemas", ".maquettiste"), repo.Repo.ModelRoot);
        CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", "sql-ddl"), Path.Combine(repo.Repo.ModelRoot, "templates", "sql-ddl"));
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"] = JsonNode.Parse("""{ "allow": [ { "path": "db" } ] }""");
            settings["packs"] = JsonNode.Parse("""{ "sql-ddl": { "output": "db" } }""");
        });
        return repo;
    }

    /// <summary>
    /// The bindings fixture (erratum E43) with both example packs: designed tables bound one to one (Product, an identity key), with a
    /// soft delete (Customer), shared through an entity_type constant (ProductNote and CustomerNote), and a read-only entity over a
    /// query (CustomerSummary); no entity is projected. With <paramref name="sqlite"/>, the database is SQLite, so the generated
    /// repositories run against an in-memory database (CompileTests).
    /// </summary>
    /// <param name="sqlite">Whether the database is SQLite instead of PostgreSQL.</param>
    /// <returns>The repo.</returns>
    public static PackRepo Bindings(bool sqlite = false)
    {
        var repo = new PackRepo();
        CopyTree(Fixtures.Path("models", "bindings", ".maquettiste"), repo.Repo.ModelRoot);
        foreach (var pack in Packs)
            CopyTree(Path.Combine(Fixtures.RepoRoot, "packs", pack), Path.Combine(repo.Repo.ModelRoot, "templates", pack));
        if (sqlite)
            repo.EditJson(".maquettiste/model/databases/main/database.json", database => database["dialect"] = "sqlite");
        return repo;
    }

    /// <summary>
    /// A fixture model (<c>tests/fixtures/models/&lt;fixture&gt;</c>) with only the TypeScript sample pack
    /// (<c>samples/typescript-pack</c>), writing to the root <c>web</c>. Relative imports end in <c>.ts</c>, so node runs
    /// the output with its type stripping, and zod schemas are off, so the output needs no package beyond node's types.
    /// </summary>
    /// <param name="fixture">The fixture folder name.</param>
    /// <returns>The repo.</returns>
    public static PackRepo TypeScript(string fixture)
    {
        var repo = new PackRepo();
        CopyTree(Fixtures.Path("models", fixture, ".maquettiste"), repo.Repo.ModelRoot);
        CopyTree(Path.Combine(Fixtures.RepoRoot, "samples", "typescript-pack"), Path.Combine(repo.Repo.ModelRoot, "templates", "typescript"));
        repo.EditJson(".maquettiste/maquettiste.json", settings =>
        {
            settings["outputs"] = JsonNode.Parse("""{ "allow": [ { "path": "web" } ] }""");
            settings["packs"] = JsonNode.Parse("""{ "typescript": { "output": "web", "parameters": { "importExtension": ".ts", "zod": false } } }""");
        });
        return repo;
    }

    /// <summary>
    /// The gate 3 fixture (<c>tests/fixtures/models/processes</c>, phase-3-design.md section 8.1) with every example pack its settings
    /// name that exists under <c>packs/</c>. With <paramref name="sources"/> the fixture's C# solution comes along (its isolation files,
    /// projects, the committed companions under <c>src/Processes.Data/Custom</c> and the test host), so the generated code can be built
    /// and its scenario tests run.
    /// </summary>
    /// <param name="sources">Whether to copy the fixture's solution.</param>
    /// <returns>The repo.</returns>
    public static PackRepo Processes(bool sources = false)
    {
        var repo = new PackRepo();
        var fixture = Fixtures.Path("models", "processes");
        CopyTree(Path.Combine(fixture, ".maquettiste"), repo.Repo.ModelRoot);
        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture, ".maquettiste", "maquettiste.json")))!;
        foreach (var (pack, _) in settings["packs"]!.AsObject())
        {
            var folder = Path.Combine(Fixtures.RepoRoot, "packs", pack);
            if (Directory.Exists(folder))
                CopyTree(folder, Path.Combine(repo.Repo.ModelRoot, "templates", pack));
        }

        if (sources)
        {
            foreach (var file in new[] { "Directory.Build.props", "Directory.Packages.props", ".editorconfig" })
                File.Copy(Path.Combine(fixture, file), repo.PathOf(file));
            CopyTree(Path.Combine(fixture, "src"), repo.PathOf("src"));
        }

        return repo;
    }

    /// <summary>The id of the reference-data variants' PostgreSQL database.</summary>
    public const string MainDatabaseId = "01JRDD00000000000000000001";

    /// <summary>
    /// The reference-data fixture with a PostgreSQL (<c>main</c>), a SQL Server (<c>reporting</c>) and a SQLite (<c>local</c>)
    /// database, the project convention choosing the reference storage strategy: <c>lookup-table</c>, <c>check</c> or
    /// <c>native</c>.
    /// </summary>
    /// <param name="strategy">The strategy key.</param>
    /// <returns>The repo.</returns>
    public static PackRepo ReferenceData(string strategy) => new(strategy);

    /// <summary>The repo.</summary>
    public TempRepo Repo { get; }

    /// <summary>The billing fixture with its single PostgreSQL database.</summary>
    /// <returns>The repo.</returns>
    public static PackRepo Billing() => new(dialects: false);

    /// <summary>The billing fixture with a PostgreSQL, a SQL Server and a SQLite database.</summary>
    /// <returns>The repo.</returns>
    public static PackRepo BillingDialects() => new(dialects: true);

    /// <summary>Runs generation in process.</summary>
    /// <param name="mode">Apply, dry run or check.</param>
    /// <param name="packs">The packs to run; null for all.</param>
    /// <param name="force">Render every unit.</param>
    /// <param name="jobs">Render parallelism; null for the default.</param>
    /// <returns>The result.</returns>
    public async Task<GenerationResult> GenerateAsync(GenerationMode mode = GenerationMode.Apply, IReadOnlyList<string>? packs = null, bool force = false, int? jobs = null)
    {
        await using var store = new ModelStore(Repo.Options);
        var service = new GenerationService(store, Repo.Options);
        var request = new GenerationRequest { Mode = mode, Packs = packs, Force = force, Jobs = jobs };
        return await service.RunAsync(request, progress: null, TestContext.Current.CancellationToken);
    }

    /// <summary>Runs generation and fails the test unless it succeeded without errors.</summary>
    /// <param name="mode">Apply, dry run or check.</param>
    /// <param name="packs">The packs to run; null for all.</param>
    /// <param name="force">Render every unit.</param>
    /// <param name="jobs">Render parallelism; null for the default.</param>
    /// <returns>The result.</returns>
    public async Task<GenerationResult> GenerateCleanlyAsync(GenerationMode mode = GenerationMode.Apply, IReadOnlyList<string>? packs = null, bool force = false, int? jobs = null)
    {
        var result = await GenerateAsync(mode, packs, force, jobs);
        var errors = result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Assert.True(result.Outcome == RunOutcome.Succeeded && errors.Count == 0, Describe(result));
        return result;
    }

    /// <summary>Describes a result for assertion messages.</summary>
    /// <param name="result">The result.</param>
    /// <returns>The outcome, diagnostics and changes.</returns>
    public static string Describe(GenerationResult result) =>
        $"Outcome {result.Outcome}\n"
        + string.Join('\n', result.Diagnostics.Select(d => $"{d.FilePath}({d.Line},{d.Column}): {d.Severity} {d.Rule}: {d.Message}"))
        + "\n" + string.Join('\n', result.Changes.Select(c => $"{c.Kind} {c.Path}"));

    /// <summary>The absolute path of a repo-relative path.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The absolute path.</returns>
    public string PathOf(string path) => Repo.PathOf(path);

    /// <summary>Reads a repo file.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <returns>The text.</returns>
    public string Read(string path) => Repo.ReadFile(path);

    /// <summary>Writes a repo file.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="text">The text.</param>
    public void Write(string path, string text) => Repo.WriteFile(path, text);

    /// <inheritdoc/>
    public void Dispose() => Repo.Dispose();

    /// <summary>Copies a folder tree.</summary>
    /// <param name="from">The source folder.</param>
    /// <param name="to">The target folder.</param>
    public static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Edits a repo JSON file in place.</summary>
    /// <param name="path">The repo-relative path.</param>
    /// <param name="edit">The edit.</param>
    public void EditJson(string path, Action<JsonNode> edit)
    {
        var node = JsonNode.Parse(Read(path))!;
        edit(node);
        Write(path, node.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private void Patch(string path, string from, string to)
    {
        var text = Repo.ReadFile(path);
        Assert.Contains(from, text, StringComparison.Ordinal);
        Repo.WriteFile(path, text.Replace(from, to, StringComparison.Ordinal));
    }

    private static string Database(string id, string name, string dialect) => $$"""
        {
          "$schema": "../../../.schema/v1/database.json",
          "kind": "database",
          "id": "{{id}}",
          "name": "{{name}}",
          "dialect": "{{dialect}}"
        }

        """;
}
