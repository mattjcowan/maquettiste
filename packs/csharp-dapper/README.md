# csharp-dapper

A C# model and [Dapper](https://github.com/DapperLib/Dapper) data access layer from the resolved model: entity classes as
generated and hand-written halves of one partial class, enums, value objects, a repository per mapped entity, one registration
file per package and Dapper type handlers.

The pack writes to a **built** output root. `maquettiste init --pack csharp-dapper` sets `packs.csharp-dapper.output` to
`src/Generated` and declares that root without `commit`, so it is gitignored and regenerated on every build; every path below is
under `src/Generated/`. The generated code needs C# 12, .NET 9 or later (`Guid.CreateVersion7`) and the `Dapper` package.

## Output

For entity `Invoice` in package `Billing`:

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `entity` | `pair` | `each entity` | `Billing/Invoice.g.cs` every run, and its companion `Billing/Invoice.cs` once |
| `enum` | `overwrite` | `each enum` | `Billing/InvoiceStatus.g.cs`: the enum and an `InvoiceStatusCodes` class (`ToCode`, `Parse`) for text storage |
| `value-object` | `overwrite` | `each value object` | `Billing/Money.g.cs`: a `sealed partial record` with `init` properties |
| `reference-type` | `overwrite` | `each reference type` | `ReferenceData/UnitOfMeasure.g.cs`: a `sealed record` and a static class of rows (see Reference types) |
| `resources` | `overwrite` | `each locale` | `ReferenceData/ReferenceData.resx`, `ReferenceData.fr.resx`, …: display names and row labels per declared locale |
| `repository` | file blocks | `each entity` | `Billing/InvoiceRepository.g.cs`: `IInvoiceRepository` and `InvoiceRepository` |
| `registrations` | file blocks | `model` | `Billing/BillingRepositories.g.cs`: one per package with repositories |
| `type-handlers` | `overwrite` | `model` | `DapperTypeHandlers.g.cs`: the Dapper type handlers and `UlidGenerator` |

Folders follow the package tree (`Billing/Catalog/` for package `Catalog` inside `Billing`), and namespaces follow it too:
`<namespace>.Billing.Catalog`.

## The pair pattern

A `pair` unit renders two templates for one entity. `Invoice.g.cs` is generated on every run and must not be edited; it declares
`public partial class Invoice` with a property per attribute (inherited attributes stay on the base class) and, for the table the
repositories use, a property per column that no attribute maps, such as the foreign key `CustomerId`. `Invoice.cs` is written
only when it is missing and is never touched again (the manifest records it as owned), so it is where hand-written members,
interfaces and attributes go:

```csharp
// Invoice.cs, yours
public partial class Invoice
{
    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Issued && IssuedOn.AddDays(30) < today;
}
```

The C# compiler merges both halves. The generated half never needs editing, so regeneration never loses work, and deleting a
companion brings back the empty stub.

With the default settings the companions sit next to the generated files, in the gitignored `src/Generated` root. To commit
them, give them their own committed root: set `packs.csharp-dapper.output` to `src`, `generatedFolder` to `Generated` and
`partialFolder` to `Model`, and declare `src/Generated` (built) and `src/Model` (`commit: true`) in `outputs.allow`.

## Repositories

A repository is generated for every concrete entity with a mapping in the chosen database (the `database` parameter, else the
first database by name that maps the entity) whose columns live in the entity's own table. It takes an `IDbConnection` (and an
optional `IDbTransaction`) and offers:

| Method | SQL |
| --- | --- |
| `GetAsync(key…)` | `SELECT … WHERE <primary key>` |
| `ListAsync(skip, take)` | `SELECT … ORDER BY <primary key>` with `LIMIT/OFFSET` or `OFFSET … FETCH` |
| `InsertAsync(entity)` | `INSERT`, returning a database-generated key (`RETURNING` or `OUTPUT INSERTED`) into the entity; `uuid-v7` keys are filled with `Guid.CreateVersion7()` and `ulid` keys with `UlidGenerator.NewUlid()` when empty |
| `UpdateAsync(entity)` | `UPDATE … SET <every column but the key, generated and immutable ones> WHERE <primary key>` |
| `DeleteAsync(key…)` | `DELETE … WHERE <primary key>` |

