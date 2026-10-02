// Incremental index patching (explorer-redesign.md 4.4, step 12): for every model.changed shape the mock emits (its
// wire rules in src/mocks/wire.ts: E5d summaries, the 200 KB cut with `truncated`), the patched index equals a
// refetched one, the patched tree equals a rebuilt one, and the patched search index equals a reloaded one.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { MAX_EVENT_BYTES, truncateChangeEvent } from "@/mocks/wire";
import { applyIndexPatch, createQueryClient, indexPatchOf, keys, patchIndex, patchesBetween, upsertIndexRow } from "@/api/queries";
import type { ChangeSet, ElementSummary } from "@/api/types";
import { indexChangesOf } from "@/realtime/events";
import { EXPLORERS, buildForest, forestOf, patchForest, visibleRows, type Forest } from "@/explorer/tree";
import { SearchIndex, encodeRows } from "@/search/engine";
import { newId } from "@/lib/ids";

type Json = Record<string, unknown>;
const everything = { has: () => true } as unknown as ReadonlySet<string>;

/** Every row of every explorer, fully expanded, with what a row shows; the headers and the related maps. */
function snapshot(forest: Forest) {
  const trees = EXPLORERS.map((explorer) =>
    visibleRows(forest, explorer, everything).map(({ key, depth }) => {
      const n = forest.nodes.get(key)!;
      return [depth, key, n.label, n.secondary, n.tooltip, n.count, n.errors, n.warning, n.icon, n.kind, n.id, n.home, n.target, n.load, n.sort];
    }),
  );
  const sorted = (m: Map<string, string[] | string>) =>
    [...m].map(([k, v]) => [k, Array.isArray(v) ? [...v].sort() : v]).sort((a, b) => (String(a[0]) < String(b[0]) ? -1 : 1));
  const r = forest.related;
  return {
    trees,
    headers: forest.headers,
    related: [r.relationsOf, r.endsOf, r.tablesOf, r.mappedBy, r.junctionOf, r.lookupOf, r.baseOf, r.derivedOf, r.seedsOf, r.mappingsOf].map((m) =>
      sorted(m as Map<string, string[] | string>),
    ),
    byId: [...forest.byId.keys()].sort(),
  };
}

function searchDocs(index: SearchIndex) {
  return JSON.stringify((index as unknown as { rows: unknown[] }).rows);
}

/** A mock backend with the index, a tree (expanded after every step), a tree never expanded, and a search index. */
function harness(scenario: "medium" | undefined) {
  const backend = new MockBackend(scenario ? { scenarios: [scenario] } : {});
  const qc = createQueryClient();
  qc.setQueryData(keys.index, backend.model.index());
  let rows = qc.getQueryData<readonly ElementSummary[]>(keys.index)!;
  let expanded = buildForest({ rows });
  let lazy = buildForest({ rows });
  const search = new SearchIndex();
  search.loadRows(0, encodeRows(rows));
  let seen = backend.realtime.published.length;
  const stats = { inPlace: 0, rebuilt: 0, events: 0 };

  /** Runs one write, applies its model.changed events, and checks every consumer against a fresh build. */
  const step = (write: () => void, expectInPlace?: boolean) => {
    write();
    const events = backend.realtime.published.slice(seen).filter((e) => e.event === "model.changed");
    seen = backend.realtime.published.length;
    expect(events.length).toBeGreaterThan(0);
    for (const e of events) {
      stats.events++;
      expect(patchIndex(qc, e.payload as ChangeSet)).toBe(true);
    }
    const next = qc.getQueryData<readonly ElementSummary[]>(keys.index)!;
    // The patched index is what a refetch answers.
    expect(next).toEqual(backend.model.index());
    const chain = patchesBetween(rows, next)!;
    expect(chain).not.toBeNull();
    const fresh = buildForest({ rows: next });
    let inPlace = true;
    for (const p of chain) {
      const a = patchForest(expanded, p);
      const b = patchForest(lazy, p);
      inPlace &&= !!a && !!b;
      expanded = a ?? buildForest({ rows: p === chain[chain.length - 1] ? next : applyIndexPatch(p.from, p.upserts, p.deleted) });
      lazy = b ?? buildForest({ rows: next });
      search.updateRows(encodeRows(p.upserts), p.deleted, p.moves);
    }
    if (inPlace) stats.inPlace++;
    else stats.rebuilt++;
    if (expectInPlace !== undefined) expect(inPlace).toBe(expectInPlace);
    expect(snapshot(expanded)).toEqual(snapshot(fresh));
    const reloaded = new SearchIndex();
    reloaded.loadRows(0, encodeRows(next));
    expect(searchDocs(search)).toBe(searchDocs(reloaded));
    rows = next;
  };
  const finish = () => expect(snapshot(lazy)).toEqual(snapshot(buildForest({ rows })));
  return { backend, qc, step, finish, stats, rows: () => rows, forest: () => expanded };
}

