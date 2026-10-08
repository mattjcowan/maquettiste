# models

**Owner:** W1 Loader. See docs/engineering/engine-design.md sections 17 and 18.

Fixture repos for the loader, the store and the end-to-end tests. Each subfolder is a repo root holding `.maquettiste/`.

- `billing/`: the billing model the design names for determinism and golden runs. Packages `Billing` and `Catalog`; entities `Customer`, `Invoice` (description sidecar `invoice.md`, custom property), `InvoiceLine`, `Payment`, `Product` (alternate key); value object `Money`; scalar type `EmailAddress`; enum `InvoiceStatus`; relations `places` (1:n), `contains` (composition, ordered), `refers to` (n:1) and `settles` (m:n with an attribute); PostgreSQL database `main` with schema `billing`, a synthesized overlay table for `Invoice` (native type, check, descending index), a view and a sequence; mappings for `Invoice` (storage and prefix) and `settles` (junction); a diagram; a strict tag vocabulary, a category tree, stereotypes `aggregate-root`, `audited` (virtual attributes, default properties) and `soft-delete`; the extension `retention`. `maquettiste.json` allows `db` (committed) and `src/Generated` (built) and enables `sql-ddl` and `csharp-dapper`.

Every file is canonical and loads without any diagnostic (`BillingFixtureTests`). To rewrite the files canonically after a hand edit, run the engine tests with `MAQUETTISTE_UPDATE_GOLDEN=1`. Ids are valid ULIDs; the synthesized table's file is named after its id because it has no name.

- `reference-data/`: the contract fixture of reference types, seeds and localization (reference-types-seeds-localization.md section 5 step 1). Reference type `UnitOfMeasure` (code, label, user fields `factor` and `symbol`, a `*` storage choice), its seed with three rows (one line per row, trailing nulls trimmed), a French locale shard `_reference-data.json`, and settings with `localization`, `referenceData.strategies` and `conventions.referenceStorage`. Loads without any diagnostic (`ReferenceDataContractTests`); the shard is read and checked but is not an element. Step 2 adds reference type `Allergen` (a `parent` field typed by itself, so rows name rows), entities `Recipe` and `Ingredient` (reference-typed attributes: single and collection, required and optional, a default code, `allowedValues`), relation `contains` with `quantity` and `unit_of_measure`, relation `substitutes` (a to-one end named by the Ingredient seed), and seeds for every target with cross-row references (end cells naming rows, codes naming rows). Validates without any diagnostic (`ReferenceDataStoreTests`).

- `schemas/`: database schemas (erratum E26). PostgreSQL database `main` with schemas `sales` (the default), `ops` and `audit`; `byConvention: packages` with package `Sales` as a plain id and `Ops` as `{ "package", "schema": ops }`; entities `Order` (Sales), `Log` and `Audit` (Ops), and a mapping that places `Audit` in `audit`. The sql-ddl pack test (`SchemaPlacementTests`) shows each table in its schema and each schema created.

- `showcase/`: Spoke & Chain, an invented bicycle-sharing and repair cooperative, for the documentation's screenshots
  (`npm run docs:screenshots` in src/editor, tests/docs/take.sh) and the guides' story. Four domains, 17 entities, 20 relations,
  two enums, three reference types with seeds, a table seed, PostgreSQL database `main` with 19 bound tables, a view and a query,
  three diagrams, a French locale, the lifecycle `RentalLifecycle` and the orchestration `RepairOrchestration` (a parallel state),
  four actors and five scenarios. take.sh adds the schemas and the packs maquettiste.json names. Validates with no error or warning (six
  MQ7204 infos: the French locale is partial), and every scenario passes `process verify`. No test reads it; refresh it with
  `format` after a hand edit.
