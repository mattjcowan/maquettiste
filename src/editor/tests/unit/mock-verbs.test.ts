// The mock backend's verbs (verbs-design.md): the vocabulary kind at its fixed path, own verbs as sub-elements and references,
// the index rows, rules MQ9701 to MQ9707, the batch operations rename-verb and remove-verb, and deletes.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { verbOperation } from "@/mocks/model/verbs";

type Json = Record<string, unknown>;

const ESCALATE = "01JVERB000000000000000E001";
const RAISE = "01JVERB000000000000000E002";
const VOCAB = "01JVERB000000000000000V001";

interface Setup {
  backend: MockBackend;
  process: string;
  event: string;
  operation: string;
}

/** A model with a process whose first event names `escalate`, an operation naming it too, and an own verb escalate. */
function setup(): Setup {
  const backend = new MockBackend();
  const row = backend.model.index().find((r) => r.kind === "process" && r.use === "lifecycle")!;
  const doc = structuredClone(backend.model.get(row.id)!.json) as Json;
  const events = (doc.events as Json[]) ?? [];
  expect(events.length).toBeGreaterThan(0);
  events[0].verb = "escalate";
  const saved = backend.model.save(row.id, doc, backend.model.get(row.id)!.hash);
  expect(saved.outcome).toBe("invalid"); // unknown verb until declared: MQ9701 introduced
  expect(saved.diagnostics.map((d) => d.rule)).toContain("MQ9701");
  const created = backend.model.create({ kind: "verb-vocabulary", id: VOCAB, name: "verbs", verbs: [{ id: ESCALATE, key: "escalate" }] });
  expect(created.outcome).toBe("saved");
  expect(backend.model.save(row.id, doc, backend.model.get(row.id)!.hash).outcome).toBe("saved");
  const op = backend.model.create({
    kind: "operation",
    id: "01JVERB000000000000000P001",
    name: "Escalate",
    effect: "change",
    verb: "escalate",
    outcomes: [{ id: "01JVERB000000000000000C001", name: "done" }],
  });
  expect(op.diagnostics.map((d) => d.message)).toEqual([]);
  return { backend, process: row.id, event: String(events[0].id), operation: "01JVERB000000000000000P001" };
}

const rules = (backend: MockBackend) =>
  backend.model
    .validate()
    .diagnostics.filter((d) => /^MQ97/.test(d.rule))
    .map((d) => `${d.rule} ${d.jsonPointer}`);

