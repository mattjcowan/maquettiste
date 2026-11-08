# process-docs

Markdown documentation of the model's processes: a page per process with a state diagram, its states, transitions, gates,
events, context, guards, actions, invokes, actors and scenarios; a page per actor; a walk-through page per scenario; and an index
page. Every page is derived from the model and overwritten on each run, so the pages never need editing: change the model and
generate again.

The pages are meant to be read and reviewed next to the model, so the pack writes to an output root you would normally commit. A project uses
it by copying the pack into `.maquettiste/templates/process-docs/`, registering its output and allowing that root in
`maquettiste.json`:

```json
"outputs": { "allow": [ { "path": "docs" } ] },
"packs": { "process-docs": { "output": "docs" } }
```

Every path below is under `docs/`. The gate 3 fixture (`tests/fixtures/models/processes`) is registered this way.

## Output

For process `PurchaseApproval` with scenario `HappyPath` and actor `BudgetHolder`:

| Unit | Mode | For | Writes |
| --- | --- | --- | --- |
| `process-page` | `overwrite` | `each process` | `processes/purchase-approval.md`: the description, a summary table, the lifecycle binding (a lifecycle's bound states and their enum members), the field each subject holds its owner in, the state diagram, then tables of states, transitions (priority order, numbered), gates with their audit record (an approval, a gate with stages, shows a table of named signatures per stage: signers, required, required actors, repeat signer, needed when, not by, why; and a paragraph on what each response does; where the approval uses them, each stage's round and its skip condition, and per signature who it may be delegated to, when to remind and escalate, to whom, and whether it is signed again with a password), events with their actors and payloads, context, guards, actions, invokes, the actors involved (an actor taken from the object in plain words, "the order's owner") and links to the scenarios |
| `actor-page` | `overwrite` | `each actor` | `actors/budget-holder.md`: type (the system actor shows as Automatic), how an actor taken from the object is worked out ("the order's account manager"), what it is a member of and its members, stereotypes and tags, the actor's `properties` (a persona's `goals` as a list), the processes that name it, the events it raises, the gates it signs (and whether its signature is required), the named signatures of approvals it gives, the tasks assigned to it, and what it acts in through its memberships |
| `scenario-page` | `overwrite` | `each scenario` | `processes/purchase-approval/happy-path.md`: the start (instant and context), the participants (identity, the actor they stand for, holdings and their object), one table row per step with its participant and the actor it acts as, the input, whether it is accepted or **refused**, the active states after it and notes (signer and meaning, a delegation and an answer on behalf, the notices due, reason, payload, assumed guards, changed context, the step's description), then the outcome |
| `rights-page` | `overwrite` (file block) | `model` | `rights.md`: each named right with who it applies to, its exceptions, verbs, condition and why, and the lifecycles that use it in which states; written only when the model has named rights. A lifecycle with rights or a default of deny gains a Rights section with its rights and a "Who can do what" table per state (a cell that depends on another lifecycle says which); an actor page lists the rights that name it, the verbs page the rights among a verb's uses, and a scenario page verb steps and refusals with the element that refused them. When a lifecycle's right names attributes or has `whileFrozen`, its rights table gains an Attributes or a While frozen column, and a named right's page a row for each; a scenario whose objects or links start frozen gains a Frozen column in its Object states table, and a verb step lists the attributes it reads |
| `trigger-points-page` | `overwrite` (file block) | `model` | `trigger-points.md`: each named trigger point with what it does, its verbs, its subjects, the types it runs for and the types it skips, and "Never skipped by a bulk load" for one with `noBypass`; written only when the model has named trigger points, and the index then links to it. A lifecycle page gains a "Trigger points" line naming the named points that run for the types it governs, and a trigger point with `noBypass` says "Never skipped by a bulk load" in its row and its sentence |
| `verbs-page` | `overwrite` (file block) | `model` | `verbs.md`: each verb of `model.verbs` with its key, group and meaning, and the events, automatic moves and operations that name it; written only when the model uses verbs. A process page's Events and Transitions tables then gain a Verb column when one of them names a verb, and the index links to the page |
| `index` | `overwrite` (file block) | `model` | `index.md`: every process and actor with links; written only when the model has a process or an actor |

File names are the kebab-case element names. Links between pages are relative, so the folder can move as a whole.

Descriptions are the author's own Markdown and are written as they are (in table cells, line breaks become spaces). Names and
paths are written as code spans; display names are escaped.

## The diagram

The state diagram is a fenced block of diagram text in the `stateDiagram-v2` syntax of Mermaid, which common Markdown renderers
draw in place. What it shows:

- **Compound states** are nested blocks; `[*] -->` points at the initial child and `--> [*]` leaves each final child. The top
  level does the same with the process's initial state and its final states.
- **Parallel states** are blocks whose regions (each a nested block) are separated by `--` lines.
- **Choice states** are the choice pseudo-state (`state CreditCheck <<choice>>`); their outgoing edges carry the guards, and the
  last, unguarded one is the "else".
- **History states** are states labelled with their name and `H` (shallow) or `H*` (deep), with an edge labelled `default` to
  the state entered when no history is recorded.
- **Transitions** with targets are one edge per target, labelled as the editor's canvas labels them (`event [guard] / actions`,
  `after 5d`, `done`, `always`, `done: invoke`, `error: invoke`); a gated transition adds its count, `(gate 2 of 2)`, or `(approval, 3 signatures)` for an approval. An edge is
  declared in the innermost compound state that holds both its ends, so the renderer draws it inside that block.
