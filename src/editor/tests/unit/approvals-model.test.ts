// The Gates tab's approval model (signoff-design.md section 11): the conversion both ways, the grid's rows and edits.
import { describe, expect, it } from "vitest";
import {
  addSignature,
  canRevert,
  editSignature,
  gateSignerIds,
  notByVerbKeys,
  refusal,
  removeSignature,
  reorderStages,
  revertToSigners,
  setNotBy,
  signatureRows,
  signaturesOf,
  toApproval,
  ANYONE,
} from "@/model/approvals";
import type { GateDoc } from "@/model/process";

const ACTORS = [
  { id: "a1", name: "Approver" },
  { id: "a2", name: "Controller" },
  { id: "a3", name: "Clerk" },
];
const names = {
  actor: (id: string) => ACTORS.find((a) => a.id === id)?.name ?? "?",
  transition: (id: string) => `move ${id}`,
  verb: (key: string) => key.toUpperCase(),
  condition: (t: Record<string, unknown> | undefined) => String((t as { text?: string } | undefined)?.text ?? ""),
};
const plain = (): GateDoc => ({
  id: "g1",
  name: "Release",
  required: 2,
  signers: ["a1", "a2"],
  requiredActors: ["a1"],
  allowRepeatSigner: true,
  reasonRequired: true,
  meanings: [{ id: "m1", name: "approved" }],
});

describe("Use named signatures", () => {
  it("moves the counting keys onto one signature in one stage named after the gate", () => {
    const g = plain();
    expect(toApproval(g)).toBe(true);
    expect(g.signers).toBeUndefined();
    expect(g.required).toBeUndefined();
    expect(g.requiredActors).toBeUndefined();
    expect(g.allowRepeatSigner).toBeUndefined();
    expect(g.reasonRequired).toBe(true);
    expect(g.meanings).toHaveLength(1);
    expect(g.stages).toHaveLength(1);
    expect(g.stages![0].name).toBe("Release");
    const [sig] = g.stages![0].signatures;
    expect(sig).toMatchObject({ signers: ["a1", "a2"], required: 2, requiredActors: ["a1"], allowRepeatSigner: true });
  });

  it("leaves default counts out of the signature, and does nothing twice", () => {
    const g: GateDoc = { id: "g", name: "G", signers: ["a1"], required: 1 };
    toApproval(g);
    expect(Object.keys(g.stages![0].signatures[0]).sort()).toEqual(["id", "name", "signers"]);
    expect(toApproval(g)).toBe(false);
  });

  it("goes back only for one signature with no when or notBy, and carries its keys back", () => {
    const g = plain();
    toApproval(g);
    expect(canRevert(g)).toBe(true);
    g.allowSignerAcrossSignatures = true;
    expect(revertToSigners(g)).toBe(true);
    expect(g).toMatchObject({ signers: ["a1", "a2"], required: 2, requiredActors: ["a1"], allowRepeatSigner: true });
    expect(g.stages).toBeUndefined();
    expect(g.allowSignerAcrossSignatures).toBeUndefined();
  });

  it("refuses to go back with a when, a notBy or a second signature", () => {
    const g = plain();
    toApproval(g);
    const id = g.stages![0].signatures[0].id;
    editSignature(g, id, "when", "subject.cost > 10", { actors: ACTORS });
    expect(canRevert(g)).toBe(false);
    expect(revertToSigners(g)).toBe(false);
    editSignature(g, id, "when", "", { actors: ACTORS });
    setNotBy(g, id, [{ verb: "promote" }]);
    expect(canRevert(g)).toBe(false);
    setNotBy(g, id, []);
    expect(canRevert(g)).toBe(true);
    addSignature(g);
    expect(canRevert(g)).toBe(false);
  });
});

