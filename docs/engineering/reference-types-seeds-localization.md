# Reference types, seeds and localization (design)

Status: binding design, being implemented in the order of §5; steps 7 and 8 carry "as built" notes there listing what is not built yet. It adds two element kinds (reference type and seed; seeds were a phase 4 plan), and localizes the standard fields. It builds on SPEC §5, §6, §10 to §14 and §18, on `engine-design.md` §2, §3, §6, §7, §9, §11, §14 and §15 (whose names it extends; a worker who implements a step edits that file in the same commit), and on `explorer-redesign.md`. It **supersedes** the first version of `explorer-redesign.md` §8, whose §8.2 (a `lookup` stereotype family and a per-domain Reference data group) and §8.4 (localization) were removed in that document's 2026-09-28 revision; the current `explorer-redesign.md` §1.7 and §8.3 to §8.5 already point here. Decisions are marked RS1, RS2… and listed in §6; SPEC changes are errata E6 to E17 in `spec-errata.md`.

Two principles bind every choice here. **(P1) The engine models intent; templates decide persistence.** No feature below makes the engine synthesize a table, column, constraint or type. Physical realization is at most a project-declared storage strategy whose default is "template-defined". **(P2) The product reserves no vocabulary.** No built-in stereotype, tag, category or strategy name; strategies are keys the project declares, and example packs document the keys they understand.

## 1. Reference types

### 1.1 What a reference type is

A **reference type** (`kind: "reference-type"`) is a named set of rows that attributes use as their type: units of measure, countries, currencies, allergens, payment terms. It has two built-in fields, **code** (the row's business key, unique in the type) and **label** (its display text), any number of user fields defined with the entity attribute model, localized display names and description, and rows held in seeds (§2). File: `model/reference-types/<kebab-name>.json`.

```json
{
  "$schema": "../../.schema/v1/reference-type.json",
  "kind": "reference-type",
  "id": "01JBM9S346Q3D25VT4F5V37E3S",
  "name": "UnitOfMeasure",
  "displayName": "Unit of measure",
  "pluralName": "Units of measure",
  "description": "Units in which quantities are expressed.",
  "category": "01JBQ4N7ZB6XW2T9C5D8E0FGHK",
  "code": { "id": "01JB3E28JT97KB6CQ643DZVMXX", "length": 8, "pattern": "^[a-z0-9_]+$" },
  "label": { "id": "01JBQKFBF5KZNWJ47TAN9ZT24M", "length": 64 },
  "attributes": [
    { "id": "01JBNPZX45HY43KWJRP1XPA7Z3", "name": "factor", "type": "decimal", "precision": 18, "scale": 6, "required": true },
    { "id": "01JBDJ8FSSZ5AWSH8VHTPRE95B", "name": "symbol", "type": "string", "length": 8 }
  ],
  "storage": { "*": { "strategy": "check" }, "01JBR2K8Q6W4T9V3X5Z7N1M0PD": { "strategy": "lookup-table", "options": { "schema": "ref" } } }
}
```

- **Built-in fields.** `code` has a type (`string` by default, or `int16`, `int32`, `int64`, `uuid`), `length` and `pattern`; a `uuid` code is written in its canonical form, lowercase and hyphenated (MQ7013); `label` is a `string` with a `length`. Both are always required on every row. Each carries an `id` (so its display name and description can be translated, §3) and optional `displayName` and `description` ("ISO code"). A third built-in column, `description`, holds a per-row Markdown description when a seed lists it; the editor lists it in every new seed and shows it third in the Rows grid (2026-09-29).
- **User fields** are `ModelAttribute`s (SPEC §6), with the same grid and the same facets. Their `type` may be a built-in, a custom scalar, an enum or another reference type (single or collection); not a value object or an entity, so a row stays one flat line and one CSV record (RS2). Stereotypes with virtual attributes apply as on entities.
- **No `package`** (RS3). The owner wants one place for all reference data; grouping in the screen follows the project setting `referenceData.groupBy` (the category path by default, §4.2). Packs that need a namespace read a category or a project-defined custom property.
- **`storage`** holds per-type storage choices (§1.4), keyed by **database id** (`01JBR2…` above is the `main` database) or `*`, since every reference between elements uses the id (SPEC §11); the editor shows database names.
- **User field names** exclude `code`, `label` and `description` in any case (MQ7010), so the built-ins, seed column keywords and CSV headers never collide with a user field.
- Tags, category, stereotypes, `properties` and `generation` work as on every element, so a project marks "extensible by tenants" or "editable at runtime" with its own stereotype and extension schema (P2); the product attaches no behavior to such marks.

### 1.2 Rows

A reference type's rows are the rows of the seeds whose `target` is the type (§2), in (seed name, file order). The editor creates one seed with the type and names it after it; further seeds (demo rows, test rows, a customer's extra units) are allowed and are told apart by their own tags. Codes are unique across all seeds of the type (MQ7001).

**Rows are referenced by code, not by row id** (RS4). Under every storage strategy the stored value of a single-valued attribute is the code (the lookup table's key, the CHECK list's value, the native type's label), so the code is the physical identity and a code change is a data migration whatever the model says. Defaults and seed cells therefore hold codes, as enum defaults hold member names. Row ids exist for translations and for merges. Changing a code is a refactoring ("Rename code…") that rewrites every default, `allowedValues` entry and seed cell in one batch, found through the reverse index (§2.2).

### 1.3 Using a reference type as an attribute type

`TypeRef.Ref` may name a reference type, on any attribute: entity, value object, relation, stereotype virtual attribute, or another reference type's field. Nothing else is new:

- **Single or many**: `collection: false | true`. **Nullability**: `required`. **Restriction**: `validation.allowedValues` lists the codes allowed for this attribute. **Default**: a code (`"default": "kg"`); MQ3019 is extended to report a code not in the rows.
- **On a relation** (the owner's example): relation attributes already exist (SPEC §7), so `contains` carries `quantity` and `unitOfMeasure`. Relation *ends* never take a reference type; they name entities.

```json
{
  "kind": "relation", "id": "01JBWPQ5E6EYCNDY0YP57RCYBV", "name": "contains",
  "ends": [
    { "id": "01JBN5SXS5AA819X9YP981068V", "entity": "01JB8XE6SZAEAVSNTCPM5Q1NXW", "role": "recipe" },
    { "id": "01JBCD1GDJFMGT83PXT891WB09", "entity": "01JB1RNJ47E65GH2BH8VGS9ZM5", "role": "ingredient" }
  ],
  "attributes": [
    { "id": "01JBB9Y73MY63FCH26W14WMCHW", "name": "quantity", "type": "decimal", "precision": 12, "scale": 3, "required": true },
    { "id": "01JBYFGCW8T7SWM4FV4DK79Q9G", "name": "unitOfMeasure", "type": { "ref": "01JBM9S346Q3D25VT4F5V37E3S" }, "required": true, "default": "g" }
  ]
}
```

A collection example on Ingredient: `{ "id": "01JBF0ZKQ3T8W1Z5C6B0DEH2GM", "name": "allergens", "type": { "ref": "01JBH7V2M4T8W1Z5C6B0DEX9QR" }, "collection": true }`.

### 1.4 Storage strategies (default: template-defined)

The engine knows no strategy. A project that wants one declares it in `maquettiste.json` and chooses it per project, per database or per type; the resolver only resolves the choice and validates it (RS5).

```json
"referenceData": { "strategies": {
  "lookup-table": { "description": "Table keyed by code, FK from each column", "collections": true,
                    "options": { "schema": { "type": "string" }, "tableName": { "type": "string" } } },
  "check":        { "description": "CHECK (col IN (...codes))", "collections": false },
  "native":       { "description": "CREATE TYPE ... AS ENUM on PostgreSQL, ENUM(...) on MySQL", "collections": { "*": false, "postgresql": true } } } },
"conventions": { "referenceStorage": { "strategy": "lookup-table" } },
"databases": { "reporting": { "referenceStorage": { "strategy": "check" } } }
```

- **Effective choice** for a type in a database: `type.storage[<database id>]` → `type.storage["*"]` → `databases.<db>.referenceStorage` → `conventions.referenceStorage` → none. **None means template-defined**: the pack decides, usually from its own parameter. A choice's `options` are validated against the declared option schema (MQ7008). `collections` is a boolean or a map from dialect (with `*` for the rest) to boolean, evaluated for the database's dialect; `false` makes a collection attribute on that type an error in that database (MQ7006). A `storage` key naming no database is MQ2001, and one naming another kind MQ2002: the index records every key as a reference, so a database rename changes nothing and a database delete with `remove-references` drops the override.
- **What the resolver does and does not do.** A single-valued reference attribute maps to one column through the existing attribute-to-column rule (SPEC §10). The column's type is **reference-typed**, not a physical type: `RColumn.Type` has kind `reference`, `RColumn.ReferenceType` is set, and the code's logical type and facets (`string`, length 8) ride along. `type_of <column> "<target>"` asks the pack's type map for the effective strategy first (`reference:<strategy>`, or `reference:*` for template-defined) and falls back to the code's logical type, so the DDL the pack emits and the type the snapshot records come from the same answer. No FK, CHECK, lookup table or native type is added whatever the strategy. A collection attribute maps to **no** column and no child table: it is listed in `REntityMapping.TemplateDefined` (or `RRelationMapping.TemplateDefined`), and the template creates what the strategy calls for (junction table, array column, JSON). Overlays and mappings keep working on the single-valued column as on any column.
- **Snapshot.** The schema snapshot (SPEC §9) records a reference column as `type: "reference"` with `referenceType` (id), `strategy` (or null) and the code facets, never as `varchar(8)`. The differ reports a change of strategy or of code facets as `Altered` with `PropertyChange`s on those properties, and the pack decides the DDL: widening a `lookup-table` or `check` code column is an `ALTER COLUMN`; under `native` the column itself does not change. This changes `PhysicalSnapshot` (engine-design §14), in step 3.
- **Strategy objects** (lookup tables and their rows, CHECKs, native types, FKs to lookup tables) are not in `RDatabase` or the snapshot, so the `sql-ddl` pack reconciles them on every run instead of creating them if missing. Every object has a deterministic name (`ck_<table>_<column>_ref`, `<type snake>_t`, `fk_<table>_<column>_ref`). A CHECK is dropped if it exists and recreated with the current codes. A native type gets `ADD VALUE IF NOT EXISTS` for each new code; a removed or renamed code is a generation error from the pack naming the codes, because PostgreSQL cannot drop an enum value and the migration has to be written by hand. Lookup rows are upserted by code, then codes no longer in the seeds are deleted with a guarded `DELETE … WHERE code NOT IN (…)`, which fails on the FK while a retired code is still stored. Switching strategy drops the previous strategy's objects by their deterministic names before creating the new ones, and the migration drops a column's strategy objects before a diff-generated `DROP` or `ALTER` of that column and recreates them after. A later, opt-in extension may let a strategy declare its objects to the snapshot; it is not in this design.
- **Enums** are stored as `int` or `string` (`EnumStorage`, D9). The former `lookup` option, a lookup table the engine synthesized from the members, is retired (RS14, erratum E17): a settings or mapping file that still sets it does not load and reports MQ7012 naming the conversion to a reference type, whose strategy the templates realize like any other. This revises the first answer recorded in §7.

### 1.5 What the resolved model exposes

- `model.reference_types` (`RReferenceType`, ordered by (name, id)): the common element members (display name, plural, description, tags, category, stereotypes, properties); `code` and `label` (`RReferenceField`: `id`, `type`, `length`, `pattern`, `display_name`, `description`); `attributes` (flattened as for entities, SPEC §7.2 order); `rows` (`RRow`: `id`, `code`, `label`, `description`, `values` by field name, `refs`, `seed`, `order`). A reference-typed field's value is its code (or a list of codes), never a nested row, so a type that references itself (a unit's `baseUnit`) or two types that reference each other give no cyclic value; `refs` by field name resolves those codes to `RRow`s lazily, and `json <value>` writes `values` and never follows `refs`; `seeds`; `used_by` (every `RAttribute` whose type is this type, ordered by owner then order); `storage` (database name → `RStorageChoice`, resolved from the id keys).
- For each attribute using it: `attr.type.kind == "reference"`, `attr.type.reference_type`, and `attr.reference` (`RReferenceUsage`: `type`, `is_collection`, `required`, `allowed` (the rows allowed, after `allowedValues`), `default_row`, `storage` by database name). `RStorageChoice` is `strategy` (string, or null for template-defined), `options` (plain values) and `source` (`"type"`, `"database"`, `"project"` or null).
- For the column: `column.reference_type`. For the mapping: `mapping.template_defined` (collection attributes the templates must realize).
- New `for` scopes: `each reference type`, `each seed` (§2.5) and `each locale` (§3.7). Dependency keys: a reference type's `e:` key plus the `e:` keys of its seeds; `s:referenceData` for anything that reads a storage choice or a strategy declaration (§3.8); `RList` membership of `model.reference_types` records `k:reference-type`, of `rows` records `k:seed` and the type's seeds.

### 1.6 What the example packs demonstrate

- **`sql-ddl`** knows three realizations, `lookup-table`, `check` and `native`, and reaches them through its parameter `strategyMap`, from project strategy keys to realizations. The default map sends each of the three keys above to the realization of the same name; a project whose keys differ (`table`, `list-check`) maps them in the pack's parameters, and a key the map does not know is a pack diagnostic naming it, so the pack reserves no strategy name (P2). Its README documents the realizations with the settings snippet of §1.4, and its parameter `referenceStrategy` (default `lookup-table`) is what "template-defined" means in this pack. It shows: lookup table per type (code primary key, label, user fields, `ref` schema option), FK from each single-valued column, a junction table `<table>_<attribute>` for collections; CHECK constraints (single only; collections rejected by `collections: false` with MQ7006); `native` as `CREATE TYPE ... AS ENUM` on PostgreSQL with `text[]`-style arrays for collections, `ENUM(...)` on MySQL, and a CHECK fallback with a comment on SQL Server and SQLite, chosen by the template (single values only there: the example declaration's `collections` map is false outside PostgreSQL). Strategy objects are reconciled as §1.4 describes. `seed.scriban` upserts reference rows and entity seeds (§2) in dependency order. A `uuid` code is `uuid` on PostgreSQL, `uniqueidentifier` on SQL Server, `TEXT` on SQLite and `char(36)` elsewhere in the lookup table's key, and its codes are quoted literals in the CHECK, like string codes; `csharp-dapper` types it `Guid` (rows as `new Guid("…")`, `Find` compares the rows in turn) and the TypeScript sample keeps it a string with a comment (2026-09-29).
- **`csharp-dapper`** emits one `sealed record UnitOfMeasure(string Code, string Label, decimal Factor, string? Symbol)` per type, a static class with one field per row (`UnitOfMeasures.Kg`) and `All`, attributes typed as the code (`string`, or `IReadOnlyList<string>` for collections) with an optional strongly typed code struct behind a pack parameter, and a Dapper type handler for collection columns. With `each locale` it emits `.resx` resource files for display names and row labels (§3).
- The example model gains `UnitOfMeasure`, `Allergen`, `Recipe`, `Ingredient` and the `contains` relation above, with golden outputs for all three strategies.