function edit(backend: MockBackend, id: string, mutate: (json: Json) => void) {
  const doc = backend.model.get(id)!;
  const json = structuredClone(doc.json) as Json;
  mutate(json);
  const result = backend.model.save(id, json, doc.hash);
  expect(result.outcome, JSON.stringify(result.diagnostics.map((d) => d.message))).toBe("saved");
}

for (const scenario of [undefined, "medium"] as const) {
  describe(`index patching (${scenario ?? "billing"} mock)`, () => {
    it("patches the index, the tree and search for every event shape, as a refetch and a rebuild would", () => {
      const h = harness(scenario);
      const { backend } = h;
      const rows = () => h.rows();
      const packages = rows().filter((r) => r.kind === "package");
      const forest = () => h.forest();
      const entitiesIn = (pkg: string) => rows().filter((r) => r.kind === "entity" && r.package === pkg);
      const lonely = (r: ElementSummary) => !forest().related.relationsOf.get(r.id)?.length && !forest().related.tablesOf.get(r.id)?.length;
      const home = packages.find((p) => entitiesIn(p.id).length >= 3)!;
      const away = packages.find((p) => p.id !== home.id && entitiesIn(p.id).length >= 1)!;
      expect(home && away).toBeTruthy();
      const entity = entitiesIn(home.id)[0];
      const relation = rows().find((r) => r.kind === "relation")!;

      // A change that leaves every row's place and label alone (a description).
      h.step(() => edit(backend, entity.id, (j) => (j.description = "Patched in place.")), true);
      // Rename: an entity (its relations' ends and navigation rows follow), a relation, a diagram.
      h.step(() => edit(backend, entity.id, (j) => (j.name = "AaaRenamedFirst")));
      h.step(() => edit(backend, relation.id, (j) => (j.name = "zzRenamedLink")), true);
      const diagram = rows().find((r) => r.kind === "diagram");
      if (diagram) h.step(() => edit(backend, diagram.id, (j) => (j.name = "An overview renamed")));
      // Tags (no project folder takes them): in place.
      const vocabulary = rows().find((r) => r.kind === "tag-vocabulary");
      const keys = vocabulary ? ((backend.model.get(vocabulary.id)!.json as Json).definitions as Json[]).map((d) => String(d.key)) : [];
      // A vocabulary edit leaves its row's shape alone: in place.
      if (vocabulary && !keys.length) {
        h.step(() => edit(backend, vocabulary.id, (j) => (j.definitions = [{ key: "patched" }])), true);
        keys.push("patched");
      }
      const tags = entity.tags.length ? [] : [keys[0] ?? "patched"];
      h.step(() => edit(backend, entity.id, (j) => (j.tags = tags)), true);
      // Add an entity, then move it to another domain, then rename it, then delete it.
      const created = newId();
      h.step(() => {
        const result = backend.model.create({ kind: "entity", id: created, name: "PatchCreated", package: home.id, abstract: true, attributes: [] });
        expect(result.outcome, JSON.stringify(result.diagnostics.map((d) => d.message))).toBe("saved");
      }, true);
      h.step(() => edit(backend, created, (j) => (j.package = away.id)), true);
      h.step(() => edit(backend, created, (j) => (j.name = "PatchMoved")), true);
      h.step(() => {
        const doc = backend.model.get(created)!;
        expect(backend.model.delete(created, doc.hash).outcome).toBe("saved");
      }, true);
      // A relation added between two entities, then removed.
      const [a, b] = entitiesIn(home.id);
      const link = newId();
      h.step(() => {
        const json = structuredClone(backend.model.get(relation.id)!.json) as Json & { ends: Json[] };
        json.id = link;
        json.name = "patchLink";
        json.package = home.id;
        json.ends.forEach((e, i) => {
          e.id = newId();
          e.entity = [a.id, b.id][i] ?? a.id;
        });
        const result = backend.model.create(json);
        expect(result.outcome, JSON.stringify(result.diagnostics.map((d) => d.message))).toBe("saved");
      });
      h.step(() => {
        const doc = backend.model.get(link)!;
        expect(backend.model.delete(link, doc.hash).outcome).toBe("saved");
      });
      // A change from the disk (source disk), and a delete that edits its referrers (several rows in one event).
      h.step(() => backend.model.externalEdit(entity.id, (j) => void (j.name = "FromDisk")));
      // (An entity only diagrams or optional fields refer to, or a relation: the billing queries name the relations' ends, so the delete
      // takes the queries with it and edits the diagram.)
      const referenced = rows().find((r) => (r.kind === "relation" || (r.kind === "entity" && lonely(r))) && (backend.model.references(r.id)?.length ?? 0) > 0);
      expect(referenced).toBeTruthy();
      if (referenced) {
        const doc = backend.model.get(referenced.id)!;
        h.step(() => {
          const result = backend.model.delete(referenced.id, doc.hash, "delete-dependents");
          expect(result.outcome).toBe("saved");
          expect(result.changes!.changed.length).toBeGreaterThan(0);
        });
      }
      // A batch: several creates and saves in one event.
      const batchIds = [newId(), newId()];
      h.step(() => {
        const result = backend.model.batch({
          operations: batchIds.map((id, i) => ({ op: "create", element: { kind: "enum", id, name: `PatchEnum${i}`, package: away.id, members: [] } })),
        });
        expect(result.status).toBe(200);
      });
      // A new domain (a structural change the tree rebuilds for) and a lonely entity moved into it.
      const domain = newId();
      h.step(() => void backend.model.create({ kind: "package", id: domain, name: "PatchDomain" }), false);
      const mover = entitiesIn(home.id).find(lonely);
      if (mover) h.step(() => edit(backend, mover.id, (j) => (j.package = domain)));
      h.finish();
      expect(h.stats.inPlace).toBeGreaterThanOrEqual(8);
      expect(h.stats.rebuilt).toBeGreaterThanOrEqual(1);
    });
  });
}

