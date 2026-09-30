// The mock's process rules (mocks/model/validate.ts): MQ9001, MQ9201 and MQ9205 as the engine reports them. The default
// model validates clean; the `lifecycle` scenario has one finding per quick-fix kind, and `drift` only its MQ9203.
import { describe, expect, it } from "vitest";
import { MockBackend } from "@/mocks/backend";

const processRules = (backend: MockBackend) =>
  backend.model
    .validate()
    .diagnostics.filter((d) => /^MQ9/.test(d.rule))
    .map((d) => `${d.rule} ${d.jsonPointer}`)
    .sort();

describe("mock process rules", () => {
  it("reports nothing on the default model, one finding per quick-fix kind on lifecycle, MQ9203 alone on drift", () => {
    expect(processRules(new MockBackend())).toEqual([]);
    expect(processRules(new MockBackend({ scenarios: ["lifecycle"] }))).toEqual(["MQ9001 /initial", "MQ9201 /subject", "MQ9205 /boundAttribute"]);
    expect(processRules(new MockBackend({ scenarios: ["drift"] }))).toEqual(["MQ9203 /boundAttribute"]);
  });
});
