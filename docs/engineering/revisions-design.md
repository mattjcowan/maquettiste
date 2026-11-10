# Revisions: design

**Status (2026-11-10): built, for 0.23.0. It was designed, checked twice, simplified at the owner's choice, then checked again (section 19).** It designs the roadmap row "Revisions: a revision sequence, revising to a chosen name, and release moving the revision on" (`next.md`, the lifecycles table), with part G of [What lifecycle systems can do](lifecycle-capabilities.md#g-revisions) as its yardstick, and the rows "Default revision scheme in this state" and "May be revised, or versioned, in this state" (part B) and "Changing the revision on a move" (part C). [Verbs](verbs-design.md) fixed the meaning of `revise` and `copy`; [trigger points](trigger-points-design.md), [rights](rights-design.md), [object effects](object-effects-design.md), [lifecycles](lifecycle-design.md) and [several types](several-types-design.md) give the parts it builds on. Every code fact here was checked against the code at 0.22.0 (221c425f). A file path without a project prefix is under `src/Maquettiste.Engine/`. The examples are invented. Each new name and format is held to the roadmap's test: it stays for good, it must help any team whether they use it or not, and it stays optional.

## 1. In short

Today `revise` is only a word. Rights and trigger points can name it, but nothing says what "the next revision" is, and nothing moves it.

| Layer | What changes |
| --- | --- |
| The model | An entity may carry one optional block, **`revisions`**. It says what revisions of that type look like: one to three **levels** (such as a letter, then a number: `A`, `A.1`, `A.2`, `B`), each a run of letters, of numbers or a list, with a first value and skipped letters; a **separator**; and, as one option, the attribute that **holds** the current revision. The same block says **what a revise does to an instance**: for each lifecycle that governs the type, start over, keep the state or go to a named state; for each attribute, reset or clear (anything else is copied); for each link, stay, move or duplicate. A derived type inherits the block and overrides it item by item. **`revise`** then means: the object gets the next value at a level, or a value the person chooses later in the sequence, and what it was stays as the earlier revision. A **move** can give the object its next revision (`nextRevision`), which is how "release moves the revision on" is said |
| Simulation and scenarios | A scenario follows the latest revision. A revise step moves the subject's revision, thaws it, and moves each lifecycle and value as its type says. A move that gives the next revision moves it as part of the move. A step can expect the revision, and a revise the sequence can't give is refused |
| Code generation | Templates read `entity.revisions` and `transition.next_revision`. `process-docs` adds a Revisions page, which is the example of reading them. `csharp-dapper` gains nothing that runs; it only skips its generated tests of scenarios that revise |
| The team's system, or a lifecycle system | Nothing changes until the team adds a `revisions` block or builds from the model |

Two places answer every question a reader has. **The type** says what a revision is, where it is kept, and what a revise carries over. **The lifecycle, and rights,** say when: which moves revise or give the next revision, and who may revise in which states.

**A project that uses none of this** keeps its files, output bytes, findings and messages, keys, replays and speed. Section 13 says how, and which tests pin it.

**One release, 0.23.0.** Readings in conditions (`is_latest`, `at_revision`), what a copy carries over, an edit that revises by itself, and links in a run wait for teams to ask (section 14).

## 2. Scope

