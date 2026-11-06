# A stereotype as a set of types: design

**Status (2026-11-06): a design for the owner, not built.** It designs the item after actors in the owner's order. [Actors](actors-design.md) decision 19 set its meaning and its first uses, and section 11 put it right after actors. [Who, what and when](lifecycle-actors-scenarios.md) section 4 gives the direction. [The design of 4.1](lifecycle-design.md) decision 20 kept a stereotype's attributes out of guards until this item. A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today a lifecycle lists its types one by one. A team that marks its types with a stereotype, such as `approvable`, can't say "every approvable type follows the approval lifecycle". It has to list each type, and remember to add the next one.

| Layer | What changes |
| --- | --- |
| The model | A lifecycle's or orchestration's subject entry may name a **stereotype** in place of an entity. It then covers every entity that carries the stereotype, and the entities derived from them. Its bound attribute and owner field are the stereotype's own attributes. Guards, owner fields and actors from the object may read a stereotype's attributes, which 4.1 and actors kept out |
| Code generation | Templates see one subject per covered entity, each saying which stereotype brought it, so today's packs work as they are. `process-docs` says "through stereotype `approvable`". Each only when a model uses it |
| The team's system, or a lifecycle system | Nothing changes until the team uses the new output or builds from the model |

**A project that uses none of this** keeps its files, output, findings and tool answers. Section 10 lists everything.

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **This item** | A subject entry naming a stereotype; its binding and owner; a stereotype's attributes in guards, owner fields, actors from the object and scenario values; the readers below; output, editor and explorer | 3 to 9 |
| **Later items, same meaning** | Relations with several types per end (once their drawing is agreed), verbs, rights and trigger points over a stereotype. Each takes `{ "stereotype": "<key>" }` where it lists entity types | 11 |
| **Not changed** | An operation's guards and an entity's checks. They read an entity's own and inherited attributes, as today | 11 |

**The readers of `subjects[].entity`**, each handled in section 4 or 5: the index's `Subject` and `Subjects` (`Model/ModelIndexer.cs`, with `MovesBindings`); `LifecycleBindings`; the MQ92xx rules (`Validation/ProcessRules.cs`); MQ9604 and MQ9606 (`Validation/ProcessRules.Actors.cs`); `ScenarioObjects`; `RelativeActors` (`Resolve`, `EntryOf`, `Reach`); the guard scope (`ExpressionRules.SubjectMembers`); set-lifecycle, sync-enum and sync-codes (`ModelStore.Processes.cs`); deletes (`ModelStore.ReferenceRefactors.cs`); the resolver (`Resolution/ResolveRun.Processes.cs`, `RSubject`); the records (`Editor/ResolvedRecords.cs`); the validator's peers (`ModelValidator.ProcessPeers`); the explorer (`explorer/tree.ts`) and the mock (`mocks/model/lifecycleRules.ts`, `relativeRules.ts`, `modelIndex.ts`).

## 3. Decisions

Each decision gives its confidence: **high**, **medium** or **low**.

### Where and how a stereotype is named

1. **A stereotype stands for a set of types in a process's `subjects`, and nowhere else yet.** Confidence: high.
   - Lifecycles and orchestrations alike, since both lists mean "what this process is about".
   - No other list in today's format names several entity types. An operation, a mapping, a seed and a scenario object each name one type, and one object is always of one concrete type.
   - Relation ends, verbs, rights and triggers take the same form when they are built.
2. **A new key `stereotype` on the entry holds the stereotype's key. An entry has exactly one of `entity` and `stereotype`.** Confidence: medium-high.
   - **The key, not the id:** actors decision 19 says so, and every other place names a stereotype by its key (`stereotypes`, `excludeStereotypes`, `implies`, `defaultStereotypes`). Keys never change. The index records it as a reference to the stereotype, as it does for those lists.
   - **Its own key, not `entity`:** a reader that expects an entity never gets a stereotype by mistake. `ProcessSubject.Entity` becomes optional, so the compiler lists every reader.
   - The one-subject form (`subject` on the process) stays entity only. `use-subjects` is unchanged.

### What it covers

3. **A stereotype entry covers each entity that carries the stereotype, and every entity derived from one.** Confidence: high.
   - **Carries:** the stereotype is in the entity's effective set, as the resolver works it out today: package defaults, implied stereotypes, its own list, less what it excludes.
   - **Derived:** an entry covers derived entities, as an entity entry does (4.1 decision 5). A derived entity holds its base's stereotype attributes, so it stays covered even when it excludes the stereotype as a default.
   - **Nothing carries it yet:** no finding. A team may model the lifecycle first and mark types later. The explorer and `process-docs` say "no entity carries it yet".