describe("verb vocabulary in the mock", () => {
  it("lives at the fixed path, holds own verbs as sub-elements and reports every use as a reference", () => {
    const { backend, process, operation } = setup();
    expect(backend.model.get(VOCAB)!.path).toBe(".maquettiste/model/vocabularies/verbs.json");
    expect(backend.model.owner(ESCALATE)?.id).toBe(VOCAB);
    const refs = backend.model.references(ESCALATE)!;
    expect(refs.map((r) => r.fromElementId).sort()).toEqual([operation, process].sort());
    expect(refs.every((r) => r.field === "verb")).toBe(true);
    expect(rules(backend)).toEqual([]);
  });

  it("puts verbs in the index rows: an operation's verb and a process's distinct sorted verbs", () => {
    const { backend, process, operation } = setup();
    const rows = backend.model.index();
    expect(rows.find((r) => r.id === operation)!.verb).toBe("escalate");
    const doc = structuredClone(backend.model.get(process)!.json) as Json;
    (doc.events as Json[])[0].verb = "promote";
    expect(backend.model.save(process, doc, backend.model.get(process)!.hash).outcome).toBe("saved");
    expect(backend.model.index().find((r) => r.id === process)!.verbs).toEqual(["promote"]);
    expect(backend.model.index().find((r) => r.id === ESCALATE)).toBeUndefined();
  });

  it("reports MQ9702, MQ9703 and MQ9706 on the vocabulary, MQ9704 at a hidden use", () => {
    const { backend } = setup();
    const doc = structuredClone(backend.model.get(VOCAB)!.json) as Json;
    doc.verbs = [
      { id: ESCALATE, key: "escalate" },
      { id: "01JVERB000000000000000E003", key: "promote" },
      { id: "01JVERB000000000000000E004", key: "setstate" },
    ];
    doc.hidden = ["modify", "nonsense"];
    // MQ9702 and MQ9703 are errors: the save is refused and says so.
    const refused = backend.model.save(VOCAB, doc, backend.model.get(VOCAB)!.hash);
    expect(refused.outcome).toBe("invalid");
    expect(refused.diagnostics.map((d) => `${d.rule} ${d.jsonPointer}`).sort()).toEqual(
      ["MQ9702 /verbs/1/key", "MQ9703 /hidden/1", "MQ9706 /verbs/2/key"].sort(),
    );
    doc.verbs = [
      { id: ESCALATE, key: "escalate" },
      { id: "01JVERB000000000000000E004", key: "setstate" },
    ];
    doc.hidden = ["promote"];
    expect(backend.model.save(VOCAB, doc, backend.model.get(VOCAB)!.hash).outcome).toBe("saved");
    const found = rules(backend);
    expect(found).toContain("MQ9706 /verbs/1/key");
    // The operation names escalate, not promote; name the hidden built-in on the operation's event.
    const op = structuredClone(backend.model.get("01JVERB000000000000000P001")!.json) as Json;
    op.verb = "promote";
    expect(backend.model.save("01JVERB000000000000000P001", op, backend.model.get("01JVERB000000000000000P001")!.hash).outcome).toBe("saved");
    expect(rules(backend)).toContain("MQ9704 /verb");
  });

  it("refuses an unknown verb at a use (MQ9701) and an eventful transition's own verb (MQ9705)", () => {
    const { backend, process } = setup();
    const doc = structuredClone(backend.model.get(process)!.json) as Json;
    const transition = (doc.transitions as Json[]).find((t) => typeof t.event === "string")!;
    transition.verb = "promote";
    const refused = backend.model.save(process, doc, backend.model.get(process)!.hash);
    expect(refused.outcome).toBe("invalid");
    expect(refused.diagnostics.map((d) => d.rule)).toContain("MQ9705");
    delete transition.verb;
    (doc.events as Json[])[0].verb = "nope";
    const unknown = backend.model.save(process, doc, backend.model.get(process)!.hash);
    expect(unknown.diagnostics.map((d) => d.rule)).toContain("MQ9701");
  });

  it("refuses a save that changes the key of an own verb in use (MQ9707) but allows an unused one", () => {
    const { backend } = setup();
    const doc = structuredClone(backend.model.get(VOCAB)!.json) as Json;
    (doc.verbs as Json[])[0].key = "raise";
    const refused = backend.model.save(VOCAB, doc, backend.model.get(VOCAB)!.hash);
    expect(refused.outcome).toBe("invalid");
    expect(refused.diagnostics.map((d) => d.rule)).toEqual(["MQ9707"]);
    const lone = new MockBackend();
    lone.model.create({ kind: "verb-vocabulary", id: VOCAB, name: "verbs", verbs: [{ id: ESCALATE, key: "escalate" }] });
    const free = structuredClone(lone.model.get(VOCAB)!.json) as Json;
    (free.verbs as Json[])[0].key = "raise";
    expect(lone.model.save(VOCAB, free, lone.model.get(VOCAB)!.hash).outcome).toBe("saved");
  });

  it("reports a second vocabulary file as MQ1009", () => {
    const { backend } = setup();
    const second = backend.model.create({ kind: "verb-vocabulary", id: "01JVERB000000000000000V002", name: "verbs", hidden: ["lock"] });
    expect(second.outcome).toBe("invalid");
    expect(second.diagnostics.map((d) => d.rule)).toContain("MQ1009");
  });
});