describe("index patch fallbacks and order", () => {
  it("refuses a truncated event and a change without a summary; the caller refetches with the ETag", () => {
    const backend = new MockBackend();
    const qc = createQueryClient();
    const rows = backend.model.index();
    qc.setQueryData(keys.index, rows);
    const set: ChangeSet = {
      changed: rows.slice(0, 3).map((r) => ({ id: r.id, kind: r.kind, path: r.path, hash: r.hash, summary: r })),
      deleted: [],
      source: "editor",
      truncated: false,
      isEmpty: false,
    };
    expect(indexChangesOf(set)?.upserts).toHaveLength(3);
    const cut = truncateChangeEvent(set, 400);
    expect(cut.truncated).toBe(true);
    expect(indexChangesOf(cut)).toBeNull();
    expect(patchIndex(qc, cut)).toBe(false);
    expect(patchIndex(qc, { ...set, changed: set.changed.map(({ summary: _summary, ...c }) => c) })).toBe(false);
    expect(qc.getQueryData(keys.index)).toBe(rows);
    expect(MAX_EVENT_BYTES).toBe(200 * 1024);
  });

  it("keeps path order, registers each patch, and chains patches for the tree and the search client", () => {
    const rows = new MockBackend().model.index();
    const entity = rows.find((r) => r.kind === "entity")!;
    const moved = { ...entity, path: "zzz/last.json", name: "Moved" };
    const dropped = applyIndexPatch(rows, [moved], [rows[1].id]);
    expect(dropped.at(-1)).toBe(moved);
    expect(dropped).toHaveLength(rows.length - 1);
    expect(indexPatchOf(dropped)).toMatchObject({ from: rows, deleted: [rows[1].id], moves: [[moved.id, null]] });
    expect(applyIndexPatch(dropped, [], ["missing"])).toBe(dropped);
    const next = applyIndexPatch(rows, [moved], []);
    const qc = createQueryClient();
    qc.setQueryData(keys.index, next);
    upsertIndexRow(qc, { ...moved, name: "Again" });
    const third = qc.getQueryData<readonly ElementSummary[]>(keys.index)!;
    expect(patchesBetween(rows, third)).toHaveLength(2);
    expect(patchesBetween(third, rows)).toBeNull();
    // forestOf follows the chain from its memoized rows instead of rebuilding.
    const first = forestOf({ rows });
    const patched = forestOf({ rows: third });
    expect(patched).not.toBe(first);
    expect(patched.nodes).toBe(first.nodes);
    expect(snapshot(patched)).toEqual(snapshot(buildForest({ rows: third })));
  });
});

