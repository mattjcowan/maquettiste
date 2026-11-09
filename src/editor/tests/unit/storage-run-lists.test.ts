// The Storage tab's lists and gaps, the storage quick fixes, the per-type target list and the mock's storage run view
// (several-types-design.md 19.10, 19.16): pure helpers and the mock on the engine's run fixture.
import fs from "node:fs";
import path from "node:path";
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { resolveDatabase } from "@/mocks/model/physical";
import { packs } from "@/mocks/model/seed";
import type { SeedFile } from "@/mocks/model/store";
import { GAP_WORDS, storageLists } from "@/editors/storage/RelationStorageRun";
import { coveredTargets } from "@/editors/storage/relationStorage";
import { deriveQuickFix, extraQuickFixes, hasQuickFix } from "@/problems/quickFix";
import { copyTextOf, keptCodeOf, mappingKeyFix } from "@/problems/storageFix";
import type { Diagnostic, ModelJson } from "@/api/types";

type Json = Record<string, unknown>;

const diag = (rule: string, message: string, elementId: string, jsonPointer: string): Diagnostic => ({
  rule,
  severity: "info",
  message,
  elementId,
  filePath: null,
  jsonPointer,
  line: null,
  column: null,
});

describe("the lists and gaps", () => {
  const item = {
    relationId: "R",
    relationName: "PinnedTo",
    mappingId: "M",
    tables: ["T"],
    leftOut: [{ endId: "E", entityId: "tool" }],
    notStored: [{ endId: "E", entityId: "memo" }],
    gaps: [
      { endId: "E", kind: "type-check" as const, text: "CONSTRAINT ck CHECK (a IN ('B'))" },
      { endId: "E", kind: "id-only" as const },
    ],
  };
  const names: Record<string, string> = { tool: "Tool", memo: "Memo" };
  it("says what is left out and not stored in the design's words", () => {
    const lists = storageLists(
      item,
      (id) => names[id] ?? id,
      () => "attachedTo",
    );
    expect(lists.leftOut).toBe("Left out (not in this database): Tool");
    expect(lists.notStored).toBe("Not stored: Memo, no key or column points at it");
  });
  it("gives each gap its words and the text to copy, and none where there is none", () => {
    const lists = storageLists(
      item,
      (id) => id,
      () => "attachedTo",
    );
    expect(lists.gaps).toEqual([
      { endId: "E", role: "attachedTo", kind: "type-check", words: GAP_WORDS["type-check"], text: "CONSTRAINT ck CHECK (a IN ('B'))" },
      { endId: "E", role: "attachedTo", kind: "id-only", words: GAP_WORDS["id-only"], text: null },
    ]);
  });
  it("shows nothing for a relation the run has no item for", () => {
    expect(
      storageLists(
        undefined,
        (id) => id,
        (id) => id,
      ),
    ).toEqual({ leftOut: null, notStored: null, gaps: [] });
  });
});

