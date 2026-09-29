// The mock model's incremental index (src/mocks/model/modelIndex.ts) must give exactly what a
// from-scratch validation gives, after any sequence of writes, and answer owner and reference
// lookups the way the scans it replaced did.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import type { MockModel } from "@/mocks/model/store";

type Json = Record<string, unknown>;

function fromScratch(model: MockModel) {
  return model.diagnosticsOf(model.entries.values());
}

function entriesOf(model: MockModel, kind: string) {
  return [...model.entries.values()].filter((e) => e.json.kind === kind).sort((a, b) => (a.path < b.path ? -1 : 1));
}

function edit(model: MockModel, id: string, mutate: (json: Json) => void) {
  const doc = model.get(id)!;
  const json = structuredClone(doc.json) as Json;
  mutate(json);
  return model.save(id, json, doc.hash);
}

describe("mock model index", () => {
  it("matches a from-scratch validation after external edits that break and repair references and names", () => {
    const backend = new MockBackend();
    const model = backend.model;
    expect(model.validate().diagnostics).toEqual(fromScratch(model));

    const entities = entriesOf(model, "entity");
    const [first, second] = entities;
    // A duplicate name (MQ3001) on the second entity of the same package, then a dangling reference (MQ2001).
    model.externalEdit(second.id, (json) => {
      json.name = String(first.json.name);
      json.package = first.json.package;
    });
    expect(model.validate().diagnostics).toEqual(fromScratch(model));
    expect(model.validate().diagnostics.some((d) => d.rule === "MQ3001")).toBe(true);

    model.externalEdit(first.id, (json) => {
      json.package = "01J00000000000000000000000";
    });
    expect(model.validate().diagnostics).toEqual(fromScratch(model));
    expect(model.validate().diagnostics.some((d) => d.rule === "MQ2001")).toBe(true);

    // Removing an attribute that another element references makes the referrer dangle.
    const relation = entriesOf(model, "relation")[0];
    const target = String((relation.json.ends as Json[])[0].entity);
    model.externalEdit(target, (json) => {
      json.id = json.id as string;
      json.attributes = [];
    });
    expect(model.validate().diagnostics).toEqual(fromScratch(model));

    // A stereotype change re-checks every entry.
    const stereotype = entriesOf(model, "stereotype")[0];
    if (stereotype)
      model.externalEdit(stereotype.id, (json) => {
        json.appliesTo = ["relation"];
      });
    expect(model.validate().diagnostics).toEqual(fromScratch(model));
  });

  it("matches a from-scratch validation after saves, creates and deletes on the medium model", () => {
    let n = 0;
    const backend = new MockBackend({ scenarios: ["medium"], newId: () => `01M9${String(++n).padStart(22, "0")}` });
    const model = backend.model;
    const things = entriesOf(model, "entity");
    expect(edit(model, things[3].id, (json) => (json.description = "changed")).outcome).toBe("saved");
    expect(model.validate().diagnostics).toEqual(fromScratch(model));

    // A save that would introduce an error is refused and leaves the model (and its index) as it was.
    // Thing5 and Thing17 share a package (12 packages, round robin).
    const named = (name: string) => things.find((t) => t.json.name === name)!;
    const refused = edit(model, named("Thing5").id, (json) => (json.name = "Thing17"));
    expect(refused.outcome).toBe("invalid");
    expect(refused.diagnostics.some((d) => d.rule === "MQ3001")).toBe(true);
    expect(model.validate().diagnostics).toEqual(fromScratch(model));

    const created = model.create({
      kind: "entity",
      name: "Fresh",
      key: { attributes: ["01M9KEY0000000000000000001"] },
      attributes: [{ id: "01M9KEY0000000000000000001", name: "id", type: "uuid" }],
    });
    expect(created.outcome, JSON.stringify(created.diagnostics)).toBe("saved");
    expect(model.owner("01M9KEY0000000000000000001")?.id).toBe(created.id);

    // Deleting a relation drops it from the diagram that shows it (remove-references).
    const link = entriesOf(model, "relation")[0];
    const removed = model.delete(link.id, link.hash, "remove-references");
    expect(removed.outcome, JSON.stringify(removed.diagnostics)).toBe("saved");
    expect(model.validate().diagnostics).toEqual(fromScratch(model));
    expect(model.owner(link.id)).toBeUndefined();
    expect(model.owner(String((link.json.ends as Json[])[0].id))).toBeUndefined();
  });

  it("revalidates the elements under a moved domain when a domain vocabulary exists (MQ2008)", () => {
    const model = new MockBackend().model;
    const saved = (input: Json) => {
      const result = model.create(input);
      expect(result.outcome, JSON.stringify(result.diagnostics)).toBe("saved");
      return result.id!;
    };
    const home = saved({ kind: "package", name: "VocabHome" });
    const away = saved({ kind: "package", name: "VocabAway" });
    const moved = saved({ kind: "package", name: "VocabMoved", parent: home });
    const inner = saved({ kind: "package", name: "VocabInner", parent: moved });
    saved({ kind: "tag-vocabulary", name: "vocab-home-tags", package: home, definitions: [{ key: "home-only" }] });
    const tagged = saved({
      kind: "entity",
      name: "VocabTagged",
      package: inner,
      tags: ["home-only"],
      key: { attributes: ["01M9KEY0000000000000000002"] },
      attributes: [{ id: "01M9KEY0000000000000000002", name: "id", type: "uuid" }],
    });
    expect(model.validate().diagnostics.some((d) => d.rule === "MQ2008" && d.elementId === tagged)).toBe(false);

    // The entity references its own package, not the moved one, so only the chain change can reach it.
    model.externalEdit(moved, (json) => (json.parent = away));
    const after = model.validate().diagnostics;
    expect(after.some((d) => d.rule === "MQ2008" && d.elementId === tagged)).toBe(true);
    expect(after).toEqual(fromScratch(model));
  });

  it("answers owners and references from the index", () => {
    const model = new MockBackend().model;
    for (const entry of model.entries.values()) {
      const subs = ((entry.json.attributes as Json[] | undefined) ?? []).flatMap((a) => (typeof a.id === "string" ? [a.id] : []));
      for (const sub of subs) expect(model.owner(sub)?.id).toBe(entry.id);
    }
    const entity = entriesOf(model, "entity")[0];
    const refs = model.references(entity.id)!;
    const scanned = [...model.entries.values()].filter((e) => e.id !== entity.id && JSON.stringify(e.json).includes(entity.id)).map((e) => e.id);
    expect(new Set(refs.map((r) => r.fromElementId))).toEqual(new Set(scanned));
  });
});
