// The named trigger point's quick fixes (trigger-points-design.md section 11): the same fixes the lifecycle grid offers, read from a
// document whose pointers start at its root.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import { deriveQuickFix } from "@/problems/quickFix";

type Json = Record<string, unknown>;

const doc = (extra: Json): Json => ({ kind: "trigger-point", id: "T", name: "T", type: "check", verbs: ["delete"], subjects: [{ entity: "E" }], ...extra });
const fix = (rule: string, pointer: string, json: Json) =>
  deriveQuickFix({ rule, severity: "error", message: "m", jsonPointer: pointer, elementId: "T" } as Diagnostic, null, () => json as never);

describe("named trigger point quick fixes", () => {
  it("MQ8102 removes the last Except entry with its key, and one of several alone", () => {
    const one = fix("MQ8102", "/exceptEntities/0", doc({ exceptEntities: ["X"] }));
    expect(one).toMatchObject({ kind: "update", label: "Remove the entry" });
    expect("exceptEntities" in (one as { json: Json }).json).toBe(false);
    const two = fix("MQ8102", "/exceptEntities/0", doc({ exceptEntities: ["X", "Y"] })) as { json: Json };
    expect(two.json.exceptEntities).toEqual(["Y"]);
  });

  it("MQ8101 removes the key", () => {
    const f = fix("MQ8101", "/outside", doc({ outside: true, condition: { text: "true" } })) as { json: Json; label: string };
    expect(f.label).toBe("Remove the key");
    expect("outside" in f.json).toBe(false);
  });

  it("MQ8103 makes it an action, MQ8105 and MQ8106 change its timing", () => {
    expect((fix("MQ8103", "/verbs/0", doc({ type: "replace", verbs: ["modify"] })) as { json: Json }).json).toMatchObject({
      type: "action",
      timing: "with-change",
    });
    expect((fix("MQ8105", "/sets", doc({ type: "action", timing: "after-commit" })) as { json: Json }).json.timing).toBe("with-change");
    expect((fix("MQ8106", "/timing", doc({ type: "action", timing: "with-change" })) as { json: Json }).json.timing).toBe("after-commit");
  });

  it("offers no update for MQ8107 (the delete is the editor's) or for the last Covers entry", () => {
    expect(fix("MQ8107", "/verbs", doc({}))).toBeNull();
    expect(fix("MQ8102", "/subjects/0", doc({}))).toBeNull();
  });
});