describe("the signatures grid", () => {
  const approval = () => {
    const g = plain();
    toApproval(g);
    const id = g.stages![0].signatures[0].id;
    editSignature(g, id, "when", "subject.total > 10000", { actors: ACTORS });
    setNotBy(g, id, [{ verb: "promote" }, { transition: "t1" }]);
    return g;
  };

  it("prints a row per signature with names, the condition and Not by", () => {
    const g = approval();
    addSignature(g);
    const rows = signatureRows(g, "/transitions/0/gate", names);
    expect(rows).toHaveLength(2);
    expect(rows[0]).toMatchObject({
      pointer: "/transitions/0/gate/stages/0/signatures/0",
      signers: "Approver, Controller",
      requiredActors: "Approver",
      required: 2,
      repeat: true,
      when: "subject.total > 10000",
      notBy: "PROMOTE, move t1",
    });
    expect(rows[1]).toMatchObject({ name: "signature2", when: "", notBy: ANYONE, signers: "Approver, Controller", required: 1 });
  });

  it("adds a signature after a row and refuses to remove the last one", () => {
    const g = approval();
    const first = signaturesOf(g)[0].id;
    const added = addSignature(g, first)!;
    addSignature(g);
    expect(signaturesOf(g).map((s) => s.id)[1]).toBe(added);
    expect(removeSignature(g, added)).toBe(true);
    expect(removeSignature(g, signaturesOf(g)[1].id)).toBe(true);
    expect(removeSignature(g, first)).toBe(false);
    expect(signaturesOf(g)).toHaveLength(1);
  });

  it("refuses an unknown actor name, an empty signers cell and a count below one", () => {
    const g = approval();
    const sig = signaturesOf(g)[0];
    const o = { actors: ACTORS };
    expect(refusal(sig, "signers", "Nobody", o)).toMatch(/No actor is named 'Nobody'/);
    expect(refusal(sig, "signers", "", o)).toMatch(/at least one signer/);
    expect(refusal(sig, "requiredActors", "Clerk", o)).toMatch(/must be one of the signers/);
    expect(refusal(sig, "required", "0", o)).toMatch(/whole number/);
    expect(refusal(sig, "required", "1.5", o)).toMatch(/whole number/);
    expect(editSignature(g, sig.id, "signers", "Nobody", o)).toBe(false);
    expect(sig.signers).toEqual(["a1", "a2"]);
  });

  it("edits the cells and drops required actors that stop being signers", () => {
    const g = approval();
    const sig = signaturesOf(g)[0];
    const o = { actors: ACTORS };
    editSignature(g, sig.id, "signers", "Controller, Clerk", o);
    expect(sig.signers).toEqual(["a2", "a3"]);
    expect(sig.requiredActors).toBeUndefined();
    editSignature(g, sig.id, "requiredActors", "Clerk", o);
    expect(sig.requiredActors).toEqual(["a3"]);
    editSignature(g, sig.id, "required", "1", o);
    expect(sig.required).toBeUndefined();
    editSignature(g, sig.id, "repeat", false, o);
    expect(sig.allowRepeatSigner).toBeUndefined();
    editSignature(g, sig.id, "why", "Because", o);
    expect(sig.description).toBe("Because");
    editSignature(g, sig.id, "when", "", o);
    expect(sig.when).toBeUndefined();
    editSignature(g, sig.id, "name", "Money", o);
    expect(sig.name).toBe("Money");
  });
});

describe("helpers", () => {
  it("offers the verbs a lifecycle's runs see: move, signature, complete and its events' verbs", () => {
    const keys = notByVerbKeys({ events: [{ id: "e", name: "E", verb: "submit-for-review" }] });
    expect(keys).toEqual(expect.arrayContaining(["promote", "sign", "reject", "complete", "submit-for-review"]));
    expect(keys).not.toContain("modify");
    expect(keys).not.toContain("delete");
  });

  it("reads every signer of a gate document, plain or approval", () => {
    expect(gateSignerIds({ signers: ["a1"] })).toEqual(["a1"]);
    expect(gateSignerIds({ stages: [{ signatures: [{ signers: ["a1", "a2"] }, { signers: ["a2", "a3"] }] }] })).toEqual(["a1", "a2", "a3"]);
    expect(gateSignerIds(undefined)).toEqual([]);
  });

  it("puts stages in round order, keeping the file order within a round", () => {
    const g = { id: "g", name: "G", stages: [{ id: "s1", round: 2 }, { id: "s2" }, { id: "s3", round: 2 }, { id: "s4", round: 1 }] } as unknown as GateDoc;
    expect(reorderStages(g)).toBe(true);
    expect(g.stages!.map((s) => s.id)).toEqual(["s2", "s4", "s1", "s3"]);
    expect(reorderStages(g)).toBe(false);
  });
});