### 1.7 Validation

Existing rules cover references: **MQ2001** for a type ref, seed target or default naming a missing id, **MQ2002** for one naming the wrong kind, **MQ3019** (extended) for a default code outside the rows, **MQ1004** for duplicate row ids, **MQ2001** and **MQ2002** for a `storage` key that names no database or another kind. New rules, range MQ7xxx (data and translations; none is MQ1xxx, so every one can be re-levelled in `validation.rules`):

| Id | Severity | Rule |
| --- | --- | --- |
| MQ7001 | error | Duplicate code in a reference type, across all its seeds (ordinal comparison) |
| MQ7002 | warning | Two codes of one type differ only by case |
| MQ7003 | error | A row misses a required field: code or label on a reference row; a required attribute with no default and no default expression on a seed row (a generated key excepted) |
| MQ7004 | error | A cell does not match its column's type or facets (the MQ3019 checks: type, length, pattern, range, enum member name) |
| MQ7005 | error | A seed column names neither a field nor an end of the target, names one twice, or a row has more values than columns |
| MQ7006 | error | A collection attribute whose effective strategy in a database declares `collections: false`; the message names the database and where the choice came from |
| MQ7007 | error | A storage choice names a strategy the project has not declared |
| MQ7008 | error | Storage options fail the strategy's option schema |
| MQ7009 | error | A cell names a row that does not exist: a code not in the reference type, or a row id not in a seed of the end's entity |
| MQ7010 | error | A reference type's code type is not `string`, `int16`, `int32`, `int64` or `uuid`, a field's type is a value object or an entity, or a field is named `code`, `label` or `description` in any case |
| MQ7011 | error | A `validation.allowedValues` entry on a reference-typed attribute names a code that is not in the type's rows |
| MQ7013 | error | A row code of a `uuid`-coded type is not a UUID in canonical form (8-4-4-4-12 lowercase hexadecimal digits with hyphens); the message gives the canonical form of an uppercase one. A code of the wrong JSON kind stays MQ7004 |
| MQ7012 | error | A settings (`enumStorage`) or mapping (`storage`) file sets the retired enum `lookup` storage; raised at load, the file does not load; convert the enum to a reference type |

### 1.8 Enums stay separate

| Use an **enum** when | Use a **reference type** when |
| --- | --- |
| Code branches on the values (`switch`, state machines, permissions) | Values are data that people pick from and maintain |
| The set changes only with a release | Rows are added or retired between releases, or per tenant (a project stereotype) |
| Members need only a name, a value or code and a description | Rows carry fields (factor, symbol, ISO numbers, sort order) |
| Tens of members | Tens to thousands of rows |
| Generated as a language enum | Generated as a record and static class, a table, a CHECK or a native type, as the pack decides |

Both are localized the same way (§3). Two refactorings convert between them in one batch: **Convert enum to reference type** (members become rows: name → code, display name → label; attributes, defaults and seeds retargeted) and back when the type has no user fields and its codes are identifiers.

### 1.9 Where reference types live