4. **When several entries of one process cover an entity, an entry naming the entity or a base wins, nearest first; then stereotype entries, in entry order.** Confidence: medium-high.
   - This is today's rule for a base and a derived entity (`RelativeActors.EntryOf`), with stereotype entries after entity entries.
   - It lets a team say "every approvable type, but `Contract` holds its state in `status`" without an error.
   - Section 12 asks the owner to confirm it, since it is a lasting meaning.
5. **A stereotype must apply to entities.** A stereotype whose `appliesTo` leaves out entities is refused in a subject entry (MQ9211). An unknown key is MQ2003, as in any stereotype list. Confidence: high.

### Binding, owner and what the object offers

6. **`boundAttribute` and `owner` are allowed beside a stereotype, and name one of the stereotype's own attributes.** Confidence: high.
   - Every carrier holds them under one id, so each covered entity reads the same field.
   - Not an attribute of a stereotype it implies, so the rule never depends on how defaults and exclusions combine.
   - The owner is a single-valued attribute, as MQ9606 asks today. A stereotype has no relation ends, so an owner end isn't possible.
   - The binding rules are 4.1's: an enum, a reference type or a plain status, codes forming a map, MQ9203 to MQ9210.
7. **A stereotype's attributes may be read wherever a lifecycle reads the object.** This lifts the limits of 4.1 decision 20 and actors decision 10. Confidence: medium-high.
   - **What each entry holds:** an entity entry, its own and inherited attributes and those of the stereotypes it or a base carries, with its navigations as today; a stereotype entry, the stereotype's own attributes and no navigations.
   - **In scope:** what every entry holds under one id, as today. So two entities that both carry `approvable` share `approvalStatus`, and a stereotype entry beside entity entries leaves only the stereotype's attributes and no to-one paths.
   - **Where:** guard conditions, owner fields (MQ9606), the paths of actors from the object (MQ9604; with a stereotype entry, a path is one step to a stereotype attribute, and `within` must be empty), and a scenario object's `values`.
   - **For entity entries too:** binding already accepts a stereotype's attribute on an entity entry. One rule for both forms keeps a mixed list consistent.
   - **Kept out, as today:** the bound attribute, JavaScript expressions, and everything when a context attribute is named `subject`.
   - **Resolved conditions:** a path to a stereotype's attribute ends at the stereotype's attribute, so it has one target. Generated code leaves such a guard to a person, as for any guard that reads the object (4.1 decision 21).
8. **Two lifecycles binding one attribute follow 4.1 decision 11.** "Both list" means both name the same entity, or the same stereotype. Covering doesn't count, as for a base and a derived entity. Confidence: high.

### Tools, deletes and output

9. **set-lifecycle stays about one entity, and never edits a stereotype entry.** Confidence: medium-high.
   - A target that covers the entity through a stereotype counts as listing it, so no entry is added.
   - Clearing removes entity entries as today. If a stereotype entry still covers the entity, the answer says so: the entity follows that lifecycle while it carries the stereotype.
10. **sync-enum and sync-codes take `stereotype` (a key) in place of `entity`** to pick a stereotype entry. sync-codes binds only the stereotype's own attribute. Without either, today's "the one binding" choice applies. Confidence: high.
11. **Deleting a stereotype removes its entries under either delete choice,** as deleting a listed entity does (4.1 decision 5). An emptied list goes, and the lifecycle gets MQ9201. Confidence: high.
12. **Templates see one subject per covered entity.** Confidence: medium-high.
    - `p.subjects` expands a stereotype entry at its place into one subject per carrier, by qualified name. Each has the carrier's entity, its copy of the bound and owner attributes, and `stereotype`, the stereotype's definition. An entity that its own entry covers is skipped (decision 4).
    - An expanded subject's id is `<entry id>/<entity id>`, as promoted key attributes already join ids.
    - `p.stereotype_subjects` lists the entries as written: the stereotype, the bound and owner attributes, and the carriers. It shows a stereotype nobody carries yet.
    - **Why:** today's packs, the team's own included, then work for a stereotype subject with no change. A pack that writes a lifecycle system's setup can still say "applies to every approvable type".
13. **Release.** An older engine refuses a file with a stereotype entry, so a team upgrades together, as for 4.1 and actors. `formatVersion` stays 1. Confidence: high.

## 4. Schema and files

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `process.json` | subject entry `stereotype` (after `entity`) | a stereotype key | Decision 2. `entity` stops being required; the entry needs exactly one of the two |
| `batch.json` | `sync-enum` and `sync-codes` gain `stereotype` | a stereotype key | Decision 10 |
| `openapi.yaml` | `ProcessSubject.stereotype`; the index row's `subjectStereotypes` and `covers` | | Section 8 |

