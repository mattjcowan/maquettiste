// The mock's fixes from the review of trigger points, part 1: refresh-scenario's `triggerPoints` flag in one update, the references a
// scenario's expectations make to trigger points, and what remove-verb forgets (findings 15 to 19 and section A, item 4).
import { describe, expect, it } from "vitest";
import { APPROVER, INVOICE_LIFECYCLE } from "@/mocks/model/processSeed";
import { referencesOf } from "@/mocks/model/refs";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

describe("refresh-scenario with triggerPoints", () => {
  const api = useMockApi();

  const setup = () => {
    const { model } = api.backend;
    const lifecycle = structuredClone(model.get(INVOICE_LIFECYCLE)!.json) as Json;
    lifecycle.triggerPoints = [{ id: "01JQR000000000000000000050", name: "Seen", type: "check", verbs: ["read"] }];
    delete lifecycle.rights;
    delete lifecycle.rightsDefault;
    expect(model.save(INVOICE_LIFECYCLE, lifecycle, model.get(INVOICE_LIFECYCLE)!.hash).outcome).toBe("saved");
    const created = model.create({
      kind: "scenario",
      name: "RefreshPoints",
      process: INVOICE_LIFECYCLE,
      steps: [{ id: "01JQR000000000000000000051", input: "verb", verb: "read", actor: APPROVER, expect: { accepted: true, states: [] } }],
    });
    expect(created.outcome).toBe("saved");
    return (created as { id: string }).id;
  };
  const expectOf = (id: string) => ((api.backend.model.get(id)!.json as Json).steps as { expect: Json }[])[0].expect;

  it("writes expect.triggerPoints in the same update when asked, and not otherwise", () => {
    const id = setup();
    expect(api.backend.model.batch({ operations: [{ op: "refresh-scenario", id }] }).status).toBe(200);
    expect(expectOf(id).triggerPoints).toBeUndefined();
    expect(api.backend.model.batch({ operations: [{ op: "refresh-scenario", id, triggerPoints: true }] }).status).toBe(200);
    expect(expectOf(id).triggerPoints).toEqual(["01JQR000000000000000000050"]);
    // Without the flag, a step that already expects them keeps them (the engine's Observed).
    expect(api.backend.model.batch({ operations: [{ op: "refresh-scenario", id }] }).status).toBe(200);
    expect(expectOf(id).triggerPoints).toEqual(["01JQR000000000000000000050"]);
  });
});

describe("references to trigger points from a scenario's expectations (finding 19)", () => {
  it("lists expect.checkWarnings and expect.triggerPoints ids, each removable as an item", () => {
    const scenario = {
      kind: "scenario",
      id: "SC",
      process: INVOICE_LIFECYCLE,
      steps: [{ id: "ST1", expect: { checkWarnings: ["CHECK1"], triggerPoints: ["CHECK1", "ACTION1"] } }],
    };
    const refs = referencesOf(scenario).filter((r) => r.field === "checkWarnings" || r.field === "triggerPoints");
    expect(refs.map((r) => [r.toId, r.pointer, r.removeItem])).toEqual([
      ["CHECK1", "/steps/0/expect/checkWarnings/0", "/steps/0/expect/checkWarnings/0"],
      ["CHECK1", "/steps/0/expect/triggerPoints/0", "/steps/0/expect/triggerPoints/0"],
      ["ACTION1", "/steps/0/expect/triggerPoints/1", "/steps/0/expect/triggerPoints/1"],
    ]);
  });
});

describe("remove-verb forgets the trigger points that go (finding 19)", () => {
  it("clears a step's assume word and its expected ids for a point whose last verb goes", async () => {
    const { verbOperation } = await import("@/mocks/model/verbs");
    const docs = new Map<string, Json>([
      ["V", { kind: "verb-vocabulary", id: "V", verbs: [{ id: "VE", key: "escalate" }] }],
      [
        "L",
        {
          kind: "process",
          id: "L",
          use: "lifecycle",
          triggerPoints: [
            { id: "T1", name: "OnlyEscalate", type: "check", verbs: ["escalate"] },
            { id: "T2", name: "Both", type: "check", verbs: ["escalate", "modify"] },
          ],
        },
      ],
      [
        "S",
        {
          kind: "scenario",
          id: "S",
          process: "L",
          steps: [{ id: "ST", assume: { T1: "error", T2: "pass" }, expect: { checkWarnings: ["T1", "T2"], triggerPoints: ["T1"] } }],
        },
      ],
    ]);
    const result = verbOperation(docs, { op: "remove-verb", verb: "escalate" });
    if ("error" in result) throw new Error(result.error);
    expect((result.changed.get("L")!.triggerPoints as Json[]).map((t) => t.id)).toEqual(["T2"]);
    const step = (result.changed.get("S")!.steps as Json[])[0];
    expect(step.assume).toEqual({ T2: "pass" });
    expect(step.expect).toEqual({ checkWarnings: ["T2"], triggerPoints: [] });
  });
});
