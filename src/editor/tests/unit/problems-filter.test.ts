// The Problems panel's severity filter (problems/filter.ts): groups keep only the shown severities, empty groups go,
// and the hidden count is the diagnostics the filter removed.
import { describe, expect, it } from "vitest";
import type { Diagnostic } from "@/api/types";
import { applyProblemFilter } from "@/problems/filter";
import type { ProblemGroup } from "@/problems/group";

const d = (severity: Diagnostic["severity"], rule = "MQ0000"): Diagnostic =>
  ({ rule, severity, message: rule, file: "x.json", jsonPointer: null, line: null, column: null }) as unknown as Diagnostic;
const group = (key: string, diagnostics: Diagnostic[]): ProblemGroup =>
  ({ key, label: key, elementId: null, unsaved: false, diagnostics }) as unknown as ProblemGroup;

describe("applyProblemFilter", () => {
  const groups = [group("a", [d("error"), d("info")]), group("b", [d("info"), d("info")]), group("c", [d("warning")])];

  it("shows everything with every severity on", () => {
    const out = applyProblemFilter(groups, { errors: true, warnings: true, infos: true });
    expect(out.groups.map((g) => g.diagnostics.length)).toEqual([2, 2, 1]);
    expect(out.hidden).toBe(0);
  });

  it("drops the notes and the groups left empty, counting what it hid", () => {
    const out = applyProblemFilter(groups, { errors: true, warnings: true, infos: false });
    expect(out.groups.map((g) => g.key)).toEqual(["a", "c"]);
    expect(out.groups[0]!.diagnostics.map((x) => x.severity)).toEqual(["error"]);
    expect(out.hidden).toBe(3);
  });
});
