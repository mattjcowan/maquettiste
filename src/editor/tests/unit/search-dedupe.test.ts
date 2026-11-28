// A table that answers twice (its index row and its database-side summary) is listed once, the one with database and schema.
import { describe, expect, it } from "vitest";
import type { ElementSummary } from "@/api/types";
import { dropTableDuplicates } from "@/search/dedupe";
import type { SearchHit } from "@/search/engine";

const hit = (id: string, kind: string, db?: string): SearchHit =>
  ({ id, kind, name: "notes", path: db ? "main · billing" : "", place: db ? "databases" : "domain-model", db, tier: 0 }) as SearchHit;
const byId = new Map([["T1", { id: "T1", kind: "table", name: "notes", database: "D1" } as unknown as ElementSummary]]);

describe("dropTableDuplicates", () => {
  it("drops the index row of a table the database side lists too", () => {
    const out = dropTableDuplicates([hit("T1", "table"), hit("D1/t:T1", "table", "D1"), hit("E1", "entity")], byId);
    expect(out.map((h) => h.id)).toEqual(["D1/t:T1", "E1"]);
  });
  it("keeps the index row when the database side does not list the table", () => {
    expect(dropTableDuplicates([hit("T1", "table")], byId).map((h) => h.id)).toEqual(["T1"]);
  });
});