describe("rename-verb and remove-verb", () => {
  const batch = (backend: MockBackend, operation: Json) =>
    ({ outcome: "invalid", ...(backend.model.batch({ operations: [operation] }).body as object) }) as {
      outcome: string;
      items: { diagnostics: { rule: string; message: string }[] }[];
    };

  it("renames the key and rewrites every use, all in one batch", () => {
    const { backend, process, operation } = setup();
    expect(batch(backend, { op: "rename-verb", verb: "escalate", to: "raise" }).outcome).toBe("saved");
    expect(((backend.model.get(VOCAB)!.json as Json).verbs as Json[])[0]).toMatchObject({ id: ESCALATE, key: "raise" });
    expect((backend.model.get(operation)!.json as Json).verb).toBe("raise");
    expect((((backend.model.get(process)!.json as Json).events as Json[])[0] as Json).verb).toBe("raise");
    expect(rules(backend)).toEqual([]);
  });

  it("merges into another own verb or a built-in, and adopts the built-in of the same key", () => {
    const { backend, operation } = setup();
    expect(batch(backend, { op: "rename-verb", verb: "escalate", to: "promote" }).outcome).toBe("saved");
    expect((backend.model.get(operation)!.json as Json).verb).toBe("promote");
    expect((backend.model.get(VOCAB)!.json as Json).verbs ?? []).toEqual([]);
    const other = setup();
    const doc = structuredClone(other.backend.model.get(VOCAB)!.json) as Json;
    (doc.verbs as Json[]).push({ id: RAISE, key: "raise" });
    other.backend.model.save(VOCAB, doc, other.backend.model.get(VOCAB)!.hash);
    expect(batch(other.backend, { op: "rename-verb", verb: "escalate", to: "raise" }).outcome).toBe("saved");
    expect(((other.backend.model.get(VOCAB)!.json as Json).verbs as Json[]).map((v) => v.key)).toEqual(["raise"]);
    expect((other.backend.model.get(other.operation)!.json as Json).verb).toBe("raise");
  });

  it("removes an own verb and clears every use", () => {
    const { backend, process, operation } = setup();
    expect(batch(backend, { op: "remove-verb", verb: "escalate" }).outcome).toBe("saved");
    expect((backend.model.get(operation)!.json as Json).verb).toBeUndefined();
    expect((((backend.model.get(process)!.json as Json).events as Json[])[0] as Json).verb).toBeUndefined();
    expect(rules(backend)).toEqual([]);
  });

  it("refuses a built-in or unknown key, a bad target and a stale hash, writing nothing", () => {
    const { backend, operation } = setup();
    expect(batch(backend, { op: "remove-verb", verb: "promote" }).outcome).toBe("invalid");
    expect(batch(backend, { op: "rename-verb", verb: "escalate", to: "Not A Key" }).outcome).toBe("invalid");
    expect(batch(backend, { op: "rename-verb", verb: "escalate", to: "escalate" }).outcome).toBe("invalid");
    expect(batch(backend, { op: "rename-verb", verb: "escalate", to: "raise", expectedHashes: { [VOCAB]: "0".repeat(64) } }).outcome).toBe("conflict");
    expect((backend.model.get(operation)!.json as Json).verb).toBe("escalate");
  });

  it("writes an emptied list as [], not by deleting it", () => {
    const { backend } = setup();
    expect(batch(backend, { op: "remove-verb", verb: "escalate" }).outcome).toBe("saved");
    expect((backend.model.get(VOCAB)!.json as Json).verbs).toEqual([]);
  });

  it("leaves the uses of a built-in key alone when an own verb shadows it, and skips writes that change nothing", () => {
    const docs = new Map<string, Json>([
      [VOCAB, { kind: "verb-vocabulary", id: VOCAB, verbs: [{ id: ESCALATE, key: "promote" }] }],
      ["o", { kind: "operation", id: "o", verb: "promote" }],
      ["p", { kind: "operation", id: "p", name: "unrelated" }],
    ]);
    const removed = verbOperation(docs, { op: "remove-verb", verb: "promote" });
    expect("changed" in removed && [...removed.changed.keys()]).toEqual([VOCAB]);
    expect("changed" in removed && removed.changed.get(VOCAB)!.verbs).toEqual([]);
    // Adopting the built-in of the same key writes the vocabulary only.
    const adopted = verbOperation(docs, { op: "rename-verb", verb: "promote", to: "promote" });
    expect("changed" in adopted && [...adopted.changed.keys()]).toEqual([VOCAB]);
    // An own key that names no use writes the vocabulary only, never the unrelated documents.
    const quiet = verbOperation(new Map<string, Json>([...docs, [VOCAB, { kind: "verb-vocabulary", id: VOCAB, verbs: [{ id: ESCALATE, key: "escalate" }] }]]), {
      op: "rename-verb",
      verb: "escalate",
      to: "raise",
    });
    expect("changed" in quiet && [...quiet.changed.keys()]).toEqual([VOCAB]);
  });

  it("does not refuse MQ9707 for a use the batch itself writes", () => {
    const { backend, operation } = setup();
    const vocab = backend.model.get(VOCAB)!;
    const op = backend.model.get(operation)!;
    const renamed = structuredClone(vocab.json) as Json;
    (renamed.verbs as Json[])[0].key = "raise";
    const moved = structuredClone(op.json) as Json;
    moved.verb = "raise";
    const process = backend.model.index().find((r) => r.kind === "process" && r.use === "lifecycle")!.id;
    const processDoc = structuredClone(backend.model.get(process)!.json) as Json;
    (processDoc.events as Json[])[0].verb = "raise";
    const result = backend.model.batch({
      operations: [
        { op: "update", id: VOCAB, expectedHash: vocab.hash, element: renamed },
        { op: "update", id: operation, expectedHash: op.hash, element: moved },
        { op: "update", id: process, expectedHash: backend.model.get(process)!.hash, element: processDoc },
      ],
    });
    expect((result.body as { outcome: string }).outcome).toBe("saved");
    // Left out of the batch, the same change is refused.
    const again = setup();
    const lone = structuredClone(again.backend.model.get(VOCAB)!.json) as Json;
    (lone.verbs as Json[])[0].key = "raise";
    const refused = again.backend.model.batch({
      operations: [{ op: "update", id: VOCAB, expectedHash: again.backend.model.get(VOCAB)!.hash, element: lone }],
    });
    expect((refused.body as { outcome: string }).outcome).toBe("invalid");
  });

  it("reports own keys that differ only by hyphens as MQ9702", () => {
    const { backend } = setup();
    const doc = structuredClone(backend.model.get(VOCAB)!.json) as Json;
    doc.verbs = [
      { id: ESCALATE, key: "escalate" },
      { id: "01JVERB000000000000000E005", key: "x-1" },
      { id: "01JVERB000000000000000E006", key: "x1" },
    ];
    const refused = backend.model.save(VOCAB, doc, backend.model.get(VOCAB)!.hash);
    expect(refused.outcome).toBe("invalid");
    expect(refused.diagnostics.map((d) => `${d.rule} ${d.jsonPointer}`)).toContain("MQ9702 /verbs/2/key");
  });

  it("is a pure function over documents", () => {
    const docs = new Map<string, Json>([
      [VOCAB, { kind: "verb-vocabulary", id: VOCAB, verbs: [{ id: ESCALATE, key: "escalate" }] }],
      ["o", { kind: "operation", id: "o", verb: "escalate" }],
    ]);
    const out = verbOperation(docs, { op: "rename-verb", verb: "escalate", to: "raise" });
    expect("changed" in out && out.changed.get("o")!.verb).toBe("raise");
    expect(docs.get("o")!.verb).toBe("escalate");
  });
});

describe("deleting the vocabulary", () => {
  it("is refused while verbs are in use, and with remove-references clears each use", () => {
    const { backend, operation } = setup();
    expect(backend.model.delete(VOCAB, backend.model.get(VOCAB)!.hash, "refuse").outcome).toBe("referenced");
    expect(backend.model.delete(VOCAB, backend.model.get(VOCAB)!.hash, "remove-references").outcome).toBe("saved");
    expect((backend.model.get(operation)!.json as Json).verb).toBeUndefined();
  });
});
