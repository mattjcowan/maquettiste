# showcase

The model behind the documentation site's screenshots (`npm run docs:screenshots` in src/editor, tests/docs/take.sh). It is meant to look good on screen and to
explain the product to a newcomer, so it uses a bit of every kind of element rather than a lot of any one.

**Spoke & Chain** is an invented community bicycle-sharing and repair cooperative. Every name in it (the business, the
people, the stations, the suppliers) is made up; it describes no real company or customer.

What it holds:

- Four domains: `Membership`, `Fleet`, `Rentals` and `Workshop`, with 17 entities, 20 relationships (compositions, an
  optional end, two many-to-many relationships with attributes) and the enums `RentalStatus` and `BikeCondition`.
- Vocabularies: tags `finance`, `inventory`, `pii` and `safety` (strict, with colors), the stereotypes `aggregate-root`,
  `audited` (virtual `createdAt` and `updatedAt`) and `lookup`, and a small category tree.
- Reference types `BikeType`, `RepairCategory` and `MembershipPlan`, each with a seed, stored as lookup tables in the
  `reference` schema; a French locale translating the domains, the entities, the reference types and their rows.
- PostgreSQL database `main` with a schema per domain: a designed table for every entity, each entity bound to its table,
  the two junction tables, extra indexes (two partial), a unique constraint, a check, the view `overdue_rentals`, the
  query `AvailableBikesAtStation` and a table seed of the tariffs.
- Diagrams `Rentals`, `Workshop` and `Spoke & Chain overview`.
- Processes `RentalLifecycle` (the lifecycle of `Rental`, with timers, a guard and actions) and `RepairOrchestration`
  (a parallel state: the mechanic's inspection and the parts run side by side, then a sign-off gate), the actors `Member`,
  `Mechanic`, `WorkshopLead` and `PaymentService`, and five scenarios that pass `maquettiste process verify`.
- Settings for the `sql-ddl` and `csharp-dapper` packs, writing to `db` and `src/Generated`. The schemas and the packs are
  not kept here: take.sh copies them in from `schemas/v1` and `packs/`, and so must anyone who generates from the model
  (copy them under `.maquettiste/.schema/v1` and `.maquettiste/templates/`). The generated files are not kept either.

With the schemas and packs in place, the model validates without an error or a warning (the French locale is reported as incomplete, as information) and every
file is in canonical form:

```sh
maquettiste --repo tests/fixtures/models/showcase validate
maquettiste --repo tests/fixtures/models/showcase format --check
maquettiste --repo tests/fixtures/models/showcase process verify
maquettiste --repo tests/fixtures/models/showcase generate --dry-run
```
