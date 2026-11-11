# The resolved model

<!-- Generated from the engine's record types by the engine tests; do not edit. -->

**Contract 0.24.0.** This page describes what Maquettiste 0.24.0 answers when you read the resolved model: the model as
code generation sees it, with names, types, keys, tables and processes worked out. Until version 1 the contract is the
Maquettiste release and is not frozen: any release may change it, and this page changes with it.

## Where to read it

| Where | How |
| --- | --- |
| Command line | `maquettiste model export --resolved` (`--scope`, `--database`, `--format json` or `ndjson`, `--out`) |
| HTTP API | `GET /api/model/resolved` (`scope`, `database`, `cursor`, `limit`) |
| MCP | `get_resolved_model` (`scope`, `database`, `cursor`, `limit`) |

Every answer states the version in `contract` and names its JSON Schema in `schema` (`resolved-model.json`). To get the schema:

- In your project: `.maquettiste/.schema/v1/resolved-model.json`, with this page beside it as `resolved-model.md`. The editor and
  `maquettiste mcp` refresh both when they start, and so does `maquettiste init`. `validate` and `generate` warn (MQ1008) while
  they belong to another release. Both files hold the version, so they are rewritten at every release, even one that
  changes nothing else: keep one Maquettiste version per repository (the local tool manifest pins it), or the copies
  change back and forth between the people and machines that run different releases.
- Over HTTP: `GET /api/model/resolved/schema`, or `?scope=entities` for one scope's records.
- Over MCP: `get_schema` with kind `resolved-model`, or `resolved-model:entities` for one scope's records.

A reader you write by hand should ignore fields it does not know, since a later release may add some. A check against the
schema is exact for one release: an extra field, or an answer from another release, fails it. So validate an answer with the
schema of the release its `contract` names.

## Answers