Table, column and key names come from the resolved model and are quoted for the database's dialect. Dapper maps a private `Row`
class with one property per column (aliased in the `SELECT`), and `Row` converts to and from the entity: embedded value objects
are rebuilt from their prefixed columns, enums stored as text go through `ToCode`/`Parse`, enums stored as integers or lookup
keys are cast, and JSON columns are (de)serialized with `System.Text.Json`. A table-per-hierarchy discriminator is written on
insert and filtered on read.

Not covered, by design of an example pack: navigations and junction tables (write queries for them in the companion or in your
own repository class, which is `partial`), collections stored in child tables (their property is left empty), and table-per-type
hierarchies, whose rows span tables (no repository is generated for them).

## Registration

`BillingRepositories.All` lists `(Service, Implementation)` pairs and `Register` walks them, so any container works:

```csharp
DapperTypeHandlers.Register();
services.AddScoped<IDbConnection>(_ => new NpgsqlConnection(connectionString));
BillingRepositories.Register((service, implementation) => services.AddScoped(service, implementation));
```

`DapperTypeHandlers.Register()` teaches Dapper to read `DateOnly`, `TimeOnly`, and (for SQLite, which stores them as text)
`Guid` and `DateTimeOffset`. Call it once at startup.

## Reference types

An attribute typed by a reference type holds the row's code: `string` (or the code's integer type, or `Guid` for a uuid code), `IReadOnlyList<string>` for a
collection. For reading the rows in code, each type gets a record and a static class of its rows, in seed order:

```csharp
public sealed record UnitOfMeasure(string Code, string Label, decimal Factor, string? Symbol);

public static partial class UnitOfMeasures
{
    public static readonly UnitOfMeasure Kg = new("kg", "Kilogram", 1000m, "kg");
    // ...
    public static IReadOnlyList<UnitOfMeasure> All { get; } = [Kg, G, Pinch];
    public static UnitOfMeasure? Find(string code) => code switch { "kg" => Kg, /* ... */ _ => null };
}
```

Row fields are the codes in PascalCase (`Code` in front when that does not start with a letter). With locales declared in
`localization`, one `.resx` per locale holds `<Type>_DisplayName`, `<Type>_PluralName`, `<Type>_Description` and `<Type>_<Row>`
(the row label) through the locale's fallback chain; the default locale's file is the neutral `ReferenceData.resx` and the others
are satellites the SDK compiles into `fr/…resources.dll`.

## Type map

`types/csharp.json` maps the model's built-in types to C# for `type_of attribute "csharp"`, with `nullable` (`{type}?`) and
`collection` (`IReadOnlyList<{type}>`) patterns. Enums, value objects and scalar types map to their own names (a scalar type to
its base type); add an entry named after one of them to map it elsewhere, for example `"EmailAddress": "MailAddress"`. `ulid`
maps to `string`, so the pack needs no ULID library.

## Parameters

Set them in `maquettiste.json` under `packs.csharp-dapper.parameters`.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `namespace` | `"App.Model"` | Root namespace; each package adds its qualified name. |
| `database` | `""` | The database the repositories and foreign-key properties use; empty picks, per entity, the first database by name that maps it. |
| `generatedFolder` | `""` | Folder (under the pack output) for generated files. |
| `partialFolder` | `""` | Folder (under the pack output) for the once-written companions. |

## Files

| File | Holds |
| --- | --- |
| `pack.json` | Units and parameter defaults. |
| `helpers.js` | `join_path`, `package_folder`, `cs_ident` (escapes C# keywords) and `cs_value_type`. |
| `_csharp.scriban` | Shared functions: namespaces, property names and types, XML documentation, file header. |
| `_dapper.scriban` | Mapping choice, the row model and the entity/row conversions. |
| `entity.scriban`, `entity.partial.scriban` | The generated and hand-written halves of an entity. |
| `enum.scriban`, `value-object.scriban`, `repository.scriban`, `registrations.scriban`, `type-handlers.scriban` | The other units. |
| `types/csharp.json` | The type map. |
