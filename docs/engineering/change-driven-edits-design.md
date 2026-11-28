# Change-driven edits: an edit costs what it changes

Status: design, revision 2, 2026-11-28. The owner asked for it on 2026-11-27, after measurements showed that one entity
edit in a large model costs the whole model ("Models should not require that kind of memory"). On 2026-11-28 the owner
approved the order in section 9 (revision 2), which replaces the plan in sections 4 and 7 where they differ. Ruling R1
(section 8) is still open and is needed before the skip check is built.

## 1. The problem

When a team edits one entity and the editor or `watch` generates again, only rendering is limited to what changed.
Every other stage still covers the whole model or every output. Measured on the benchmark model (5,000 entities,
61,109 model files, 100,050 outputs) on 2026-11-27, one edit of one attribute of one entity:

| Stage | Covers | Allocated |
|---|---|---:|
| Rescan (list, stat, assemble, index) | every model file | 265 MB |
| Validate | every element | 621 MB |
| Read manifests | every output path, parsed from disk | 265 MB |
| Resolve | every element, built again from nothing | 1,042 MB |
| Plan units | every unit | 58 MB |
| Skip check | every unit's input hashes and outputs | 186 MB |
| Render | the 57 units that changed | 18 MB |
| Write outputs | the 12 files that changed | 46 MB |
| Save manifests | every output path, formatted again | 121 MB |
| Save unit states | every unit, encoded again | 185 MB |
| **Total** | | **about 2.8 GB** |

The wall time is about 1.9 s on a quiet machine and 3.6 s on a busy one; most of the spread is garbage collection. The
live heap is about 2 GB. Revision 1 said most of it was the previous resolved model kept alive beside the new one; the
measurement in section 9 shows the previous model is about 240 MB of it, and the rest is one model's own data. The cost grows with the model: about 0.56 MB per entity, so a few hundred entities stay under a second, but the
waste is the same in kind at any size.

The code already knows what changed. The rescan works out the list of changed files (`ModelStore.RescanAsync` returns a
`ChangeSet`), but `GetSnapshotAsync` drops it and returns only the new snapshot, so every later stage starts from the
whole model.

## 2. The goal

- One entity edit in the 5,000-entity model allocates **under 100 MB** (target 30 to 60 MB) and takes **under 0.5 s**.
- The live heap after an edit stays close to one model, not two, and what one model holds is measured and reduced
  (section 9, step 4).
- Outputs, diagnostics, manifests and unit states stay **byte-identical** to a cold run of the same model. This is the
  rule that decides every step: where a stage cannot be sure what an edit reaches, it does the whole model, as today.
- A cold run (first open, `generate` from the command line, `--check`) is unchanged. It covers the whole model by nature.

## 3. The principle

The list of changes flows through every stage. Each stage keeps its previous result and patches it from the list,
instead of building it again. A stage that receives no list, or a list it cannot handle (settings changed, a pack
changed, a kind added), runs whole, as today. Every step below is safe to ship on its own, because each falls back to
today's behaviour.

## 4. The steps

### Step 1. Quick wins (2 to 3 days)

