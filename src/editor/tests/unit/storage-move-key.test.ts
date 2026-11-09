// "Move it to the end's entry" (MQ4009): a top-level key moves to the end whose table it refers to, and on an end with several types it
// becomes the entry's key only when its table holds every listed type; a key to one type's table becomes that type's `types` item.
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { QueryClient } from "@tanstack/react-query";

const docs = new Map<string, Record<string, unknown>>();
vi.mock("@/api/endpoints", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/api/endpoints")>()),
  getElement: async (id: string) => ({ json: structuredClone(docs.get(id)) }),
}));

const { applyStorageFix } = await import("@/problems/storageFixApply");

type Json = Record<string, unknown>;
const bound = (table: string) => ({ bindings: [{ database: "db", source: table }] });

function run(relationEnds: Json[]): Promise<{ reason: string | null; mapping: Json }> {
  docs.set("rel", { id: "rel", ends: relationEnds });
  docs.set("map", { id: "map", database: "db", relation: "rel", foreignKey: "fk_part", foreignKeyEnd: "e2" });
  docs.set("comments", { id: "comments", foreignKeys: [{ id: "fk_part", referencesTable: "parts" }] });
  const rows = [{ id: "comments", kind: "table", database: "db" }];
  const qc = { ensureQueryData: async () => rows } as unknown as QueryClient;
  const edits = {
    update: async (id: string, fn: (j: Json) => void) => {
      const j = structuredClone(docs.get(id)!);
      fn(j);
      docs.set(id, j);
    },
  };
  const fix = { kind: "storage", action: "move-key", mapping: "map", relation: "rel", key: "fk_part", label: "Move it to the end's entry" } as const;
  return applyStorageFix(fix, { qc, store: {} as never, edits: edits as never }).then((reason) => ({ reason, mapping: docs.get("map")! }));
}

describe("move-key quick fix", () => {
  beforeEach(() => {
    docs.clear();
    docs.set("Comment", bound("comments"));
    docs.set("Part", bound("parts"));
    docs.set("Assembly", bound("assemblies"));
  });

  it("writes a key to one type's table as that type's types item", async () => {
    const { reason, mapping } = await run([
      { id: "e1", entity: "Comment" },
      { id: "e2", types: [{ entity: "Part" }, { entity: "Assembly" }] },
    ]);
    expect(reason).toBeNull();
    expect(mapping.ends).toEqual([{ end: "e2", types: [{ entity: "Part", foreignKey: "fk_part" }] }]);
    expect(mapping.foreignKey).toBeUndefined();
    expect(mapping.foreignKeyEnd).toBeUndefined();
  });

  it("keeps a key to a table that holds every listed type as the entry's key", async () => {
    docs.set("Assembly", bound("parts"));
    const { reason, mapping } = await run([
      { id: "e1", entity: "Comment" },
      { id: "e2", types: [{ entity: "Part" }, { entity: "Assembly" }] },
    ]);
    expect(reason).toBeNull();
    expect(mapping.ends).toEqual([{ end: "e2", foreignKey: "fk_part" }]);
  });

  it("writes the entry's key on a one-type end", async () => {
    const { reason, mapping } = await run([
      { id: "e1", types: [{ entity: "Comment" }, { entity: "Assembly" }] },
      { id: "e2", entity: "Part" },
    ]);
    expect(reason).toBeNull();
    expect(mapping.ends).toEqual([{ end: "e2", foreignKey: "fk_part" }]);
  });

  it("moves nothing when the key's table holds some but not one or all of the end's types", async () => {
    docs.set("Tool", bound("parts"));
    const { reason, mapping } = await run([
      { id: "e1", entity: "Comment" },
      { id: "e2", types: [{ entity: "Part" }, { entity: "Tool" }, { entity: "Assembly" }] },
    ]);
    expect(reason).toMatch(/some of the end's types/);
    expect(mapping.foreignKey).toBe("fk_part");
  });
});
