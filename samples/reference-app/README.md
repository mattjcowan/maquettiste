# Reference application: Northwind Operations

**Owner:** P2-R. See docs/engineering/phase2-design.md section 7 and SPEC.md section 21 (gate 2).

The data layer of a B2B wholesale distributor, modeled in Maquettiste and generated end to end: 200 entities in 12 packages,
8 value objects, 5 scalar types, 23 enums, 2 reference types, 439 relations, one PostgreSQL 16 database (`main`, schema `northwind`) with
table overlays, a designed table, a view and three sequences, and one subject-area diagram per package.

## Layout

```
domain/*.yaml            the domain description (grammar in domain/README.md) - edit these
tools/build-model.mjs    domain -> .maquettiste/ (model, maquettiste.json, schema copies, pack copies); deterministic
tools/check-model.mjs    fails when .maquettiste/ is not exactly what build-model.mjs produces (npm test)
tools/seed.mjs           builds the same model through a running editor's API, batch by batch
tools/empty-editor.sh    starts the editor image on an empty project for seed.mjs
.maquettiste/            the committed model; templates/ are copies of packs/sql-ddl and packs/csharp-dapper;
                         manifest/sql-ddl.json and snapshots/main.json are written by maquettiste generate
tools/db-apply.sh        applies db/main to postgres:16 (schema, migrations, seed twice) and asserts the object counts
tools/gate2.sh           the gate 2 check, step by step (also what .github/workflows/gate2.yml runs)
db/main/                 committed sql-ddl output: schema.sql, tables/views/sequences, migrations/0001.sql, seed.sql
src/ReferenceApp.Data/   ReferenceApp.Data.csproj (net10.0, Dapper, Npgsql); Generated/ is built (gitignored),
                         Custom/ holds hand-written partial halves of generated types
Directory.Build.props, Directory.Packages.props, .editorconfig
                         isolation: keep the repository's central package management, lock files and analyzers out
```

`maquettiste.json` declares `db` (committed: the sql-ddl output) and `src/ReferenceApp.Data/Generated` (built: the
csharp-dapper output, namespace `ReferenceApp.Data`), and stores enums as their codes by default (`enumStorage: string`).

## Build and check the model

```sh
cd samples/reference-app/tools
npm ci
node build-model.mjs        # rewrites ../.maquettiste/ from ../domain/
npm test                    # check-model.mjs: the committed files equal a fresh build, twice
dotnet run --project ../../../src/Maquettiste.Cli -- --repo .. validate     # 0 errors, 0 warnings, 0 infos
```

## Generate, build and apply

From the repository root (the CLI built with `dotnet build -c Release src/Maquettiste.Cli`):

```sh
CLI="dotnet src/Maquettiste.Cli/bin/Release/net10.0/Maquettiste.Cli.dll --repo samples/reference-app"
$CLI generate                   # db/ (committed) and src/ReferenceApp.Data/Generated/ (built): 855 files, 209 tables
$CLI generate --check           # exit 0: the committed db/ equals what the model generates (exit 2 on drift)
dotnet build samples/reference-app/src/ReferenceApp.Data -warnaserror
samples/reference-app/tools/db-apply.sh          # 209 tables, 1 view, 3 sequences in both databases
samples/reference-app/tools/db-apply.sh --down
```

`db/main/seed.sql` carries the generated lookup rows plus hand-written currencies and countries inside its
`maquettiste:keep id=seed-data` region, which regeneration preserves. The C# companions (`Customer.cs` next to
`Customer.g.cs`) are written once into the built root; the hand-written code lives in `src/ReferenceApp.Data/Custom/`
as further partial parts, so a fresh clone needs no committed stubs.

## Seeding through the editor

The committed model can be reproduced by the editor's store (PD21). From the repository root, with the image built:

```sh
export MAQUETTISTE_EDITOR_TOKEN=$(openssl rand -hex 24) MAQUETTISTE_PORT=8093
samples/reference-app/tools/empty-editor.sh tmp/nw-seed
node samples/reference-app/tools/seed.mjs --url http://127.0.0.1:8093 --compare tmp/nw-seed
docker compose -f docker/compose.yaml --project-directory tmp/nw-seed down -v
```