1. **Release the old model before resolving the new one.** The store and the last generation run each hold the previous
   resolved model and snapshot until the new resolve finishes (`ModelStore.ShareModel`, the run's own copy). Drop both
   references as soon as the new snapshot exists. Halves the live heap; no change to results.
2. **Pass the list of changes through.** `GetSnapshotAsync` returns the snapshot with the `ChangeSet` that produced it
   (an internal overload; the public signature stays). Steps 2 to 4 read it.
3. **Manifests and unit states in memory.** The store keeps what it last read and wrote, per pack, and reads the files
   again only when their stat changes behind it (another process, a hand edit). Today every run parses the manifests from
   disk, and reads each pack's unit-state file from disk twice (beside resolve and in the skip check), comparing it byte by
   byte with what the store last decoded.
4. **Write only what changed.** Saving a manifest or the unit states encodes and compares every entry, then writes the
   file only if it differs. Keep the encoded bytes of each pack's entries and patch only the changed units' entries, so
   the write costs the change, not the pack.

Expected: about 600 MB less per edit (manifests read and save, unit states save) and half the live heap.

### Step 2. Validate what the edit reaches (3 to 4 days)

The validator already checks a list of elements: the editor's save uses it (`ValidationScope.ElementIds`). It picks the
changed elements, their referrers and their "conflict peers" (elements whose rules compare with each other, such as two
tables with the same name in one schema).

- Keep the previous whole-model report, grouped by element.
- On an edit, validate the changed elements with that scope, and replace those elements' findings in the kept report.
- Run whole when the list holds settings, a kind's schema, a stereotype set, an extension script, or a removed or added
  file whose kind has model-wide rules (a new database, for example). The exact list comes from an inventory of every
  rule that reads more than its element and its referrers; the inventory is part of this step.

Expected: 621 MB down to a few MB for an entity edit.

### Step 3. Skip check by key (3 to 5 days)

Each unit records the keys it read (its dependency set). Today the skip check visits all 100,050 units, hashes their
inputs again and checks their outputs on disk.

- Build, once per run and kept between runs, a map from each key to the units that read it.
- An edit's changed keys name the units to look at; every other unit is skipped without being visited.
- Outputs on disk: see section 8, ruling R1.

Expected: 186 MB and most of the plan stage's per-unit work down to the units the edit reaches.

### Step 4. A cheaper rescan (1 to 2 days)

In the editor and `watch` the file watcher already reports which paths changed. Use that list instead of listing and
stat-ing all 61,109 files, and keep a full stat pass as the fallback (watcher overflow, a missed event, the command
line). Also cut the per-path string work in path classification, which accounts for most of the rescan's allocation.

Expected: 265 MB down to the changed files.

### Step 5. Resolve what the edit reaches (2 to 4 weeks)

The largest piece: resolve builds every resolved object again from nothing (1 GB). The design is already written in the
Resolution README ("Not built: reuse across an edit"): resolved objects split into an immutable inner layer that a run
can reuse and thin per-run views over it, with invalidation driven by the same dependency keys. About 25,000 lines
change; its own pull request and review.

Expected: 1 GB down to the objects the edit reaches.

### Stopgap, only if wanted (half a day)

After an idle moment the editor could ask the runtime to collect, so a large model's heap shrinks between edits. It hides
the symptom and fixes nothing; listed only because it is cheap. I do not recommend it once step 1 ships.

## 5. How it is proved

- **An edit sequence test**: add, change and remove an entity, a binding, a table, a relation mapping, a setting and a
  pack file, one at a time, in a fixture model. After each, an incremental run's outputs, diagnostics, manifests and unit
  states must equal a cold run's, byte for byte. Each step adds the stages it changes to the comparison.
- **The bench measures memory per edit**: the incremental run reports bytes allocated and live heap after, beside the
  time, with a budget (100 MB allocated) that gate 1 checks like the time budgets.
- The determinism, pack golden and integration tests run as today.

## 6. What changes for the team

- **In the editor**: nothing to learn. Generate after an edit, and `watch`, get faster and use far less memory on large
  models. No new setting.
- **Is it intuitive**: there is nothing new to understand; the tool simply stops doing work the edit did not cause.
- **Modeling and code generation value**: large models (thousands of entities) become practical to edit with generation
  running, which is the size Maquettiste is meant for. Outputs do not change.

## 7. Order, pull requests and release

| Pull request | Steps | Estimate |
|---|---|---|
| A | 1 (quick wins) | 2 to 3 days |
| B | 2, 3 and 4 | 7 to 11 days |
| C | 5 (resolve) | 2 to 4 weeks |

Each with a Fable review. Steps 1 to 4 together should bring an edit from about 2.8 GB to about 1.1 GB, almost all of it
resolve; step 5 brings it to the goal.

## 8. Rulings needed

- **R1. Hand-edited outputs.** Today every run checks every output file on disk, so a deleted or hand-edited output is
  written again on the next run. A key-driven skip check (step 3) would no longer look at outputs the edit does not reach.
  Recommended: in the editor and `watch`, look only at the outputs the edit reaches (the file watcher can report changes
  to output folders separately later); `generate` from the command line and the editor's "Generate everything" keep the
  full check. Alternative: keep checking every output's stat on every run (about 100 MB and 0.3 s at 100,000 outputs).
- **R2. Releases.** Recommended: release after pull request B (0.30.0), then again after C. Alternative: one release
  after C.
- **R3. Start.** Recommended: start pull request A now.

## 9. Revision 2 (2026-11-28): measured, re-planned, approved

**Estimates were in human days.** Revision 1's "days" were human-engineer days. Elapsed AI time is far shorter: retiring
projection, estimated at 14 to 17 days, went from its design commit to the 0.29.0 release in about 24 hours. Estimates
below are elapsed AI hours, CI and review included.

**Pull request A, built and measured** (bench, 5,000 entities, shared machine, two runs each):

| One edit | Before | After A |
|---|---:|---:|
| Allocated | 2,683 MiB | 2,258 MiB |
| Live heap at the next edit, before its resolve | about 2,090 MiB | about 1,850 MiB |
| Time | 3.6 to 4.1 s | 3.4 to 3.6 s |

A frees the previous model as soon as a new snapshot is published, keeps manifests and unit states in memory (read again
only when a file's stat changes), and makes the database view and the query and binding SQL previews use the store's
shared resolution: they used to validate and resolve the whole model on every call. The bench now reports allocated
bytes and live heap.

**Previews found to build the whole model.** The template preview, the seed rows preview, "explain this file" and the
full Problems check build the whole model once per edit (shared until the next edit); the three views above did so on
every call until A. Step 2 below gives previews the per-database resolve the engine already has.

**A resolve-reuse spike** (5,000 entities, the bench's edit; scratch code, nothing kept):

- The dependency keys already form a safe map of what an edit reaches: after a one-attribute edit, 440 of 854,409
  resolved objects have a changed key; a rename gave the same; no changed object was missed.
- Reusing the rest as they are is blocked by direct links: an unchanged object that links to a rebuilt one must be
  re-created, which is 91% of all objects; with links to owners and reverse indexes as id lookups, 35%; only with every
  link between elements as a lookup does it fall to the 440 (71 link fields on 23 types for the bench's features, 690
  fields on 147 types in all).
- About three quarters of what resolve allocates is short-lived working memory (collections and arrays 532 MB,
  enumerators and closures 156 MB, delegates 140 MB, against 154 MB of resolved objects and 279 MB live).
- Planning reads every element of every unit type, so building the resolved model on demand does not help generation;
  it does help previews.

**The order the owner approved:**

1. **A** (built; review, then pull request).
2. **B**: validate what the edit reaches, the skip check by key, a cheaper rescan, the resolver's working memory cut
   (about 1.17 GB to about 0.5 GB, byte-identical by construction), and previews on the per-database resolve. About 1 to
   1.5 days of elapsed AI time.
3. **Measure what one model holds** (about 1.8 GB live at 5,000 entities: snapshot, indexes, manifests, unit states) and
   why, with a plan to reduce it.
4. **Decide on reuse** (links as lookups with a per-run scope, plus a partial resolve): about 45 to 70 elapsed AI hours,
   against about 80 to 120 for the two-layer design in the Resolution README. Decided on the numbers after steps 2
   and 3.

