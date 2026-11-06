// The second browser pass on rights: refresh-scenario writes refusals as the engine does, MQ9905's extra fixes show when the main
// fix cannot be derived, and the header line leaves rights findings out.
import { describe, expect, it } from "vitest";
import { isRightsFinding } from "@/editors/EditorFrame";
import { INVOICE_LIFECYCLE, APPROVER } from "@/mocks/model/processSeed";
import { deriveQuickFix, extraQuickFixes } from "@/problems/quickFix";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

describe("refresh-scenario", () => {
  const api = useMockApi();

  it("rewrites each step's accepted, refusal and refusedBy from the replay, so a replay then passes", async () => {
    const { model } = api.backend;
    const created = model.create({
      kind: "scenario",
      name: "RefreshMe",
      process: INVOICE_LIFECYCLE,
      steps: [
        { id: "01JQR000000000000000000001", input: "verb", verb: "delete", actor: APPROVER, expect: { accepted: true, states: [] } },
        {
          id: "01JQR000000000000000000002",
          input: "verb",
          verb: "delete",
          actor: APPROVER,
          expect: { accepted: false, refusal: "default", refusedBy: "01JQR000000000000000000009" },
        },
      ],
    });
    expect(created.outcome).toBe("saved");
    const id = (created as { id: string }).id;
    const verify = () =>
      fetch(api.url(`/api/processes/${INVOICE_LIFECYCLE}/verify`), { method: "POST", body: "{}", headers: { "Content-Type": "application/json" } }).then((r) =>
        r.json(),
      );
    const mine = (r: { results: { scenario: string; passed?: boolean }[] }) => r.results.find((s) => s.scenario === id);
    expect(mine(await verify())?.passed).toBe(false);
    const result = model.batch({ operations: [{ op: "refresh-scenario", id }] });
    expect(result.status).toBe(200);
    const steps = (model.get(id)!.json as Json).steps as { expect: Json }[];
    expect(steps[1].expect).toMatchObject({ accepted: false, refusal: "no-transition" });
    expect(steps[1].expect.refusedBy).toBeUndefined();
    expect(mine(await verify())?.passed).toBe(true);
  });
});

describe("MQ9905 quick fixes", () => {
  const d = { rule: "MQ9905", severity: "warning", message: "", elementId: "P", filePath: null, jsonPointer: "/events/0", line: null, column: null } as never;
  const doc = (id: string) => (id === "P" ? ({ kind: "process", id: "P", name: "Invoice", events: [{ id: "E1", name: "issue" }] } as never) : undefined);

  it("an event with no verb has no main fix but still offers to open the event", () => {
    expect(deriveQuickFix(d, null, doc)).toBeNull();
    expect(extraQuickFixes(d, doc).map((f) => f.label)).toEqual(["Open the event"]);
  });
});

describe("the header line", () => {
  it("leaves out the rights rules and findings on a rights row", () => {
    expect(isRightsFinding({ rule: "MQ9905", jsonPointer: "/events/0" })).toBe(true);
    expect(isRightsFinding({ rule: "MQ2001", jsonPointer: "/rights/1/verbs" })).toBe(true);
    expect(isRightsFinding({ rule: "MQ2001", jsonPointer: "/states/0" })).toBe(false);
  });
});