A page (API and MCP):

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `contract` | string | never | The contract version the page follows: the version inside `resolved-model.json` of the same release. Until version 1 it is the Maquettiste release, and the contract may change in any release. Always `0.24.0`. |
| `schema` | string | never | The file name of the JSON Schema that describes the page: `resolved-model.json`. A project holds it in `.maquettiste/.schema/v1/`; `GET /api/model/resolved/schema` and the MCP tool `get_schema` (`resolved-model`) return it. Always `resolved-model.json`. |
| `items` | list of record (by `kind`) | never | The records of the page, ordered by kind, then name, then id (ordinal); migration facts by database name, then applied order. Each record has `id`, `kind` and `name`, and `kind` says which record it is. |
| `next` | string | can be null | The cursor of the next page, or null on the last one. |
| `diagnostics` | list of [diagnostic](#diagnostic) | never | When the model has errors, the errors that prevented resolution (and `items` is empty); otherwise the failures of the calculated values the page reads (MQ5010, MQ5011, MQ5013), and nothing else: validate reports the warnings. |
| `calculated` | map of map of any | never | The calculated values of the objects on the page and the sub-objects their records list, by site (the object's id, or `<holder id>/<attribute id>` for an attribute its holder does not own), then by name, ordinal. A value that fails is left out and its diagnostic is in `resolvedPage.diagnostics`; an object with no value is left out. |

Pass `next` back as `cursor` until it is null. The export command reads every page for you and writes one file:

- `--format json` (the default): one object, `{ "contract", "schema", "items" }`, with every record of the scope in `items`.
- `--format ndjson`: a first line `{"contract": ..., "schema": ...}`, then one record per line.

## Scopes

`scope` picks the records. `all` (the default) is every scope but `tables`, `routines`, `database-types`, `sql-objects` and
`queries`, which `databases` already holds, plus the records only `all` lists. `database` narrows entities and relations to the ones
mapped in that database, and databases, tables and migration facts to it.

| Scope | Record |
| --- | --- |
| `packages` | [packageRecord](#packagerecord) |
| `entities` | [entityRecord](#entityrecord) |
| `relations` | [relationRecord](#relationrecord) |
| `enums` | [enumRecord](#enumrecord) |
| `value-objects` | [valueObjectRecord](#valueobjectrecord) |
| `scalar-types` | [scalarTypeRecord](#scalartyperecord) |
| `reference-types` | [referenceTypeRecord](#referencetyperecord) |
| `seeds` | [seedRecord](#seedrecord) |
| `processes` | [processRecord](#processrecord) |
| `actors` | [actorRecord](#actorrecord) |
| `scenarios` | [scenarioRecord](#scenariorecord) |
| `operations` | [operationRecord](#operationrecord) |
| `custom-elements` | [customRecord](#customrecord) |
| `constraints` | [constraintRecord](#constraintrecord) |
| `databases` | [databaseRecord](#databaserecord) |
| `tables` | [tableRecord](#tablerecord) |
| `routines` | [routineRecord](#routinerecord) |
| `database-types` | [databaseTypeRecord](#databasetyperecord) |
| `sql-objects` | [sqlObjectRecord](#sqlobjectrecord) |
| `queries` | [queryRecord](#queryrecord) |
| `migrations` | [migrationRecord](#migrationrecord) |
| `all` only | [verbRecord](#verbrecord) |
| `all` only | [rightRecord](#rightrecord) |
| `all` only | [namedTriggerPointRecord](#namedtriggerpointrecord) |

The answer's own records come first, then the records they hold, each listed once where it first appears.

## Scope packages

### packageRecord

A resolved package.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The package id. |
| `kind` | string | never | `package`. Always `package`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The parent package's id, or null at the root. |
| `qualifiedName` | string | never | The names from the root, joined with dots. |
| `children` | list of string | never | The child packages' ids. |
| `entities` | list of string | never | The entities' ids. |
| `valueObjects` | list of string | never | The value objects' ids. |
| `enums` | list of string | never | The enums' ids. |
| `relations` | list of string | never | The relations' ids. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### generationHints

Per-pack generation hints on an element (`generation.<pack>`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `skip` | boolean | never | When true, the pack produces no unit for this element. |
| `rename` | string | can be null | A name the pack should use instead of the element's name. Null when not set. |
| `variables` | map of any | never | Extra template variables, visible to templates as `hints.variables`. |

## Scope entities

### entityRecord

A resolved entity: its attributes resolved (inherited ones included and marked), keys, inheritance, relations and mappings.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The entity id (a promoted relation's entity has the relation's id). |
| `kind` | string | never | `entity`. Always `entity`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `isAbstract` | boolean | never | Whether the entity is abstract. |
| `base` | string | can be null | The base entity's id. Null when not set. |
| `derived` | list of string | never | The ids of the entities that derive from it directly. |
| `attributes` | list of [attributeRecord](#attributerecord) | never | Every attribute, the base's first (marked `isInherited`), then the stereotypes', then its own. |
| `key` | [keyRecord](#keyrecord) | can be null | The primary key, or null. |
| `alternateKeys` | list of [alternateKeyRecord](#alternatekeyrecord) | never | The alternate keys. |
| `navigations` | list of [navigationRecord](#navigationrecord) | never | The navigations to related entities. |
| `relations` | list of string | never | The ids of the relations with an end at this entity. |
| `mappings` | list of [entityMappingRecord](#entitymappingrecord) | never | One mapping per database the entity is mapped to (not ignored in), by database name. |
| `isPromoted` | boolean | never | Whether the entity is a promoted relation. |
| `promotedFrom` | string | can be null | The promoted relation's id. Null when not set. |
| `seeds` | list of string | never | The ids of the seeds that hold its rows. |
| `lifecycle` | string | can be null | The id of the process that is its lifecycle. Null when not set. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `bindings` | list of [entityBindingRecord](#entitybindingrecord) | never | The entity's bindings, one per database it is bound to, by database name. |
| `checks` | list of [entityCheckRecord](#entitycheckrecord) | left out when not set | The entity checks, the root base's first; null, and left out of the JSON, for an entity without any. |
| `lifecycles` | list of string | left out when not set | The ids of the lifecycles that govern the entity or a base of it, by name then id; null, and left out of the JSON, unless one of them lists its subjects in `subjects`. |
| `revisions` | [revisionsRecord](#revisionsrecord) | left out when not set | The entity's revisions merged through its bases; null, and left out of the JSON, when neither the entity nor a base has a revisions block. |

### attributeRecord

A resolved attribute of an entity, value object, relation, process context or event payload.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The attribute id. |
| `name` | string | never | The name. |
| `owner` | string | never | The id of the element that holds it (for an inherited attribute, the entity that inherits it). |
| `declaringEntity` | string | can be null | The id of the entity that declares it, for an entity attribute. Null otherwise. |
| `type` | [typeRecord](#typerecord) | never | The resolved type. |
| `required` | boolean | never | Whether a value is required. |
| `default` | any | can be null | The default value, as a plain value (an enum member by name). Null when not set. |
| `defaultExpression` | string | can be null | The default expression. Null when not set. |
| `length` | integer | can be null | The effective length. Null when not set. |
| `precision` | integer | can be null | The effective precision. Null when not set. |
| `scale` | integer | can be null | The effective scale. Null when not set. |
| `collection` | boolean | never | Whether the attribute holds a list. |
| `unique` | boolean | never | Whether values are unique. |
| `indexed` | boolean | never | Whether the attribute is indexed. |
| `readOnly` | boolean | never | Whether the attribute is read-only. |
| `immutable` | boolean | never | Whether the attribute cannot change after creation. |
| `derived` | [derivedRecord](#derivedrecord) | can be null | The derivation, or null. |
| `sensitive` | string | can be null | The sensitivity class, or null. |
| `validation` | [validationRecord](#validationrecord) | can be null | The validation, or null. |
| `order` | integer | never | The 0-based position in its owner's list. |
| `isKey` | boolean | never | Whether the attribute is part of its entity's primary key. |
| `isInherited` | boolean | never | Whether the attribute comes from a base entity. |
| `isVirtual` | boolean | never | Whether the attribute is virtual (not stored). |
| `fromStereotype` | string | can be null | The key of the stereotype that adds the attribute, or null. |
| `reference` | [referenceUsageRecord](#referenceusagerecord) | can be null | For an attribute typed by a reference type: how it uses it. Null otherwise. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `descriptionColumn` | string | can be null | The key of the column whose comment `description` shows, when the attribute has no description of its own; otherwise null. |

### typeRecord

An attribute's resolved type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `kind` | string | never | `builtin`, `enum`, `value-object`, `scalar` or `reference`. |
| `name` | string | never | The type's name as written. |
| `builtin` | string | can be null | The built-in type keyword at the bottom (a scalar type's base, for example). Null when not set. |
| `enum` | string | can be null | The enum's id. Null when not set. |
| `valueObject` | string | can be null | The value object's id. Null when not set. |
| `scalar` | string | can be null | The scalar type's id. Null when not set. |
| `referenceType` | string | can be null | The reference type's id. Null when not set. |

### derivedRecord

A derived attribute's expression.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `expression` | string | never | The raw expression; empty when the value is written in the expression language. |
| `stored` | boolean | never | Whether the value is stored. |
| `value` | [expressionRecord](#expressionrecord) | left out when not set | The value written in the expression language; null, and left out of the JSON, for a raw expression. |

### expressionRecord

An expression: its text printed from current names, its type, and the tree of ids the file holds.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `text` | string | never | The text form, printed from current names, so a rename changes it and never the file. |
| `type` | string | never | The type as messages write it: `bool`, `int32`, `decimal`, `date`, `enum <id>`, `code <id>`… |
| `nullable` | boolean | never | Whether it may be null. |
| `tree` | any | never | The tree as the file holds it: paths, enum members, codes and constraints by id. |

### validationRecord

Validation of an attribute or scalar type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `min` | any | can be null | The minimum. Null when not set. |
| `max` | any | can be null | The maximum. Null when not set. |
| `pattern` | string | can be null | The pattern. Null when not set. |
| `allowedValues` | list of any | never | The allowed values. |
| `rules` | list of string | never | The named rules. |

### referenceUsageRecord

How an attribute uses its reference type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `type` | string | never | The reference type's id. |
| `isCollection` | boolean | never | Whether the attribute holds many codes. |
| `required` | boolean | never | Whether a code is required. |
| `allowed` | list of string | never | The ids of the rows the attribute allows (all when the list is empty). |
| `defaultRow` | string | can be null | The id of the default row. Null when not set. |
| `storage` | list of [storageChoiceRecord](#storagechoicerecord) | never | The effective storage choice per database. |

### storageChoiceRecord

The effective storage of a reference type in one database.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `database` | string | never | The database's id. |
| `strategy` | string | can be null | The project's strategy key, or null for template-defined. |
| `options` | map of any | never | The choice's options. |
| `source` | string | can be null | Where the choice came from: `type`, `database`, `project`, or null. |
| `description` | string | can be null | The strategy's description. Null when not set. |
| `collections` | boolean | never | Whether the strategy supports collections in the database's dialect. |

### keyRecord

An entity's primary key.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `attributes` | list of string | never | The key attributes' ids, in key order. |
| `strategy` | string | never | `application`, `identity`, `sequence`,... |
| `sequences` | list of [keySequenceRecord](#keysequencerecord) | never | For the `sequence` strategy, the key sequence per database. |

### keySequenceRecord

The key sequence of an entity in one database.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `database` | string | never | The database's id. |
| `sequence` | string | never | The sequence's id or synthesized key, as `sequenceView.id`. |

### alternateKeyRecord

An alternate key.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The key id. |
| `name` | string | never | The name. |
| `attributes` | list of string | never | The attributes' ids, in key order. |

### navigationRecord

A navigation from an entity across a relation.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The navigation id (the end it starts from). |
| `name` | string | never | The navigation name. |
| `relation` | string | never | The relation's id. |
| `from` | string | never | The id of the end at this entity. |
| `to` | string | never | The id of the end it leads to. |
| `target` | string | can be null | The id of the entity it leads to; null when it leads to an end with several types. |
| `isCollection` | boolean | never | Whether it leads to many. |
| `targets` | list of string | left out when not set | The types of the end it leads to, as entity ids, when that end has several types; left out of the JSON otherwise. |

### entityMappingRecord

An entity's mapping to one database.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `database` | string | never | The database's id. |
| `table` | string | never | The table's key, as `tableView.key`. |
| `inheritance` | string | can be null | The inheritance strategy, or null. |
| `discriminatorColumn` | string | can be null | The discriminator column's key. Null when not set. |
| `discriminatorValue` | any | can be null | The discriminator value. Null when not set. |
| `columns` | list of [columnMappingRecord](#columnmappingrecord) | never | Which column stores each attribute path. |

### columnMappingRecord

The column that stores an attribute path.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `attributePath` | string | never | The attribute path (a value object's members are dotted). |
| `column` | string | never | The column's key, as `columnView.key`. |

### entityBindingRecord

A resolved entity binding, as the resolved model's entity records carry it.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The binding's id. |
| `database` | string | never | The database's id. |
| `sourceKind` | string | can be null | `table`, `view` or `query`; null when the source does not resolve. |
| `source` | string | never | The source's key (a table key, a view or query id), as the binding names it when it does not resolve. |
| `sourceName` | string | never | The source's name. |
| `writeTable` | string | can be null | The key of the table the binding writes, or null when it does not write. |
| `delete` | string | never | `key`, `soft` or `none`. |
| `softDeleteColumn` | string | can be null | The column a soft delete sets, or null. |
| `softDeleteValue` | any | can be null | The value a soft delete sets. Null when not set. |
| `constants` | list of [bindingConstantView](#bindingconstantview) | never | The constants. |
| `fields` | list of [bindingFieldRecord](#bindingfieldrecord) | never | The field map. |
| `columns` | list of [bindingColumnRecord](#bindingcolumnrecord) | never | Every source column with what accounts for it. |
| `writeColumns` | list of [bindingColumnRecord](#bindingcolumnrecord) | never | Every write table column with what accounts for it, when the write table is not the source. |
| `discriminatorValue` | any | left out when not set | The entity's type code the binding sets, a string or an integer; left out when unset. |

### bindingConstantView

A constant column of a binding.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `column` | string | never | The column's physical name. |
| `value` | any | can be null | The value (a string, a number or a bool), or null for NULL. |

### bindingFieldRecord

One field of a resolved binding.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The field's name (the select's alias and the statements' parameter). |
| `attribute` | string | never | The attribute, `attributeId.memberId` or relation end id the field maps, as the file writes it. |
| `attributeId` | string | can be null | The attribute's id (a value object member's owner), or null. |
| `memberId` | string | can be null | The value object member's id, or null. |
| `endId` | string | can be null | The relation end's id, for a to-one navigation's key. Null otherwise. |
| `column` | string | never | The source column's physical name. |
| `columnKey` | string | can be null | The source column's key, for a table source. Null otherwise. |
| `writeColumn` | string | can be null | The write table column's key, or null when the field is not written. |
| `type` | string | can be null | The column's built-in type keyword, or null. |
| `nullable` | boolean | never | Whether the column is nullable. |
| `isKey` | boolean | never | Whether the field maps a key attribute. |
| `isGenerated` | boolean | never | Whether the database fills the column. |
| `inInsert` | boolean | never | Whether the insert writes the field. |
| `inUpdate` | boolean | never | Whether the update sets the field. |
| `holds` | string | left out when not set | On a relation end with several types: `id`, `type` or `key`, what the field's column holds; left out on today's fields. |
| `keyOf` | string | left out when not set | The entity of an `endId.entityId` field (`holds` is `key`); left out otherwise. |

### bindingColumnRecord

A column of a binding's source or write table and what accounts for it.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The column's physical name. |
| `key` | string | can be null | The column's key, for a table. Null otherwise. |
| `status` | string | never | `field`, `constant`, `ignored`, `database`, `computed`, `identity`, `default`, `soft-delete` or `unaccounted`. |
| `field` | string | can be null | The name of the field that maps it, with status `field`. Null otherwise. |

### entityCheckRecord

An entity check: a condition every record of the entity meets, unknown passing.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The check's id. |
| `name` | string | never | The name. |
| `attribute` | string | can be null | The id of the attribute a failure points at, or null. |
| `condition` | [expressionRecord](#expressionrecord) | can be null | The condition, or null when it does not type (validation says why). |
| `message` | string | can be null | The text a failure reports, or null. |
| `isInherited` | boolean | never | Whether the check comes from a base entity. |
| `bindings` | list of string | never | The ids of the bindings that list it, by database name: each enforces it on its write table. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### revisionsRecord

An entity's revisions, merged through its bases; other elements by id.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `levels` | list of [revisionLevelRecord](#revisionlevelrecord) | never | The levels, from the most significant. |
| `separator` | string | never | Written between two levels that both hold a value; `.` when neither the entity nor a base writes one. |
| `firstRevision` | string | never | The sequence's start, which a new object takes; empty when level 1 starts empty (no revision). |
| `boundAttribute` | string | can be null | The id of the attribute that holds the current revision; null when the team's system holds it. |
| `lifecycles` | list of [revisionLifecycleRecord](#revisionlifecyclerecord) | never | Every lifecycle that governs the entity, with what a revise does to it; the fallback filled in. |
| `attributes` | list of [revisionAttributeRecord](#revisionattributerecord) | never | Each attribute a revise doesn't copy. |
| `links` | list of [revisionLinkRecord](#revisionlinkrecord) | never | Each relation end the entity reaches with a rule for its links. |

### revisionLevelRecord

One level of an entity's revision sequence.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `number` | integer | never | The level's number, 1 to 3. |
| `name` | string | can be null | The level's name, or null. |
| `format` | string | never | `letters`, `numbers` or `list`. |
| `values` | list of string | never | The list's values; empty for letters and numbers. |
| `first` | string | never | The first value as text. |
| `skip` | list of string | never | The skipped letters. |
| `width` | integer | can be null | The width numbers are padded to, or null. |
| `startEmpty` | boolean | never | Whether the level holds nothing at first. |
| `examples` | list of string | never | The level's first five values on their own. |

### revisionLifecycleRecord

What a revise does to a lifecycle that governs the entity.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `lifecycle` | string | never | The lifecycle's id. |
| `onRevise` | string | never | `restart`, `keep` or `state`. |
| `state` | string | can be null | With `state`, the id of the state entered; else null. |
| `from` | string | can be null | The id of the entity whose entry says it; null for the fallback. |

### revisionAttributeRecord

What a revise does to an attribute it doesn't copy.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `attribute` | string | never | The attribute's id. |
| `onRevise` | string | never | `reset` or `clear`. |
| `from` | string | never | The id of the entity whose entry says it. |

### revisionLinkRecord

What a revise does to the links through a relation end the entity reaches.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `end` | string | never | The relation end's id. |
| `onRevise` | string | never | `stay`, `move` or `duplicate`. |
| `from` | string | never | The id of the entity whose entry says it. |

## Scope relations

### relationRecord

A resolved relation.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The relation id. |
| `kind` | string | never | `relation`. Always `relation`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `inverseName` | string | can be null | The inverse name. Null when not set. |
| `relationKind` | string | never | `association`, `aggregation` or `composition`. |
| `cardinality` | string | never | `one-to-one`, `one-to-many`, `many-to-many` or `n-ary`. |
| `allowDuplicates` | boolean | never | Whether the same pair may occur more than once. |
| `ends` | list of [endRecord](#endrecord) | never | The ends, in document order. |
| `attributes` | list of [attributeRecord](#attributerecord) | never | The relation's own attributes. |
| `mappings` | list of [relationMappingRecord](#relationmappingrecord) | never | One mapping per database the relation is mapped to. |
| `seeds` | list of string | never | The ids of the seeds that hold its rows. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### endRecord

A relation end. The members after `opposite` describe an end with several types and are left out of the JSON on a one-type end.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The end id. |
| `entity` | string | can be null | The entity's id; null on an end with several types. |
| `role` | string | never | The role name. |
| `navigation` | string | can be null | The navigation name, or null. |
| `min` | integer | never | The lower bound. |
| `max` | string | never | The upper bound, a number or `*`. |
| `isMany` | boolean | never | Whether the upper bound is above one. |
| `onDelete` | string | never | What deleting the other side does. |
| `ordered` | boolean | never | Whether the end is ordered. |
| `opposite` | string | can be null | The opposite end's id, for a binary relation. Null otherwise. |
| `types` | list of string | left out when not set | The end's types as entity ids, on an end with several types. Left out otherwise. |
| `hasSeveralTypes` | boolean | left out when not set | true on an end with several types. Left out otherwise. |
| `stereotypes` | list of string | left out when not set | The ids of the stereotypes the end's entries name, in entry order, when it names some. Left out otherwise. |
| `anyEntity` | boolean | left out when not set | true when the end may be any entity. Left out otherwise. |
| `commonBase` | string | left out when not set | The id of the nearest entity every type is or derives from, on an end with several types whose types share one. Left out otherwise. |

### relationMappingRecord

A relation's mapping to one database.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `database` | string | never | The database's id. |
| `shape` | string | never | `foreign-key`, `junction` or `promoted`. |
| `foreignKey` | string | can be null | The foreign key's name, for the `foreign-key` shape. Null otherwise. |
| `junctionTable` | string | can be null | The junction table's key, for the `junction` shape. Null otherwise. |
| `promotedEntity` | string | can be null | The promoted entity's id, for the `promoted` shape. Null otherwise. |
| `links` | list of [relationLinkRecord](#relationlinkrecord) | left out when not set | For a relation with an end of several types: one entry per pair of targets; for a one-type relation whose designed link table's mapping writes constants or attribute columns, its one link; left out when empty. |
| `constants` | list of [relationConstantRecord](#relationconstantrecord) | left out when not set | The link table's columns with a fixed value; left out when empty. |
| `attributes` | list of [relationAttributeColumnRecord](#relationattributecolumnrecord) | left out when not set | The relation attributes' columns in the table that holds every link; left out when empty. |
| `leftOut` | list of [relationEndEntityRecord](#relationendentityrecord) | left out when not set | Covered entities not placed in the database; left out when empty. |
| `notStored` | list of [relationEndEntityRecord](#relationendentityrecord) | left out when not set | Placed targets no pointer reaches (MQ4120); left out when empty. |
| `typeCodes` | list of [typeCodeRecord](#typecoderecord) | left out when not set | The code of every entity a type column holds; left out when empty. |

### relationLinkRecord

Where one pair of targets of a relation mapping is linked.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `table` | string | never | The key of the table that holds the link. |
| `ends` | list of [relationLinkEndRecord](#relationlinkendrecord) | never | One per relation end, in file order. |

### relationLinkEndRecord

How the table that holds a link points at one end's target.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `end` | string | never | The end's id. |
| `type` | string | never | The target entity's id at this end. |
| `isHolder` | boolean | never | Whether this end's rows hold the link. |
| `columns` | list of string | never | The keys of the pointer's columns in the holding table. |
| `referencedTable` | string | can be null | The key of the table the pointer refers to. Null when not set. |
| `referencedColumns` | list of string | never | The keys of the columns the pointer refers to. |
| `foreignKey` | string | can be null | The foreign key's name, when the pointer is a key. Null otherwise. |
| `typeColumn` | string | can be null | The key of the column that holds the type code, when there is one. Null otherwise. |
| `codes` | list of any | can be null | The codes the type column holds for this target, when there is a type column (strings or integers). Null otherwise. |

### relationConstantRecord

A link table's column with a fixed value.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `column` | string | never | The column's key. |
| `values` | list of any | never | The values the column holds; null is SQL NULL. |

### relationAttributeColumnRecord

A relation attribute's column in the table that holds every link.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `attribute` | string | never | The attribute's id. |
| `column` | string | never | The column's key. |

### relationEndEntityRecord

A covered entity at one end of a relation mapping.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `end` | string | never | The end's id. |
| `entity` | string | never | The entity's id. |

### typeCodeRecord

An entity's type code in a database.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `entity` | string | never | The entity's id. |
| `code` | any | never | The code, a string or an integer. |

## Scope enums

### enumRecord

A resolved enum.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The enum id. |
| `kind` | string | never | `enum`. Always `enum`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `flags` | boolean | never | Whether members combine as flags. |
| `members` | list of [enumMemberRecord](#enummemberrecord) | never | The members, in order. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### enumMemberRecord

An enum member.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The member id. |
| `name` | string | never | The name. |
| `displayName` | string | never | The display name (the name when none is set). |
| `description` | string | can be null | The description. Null when not set. |
| `value` | integer | can be null | The numeric value. Null when not set. |
| `code` | string | can be null | The code. Null when not set. |
| `properties` | map of any | never | The custom properties. |

## Scope value-objects

### valueObjectRecord

A resolved value object.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The value object id. |
| `kind` | string | never | `value-object`. Always `value-object`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `attributes` | list of [attributeRecord](#attributerecord) | never | The attributes. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

## Scope scalar-types

### scalarTypeRecord

A resolved custom scalar type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The scalar type id. |
| `kind` | string | never | `scalar-type`. Always `scalar-type`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `base` | string | never | The built-in base type keyword. |
| `length` | integer | can be null | The length. Null when not set. |
| `precision` | integer | can be null | The precision. Null when not set. |
| `scale` | integer | can be null | The scale. Null when not set. |
| `validation` | [validationRecord](#validationrecord) | can be null | The validation, or null. |
| `nativeTypes` | map of string | never | The native type per dialect name, as written (sorted by dialect name; empty when none). |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

## Scope reference-types

### referenceTypeRecord

A resolved reference type with its rows (every seed's rows merged).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The reference type id. |
| `kind` | string | never | `reference-type`. Always `reference-type`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id, or null. |
| `code` | [referenceFieldRecord](#referencefieldrecord) | never | The code field. |
| `label` | [referenceFieldRecord](#referencefieldrecord) | never | The label field. |
| `attributes` | list of [attributeRecord](#attributerecord) | never | The user fields. |
| `rows` | list of [rowRecord](#rowrecord) | never | The rows, in order. |
| `seeds` | list of string | never | The ids of its seeds. |
| `usedBy` | list of string | never | The ids of the attributes typed by it. |
| `storage` | list of [storageChoiceRecord](#storagechoicerecord) | never | The effective storage choice per database. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `formerCodes` | list of [formerCodeRecord](#formercoderecord) | never | The codes its rows used to have, ordinal by code, each with the row that replaced it. |

### referenceFieldRecord

The code or label field of a reference type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The field id. |
| `type` | string | never | The type keyword. |
| `length` | integer | can be null | The length. Null when not set. |
| `pattern` | string | can be null | The pattern. Null when not set. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |

### rowRecord

A row of a reference type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The row id. |
| `code` | any | can be null | The code. Null when not set. |
| `label` | string | can be null | The label. Null when not set. |
| `description` | string | can be null | The description. Null when not set. |
| `values` | map of any | never | The user fields by name (a reference-typed field holds its code or codes). |
| `seed` | string | never | The id of the seed that holds it. |
| `order` | integer | never | The 0-based position among the type's rows. |

### formerCodeRecord

A code a row of a reference type used to have.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `code` | any | can be null | The former code: a string, or an integer for an integer code type. Null otherwise. |
| `replacedBy` | string | never | The id of the row that replaced it. |

## Scope seeds

### seedRecord

A resolved seed.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The seed id. |
| `kind` | string | never | `seed`. Always `seed`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id, or null. |
| `target` | string | can be null | The id of the entity, relation or reference type it holds rows for. Null when not set. |
| `columns` | list of [seedColumnRecord](#seedcolumnrecord) | never | The columns. |
| `rows` | list of [seedRowRecord](#seedrowrecord) | never | The rows, in order. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### seedColumnRecord

A seed column.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The column name. |
| `kind` | string | never | `builtin`, `attribute` or `end`. |
| `attribute` | string | can be null | The attribute's id, for an attribute column. Null otherwise. |
| `end` | string | can be null | The relation end's id, for an end column. Null otherwise. |

### seedRowRecord

A seed row.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The row id. |
| `order` | integer | never | The 0-based position. |
| `values` | map of any | never | The cells by column name (an enum cell by member name, a reference cell as its code, an end cell as a row id). |

## Scope processes

### processRecord

A resolved process: every state at every depth, transitions, events, guards, actions, gates and invokes, other objects by id.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The process id. |
| `kind` | string | never | `process`. Always `process`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `use` | string | never | `lifecycle` or `orchestration`. |
| `subject` | string | can be null | The id of the entity whose lifecycle it is. Null when not set. |
| `boundAttribute` | string | can be null | The id of the subject's attribute that holds the state. Null when not set. |
| `boundEnum` | string | can be null | The id of the enum whose members are the root-level states. Null when not set. |
| `initial` | string | can be null | The initial state's id. Null when not set. |
| `context` | list of [attributeRecord](#attributerecord) | never | The context attributes. |
| `states` | list of [stateRecord](#staterecord) | never | Every state at every depth, in document order; `parent` and `children` give the tree. |
| `transitions` | list of [transitionRecord](#transitionrecord) | never | Every transition. |
| `events` | list of [eventRecord](#eventrecord) | never | The events. |
| `guards` | list of [guardRecord](#guardrecord) | never | The guards. |
| `actions` | list of [actionRecord](#actionrecord) | never | The actions. |
| `gates` | list of [gateRecord](#gaterecord) | never | The approval gates. |
| `invokes` | list of [invokeRecord](#invokerecord) | never | The invocations. |
| `actors` | list of string | never | The ids of the actors that take part. |
| `scenarios` | list of string | never | The ids of its scenarios. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `subjects` | list of [subjectRecord](#subjectrecord) | left out when not set | The entries of `subjects`, a stereotype entry expanded at its place into one subject per carrier it governs; null, and left out of the JSON, for a process in the one-subject form or with no subject. |
| `codesMap` | boolean | left out when not set | Whether the states' codes form a map; null, and left out of the JSON, when no state has a code. |
| `stereotypeSubjects` | list of [stereotypeSubjectRecord](#stereotypesubjectrecord) | left out when not set | The stereotype entries of `subjects` as written; null, and left out of the JSON, when the process has none. |
| `rightsDefault` | string | left out when not set | A lifecycle's `rightsDefault` as its file writes it; null, and left out of the JSON, when it writes none. |
| `rights` | list of [processRightRecord](#processrightrecord) | left out when not set | The entries of a lifecycle's rights in file order; null, and left out of the JSON, when it has none. |
| `triggerPoints` | list of [triggerPointRecord](#triggerpointrecord) | left out when not set | A lifecycle's trigger points in file order, their run order at each moment; null, and left out of the JSON, when it has none. |

### stateRecord

A state of a process.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The state id. |
| `name` | string | never | The name. |
| `path` | string | never | The names from the root, joined with dots. |
| `type` | string | never | `atomic`, `compound`, `parallel`, `final` or `history`. |
| `parent` | string | can be null | The parent state's id. Null when not set. |
| `children` | list of string | never | The child states' ids. |
| `initial` | string | can be null | The initial child's id. Null when not set. |
| `history` | string | can be null | `shallow` or `deep`, for a history state. Null otherwise. |
| `defaultTarget` | string | can be null | A history state's default target's id. Null when not set. |
| `entry` | list of string | never | The entry actions' ids. |
| `exit` | list of string | never | The exit actions' ids. |
| `invoke` | list of string | never | The ids of the invocations that run while the state is active. |
| `isFinal` | boolean | never | Whether the state is final. |
| `isAtomic` | boolean | never | Whether the state has no children. |
| `depth` | integer | never | The depth (0 at the root). |
| `boundMember` | string | can be null | The id of the enum member a lifecycle's root-level state stands for. Null when not set. |
| `transitionsOut` | list of string | never | The ids of the transitions that leave the state. |
| `regionIndex` | integer | can be null | The region's index under a parallel parent. Null when not set. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `code` | any | left out when not set | The state's code, a string or an integer; null, and left out of the JSON, when it has none. |

### transitionRecord

A transition of a process.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The transition id. |
| `source` | string | never | The source state's id. |
| `targets` | list of string | never | The target states' ids. |
| `trigger` | string | never | `event`, `after`, `done`, `error` or `always`. |
| `event` | string | can be null | The event's id. Null when not set. |
| `after` | string | can be null | The delay as written. Null when not set. |
| `afterMs` | integer | can be null | The delay in milliseconds. Null when not set. |
| `afterTicks` | integer | can be null | The delay in ticks. Null when not set. |
| `invoke` | string | can be null | The invocation whose completion or failure triggers it. Null when not set. |
| `guard` | string | can be null | The guard's id. Null when not set. |
| `guardMissing` | boolean | never | Whether the guard named is not declared. |
| `actions` | list of string | never | The actions' ids. |
| `external` | boolean | never | Whether the transition exits and re-enters its source. |
| `gate` | string | can be null | The approval gate's id. Null when not set. |
| `label` | string | never | The label the canvas shows. |
| `isTargetless` | boolean | never | Whether the transition has no target. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `verb` | string | left out when not set | The key of the verb of the move: the transition's own, else its event's; null, and left out of the JSON, when neither names one. |
| `nextRevision` | integer | left out when not set | The revision level, 1 to 3, the move moves; null, and left out of the JSON, when the transition names none. |

### eventRecord

An event of a process.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The event id. |
| `name` | string | never | The name. |
| `payload` | list of [attributeRecord](#attributerecord) | never | The payload attributes. |
| `actors` | list of string | never | The ids of the actors that may send it. |
| `transitions` | list of string | never | The ids of the transitions it triggers. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `verb` | string | left out when not set | The key of the verb the event names; null, and left out of the JSON, when it names none. |
| `firedBy` | list of string | left out when not set | The ids of the operations whose `fires` names the event, in the resolved model's order of operations: when an operation's work is done, the team's system sends the event. null, and left out of the JSON, when no operation fires it. |

### guardRecord

A guard of a process.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The guard id. |
| `name` | string | never | The name. |
| `expression` | string | can be null | The JavaScript expression, or null for a stub or a guard written as a condition. |
| `isStub` | boolean | never | Whether the guard has nothing that runs: neither an expression nor a condition that types. |
| `usedBy` | list of string | never | The ids of the transitions it guards. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `condition` | [expressionRecord](#expressionrecord) | left out when not set | The condition written in the expression language, only true passing; null, and left out of the JSON, for a guard without one or one that does not type. |

### actionRecord

An action of a process.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The action id. |
| `name` | string | never | The name. |
| `expression` | string | can be null | The JavaScript expression of its context changes, or null when it has none. |
| `isStub` | boolean | never | Whether the action has neither an expression nor sets; the host implements it. A call is that work, recorded on `call`; a call-only action is still a stub. |
| `raises` | list of string | never | The ids of the events it raises. |
| `usedBy` | list of string | never | The ids of the states and transitions that run it. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `sets` | list of [setRecord](#setrecord) | left out when not set | The values the action sets on the object, in file order; null, and left out of the JSON, when it sets none. |
| `call` | [callRecord](#callrecord) | left out when not set | The operation the action calls and the inputs it gives; null, and left out of the JSON, when it calls none. |

### setRecord

One attribute of the object an action sets, and its value.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The set's id. |
| `attribute` | string | never | The id of the attribute set (an attribute every subject of the process holds). |
| `value` | [expressionRecord](#expressionrecord) | never | The value: its text printed from current names, its type and its tree. |

### callRecord

The operation an action calls, and a value for some of its inputs. When the action runs, the team's system asks for the operation to be run with these inputs; inputs not listed are the team's.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `operation` | string | never | The id of the operation called. |
| `inputs` | list of [callInputRecord](#callinputrecord) | never | The inputs the call gives, in file order. An input is left out when its member doesn't resolve or is not an input of the operation, when it names a member an earlier input names, when the member can't be given (a file, a list, a record key of an entity with a composite key or no key, or a type expressions lack; MQ9025), or when its value doesn't type (MQ3103). |

### callInputRecord

One input member of a called operation and the value the call gives it.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The input's id. |
| `member` | string | never | The id of the operation's input member. |
| `value` | [expressionRecord](#expressionrecord) | never | The value: its text printed from current names, its type and its tree, typed against the member as a set's value is typed against its attribute. |

### gateRecord

An approval gate on a transition.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The gate id. |
| `name` | string | never | The name. |
| `transition` | string | can be null | The transition's id; null for a library's gate. |
| `usedBy` | list of string | never | The ids of the transitions that use the gate. |
| `required` | integer | never | How many signatures it needs. |
| `signers` | list of string | never | The ids of the actors that may sign. |
| `requiredActors` | list of string | never | The ids of the actors that must sign. |
| `allowRepeatSigner` | boolean | never | Whether one signer may sign more than once. |
| `reasonRequired` | boolean | never | Whether a signature needs a reason. |
| `meanings` | list of [meaningRecord](#meaningrecord) | never | The meanings a signature can carry. |
| `audit` | list of [auditFieldRecord](#auditfieldrecord) | never | The audit fields a signature records. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `stages` | list of [gateStageRecord](#gatestagerecord) | left out when not set | An approval's stages in file order; left out on a gate without stages. |
| `allowSignerAcrossSignatures` | boolean | left out when not set | Whether one person may give more than one response that counts on the approval; left out unless true. |

### meaningRecord

A meaning of a gate's signature.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The meaning id. |
| `name` | string | never | The name. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |

### auditFieldRecord

An audit field of a gate.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The field id. |
| `name` | string | never | The name. |
| `type` | string | never | The type keyword. |
| `required` | boolean | never | Whether a value is required. |
| `description` | string | can be null | The description. Null when not set. |
| `values` | list of string | never | The allowed values. |
| `attribute` | string | can be null | The id of the context attribute it records into. Null when not set. |

### gateStageRecord

A stage of an approval, holding named signatures.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The stage id. |
| `name` | string | never | The name. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `signatures` | list of [namedSignatureRecord](#namedsignaturerecord) | never | The named signatures, in file order. |
| `round` | integer | left out when not set | The round the stage runs in; left out when it is 1. |
| `when` | [expressionRecord](#expressionrecord) | left out when not set | The condition that skips the stage when false, typed at the approval site; left out when it has none or it does not type, and on a gate policy's stage. |

### namedSignatureRecord

A named signature of an approval.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The signature id. |
| `name` | string | never | The name. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | Why it is needed. Null when not set. |
| `stage` | string | never | The id of the stage that holds it. |
| `signers` | list of string | never | The ids of the actors whose members may give it. |
| `required` | integer | never | How many signatures it needs. |
| `requiredActors` | list of string | never | The ids of the actors that must each give one. |
| `allowRepeatSigner` | boolean | never | Whether one person may count twice on it. |
| `when` | [expressionRecord](#expressionrecord) | left out when not set | The condition, typed at the approval site; left out when it has none or it does not type, and on a gate policy's signature. |
| `notBy` | list of [notByRecord](#notbyrecord) | never | The entries of who may not give it, in file order. |
| `delegateTo` | list of string | left out when not set | The ids of the actors it may be delegated to; left out when it may not be. |
| `remindAfter` | string | left out when not set | The ISO 8601 duration after the round's start at which a reminder falls due, as written; left out when none. |
| `escalateAfter` | string | left out when not set | The ISO 8601 duration after the round's start at which an escalation falls due, as written; left out when none. |
| `escalateTo` | list of string | left out when not set | The ids of the actors an escalation goes to; left out when none. |
| `reauthenticate` | boolean | left out when not set | Whether the person's identity is checked again for every response on it; left out unless true. |

### notByRecord

An entry of a named signature's `notBy`, as written: a verb key or a transition id.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `verb` | string | left out when not set | The verb key; left out for a transition entry. |
| `transition` | string | left out when not set | The transition id; left out for a verb entry. |

### invokeRecord

An invocation of a service or another process.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The invocation id. |
| `name` | string | never | The name. |
| `type` | string | never | `service`, `process` or `human-task`. |
| `process` | string | can be null | The invoked process's id. Null when not set. |
| `actors` | list of string | never | The ids of the actors that answer it. |
| `state` | string | never | The id of the state it runs in. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `operation` | string | left out when not set | The id of the operation whose run does a service step's work; null, and left out of the JSON, when the step names none. |

### subjectRecord

An entry of a process's `subjects`, or one subject a stereotype entry gives.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The entry's id; for a subject a stereotype entry gives, `<entry id>/<entity id>`. |
| `entity` | string | never | The entity's id; for a subject a stereotype entry gives, the carrier's. |
| `boundAttribute` | string | can be null | The bound attribute's id. Null when not set. |
| `boundEnum` | string | can be null | The bound attribute's enum's id. Null when not set. |
| `boundReferenceType` | string | left out when not set | The bound attribute's reference type's id; null, and left out of the JSON, for any other binding. |
| `owner` | string | left out when not set | The id of the attribute or to-one end that holds the object's owner; null, and left out of the JSON, when the entry names none or MQ9606 reports it. |
| `stereotype` | string | left out when not set | The id of the stereotype whose entry gave the subject; null, and left out of the JSON, on a subject an entity entry gave. |

### stereotypeSubjectRecord

A stereotype entry of a process's `subjects`, as written.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The entry's id. |
| `stereotype` | string | never | The stereotype's id. |
| `boundAttribute` | string | can be null | The bound attribute's id, one of the stereotype's own attributes. Null when not set. |
| `entities` | list of string | never | The ids of the carriers the entry governs, by qualified name; empty when nothing carries the stereotype yet. |
| `owner` | string | left out when not set | The id of the stereotype's own attribute that holds the object's owner; null, and left out of the JSON, when the entry names none or MQ9606 reports it. |

### processRightRecord

One entry of a lifecycle's rights: an inline right, which carries what it says and shares its id with the entry, or a use of a named right, whose record (kind `right`) carries what it says.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The entry's id. |
| `right` | string | never | The right's id: the entry's own for an inline right, the named right's for a use. |
| `named` | boolean | never | Whether the entry uses a named right. |
| `states` | list of string | never | The ids of the states where the right holds; empty for every state. |
| `condition` | [expressionRecord](#expressionrecord) | left out when not set | The right's condition typed for this lifecycle's subjects; left out when it has none or it does not type. |
| `name` | string | left out when not set | An inline right's name; left out for a use. |
| `displayName` | string | left out when not set | An inline right's display name; left out when it has none, and for a use. |
| `description` | string | left out when not set | An inline right's description; left out when it has none, and for a use. |
| `access` | string | left out when not set | An inline right's access, `allow` or `deny`; left out for a use. |
| `actors` | list of string | left out when not set | An inline right's actor ids in file order, empty for everyone; left out for a use. |
| `except` | list of string | left out when not set | The ids of the actors an inline right does not apply to; left out for a use. |
| `verbs` | list of string | left out when not set | An inline right's verb keys in file order; left out for a use. |
| `ends` | list of string | left out when not set | The ids of the relation ends whose links an inline right speaks of; left out for a use. |
| `attributes` | list of string | left out when not set | The ids of the attributes whose reading or modifying an inline right speaks of; left out when it names none, and for a use. |
| `whileFrozen` | boolean | left out when not set | Whether an inline right holds only while the object, or with `ends` the link, is frozen: true, or left out. |

### triggerPointRecord

One trigger point of a lifecycle: what runs when one of its verbs happens, in the places its keys name. A check has a condition, an action or a replace has sets; with neither, `teamCode` is true.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The id. |
| `name` | string | never | The name. |
| `message` | string | never | What a refusal or a warning shows: the description, else the display name, else the name. |
| `type` | string | never | `check`, `action` or `replace`. |
| `verbs` | list of string | never | The verb keys it runs on, in file order. |
| `covers` | list of string | never | The ids of the entities it runs for, in name order. |
| `displayName` | string | left out when not set | The display name; left out when it has none. |
| `description` | string | left out when not set | Why; left out when it has none. |
| `timing` | string | left out when not set | An action's moment; left out for a check or a replace. |
| `severity` | string | left out when not set | A check's severity, `error` when the file gives none; left out for an action or a replace. |
| `states` | list of string | left out when not set | The ids of the states the object must be in; left out when it names none. |
| `targets` | list of string | left out when not set | The ids of the states a move must enter; left out when it names none. |
| `transitions` | list of string | left out when not set | The ids of the transitions a move must be, or for `sign` whose gates are signed; left out when it names none. |
| `entities` | list of string | left out when not set | The ids of the entities it is narrowed to, as written; left out when it names none. |
| `attributes` | list of string | left out when not set | The ids of the attributes a `read` or `modify` step must read or change; left out when it names none. |
| `ends` | list of string | left out when not set | The ids of the subject's relation ends whose links it speaks of; left out when it names none. |
| `when` | [expressionRecord](#expressionrecord) | left out when not set | It runs only when this is true; left out when it has none or it does not type. |
| `condition` | [expressionRecord](#expressionrecord) | left out when not set | A check's condition; left out when it has none or it does not type. |
| `sets` | list of [setRecord](#setrecord) | left out when not set | What an action or a replace sets, in file order; left out when it sets none. |
| `outside` | boolean | left out when not set | Whether the team's code reaches outside systems: true, or left out. |
| `teamCode` | boolean | left out when not set | Whether the body is the team's code: true, or left out. |
| `noBypass` | boolean | left out when not set | Whether no bulk load may ever skip it: true, or left out. |

## Scope actors

### actorRecord

A resolved actor.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The actor id. |
| `kind` | string | never | `actor`. Always `actor`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id, or null. |
| `type` | string | never | `person`, `role`, `group`, `organization`, `external-system` or `system`. |
| `processes` | list of string | never | The ids of the processes it takes part in. |
| `events` | list of string | never | The ids of the events it may send. |
| `gates` | list of string | never | The ids of the gates it may sign. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `memberOf` | list of string | left out when not set | The ids of the roles, groups and organizations it is a member of, in file order, each once, without the entries MQ9601 reports; left out when none. |
| `relative` | [relativeRecord](#relativerecord) | left out when not set | How it is worked out from the object; left out for an actor that stands for itself. |

### relativeRecord

How an actor from the object is worked out.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `form` | string | never | `owner`, `path` or `members-of`; the first with several (MQ9603), `none` with none. |
| `path` | list of string | never | The ids along the path as the model resolves them (an id it does not hold is left out): a path's steps, a members-of's within ends; empty for the owner. |
| `membersOf` | string | left out when not set | The id of the role, group or organization whose members it is; null, and left out, otherwise. |

## Scope scenarios

### scenarioRecord

A resolved scenario.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The scenario id. |
| `kind` | string | never | `scenario`. Always `scenario`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id, or null. |
| `process` | string | never | The process's id. |
| `start` | [scenarioStartRecord](#scenariostartrecord) | never | Where the run starts. |
| `steps` | list of [stepRecord](#steprecord) | never | The steps, in order. |
| `outcome` | string | never | The expected outcome. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `objects` | list of [scenarioObjectRecord](#scenarioobjectrecord) | left out when not set | The objects; null, and left out of the JSON, for a scenario with no objects. |
| `subject` | string | left out when not set | The id of the object the process runs for; null, and left out of the JSON, for a scenario with no objects. |
| `participants` | list of [participantRecord](#participantrecord) | left out when not set | The participants; null, and left out of the JSON, for a scenario with none. |

### scenarioStartRecord

Where a scenario's run starts.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `context` | map of any | never | The starting context values. |
| `at` | string | never | The starting clock, ISO 8601. |

### stepRecord

A scenario step.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The step id. |
| `index` | integer | never | The 0-based position. |
| `input` | string | never | `event`, `advance`, `done`, `error` or `sign`. |
| `event` | string | can be null | The event's id. Null when not set. |
| `invoke` | string | can be null | The invocation's id. Null when not set. |
| `after` | string | can be null | The delay as written. Null when not set. |
| `afterMs` | integer | can be null | The delay in milliseconds. Null when not set. |
| `afterTicks` | integer | can be null | The delay in ticks. Null when not set. |
| `actor` | string | can be null | The acting actor's id. Null when not set. |
| `signer` | string | can be null | The signer's name. Null when not set. |
| `meaning` | string | can be null | The meaning's id. Null when not set. |
| `reason` | string | can be null | The reason. Null when not set. |
| `payload` | map of any | never | The event payload. |
| `assume` | map of any | never | The guard outcomes assumed. |
| `expect` | [expectationRecord](#expectationrecord) | can be null | What the step expects, or null. |
| `description` | string | can be null | The description. Null when not set. |
| `participant` | string | left out when not set | The id of the participant that acts; left out for a step with none. |
| `actsAs` | string | left out when not set | With a participant, the id of the actor the step acts as; left out without a participant, or when it satisfies none of the actors the step needs. |
| `verb` | string | left out when not set | A verb step's verb key; left out for other steps. |
| `values` | map of any | left out when not set | The values a modify verb step sets, by attribute name; left out when it gives none. |
| `end` | string | left out when not set | The id of the subject's relation end a verb step's link goes through; left out when it names none. |
| `object` | string | left out when not set | The id of the scenario object at the other end of a verb step's link; left out when it names none. |
| `attributes` | list of string | left out when not set | The ids of the attributes a read verb step reads, as written; left out when it names none. |
| `response` | string | left out when not set | How the step answers an approval; left out for a signature, the default. |
| `signature` | string | left out when not set | The id of the named signature the step answers, as written; left out when the engine picks it. |
| `delegateTo` | string | left out when not set | With the response `delegate`, the id of the participant the signature is delegated to; left out when the step names none. |
| `onBehalfOf` | string | left out when not set | The id of the participant on whose behalf the step answers, who delegated the signature; left out when the step names none. |

### expectationRecord

What a step expects.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `accepted` | boolean | never | Whether the input is accepted. |
| `states` | list of string | never | The active states' ids. |
| `statePaths` | list of string | never | The active states' paths. |
| `context` | map of any | never | The expected context values. |
| `values` | map of any | left out when not set | The object's values the step expects it set, by attribute name; left out when the file gives none. |
| `refusal` | string | left out when not set | The refusal the step expects; left out when the file gives none. |
| `refusedBy` | string | left out when not set | The id of the element the step expects to refuse it; left out when the file gives none. |
| `checkWarnings` | list of string | left out when not set | The ids of the checks the step expects to warn; left out when the file gives none. |
| `triggerPoints` | list of string | left out when not set | The ids of the trigger points the step expects to run, in run order; left out when the file gives none. |
| `signatures` | map of integer | left out when not set | The signatures the step expects counted after it, by named signature id, in the gate's file order, each id naming a signature of the process; left out when the file gives none. |
| `notices` | list of [expectedNoticeRecord](#expectednoticerecord) | left out when not set | The reminders and escalations the step expects due, compared as a set, as written, each naming a signature of the process; left out when the file gives none. |

### expectedNoticeRecord

A reminder or an escalation a step expects due.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `signature` | string | never | The id of the named signature. |
| `kind` | string | never | `remind` or `escalate`. |

### scenarioObjectRecord

An object of a scenario.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The object's id. |
| `name` | string | never | The name. |
| `entity` | string | never | The entity's id. |
| `values` | map of any | never | The values given, by attribute name (by id when the entity has no such attribute). |
| `links` | list of [scenarioLinkRecord](#scenariolinkrecord) | never | The links to other objects. |
| `owner` | string | left out when not set | The id of the participant that owns the object; left out when there is none. |
| `states` | list of string | left out when not set | The ids of the object's states in other lifecycles, as given; left out when the file gives none. |
| `frozen` | boolean | left out when not set | Whether the object starts frozen: true, or left out. |

### scenarioLinkRecord

A link from a scenario object to another object of the scenario.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `end` | string | never | The relation end's id. |
| `object` | string | never | The linked object's id. |
| `frozen` | boolean | left out when not set | Whether the link starts frozen: true, or left out. |

### participantRecord

A participant of a scenario.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The participant's id. |
| `name` | string | never | The name. |
| `identity` | any | never | The identity: a string or an integer; the name when the file gives none. |
| `holds` | list of [holdingRecord](#holdingrecord) | never | The holdings. |
| `displayName` | string | left out when not set | The display name; left out when the file gives none. |
| `actor` | string | left out when not set | The id of the actor they stand for; left out when there is none. |
| `object` | string | left out when not set | The id of the object that is their record; left out when there is none. |

### holdingRecord

A role, group or organization a participant holds.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The holding's id. |
| `actor` | string | never | The id of the actor held. |
| `within` | string | left out when not set | The id of the object it counts within; left out for a holding with no context. |

## Scope operations

### operationRecord

A resolved operation: its input, outcomes and own errors, with the events it fires and what it requires by id.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The operation id. |
| `kind` | string | never | `operation`. Always `operation`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id: its own, else its subject's. Null when not set. |
| `subject` | string | can be null | The id of the entity it acts on. Null when not set. |
| `level` | string | can be null | `record` or `collection`; null without a subject. |
| `effect` | string | never | `read` or `change`. |
| `input` | list of [operationMemberRecord](#operationmemberrecord) | never | The input members, in file order. |
| `outcomes` | list of [operationOutcomeRecord](#operationoutcomerecord) | never | The outcomes, in file order. |
| `defaultOutcome` | string | can be null | The default outcome's id: the one named, else the first. Null when not set. |
| `errors` | list of [operationErrorRecord](#operationerrorrecord) | never | The errors, in file order. |
| `fires` | list of string | never | The ids of the process events it fires. |
| `requires` | list of string | never | The ids of what it requires, of any kind, in file order. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `guards` | list of [operationGuardRecord](#operationguardrecord) | left out when not set | The guards, in the order they are checked; null, and left out of the JSON, for an operation without any. |
| `verb` | string | left out when not set | The key of the verb the operation names; null, and left out of the JSON, when it names none. |
| `calledBy` | list of string | left out when not set | The ids of the actions that call the operation, in the order of processes, then in file order; null, and left out of the JSON, when none does. |
| `invokedBy` | list of string | left out when not set | The ids of the service steps (invokes) that name the operation, in the order of processes, then in file order; null, and left out of the JSON, when none does. |

### operationMemberRecord

A member of an operation's input or of an outcome's returns: a typed value, a record key or a file.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The member id. |
| `name` | string | never | The name. |
| `type` | [typeRecord](#typerecord) | can be null | The type: a typed value's, or a record key's single key attribute's; null for a key of several attributes. |
| `keyOf` | string | can be null | The id of the entity whose key the member holds, or null. |
| `file` | boolean | never | Whether the member is a file; `length` caps its size in bytes. |
| `mediaTypes` | list of string | never | The media types a file member accepts; empty accepts any. |
| `required` | boolean | never | Whether a value must be given. |
| `default` | any | can be null | The default value, or null. |
| `length` | integer | can be null | The length, or null. |
| `precision` | integer | can be null | The precision, or null. |
| `scale` | integer | can be null | The scale, or null. |
| `collection` | boolean | never | Whether the member holds a list. |
| `sensitive` | string | can be null | The sensitivity class, or null. |
| `validation` | [validationRecord](#validationrecord) | can be null | The validation, or null. |
| `order` | integer | never | The 0-based position in its list. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |

### operationOutcomeRecord

An outcome of an operation.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The outcome id. |
| `name` | string | never | The name. |
| `isDefault` | boolean | never | Whether it is the operation's default outcome. |
| `completes` | string | never | `now` or `later`. |
| `returns` | [operationReturnsRecord](#operationreturnsrecord) | can be null | What it returns, or null. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |

### operationReturnsRecord

What an outcome returns: an inline shape (`members`), records of an entity (`entity`) or a file (`file`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `members` | list of [operationMemberRecord](#operationmemberrecord) | never | The inline shape's members, in file order; empty unless the returns is of that form. |
| `entity` | string | can be null | The id of the entity whose records are returned, or null. |
| `file` | boolean | never | Whether a file is returned. |
| `mediaTypes` | list of string | never | The media types a file result may have, the first the usual one. |
| `list` | boolean | never | Whether a list is returned. |
| `paging` | string | can be null | `page` or `cursor` for a paged list, else null. |
| `maxPageSize` | integer | can be null | The largest page a caller may ask for, set with `paging`. Null otherwise. |

### operationErrorRecord

An error of an operation: its own, or one a stereotype contributes (by its keyed id).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The error id; an inherited error's is `<operationId>.<errorId>`. |
| `name` | string | never | The name. |
| `code` | string | never | The stable text clients see: the code, else the name. |
| `input` | string | can be null | The id of the input member it points at, or null. |
| `memberOrigin` | [memberOriginView](#memberoriginview) | can be null | Where an inherited error comes from (the stereotype, its error id and the facets redefined); null for an own error. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |

### memberOriginView

Where a member of a table comes from when a stereotype of the table's effective set contributes it. The member's key in the table is `<tableKey>.<MemberId>`: the keyed id a seed, index, binding or query names it by. The table's own members carry none.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `source` | string | never | `stereotype` (a stereotype the table lists, or one it implies), `schema-default` or `package-default` (a stereotype a database schema's or a package's `defaultStereotypes` gives the table, or one it implies). |
| `stereotype` | string | can be null | The contributing stereotype's key. Null when not set. |
| `memberId` | string | can be null | The stereotype member's id. Null when not set. |
| `redefined` | list of string | never | The facets the table's `redefines` entry states for the member (their JSON names, ordinal); empty when none. |

### operationGuardRecord

A guard of an operation: a condition it requires before it runs, and the error it reports when the condition does not hold; only true passes.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The guard id. |
| `name` | string | never | The name. |
| `condition` | [expressionRecord](#expressionrecord) | can be null | The condition, or null when it does not type (validation says why). |
| `error` | string | can be null | The id of the error it reports (an inherited error's keyed id), or null. |
| `displayName` | string | never | The display name. |
| `description` | string | can be null | The description. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |

## Scope custom-elements

### customRecord

A resolved custom element.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The element id. |
| `kind` | string | never | `custom`. Always `custom`. |
| `name` | string | never | The name. |
| `type` | string | never | The custom kind's type key. |
| `package` | string | can be null | The package id, or null. |
| `content` | map of any | never | The content as written. |
| `members` | list of [memberRecord](#memberrecord) | never | Every member at any depth, in walk order. |
| `displayName` | string | never | The display name. |
| `pluralName` | string | never | The plural name. |
| `description` | string | can be null | The description. Null when not set. |
| `tags` | list of string | never | The tags. |
| `category` | string | can be null | The category id, or null. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints by pack. |

### memberRecord

A member of a custom element.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The member id. |
| `name` | string | can be null | The member's name, or null when its item has none. |
| `array` | string | never | The key of the array that holds it. |
| `parent` | string | can be null | The id of the member that holds it, or null. |
| `pointer` | string | never | Its JSON pointer in the element's file. |

## Scope constraints

### constraintRecord

A named constraint: one condition over typed parameters, inlined where a site cites it.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The constraint's id. |
| `kind` | string | never | `constraint`. Always `constraint`. |
| `name` | string | never | The name a `satisfies` writes. |
| `package` | string | can be null | The package's id. Null when not set. |
| `parameters` | list of [constraintParameterRecord](#constraintparameterrecord) | never | The typed parameters, in order. |
| `condition` | [expressionRecord](#expressionrecord) | can be null | The condition over the parameters, or null when it does not type (validation says why). |
| `message` | string | can be null | The text a failure reports, or null. |
| `displayName` | string | never | The display name (the name when none is set). |
| `pluralName` | string | never | The plural name (the inflector's when none is set). |
| `description` | string | can be null | The description text, a sidecar file loaded, or null. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the element's own. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### constraintParameterRecord

A parameter of a named constraint.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The parameter's id, which the condition's paths name. |
| `name` | string | never | The name. |
| `type` | [typeRecord](#typerecord) | never | Its type, an attribute's type. |
| `displayName` | string | never | The display name (the name when none is set). |
| `description` | string | can be null | The description text, or null. |

## Scope databases

### databaseRecord

A resolved database: the database view (`databaseView`) with its kind.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The database id. |
| `kind` | string | never | `database`. Always `database`. |
| `name` | string | never | The name. |
| `dialect` | string | never | The dialect. |
| `version` | string | can be null | The target version. Null when not set. |
| `defaultSchema` | string | can be null | The effective default schema. Null when not set. |
| `tables` | list of [tableView](#tableview) | never | Every table, as `databaseView.tables`. |
| `views` | list of [viewView](#viewview) | never | Every view. |
| `sequences` | list of [sequenceView](#sequenceview) | never | Every sequence. |
| `routines` | list of [routineView](#routineview) | never | Every routine, as `databaseView.routines`. |
| `types` | list of [databaseTypeView](#databasetypeview) | never | Every database type, as `databaseView.types`. |
| `objects` | list of [sqlObjectView](#sqlobjectview) | never | Every SQL object, as `databaseView.objects`. |
| `queries` | list of [queryView](#queryview) | never | Every query, as `databaseView.queries`. |
| `schemas` | list of [schemaView](#schemaview) | never | Every schema, as `databaseView.schemas`. |
| `quoting` | string | never | `always`, `reserved` or `never`. |
| `maxIdentifierLength` | integer | can be null | The effective identifier length limit, or null for none. |
| `byConvention` | string | never | Which entities map here by convention: `all`, `packages` or `none` (the effective value). |
| `packages` | list of [conventionPackageView](#conventionpackageview) | never | The packages whose entities map here by convention, with the schema their tables go to. |
| `displayName` | string | can be null | The display name the database file sets, or null (no fallback). |
| `pluralName` | string | can be null | The plural name the database file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### tableView

One resolved table (resolved objects reference each other, so they are flattened: other objects appear by key or id).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `key` | string | never | The table key: a table file's id, or a synthesized key such as `<entityId>@<databaseId>`. |
| `name` | string | never | The physical name. |
| `schema` | string | can be null | The schema name, or null. |
| `origin` | string | never | `synthesized`, `designed` or `imported`. |
| `entityId` | string | can be null | The mapped entity's id, for an entity table. Null otherwise. |
| `relationId` | string | can be null | The relation's id, for a junction table. Null otherwise. |
| `isJunction` | boolean | never | Whether the table is a relation's junction table. |
| `isLookup` | boolean | never | Always false: the enum lookup-table option is retired (MQ7012); kept for the contract. |
| `comment` | string | can be null | The comment. Null when not set. |
| `columns` | list of [columnView](#columnview) | never | The columns, by position. |
| `primaryKey` | [keyView](#keyview) | can be null | The primary key, or null. |
| `uniques` | list of [keyView](#keyview) | never | Unique constraints. |
| `foreignKeys` | list of [foreignKeyView](#foreignkeyview) | never | Foreign keys. |
| `indexes` | list of [indexView](#indexview) | never | Indexes. |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `boundBy` | list of [boundByView](#boundbyview) | never | The entity bindings that read or write the table: the entities that materialize from it. |
| `referenceBindings` | list of [referenceBindingView](#referencebindingview) | never | The reference types whose rows the table holds. |

### columnView

One resolved column.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `key` | string | never | The column key: an attribute path, `discriminator`, `position`, `id`, or a column id. |
| `name` | string | never | The physical name. |
| `type` | string | never | The built-in type keyword. |
| `nativeType` | string | never | The dialect's type, such as `varchar(120)`. |
| `length` | integer | can be null | The effective length. Null when not set. |
| `precision` | integer | can be null | The effective precision. Null when not set. |
| `scale` | integer | can be null | The effective scale. Null when not set. |
| `nullable` | boolean | never | Whether the column is nullable. |
| `defaultSql` | string | can be null | The default SQL expression for the dialect, or null when the column has no default. |
| `identity` | boolean | never | Whether the column is an identity column. |
| `computed` | string | can be null | The computed expression. Null when not set. |
| `attributeId` | string | can be null | The id of the attribute the column stores, if any. Null otherwise. |
| `attributePath` | string | can be null | The attribute path the column stores, if any. Null otherwise. |
| `isPrimaryKey` | boolean | never | Whether the column is part of the primary key. |
| `isForeignKey` | boolean | never | Whether the column is part of a foreign key. |
| `isDiscriminator` | boolean | never | Whether the column is a TPH discriminator. |
| `position` | integer | never | The 0-based position in the table. |
| `default` | any | can be null | The literal default, as a plain value, or null. |
| `computedStored` | boolean | never | Whether a computed value is stored. |
| `sequenceId` | string | can be null | The id (or synthesized key) of the sequence that supplies values, or null. |
| `dbTypeId` | string | can be null | The id of the database type the column uses (its file's `nativeType` names it), or null. |
| `collation` | string | can be null | The collation, or null. |
| `comment` | string | can be null | The comment, or null. |
| `displayName` | string | can be null | The display name the column's own entry sets (a designed or extra column, or a synthesized column's overlay entry), or null; a synthesized column never takes its attribute's, which `attributeId` leads to. |
| `pluralName` | string | can be null | The plural name the column's own entry sets, or null. |
| `description` | string | can be null | The description the column's own entry sets, or null. |
| `stereotypes` | list of string | never | As `tableView.stereotypes`, from the column's own entry. |
| `tags` | list of string | never | As `tableView.tags`, from the column's own entry. |
| `category` | string | can be null | As `tableView.category`, from the column's own entry. Null when not set. |
| `properties` | map of any | never | As `tableView.properties`, from the column's own entry. |
| `generation` | map of [generationHints](#generationhints) | never | As `tableView.generation`, from the column's own entry. |
| `typeRef` | [typeRecord](#typerecord) | can be null | The enum, reference type or scalar type the column's own entry names, or null; a projected column has none, its attribute (`attributeId`) types it. |
| `sensitive` | string | can be null | `pii` or `secret`: the column's own; else a projected column's attribute's own; else the strongest own value of the attributes bindings map to it; null when none says. |
| `memberOrigin` | [memberOriginView](#memberoriginview) | can be null | Where an inherited column comes from; null for the table's own. |

### keyView

A primary key or unique constraint.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The constraint name. |
| `columns` | list of string | never | The column keys, in key order. |
| `memberOrigin` | [memberOriginView](#memberoriginview) | can be null | Where an inherited key comes from; null for the table's own. |

### foreignKeyView

A foreign key.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The constraint name. |
| `columns` | list of string | never | The referencing column keys. |
| `referencedTable` | string | never | The referenced table's key. |
| `referencedColumns` | list of string | never | The referenced column keys. |
| `onDelete` | string | never | `no-action`, `restrict`, `cascade`, `set-null` or `set-default`. |
| `onUpdate` | string | never | The on-update action, as `onDelete`. |
| `relationId` | string | can be null | The relation the key implements, if any. Null otherwise. |
| `endId` | string | can be null | The relation end whose table holds the key, if any. Null otherwise. |
| `memberOrigin` | [memberOriginView](#memberoriginview) | can be null | Where an inherited foreign key comes from; null for the table's own. |

### indexView

An index.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The index name. |
| `columns` | list of [indexColumnView](#indexcolumnview) | never | The indexed columns with their sort order. |
| `unique` | boolean | never | Whether the index is unique. |
| `where` | string | can be null | The partial-index predicate. Null when not set. |
| `memberOrigin` | [memberOriginView](#memberoriginview) | can be null | Where an inherited index comes from; null for the table's own. |

### indexColumnView

A column of an index.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `column` | string | never | The column key. |
| `descending` | boolean | never | Whether the column sorts descending. |

### boundByView

An entity bound to a table, view or query: what the database object's `boundBy` lists.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `entityId` | string | never | The entity's id. |
| `entityName` | string | never | The entity's name. |
| `bindingId` | string | never | The binding's id. |
| `reads` | boolean | never | Whether the binding reads the object (it is the binding's source). |
| `writes` | boolean | never | Whether the binding writes the object (it is the binding's write table). |
| `constants` | list of [bindingConstantView](#bindingconstantview) | never | The binding's constants: what tells several entities of one table apart. |

### referenceBindingView

A reference type whose rows a table holds: what the table's `referenceBindings` lists.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `typeId` | string | never | The reference type's id. |
| `typeName` | string | never | The reference type's name. |
| `bindingId` | string | never | The binding's id. |
| `codeColumn` | string | can be null | The physical name of the column the code fills, or null when none does. |
| `fields` | list of [referenceBindingFieldView](#referencebindingfieldview) | never | The field map: each field (`code`, `label`, `description` or a user field's name) and its column's physical name. |
| `constants` | list of [bindingConstantView](#bindingconstantview) | never | The binding's constants: what tells several types of one table apart. |

### referenceBindingFieldView

One entry of a reference type binding's field map.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `field` | string | never | The field: `code`, `label`, `description` or a user field's name. |
| `column` | string | never | The column's physical name. |

### viewView

One resolved view.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The view file's id. |
| `name` | string | never | The physical name. |
| `schema` | string | can be null | The schema name, or null. |
| `body` | string | can be null | The body for the database's dialect (or the `"*"` body), or null when the file has none for it. |
| `columns` | list of [viewColumnView](#viewcolumnview) | never | The declared columns. |
| `comment` | string | can be null | The comment. Null when not set. |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `boundBy` | list of [boundByView](#boundbyview) | never | The entity bindings that read the view. |

### viewColumnView

A declared column of a view.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The name. |
| `type` | string | can be null | The built-in type keyword, or null. |
| `nativeType` | string | can be null | The dialect's type, or null. |
| `nullable` | boolean | never | Whether the column is nullable. |

### sequenceView

One resolved sequence.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The sequence file's id, or a synthesized key such as `<entityId>.sequence@<databaseId>`. |
| `name` | string | never | The physical name. |
| `schema` | string | can be null | The schema name, or null. |
| `type` | string | never | The built-in integer type keyword. |
| `nativeType` | string | never | The dialect's type. |
| `start` | integer | never | The first value. |
| `increment` | integer | never | The increment. |
| `min` | integer | can be null | The minimum value. Null when not set. |
| `max` | integer | can be null | The maximum value. Null when not set. |
| `cycle` | boolean | never | Whether the sequence wraps around. |
| `cache` | integer | can be null | How many values the server caches. Null when not set. |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### routineView

One resolved routine.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The routine file's id. |
| `name` | string | never | The physical name. |
| `schema` | string | can be null | The schema name, or null. |
| `routineKind` | string | never | `function` or `procedure`. |
| `parameters` | list of [routineParameterView](#routineparameterview) | never | The parameters, in order. |
| `returns` | [routineReturnsView](#routinereturnsview) | can be null | The result, or null when the routine returns nothing. |
| `language` | string | never | The body's language: the file's, else the dialect's own. |
| `body` | string | can be null | The body for the database's dialect, or null when the file has none for it. |
| `hasBody` | boolean | never | Whether the file has a body for the database's dialect (or for every dialect). |
| `deterministic` | boolean | never | Whether the routine returns the same result for the same arguments. |
| `security` | string | never | `invoker` or `definer`. |
| `dependsOn` | list of string | never | The ids (or keys, for a synthesized table) of the objects of the database it names in `dependsOn`. |
| `comment` | string | can be null | The comment, or null. |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### routineParameterView

A parameter of a routine.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The name. |
| `type` | string | can be null | The built-in type keyword, or null when a database type or a native type gives the type. |
| `dbTypeId` | string | can be null | The id of the database type, or null. |
| `length` | integer | can be null | The length facet. Null when not set. |
| `precision` | integer | can be null | The precision facet. Null when not set. |
| `scale` | integer | can be null | The scale facet. Null when not set. |
| `nativeType` | string | never | The native type for the database's dialect. |
| `mode` | string | never | `in`, `out` or `inout`. |
| `default` | string | can be null | The default value as SQL text, or null. |

### routineReturnsView

The result of a routine: a single value, or a table when `table` is set.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `type` | string | can be null | The built-in type keyword of a single value, or null. |
| `dbTypeId` | string | can be null | The id of the database type of a single value, or null. |
| `length` | integer | can be null | The length facet. Null when not set. |
| `precision` | integer | can be null | The precision facet. Null when not set. |
| `scale` | integer | can be null | The scale facet. Null when not set. |
| `nativeType` | string | can be null | The native type of a single value, or null for a table result. |
| `table` | list of [routineColumnView](#routinecolumnview) | can be null | The columns of a table result, or null. |

### routineColumnView

A column of a routine's table result.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The name. |
| `type` | string | can be null | The built-in type keyword, or null. |
| `dbTypeId` | string | can be null | The id of the database type, or null. |
| `length` | integer | can be null | The length facet. Null when not set. |
| `precision` | integer | can be null | The precision facet. Null when not set. |
| `scale` | integer | can be null | The scale facet. Null when not set. |
| `nativeType` | string | never | The native type for the database's dialect. |
| `nullable` | boolean | never | Whether the column is nullable. |

### databaseTypeView

One resolved database type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The type file's id. |
| `name` | string | never | The physical name. |
| `schema` | string | can be null | The schema name, or null. |
| `typeKind` | string | never | `domain`, `composite`, `enum` or `range`. |
| `base` | string | can be null | For a domain: the built-in type it restricts. Null otherwise. |
| `baseNativeType` | string | can be null | For a domain: the native type of its base. Null otherwise. |
| `length` | integer | can be null | The length facet of the base. Null when not set. |
| `precision` | integer | can be null | The precision facet of the base. Null when not set. |
| `scale` | integer | can be null | The scale facet of the base. Null when not set. |
| `check` | string | can be null | For a domain: the CHECK expression over `VALUE`. Null otherwise. |
| `members` | list of string | never | For an enum: the labels. |
| `fields` | list of [databaseTypeFieldView](#databasetypefieldview) | never | For a composite: the fields. |
| `subtype` | string | can be null | For a range: the built-in type of its bounds. Null otherwise. |
| `subtypeNativeType` | string | can be null | For a range: the native type of its subtype. Null otherwise. |
| `definition` | string | can be null | The definition for the database's dialect, or null (built from the structured form). |
| `isCreated` | boolean | never | Whether the database creates the type. |
| `nativeName` | string | can be null | What a column or parameter typed by it writes. Null when not set. |
| `dependsOn` | list of string | never | The ids of the database types its fields use. |
| `comment` | string | can be null | The comment, or null. |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### databaseTypeFieldView

A field of a composite database type.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The name. |
| `type` | string | can be null | The built-in type keyword, or null. |
| `dbTypeId` | string | can be null | The id of the database type, or null. |
| `length` | integer | can be null | The length facet. Null when not set. |
| `precision` | integer | can be null | The precision facet. Null when not set. |
| `scale` | integer | can be null | The scale facet. Null when not set. |
| `nativeType` | string | never | The native type for the database's dialect. |

### sqlObjectView

One resolved SQL object.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The object file's id. |
| `name` | string | never | The name. |
| `schema` | string | can be null | The schema name, or null. |
| `objectKind` | string | never | What the object is, as the file says (trigger, grant, extension...). |
| `phase` | string | never | `before` or `after` the tables. |
| `dependsOn` | list of string | never | The ids (or keys, for a synthesized table) of the objects of the database it names in `dependsOn`. |
| `body` | string | can be null | The statements for the database's dialect, or null when the file has none for it. |
| `hasBody` | boolean | never | Whether the file has statements for the database's dialect (or for every dialect). |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### queryView

One resolved query: the trees as the file writes them, with what resolution adds (sources, field types, collection keys) and the SQL for the database's dialect.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The query file's id. |
| `name` | string | never | The query's name. |
| `entityId` | string | can be null | The result entity's id, or null for an ad hoc row. |
| `parameters` | list of [queryParameterView](#queryparameterview) | never | The parameters, in order. |
| `from` | [querySourceView](#querysourceview) | never | The first source. |
| `joins` | list of [querySourceView](#querysourceview) | never | The joined sources. |
| `select` | list of [queryFieldView](#queryfieldview) | never | The select list. |
| `where` | any | can be null | The row filter as the file writes it (a predicate tree), or null. |
| `groupBy` | list of any | never | The grouping expressions as the file writes them. |
| `having` | any | can be null | The group filter as the file writes it, or null. |
| `orderBy` | list of any | never | The ordering terms as the file writes them (`expression`, `direction`, `nulls`). |
| `distinct` | boolean | never | Whether duplicate rows are removed. |
| `paging` | any | can be null | The paging as the file writes it, or null. |
| `collections` | list of [queryCollectionView](#querycollectionview) | never | The collections. |
| `sql` | string | can be null | The SQL for the database's dialect (`@name` placeholders), with the hidden key columns its collections need, or null when it cannot be rendered there. |
| `sqlParameters` | list of string | never | The parameters the SQL names, in first-appearance order. |
| `uses` | list of string | never | The ids (keys, for a synthesized table) of the tables, views, routines and database types the query reads. |
| `displayName` | string | can be null | The display name the table's file sets, or null (no fallback; a synthesized table without an overlay has no file). |
| `pluralName` | string | can be null | The plural name the table's file sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |
| `boundBy` | list of [boundByView](#boundbyview) | never | The entity bindings that read the query. |

### queryParameterView

A parameter of a query.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The name. |
| `type` | string | can be null | The built-in type keyword, or null when a database type gives the type. |
| `dbTypeId` | string | can be null | The id of the database type, or null. |
| `length` | integer | can be null | The length facet. Null when not set. |
| `precision` | integer | can be null | The precision facet. Null when not set. |
| `scale` | integer | can be null | The scale facet. Null when not set. |
| `nativeType` | string | never | The native type for the database's dialect. |
| `codeType` | string | never | The built-in keyword a code type map maps. |
| `collection` | boolean | never | Whether the caller passes a list of values. |
| `default` | any | can be null | The default value, or null. |
| `description` | string | can be null | The description, or null. |

### querySourceView

A source of a query.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `source` | string | never | The source as the file writes it. |
| `alias` | string | never | The alias. |
| `kind` | string | never | `from`, `inner`, `left`, `right`, `full` or `cross`. |
| `tableKey` | string | can be null | The table's key, when the source is a table. Null otherwise. |
| `viewId` | string | can be null | The view's id, when the source is a view. Null otherwise. |
| `name` | string | never | The physical name. |
| `schema` | string | can be null | The schema name, or null. |
| `on` | any | can be null | The join condition as the file writes it, or null. |

### queryFieldView

A field of a select list.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The field's name in the result row. |
| `attributeId` | string | can be null | What it fills: an attribute's id, `attributeId.memberId` for a value object member, the relation end a to-one navigation leads to for its foreign key, or null. |
| `expression` | any | never | The value as the file writes it (an expression tree). |
| `type` | string | can be null | The built-in type keyword, or null when unknown. |
| `nativeType` | string | can be null | The native type, or null when unknown. |
| `codeType` | string | can be null | The keyword a code type map maps, or null. |
| `nullable` | boolean | never | Whether the value may be null. |

### queryCollectionView

A collection of a query: its nested query and the statement that fills it for every parent row.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | never | The collection's name. |
| `attribute` | string | never | The file's `attribute`: an attribute id, a relation end id or an ad hoc name. |
| `entityId` | string | can be null | The element entity's id, or null for an ad hoc element. |
| `query` | any | never | The nested query as the file writes it. |
| `select` | list of [queryFieldView](#queryfieldview) | never | The nested select list, resolved. |
| `keys` | list of [queryKeyView](#querykeyview) | never | The correlation keys. |
| `sql` | string | never | The collection's statement for the database's dialect. |
| `sqlParameters` | list of string | never | The parameters it names, in first-appearance order. |

### queryKeyView

A correlation key of a collection.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `outer` | string | never | The parent's column (`alias.column`). |
| `parentField` | string | never | The parent row's field that carries the value. |
| `hidden` | boolean | never | Whether the parent's SQL adds the field as a hidden column. |
| `childField` | string | never | The collection statement's column that carries the value. |
| `parameter` | string | never | The list parameter of the collection's statement. |
| `type` | string | can be null | The value's built-in type keyword, or null. |
| `nativeType` | string | can be null | The value's native type, or null. |

### schemaView

One schema of a resolved database.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The schema's id in the database file, or `<databaseId>/<name>` for a schema the file does not declare. |
| `name` | string | never | The schema name. |
| `isDefault` | boolean | never | Whether it is the database's effective default schema. |
| `isDeclared` | boolean | never | Whether the database file declares it (only a declared schema carries annotations). |
| `displayName` | string | can be null | The display name the schema entry sets, or null. |
| `pluralName` | string | can be null | The plural name the schema entry sets, or null. |
| `description` | string | can be null | The description text (a sidecar file loaded), or null. |
| `stereotypes` | list of string | never | The stereotype keys, in application order: the effective set, with the stereotypes a schema or package default gives the table, which a synthesized table without an overlay carries too. |
| `tags` | list of string | never | The tag keys. |
| `category` | string | can be null | The category's id, or null. |
| `properties` | map of any | never | The custom properties: stereotype defaults merged under the file's own (for a table without a file, the default properties of the stereotypes its schema and package defaults give it). |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints, by pack name or `"*"`. |

### conventionPackageView

One package a database takes by convention.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `packageId` | string | never | The package id. |
| `schema` | string | can be null | The schema name its conventional tables go to, or null for the default schema. |

## Scope tables

### tableRecord

One table of a database, on its own (scope `tables`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The table's key, as `tableView.key`. |
| `kind` | string | never | `table`. Always `table`. |
| `name` | string | never | The physical name. |
| `database` | string | never | The database's id. |
| `table` | [tableView](#tableview) | never | The table. |

## Scope routines

### routineRecord

One routine of a database, on its own (scope `routines`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The routine's id. |
| `kind` | string | never | `routine`. Always `routine`. |
| `name` | string | never | The physical name. |
| `database` | string | never | The database's id. |
| `routine` | [routineView](#routineview) | never | The routine. |

## Scope database-types

### databaseTypeRecord

One database type of a database, on its own (scope `database-types`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The type's id. |
| `kind` | string | never | `database-type`. Always `database-type`. |
| `name` | string | never | The physical name. |
| `database` | string | never | The database's id. |
| `databaseType` | [databaseTypeView](#databasetypeview) | never | The type. |

## Scope sql-objects

### sqlObjectRecord

One SQL object of a database, on its own (scope `sql-objects`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The object's id. |
| `kind` | string | never | `sql-object`. Always `sql-object`. |
| `name` | string | never | The name. |
| `database` | string | never | The database's id. |
| `sqlObject` | [sqlObjectView](#sqlobjectview) | never | The object. |

## Scope queries

### queryRecord

One query of a database, on its own (scope `queries`).

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The query's id. |
| `kind` | string | never | `query`. Always `query`. |
| `name` | string | never | The query's name. |
| `database` | string | never | The database's id. |
| `query` | [queryView](#queryview) | never | The query. |

## Scope migrations

### migrationRecord

One open migration fact, sorted by database name, then applied order.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The fact's id. |
| `kind` | string | never | `migration`. Always `migration`. |
| `name` | string | never | The fact's name: the empty string for a recorded fact. |
| `database` | string | never | The database's id. |
| `order` | integer | never | The 1-based place of the fact in its database's applied order. |
| `fact` | string | never | What the fact records: `rename`, `rekey`, `change-type`, `drop`,... |
| `state` | string | never | `pending`, `complete`, `held` or `released`. |
| `missing` | list of string | never | The intent fields MQ4101 names; empty unless pending. |
| `after` | list of string | never | The ids of the facts it follows that exist in its database. |
| `object` | [migrationObject](#migrationobject) | can be null | Its object as recorded, or null. |
| `currentKey` | string | can be null | The object's key moved through the `rekey` facts after it, or null without an object. |
| `from` | [migrationState](#migrationstate) | can be null | The state before, as recorded. Null when not set. |
| `to` | [migrationState](#migrationstate) | can be null | The state after, as recorded. Null when not set. |
| `hash` | string | never | The fact content hash. |

### migrationObject

An object a migration fact names, by physical key and by its physical names at the time. It has no id, so the indexer registers no sub-element, and its keys are plain strings that no reference walker sees.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `kind` | string | never | What the object is: `table`, `column`, `view`, `sequence`, `index`, `unique`, `foreign-key`, `check`, `routine`, `database-type`, `sql-object` or `schema`. |
| `table` | string | can be null | The key of the table a member (a column, index or constraint) belongs to. Null when not set. |
| `key` | string | never | The object's physical key, as the schema diff matches it. |
| `schema` | string | can be null | The object's database schema name at the time. Null when not set. |
| `tableName` | string | can be null | A member's table name at the time. Null when not set. |
| `name` | string | can be null | The object's name at the time. Null when not set. |
| `formerNames` | list of string | never | The names the object had before (`baseline` only). |

### migrationState

A side of a migration fact: names for a `rename`; type facets for a `change-type`, with a reference column's strategy and bound table; a `value-map`'s reference type name and plural name.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `name` | string | can be null | The object's name (a `value-map`'s reference type name). Null when not set. |
| `pluralName` | string | can be null | A `value-map`'s reference type plural name, so a pack finds the objects it named after the type. Null when not set. |
| `schema` | string | can be null | The object's database schema name. Null when not set. |
| `type` | string | can be null | The column's built-in type keyword. Null when not set. |
| `length` | integer | can be null | The column's length. Null when not set. |
| `precision` | integer | can be null | The column's precision. Null when not set. |
| `scale` | integer | can be null | The column's scale. Null when not set. |
| `nativeType` | string | can be null | The column's effective native type. Null when not set. |
| `strategy` | string | can be null | A reference column's effective storage strategy. Null when not set. |
| `referenceTable` | string | can be null | The key of the table a reference column's reference type is bound to, at the time: a historical key like the object's, which later `rekey` facts move forward. Null when not set. |
| `nullable` | boolean | can be null | Whether the column takes NULL. |

## Records only all lists

### verbRecord

A verb of `model.verbs`; verb records appear only when the project uses verbs.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | can be null | The own verb's id; null for a built-in verb. |
| `kind` | string | never | `verb`. Always `verb`. |
| `name` | string | never | The key, so pages order verbs by it. |
| `key` | string | never | The key every use names, which is what to store. |
| `displayName` | string | never | The fixed label of a built-in verb, or an own verb's display name, else from its key. |
| `description` | string | never | The fixed meaning of a built-in verb, or an own verb's description (`""` when none). |
| `builtIn` | boolean | never | Whether it is a built-in verb. |
| `group` | string | never | What a built-in verb acts on; `project` for an own verb. |
| `hidden` | boolean | never | Whether it is a hidden built-in verb that is still named. |
| `used` | boolean | never | Whether an event, a transition or an operation names it. |
| `events` | list of string | never | The ids of the events that name it, in model order. |
| `transitions` | list of string | never | The ids of the transitions whose move has it, in model order. |
| `operations` | list of string | never | The ids of the operations that name it, in model order. |

### rightRecord

A named right, with what it says and the lifecycles that use it.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | The id. |
| `kind` | string | never | `right`. Always `right`. |
| `name` | string | never | The name. |
| `package` | string | can be null | The package's id. Null when not set. |
| `access` | string | never | `allow` or `deny`. |
| `actors` | list of string | never | The ids of the actors it applies to, in file order; empty for everyone. |
| `except` | list of string | never | The ids of the actors it does not apply to. |
| `verbs` | list of string | never | Its verb keys, in file order. |
| `ends` | list of string | never | The ids of the relation ends whose links it speaks of. |
| `conditionText` | string | can be null | Its condition's text, printed from current names, or as stored for a right no lifecycle uses; null for a right without a condition. |
| `processes` | list of string | never | The ids of the lifecycles that use it. |
| `displayName` | string | never | The display name. |
| `pluralName` | string | never | The plural name. |
| `description` | string | can be null | The description. Null when not set. |
| `tags` | list of string | never | The tags. |
| `category` | string | can be null | The category id. Null when not set. |
| `stereotypes` | list of string | never | The stereotype keys. |
| `properties` | map of any | never | The custom properties. |
| `generation` | map of [generationHints](#generationhints) | never | The generation hints. |
| `attributes` | list of string | left out when not set | The ids of the attributes whose reading or modifying it speaks of; left out when it names none. |
| `whileFrozen` | boolean | left out when not set | Whether it holds only while the object, or with `ends` the link, is frozen: true, or left out. |

### namedTriggerPointRecord

A named trigger point: a lifecycle's trigger point's members without states, targets and transitions, with its package, subjects and exceptEntities. Records come with every other kind, as verb records do.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `id` | string | never | Its id. |
| `kind` | string | never | `trigger-point`. Always `trigger-point`. |
| `name` | string | never | Its name. |
| `package` | string | can be null | The id of its package, or null for the root. |
| `subjects` | list of [triggerPointSubjectRecord](#triggerpointsubjectrecord) | never | Its subjects in file order: an entity's id or a stereotype's id each. |
| `message` | string | never | What a refusal or a warning shows: the description, else the display name, else the name. |
| `type` | string | never | `check`, `action` or `replace`. |
| `verbs` | list of string | never | The verb keys it runs on, in file order. |
| `covers` | list of string | never | The ids of the entities it runs for, in name order. |
| `displayName` | string | left out when not set | Its display name when it differs from its name. Left out otherwise. |
| `description` | string | left out when not set | Its description. Left out when not set. |
| `exceptEntities` | list of string | left out when not set | The ids of the entities it skips, as written; left out when it names none. |
| `timing` | string | left out when not set | An action's moment. Left out when not set. |
| `severity` | string | left out when not set | A check's severity. Left out when not set. |
| `entities` | list of string | left out when not set | The ids of the entities it is narrowed to; left out when it names none. |
| `attributes` | list of string | left out when not set | The ids of the attributes a `read` or `modify` step must read or change; left out when it names none. |
| `ends` | list of string | left out when not set | The ids of the subject's relation ends whose links it speaks of; left out when it names none. |
| `when` | [expressionRecord](#expressionrecord) | left out when not set | It runs only when this is true; left out when it has none or it does not type. |
| `condition` | [expressionRecord](#expressionrecord) | left out when not set | A check's condition; left out when it has none or it does not type. |
| `sets` | list of [setRecord](#setrecord) | left out when not set | What an action or a replace sets, in file order; left out when it sets none. |
| `outside` | boolean | left out when not set | Whether the team's code reaches outside systems: true, or left out. |
| `noBypass` | boolean | left out when not set | Whether no bulk load may ever skip it: true, or left out. |
| `teamCode` | boolean | left out when not set | Whether the body is the team's code: true, or left out. |

### triggerPointSubjectRecord

A subject of a named trigger point, as written: exactly one member is present.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `entity` | string | left out when not set | The entity's id. Left out when not set. |
| `stereotype` | string | left out when not set | The stereotype's id. Left out when not set. |

## Records of the page

### diagnostic

One finding, as a plain record. The same records feed the API, `validate --format json` and SARIF.

| Field | Type | Null | Meaning |
| --- | --- | --- | --- |
| `rule` | string | never | The rule id: `MQ` and four digits (the user guide lists them) or `x/<id>` for the project's script rules. |
| `severity` | `error`, `warning`, `info` | never | The effective severity. |
| `message` | string | never | A one-sentence message. |
| `elementId` | string | can be null | The id of the element (or sub-element) concerned, when known. Null otherwise. |
| `filePath` | string | can be null | The repo-relative file path with `/` separators, when known. Null otherwise. |
| `jsonPointer` | string | can be null | A JSON pointer into the file, when known. Null otherwise. |
| `line` | integer | can be null | The 1-based line, when known. Null otherwise. |
| `column` | integer | can be null | The 1-based column, when known. Null otherwise. |

## What templates see

Templates in a pack, and the scripts beside them, read the same resolved model, but not these records. They read the resolved
objects the records are made from:

- Names are snake_case: a template writes `entity.is_abstract` where a record has `isAbstract`.
- Another object is the object itself, not its id: `entity.base.name` works, where a record holds `base` as an id.
- Some shapes differ. For example, `entity.mappings` is a map by database name where the record has a list, and templates see
  members the records leave out, such as `entity.own_attributes`, `entity.trigger_points` and `entity.operations`.
- A calculated value is a member, `element.calculated.<name>`, not an entry of the page's `calculated` map.
- Templates also get helpers and the unit's variables: `model`, `element`, the scope's alias (such as `entity` or `table`),
  `pack`, `mapping`, `mappings`, `hints`, `data` and `unit`.

To list exactly what one unit's templates can read, use the MCP tool `get_template_context` (pack and unit), or the
completions of the editor's template editor.
