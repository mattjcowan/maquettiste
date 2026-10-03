# The Northwind Operations domain

Northwind Operations is a B2B wholesale distributor of janitorial, safety and facility supplies (phase2-design.md
section 7.1). This folder describes its data model; `tools/build-model.mjs` turns it into `.maquettiste/` and
`tools/seed.mjs` builds the same model through a running editor. Edit these files, never the JSON under `.maquettiste/model/`.

| File | Package | Entities |
| --- | --- | --- |
| `00-project.yaml` | project settings, tags, categories, stereotypes, scalar types, value objects, database `main` | |
| `01-identity.yaml` | Identity and access | 12 |
| `02-crm.yaml` | Parties and CRM | 22 |
| `03-catalog.yaml` | Catalog | 24 |
| `04-pricing.yaml` | Pricing and promotions | 16 |
| `05-sales.yaml` | Sales orders | 20 |
| `06-finance.yaml` | Finance and ledger | 10 |
| `07-inventory.yaml` | Inventory and warehousing | 24 |
| `08-fulfillment.yaml` | Fulfillment and shipping | 18 |
| `09-purchasing.yaml` | Purchasing and suppliers | 18 |
| `10-billing.yaml` | Billing and payments | 20 |
| `11-returns.yaml` | Returns and service | 10 |
| `12-reference-data.yaml` | Reference data | 6 |

## Grammar

A package file has `package` (the identifier), `displayName`, `description`, `enums`, `referenceTypes`, `entities`, `relations` and
`diagram`. A reference type has `description`, `code` (the code's length), optional `label` (the label's length, 64 by default)
and `rows`, one `<code>: <label> | <description>` per row; it becomes `model/reference-types/<name>.json` and one seed,
`model/seeds/<name>/<name>.json`, holding its rows. An attribute names it like any type, and its default is a code.
Every element and attribute needs a description; the build fails without one.

**Attributes** are one line: `name: <type>[!] [flags] | description`.

- `<type>` is a built-in (`string(40)`, `decimal(19,4)`, `text`, `bool`, `int16/32/64`, `date`, `time`, `datetimeoffset`,
  `uuid`, `ulid`, `json`, ...) or the name of an enum, value object or scalar type; `[]` makes a collection (`Address[]`).
- `!` means required. Flags: `unique`, `indexed`, `readonly`, `immutable`, `pii`, `secret`, `default=<literal>`,
  `expr=<now|today|new-uuid|new-ulid>`, `min=<n>`, `max=<n>`, `pattern=<regex>`.
- Descriptions follow ` | `; they must not contain `: ` (YAML would read a nested mapping).

**Entities**: `description`, `displayName`/`pluralName` (derived from the name when omitted), `stereotypes`, `tags`,
`abstract`, `base`, `key`, `attributes`, `alternateKeys` (`name: [attribute, ...]`), `refs`, `children`, `mapping`, `overlay`.

- `key`: `uuid-v7` (the default), `ulid`, `identity` (int64), `identity32`, `sequence` (int64 from a named sequence, see
  `overlay`), or the name of an attribute for an application-assigned natural key. The surrogate `id` attribute is added.
  Derived entities (`base`) have no key.
- `refs` are many-to-one relations: `role: <Entity>[!] [restrict|cascade|set-null|none] [back=<role>] [one] [name=<words>] | description`.
  The role names the navigation on this entity and the foreign key; `!` makes it required (default on delete `restrict`,
  else `set-null`); `back=` adds the collection navigation on the target; `one` makes it one-to-one. `name=` names the
  relation, underscores for spaces: a verb phrase read from this entity to the other end, such as
  `name=sales_order_belongs_to_customer` or `name=shipment_is_shipped_by_carrier` (relation names are unique in a domain, so the
  phrase carries its two ends). Without it the relation is named `<entity words> <role words>`, such as `sales order customer`.
- `children` are compositions: `role: <Entity> [ordered] [aggregation] [name=<words>] | description`. The child gets a
  required navigation back to the parent named after it, deleted with the parent. `name=` as for `refs`
  (`name=sales_order_contains_lines`); without it, `<entity words> <role words>`.
- `mapping` (only where conventions are not enough): `inheritance` (`tph`, `tpt`), `discriminator`, `storage`
  (`attribute: int|string|lookup|json|table|embedded`), `prefix` (`attribute: column_prefix_`).
- `binding` binds the entity to a designed table of `main` (erratum E43) instead of projecting it: `table` (the designed
  table's name), `constants` (`column: value`, a filter on every read and a value on every insert), `fields`
  (`attribute: column`, the surrogate `id` included) and `columns` (`column: ignored|database|computed`, the columns no
  field maps). The entity gets no table of its own; `remarks` is shared by `CustomerRemark` and `SalesOrderRemark` this way.
- `overlay` is a synthesized table overlay in `main`: `columns` (`attribute: {nativeType, defaultSql, sequence, comment}`),
  `checks` (`name: SQL`), `indexes` (`{name, columns: [attribute [desc]], where, unique}`).

**Relations** (many-to-many and anything `refs`/`children` cannot say): `name`, `description`, `kind`, `inverseName`,
`ends` (`"<Entity> <role> <1|0..1|*|1..*> [nav|nav=<name>] [restrict|cascade|set-null] [ordered]"`; `nav` on an end creates
the navigation on the other end's entity) and `attributes` (which make the convention map it to a junction table).

**Enums**: `description` and `members` (`Name: CODE | description`); values count from 0 in file order.

**Diagram**: `name`, `description`, `columns` (grid width, default 4). Members are the package's entities in file order and
every relation between two of them.

**Queries** (`database.queries` in `00-project.yaml`): each one is a query file's JSON tree written as YAML (`parameters`,
`from`, `joins`, `select`, `where`, `groupBy`, `orderBy`, `paging`, `collections`), with names where the file has ids: `entity`
and a collection's `entity` are entity names, a `source` is an entity name (its table), a designed table's name or
`view:<name>`, a field's `attribute` is an attribute of the query's (or the collection's) entity, stereotype attributes
included, and a collection's `attribute` is the navigation it fills. Column references are written `alias.column_name`.

## Ids

Ids are ULIDs derived from each element's domain key (`entity:SalesOrder`, `attr:SalesOrder.orderNumber`,
`relation:sales order customer`, ...; `tools/lib/ids.mjs`). A `refs` or `children` relation takes its key from its entity and
role (`<entity words> <role words>`), not from `name=`, so naming or renaming it keeps its id, its ends and its database
columns. Renaming anything else in YAML (an entity, an attribute, a role, an explicit relation) gives it a new id; keep those
names stable once other work depends on them.