Also updated: the descriptions of `boundAttribute` and `owner`, the sample app's schema copies, and the editor files generated from `openapi.yaml`.

```json
{
  "kind": "process",
  "name": "Approval",
  "use": "lifecycle",
  "subjects": [
    { "id": "…", "stereotype": "approvable", "boundAttribute": "<id of approvable.approvalStatus>", "owner": "<id of approvable.requestedBy>" },
    { "id": "…", "entity": "<id of Contract>", "boundAttribute": "<id of Contract.status>" }
  ]
}
```

`PurchaseOrder` and `TravelRequest` carry `approvable` through their package's defaults, so both follow `Approval` and hold its state in `approvalStatus`. `Contract` carries it too, but its own entry wins, so it holds the state in `status`.

## 5. Rules

| Rule | Sev | Change |
| --- | --- | --- |
| MQ2003 | E | Also an unknown key in a subject entry's `stereotype` |
| MQ9201 | E | A stereotype listed twice. Entity side: an entity is governed when it, a base, or a stereotype it or a base carries is listed |
| MQ9202 | E | On a stereotype entry, a binding that isn't one of the stereotype's own attributes. "Both list" includes the same stereotype (decision 8) |
| MQ9211 | E | New: a subject entry names a stereotype that doesn't apply to entities. Fix: name a stereotype entities carry, or an entity |
| MQ9604 | E | A path may start at a stereotype attribute that every entry holds (decision 7) |
| MQ9606 | E | A stereotype attribute is allowed: on an entity entry, one of a stereotype it or a base carries; on a stereotype entry, the stereotype's own |
| MQ9305 | E | A subject object of an entity covered through a stereotype is fine; a value for a stereotype attribute it holds is accepted |

Guard conditions keep their rules; only their scope grows (decision 7).

**Incremental validation.** `SubjectCoverage.Of(model)` maps each listed stereotype to its carriers, once per snapshot. It does no work when no entry names a stereotype. While one does:
- Saving a stereotype re-checks the processes that list it, and the processes that list a stereotype it implies.
- Saving an entity or a package re-checks the entities whose `lifecycle` names a process with a stereotype entry (MQ9201), and the scenarios of those processes (MQ9305 and replay), since carriers may change.

## 6. Resolved model and templates