- **Files**: `model/reference-types/` for the types, `model/seeds/<type kebab>/` for their rows (§2.1), `model/locales/<locale>/_reference-data.json` for their translations (§3.3).
- **Explorer**: reference types appear **only** in the Reference data screen. The tree gets one top-level leaf row, **Reference data** (`812 types · 16,240 rows`) between Domain model and Databases (RS11); Enter opens the screen; it has no children. *(As built 2026-10-01, §4.7: the Reference data explorer lists the types; a type's only seed, named after it, is not listed under it, several seeds are.)* The rail gains a **Reference data** screen. Canvases never show reference types as nodes; an attribute typed by one shows `→ Unit of measure` with the reference-data icon.
- **Search** covers every kind (the owner's "search across all types"): reference types are index rows like any element, ranked with the rest (explorer-redesign §3.1), shown with the breadcrumb `Reference data › Measurement › Unit of measure` (category path) and opened in the screen. Row codes and labels are searched inside the screen, not globally, to keep 16,000 rows out of the search worker.
- Seeds of entities and relations stay in their target's domain, in the **Seed data** kind group already named in explorer-redesign §1.2, and on a Seed data tab of the entity editor. Seeds of reference types appear only in the screen.

## 2. Seeds

A **seed** (`kind: "seed"`) holds rows of data for one target: an entity (including inherited and stereotype virtual attributes), a relation (its ends and attributes: the rows of `contains`), or a reference type. Any number of seeds per target; `tags` tell them apart (a pack filters with `where.tags`). A seed belongs to its target: it is deleted with it, and its columns follow the target's fields (§2.7, RS12). This moves seeds from phase 4 into the phase that ships reference types (RS6).

### 2.1 File format

`model/seeds/<target kebab>/<seed kebab>.json`: a target's seeds share one folder named after the target, suffixed `-<last 6 characters of the target id>` on a collision as element files are, and moved in the same save when the target is renamed. Seed names are unique per target (the MQ3001 scope of a seed is its target), so 5,000 entities can each have a seed named `Demo`. Columns are listed once; each row is one line with its own id.

```json
{
  "$schema": "../../../.schema/v1/seed.json",
  "kind": "seed",
  "id": "01JB9EE0ZBGJ09TQM83XSSSS6Y",
  "name": "UnitOfMeasure",
  "target": "01JBM9S346Q3D25VT4F5V37E3S",
  "columns": ["code", "label", "01JBNPZX45HY43KWJRP1XPA7Z3", "01JBDJ8FSSZ5AWSH8VHTPRE95B"],
  "rows": [
    { "id": "01JBS3C4DWA7N36096Q14DR9GP", "values": ["kg", "Kilogram", 1000, "kg"] },
    { "id": "01JBQY77ZXYYK596NGYA1DQ91K", "values": ["g", "Gram", 1, "g"] },
    { "id": "01JB5GQAPENECFSECZP11HYGCP", "values": ["pinch", "Pinch", 0.36] }
  ]
}
```

- **Columns** are built-in keywords (`code`, `label`, `description`) for reference types, and attribute or relation-end ids otherwise. Ids, not names, so renames never touch seeds; the editor and CSV show names. An entity seed may name to-one ends only (MQ7105).
- **Cells**: a JSON literal of the attribute's type (as `default`, SPEC §6); an enum member name; a reference code, or an array of codes for a collection; an object of member values for a value object (an array for a collection); `null` for no value. Trailing nulls are dropped in canonical form.
- **Canonical form** (RS7). The `rows` array schema carries `"x-layout": "row-per-line"` and each row's `values` array carries `"x-trim": "trailing-nulls"`; the layout-driven writer and `ICanonicalJson.IsCanonical` (MQ1003) both read these two schema keywords, so the semantic rule lives in the schema like `default` omission does. Each row is written on its own line, indented as an array item, in exactly one form: `{ "id": "<ulid>", "values": [<cell>, <cell>] }`, with one space inside braces, none inside brackets, `", "` between items and `": "` after keys, value-object cells written the same way (`{ "amount": 5, "currency": "EUR" }`), arrays as `["a", "b"]`, and `values` left out when empty. Numbers in cells are written as the shortest plain decimal text of their value: no exponent, no `+`, no leading zeros, no trailing fractional zeros (`1e3` and `1000.0` become `1000`, `0.360` becomes `0.36`), computed on the decimal text, never through `double`.
- **Diffs.** Changing a row is a one-line diff. Appending a row is a two-line diff, because the previous last line gains a comma, so two branches that each append a row conflict on that line; the resolution keeps both lines. Git merges are otherwise per row.

### 2.2 References between rows

A relation-end column (for a to-one end of an entity (MQ7105), or either end of a relation seed) holds the **row id** of a row in any seed of the end's entity: entities have no code, and row ids survive key changes. An `Ingredient` seed row's to-one end column holds `"01JB…"`, a row id of a seed of the other entity; a `contains` seed row holds a recipe row id, an ingredient row id, `0.25` and `"kg"`. Reference cells hold codes (§1.2). The engine indexes every row id and every code use as reverse references (`ReferenceInfo` with the pointer `/rows/3/values/2`), so "where used" and code renames reach seed cells.

A relation's links are stated in one place only: either end columns in seeds of an end's entity (natural for a relation mapped to a foreign key) or seeds of the relation (needed when the relation has attributes, like `contains`), never both (MQ7106).

### 2.3 CSV import and export

- **Headers.** Built-in columns carry an `@` prefix that no attribute or role name can have (names match `^[A-Za-z_][A-Za-z0-9_]*$`): `@id` first, then `@code`, `@label` and `@description` for reference types, then attribute names and end role names as written, then optionally `@label:<locale>` and `@description:<locale>` per requested locale. SPEC §6's Invoice, which has an attribute named `id`, exports `@id` and `id` as two distinct headers. Header matching is exact and case-sensitive; a header that matches no column is listed in the import preview and ignored.
- **Export** (`GET /api/seeds/{id}/csv`, CLI `maquettiste seed export`): RFC 4180, UTF-8, the headers above. Reference cells export codes, collections as `;`-joined codes, end cells as row ids, value objects as compact JSON. `?bom=true` adds a BOM and CRLF, since spreadsheet programs need them; the default has neither.
- **Import** (`POST /api/seeds/{id}/csv?mode=merge|replace&dryRun=true`): rows match by `@id`, else by `@code` for reference types, else are new rows with new ULIDs. `merge` updates and adds; `replace` also deletes rows missing from the file, under the row-delete rules of §2.7. `@label:<locale>` and `@description:<locale>` columns write translations (§3). The answer is a preview (`added`, `changed` with before and after cells, `removed`, rows blocked by references, diagnostics); applying it is one batch with the seed's `expectedHash`, so it is atomic and undoable. Pasting a tab-separated range into the Rows grid goes through the same parser.

### 2.4 Validation

MQ7003 to MQ7005 and MQ7009 apply to every seed row. Specific to seeds:

| Id | Severity | Rule |
| --- | --- | --- |
| MQ7101 | error | A seed targets an abstract entity |
| MQ7102 | error | A key cell is missing when the target's key strategy is `application`, or two rows of one target (across its seeds) share a primary or alternate key value |
| MQ7103 | warning | Required row references form a cycle, so no insert order exists; rows in the cycle keep file order. Reference cells count as row references here (a reference type's field typed by another reference type orders lookup rows), end cells always do |
| MQ7104 | warning | A seed has more than 10,000 rows or 5 MB: seeds are reference data, not bulk data |
| MQ7105 | error | A column of an entity seed names a to-many end (the link belongs in a seed of the relation) |
| MQ7106 | error | A link stated twice: an entity seed has a column for an end of a relation that also has seeds |

### 2.5 Template access

`model.seeds` and `for: "each seed"`; `entity.seeds`, `relation.seeds`, `reference_type.seeds`. `RSeed`: `target`, `columns` (`RSeedColumn`: `name`, `kind` `"builtin" | "attribute" | "end"`, `attribute`, `end`), `rows` (file order) and `ordered_rows` (dependency order), and `model.seeds_in_order` (seeds ordered so that the targets of referenced rows come first). `RSeedRow`: `id`, `seed`, `order`, `values` by column name (enum cells as `REnumMember`, reference cells as codes, end cells as row ids) and `refs` by column name, which resolves reference and end cells lazily to `RRow` and `RSeedRow`; `json <value>` never follows `refs`, so rows that reference each other (MQ7103 allows such cycles) serialize finitely. A unit of `each seed` has the seed's id as its unit key. Helpers: `row <reference type> "<code>"` returns the `RRow`; `row_uuid <row>` returns the row's 128-bit ULID written as a UUID, for packs that key seeded entities by UUID.

### 2.6 Determinism

Rows keep file order; `ordered_rows` is Kahn's algorithm with ties broken by file order, and across seeds by (target dependency depth, seed name, id). Numbers keep their canonical decimal text (§2.1; a decimal cell `0.36` reaches `sql_literal` as written, never through `double`). CSV export orders rows as the file and columns as the seed; import assigns ULIDs through `IIdGenerator`, never by time in output. Nothing in a seed depends on the clock or the machine.

### 2.7 Ownership and deletes (RS12)

- **A seed belongs to its target.** The index records `target` as an **owning** reference (`ReferenceInfo.Owning`), which never refuses the target's delete. Deleting a target deletes its seeds in the same batch, and the delete confirmation lists them with their row counts, as an impact analysis lists dependent objects. A reference type is therefore refused only while attributes, defaults, `allowedValues` or cells of other seeds use it (§4.2), never because of its own seeds.
- **Rows referenced from outside.** Rows of the deleted seeds may be named by end cells of other seeds. Those cells are references like any other: `refuse` lists them; `remove-references` sets optional end cells to null and makes the delete invalid when an end is required (MQ7003). The editor then offers **Delete the referencing rows too**, one batch that deletes those rows first (repeating for rows that reference them), previewed with counts. The `DeleteResolution` enum does not change.
- **Removing a field.** Removing an attribute or an end from a target, removing it from a supertype, or removing a stereotype (and so its virtual attributes) drops that column and its cells from every seed of the target and of its subtypes in the same save. The editor and the API do it as one batch, as a rename moves a file. A hand edit that leaves such a column gets MQ2001 or MQ7005 with the quick fix "Remove the stale column".
- **Deleting rows.** Deleting a seed row (grid, CSV `replace`, API) that end cells elsewhere name is refused with the list of cells, unless the user chooses to clear the optional cells or delete the referencing rows in the same batch. Deleting a reference row whose code a default, an `allowedValues` entry or a cell uses is refused with the list; "Replace with…" rewrites them to another code in the same batch.
- **Translations.** Deleting any element, or removing any localized sub-element (attribute, end, member, row), removes its entries and their Markdown sidecars from every locale shard in the same save. MQ7203 (orphans) remains for hand edits and merges.

## 3. Localization of the standard fields

### 3.1 What is localized

**The rule** (RS13): every domain-model element's standard fields (`displayName`, `pluralName`, `description`: SPEC §5 "Fields on every element"), and every sub-element field named `displayName`, `pluralName` or `description`, plus a reference row's `label`, are localizable. Physical elements (databases, tables, views, sequences), mappings and diagrams are out of scope: they are not model content in a user's language. A new kind or sub-element that has these fields is localizable without a change to this design. The table shows examples, not a closed list:

| Node | displayName | pluralName | description | label |
| --- | --- | --- | --- | --- |
| Any domain-model element: package, entity, value object, scalar type, enum, relation, reference type, seed, category; later process, state, transition, operation, event, actor, query, projection | ✓ | ✓ | ✓ | |
| Attribute (entity, value object, relation, stereotype virtual, reference-type field), reference `code` and `label` fields | ✓ | | ✓ | |
| Relation end (new optional `displayName` and `pluralName` on `RelationEnd`, RS8) | ✓ | ✓ (to-many ends; MQ7211 on a to-one end) | ✓ | |
| Enum member | ✓ | | ✓ | |
| Reference-type row | | | ✓ | ✓ |

Names (identifiers), codes and user-field values are not localized: the owner limited localization to the standard fields.

**Shard of a node** (§3.3): an element's own package (a package's own entry lives in its own shard); a seed's target's package; reference types, their seeds and their rows `_reference-data`; an element with no package `_root`. A sub-element goes where its owning element goes.

### 3.2 Project settings

```json
"localization": { "defaultLocale": "en", "locales": ["en", "fr", "fr-CA", "de"], "fallbacks": { "fr-CA": ["fr"] }, "require": ["entity", "attribute", "enum-member", "reference-row"] }
```

`defaultLocale` is the language of the texts inside element files; `locales` (BCP 47, must contain the default) are the supported ones; `fallbacks` replace the default chain for a locale; `require` lists the node kinds the completeness rules count (empty = every localizable node): element kinds (`entity`, `relation`, `reference-type`…) and the sub-element kinds `attribute`, `end`, `enum-member`, `reference-field` and `reference-row`; an unknown kind is MQ7201. Without a `localization` block, nothing in this section appears anywhere. Changing the default is a refactoring (`maquettiste l10n set-default de`) that swaps texts between element files and sidecars in one reviewable change.

### 3.3 Sidecar files, per language and domain (RS9)

Default-language texts stay in the element files, unchanged. Every other locale has one folder, `model/locales/<locale>/`, with **one file per domain**: `billing.json`, `billing/catalog.json` for a sub-domain, `_root.json` for elements with no package, `_reference-data.json` for reference types and their rows. Entries are keyed by id and placed by the shard rule of §3.1.

**Shards are identified by content, not by path.** Each shard has `"kind": "locale-shard"`, so the loader dispatches on it like any file (a shard outside `model/locales/<its locale>/` loads, with MQ1005), and `"scope"`: the package's id, `"root"` or `"reference-data"`. The file name is derived from the scope, as element file names are from names: the package's kebab path, renamed (with its sub-packages' shards) in the same save as a package rename, and suffixed `-<last 6 characters of the package id>` when two sibling packages kebab to the same name (`HttpServer` and `HTTPServer` both give `http-server`, engine-design §9). Moving an element to another domain moves its entries in the same save. An entry in a shard other than its node's is MQ7207 and still applies; one id in two shards of a locale is MQ7209, and the ordinally first path wins; a shard whose `locale` or `scope` disagrees with its path is MQ7210.

**Why per language and domain, not per language.** At spec scale (5,000 entities, 60,000 attributes at 12 each, 20,000 relations with 40,000 ends, 500 enums with 5,000 members, 1,800 types and value-object fields, 800 reference types with 4,000 fields and 16,000 rows) a locale has about 150,000 nodes. At about 85 bytes an entry (26-character key, one or two short texts, indentation), one file per language is about **13 MB**: every translation saved rewrites and rehashes 13 MB, the file watcher reparses it, and every translator touches the same file. Per domain, 40 domains average about **320 KB**, the largest about 1.5 MB, and `_reference-data.json` about 1.8 MB; a save touches one small file and two people translating different domains never touch the same file. One file per element (27,000 per locale) was rejected: it multiplies the model's file count by the number of locales for no gain over domain shards. The same estimate sets what the index carries (§3.8): all locales' display names in the index would add about 540 KB per locale, so the index serves one locale at a time.

```json
{
  "$schema": "../../../.schema/v1/locale.json",
  "kind": "locale-shard",
  "locale": "fr",
  "scope": "reference-data",
  "entries": {
    "01JBM9S346Q3D25VT4F5V37E3S": { "displayName": "Unité de mesure", "pluralName": "Unités de mesure", "src": { "displayName": "5c1e9a02", "pluralName": "0d4b7e11" } },
    "01JBS3C4DWA7N36096Q14DR9GP": { "label": "Kilogramme", "src": { "label": "a93f0d7b" } }
  }
}
```

`src` maps each translated field to the first 8 hex digits of SHA-256 over the default-locale text that field was translated from: the string, or for a sidecar description the Markdown content with LF line ends (not the `{ file }` reference), or for a row's label the label cell. Staleness is per field: translating the description later hashes only the description, so a stale display name stays stale. The editor, API, CSV and XLIFF imports write it; a hand editor may leave a field out (then staleness is not tracked for that field). When a default text changes, that field is **stale** until someone confirms or retranslates it, as in XLIFF's `needs-review` state; confirming rewrites that field's hash only.

### 3.4 Long descriptions

A translated description is a string or `{ "file": "<path>" }`, relative to `model/locales/<locale>/`. The editor mirrors the default: when the node's default description is a sidecar (SPEC §11), the translation is a Markdown sidecar; otherwise a string. The sidecar's path mirrors the model-relative path of the file that holds the node, without `.json`: `model/entities/billing/invoice.json` gives `model/locales/fr/entities/billing/invoice.md` for the entity and `…/invoice.<sub-element id, lowercase>.md` for one of its attributes, ends, members or (under the seed's path) rows. Element file paths are unique, so these are too, whatever the kinds (an entity and a relation both named Invoice have different kind folders), and a rename or move of the element file moves its sidecars in every locale in the same save. A sidecar change changes the shard's dependency hash (as `DependencyHash` does for elements today) and the owner's `l:` key.

### 3.5 Canonical form

`locale.json` has `x-order` `["$schema", "kind", "locale", "scope", "entries"]`; `entries` is a free-form map, sorted ordinal by id (added to the engine-design §3 list of free-form maps with `storage`, `strategies` and `fallbacks`); entry keys follow `x-order` `["displayName", "pluralName", "label", "description", "src"]`, and `src` keys the same order; empty strings and empty entries are dropped; a shard with no entries is deleted. Standard file rules otherwise (UTF-8, LF, two spaces, trailing newline).

### 3.6 Validation

Completeness must not flood the Problems panel with 100,000 rows, so it reports per shard by default and per node only on request:

| Id | Severity | Rule |
| --- | --- | --- |
| MQ7201 | error | Invalid settings: a locale that is not BCP 47, a default not in `locales`, a fallback naming an undeclared locale, a fallback cycle, a `require` entry naming an unknown node kind |
| MQ7202 | warning | A locale folder for an undeclared locale or for the default locale; its files are not loaded |
| MQ7203 | warning | Orphan: an entry whose id is not in the model, or a field the node does not have (a `label` on an entity); remove them by setting the entry to null through `PUT /api/localization/{locale}/entries` or the `set_translations` MCP tool or with `maquettiste l10n prune --apply` (§3.9) |
| MQ7204 | info | Incomplete: one diagnostic per (locale, shard) with the missing and stale counts, for the kinds in `require`. A team that gates releases raises it to error |
| MQ7205 | off | Missing translation, one per node and field; turned on for per-element detail |
| MQ7206 | info | Stale translation (`src` differs), one per (locale, shard) with the count |
| MQ7207 | warning | An entry in the wrong shard (its owner moved outside the editor); it still applies |
| MQ7208 | error | A description sidecar that does not exist |
| MQ7209 | error | One id in two shards of one locale; the ordinally first path applies |
| MQ7210 | warning | A shard whose `locale` differs from its folder, or whose `scope` differs from the path derived from it; it applies to its declared locale and scope |
| MQ7211 | warning | A `pluralName` on a to-one relation end, or its translation; it is never read |

"Expected" means: a display name for every node of §3.1 (the default-locale display name falls back to the name, so there is always a source), a plural name for the kinds marked, a label for every row, and a description only when the default locale has one.

### 3.7 Helpers and the fallback chain

- `display_name <x> [locale]`, `plural_name <x> [locale]`, `description_of <x> [locale]`, `label_of <row> [locale]`, `translate <x> "<field>" [locale]` (any §3.1 field), and `has_translation <x> "<field>" <locale>` (no fallback, for packs that emit only translated keys). `model.locales` lists `RLocale` (`tag`, `is_default`, `chain`).
- With no locale argument, the locale is the unit's `locale` in an `each locale` unit, else the default. `for: "each locale"` plans one unit per supported locale (default first, then ordinal), with `locale` as the scope alias, for resource bundles. Its unit key is `locale:<tag>` (there is no element), and `where` is rejected on it at pack load, since there is no element to filter.
- **Chain**: the locale itself; then `fallbacks[locale]` if declared, else its BCP 47 truncations that are supported (`fr-CA` → `fr`); then the default locale; then the built-in fallback the resolver already applies (display name → name; plural → inflector over the default display name). `RLocale.chain` exposes it, so a pack can show where a text came from.
- Existing members (`element.display_name`, `plural_name`, `description`) stay the default-locale values, so every current template is unchanged.

### 3.8 Determinism, incremental generation and the index

- Helpers are pure; no culture-sensitive formatting (the render context stays invariant); no inflection outside the default locale.
- **Loading.** Every shard of every declared locale is parsed once at load, streaming: the pass counts completeness (§3.6) and keeps each entry's display name and `src` hashes. Plural names, descriptions and labels are dropped and read on demand per (locale, shard), cached by file hash. "Lazy" applies to those texts only.
- **Dependency keys.** A helper reading a translation records `l:<locale>:<owner id>` for every locale of the chain it consulted, up to the one that answered, plus the owner's `e:` key when it fell through to the default text (or to the name or the inflector). The owner is the element whose file holds the node; for a reference row it is the seed that holds the row, whose `e:` key changes with the label cell. An `l:` hash covers the owner's entries in that locale (all fields of the owner and its sub-elements) and their sidecars. Two settings keys join engine-design §11's table: `s:localization` (the whole `localization` block, recorded by every helper call and by every `each locale` plan) and `s:referenceData` (strategies and `groupBy`, recorded by anything that reads a storage choice or a strategy declaration); `referenceStorage` in `conventions` and `databases` is already covered by `s:conventions`. So changing `fallbacks` or a strategy re-renders exactly the units that read them, and an incremental run equals a full run. Changing one French label re-renders only units that read French (or a locale that falls back to French) for that seed.
- **Index.** `GET /api/model/index?locale=fr` fills `displayName` from the chain for fr. The host builds a per-locale display-name table once per set of shard hashes and keeps it in the host-volume index cache beside the index, so a request joins that table and parses no shard. The E5e ETag covers the locale and the shard hashes, and the index format constant becomes `maquettiste-index/e6`, because the row shape changes. Without `locale`, today's rows. New optional members: `fieldCount` on reference-type rows (from the type's own file); `target` and `rowCount` on seed rows. Counts derived from other files are never put on a row: the tree and the screen sum `rowCount` by `target`, so adding a row changes only its seed's summary, which E5d already sends. No per-locale texts beyond `displayName`: descriptions and other fields load on demand.
- **Events.** A translation save changes no element, so `ModelChangedEvent` gains `translations: [{ locale, displayNames: { <id>: <text> } }]`, with one item for each declared locale whose chain includes the changed locale and the effective display name after the change (the fallback text when a translation was removed). It is cut at 200 KB like E5d; beyond that, `translationsTruncated: true` makes the editor refetch the index for its content locale only. Other fields are not sent; an open inspector or translation queue refetches its owner's entries.

### 3.9 API operations, MCP tools and CLI

| Operation | Purpose |
| --- | --- |
| `GET /api/localization` | Settings, and completeness per locale and shard: expected, translated, missing, stale |
| `GET /api/localization/{locale}/entries?owner={id}` or `?shard={path}` or `?missing=true&cursor=` | Entries with their source text, translation, state (`translated`, `missing`, `stale`, `fallback`), shard path and hash; the paged `missing` form feeds the translation queue |
| `PUT /api/localization/{locale}/entries` | `{ entries: [{ id, field, value \| null, confirm? }], expected: { shardPath: hash } }` → 200 with new hashes, 409 on a changed shard, 422 with diagnostics; `confirm` rewrites that field's `src` hash without changing the text |
| `GET /api/localization/{locale}/export?format=xliff\|csv&shard=` and `POST …/import?dryRun=true` | XLIFF 2.1 (units keyed `id/field`, with `state`) or CSV for translators and translation services; import previews then applies as one batch |
| `GET /api/seeds/{id}/csv`, `POST /api/seeds/{id}/csv`, `POST /api/seeds/csv` (several seeds as one change, round 7) | §2.3 |
| `GET /api/reference-types/{id}/usage` | `used_by` with owner, domain, collection, required and effective storage per database |
| `batch.json` | A new operation `{ "op": "translate", "locale", "id", "field", "value" }`, so an agent creates an element and its translations atomically |

MCP tools (`src/Maquettiste.Cli/Mcp/ModelTools.cs`): `localization_status`, `get_translations`, `set_translations`, `export_seed_csv`, `import_seed_csv` (dry run unless `apply: true`), `reference_type_usage`. Reference types and seeds need no new tool: `get_element`, `create_element`, `save_element` and `apply_batch` handle them as elements. CLI (**built** 2026-09-29, after the demo; `src/Maquettiste.Cli/Commands/L10nCommand.cs`, `SetDefaultCommand.cs`, `SeedCommand.cs`): `maquettiste l10n status` (text or `--format json`), `l10n export <locale>` (XLIFF 2.1 or `--format csv`, `--out <file>`), `l10n import <locale> <file>` (a preview that also counts stale confirmations; `--apply` plans again and writes one save against the shard hashes read with that plan), `l10n prune` (lists the MQ7203 orphans; `--apply` removes them in one save, `ModelStore.PruneTranslationsAsync`) and `l10n set-default <locale>` (`ModelStore.ChangeDefaultLocaleAsync`: moves the new default's texts into the element files and the replaced texts into the old default's shard, one atomic change, checked against the hashes the model was loaded with; sidecar descriptions and orphans are not moved: they stay in the new default's locale folder, which is no longer loaded (MQ7202), and a sidecar-described element keeps its old-language description; the command lists them as skipped. Move or delete them by hand, or run `l10n prune` before switching), and `maquettiste seed export <seed> [--locale <tag>]` and `seed import <seed> <file> [--mode merge|replace]` (a preview unless `--apply`). On these verbs, `--apply` re-plans in its own run: nothing carries over from an earlier preview run, and a write is refused (exit 3) only when a file changes while the command runs; the output lists what was written. `--check` exits 2 when something would change; an argument that names no locale or seed, and a refused write (MQ6004), exit 4. docs/user-guide.md lists them; the MCP tools are unchanged (no prune or set-default tool).

### 3.10 Editor: present, not crowded

- **Nothing appears while one locale is declared.** No switcher, no Translations section, no locale columns.
- **Top bar locale switcher** (with two or more locales): "Content: English ▾", listing each locale with its completeness percentage. It sets the language shown in the tree (when display names are shown), canvases, the Reference data screen and search. Editing a display name, plural or description while a non-default locale is active writes that locale's translation; the field shows a small locale badge and the default text as a placeholder. Names and codes are never localized, so they edit the same in every locale.
- **Translations section** in the inspector (two or more locales, collapsed by default, remembered per user): one compact row per locale with display name, plural and description (a Markdown editor opens for long ones), fallback text in muted italic, a stale marker with "Confirm".
- **Completeness view**: Settings › Localization declares the locales and fallbacks and shows a matrix of locales × domains (plus Reference data) with percentages. A cell opens the **translation queue** for that locale and shard: a two-column list (source, translation) with Enter to save and go to the next missing entry, Ctrl+Enter to confirm a stale one, and "Open element" beside each row. The explorer gains a filter chip "Missing in fr" (explorer-redesign §3.2).

## 4. The Reference data screen

### 4.1 Layout

```
┌ Reference data ──────────────┬──────────────────────────────────────────────────────────┐
│ [Search 812 types…        ]  │ Unit of measure   UnitOfMeasure · 42 rows · used by 7    │
│ Category ▾  Tag ▾  Strategy ▾│ [Fields] [Rows] [Used by] [Storage]                      │
│ ▾ Measurement           12   │  code ▲  label         factor   symbol                   │
│   Dimension              6   │  kg      Kilogram      1000     kg                        │
│ ● Unit of measure       42   │  g       Gram          1        g                         │
│ ▸ Geography              4   │  …                                                       │
│ ▸ No category           31   │ 42 rows · 0 errors · fr 90 %     [Import CSV] [Export]   │
└──────────────────────────────┴──────────────────────────────────────────────────────────┘
```

### 4.2 Type list

- A virtualized tree of the grouping path (nested like the explorer's domains, so the user always knows where they are), each group with its count, plus a flat A–Z toggle. The grouping is the project setting `referenceData.groupBy`: `category` (the default, the category path), `tag:<prefix>` (the tag under a project-chosen prefix, `tag:domain/`) or `property:<name>` (a project custom property). The product attaches no meaning to any of them (P2). Each type row shows its display name in the content locale and its row count; hovering shows the name, category path and field count.
- Search matches name, display name and category path, ranked like the explorer (§3.1 of explorer-redesign), and accepts the explorer's four operators as a prefix: `*` contains (default), `^` starts with, `~` like with `%`, `=` equals; case-insensitive; non-matching types are hidden and groups show "3 of 12". Filters: category, tag, strategy (including "template-defined"), "has errors", "missing in <locale>".
- Context menu (multi-select of types): New reference type, Duplicate, Rename, Move to category, Set storage…, Export CSV (one zip for several), Convert to enum, Delete (refused while attributes, defaults, `allowedValues` entries or cells of other seeds use it, listing them; the type's own seeds are not users and are deleted with it, §2.7).

### 4.3 Tabs

The tab choice persists when another type is selected (as an element editor in General mode keeps its tab), so a user can review Rows or Storage across many types with the arrow keys.

- **Fields**: the entity attribute grid (`inspector/AttributeGrid.tsx`), with `code` and `label` pinned and locked at the top (name fixed; type, length and pattern editable), then user fields. The type picker offers built-ins, custom scalars, enums and reference types.
- **Rows**: the grid of §4.4, over all the type's seeds (a Seed column appears when there are several).
- **Used by**: every attribute typed by this type, grouped by owner kind then domain, with collection and required badges; a row opens its owner. Selecting a type also highlights those attributes' owners in the explorer tree, as the explorer highlights related elements (switchable off).
- **Storage**: the effective strategy per database with its source ("from project settings"), an override chosen from the declared strategies or "Template-defined", an options form rendered from the strategy's option schema, and "Preview output" through `POST /api/templates/preview` on the type.

### 4.4 Rows grid and keyboard

- Virtualized rows and columns; `code` frozen on the left; errors as a red cell outline with the diagnostic as tooltip; the status bar counts rows, errors and the content locale's completeness.
- **Inline localization** (two or more locales): the label and description columns show the content locale, with fallbacks muted; "All locales" adds an `@label:<locale>` and an `@description:<locale>` column per locale, like a spreadsheet of translations. With one declared locale the status bar says "Declare a second locale under Settings › Locales to translate labels and descriptions."
- **Keys** (the SPEC §14 spreadsheet model): arrows move; Enter or F2 edits, Enter commits and moves down, Tab commits and moves right, Esc cancels; Ctrl+Enter inserts a row below; Ctrl+D duplicates the row with a new id and an empty code; Delete clears cells; Ctrl+Delete deletes the selected rows; Alt+Up and Alt+Down move rows; Shift+arrows and Shift+click select a range; Ctrl+C copies it as TSV; Ctrl+V pastes TSV, adding rows past the end (through the §2.3 parser, with the preview when it would add or delete rows); Ctrl+F finds in the grid; Ctrl+1 to Ctrl+4 switch tabs; `/` focuses the type search; F6 cycles regions. Every edit is one undoable draft saved like any element.
- *(Status 2026-09-29, round 7: the grid is one component, `SeedGrid` in `src/editor/src/workspaces/reference-data/RowsGrid.tsx`, used for reference types and, as a **Seed data** tab, in the entity and relation editors. Entity seed columns are the entity's and its bases' attributes plus the far to-one ends the engine lets a seed carry (MQ7105, MQ7106), edited with a picker over the far entity's rows; the locale label columns stay reference-type only, since the engine localizes only reference-row labels. Import and Export CSV sit in every seed grid's header, and "Export all seed data" / "Import seed data…" move every seed as one ZIP of CSV files. Built since: the atomic multi-seed import (`POST /api/seeds/csv`, `ModelStore.ImportSeedCsvBatchAsync`): every seed's rows in one all-or-nothing change checked against every seed's hash, used by Import seed data…; the files' `@label:<locale>`/`@description:<locale>` translations are written right after, one save per locale, because the engine's batch does not apply its `translate` operation yet.)*

### 4.5 The entity editor's field type picker

The attribute grid's Type cell opens a quick pick (a searchable drop-down list) with sections **Recent**, **Built-in**, **Custom types**, **Enums**, **Reference data** and **Value objects**, one ranked search across all of them. Picking a reference type shows two toggles beside the cell, **Many** (collection) and **Required**, and its hover card shows the row count and the first five codes. F12 on the cell opens the type in the Reference data screen; Shift+F12 on a type shows Used by. **New reference type…** at the bottom creates one (name, display name, category) without leaving the entity. The same picker serves relation attributes and value-object fields.

### 4.6 Prior art (kinds of tools, described by pattern; none of this is product vocabulary)

| Kind of tool | Pattern | What Maquettiste does |
| --- | --- | --- |
| ORM designers, model explorer | Nested group nodes with counts; search operators `* ^ ~ =`; context menus with multi-select; related-element highlighting | Category tree in the type list; the list search; the type menu; Used by highlighting |
| ORM designers, entity editor | Top controls above tabs; a general editor mode that keeps the tab across entities | Header (name, rows, used by) above Fields, Rows, Used by, Storage; tab persistence |
| ORM designers, catalog explorer | Selecting a physical element highlights the project elements mapped onto it | Storage tab preview; `column.reference_type` for a later "go to reference type" from a column |
| Data modeling tools | Domains as reusable attribute types; list-of-values checks generated per DBMS; impact analysis before a delete or change | Reference types as attribute types; per-database strategies; Used by, code renames and the delete confirmation that lists owned seeds and referencing rows (§2.7) |
| UML modeling tools | Package browser with elements and diagrams under packages | The tree's single Reference data leaf opening a dedicated browser |
| Database IDEs | Data editor grid, TSV copy and paste, CSV import with preview, lazy lists with counts | Rows grid, §2.3 import preview, counts on every group |
| SysML v2 | Definitions versus usages; metadata definitions | Reference type (definition) versus typed attributes (usages); stereotypes as the project's own metadata |
| Language IDEs | Find usages, go to definition, resource bundle editor | Used by, F12 and Shift+F12; the All locales columns |
| Code editors | Quick pick with sections, breadcrumbs | The field type picker; `Reference data › Measurement › Unit of measure` |
| String catalogs, translation platforms, XLIFF | Per-language percentages; review state for changed sources; a queue of untranslated strings | Switcher percentages; `src` staleness; the translation queue |

### 4.7 After the owner's first day of modelling (as built 2026-10-01)

*As built 2026-10-01.* The owner modelled a client's reference data for a day and reported nine problems; the screen
changed as follows (user guide, Reference data).

- **Row editor.** Long text did not read in single-line cells. `SeedGrid` (every seed grid: the Rows tab and the entity
  and relation Seed data tabs) has a side panel, `RowEditor.tsx`, resizable from the inspector's width within the
  inspector's limits, opened by Shift+Enter (`gridAction` → `open-row`), a double click on the row's handle (its number,
  now a `rowheader` holding an icon button "Open row N in the row editor") and that button. One labelled control per
  grid column (text areas for string and text fields, the end picker for relation ends), the field's description under
  its label; Save (Ctrl+S) writes the changed cells with one `setCells` in one draft save (one undo step), translations
  to their shard; Cancel (Esc) closes; Alt+Up/Down save and step. The inspector stays off on this screen
  (`inspector/context.ts`): the panel is the screen's own. Cells carry their whole text as the tooltip; the description
  column is 320 px and text fields 200 px.
- **Field display names and descriptions.** Every field, `code` and `label` included, edits `displayName` and
  `description` on the Fields tab (two columns of the built-in table; the attribute grid's last two columns, which the
  entity, value object, stereotype and process grids now have too; a sidecar description is named, not edited). The
  Rows grid's header shows the display name, the tooltip the name and the description; the row `description` column has
  no field object, so its help text is fixed.
- **Names.** The explorer no longer lists a type's only seed when it is named after the type (`soleSeedOf`; the seed is
  placed on the type's node, so revealing it reveals the type); several seeds are listed with their row counts, which
  supersedes "it has no children" in 1.9 for that case. A **General** tab (first; Ctrl+1 to Ctrl+5 now) renders
  `CommonFields` (name, display name, plural name, description, category, stereotypes, tags; no domain, RS3); its Name
  commits through the same batch as **Rename**, whose dialog now has Name and Display name and renames the type's only
  seed (or, of several, those named after the type) in one batch.
- **Used by** groups a reference-type owner as "Reference type · (category path)", or "Reference types" without a
  category, never "Not in a domain".
- **Storage.** Columns "Database", "Strategy in use" (with "from this type / from the database / from the project", or
  "The packs decide") and "Set for this type" ("Use the default (what it is)", "Let the packs decide", then each
  declared strategy by its key, its description shown under the select). A paragraph above the table says what a
  strategy is. Preview output lists the enabled packs' units scoped `each database` or `select databases` (from
  `GET /api/packs`, preferring a unit whose id mentions seed), names the unit, and on a failure asks
  `POST /api/generate/explain` and shows its `detail` (the reason the unit does not render for the database), the raw
  preview message folded under it. The owner's MQ6026 ("not '(id)' (database)") means the unit's selector did not
  return that database in his project; it did not reproduce on the billing project, so the deliverable is the
  explanation.
- **Menus.** Reference types join the explorer's marks (Apply stereotype…, Tag…; their category is Move to category…,
  which now says to add categories under Settings › Categories when none exist). No seed is movable any more: a seed
  file has no `package`, so Move to domain… wrote an invalid file; a reference type's seed also loses Rename (it is
  renamed with its type).
- **Names are identifiers, not PascalCase.** The schema's identifier is `^[A-Za-z_][A-Za-z0-9_]*$` and nothing enforces
  case, so every name hint (New reference type, Rename, the General tab, New entity, the explorer's New dialogs) now
  reads "Letters, digits and underscores, not starting with a digit, such as UnitOfMeasure or car_models"
  (`identifierHint` in `model/model.ts`). "Stored as" in New reference type uses the Storage tab's words: "Let the
  packs decide", then each declared strategy by its key, the chosen one's description shown under the select (no
  longer an option title); the type picker shows `packs decide` where it showed `template`.
- **Marks.** The General tab's Category, Stereotypes and Tags are `CommonFields`' controls, the inspector's for an
  entity, each change one draft save (one undo step); Stereotypes says when none applies to the kind.
- **Translations.** The General tab has the inspector's collapsed Translations section (display name, plural name,
  description per locale); the Fields tab has "Translations of the fields" (`FieldTranslations`): per locale, a line
  per field (`code` and `label` as `reference-field` nodes, user fields as `attribute` nodes, owned by the type, as
  §3.1 and the engine's `LocalizationIndex.SubNode` have them) with its display name and description. The Rows grid's
  one-locale hint has a button that opens Settings › Locales, and Move to category… one that opens Settings ›
  Categories. Row labels stay optional: MQ7204 is an info count per shard and MQ7205 is off by default (§3.6).

No engine or API change was needed. The mock renders a simplified sql-ddl `seed` unit per database once the model has
reference rows, so the preview has something to show, and its localization lists a reference type's fields as
sub-element nodes (it still lists no other element's sub-elements).

## 5. Migration and sizing

Each step leaves the model valid and the editor usable; steps 1 to 5 change contracts and go through `openapi.yaml`, the schemas and the contract tests first. Sizes are for one engineer or agent.

| # | Step | Status (2026-09-29, after the demo) | Main files | Tests | Size |
| --- | --- | --- | --- | --- | --- |
| 1 | Contract | **Built** | `schemas/v1/reference-type.json`, `seed.json`, `locale.json` (new); `common.json`, `relation.json`, `maquettiste.json`, `batch.json`, `snapshot.json` (reference columns), the `appliesTo` kind enums of `stereotype.json` and `extension.json`; `openapi.yaml` (`ElementKind`, `ModelDocument` and `NewModelDocument` gain the two kinds; `ElementSummary`; `ModelChangedEvent.translations`); `Model/*.cs` (Appendix A); `KindInfo`; canonical writer and `ICanonicalJson.IsCanonical` for `x-layout`, `x-trim` and seed-cell numbers; `engine-design.md` §2 and §3 | Schema consistency (x-order), canonical round trip of the three examples here, row-per-line layout, trailing-null trim and number normalization (`1e3`, `1000.0`), SPEC examples still fail only for E1–E3 | 2–3 days |
| 2 | Load, index, validate | **Built** | `ModelIndexer` (row ids, code uses and `storage` keys as references, owning `target` references, `rowCount`, `target`), `ReferenceWalker`, rules MQ7001–MQ7011 and MQ7101–MQ7106, MQ3019 extension, the delete rules of §2.7; editor: `explorer/filter.ts` leaves out `reference-type` rows and seeds whose target is a reference type, since the index serves them from this step | One fixture per rule under `tests/fixtures/validation/`; where-used reaches a seed cell; rename-code refactoring batch; deleting a seeded entity deletes its seeds; removing an attribute drops its seed column | 3 days |
| 3 | Resolver and packs API | **Built** | `RReferenceType`, `RRow`, `RSeed`, `RSeedRow`, `RReferenceUsage`, `RStorageChoice`, `TemplateDefined`; reference-typed `RColumn` and `type_of`; `PhysicalSnapshot` reference columns; scopes `each reference type`, `each seed`; helpers `row`, `row_uuid`; dependency keys incl. `s:referenceData` in engine-design §11 | Resolver golden tests; effective storage precedence table test; collection attributes absent from tables; a self-referencing type resolves and `json` terminates; incremental: editing one row re-renders only its seed units, changing a strategy declaration re-renders its users | 3 days |
| 4 | Localization engine | **Built**, and the `l10n` and `seed` CLI verbs with it (§3.9, built after the demo). *(Close-out, 2026-09-29:)* the parsed-shard cache keyed by shard hash is built (`Loading/ShardCache.cs`: `<cache>/shards/<hash>.bin` beside the index cache, filled after each full load in parallel, pruned of hashes gone; shards are scheduled first in the parallel read). `time-load --warm-cache` (restart) with four locales went from 779–1,187 ms to 569–871 ms against 454–611 ms without locales, so a restart adds about **0.1–0.3 s and meets ≤ 400 ms**; a first open (empty cache) still adds 0.4–1.1 s (837–1,609 ms before, 796–1,899 ms after, round 1 a cold process) and **misses** it. Completeness is not cached by shard hash: it depends on the source model's nodes as well, and costs 25–40 ms. `init --pack sql-ddl` declares the three standard strategies, and `seed new` / MCP `create_seed` (and `create_element` of a seed without `columns`) give a reference type's seed the code, label and description columns. Left: the first-open cost (reading and schema-validating 164 untrusted shards) | Shard loader (streamed, hashed, `kind` dispatch), MQ7201–MQ7211, `each locale` (unit key `locale:<tag>`), the six helpers, `l:` keys per chain locale and `s:localization`, shard renames with packages, entry moves on element move, entry removal on delete, `ModelStore` read and write of entries; the `l10n` and `seed` CLI verbs (§3.9) | Fallback chain table test; per-field stale detection; one French change re-renders only units that read French for that owner; a `fallbacks` change makes incremental equal full; determinism: two runs byte-identical; shard size bench at spec scale (≤ 2 MB largest) | 3–4 days |
| 5 | API, MCP, CSV, XLIFF | **Built**: the §3.9 operations, `?locale=` on the index, XLIFF and CSV, the MCP tools (docs/mcp.md), mocks | Handlers for §3.9, `?locale` on the index with the per-locale display-name table and its ETag, format constant `maquettiste-index/e6`, `translations` on `model.changed`, CSV and XLIFF readers and writers, MCP tools, mocks; editor: `api/schema.d.ts` regenerated, and the new kinds' entries in `app/icons.tsx` (`KIND_ICONS`) and `model/model.ts` (`KIND_LABELS`, `KIND_ORDER`) in the same change, so tsc passes | Contract suite; CSV round trip (export, import, no change); `@id` beside an attribute named `id`; byte order mark variant (`?bom=true`); XLIFF round trip; 409 on a changed shard | 3 days |
| 6 | Example packs and model | **Built**: both example packs and the reference application | `packs/sql-ddl` (three realizations, `strategyMap`, reconciliation of strategy objects, seeds), `packs/csharp-dapper` (records, static classes, resx), example model, goldens | Goldens per strategy and dialect; generated SQL applied to PostgreSQL and SQLite in the integration suite, twice (the second run is a no-op), and after adding and retiring a code | 3 days |
| 7 | Reference data screen | **Built**, and the type context menu (in the screen's list and, less Export CSV, the explorer's row menu). Left: the items the step 7 note lists as not yet built (type list filters, Used by highlighting, New reference type… in the type picker, and the rest). *(Close-out, 2026-09-29:)* built: Preview output on the Storage tab (the sql-ddl `seed` unit of a chosen database rendered through `previewTemplate`, narrowed to the statements for the type by `typeStatements`), the type row's hover card with the first codes (`firstCodes`), a seed picker beside Import CSV and Export CSV when a target has several seeds (`csvSeedFor`), a field removal that drops its seed columns in the same batch (`removedFieldIds`, `dropSeedColumns`, one undo step), and the Settings › Conventions hint "Declare the standard storage strategies" for a project that declares none (`StrategiesHint.tsx`, `withStandardStrategies`). Round 7 (2026-09-29): the Rows grid is `SeedGrid`, shared with the entity and relationship editors' **Seed data** tab (end columns pick a row of the far entity's seed), Import CSV and Export CSV sit in every seed grid's header, and Export all seed data / Import seed data… move every seed at once as `seed-data.zip` (import is one undo step but not one server transaction) | `workspaces/reference-data/*` (list, tabs, grid), `app/Rail.tsx`, type picker in `inspector/AttributeGrid.tsx`; the explorer's leaf row in `explorer/tree.ts` and labels in `model/labels.ts`, which explorer-redesign steps 4 to 6 create: this step depends on them, or, if they are not done, adds the leaf row to `explorer/filter.ts` and the labels to `model/model.ts` | Vitest: list search operators, grid keyboard map, TSV paste; Playwright: create UnitOfMeasure, add rows, use it on `contains`, CSV import preview, axe scan | 5–6 days |
| 8 | Editor localization | **Built**; round 6 added the new-locale field's normalization as you type (`normalizeLocaleTag`: `zh_cn` → `zh-CN`, `zh_hant_tw` → `zh-Hant-TW`) and the reason shown beside **Add locale**. Round 7 added the same normalization in the CLI `l10n` and `seed export --locale` arguments (`LocaleChains.Normalize`) and an MQ7201 message that names the hyphen form (errata E25). *(Close-out, 2026-09-29:)* header editing in a non-default content locale is built (`l10n/HeaderTextFields.tsx` over `l10n/header.ts`, in the editor header and the inspector: Display name, Plural name and Description write the content locale's entry, the default text as placeholder). Left: sub-element translations in the section, a Markdown editor for descriptions, the "Missing in <locale>" filter, XLIFF and CSV in the editor | Top bar switcher, inspector Translations section, Settings › Localization, completeness matrix, translation queue, grid locale columns, explorer chip, patching labels from `translations` events | Hidden with one locale (asserted); edit in fr writes the shard; queue walk by keyboard; axe | 3 days |
| 9 | Docs and SPEC | **Built** 2026-09-29: SPEC amendments for E6–E16 (marked as errata-driven), user guide, docs/mcp.md, skills. The reference-data part of the pack READMEs is done (they describe their strategies and seeds); their `parameterSchema` part is left under generation-ui.md step 8. Phase 2 close-out (2026-09-29): the user guide reviewed end to end against the final screens (seed import, the strategies hint, `create_seed` and the 39 MCP tools); nothing left | SPEC amendments for E6–E16, pack READMEs | Link and copy checks | 0.5 day |

**Step 7 as built.** `src/editor/src/workspaces/reference-data/` holds the screen: `listModel.ts` (category nesting with counts, A to Z, the four operators), `rowsModel.ts` (columns, seed edits, TSV parse and paste, the keyboard map), `csvPreview.ts`, `RowsGrid.tsx`, `FieldsTab.tsx`, `UsedByTab.tsx` (from `GET /api/reference-types/{id}/usage`), `StorageTab.tsx` (effective choice per database with its source, override from the declared strategies or Template-defined, options as text fields from the declaration). The field type picker is `inspector/TypePicker.tsx` over `inspector/typePicker.ts`, used by every attribute grid; `TYPE_KINDS` includes `reference-type`. The rail's Reference data icon opens the screen (workspace `reference-data`, also added to `PresenceReport.workspace`), and the explorer's reference type and seed rows select the type in it. *(After the demo:)* the type context menu is `TypeMenu.tsx` over `typeMenu.ts` (Duplicate, Rename with the seed named after the type, Move to category…, Set storage…, Export CSV, Convert to enum… when the type has no own fields and identifier codes, Delete… refused while a field uses the type, each one undoable change); the explorer's reference type rows offer the same items except Export CSV (`explorer/menus.ts`). Its gaps: Export CSV of several types at once, a Set storage dialog of its own (it opens the Storage tab), translations of the labels when converting to an enum, the attribute names in Delete's refusal, and the reverse conversion (enum to reference type). Not yet built: the type list's filters (category, tag, strategy, has errors, missing in a locale), `referenceData.groupBy` other than `category`, Used by highlighting in the explorer, Preview output on the Storage tab, F12 and Shift+F12 and **New reference type…** in the type picker, the hover card with the first codes, the TSV paste preview when a paste adds rows (the paste applies at once and can be undone), *(built in the close-out: Preview output on the Storage tab, the hover card with the first codes, the per-seed choice for Import and Export, and the column drop in seeds when a field is removed, §2.7.)*

**Step 8 as built.** `src/editor/src/l10n/` holds it: `model.ts` (pure: shown only with two or more declared locales, the content locale in effect, completeness totals and the locales × shards matrix, the queue's walk, the label patching from `translations`, and the Settings › Locales draft with the MQ7201 checks), `contentLocale.ts` (the per-browser choice and the locale in effect, which the index loader sends as `?locale=`), `queries.ts` (status, entries per owner and per shard, the write against the shard hashes, `LocalizationSync`), `LocaleSwitcher.tsx` (top bar switcher with completeness, and the explorer header chip), `TranslationsSection.tsx` (inspector and editor headers: collapsed, remembered, fallback as placeholder, stale marker and Confirm), `LocalesSettings.tsx` (Settings › Locales: default, supported, fallbacks, completeness kinds; the matrix with two or more locales) and `TranslationQueue.tsx`. The Rows grid adds one `@label:<locale>` column per translated locale, written through `PUT …/entries` (never into the seed). `realtime/sync.ts` patches index labels in the content locale from `translations`, keeps the localized name of a changed element until the refetch in that locale answers, and refetches the index on `translationsTruncated`. The mock reads `localization` from maquettiste.json live and places entries in per-domain shards with source fingerprints (so stale entries show). Settings › Locales itself is always present (it is where the second locale is declared); its matrix and queue appear only with two or more locales. Not yet built: editing the header's display name, plural or description in a non-default content locale writing the translation (with the locale badge); sub-element translations (attributes, ends, members) in the Translations section (the API serves them; the section lists the element's own fields); a Markdown editor for long descriptions and sidecar descriptions; the "Missing in <locale>" explorer filter and the Reference data type list filter; the XLIFF and CSV import and export in the editor (API and MCP only). *(2026-09-29: the Rows grid shows the content locale's label column only, with an "All locales" toggle, and its status bar shows that locale's label completeness; `rowsModel.shownLocales` and `labelCompleteness` are unit-tested.)* *(2026-09-29, after the 0.2.0 test: `description` is the grid's third built-in column (code | label | description | user fields), edited in a text area where Shift+Enter adds a line; new seeds list `["code", "label", "description"]` and a seed without the column gets it on the first description edit; each locale's description column sits beside its label column and writes the `description` entry of the row (a row with no default description is written against its label entry's shard hash); the Fields tab pins `description` (text, any length) and marks label and description as translated; the status bar hint appears while one locale is declared. The Fields tab's code type picker offers `uuid`, and the grid stores a typed UUID lowercase. The New reference type dialog's "Stored as" lists Template-defined and the declared strategies (the sql-ddl pack's `lookup-table`, `check` and `native` as Lookup table, Check constraint and Native type), preselects `check` when declared, and writes the choice under `storage["*"]` (nothing while the project declares no strategy); the attribute type picker's Reference data items show the effective storage beside the name, "Country · check" (`storageChoices.ts`, unit-tested). The mock lists a description entry for every reference row, as the engine does.)*

Total: about 26–29 working days. Performance targets: load with four complete locales at spec scale adds ≤ 400 ms to the cold load (streamed completeness pass); the Rows grid scrolls 10,000 rows at 60 fps; the type list filters 800 types in ≤ 16 ms.

**Measured (cold load with locales).** `write-model --locales 4` writes complete shards for four locales at the bench defaults (26,616 element files; 146,753 localizable nodes and 199,395 fields per locale, close to the §3.3 estimate of 150,000; 41 shards and about 26 MB per locale, twice the §3.3 estimate because each entry carries its `src` fingerprints), and `time-load` times a cold load against the same model without locales (bench/README.md). Without locales the load takes 410–540 ms; with four, the load takes 1,010–1,300 ms, building the localizable-node index 230–430 ms and the completeness pass 280–390 ms (it was 910–1,330 ms before the source fingerprints were cached per snapshot and the locales counted in parallel). Four locales therefore add about **1.2–1.4 s**, and the ≤ 400 ms target is **not met**. The remaining cost is reading and parsing about 100 MB of shards and indexing the nodes; next steps are a shard-hash-keyed cache of parsed shards and completeness counts in the index cache (so an unchanged shard is not reparsed), and building the node index from the loaded documents in parallel. **After the optimization pass (2026-09-29, same bench, 24 cores, WSL2):** the node index is built in parallel with every source fingerprint computed once in the same pass, the per-locale entry maps are filled one locale per thread, the completeness pass counts slices of the node list per locale in parallel with one entry lookup per node, and the streamed shard read builds plain maps instead of sorted immutable trees. Localizable nodes 200–250 → 95–146 ms, completeness 264–497 → 27–39 ms; `time-load --warm-cache` (new: a restart over a filled cache folder) totals 795–1,337 ms with four locales against 436–520 ms without, so four locales now add about **0.35–0.8 s** on a restart and about 0.5–1.2 s on a first open (empty cache; load 841–1,556 ms, most of it reading and schema-validating the 164 untrusted shards in the loader). The ≤ 400 ms target is **still not met**. Not built: a parsed-shard and completeness cache keyed by shard hash (the index cache already makes a restart read shards trusted and streamed, so the remaining cost is in the loader's read of about 100 MB of cached bytes and the garbage collector; the next step is a loader change, outside the localization code).

## 6. Decisions

| Id | Decision | Why |
| --- | --- | --- |
| RS1 | Reference type is an element kind, not a stereotyped entity | It has built-in code and label, rows and its own screen; a stereotype would reserve a name (P2) |
| RS2 | User fields may be scalars, enums and reference types only | One row is one flat line and one CSV record |
| RS3 | Reference types have no package | One place for all reference data; `referenceData.groupBy` groups them (category by default) |
| RS4 | Cells, defaults and `allowedValues` reference rows by code; row ids key translations | The code is what every strategy stores; the one exception to SPEC §11's id references besides stereotype keys (erratum E15) |
| RS5 | Strategies are project-declared keys; none means template-defined; the engine synthesizes nothing | P1 and P2 |
| RS6 | Seeds become a generic kind now, for entities, relations and reference types | Reference rows need them; the owner asked for any element |
| RS7 | `x-layout: "row-per-line"` for seed rows | Per-row diffs and merges |
| RS8 | Relation ends gain `displayName` and `pluralName` | Ends are the navigations people read |
| RS9 | Translations in per-locale, per-domain JSON shards; default texts stay in element files | §3.3 size estimate; no churn in element files |
| RS10 | Completeness reported per shard by default | 100,000 per-node rows would bury real problems |
| RS11 | Reference types appear only in the Reference data screen: one leaf row in the explorer, search across all kinds finds them | The owner's decision, to isolate them; the explorer's rule that you always know what you are browsing |
| RS12 | Seeds and translations are owned: a target's delete takes its seeds, a field's removal takes its seed column, a node's delete takes its translations | Otherwise a seeded element could never be deleted and orphans pile up in every locale |
| RS13 | Localizable = the standard fields of every domain-model element and sub-element, by rule, not by list | The owner asked for generic; new kinds need no design change |
| RS14 | The enum `lookup` storage option is retired in favour of reference types (MQ7012) | The owner's decision: the engine synthesizes no persistence (P1), and a lookup table is one strategy a template realizes for a reference type |

## 7. Questions for the owner

None open. The first draft's three questions are answered: enums stored as `lookup` first kept their lookup table; the owner later retired that option in favour of reference types (RS14, erratum E17, §1.4); only the standard fields are localized, as the owner said (§3.1); and "Enums" stays, from the owner's own words (explorer-redesign §7 item 5), so the type picker's section is **Enums** (§4.5).

## Appendix A. C# records (additions to engine-design §2)

```csharp
public enum ElementKind { /* …existing 15… */ ReferenceType, Seed }                   // "reference-type", "seed"
// TypeRef.Ref: → Enum|ValueObject|ScalarType|ReferenceType
public sealed record ReferenceType : Element {
    req ReferenceCode Code; req ReferenceLabel Label;
    IReadOnlyList<ModelAttribute> Attributes = [];                                   // x-sort "order"
    IReadOnlyDictionary<string, StorageChoice> Storage = {}; }                       // key: database id or "*"
public sealed record ReferenceCode { req string Id; string Type = "string"; int? Length; string? Pattern; string? DisplayName; Description? Description; }
public sealed record ReferenceLabel { req string Id; int? Length; string? DisplayName; Description? Description; }
public sealed record StorageChoice { string? Strategy; IReadOnlyDictionary<string, JsonElement> Options = {}; }   // Strategy null = template-defined
public sealed record Seed : Element {
    req string Target;                                                               // → Entity|Relation|ReferenceType; owning (deleted with it)
    req IReadOnlyList<string> Columns;                                               // "code"|"label"|"description" or → ModelAttribute|RelationEnd
    IReadOnlyList<SeedRow> Rows = []; }                                              // x-layout "row-per-line"
public sealed record SeedRow { req string Id; IReadOnlyList<JsonElement> Values = []; }   // JSON null cells; trailing nulls dropped (x-trim)
// ReferenceInfo gains: bool Owning;   (true for Seed.Target: never refuses the target's delete)
// RelationEnd gains: string? DisplayName; string? PluralName;   (Description stays a string)
// ProjectSettings gains: LocalizationSettings? Localization; ReferenceDataSettings ReferenceData = new();
// Conventions gains:     StorageChoice? ReferenceStorage;          // project default and per-database override
public sealed record LocalizationSettings { req string DefaultLocale; IReadOnlyList<string> Locales = [];
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fallbacks = {}; IReadOnlyList<string> Require = []; }
public sealed record ReferenceDataSettings { IReadOnlyDictionary<string, StrategyDeclaration> Strategies = {}; string GroupBy = "category"; }  // "category" | "tag:<prefix>" | "property:<name>"
public sealed record StrategyDeclaration { string? Description; CollectionSupport Collections; JsonElement? Options; IReadOnlyList<string> Required = []; }  // Options: JSON Schema "properties"
// CollectionSupport: a union (converter like MaxCardinality) of a bool and a map dialect|"*" → bool; For(dialect) evaluates it
public sealed record LocaleShard { [JsonPropertyName("$schema")] string? SchemaPath; req string Kind; req string Locale;   // Kind "locale-shard"; not an element
    req string Scope;                                                                // package id, "root" or "reference-data"
    IReadOnlyDictionary<string, TranslationEntry> Entries = {}; }
public sealed record TranslationEntry { string? DisplayName; string? PluralName; string? Label; Description? Description;
    IReadOnlyDictionary<string, string> Src = {}; }                                  // field → 8-hex hash of its default text
// ElementSummary gains (optional, omitted when null): string? Target; int? RowCount; int? FieldCount;
// BatchOp gains Translate; ModelBatchOperation gains string? Locale; string? Field; JsonElement? Value;
// PhysicalSnapshot column gains (reference columns): string? ReferenceType; string? Strategy;   type "reference" with the code facets
```

Resolved additions (engine-design §7): `ResolvedModel.ReferenceTypes`, `Seeds`, `SeedsInOrder`, `Locales`; `RType.Kind` `"reference"` and `RType.ReferenceType`; `RAttribute.Reference` (`RReferenceUsage`: `Type`, `IsCollection`, `Required`, `Allowed`, `DefaultRow`, `Storage`); `RColumn.ReferenceType` (with `RColumn.Type.Kind` `"reference"`); `REntityMapping.TemplateDefined` and `RRelationMapping.TemplateDefined`; `REntity.Seeds`, `RRelation.Seeds`; `RReferenceType`, `RReferenceField`, `RRow`, `RSeed`, `RSeedColumn`, `RSeedRow`, `RStorageChoice`, `RLocale` as in §1.5, §2.5 and §3.7. `PackUnit.For` gains `each reference type`, `each seed`, `each locale` (unit key `locale:<tag>`, no `where`). Dependency keys gain `s:localization` and `s:referenceData`.

## Appendix B. JSON schema changes

- **`reference-type.json`** (new): `x-order` `["$schema", "kind", "id", "name", "displayName", "pluralName", "description", "stereotypes", "tags", "category", "code", "label", "attributes", "storage", "properties", "generation", "source"]`; `kind` const `reference-type`; `code` `{ id, type (enum string|int16|int32|int64|uuid, default string), length, pattern, displayName, description }` and `label` `{ id, length, displayName, description }` required; `attributes` items `common.json#/$defs/attribute` with `x-sort: "order"`; `storage` a map (`propertyNames` the ULID pattern or `*`) of `$defs/storageChoice` `{ strategy: string, options: object }`, added to `common.json`; `attributes` item names exclude `code`, `label` and `description` in any case (MQ7010, enforced by the validator, not the schema).
- **`seed.json`** (new): `x-order` `["$schema", "kind", "id", "name", "displayName", "pluralName", "description", "stereotypes", "tags", "category", "target", "columns", "rows", "properties", "generation", "source"]` (`pluralName` because `Seed : Element` inherits it); `target` an id, required; `columns` an array of `id` or the enum `code|label|description`, `minItems: 1`, `uniqueItems: true`, required; `rows` items `{ id, values: array }` with `x-order ["id", "values"]`, the `rows` array carrying `"x-layout": "row-per-line"` and `values` carrying `"x-trim": "trailing-nulls"`.
- **`locale.json`** (new): `x-order ["$schema", "kind", "locale", "scope", "entries"]`; `kind` const `locale-shard`; `locale` a BCP 47 pattern; `scope` the ULID pattern or `root|reference-data`; `entries` `propertyNames` the ULID pattern, values `{ displayName, pluralName, label, description (common.json#/$defs/description), src }` with `src` an object whose keys are those four field names (`x-order` in that order) and values `^[0-9a-f]{8}$`, `additionalProperties: false`, `minProperties: 1`.
- **`common.json`**: `typeRef` description names reference types; `storageChoice` added. **`relation.json`**: end `x-order` becomes `["id", "entity", "role", "navigation", "displayName", "pluralName", "min", "max", "onDelete", "ordered", "description"]`.
- **`maquettiste.json`**: `x-order` gains `localization` and `referenceData` after `inflection`; `conventions` (and each `databases` entry) gains `referenceStorage`; `localization` `{ defaultLocale (required), locales, fallbacks, require }`; `referenceData` `{ strategies, groupBy }`, `strategies` a map of `{ description, collections (boolean, or a map whose keys are the dialect enum or `*` to booleans), options, required }`, `groupBy` a string matching `^(category|tag:.+|property:[A-Za-z_][A-Za-z0-9_]*)$`, default `category`.
- **`stereotype.json`** and **`extension.json`**: the `appliesTo` kind enums gain `reference-type` and `seed`, so stereotypes and extension schemas apply to them (§1.1).
- **`snapshot.json`**: a column's `type` admits `reference`, with `referenceType` and `strategy` (§1.4).
- **`openapi.yaml`**: `ElementKind` gains `reference-type` and `seed`; `ModelDocument` and `NewModelDocument` gain the two schemas in their `oneOf`; `ElementSummary` gains `target`, `rowCount` and `fieldCount`; `ModelChangedEvent` gains `translations` and `translationsTruncated`; the §3.9 operations.
- **`batch.json`**: `op` enum gains `translate`, requiring `locale`, `id`, `field` (`displayName|pluralName|label|description`) and `value` (string, `{ file }` or null to remove); `x-order` `["op", "id", "expectedHash", "element", "locale", "field", "value"]`.
- **`pack.json`**: `for` pattern admits `each reference type`, `each seed`, `each locale`.
- **Canonical writer and `ICanonicalJson.IsCanonical`**: support `x-layout: "row-per-line"` with the exact row form of §2.1, `x-trim: "trailing-nulls"`, and number normalization in seed cells; free-form map list gains `entries`, `storage`, `strategies`, `fallbacks`. The loader dispatches `kind: "locale-shard"` to `LocaleShard` (not an element) and maps `model/reference-types/`, `model/seeds/**` and `model/locales/**` as conventional folders.
