// The mock's process rules (mocks/model/validate.ts): MQ9001, MQ9201, MQ9205 and MQ9209 as the engine reports them. The
// seeds use `subjects` and validate clean; the `lifecycle` scenario has one finding per quick-fix kind, its lifecycle in
// the one-subject form (so MQ9209 too), and `drift` only its MQ9203.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";
import { subjectsOf } from "@/model/process";

const processRules = (backend: MockBackend) =>
  backend.model
    .validate()
    .diagnostics.filter((d) => /^MQ9/.test(d.rule))
    .map((d) => `${d.rule} ${d.jsonPointer}`)
    .sort();

describe("mock process rules", () => {
  it("reports nothing on the default model, one finding per quick-fix kind on lifecycle, MQ9203 beside it on drift", () => {
    expect(processRules(new MockBackend())).toEqual([]);
    expect(processRules(new MockBackend({ scenarios: ["lifecycle"] }))).toEqual([
      "MQ9001 /initial",
      "MQ9201 /subject",
      "MQ9205 /boundAttribute",
      "MQ9209 /subject",
    ]);
    expect(processRules(new MockBackend({ scenarios: ["drift"] }))).toEqual(["MQ9203 /subjects/0/boundAttribute"]);
  });

  it("converts to subjects with use-subjects and then reads the same, without MQ9209 or the entity-names-it check", () => {
    const backend = new MockBackend({ scenarios: ["lifecycle"] });
    const lifecycles = backend.model.validate().diagnostics.filter((d) => d.rule === "MQ9201" && d.jsonPointer === "/subject");
    const id = lifecycles[0].elementId!;
    const before = backend.model.get(id)!.json as Record<string, unknown>;
    const answer = backend.model.batch({ operations: [{ op: "use-subjects", elementIds: [id] }] });
    expect((answer.body as { outcome: string }).outcome).toBe("saved");
    const after = backend.model.get(id)!.json as Record<string, unknown>;
    expect(after.subject).toBeUndefined();
    expect(after.boundAttribute).toBeUndefined();
    expect(subjectsOf(after).map((e) => e.entity)).toEqual([before.subject]);
    expect(subjectsOf(after)[0].id).toMatch(/^[0-7][0-9A-HJKMNP-TV-Z]{25}$/);
    const rules = backend.model
      .validate()
      .diagnostics.filter((d) => d.elementId === id)
      .map((d) => d.rule);
    expect(rules).not.toContain("MQ9209");
    expect(rules).not.toContain("MQ9201");
  });
});