| | What | Section |
| --- | --- | --- |
| **0.23.0** | The `revisions` block on an entity: levels, separator, the bound attribute, and the `lifecycles`, `attributes` and `links` lists with `onRevise`; inheritance through bases; the written form and one engine function for the sequence; what `revise` means; revising to a chosen name; `nextRevision` on a transition (a move gives the next revision, which covers "release moves the revision on" and "changing the revision on a move"); simulation, scenarios and recording; the resolved model, `process-docs`, tools and the editor | 5 to 12 |
| **Later, if teams ask** | Readings in expressions (`is_latest`, `at_revision`); `onCopy`; `editRevises` on a state; `last` on a level; an integer bound attribute; a sequence carried by a stereotype; links on a revise in a run; a default sequence per state; moving or freezing the earlier revision; a chosen name outside the sequence | 14 |
| **Not in this item** | Saves (versions under a revision) and a warning when a sequence in use changes (the owner, 2026-11-09); where revisions are stored (the team's tables, bindings and packs); generated code that makes revisions; `check-out` and `check-in` as built-in verbs (decision 26); a "released" state marker (decision 20) | 4, 5 |

**The yardstick rows** (the owner, 2026-11-09):
- Part G's rows are covered as section 3 says. Its last row, "Revising creates a new version", is read as "a revise makes a new revision and keeps the earlier one", which decisions 13 and 37 cover. The half of row 2 that reads the previous or latest revision in a condition waits (section 14).
- Part B's "May be revised, or versioned, in this state": the "revised" half is a right on `revise` with `states` (decision 23); the "versioned" half is out, with saves (decision 24).
- Part B's "Default revision scheme in this state" waits (decision 25).
- Part C's "Changing the revision on a move" is `nextRevision` (decision 19).

## 3. What other systems do, and what this design takes

Letters are those of the prior art table in `lifecycle-capabilities.md`; D is added by the second research round (see Sources). E was read from the owner's syntax reference (syntax only). W, T, A and O were read thinly; claims marked "(unsure)" are so.

| Capability | What the systems do | What this design takes |
| --- | --- | --- |
| Where the sequence is declared | E: one `sequence` string on the policy. W: a scheme set by a site policy. S: components collected into a scheme, shown by a state. V: a scheme with up to three levels, assigned per category. A: a sequence on the item type | On the type, as A does, in the same block as its revise rules; a family shares it by inheritance (decisions 1, 9) |
| Levels and formats | V: primary, secondary, tertiary, each alphabetic (A to Z, then AA to ZZ) or numeric. S: a major and a minor counter (`A-01`). W: letter plus iteration (`A.1`) | One to three levels; letters, numbers or a list; a separator; one written form (decisions 2 to 7, section 6) |
| First, skipped, end | S: "reset to" and "increment by". V: numeric from 1; letters run on to ZZ; a scheme in use can't be edited, only copied | `first`, `skip`, `width`, `startEmpty`; letters and numbers never run out; the guide says what changing a sequence in use does (decisions 3, 8) |
| What moves it | E, W: a revise command. S, V: an option on a transition bumps a level. A: release (unsure) | The verb `revise`, and `nextRevision` on a transition (decisions 18, 19) |
| A chosen name | E: `revise … to NAME`. W: a higher level than the next, if allowed | A later value of the sequence (decision 14) |
| The new revision's state | W: the lifecycle says it per state. A: a released item's next edit goes back to the start state. T: a working revision with no status (unsure). E: the policy's first state (unsure). O: the change names it. None has two lifecycles on one object | The type says it per lifecycle: start over, keep, or a named state (decision 33) |
| What data carries over | D: per attribute, three choices (reset to the default, start from the previous value, one value for all revisions). T: per type and operation. W: everything; no built-in reset. E: attribute reset switches (unsure) | The type says it per attribute: reset or clear; anything else is copied (decision 34) |
| Links on revise | E: per relationship end, `none`, `float` or `replicate`. A: Float, Fixed and hard forms, with the lifecycle state able to override the relation type. T: per type, operation and relation | The type says it per link: stay, move or duplicate (decision 35) |
| Where it is said | Most systems split it: state on the lifecycle, data on the attribute or type, links on the relation; only A ranks two places | One place, the revised type, so nothing needs ranking (decision 32, the owner's choice) |
| Previous, next, first, last | E: selectables `previous`, `next`, `first`, `last`. W, V, A: "latest" | Waits; the revision itself is readable through the bound attribute (section 14) |
| The next edit starts over | A: editing a released item makes a new generation, which starts the life cycle over (unsure) | Said explicitly: freeze on release, and a `revise` move from Released (decision 20); an implicit form waits |
| The old revision | E, W, V: stays as history. A: superseded (unsure) | Stays as it was; moving it waits (decision 27) |
| Versions under a revision | W: iteration per check-in. A: generation per edit. E: a `version` switch per state. S: a local version beside the revision | Out (decision 24) |

One lesson from S: its transition action moves a local counter, not the revision variable, so teams copy one into the other by hand. Here one revision is said in one place.

## 4. The owner's decisions

All on 2026-11-09. Each is kept as written here, and the design follows it.

- **The type holds the revise rules.** "The thing that is getting revised which is a type is the thing that should hold the rules on what happens to that instance of that type when a revision happens." Not lifecycles, not one rule for all objects (decisions 32 to 36). This design puts the sequence in the same block, so the type answers everything about a revision in one place.
- **No saves, and no warning for a changed sequence** (decisions 8, 24).
- **Freezing against links is the team's implementation:** "an implementation thing of the model"; no model rule (decision 31).
- **Packs are examples of reading the model, not runtimes.** The provided packs are "an example of how you access the variety of settings", to make it easier to write a team's own packs. Maquettiste describes a system; generating it is the team's (decision 43).
- **One release,** 0.23.0 (decision 44).
- **The review runs on Fable.**
- **Lean:** one browser pass, and a second only if the first finds blocking problems; push once when built; the "Version 0.23.0" commit in the same pull request (section 18).
- **The simpler shape.** Offered the reworked design (a `revision-scheme` kind with subjects, readings in expressions, `onCopy`, `editRevises`, links in a run) and this simpler one, the owner chose this one. Section 19 lists what it dropped and deferred.

## 5. Decisions

Each decision gives its confidence: **high**, **medium-high** or **medium**.

### The `revisions` block

1. **An entity may carry one optional block, `revisions`. There is no new kind.** Confidence: high.
   - A sequence is one to three lines, and the owner put the revise rules on the type (decision 32); putting the sequence beside them means one block answers what a revision is, where it is kept, and what a revise does. A family of types says it once on the base (decision 9).
   - A separate element would cost a kind in the engine, the index, the cascade, the explorer, an editor and the mock, for something a project writes a few times. The thing a shared element would let a team change in one place is the thing that should not change once objects hold its values (decision 8).
   - **Not a key on a lifecycle** (E's `sequence`): a type with revisions and no lifecycle couldn't say it. **Not on a stereotype:** stereotypes give no rules (decision 36); a sequence carried by a stereotype can come later, below a carrier's own (section 14).
   - A type with no block on itself or any base has no revisions and sees no change. A block with only `levels` is valid: the fallback says the rest (decision 13).
2. **`levels`: one to three, from the most significant. Each level has an optional `name` and either a `format` (`letters` or `numbers`) or a list of `values`.** Confidence: high.
   - Three is V's limit and covers S's usual major and minor. A list covers named stages ("Prototype", "Pilot", "Production") or E's free lists.
   - Levels have no ids. Lifecycles and steps name a level by its number (decision 21). The name is a label for templates, docs and the editor; a level without one is "level n".
3. **A level's other keys are `first`, `skip`, `width` and `startEmpty`.** Confidence: medium-high.
   - **`letters`** are A to Z, upper case. After Z come AA, AB … (as spreadsheet columns), so a level never runs out. `skip` lists letters never used, at any position (drawing practice often skips I, O, Q, S, X and Z). `first` defaults to the first letter not skipped.
   - **`numbers`** are whole numbers from `first` (default 1, may be 0), with no end. `width` pads with zeros (`01`); a value past the width grows (`99`, `100`).
   - **A list** runs in its order and ends at its last value. `first`, `skip` and `width` don't apply.
   - **`startEmpty: true`**: the level holds nothing at first, and again whenever a higher level moves. It takes its first value when it moves itself, or when a lower level moves (decision 5). Every level below a `startEmpty` level must have it too (MQ8301), so empty levels only come at the end (section 6). On the first level, a new object has no revision until it is first revised (S). On a lower one, it gives `A`, `A.1`, `A.2`, `B` (W, V).
   - An end to a letters or numbers level (`last`) waits (section 14).
4. **`separator` goes between levels, `"."` by default.** It is written only between two levels that both hold a value. It may be `""` only when each two neighbouring levels are one letters level and one numbers level (`A1`, W's habit), and it never holds a letter or a digit (MQ8303). Confidence: medium-high.
5. **How the revision moves at level k.** Levels above k keep their values, but an empty one takes its first value: revising `A` at level 3 gives `A.1.1`, and revising "no revision" at level 2 gives `A.1`. Level k takes its next value, or its first if it was empty. Levels below k go back to their start: empty with `startEmpty`, else their first value. A list at its last value can't move: revising there is refused (decision 22). Confidence: high.
   - One engine function does this (`Revisions/RevisionSequence.cs`, new). Rules, simulation, records and the editor's mock follow it, and one shared fixture pins the copies (section 17).
6. **A new object starts at the sequence's start: the levels above the first `startEmpty` level at their first values, the rest empty.** So `startEmpty` on level 1 means a new object has no revision (`""`). Confidence: high. A copy is a new object, so it starts there too (decision 15).
7. **The order compares level by level, from the first.** Within a level: letters by length, then alphabetically (so `Z` comes before `AA`); numbers by value; a list by position. Empty comes before any value. So `A` comes before `A.1`, and `B` after `A.3`. Section 6 gives the written form this reads. Confidence: high.
8. **No warning for a changed sequence** (the owner, 2026-11-09). Confidence: high.
   - The guide says what such a change does: values the team has stored may stop being values of the sequence, and since levels are named by number, reordering levels changes what a lifecycle's level 2 means (decision 21). V doesn't let a used scheme change; the guide's advice is the same: a sequence in use should stay as it is.
9. **A derived entity inherits its bases' block and overrides it item by item.** Confidence: medium-high.
   - `levels`, `separator` and `boundAttribute`: the nearest statement wins, the entity's, then its base's, then that base's base. A derived type's block without `levels` uses its base's. A block whose family names no `levels` anywhere is MQ8301.
   - `lifecycles`, `attributes` and `links`: a derived type's entries add to its bases'; its own entry for the same lifecycle, attribute or end replaces the base's. So `PurchasedPart` restates only what differs from `Part`, and `copy` or `restart` lets it undo a base's entry.
   - The resolved `entity.revisions` (section 8) holds the merged result, with `from` on each entry, so no reader merges bases itself.

### Where the revision is held

10. **The model doesn't say where the current revision is kept. The block may name a `boundAttribute` as one option.** Confidence: high.
    - Lifecycle states (`boundAttribute`) and owners (`owner`, actors decision 10) work the same way: an attribute is one option, and without one the team's system holds it (the team's tables, another system). Freezing is held nowhere in the model (rights decision 19).
    - An attribute is a modeling statement, not a column (lifecycle-design owner decision 8). A team binds it to a column or not.
    - With one, guards, rights' conditions and sets read the revision as an ordinary attribute, which is how "only the latest may be promoted" is said today: the team's system compares it. A derived type inherits the base's unless it names its own (decision 9).
11. **A bound revision attribute moves only by revising.** Confidence: high.
    - It is a single-valued, non-derived attribute the entity holds (its own, inherited, or from a stereotype it carries), typed `string` or `text`, directly or through a scalar type. It is not a lifecycle's bound attribute, and the block's `attributes` don't name it. Its default, when it has one, is a value of the sequence, since a run starts from it (section 9). Each is MQ8305. An integer form waits (section 14).
    - A set can't name it (MQ9802 gains the case). A modify step's values follow the same test, since they may name only "attributes a set could set" (`Processes/VerbStepFields.cs:10`), so MQ9305 refuses it there.

### What `revise` means

12. **`revise` gets its behaviour only where the object's type has `revisions`.** Elsewhere it keeps today's meaning: its rights, its trigger points and, on an event, its move, and nothing else. Confidence: high.
    - A built-in verb's meaning can't change (verbs decision 2). Its fixed meaning is already "Make the next revision of an object" (`Model/BuiltInVerbs.cs:46`); the block says what "next" is. So the model states it, and adding the block is the opt-in, as naming `freeze` was (rights decision 16).
13. **What a revise does.** Confidence: medium-high.
    - **The revision:** the object's revision becomes the next value at a level (level 1, unless a level is given), or a chosen value (decision 14).
    - **The earlier revision** stays as it was: its values, its states and its freezing. Whether it is another object or the history of the same one is the team's storage, so both one object per revision (E, W, A) and revising in place (S, V) fit.
    - **The new revision** starts thawed: revising is how a frozen item changes (rights decision 18, `rights-design.md:156`).
    - **What it carries over** is said in the block (decisions 32 to 36). Where the block says nothing, its values are carried over, every lifecycle on it starts over, and its links are not said, so the team decides.
14. **A chosen name must be a value the sequence can form, later than the item's latest revision.** It is given instead of a level, never with one. In a run the two are the same, since the run follows the latest (decision 37). Confidence: medium-high.
    - Forward only keeps the revisions of one item in the sequence's order, which every sort relies on. Skipping ahead covers W's "a higher level than the next" and E's usual use. Comparing with the latest, not the revised one, keeps a team that revises an older revision from making a name twice.
    - E also allows a name outside the sequence. Allowing it later breaks no file; refusing it later would. So it starts strict (section 14).
15. **Copy is restated, not changed:** the copy is a new object, so it starts each lifecycle at its initial state and its revision at the sequence's start. What a copy carries over stays the team's statement, through the `copy` replace trigger point (trigger points decision 17); a model word for it waits (section 14). Confidence: high. The run holds no copy, so nothing in a run changes (rights decision 25).
16. **A replace on `revise`, for an object whose type has `revisions`, stands in for how the new revision gets its values.** The revision is still made: its value, its lifecycles, its freezing. Confidence: medium.
    - In a run, the new revision starts with the earlier one's values as its type carries them over, then the replace's sets apply to it. Generated code leaves revising to the team (section 11).
    - Trigger points decision 17 gave `revise` a replace for "the team's own rules for what carries over" (`trigger-points-design.md:181`). That is now exactly what it replaces. Without a block, it keeps today's meaning: the trace shows its sets and nothing is written (decision 25 there, `Processes/StatechartInterpreter.Triggers.cs:400-402`).
17. **Rights and freezing on `revise` stay as they are.** A right on `revise` with `states` says "may be revised in this state"; freezing doesn't stop `revise`. Confidence: medium-high. Decision 23 says why there is no state key.

### What moves the revision

18. **A revise is a verb step that names `revise`, or a move whose verb is `revise`** (an event that names it, or an automatic move that does). Confidence: high.
    - An event that names `revise` already runs `revise`'s rights and trigger points, and no replace (trigger points decisions 4 and 16). It now also makes the revision.
    - **Only the running chart revises.** A sub-process sees no object (`Processes/StatechartInterpreter.cs:1747`), so its moves never move the subject's revision, whatever their verb or `nextRevision`.
19. **`nextRevision: n` on a transition says which level moves on that move.** One integer, 1 to 3. Confidence: high for the shape; medium-high for the two readings.
    - **On a move whose verb is `revise`,** it names the level of that revise. Without it, a revise move moves level 1. The lifecycle that made the move goes where the move goes, and its other parallel regions keep their states and pending work; every other lifecycle follows its type (decision 33).
    - **On any other move, it labels the revision:** the move gives the object its next revision at that level, as part of the move (S, V). The label makes a new revision in place. The value before it stays as history and keeps no state of its own. The new revision keeps the states (they go where the move goes), the values, the freezing and the links: the block's `onRevise` entries don't apply to a label. The move's verb answers to rights and trigger points, not `revise`, as a move's `sets` are part of the move.
    - **When, within a move:** after the exit actions and before the transition actions, for both kinds. A revise move thaws and carries over at the same point. So exit actions see the earlier revision, and transition, entry and with-change actions see the new one.
    - **One revision per microstep.** When several transitions taken in one microstep (in parallel regions) would revise or label, the revision moves once: as a revise if any of them is a revise move, else as a label, at the most significant level any of them names (level 1 for a revise that names none). A note says so when they named different levels.
    - These are the two shapes the yardstick shows: a new revision to work on (E, W, A), and a move that labels a revision (S, V, and A's release).
    - Only lifecycles hold it (MQ8306).
20. **"Release moves the revision on" is `nextRevision` on the moves into the released state.** No "released" marker is added. Confidence: medium-high.
    - A and O mean different things by "released", so a marker would need its own meaning. "The next edit starts over" is said explicitly: freeze on release (rights), and a `revise` move from Released into the working state, as section 6's example does. A state key that revises on an edit (`editRevises`) waits (section 14).
21. **A level is named by its number, 1 to 3.** Confidence: medium-high.
    - One lifecycle may govern types with different sequences (derived types with their own `levels`, or several types on one subject list). A number works across them; a level id couldn't, and a name breaks on a rename. Every governed type with `revisions` must have the level (MQ8306); a governed type without `revisions` is left alone.
    - Reordering a type's levels changes what level 2 means for every lifecycle. The guide says so (decision 8).
22. **A revise or label the sequence can't give is refused, with the new refusal `revision`:** a list at its end, or a chosen name that is not later than the latest revision. Confidence: medium-high.
    - **It has its own row, 7b,** after signatures (row 7) and before the before actions (row 8), in selection and in verb steps (section 9). So no before action runs for a revision that can't be made, and the before actions can read the next value.
    - `refusedBy` is the entity whose block holds the sequence. On a move, the candidate is left out, like a refusing check, and the step is refused only when no candidate is left (trigger points decision 18). As the reason for an event with no candidate left, it ranks between a gate and a check: a check outranks it, and it outranks a gate, which outranks a guard (`trigger-points-design.md:205`).
    - A chosen name the sequence can't form at all is MQ9305 when the scenario is checked, since it needs no run.

### Per state

23. **"May be revised in this state" is a right on `revise` with `states`.** No key for it. Confidence: medium-high.
    - It works today (`rights-design.md:531`), with combining, conditions and `except`. A second way would need its own rule for how it combines with rights, as freezing did. "Who can do what" and `process-docs` already show `revise` per state.
24. **No saves: versions under a revision, and "may be versioned in this state", are out** (the owner, 2026-11-09). Confidence: high.
    - A revision is a deliberate new edition, such as A to B, that other things point at; a save is each stored edit inside one revision. The systems disagree on saves (W counts check-ins, A counts every edit, S keeps a local version, V calls saves versions), and the team's storage keeps them anyway.
25. **"Default revision scheme in this state" waits.** Confidence: medium. W's version of it is unconfirmed, and S keeps counters per component across schemes, which this design doesn't model. The common case (letters on release, numbers in between) is two levels, with each move naming its level.
26. **`check-out` and `check-in` stay the project's own verbs.** Confidence: high. Their meaning varies most between systems (verbs decision 1, `verbs-design.md:69`). The built-in set grows only when an item gives a verb behaviour (decision 5, `:81`); this item gives them none. `Model/BuiltInVerbs.cs:66-70` keeps offering them as suggestions.

### The earlier revision

27. **The earlier revision stays as it was. Moving it to a superseded state, or freezing it, waits.** Confidence: high. Both act on a second object: the row "An action on one object that moves another" (`lifecycle-capabilities.md:141`) and "Scenarios across objects" (`verbs-design.md:312`). In generated code, a team's trigger point on `revise` can do it today.

### Links

28. **`links` is the type's statement; a run doesn't act on it yet.** Confidence: medium-high.
    - A scenario's links hold no values, so the only visible effect of acting on them would be a link present or not and its frozen flag. Acting on them comes with "Scenarios across objects", where other objects' links to the revised one already wait (section 14). Until then, a revise in a run leaves the subject's links as they are, and its note says what the type's `links` would do.
29. **On an end with several types, or any entity, each type that reaches the end says its own rule.** A revised `Drawing` and a revised `Specification` can treat the same relation differently. Confidence: high.
30. **`duplicate` where the type's own end of the relation has `max` 1 is an error (MQ8308).** The object at the other end may link to one object at this end, and would have two. This covers a composition's owner: revising an assembly can't duplicate its parts' links, since a part has one owner. Confidence: high.
31. **Freezing doesn't change what links do on a revise.** There is no model rule for it (the owner, 2026-11-09: "an implementation thing of the model"). The plain reading: a moved link keeps its frozen flag; a duplicated link is a new link on the new revision, so it edits nothing frozen; nothing is refused for freezing. The team's implementation decides the rest, such as A's "follows the latest until the parent is released". Confidence: high.

### What a revise carries over: the type says it

The owner, 2026-11-09, first: what happens on a revise "would be determined most likely on the business entity type itself, so it's not an all objects behave the same way kind of thing". Then, settling it: "the thing that is getting revised which is a type is the thing that should hold the rules on what happens to that instance of that type when a revision happens." The second research round found that the systems split these rules across places (the state on the lifecycle, data on the attribute or type, links on the relation), and only A ranks two of them. Holding them on the type alone needs no ranking at all.

32. **The block's three lists, `lifecycles`, `attributes` and `links`, say what a revise does. Each entry's word is `onRevise`.** Confidence: high (the owner's decision).
    - A revise from any lifecycle, or from none, follows the same rules.
    - Each list holds entries for what the type cares about; anything not listed takes the fallback: lifecycles start over, values are carried over, links are not said.
    - No other place says it: no key on lifecycles, states, attributes or relation ends. One place to read, nothing to rank.
    - One word for all three lists, so an entry reads as a sentence: `{ "end": "lines", "onRevise": "duplicate" }`. It leaves room for a second word on the same entries later, such as `onCopy`, without a second block.
    - An entry is MQ8312 when it can't stand (section 7).
33. **`lifecycles`: for a lifecycle that governs the type, `onRevise` is `restart`, `keep` or a state of that lifecycle.** Confidence: medium-high.
    - `restart`: back to the initial state. `keep`: the states stay as they are, with what is collected in them (approvals, signatures, timers). A state: that state, entered as a restart enters the initial one.
    - The lifecycle whose move made the revise goes where the move goes (decision 19); every other lifecycle follows the type, and so does the running lifecycle on a verb step.
    - So an invoice's approval lifecycle can start over while its delivery lifecycle keeps "Shipped".
34. **`attributes`: for an attribute the type holds, `onRevise` is `reset` or `clear`; `copy` is allowed so a derived type can undo a base's entry.** Anything not listed is copied. Confidence: medium-high.
    - `reset`: the attribute's `default`, else empty; a `defaultExpression` is the team's system's to fill, and a run notes it. `clear`: empty. `copy`: the earlier value.
    - An attribute the type holds is its own, an inherited one, or one from a stereotype it carries. Never a derived attribute, which is worked out; never a lifecycle's bound state attribute, which follows the state; never `reset` or `clear` on one of the type's key attributes, since a revision is the same item (MQ8312). Never the bound revision attribute (MQ8305).
    - A required or immutable attribute may be reset or cleared: the new revision is given a value before the team stores it, as a new object would be. A run notes an emptied required value. A value shared by all revisions (D's third choice) waits (section 14).
35. **`links`: for an end the type reaches, `onRevise` is `stay`, `move` or `duplicate`.** Confidence: medium-high.
    - The end is the one the type navigates to, the end a scenario link names and a path from the type uses: a revised invoice's `lines`.
    - The type reaches an end when the relation's other end covers it: by naming it, a base, a stereotype it carries, several types or any entity (part 2 of several types).
    - `stay`: the earlier revision keeps the link, and the new one has none. `move`: the link goes with the new revision as it is. `duplicate`: the new revision gets a link of its own, and the earlier revision keeps its link. `duplicate` where the type's own end has `max` 1 is MQ8308 (decision 30).
36. **Stereotypes give no rules and no sequence.** A carrier names a carried attribute in its own block. Two entries for one lifecycle, attribute or end in one block are MQ8312. Confidence: medium-high. Inheritance through bases is decision 9.

### Simulation

37. **The run follows the latest revision.** After a revise, the scenario's subject stands for the new revision. Confidence: high. That needs no second object, so revisions run today, before "Scenarios across objects".
38. **What a revise does in a run,** in the fixed order of trigger points decision 18 with row 7b. Confidence: medium-high.
    - **A verb step that names `revise`:** rows 1 to 7 as today; row 7b gives the next value or refuses; rows 8 and 9 on a scratch copy that already holds the new revision's starting values; row 10 writes those values and the before actions' to the subject as `modify` does, applies a replace's sets, moves the revision, thaws the subject, and moves each lifecycle as the type says (decision 39); then rows 11 and 12; then automatic moves. Today the step is "checked and recorded only" (`Processes/StatechartInterpreter.cs:482, 509-511`).
    - **A move whose verb is `revise`:** selection as today with row 7b; the revision moves after the exit actions (decision 19), with the same carrying over.
    - **A move with `nextRevision` and another verb:** row 7b in selection; the revision moves after the exit actions, and nothing else.
    - **At revise sites,** `subject.` reads the new revision's proposed values and `old.` the earlier one (trigger points decision 13, `:139`), so a bound revision attribute reads the next value, and a reset attribute its new value, from the before actions on. At a move with `nextRevision` and another verb, `subject.` reads the object, and the bound attribute moves after the exit actions.
    - **With no subject object,** a revise keeps today's handling, and a move with `nextRevision` moves with no revision, each with a note. One lifecycle may govern types with different sequences and rules, so a run with no object can't know which apply. On a subject object whose type has no `revisions`, a move with `nextRevision` moves no revision and adds no note, and a revise keeps today's handling.
    - **Revise with `end`** on a subject whose type has `revisions` is MQ9305: `revise` acts on objects. Elsewhere it stays as today, so no scenario gets a new finding.
39. **Each lifecycle in a run** goes where its type sends it (decision 33). On a revise verb step, a running lifecycle that starts over leaves its states without exit actions, keeps none of its run state, and enters its initial states with their entry actions, even when its chart had ended; one sent to a named state does the same, entering that state; one that keeps stays as it is. The other lifecycles read their new states from then on. An orchestration keeps running, since it isn't the object's lifecycle. Section 9 gives the steps, the readers and the trace. Confidence: medium-high.
40. **What a scenario says and expects.** Confidence: high.
    - An object's `revision` gives its current revision, when its type has no bound attribute; with one, the value is in `values`, and `revision` is MQ9305. Absent, it is the bound attribute's value or default, else the sequence's start.
    - **A step's `revision`:** on a verb step that names `revise`, a chosen name (a string) or a level (an integer); on an event step, a chosen name only, since the transition names the level. An event step's `revision` is checked before the run: its event must be able to take a revise move or a move with `nextRevision` (MQ9305). When the move taken doesn't revise or label, it is ignored, with a note.
    - **`expect.revision`** is the subject's revision after the step, so any step may expect it. Absent checks nothing; a difference is MQ9312. `expect.values` leaves the bound revision attribute out, as a move's state isn't a value; the values a revise resets or clears are values, so they are listed.
    - **The trace** gains `revision` (`from`, `to`, `level`; `level` is null for a chosen name) on a step that moved it. Recording writes `expect.revision` there, in a project that uses revisions.
41. **What waits for "Scenarios across objects":** steps on the earlier revision, the copy as a run object, links on a revise, and other objects' links to the revised one. Confidence: high.

### Code generation and release

42. **Each output depends on what it needs, so a project pays only for what it uses.** Confidence: high.
    - **A project uses revisions** when an entity has `revisions`. Only then is anything new written or recorded.
    - **No naming key.** `entity.revisions` keys on the files of the entity and its bases, on the lifecycles that govern it the way `entity.trigger_lifecycles` does, and on the relations with an end the entity reaches (section 8). A scenario's steps already key on its objects' entities with their bases (`TraceInputs`, `Resolution/ResolveRun.Processes.cs:1700-1712`, added per step at `:1481`), so adding a block to the subject's type replays it; a scenario whose subject's type has `revisions` adds what `entity.revisions` keys on. Nothing is hashed for a project without a block.
43. **Packs show how to read the new members; they don't make revisions.** Confidence: high.
    - The owner, 2026-11-09: the provided packs are examples "of how you access the variety of settings", to make it easier to write a team's own packs; Maquettiste describes a system, and generating it is the team's.
    - `process-docs` writes a Revisions page, which is the example: it reads each type's `entity.revisions` and each lifecycle's `nextRevision`. `csharp-dapper` writes nothing that runs; it skips its generated test of a scenario that revises, which it couldn't pass. Both change only when a block exists. Section 11.
44. **One minor release, 0.23.0** (the owner, 2026-11-09). An older engine refuses the new keys. `formatVersion` stays 1. Confidence: high.

## 6. Written form, schema and files

### The written form

The team's system stores revision values, so how they are written and read back is permanent from the first release. `RevisionSequence` writes and reads them, and every copy follows it. The shared fixture holds every case below (section 17).

1. **Filled levels come first.** A revision is a run of filled levels from level 1 down. Empty levels only come at the end, because every level below a `startEmpty` level is `startEmpty` too (MQ8301).
2. **No revision.** When level 1 is `startEmpty`, a new object has no revision, written `""`. In any other sequence, `""` is not a value.
3. **Writing.** The filled levels' values joined by the separator: `A`, `A.1`, `A.1.1`; `A1` and `A1B` with `""`.
4. **Reading.** Split on the separator; with `""`, split where letters meet digits. A sequence of one level is not split, since a separator only stands between two levels: a list value such as `V1` reads back whole. The number of parts says how many levels are filled. A text is not a value of the sequence when it has more parts than levels, fewer parts than the levels that are never empty (those above the first `startEmpty` level), an empty part, or a part that is not a value of its level. So in a sequence of a letter and a number with no `startEmpty`, `A` is not a value: its revisions are `A.1`, `A.2`, `B.1`.
5. **Values of a level.**
   - Letters: upper case A to Z, then AA, AB …, with no skipped letter at any position, from `first` on. `I` is not a value when `I` is skipped, nor is `AI`.
   - Numbers: decimal digits, from `first` on. With `width`, padded with zeros to the width, and only the padded form is a value (`01`, not `1`); past the width a value grows (`100` at width 2, never `0100`). Without `width`, no leading zero.
   - A list: its values, exactly as written.
   - Nothing is trimmed, and case counts.
6. **Order within a level.** Letters by length, then alphabetically (`Y`, `Z`, `AA`); numbers by value (`A.9` before `A.10`); a list by position.
7. **Order of revisions.** Level by level from level 1; an empty level comes before any value. So `""` comes first, then `A`, `A.1`, `A.2`, `B`.
8. **The end of a level** is the last value of its list. A level there can't move (decision 22). Letters and numbers have no end.

### Schema

| File | Key | Type | Notes |
| --- | --- | --- | --- |
| `entity.json` | `revisions` (after `attributes`) | `{ levels?, separator?, boundAttribute?, lifecycles?, attributes?, links? }`, at least one key | Decisions 1 to 11, 32 to 36. `levels`: 1 to 3 items. `separator`: string, no default in the schema (`"."` when absent and no base says one). `boundAttribute`: an attribute id. `lifecycles`: list of `{ lifecycle, onRevise }`, `onRevise` being `restart`, `keep` or a state id. `attributes`: list of `{ attribute, onRevise }`, `onRevise` being `copy`, `reset` or `clear`. `links`: list of `{ end, onRevise }`, `onRevise` being `stay`, `move` or `duplicate`. Every id is a reference, so renames, references and deletes see it. State ids are upper-case ULIDs, so they never clash with the two words |
| `entity.json` | level | `{ name?, format?, values?, first?, skip?, width?, startEmpty? }` | `name`: identifier, unique among the levels. `format`: `letters` or `numbers`. `values`: unique non-empty strings, at least one. `first`: string (letters) or integer ≥ 0 (numbers). `skip`: unique single letters A to Z. `width`: 1 to 9. Exactly one of `format` and `values` (MQ8301) |
| `process.json` | transition `nextRevision` (after `targets`) | integer, 1 to 3 | Decisions 19, 21 |
| `scenario.json` | object `revision` (after `states`); step `revision` (after `object`); `expect.revision` (after `values`); refusal `revision` | object and expectation: string. Step: a string (a chosen name) or an integer (a level) | Decisions 22, 40 |
| `openapi.yaml` | `revisions` on the entity document and record; `nextRevision` on the transition document and record; the trace's `revision` | | Sections 8, 10 |

**A drawing** (`model/entities/Drawing.json`), with letters and numbers:

```json
"revisions": {
  "levels": [
    { "name": "major", "format": "letters", "skip": ["I", "O", "Q", "S", "X", "Z"] },
    { "name": "minor", "format": "numbers", "startEmpty": true }
  ],
  "boundAttribute": "<id of Drawing.revision>",
  "attributes": [ { "attribute": "<id of approvedBy>", "onRevise": "reset" } ]
}
```

A new drawing is `A`. Revising at level 2 gives `A.1`, then `A.2`. Revising at level 1 gives `B`. After `H` comes `J`. Its lifecycle (In Work, Approved, Released):

```json
"transitions": [
  { "id": "…", "source": "<id of Approved>", "event": "<id of Release>", "targets": ["<id of Released>"], "nextRevision": 1 },
  { "id": "…", "source": "<id of Released>", "event": "<id of Revise>", "targets": ["<id of InWork>"], "nextRevision": 2 }
]
```

`Release` names `promote`, so the move labels the release with the next letter: `A.2` becomes `B`, and states, values, freezing and links all stay. `Revise` names `revise`, so the second move gives `B.1`, thaws it, clears `approvedBy`, and the lifecycle goes to In Work because the move goes there. A plain `revise` step at level 2 outside a move would give the same `B.1` and start the lifecycle over, the fallback. "May be revised in Released only" is a right on `revise` with `states`, as today.

**An invoice** with two lifecycles and links, numbers only, no bound attribute:

```json
"revisions": {
  "levels": [ { "format": "numbers" } ],
  "lifecycles": [ { "lifecycle": "<id of Delivery>", "onRevise": "keep" } ],
  "attributes": [ { "attribute": "<id of approvedBy>", "onRevise": "reset" } ],
  "links": [ { "end": "<id of documents>", "onRevise": "duplicate" } ]
}
```

Revisions are `1`, `2`, `3`; the team's system holds the current one. A new revision keeps its delivery state (Shipped stays Shipped), starts its approval lifecycle over (not listed, so the fallback), has no approver, and links to the same documents, while the earlier revision keeps its own links. Its `lines` are not listed: a line has one invoice, so `duplicate` there would be MQ8308 (decision 30), and copying the lines as new objects waits (section 14).

**A family.** `Part` carries `revisions` with two levels. `PurchasedPart`, derived from it, adds only `"revisions": { "attributes": [ { "attribute": "<id of supplierQuote>", "onRevise": "clear" } ] }` and inherits the levels (decision 9).

Also updated: the sample app's schema copies, the editor files generated from `openapi.yaml`, and the recorded rule list, each by its fixed command.

## 7. Rules

The next free family is MQ83xx (`Diagnostics/RuleCatalog.cs:491-517` lists MQ80xx to MQ82xx, then MQ90xx). It is labelled "Revisions". MQ9312 is the next free scenario rule.

| Rule | Sev | Rule |
| --- | --- | --- |
| MQ8301 | E | **New.** A level's shape and values, one message each: neither or both of `format` and `values`; `skip` beside a format other than letters; `width` beside one other than numbers; `first`, `skip` or `width` beside `values`; two levels with one name; a level without `startEmpty` below one with it; a letters level whose `skip` leaves no letter; `first` that isn't a value of its format (a skipped or lower-case letter, letters in a numbers level); a list value that holds the separator; a block whose family names no `levels`. "Level 2 'minor' of 'Drawing' has both format and values; keep one." |
| MQ8303 | E | **New.** Written revisions can't be read back: a separator `""` between two levels that aren't one letters and one numbers level; a separator that holds a letter or a digit. "'Drawing' writes two numbers levels with no separator, so '111' could be 1.11 or 11.1; give a separator." |
| MQ8305 | E | **New.** The block's `boundAttribute` doesn't fit (decision 11): its type, derived or multi-valued, not held by the entity, a lifecycle's bound attribute, a default that isn't a value of the sequence, or an attribute the block's `attributes` name. "Attribute 'revision' of 'Drawing' is an int32, and its revisions are letters; hold the revision in a string attribute." |
| MQ8306 | E | **New.** `nextRevision` names a level that a governed type with `revisions` lacks; the lifecycle governs no type with `revisions`; or it stands on an orchestration or a library. "Transition Released → InWork names revision level 2, but entity 'Memo' has one level; name level 1, or add a level." |
| MQ8308 | E | **New.** `duplicate` for an end where the type's own end has `max` 1. "Revising 'Assembly' can't duplicate its 'parts' links: each part has one owner; use move or stay." |
| MQ8312 | E | **New.** An entry that can't stand: two entries for one lifecycle, attribute or end; a lifecycle that governs neither the entity nor a derived entity; a state that isn't a state of that lifecycle, or is a history or choice state; an attribute the entity doesn't hold, a derived one, or a lifecycle's bound state attribute; `reset` or `clear` on one of the entity's key attributes; an end neither the entity nor a derived entity reaches; an end of a relation with more than two ends (allowing it later is additive). "Attribute 'number' is part of the key of 'Invoice', so a revision can't reset it; remove the entry." |
| MQ2020 | E | Also a block entry naming an element from a package the entity's package doesn't depend on |
| MQ9802 | E | Also a set naming a revision's bound attribute, "which only revising moves" |
| MQ9305 | E | Also: an object's `revision` beside a bound attribute, on an entity without `revisions`, or not a value of its sequence; a bound revision attribute's value in `values` that isn't a value of the sequence; a step's `revision` on a step that can't revise or label, an integer on an event step, a level its sequence lacks, or a name its sequence can't form; a step's `revision` or an `expect.revision` on a subject whose type has no `revisions`; `revise` with `end` on a subject whose type has `revisions` |
| MQ9312 | E | **New.** The subject's revision after a step differs from `expect.revision`. Quick fix `refresh-scenario`, as MQ9307 |

Each MQ83xx rule checks every entity's merged block (decision 9). A finding goes on the file that holds the offending key, and on the derived entity when the conflict comes only from merging.

Seven new rule ids and three touched rules. MQ8302, MQ8304, MQ8307 and MQ8309 to MQ8311 are not used; a later item may take them. Every new rule and new case needs a `revisions` block, a `nextRevision`, or a revision key in a scenario. The catalog texts of MQ2020, MQ9305 and MQ9802 grow, so `list_validation_rules` answers differently for every project, as in earlier items. The mock's rule list follows.

**Incremental validation,** only while a block or a `nextRevision` exists:
- An entity's save checks its own block, and those of its derived entities and bases, whose inherited levels and entries depend on it; the lifecycles that name a level over it (MQ8306); and the scenarios with an object of the entity or of an entity derived from it (MQ9305).
- A process's save checks again MQ8306 on itself and MQ8312 on the entities its subjects cover or stopped covering (which lifecycles govern them, its states, its bound state attribute).
- A relation's save checks again MQ8308 and MQ8312 on the entities at its ends.
- A scalar type's save checks again MQ8305 on the blocks whose bound attributes it types.
- The dependents go beside `SeveralTypesPeers` and `ProcessPeers` (`Validation/ModelValidator.cs:879, 1239`), which do the same for relation ends and processes.

## 8. Resolved model and templates

**`entity.revisions`** is null for a type with no block in its family, else one object with everything merged through the bases (decision 9), worked out once per snapshot by `Revisions/RevisionCoverage.cs` (new). The run, the records and the templates read it, so no template merges bases itself.

| Member | Value |
| --- | --- |
| `levels` | `RRevisionLevel`s in order |
| `separator` | The separator, `"."` when none is written |
| `first_revision` | The sequence's start, as text; `""` when level 1 starts empty |
| `bound_attribute` | The bound attribute's name, or null |
| `lifecycles` | Every lifecycle that governs the entity, by name, each with `lifecycle`, `on_revise` (`restart`, `keep` or `state`), `state` (the named state, or null) and `from` (the entity whose entry said it, or null for the fallback). The fallback is filled in, so a reader never has to know it |
| `attributes` | Each attribute that isn't copied, with `attribute`, `on_revise` and `from` |
| `links` | Each end with a rule, with `end`, `on_revise` and `from` |

| `RRevisionLevel` | Value |
| --- | --- |
| `number` | 1 to 3 |
| `name` | The level's name, or null |
| `format` | `letters`, `numbers` or `list` |
| `values` | The list; empty for a format |
| `first` | As text; the format's default when not written; a list's first value for a list |
| `skip` | The skipped letters |
| `width` | The width, or null |
| `start_empty` | The flag |
| `examples` | The level's first five values on their own (`A, B, C, D, E`; `01, 02, 03, 04, 05`; all of a shorter list), for docs and the editor |

```json
"revisions": {
  "levels": [ { "number": 1, "name": "major", "format": "letters", "values": [], "first": "A", "skip": ["I","O","Q","S","X","Z"], "width": null, "start_empty": false, "examples": ["A","B","C","D","E"] },
              { "number": 2, "name": "minor", "format": "numbers", "values": [], "first": "1", "skip": [], "width": null, "start_empty": true, "examples": ["1","2","3","4","5"] } ],
  "separator": ".", "first_revision": "A", "bound_attribute": "revision",
  "lifecycles": [ { "lifecycle": "DrawingLifecycle", "on_revise": "restart", "state": null, "from": null } ],
  "attributes": [ { "attribute": "approvedBy", "on_revise": "reset", "from": "Drawing" } ],
  "links": []
}
```

In templates, `bound_attribute` and each entry's `attribute` are `RAttribute`s, `lifecycle` an `RProcess`, `state` an `RState`, `end` the relation end and `from` an `REntity`, as `entity.trigger_lifecycles` is an `RList<RProcess>` (`Resolution/ResolvedConceptual.cs:105`). The entity record carries their ids, as `ProcessRecord.BoundAttribute` does (`Editor/ResolvedRecords.cs:546`). The JSON above shows names for reading.

**On transitions:** `transition.next_revision` (the level number, or null), keyed on its own file. A template reads `transition.verb.key` to tell a revise from a labelling move. A template wanting "every type with revisions" filters `model.entities` by `revisions`.

**Records** (`get_resolved_model`): `revisions` on the entity record with the same members; `nextRevision` on the transition record; each left out when unset.

**Dependency keys.** `entity.revisions` keys on the files of the entity and its bases (`e:<id>`, `Planning/DependencyHasher.cs:208`); on the lifecycles that govern the entity the way `entity.trigger_lifecycles` does, each lifecycle's file and the referrers of each subject entry's chain, and for a stereotype entry the naming key and the carrier keys (`Resolution/ResolveRun.TriggerPoints.cs:378-394`, the `cs` and `sn` keys of `Planning/DependencyHasher.cs:224-225`); and on the relations with an end the entity reaches, with `et` and `ec` when an end names a stereotype or several types (`:227-230`). **Replay keys:** a scenario whose subject's type has `revisions` adds what `entity.revisions` keys on, beside the rights keys (`Resolution/ResolveRun.Processes.cs:1483`); a scenario's steps already key on its objects' entities and their bases (decision 42). No naming key and no `ImpactKeys` case: a project without a block hashes nothing new.

## 9. Simulation and scenarios

- **The interpreter** keeps the subject's revision and its type's `entity.revisions`. It starts from the object's `revision`, else the bound attribute's value in `values`, else the bound attribute's default, else the sequence's start (as `ScenarioObjects.Row` takes "the value given, else the declaring attribute's default", `Processes/ScenarioObjects.cs:85`). With no subject object, it keeps none (decision 38).
- **Row 7b**, in `Select`, `SelectAt` and `HandleTime` (`Processes/StatechartInterpreter.cs:786, 887, 301`) and in verb steps: after signatures and before `TriggerCandidate`, the sequence gives the next value or the candidate fails with `revision`. In `Select`'s ranking, it sits between a gate (2) and a check, which moves up one.
- **What carries over** is the subject type's `entity.revisions`, the same whichever lifecycle runs.
- **The scratch copy** that rows 8 and 9 use at a revise is the new revision: the earlier values with the type's `reset` and `clear` applied, and the bound revision attribute at the next value.
- **With a block, a revise counts as thawing for the frozen notes** (`_scope.Thaws`, `:271`): the before actions' sets are the new revision's, and it starts thawed.

### A revise verb step, step by step

For a revise verb step on a subject object whose type has `revisions`. Each step says where the code is today.

1. **Accepted after the end.** When the running process is a lifecycle that governs the subject, the step is accepted even when its chart has ended. Today `HandleVerb` refuses every verb step then (`Processes/StatechartInterpreter.cs:435-439`). A deleted object still refuses, and so does an ended orchestration.
2. **Rows 1 to 7** as today. Freezing doesn't stop `revise`.
3. **Row 7b.** The sequence gives the next value (`RevisionSequence.Next`), or the step is refused with `revision` and nothing else runs.
4. **Rows 8 and 9** on the scratch copy. A refusing check refuses the step, and nothing else runs.
5. **One macrostep.** From here to the end, everything runs inside one `Macrostep` (`:710`), as `Start` wraps `StartInstance` (`:157-172`), so the microstep limit (MQ9507) covers the automatic moves.
6. **The values.** The type's `reset` and `clear` values, then the before actions' proposed values, are written to the subject as sets are, so the trace's `values` lists them; a value a before action set wins. Today `copy` and `revise` write nothing (`:482-490`). A matching replace then applies its sets to the subject; today it writes nothing for `revise` (`StatechartInterpreter.Triggers.cs:400-402`).
7. **The revision.** The run's revision becomes the next value. A bound revision attribute takes it in the row and the run's values, written directly rather than as a set, so the trace's `values` and a recorded `expect.values` leave it out; the trace's `revision` says it.
8. **Thawing.** The subject thaws as a thaw does: not frozen, frozen at this step (`Freeze`, `:523-533`). Its links stay as they are (decision 28); the note of step 15 says what the type's `links` would do.
9. **The running lifecycle,** by its `on_revise` in `entity.revisions`:
   - **`restart`:** steps 10 to 13.
   - **A named state:** steps 10 to 13, entering that state in step 13.
   - **`keep`:** nothing; its states and run state stay, approvals and signatures collected included, and an ended chart stays ended.
10. **Leaving the states.** Every active state of the running chart's root instance is left, deepest first (reverse document order), as a move leaves a state but without its exit actions. `Cancel` (`:1429`) runs for each: its timers, pending invokes and the child instances of its invokes go, and its approval progress and collected signatures are discarded and audited as `discarded`, as on any move. The state leaves `Config`.
11. **Clearing the rest.** On the root instance (`Instance`, `:1790-1811`): `History`, `Timers`, `Pending`, `Children`, `Signatures`, `Approvals`, `Acts`, `EnteredAt` and `Queue` are emptied, and `Done` and `Reported` are false. The clock, the step index, the audit sequence and the timer sequence keep running.
12. **The context.** The root's context goes back to the run's start: each context attribute's default, then the start values `Start` was given (`InitContext`, `:1621`). The interpreter keeps those start values for this.
13. **Entering,** as `StartInstance` does (`:1477-1483`) but without its `RunToCompletion`: `Config` gains the root, then `Microstep(inst, [], target)` enters the initial state, or the named state with its ancestors and default children, in document order, with their entry actions, timers, invokes and approvals, and records `EnteredAt`.
14. **The other lifecycles** take their outcomes, as below.
15. **A note** names both revisions and, per lifecycle, what it did (started over, kept its states, went to a state), the states the earlier revision stays in, and what the type's `links` say each link would do.
16. **Rows 11 and 12** of `revise`: its with-change actions run on the new revision, before any automatic move; its after-commit actions are queued.
17. **Automatic moves:** `RunToCompletion` and `Propagate` on the root (`:723, 763`), as after any event.
18. **The act.** `revise` is recorded as the person's act, as today (`RecordAct`, `:518`), after any clearing. So "Not by" counts the person who revised as having acted in the new revision.

**The trace** of such a step: when the running lifecycle started over or went to a state, one microstep with no transitions for steps 10 to 13, whose `exited` lists every state left (deepest first), `entered` the states entered, and `actions` the entry actions only; then a microstep per automatic move. `changed` shows the context reset, `values` the type's reset and cleared values, `final` whether the chart is in a final state, and `revision` gives `from`, `to` and `level`.

**A revise move** (an event or automatic move whose verb is `revise`) runs row 7b in selection. In `Microstep` (`:1290`), the proposed values are written before the exit actions, as today (`:1299`). After the exit actions and before the transition actions, steps 7, 8, 14 and 15 run, and the type's reset and cleared values are written, except for attributes a before action set. The lifecycle that made the move goes where the move goes, and its other parallel regions keep their states and pending work. **A labelling move** runs row 7b and step 7 at the same point, and nothing else.

### The other lifecycles

From the revise on, every other lifecycle that governs the subject reads the states its `on_revise` gives: its initial configuration for `restart` (`ObjectStates.Configuration(chart, [])`, `Processes/ObjectStates.cs:127`), the named state's configuration (`ObjectStates.Configuration(chart, [state])`), or its states as before for `keep`, with a note. The run reads them wherever it reads other lifecycles today:
- **Rights** (`Processes/RunRights.cs:150, 187`) and **trigger points' rows** (`Processes/RunTriggers.cs:120-160`) take the subject through `Participants.Current` (`Processes/Participants.cs:338`), which lays the run's values over it. It now also replaces the subject's given states in the lifecycles that changed.
- **Trigger points' other lifecycles** (`RunTriggers.StatesOf`, `:162-175`) read the scenario's original object, so they too read the new states, and their cache (`_states`, `:23`) drops its entries at the revise.
- **The interpreter's own row** (`_current`), built once before the run (`Processes/ScenarioReplayer.cs:424`, `Processes/ProcessApi.cs:695`): its `$states` member is rewritten for those lifecycles.
- **A lifecycle's bound state attribute keeps its value** in the row. A run never writes one, not even the running lifecycle's on its moves, and conditions read states through `in_state`.

### The rest

- **A replace on `revise`** applies its sets to the new revision, when the type has `revisions` (decision 16).
- **`reset` with a `defaultExpression`** gives empty in a run, with a note: the team's system fills it. An emptied required value gets a note too.
- **Traces, audits, recording and replay** keep their shapes; the trace's `revision` and the notes are added only on steps that moved a revision. A model with no block replays as today.

## 10. Tools and API

- **No new tool.** Tools take documents and answer with records.
- `get_schema entity` and `get_schema process` show the new keys; `get_resolved_model`, `get_template_context` and `get_model_index` show the new members; `apply_batch` takes qualified names in `boundAttribute` and in the block's entries; `get_references` and `delete_element` handle entries; `simulate_process`, `record_scenario` and `verify_scenarios` handle revisions.
- **Deletes:** deleting a bound attribute clears `boundAttribute` under either choice. Deleting a lifecycle, a state, an attribute or a relation that an entry names removes the entry under either choice, and that item falls back. A block with no key left is removed.
- **Index:** no new row and no new member. The entity row is as today, so the index format stays `maquettiste-index/e14` (`Editor/ModelReads.cs:23`).
- **Tool texts:** `src/Maquettiste.Cli/Mcp/ModelTools.cs`, `ProcessTools.cs`, `Agent/tools.json` and the Functions endpoints where they list scenario keys; the conventions resource (`McpServerSetup.cs`), the modeling skill (`skills/maquettiste-modeling/SKILL.md`) and the assistant's prompt (`Agent/AssistantPrompt.cs`) explain the block, the two shapes of revising, and that the type says what a revise carries over.
- **The editor's mock server** (`src/editor/src/mocks/model/`) follows: the block's references and cascade in `refs.ts` and `cascade.ts`; the rules in `validate.ts`; the run in `stepper.ts`, with section 9's steps and the merged block through `resolveRevisions`; and the shared sequence function (section 12).

## 11. Packs

**The provided packs are examples for template authors** (decision 43). For revisions, their job is to show how to read `entity.revisions` and `transition.next_revision`, not to make generated code revise. Every change is written only when a block exists, so a project with no block keeps today's bytes, even when its events name `revise`, which they may since 0.14.0 (section 13).

| Pack | Change |
| --- | --- |
| `process-docs` | **The example.** A **Revisions** page, only when a block exists, and a link to it from the index. Per type with `revisions`: its levels (format, first, skipped, width, starts empty, examples), its separator, its first revision, and where the revision is held; then **When revised**: each lifecycle's `on_revise`, the attributes reset or cleared, the links' rules, each with where it was said (`from`). Per lifecycle: its revise moves and moves that give the next revision, with their level. Short comments in the template name the member each part reads. No other page changes |
| `csharp-dapper` | **Nothing that runs.** Its generated scenario tests (written only with `testsFolder`) skip a scenario with a step that revises a subject whose type has `revisions`, with a reason ("This scenario makes a revision; storing revisions is the team's"), since the generated interpreter doesn't revise and the test would fail. No sequence functions, definitions, hook, runner change or refusal constant: `process-docs` already shows how to read every member, and a second example would repeat it |
| `sql-ddl`, `seed-data`, `samples/typescript-pack` | No change. A bound revision attribute is an ordinary attribute, mapped or not like any other |

A pack outside this repository needs no change; it reads the block only if it wants it.

## 12. Editor

**The entity editor** (`editors/EntityEditor.tsx`, `EditorTabs.tsx`, `tabs.ts`) gains one dense **Revisions** tab (`editors/EntityRevisionsTab.tsx`, new). Only the entity editor shows these fields; the shared attribute grid doesn't change. The tab, the transition inspector's labels and the mock's stepper read the merged block from one function, `resolveRevisions` in `model/revisionSequence.ts`: it walks `baseChain` (`editors/related.ts:126`) and fills the fallback as section 8 does. Its unit tests cover the drawing, the invoice and the family of section 6.
- **Levels:** a table of up to three rows: **Name**, **Values** (Letters, Numbers or A list), **First**, **Skip** (letters), **Width** (numbers), **Starts empty**. Add, remove and move buttons, each with a tooltip. Rows inherited from a base show "from Part" and are replaced as a whole when the entity gives its own levels.
- **Separator**, and **Held in**: an attribute picker that offers only attributes that fit (decision 11).
- **Preview:** the first revision, then a short run of each level ("A, B, C, D, E …", "A.1, A.2, A.3 …"), live as fields change. It uses `model/revisionSequence.ts` (new), which the mock shares, and which a parity test keeps equal to the engine through the shared fixture.
- **When revised:** a row per lifecycle that governs the entity (Start over, Keep, or a state), and two short tables, **Attributes** (an attribute, then Copied, Reset or Cleared) and **Links** (an end, then Stay, Move or Duplicated), with add and remove buttons that have tooltips. A row inherited from a base shows "from Part" and can be replaced.
- An entity with no block shows the tab with one line and an **Add revisions** button.

**The process editor.**
- The transition inspector (`inspector/ProcessSections.tsx`, `TransitionSection`) gains **Next revision**: None, or Level 1 (major), Level 2 (minor)…, labelled from the levels of the lifecycle's types. On a revise move it reads **Revise at**.
- `editors/process/TransitionsTab.tsx` gains a **Revision** column.
- The chart (`canvas/statechart/TransitionEdge.tsx`) adds a small text label to such a transition: "next major" or "revise: minor". The canvas keeps its zoom and arrangement.

**Scenarios** (`editors/scenario/ScenarioEditor.tsx`): an object's **Revision** field (when its type has no bound attribute); a revise step's **Revision** field, with **Level** or **Name**; an event step's **Revision** (a name) when its event can take a revising or labelling move; an **Expect revision** cell on any step; the simulation panel shows the subject's revision and the note after a revise.

**Quick fixes** (`problems/quickFix.ts`): MQ8303 adds the separator `.`; MQ8306 picks level 1; MQ8308 picks `move`; MQ8312 removes the entry; MQ9312 refreshes the scenario.

Labels use plain words; no product names.

## 13. A project that uses none of this

- **Files:** nothing rewrites a file. Saves, `format`, `format --check` and snapshot import behave as today. Every new key has no schema default, so no file gains one.
- **Output:** the same bytes. Both packs change only when a block exists (section 11). **Pinned by** the pack goldens, which pass untouched, and a no-change golden for both packs on a model with an event that names `revise` and no block.
- **Findings and messages:** the same, word for word. Every new rule and case needs a block or a new key. A revise step with `end` keeps today's handling unless the subject's type has `revisions`. **Pinned by** the existing rule tests and a scenario test with a revise verb step and a revise event on a model with no block.
- **Simulation and replay:** the same outcomes, traces and notes. Revise and copy steps stay "checked and recorded only", and a revise event does its move only. **Pinned by** the replay goldens and the same no-block scenario.
- **Dependency keys:** the same. No naming key; `entity.revisions` is null and adds no key in a project without a block. **Pinned by** a key test.
- **Speed:** the same. `entity.revisions` is built once per snapshot, and only when a block exists. **Pinned by** the bench and its baseline (`bench/baseline.json`).
- **Answers that change for every project,** as in earlier items: `list_validation_rules` (a new family, and the longer texts of section 7), `get_schema` for the changed files (entities, processes, scenarios), the `tools/list` descriptions, the conventions resource, `get_template_context`, whose member lists come from reflection, and the entity editor's new tab.
- **Once, as on any upgrade:** every unit renders again with the same bytes, since the engine version is part of each unit's key; the `.schema` copies refresh (MQ1008 until then).
- **A project with a block that has only `levels`** gets decision 13's fallback: values carried over, every lifecycle starting over, links not said.
- **A project that adds a block later:** its existing revise steps start moving revisions and lifecycles, as freezing did for events that named `freeze` (rights decision 16). The release notes say so, and `verify_scenarios` names the steps whose outcome changes.

## 14. What comes next

Each item is left out of 0.23.0 on purpose. Each adds an optional key, function or member later without changing a file. Sizes are lines with tests, and cached re-reads when built as an item of its own.

| Item | Why it waits | Rough size |
| --- | --- | --- |
| **Readings in conditions: `is_latest(object)` and `at_revision('previous' \| 'next' \| 'first' \| 'latest', path)`.** The first follow-up | They define nothing about what a revise does; they are vocabulary for conditions, and touch `Expressions/` end to end (text, tree, typer, lowering, JavaScript, SQL refusal). In a run they are nearly constant (the subject is latest, `next` is null). The revision itself is readable today through the bound attribute, and "only the latest may be promoted" is one line in the team's system. This is the real loss of the simpler shape: half of part G's row 2 | 1,500 to 2,000 lines; 60M to 80M |
| `onCopy` on the same entries (what a copy carries over) | The owner's words were about revise. A copy has no run and half a yardstick row, and the `copy` replace trigger point already is "the team's own rules for what carries over". When wanted, a second word beside `onRevise`, no new block | 600 to 800 lines |
| `editRevises: n` on a state (an edit of a released item revises by itself) | One system's convenience (A). The model says the same policy explicitly: freeze on release, a `revise` move from Released. It is the most intricate mechanics the reworked design had: a two-verb step, rights for both, the frozen check read on the new revision | 900 to 1,100 lines |
| `last` on a letters or numbers level | Letters and numbers never run out; a list ends itself. It brings refusal cases and MQ8301 cases for a thing no team has asked for | Small; additive |
| An integer bound attribute for one numbers level | A second written form and null as "no revision". Text first | Small; additive |
| A sequence and rules carried by a stereotype for its carriers | Below a carrier's own block, additively; stereotypes give no rules today (decision 36) | Medium |
| Links on a revise in a run (`stay` drops the link, `duplicate` makes a new one) | With "Scenarios across objects", where other objects' links to the revised one, steps on the earlier revision and the copy as a run object already wait | Within that item |
| A default sequence per state; moving or freezing the earlier revision; a chosen name outside the sequence; a value shared by all revisions; copying a composition's parts as new objects | Decisions 25, 27, 14, 34, 30 | If teams ask |

## 15. Questions for the owner

One question is open, and it does not block the build: **the names, permanent once released.** They are confirmed before the release. Each stays clear of a name in use. The list is in section 16; alternatives with a live case: the block `revisions` (`revision`, `revising`); `nextRevision` (`revisionLevel`, which doesn't say that a promote also moves the revision); `restart` (`startOver`, plainer but two words); `stay`, `move`, `duplicate` (E says `none`, `float`, `replicate`, which read less plainly). **Recommendation:** as listed.

Answered on 2026-11-09: everything in section 4.

## 16. New names

- **In the format:** on an entity, `revisions` with `levels` (`name`, `format` with `letters` and `numbers`, `values`, `first`, `skip`, `width`, `startEmpty`), `separator`, `boundAttribute`, `lifecycles` (`lifecycle`, `onRevise`: `restart`, `keep` or a state), `attributes` (`attribute`, `onRevise`: `copy`, `reset`, `clear`) and `links` (`end`, `onRevise`: `stay`, `move`, `duplicate`); the transition key `nextRevision` (an integer); in scenarios, the object key `revision`, the step key `revision` (a chosen name, or a level), `expect.revision`, and the refusal `revision`.
- **Rules:** MQ83xx "Revisions": MQ8301, MQ8303, MQ8305, MQ8306, MQ8308, MQ8312; MQ9312.
- **Template members:** `entity.revisions` with `levels` (`number`, `name`, `format`, `values`, `first`, `skip`, `width`, `start_empty`, `examples`), `separator`, `first_revision`, `bound_attribute`, `lifecycles` (`lifecycle`, `on_revise`, `state`, `from`), `attributes` (`attribute`, `on_revise`, `from`), `links` (`end`, `on_revise`, `from`); `transition.next_revision`.
- **API:** `revisions` on the entity document and record; `nextRevision` on the transition document and record; the trace's `revision` (`from`, `to`, `level`).
- **In the engine:** `Revisions/RevisionSequence.cs` and `Revisions/RevisionCoverage.cs`; the refusal constant `Refusals.Revision`.
- **In generated code:** none that runs; the skip reason in `csharp-dapper`'s scenario tests.
- **In the editor:** Revisions, Levels, Values (Letters, Numbers, A list), First, Skip, Width, Starts empty, Separator, Held in, Preview, When revised (Start over, Keep; Copied, Reset, Cleared; Stay, Move, Duplicated), Add revisions, Next revision, Revise at, Revision (Level, Name), Expect revision.
- **Names a builder adds** follow these patterns and are listed in the pull request and the release notes for the review to settle.

## 17. Tests

- **The shared fixture** (`tests/fixtures/revisions/sequence-cases.json`), in the first contract commit, since the editor agent builds its parity test on it. It feeds `RevisionSequenceTests` and the editor's parity test, so the two can't drift. It holds every case of section 6:
  - letters: `Z` then `AA`, `AZ` then `BA`, `ZZ` then `AAA`; skips at any position (`H` then `J`, `AH` then `AJ`; `I` and `AI` not values); `first` after `A`, and a value before it not a value; `first` by default when `A` is skipped; a lower-case letter not a value. Numbers: from 1 and from 0; width 2 (`01` a value, `1` not, `99` then `100`, `0100` not); no width (`01` not a value). Lists: order; the end refused; a value with a space read exactly;
  - `startEmpty` on level 1 (`""`; level 1 gives `A`; level 2 gives `A.1`), on level 2 (`A`, `A.1`, then level 1 gives `B`), on levels 2 and 3 (`A` at level 3 gives `A.1.1`); a two-level sequence with none (`A.1`, `B.1`; `A` not a value); separators `.`, `-` and `""` (`A1`; `A12B` with three levels);
  - reading: too many parts, too few, an empty part (`A..1`), a part not a value, `""` in a sequence whose level 1 isn't `startEmpty`; order: `Z` before `AA`, `A` before `A.1`, `A.9` before `A.10`, `A.3` before `B`, `""` first, a list by position;
  - the next value at each level; chosen names: later, equal, earlier, not formable, skipping ahead; `examples`, with skips and a list shorter than five.
- **`RevisionCoverageTests`:** `entity.revisions` through bases (a derived entity's levels replacing its base's, a block without levels inheriting them, an entry replacing its base's, `copy` and `restart` undoing a base's entry, `from`), a carried stereotype attribute named by the carrier, the fallback filled in for every governing lifecycle, and null without a block.
- **`RevisionRuleTests` (new):** failing and passing cases for every MQ8301 message (a family naming no levels included), MQ8303, MQ8305 (each case of decision 11), MQ8306, MQ8308, every MQ8312 case, MQ2020 on entries, MQ9802's new case, every new MQ9305 case, MQ9312. Each incremental dependent of section 7. Today's models give the same findings and messages.
- **Simulation, revising:** a revise verb step at each level and with a chosen name; refusal `revision` at a list's end and for an earlier chosen name, with `refusedBy`, before any before action runs; its rank against a check, a gate and a guard; the scratch copy holding the next value and the reset values; row 7b in `Select`, `SelectAt` and `HandleTime`; the type's reset and cleared values written, with a before action's value winning, and listed in the trace's `values`; `reset` with a default, without one, and with a `defaultExpression` (the note); an emptied required value (the note); before actions written; a replace with a block and without one; thawing; links untouched, with the note; a revise move and a promote with `nextRevision`, each moving after the exit actions; a promote carrying nothing over; two parallel transitions that revise at different levels move once; a sub-process's revise move moves nothing; a bound attribute read by a guard before and after; a bound attribute's default as the start; no subject object (today's handling, with the note).
- **Simulation, each lifecycle:** the running lifecycle on a verb step with `restart`, with a named state (entered with its ancestors and default children) and with `keep` (nothing moves, approvals and signatures collected stay, an ended chart stays ended and the step is still accepted); the fallback `restart` when the type says nothing; the same outcomes whichever lifecycle runs; a revise from a final state that starts the chart again; a deleted object and an ended orchestration still refused; on a restart, exit actions not run, discarded approvals and signatures audited, timers, pending invokes and sub-process children dropped and the new states' started, history, acts and the queue cleared, the person who revised recorded after, the context back to the start values, with-change actions before an `always` move, every region restarted; a revise move keeping its lifecycle's other regions; other lifecycles taking `restart`, `keep` and a named state, in rights, in trigger points (with a cached state from before the revise), in the row's `$states` and in a guard's `in_state`; a lifecycle's bound state attribute unchanged; the trace's microstep (none for `keep`), `changed`, `final` and `revision`.
- **Scenarios:** `expect.revision` on a step that moved the revision and on one that didn't; an event step's chosen name, checked before the run and ignored with a note; recording, with reset values in `expect.values`; a model with no block replays unchanged.
- **References and deletes:** `get_references` pointers into the block; names in batches; deletes of a bound attribute, and of a lifecycle, state, attribute and relation that an entry names, under both choices; the index format `e14` unchanged.
- **Resolver and keys:** each member, records left out when unset; `entity.revisions` for the drawing and the invoice of section 6; stale tests for a base's block changing, a new lifecycle that starts governing the entity, and a new relation that adds an end at a base; a scenario with a revise event replays again when its subject's type gains a block and when the block changes; no key added without a block; `export_process` then `import_process` keeps `nextRevision`. Each stale test is checked to fail without its fix.
- **Packs:** a `process-docs` golden with two types, both kinds of move, and the When revised tables; the no-change goldens of section 13 for both packs, with a revise event and no block; a `csharp-dapper` golden where a revising scenario's generated test is skipped with its reason. The existing goldens pass untouched.
- **Editor:** unit tests for `revisionSequence.ts` against the fixture, the Revisions tab's conversions (levels from a base, replacing them, rows from a base, replacing one, the empty tab), the level picker's labels, and the scenario fields; the mock's restart and outcomes against the engine's on one shared case (`tests/fixtures/revisions/restart-case.json`, a scenario with its expected trace, which the simulation agent commits first); one end-to-end test that adds a block, gives a move the next revision, runs a scenario and sees the revision.
- **Acceptance,** on an invented model: drawings with letters and numbers, released by a promote that gives the next letter, revised from Released into In Work at the minor level, frozen on release and revised while frozen; a scenario that releases twice and revises once, with a chosen name refused for going back; one where the release lifecycle ends at Released and a revise starts it again; the invoice of section 6, whose delivery lifecycle keeps its state and whose approver resets; `PurchasedPart` inheriting `Part`'s levels.

## 18. Tasks, build plan and estimate

**One release, 0.23.0** (the owner, 2026-11-09): one contract, one build in waves, one review, one docs pass, one browser pass and one pull request.

### Tasks

| Wave | Group | What |
| --- | --- | --- |
| 1 | Contract and sequence | The schemas, `openapi.yaml` and rule texts; the fixture; `RevisionSequence` with the written form |
| 1 | The block and coverage | The block's record, references, cascade and deletes; `RevisionCoverage`: `entity.revisions` through bases, with its tests |
| 1 | Editor, part one | The Revisions tab's levels, separator, held-in picker and preview; `revisionSequence.ts` and its parity test |
| 2 | Rules | MQ8301, MQ8303, MQ8305, MQ8306, MQ8308, MQ8312, the changed rules MQ2020 and MQ9802, incremental dependents (the MQ9305 scenarios of section 7 included); MQ9305's new cases belong to the simulation agent |
| 2 | Simulation and scenarios | Row 7b, revise steps and moves, each lifecycle's outcome, the type's values, the replace and before actions, refusal `revision`, the trace, `expect.revision`, recording, scenario objects, MQ9305's new cases, MQ9312 |
| 2 | Resolver, records, tools and packs | The members, keys, records, tool texts; the `process-docs` page; `csharp-dapper`'s test skip |
| 2 | Editor, part two | The When revised tables, the transition inspector, column and chart label, the scenario fields, quick fixes; the mock's references, cascade, rules and stepper with section 9's steps |
| 3 | Docs | User guide (a Revisions section with the written form and the type's rules, the guide notes of decisions 8, 21 and 33), the processes guide, scenarios, releases, modeling skill, conventions resource, capabilities rows, `next.md` |

For a person who knows the code, with tests and a review, about nine days: contract and sequence a day and a half; the block and coverage half a day; rules three quarters; simulation and scenarios two and a quarter; resolver, records, tools and packs one; the editor two and a quarter over its two parts; docs half.

**Checklist for every group.**
- **The new keys:** the entity record in `Model/` (`Revisions`), `Model/Processes.cs` (`ProcessTransition.NextRevision`, `ScenarioObject.Revision`, `ScenarioStep.Revision`, the expectation); `Loading/ChangePlanner.Cascade.cs` (the bound attribute and the entries); `get_references`.
- **Processes:** `Processes/StatechartInterpreter.cs` (`HandleVerb`, `Microstep`, selection, the restart beside `Start` and `StartInstance`, `:157, 1477`), `Processes/StatechartInterpreter.Triggers.cs` (`Replace`), `Processes/RunRights.cs`, `Processes/RunTriggers.cs`, `Processes/Participants.cs`, `Processes/ScenarioObjects.cs`, `Processes/VerbStepFields.cs`, `Processes/StepTrace.cs` (`Refusals`), `Processes/ScenarioReplayer.cs`, `Processes/ProcessApi.cs`, and the exporter and importer (`nextRevision` round-trips as a property).
- **Resolver, keys and records:** `Resolution/ResolvedProcesses.cs`, `Resolution/ResolvedConceptual.cs` (`REntity`), a new `Resolution/ResolvedRevisions.cs`, `Editor/ResolvedRecords.cs`, the replay keys added per step beside the rights keys (`Resolution/ResolveRun.Processes.cs:1483`).
- **Tools:** `src/Maquettiste.Cli/Mcp/ModelTools.cs`, `ProcessTools.cs`, `Agent/tools.json`, `src/Maquettiste.Functions/_functions/`, owned by the wave 2 resolver agent.
- **Contracts:** the schema copies, `openapi.yaml` and its generated files. `validation-rules.json` is written only by the fixed command.
- **Test harness:** `RenderKit.WithSettings` and `ResolvedCoverageTests` (`tests/Maquettiste.Engine.Tests/Resolution/ResolvedCoverageTests.cs`).
- **The editor:** `npm run typecheck` (`tsc -b`) and `npm run lint` before handing back.

### Size

With tests, without pack expected files: **about 8,000 lines (7,500 to 9,000),** about half the reworked design's 15,400 to 17,000.

| Group | Reworked design | This shape | Why |
| --- | --- | --- | --- |
| Contract (schemas, openapi, rule texts, fixture) | 500 | 350 | No kind schema; one block on the entity |
| The kind, or the block's record | 350 + 300 tests | 150 + 150 | No kind; the record, cascade and references remain |
| Sequence and written form | 450 + 550 | 400 + 500 | No `last`, no integer form |
| Coverage through bases | 230 + 270 | 180 + 200 | No nearness, no stereotype path |
| Rules | 700 + 750 | 450 + 500 | Seven ids, three touched rules |
| Simulation and scenarios | 1,000 + 1,150 | 750 + 850 | No links in a run; the rest as designed |
| Resolver, keys, records, tools | 550 + 450 | 350 + 250 | No naming key, no `ImpactKeys` case, fewer members |
| `process-docs` | 180 + 100 | 150 + 80 | One page, per type |
| `csharp-dapper` | 60 + 60 | 60 + 60 | The test skip |
| Editor, mock, editor tests | 1,250 + 950 + 1,050 | 900 + 600 + 700 | One tab instead of a folder and an editor; a smaller stepper |
| Docs | 450 | 350 | |
| Readings, links in a run, edits after release | 4,400 to 5,000 | 0 | Deferred (section 14) |

### How it gets built

**Agents.** Each is capped at about 100 steps or 200k context, and kept tight: one area, one short brief, the exact files. **Opus** builds the engine and the pack page, and fixes them. **Sonnet** builds the editor, the mock and the docs, runs the browser pass, and fixes the editor and docs. **The review runs on Fable**, the owner's choice. At most three agents run at once, in their own worktrees fast-forwarded to the branch, merged by patch.

| Wave | Agent | Model | Steps | Starts from |
| --- | --- | --- | --- | --- |
| 1 | Contract and sequence, **contract first**: the schemas, all `openapi.yaml` changes, rule texts and the fixture in the first commit; then `RevisionSequence` | Opus | About 90 | `main` at 0.22.0 |
| 1 | The block's record, references and cascade; the new keys in `Model/Processes.cs`, with their loading and the exporter and importer property; `RevisionCoverage` and its tests | Opus | About 55 | The contract commit |
| 1 | Editor and mock, part one | Sonnet | About 70 | The contract commit |
| 2 | Rules | Opus | About 60 | The wave 1 engine agents' finish |
| 2 | Simulation and scenarios, through `Processes/ProcessApi.cs`, with MQ9305's new cases (`Processes/VerbStepFields.cs`) and MQ9312 (`Processes/ScenarioReplayer.cs`) | Opus | About 100, with a fresh agent for the rest if it runs over | The wave 1 engine agents' finish |
| 2 | Resolver, keys, records, tool texts, the `process-docs` page and `csharp-dapper`'s test skip. **Owns the tool files** | Opus | About 80 | The wave 1 engine agents' finish |
| 2 | Editor and mock, part two | Sonnet | About 90 | Wave 1's editor agent, in the first free slot |
| 3 | Review of the whole branch | Fable | About 70 | Every builder's finish |
| 3 | Docs | Sonnet | About 40 | Beside the review |
| 3 | Browser pass | Sonnet | About 45 | Beside the review |
| 4 | Fixers: engine and the pack page (Opus); editor and mock, and docs (Sonnet) | Opus, Sonnet | About 30 each | The review's and the browser pass's findings |
| 4 | A short second browser pass, only if the first found blocking problems | Sonnet | About 20 | The editor fixer's finish |
| 4 | The "Version 0.23.0" commit, the generated files, one push, the pull request, CI | | | The fixers' finish |

- **The shared restart case.** The simulation agent commits `tests/fixtures/revisions/restart-case.json` (a scenario with its expected trace) as early as it can; the editor agent's mock test reads it. If the case is not there in time, the wave 4 editor fixer adds the test.
- **No checkpoint.** The whole sits well inside the 500M cap; the figure is reported with the pull request. **A design gap found while building** stops the work, and the owner is asked.
- **One pull request into `main`,** with the contract, engine, editor, pack, docs and fix commits, and the "Version 0.23.0" commit; no separate version pull request. The tag waits for the owner.
- **Pushed once,** when the build, the fixes and the Version commit are done; CI runs on that push. Only a CI failure brings a fixer and a second push.
- **Generated files** (the editor's API types, the recorded rule list, the sample app's copies) are written only by the fixed commands, after each merge into the branch. Pack expected files stay with the builder, so their changes are reviewed.
- **Fixers** are fresh agents with the exact findings.

### Estimate

**Where the figure comes from.** The reworked design's itemized figures, less what this shape drops, read against the calibration line of the earlier items: about 70M cached re-reads before size counts and about 21M per thousand lines, with items whose weight is in the engine and simulation 54M to 68M below the line and items with canvas work above it. At 8,000 lines the line gives about 240M; this item is mostly engine and simulation with one editor tab and one small chart label, so about 220M.

| | Wall time | Output | New input | Cached re-reads |
| --- | --- | --- | --- | --- |
| Wave 1 (contract and sequence, the block and coverage, editor part one) | About 0.8 hour | 0.22M | 0.8M | 50M |
| Wave 2 (rules, simulation, resolver and the pack page, editor part two) | About 1.3 hours | 0.5M | 1.7M | 110M |
| Wave 3 (docs, browser pass; the review beside them) | About 0.6 hour | 0.09M | 0.35M | 24M |
| Wave 4 (fixers, Version commit, push, CI) | About 0.6 hour | 0.14M | 0.5M | 36M |
| **In all, Opus and Sonnet** | **About 3.3 hours (3 to 3.5)** | **About 0.95M** | **About 3.35M** | **About 220M (180M to 260M)** |
| The review on Fable, its own allowance | Within wave 3 | About 0.05M | About 0.18M | About 14M (12M to 17M) |
| Everything, Fable included | | About 1.0M | About 3.5M | About 234M (192M to 277M) |

**Lines:** about 8,000 (7,500 to 9,000). **Against the reworked design:** about 405M (370M to 455M) and 5.6 hours, with two more builders (expressions, simulation part 2), one more editor wave and a checkpoint.

**What earlier items cost,** for calibration: trigger points part 2 (about 8,000 lines, 173M, 3 hours), sign-off part 1 (10,800, 247M, 3.5), sign-off part 2 (4,500, 194M, 3), several types part 1 (8,500, 296M, 3) and part 2 (12,500, 388M against an estimate of 335M, 3.5). The nearest are the two engine-heavy ones, which this item resembles.

**The deferred items, if asked later:** the readings about 1,500 to 2,000 lines and 60M to 80M as their own small item; `onCopy` about 600 to 800 lines; `editRevises` about 900 to 1,100; links in a run come with "Scenarios across objects". Built separately they cost a little more than inside this item (a contract, a review and a release each), which is the price of not building them before a team asks.

## 19. What the independent checks changed, and the owner's choice

**The first check** read the first draft against the code at 0.22.0, the research, part G and the B and C rows of the yardstick, and the designs it builds on. **Verdict: build after fixes,** with medium-high confidence. It found **3 blocking, 12 should-fix and 16 minor** problems, and needed no new research. Every finding was applied, or fell away with a later owner answer. What it verified is carried into this shape as written.

| Finding of the first check | How it stands in this design | Where |
| --- | --- | --- |
| **B1.** The written form and empty levels were not defined | The written form: filled levels first, `startEmpty` carried down (an MQ8301 case), writing, reading, padded numbers only, letters by length then alphabet, the end of a list as its last; levels that are never empty must be filled. Every case is in the fixture | Section 6; decisions 3 to 7; section 17 |
| **B2.** Starting over was under-specified, and refused after a chart ended | The numbered procedure: accepted after the end; every instance member cleared or kept; one macrostep; leaving runs `Cancel`, so discarded approvals and signatures are audited; other lifecycles' bound state attributes keep their values; the two missed readers; the states cache | Decisions 38, 39; section 9 |
| **B3.** Output changed for projects with a revise event and no scheme | Both packs change only when a block exists; no-change goldens with a revise event and no block | Sections 11, 13, 17 |
| **S1.** A replace on `revise`; before actions not written | Rows 8 to 10 write to the subject as `modify` does, and a replace's sets apply on top of the carried-over values | Decision 16; section 9 |
| **S2.** Where the `revision` refusal sits | Row 7b, before the before actions, in selection and verb steps, ranked between a gate and a check; the scratch copy holds the next value | Decision 22; section 9 |
| **S3.** Replays went stale when a scheme started covering a subject | Now without a naming key: a scenario keys on its subject's entity and bases, and the block lives in the entity's file, so adding one replays it | Decision 42; section 8 |
| **S5, S6, S7.** A label's earlier revision; several revising transitions, sub-processes, other regions; when within a move | A label makes a new revision in place with no state of its own; one revision per microstep at the most significant level; sub-processes never revise; a revise move keeps its other regions; after the exit actions, before the transition actions | Decisions 18, 19 |
| **S8.** Which rules with no subject object; the bound attribute's default | No subject object keeps today's handling, with a note; a default counts as given; MQ8305 covers a default outside the sequence | Decisions 11, 38 |
| **S9.** Yardstick rows deferred without the owner; an unasked rule | The owner decided: no saves, no warning; MQ8307 is not used | Decisions 8, 24, 25 |
| **S4, S10, S11, S12.** The generated runtime; `duplicate` against a frozen object; an edit in a frozen state; the `atRevision` node | Fell away: packs are examples; freezing against links is the team's; `editRevises` and the readings are deferred | Decisions 31, 43; section 14 |
| **Minor 1 to 7, 13, 14, 16.** The step key; `expect.revision` on any step; `level` null for a name; when an event step's name is checked; names against the latest; `first` when `A` is skipped; frozen links; reordering levels; existing scenarios when revisions are added | Kept as applied: a step's `revision` is a name or a level; any step may expect; the guide notes; release notes and `verify_scenarios` | Decisions 3, 14, 21, 31, 40; section 13 |
| **Minor 8 to 12, 15.** MQ8307; the index format; incremental dependents; agent ownership; the fixture's timing; the cost line | MQ8307 dropped; no index change at all; a scalar type's save added; the simulation agent owns MQ9305 and MQ9312; the fixture in the first contract commit; the calibration line | Sections 7, 10, 17, 18 |

**The second check** read a layered draft of what a revise carries over (rules on lifecycles, states, attributes, ends and the type, with a ranking). **Verdict: build after fixes,** medium-high confidence, with **1 blocking, 6 should-fix and 6 minor** findings. The owner's answer that the type alone holds the rules removed the layers, so its B1, S5 and Minor 1 and 2 fell away. Kept: `entity.revisions` lists one outcome per lifecycle, attribute and end, merged through bases (S1, with the keys on the bases' files, the governing lifecycles and the relations at the ends, and the stale tests); MQ8312's key case and the note on emptied required values (S2); a bound attribute named in the block is MQ8305, and a process's and a relation's saves check MQ8312 again (S4); only the entity editor shows the fields (S6); `keep` keeps approvals and signatures, said and tested (Minor 4); the size is itemized (Minor 6). Its S3 and Minor 5 were about `editRevises`, now deferred; its Minor 3 about `onCopy`, now deferred.

**The third check** (2026-11-09) read this simpler shape against the proposal, the two earlier checks and the code. **Verdict: build after fixes,** nothing blocking: 7 should-fix and 10 minor findings, all applied here. The invoice example's `lines` entry broke MQ8308 (now `documents`); the template members are objects and the records carry ids (section 8); a subject whose type has no `revisions` under a `nextRevision` move is said (decision 38, MQ9305); the process and scenario keys have an owner (wave 1); the editor and mock merge the block through one function, `resolveRevisions` (section 12); incremental validation reaches scenarios of derived entities and rules check the merged block (section 7); and ten small fixes: decision 1 against decision 9, a section number, the drawing text's level, MQ8305's derived and multi-valued cases, the replay key code lines (`TraceInputs`), MQ9305's owner, `first` for a list, ends of a relation with more than two ends, a round-trip test for `nextRevision`, and the shared restart case for the mock test.

**The owner's choice, 2026-11-09.** After the two checks the design had grown to a `revision-scheme` kind with subjects and a nearness rule, the type's `onRevise` and `onCopy`, `nextRevision`, `editRevises` on a state, three expression functions with their tree nodes, `earlierRevisions` in scenarios, links acted on in a run, twelve new rule ids, a naming key and about 16,000 lines. An independent proposal put the sequence on the type beside its rules and left out what only adds vocabulary to conditions or makes things execute. Offered both, the owner chose the simpler shape. **Dropped:** the kind, its folder, index row and localization; `subjects`; nearness and MQ8304; MQ8309; the naming key `rv:revisions` and its `ImpactKeys` case; `earlierRevisions`; links acted on in a run (the run notes what the type says). **Deferred, each additive** (section 14): the readings `is_latest` and `at_revision`, `onCopy`, `editRevises`, `last`, the integer bound attribute, a sequence carried by a stereotype. **Kept as the checks verified it:** the written form and the one sequence function with its fixture; the fallback; thawing; the per-lifecycle restart procedure; one revision per microstep; the refusal's place; the no-change gates and tests. The result is about half the size with the same meaning for a system that reads the model.

## Sources

The letters and their sources are those of `lifecycle-capabilities.md` (prior art table and sources). This item's extra reading, by letter, and kept to syntax and behaviour:
- **E:** the owner's syntax reference: a policy's `sequence`; a state's `revision` and `version` switches; `revise … [to REVISION_NAME]`; `copy … to NAME REVISION`; the selectables `revision`, `revisions[]`, `previous`, `next`, `first`, `last`; per relationship end, `revision` and `clone` with `none`, `float` and `replicate`; `revise` as a trigger event. Round 2: attribute reset switches on clone and revision, named only in a schema tool's change log (unsure).
- **W:** help on object versions (a revision letter and an iteration number; check-in adds an iteration; revise takes the latest iteration) and community answers on major and minor revisions. Round 2: the lifecycle template's revise setting per state; links carried forward on revise; no built-in per-attribute reset.
- **T:** round 2, support articles on deep copy rules per type, operation and relation, and on property copy per operation (unsure in places).
- **S:** help and articles on revision number components, "increment by" and "reset to" on transitions, and the local revision beside the revision variable.
- **V:** help on revision scheme administration (levels, delimiter, formats) and on lifecycle transitions that bump a level.
- **A:** community answers on relationship behaviour (Float, Fixed and their hard forms) and on generations and major revisions (unsure). Round 2: help on item behaviour, where a lifecycle state's behaviour beats the relation type's unless the relation type says Hard, and on a released item's next edit returning to the start state.
- **O:** round 2, help on changes that name an affected item's new revision and lifecycle phase.
- **D (added in round 2):** Serena Dimensions CM help on copying attributes between item revisions: per attribute, reset to the default, start from the previous revision, or one value for all revisions.