- **Targetless transitions** (the source stays active, nothing is exited or entered) are a note beside the source, one line
  `internal: <label>` per transition, as the canvas lists them inside the state.

State ids are the state names; a name that two states share, or that the diagram text reserves (`state`, `note`, `end`, …), is
replaced by `s_` and the state's path with `_` between names (`s_Fulfilment_Billing_Shipped`), labelled with the name. Characters
that the diagram text reads as syntax (`#`, `;`, `"`, `<`, `>`) are written as its entity codes.

## Parameters

Set them in `maquettiste.json` under `packs.process-docs.parameters`.

| Parameter | Default | Meaning |
| --- | --- | --- |
| `diagrams` | `true` | Write the state diagram on each process page; `false` leaves the section out (for renderers that do not draw diagram text). |
| `title` | `"Process documentation"` | The heading of `index.md`. |

## Tests

`tests/billing-small/` is the pack's case: an `InvoiceLifecycle` process with three states, an actor `Clerk` and one
scenario, and under `expected/` the four pages the pack writes for it. `tests/helpers.test.js` tests each helper of
`helpers.js`. Copy the pack folder, `tests/` included, into a project's `.maquettiste/templates/` and run
`maquettiste pack test process-docs`: the case runs as a project of its own and its output is compared byte for byte
with `expected/`. `--update` rewrites `expected/` from the output; review the diff before keeping it. In this repository
`ShippedPackTests` in `tests/Maquettiste.Cli.Tests` runs it, and `MAQUETTISTE_UPDATE_GOLDEN=1` updates it.

## Files

| File | Holds |
| --- | --- |
| `pack.json` | Units and parameter defaults. |
| `helpers.js` | Pure string helpers: `md_cell` (escaped text for a table cell), `md_code` (a code span safe in a table), `diagram_text` (entity codes for diagram text) and `diagram_reserved`. |
| `tests/` | The `billing-small` case and `helpers.test.js` (see Tests). |
| `_docs.scriban` | Shared functions: lists of names and links, attribute types, trigger wording, transition numbers and the state diagram. |
| `process-page.scriban`, `actor-page.scriban`, `scenario-page.scriban`, `index.scriban` | The unit templates. |

## Notes

- Transition numbers are the transitions' priority order in the process and are used across the page (gates, events, guards
  and actions refer to them).
- An actor page lists the events that name the actor; an event open to any actor is listed on the process page as "any actor"
  and not on every actor's page.
- A scenario page shows what the scenario expects (its recorded expectations), not a fresh run: `maquettiste process verify`
  checks that the process still behaves that way.
- Two processes with the same name in different domains would write the same page: give them distinct names.
- Pages are deterministic: no dates, no machine data, lists in the model's order.
