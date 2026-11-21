// A refused save lists only the findings of the element saved (or no element), never other elements' notes.
import { describe, expect, it } from "vitest";
import type { Diagnostic, ElementSummary } from "@/api/types";
import type { Draft } from "@/state/store";
import { groupProblems } from "@/problems/group";

const d = (rule: string, severity: Diagnostic["severity"], elementId: string | null, filePath: string | null = null): Diagnostic =>
  ({ rule, severity, message: rule, elementId, filePath, jsonPointer: null, line: null, column: null }) as Diagnostic;
const summary = (id: string, path: string): ElementSummary => ({ id, kind: "entity", name: id, path }) as unknown as ElementSummary;
const draft = (diagnostics: Diagnostic[]): Draft => ({ id: "a", status: "invalid", diagnostics, json: { name: "a" }, error: null }) as unknown as Draft;
const byId = new Map([summary("a", "a.json"), summary("b", "b.json")].map((s) => [s.id, s]));

describe("groupProblems with a refused draft", () => {
  it("keeps its own findings, its parts' (by file path) and those naming no element", () => {
    const g = groupProblems(
      [],
      [draft([d("MQ1", "error", "a"), d("MQ2", "error", "part-1", "a.json"), d("MQ3", "warning", null), d("MQ2006", "info", "b", "b.json")])],
      byId,
    );
    expect(g[0]!.diagnostics.map((x) => x.rule)).toEqual(["MQ1", "MQ2", "MQ3"]);
  });

  it("falls back to the refusal line when nothing of its own or no error remains", () => {
    const g = groupProblems([], [draft([d("MQ2006", "info", "b", "b.json")])], byId);
    expect(g[0]!.diagnostics.map((x) => x.rule)).toEqual(["save"]);
  });

  it("keeps an error of another element rather than losing it", () => {
    const g = groupProblems([], [draft([d("MQ9", "error", "b", "b.json"), d("MQ2006", "info", "b", "b.json")])], byId);
    expect(g[0]!.diagnostics.map((x) => x.rule)).toEqual(["MQ9"]);
  });

  it("keeps an error of another element next to its own warning", () => {
    const g = groupProblems([], [draft([d("MQ3", "warning", "a"), d("MQ9", "error", "b", "b.json")])], byId);
    expect(g[0]!.diagnostics.map((x) => x.rule)).toEqual(["MQ3", "MQ9"]);
  });

  it("drops notes of other elements when its own finding is an error", () => {
    const g = groupProblems([], [draft([d("MQ1", "error", "a"), d("MQ2006", "info", "b", "b.json"), d("MQ7", "warning", "b", "b.json")])], byId);
    expect(g[0]!.diagnostics.map((x) => x.rule)).toEqual(["MQ1"]);
  });
});