describe("the quick fixes", () => {
  const relation = { kind: "relation", id: "REL", name: "Notes", ends: [{ id: "E1" }, { id: "E2" }] } as unknown as ModelJson;
  const mapping = (extra: Json) => ({ kind: "mapping", id: "MAP", relation: "REL", database: "DB", ...extra }) as unknown as ModelJson;
  it("offers every rule a fix", () => {
    for (const rule of ["MQ4009", "MQ4115", "MQ4118", "MQ4119", "MQ4120"]) expect(hasQuickFix(rule, null)).toBe(true);
  });
  it("MQ4115: Not in this database, then Open the Storage tab", () => {
    const d = diag("MQ4115", "Relation 'Notes' has an end with several types, so database 'main' stores nothing for it.", "E1", "/ends/0");
    expect(deriveQuickFix(d, null, () => relation)).toEqual({
      kind: "storage",
      action: "ignore",
      relation: "REL",
      database: "main",
      label: "Not in this database",
    });
    expect(extraQuickFixes(d, () => relation)).toEqual([{ kind: "storage", action: "open", relation: "REL", label: "Open the Storage tab" }]);
  });
  it("MQ4120: Open the Storage tab, on the mapping's relation", () => {
    const d = diag("MQ4120", "Relation 'Notes' covers entity 'Memo' in database 'main', but its mapping names nothing", "MAP", "/ends/0");
    expect(deriveQuickFix(d, null, () => mapping({}))).toEqual({ kind: "storage", action: "open", relation: "REL", label: "Open the Storage tab" });
  });
  it("MQ4118: Copy the check copies every text of the finding", () => {
    const message =
      "Database 'main' holds end 'x' of relation 'R' as a type and an id in table 't', so it finds no check. To check the codes, add: CONSTRAINT ck CHECK (a IN ('A')) To refuse duplicate links, add: CONSTRAINT uq UNIQUE (a, b)";
    expect(copyTextOf(message)).toBe("CONSTRAINT ck CHECK (a IN ('A'))\nCONSTRAINT uq UNIQUE (a, b)");
    expect(copyTextOf("so it can't check the type an id points at.")).toBeNull();
    expect(deriveQuickFix(diag("MQ4118", message, "MAP", "/ends/1"), null, () => mapping({}))).toMatchObject({ action: "copy", label: "Copy the check" });
  });
  it("MQ4119: Keep the stored code names the entity, database and old code", () => {
    const message =
      "Since the baseline, the type code of 'Part' in database 'main' changed from 'Part' to 'P', and column 'attached_type' of table 'comments' holds codes.";
    expect(keptCodeOf(message)).toEqual({ entity: "Part", database: "main", code: "Part" });
    expect(deriveQuickFix(diag("MQ4119", message, "MAP", ""), null, () => mapping({}))).toMatchObject({
      action: "keep-code",
      code: "Part",
      label: "Keep the stored code",
    });
  });
  it("MQ4009: removes a refused key, or moves a top-level foreignKey to the entry of an end with none", () => {
    expect(mappingKeyFix(mapping({ promotedName: "x" }), relation, "/promotedName")).toMatchObject({ label: "Remove the key" });
    const moved = mappingKeyFix(mapping({ foreignKey: "FK", ends: [{ end: "E1", idColumn: "C" }] }), relation, "/foreignKey");
    expect(moved?.label).toBe("Move it to the end's entry");
    expect(moved?.json).toMatchObject({
      ends: [
        { end: "E1", idColumn: "C" },
        { end: "E2", foreignKey: "FK" },
      ],
    });
    expect((moved?.json as Json).foreignKey).toBeUndefined();
    expect(mappingKeyFix(mapping({}), relation, "/shape")).toBeNull();
  });
});

describe("the per-type targets", () => {
  const entities = [
    { id: "part", name: "Part" },
    { id: "pp", name: "PurchasedPart", base: "part" },
    { id: "memo", name: "Memo" },
    { id: "note", name: "Note" },
  ];
  it("lists listed entities, then carriers, each followed by what is derived from it", () => {
    expect(coveredTargets(["part"], ["memo"], false, entities)).toEqual(["part", "pp", "memo"]);
  });
  it("covers every entity for an end that is any entity", () => {
    expect(coveredTargets([], [], true, entities)).toEqual(["memo", "note", "part", "pp"]);
  });
});

describe("the mock's storage run on the engine's fixture", () => {
  const root = path.resolve(__dirname, "../fixtures/several-types-storage-run/.maquettiste");
  const walk = (dir: string): string[] =>
    fs.readdirSync(dir, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(dir, e.name)) : [path.join(dir, e.name)]));
  const files: SeedFile[] = walk(root)
    .filter((f) => !f.includes(`${path.sep}.schema${path.sep}`))
    .map((f) => ({ path: path.relative(root, f).split(path.sep).join("/"), text: fs.readFileSync(f, "utf8") }));
  const backend = new MockBackend({ seed: { files, packs: packs() } });
  const docs = backend.model.docs();
  const settings = backend.model.projectSettings();
  const database = [...docs.values()].find((d) => d.kind === "database")!;
  const view = resolveDatabase(
    { docs, conventions: settings.conventions as Json, databaseConventions: settings.databases as Record<string, Json> },
    String(database.id),
  )!;
  const named = (name: string) => view.relationStorage?.find((x) => x.relationName === name);

  it("reports one item per placed relation, with the gaps and their texts", () => {
    const attached = named("AttachedTo")!;
    expect(attached.gaps.map((g) => g.kind)).toEqual(["no-foreign-key", "type-check"]);
    expect(attached.gaps[1].text).toBe("CONSTRAINT ck_comments_attached_to CHECK (attached_type IN ('Assembly', 'Part'))");
    expect(named("Folded")?.gaps).toEqual([]);
  });
  it("reports the placed type no pointer reaches, and what is not stored", () => {
    const pinned = named("PinnedTo")!;
    expect(pinned.notStored).toHaveLength(1);
    expect(pinned.gaps.map((g) => g.kind)).toEqual(["count-check"]);
  });
});