describe("index patching property (medium mock)", () => {
  // EX 6: a patched forest equals a rebuilt one over 1,000 pseudo-random changes (seeded, so a failure replays).
  it("patch == full rebuild over 1,000 random changes", () => {
    let seed = 0x5eed;
    const random = () => (seed = (seed * 1_103_515_245 + 12_345) & 0x7fffffff) / 0x80000000;
    const pick = <T>(list: readonly T[]): T => list[Math.floor(random() * list.length)];
    const backend = new MockBackend({ scenarios: ["medium"] });
    const qc = createQueryClient();
    qc.setQueryData(keys.index, backend.model.index());
    let rows = qc.getQueryData<readonly ElementSummary[]>(keys.index)!;
    let forest = buildForest({ rows });
    let seen = backend.realtime.published.length;
    const packages = rows.filter((r) => r.kind === "package").map((r) => r.id);
    const entities = rows.filter((r) => r.kind === "entity").map((r) => r.id);
    const relations = rows.filter((r) => r.kind === "relation").map((r) => r.id);
    const created: string[] = [];
    let rebuilt = 0;
    for (let i = 0; i < 1_000; i++) {
      const op = random();
      if (op < 0.25) edit(backend, pick(entities), (j) => (j.description = `Random ${i}.`));
      else if (op < 0.45) edit(backend, pick(entities), (j) => (j.name = `Rnd${i}`));
      else if (op < 0.55 && relations.length) edit(backend, pick(relations), (j) => (j.name = `rnd${i}`));
      else if (op < 0.75 || !created.length) {
        const id = newId();
        const result = backend.model.create({ kind: "entity", id, name: `RndNew${i}`, package: pick(packages), abstract: true, attributes: [] });
        expect(result.outcome).toBe("saved");
        created.push(id);
      } else if (op < 0.88) edit(backend, pick(created), (j) => (j.package = pick(packages)));
      else {
        const id = created.splice(Math.floor(random() * created.length), 1)[0];
        expect(backend.model.delete(id, backend.model.get(id)!.hash).outcome).toBe("saved");
      }
      const events = backend.realtime.published.slice(seen).filter((e) => e.event === "model.changed");
      seen = backend.realtime.published.length;
      for (const e of events) expect(patchIndex(qc, e.payload as ChangeSet)).toBe(true);
      const next = qc.getQueryData<readonly ElementSummary[]>(keys.index)!;
      for (const p of patchesBetween(rows, next) ?? []) {
        const patched = patchForest(forest, p);
        if (!patched) rebuilt++;
        forest = patched ?? buildForest({ rows: applyIndexPatch(p.from, p.upserts, p.deleted) });
      }
      rows = next;
      // A full comparison every 10 changes (the last one included).
      if (i % 10 === 9) {
        expect(rows, `index after change ${i}`).toEqual(backend.model.index());
        expect(snapshot(forest), `forest after change ${i}`).toEqual(snapshot(buildForest({ rows })));
      }
    }
    console.info(`1,000 random changes over ${rows.length} rows: ${rebuilt} patches fell back to a rebuild`);
  }, 300_000);
});