`seed.mjs` saves `maquettiste.json` through `PUT /api/project/settings`, then posts 31 batches to `POST /api/model/batch`
(vocabularies, packages, types, database objects, entities per package, relations per package, table overlays, mappings,
diagrams), checks `POST /api/validate` reports nothing and, with `--compare`, that every file equals `build-model.mjs`'s and that
`.maquettiste/model` holds no other file.
`--dry-run` lists the batches without sending anything.

## Gate 2 check

`tools/gate2.sh` runs SPEC Section 21's gate 2 the way `.github/workflows/gate2.yml` does (phase2-design.md 7.2): it copies
this folder to `tmp/gate2/` without the model, `db/` and build output, starts the image over it, seeds the model through the
API (`seed.mjs --compare`), runs the Playwright walk `src/editor/tests/e2e/gate2.spec.ts` (add `receivingHours` to
`Warehouse` in the grid, rename the relation `delivery route warehouse` to `delivery route depot`, plan, open a diff, apply,
`applyResult.outcome` = `succeeded`). Then `check` asserts that both edits are in the model files, `warehouses.sql` and
`Warehouse.g.cs`, runs `validate`, runs `generate --check` (the committed root `db/main` equals the CLI's output) and, since
`--check` covers committed roots only, generates the built root `Generated/` with the CLI into a second copy and compares
it with `diff -r`. Last come `dotnet build -warnaserror` of `ReferenceApp.Data` and `db-apply.sh` against PostgreSQL
(both databases must hold 209 tables, 1 view and 3 sequences). The workflow also runs `generate --check` on this folder, so
the committed `db/main` cannot go stale when a pack changes, and it runs on pull requests to main. From the repository root, with the image built and
`npm ci && npx playwright install chromium` done in `src/editor`:

```sh
docker build -f docker/Dockerfile -t mattjcowan/maquettiste:dev .
samples/reference-app/tools/gate2.sh                 # all steps, then stops the containers; about 25 s after the image
samples/reference-app/tools/gate2.sh prepare seed    # or one step at a time: prepare seed walk check build ddl down
```

The editor listens on 127.0.0.1:8097 (`MAQUETTISTE_PORT`); the walk edits the model, so rerun from `prepare`.

## What the model exercises

- Associations with foreign keys (required, optional, restrict, cascade, set-null), one-to-one (`CreditProfile`,
  `Driver`, `ProofOfDelivery`), compositions with ordered children (`SalesOrder.lines`, `Product.images`).
- Many-to-many without attributes (`role grants permission`, `collection includes product`) and with attributes
  (`user account holds role`, `payment settles invoice`, `rfq invites supplier`), mapped to junction tables.
- Self-references: `Employee.manager`, `Department.parent`, `ProductCategory.parent`, `LedgerAccount.parent` and more.
- TPH inheritance `Party` -> `Customer`, `Supplier`, `Carrier` with discriminator values; TPT `Payment` -> `CardPayment`,
  `BankTransfer`.
- Value objects embedded (with `bill_`/`ship_`/`hq_` prefixes), a value-object collection table (`Supplier.remitToAddresses`)
  and a JSON one (`Contact.additionalPhones`); enums stored as codes and as integers (`SalesOrderLine.status`);
  reference types stored as lookup tables keyed by code (`Product.status`, `Shipment.status`; migration 0002 converts
  their former enum lookup ids to codes).
- Stereotypes `aggregate-root`, `audited`, `soft-delete`, `reference-data` and `lookup` (11 lookup entities); a strict tag
  vocabulary; a category tree with one category per package under four groups.
- Alternate keys, indexed and unique attributes, overlays with native types, checks, filtered and descending indexes,
  sequence defaults, a sequence-keyed entity (`JournalEntry`), the designed `integration_outbox` table and the
  `open_sales_orders` view.
