# models

**Owner:** W1 Loader. See docs/engineering/engine-design.md sections 17 and 18.

Fixture repos for the loader, the store and the end-to-end tests. Each subfolder is a repo root holding `.maquettiste/`.

- `billing/`: the billing model the design names for determinism and golden runs. Packages `Billing` and `Catalog`; entities `Customer`, `Invoice` (description sidecar `invoice.md`, custom property), `InvoiceLine`, `Payment`, `Product` (alternate key); value object `Money`; scalar type `EmailAddress`; enum `InvoiceStatus`; relations `places` (1:n), `contains` (composition, ordered), `refers to` (n:1) and `settles` (m:n with an attribute); PostgreSQL database `main` with schema `billing`, a synthesized overlay table for `Invoice` (native type, check, descending index), a view and a sequence; mappings for `Invoice` (storage and prefix) and `settles` (junction); a diagram; a strict tag vocabulary, a category tree, stereotypes `aggregate-root`, `audited` (virtual attributes, default properties) and `soft-delete`; the extension `retention`. `maquettiste.json` allows `db` (committed) and `src/Generated` (built) and enables `sql-ddl` and `csharp-dapper`.

Every file is canonical and loads without any diagnostic (`BillingFixtureTests`). To rewrite the files canonically after a hand edit, run the engine tests with `MAQUETTISTE_UPDATE_GOLDEN=1`. Ids are valid ULIDs; the synthesized table's file is named after its id because it has no name.