- **`subjects`:** expanded, as decision 12 says. `stereotype` is left out on a subject that an entity entry gave.
- **`stereotype_subjects`:** each entry with `stereotype`, `bound_attribute` and `owner_attribute` (the stereotype's own), and `entities` (its carriers). Left out when there is none.
- **Records (`get_resolved_model`):** the same members, left out when unused.
- **Dependency keys:** a process with a stereotype entry keys its subjects on `k:entity`, `k:package` and `k:stereotype`, since any of them can change the carriers. Only a template that reads its subjects pays.
- **Guards:** a resolved condition's path to a stereotype's attribute ends at the stereotype's attribute.
- **A scenario's subject row** gains the stereotype attributes its object holds only when a guard of the process reads one, so simulation answers stay the same otherwise.

## 7. Packs

Every change depends on a stereotype entry, so today's output is the same bytes.
- **`csharp-dapper` and `sql-ddl`:** no template change is expected. They write per covered entity, as for a list of entities. Golden tests prove the code compiles and the check on a status column appears on each carrier's table.
- **`process-docs`:** a subject from a stereotype says "through stereotype `approvable`". A stereotype nobody carries yet is listed with "no entity carries it yet".
- **`samples/typescript-pack`:** checked the same way.

## 8. Editor and explorer

- **Subject control (one entry):** the picker lists entities, then a **Stereotypes** group with those that apply to entities. Picking one writes `stereotype` and drops `entity`. The bound attribute and owner are kept only if they are the stereotype's own. Their pickers then list the stereotype's own attributes that fit.
- **Two or more entries:** shown read-only, a stereotype as "approvable (stereotype)", editable in the JSON view, as 4.1 decision 10.
- **The index row of a process** gains `subjectStereotypes` (stereotype ids, in entry order) and `covers` (the carriers' ids), each left out when empty. `Subject` and `Subjects` keep counting entity entries only. Index rows carry an element's own stereotype keys, not package defaults, so the explorer can't work out carriers by itself. The index rebuilds such rows when an entity's, package's or stereotype's stereotypes change, and only while a stereotype entry exists.
- **Explorer:** the Lifecycles folder names the stereotype in the row's text. An entity's own Lifecycles folder adds the lifecycles that cover it through a stereotype, with "through stereotype approvable", as it shows "through base" today.
- **Mock:** the rules above and the new row fields.

## 9. Tools

`set-lifecycle`, `sync-enum` and `sync-codes` as decisions 9 and 10. `get_references` on a stereotype lists the entries that name it. `delete_element` as decision 11. `import_process` and `export_process` carry the key. The tool texts and the modeling skill describe the form.

## 10. A project that uses none of this

- **Files:** nothing rewrites a file, so saves, `format`, `format --check` and snapshot import behave as today.
- **Output:** the same bytes. Expansion and `stereotype_subjects` happen only with a stereotype entry.
- **Tool answers:** the same. New row fields and record members are left out when unused. The rule list gains MQ9211, and the schemas and tool texts describe the key.
- **Findings:** the same, with one exception. A model that already reaches for a stereotype's attribute loses that error: MQ9606 on such an owner, MQ9604 on such a path, MQ9305 on such a value, and an unknown name in a guard that reads one. No model gets a new error.
- **Speed:** the same. Coverage, the extra keys and the index rebuild apply only with a stereotype entry.
- **Once, as on any upgrade:** every unit renders again with the same bytes, the `.schema` copies refresh (MQ1008 until then), and the index cache is read in full once.

## 11. What comes next

- **Relations with several types per end** take `{ "stereotype": "<key>" }` on an end, with this coverage, once their drawing is agreed.
- **Verbs, rights and trigger points** over a stereotype use the same form and meaning.
- **Operation guards and entity checks** keep reading own and inherited attributes. Reading a stereotype's attributes there is its own small item, if a team asks.

## 12. Questions for the owner

Nothing blocks the build. One meaning to confirm, since it lasts:

1. **An entity listed by name beside a stereotype that covers it: its own entry wins (decision 4).** The alternative is an error, which would make "every approvable type except this one's storage" impossible to say. Recommended: its own entry wins. Confidence: medium-high.

## 13. Tests

- **`ProcessRuleTests`:** failing and passing cases for each rule in section 5. Today's models give the same findings.
- **Coverage:** a carrier by package default, by implication, by its own list; an excluded default; a derived entity of a carrier, also when it excludes the default; an entity entry beside a stereotype entry; two stereotype entries covering one entity; nothing carrying the stereotype.
- **Guard scope:** a stereotype entry alone; with entity entries; an entity entry reading a stereotype attribute it carries; the bound attribute kept out; a context attribute named `subject`.
- **Actors from the object:** the owner on a stereotype entry, a one-step path to a stereotype attribute, and `within` refused (MQ9604). Scenario replay reaches the owner on a carrier's object.
- **Resolver:** expansion order, ids, skipped entities, `stereotype`, `stereotype_subjects`, and the bound attribute per carrier.
- **Stale output:** an entity gaining the stereotype re-renders a unit that reads the subjects. Each stale test is checked to fail without its fix.
- **Incremental validation and index:** an entity or package gaining or losing the stereotype re-checks MQ9201 and the scenarios, and refreshes `covers`.
- **Tools:** set-lifecycle with a covering stereotype; sync-enum and sync-codes with `stereotype`; deleting a stereotype under each choice.
- **Packs:** a golden model with a stereotype subject for each pack. The existing goldens pass untouched.
- **Editor:** unit tests for the picker and the explorer texts; one end-to-end test that picks a stereotype as the subject.

## 14. Build plan and estimate

**Size.** About 2,300 lines with tests: engine about 650, its tests about 750, packs about 150 with expected files, editor and mock about 500, docs about 250. That is about a fifth of 4.1 (about 11,000 lines) and a third of actors.

**Agents.** One pull request, on the actors pull request until that is merged. Each agent is capped at about 100 steps.

| Agent | Model | Steps | Starts from |
| --- | --- | --- | --- |
| Engine; **contract first**: `process.json`, `batch.json`, `openapi.yaml`, rule texts | Opus | About 100 | The actors branch |
| Editor and mock | Sonnet | About 60 | The contract commit |
| Output: pack goldens and `process-docs` | Opus | About 40 | The engine agent's finish |
| Review | Opus | About 40 | |
| One fixer, if needed | Opus | About 30 | The review |
| Docs: user guide, processes guide, releases, modeling skill, `next.md` | Sonnet | About 30 | The review |

**Checklist for the engine agent.** Every reader in section 2's list, found again with grep on `.Entity` of a subject and `Subjects`, since `ProcessSubject.Entity` turns optional.

**Estimate**, from 4.1's actuals (about 3.75 hours, 1.71M output, 4.67M new input and 436M cached for about 11,000 lines) and about 20M cached per 100 Opus steps:

| Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- |
| **About 1.5 hours** | **About 0.3M** | **About 0.8M** | **About 75M** |

The cached figure is about 55M for the agents (210 Opus steps, 90 Sonnet steps) and about 20M for coordinating one pull request: launching, merging, regenerating and waiting on CI. It matches the figure actors gave for this item, less the design.
