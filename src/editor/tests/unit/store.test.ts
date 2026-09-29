import { beforeEach, describe, expect, it } from "vitest";
import { createEditorStore, type Draft, type UndoEntry } from "@/state/store";

const draft = (id: string, name: string): Draft =>
  ({
    id,
    channel: "element",
    baseHash: "h0",
    baseJson: { name: "old" },
    json: { name },
    status: "dirty",
    diagnostics: [],
    conflict: null,
    error: null,
  }) as unknown as Draft;

const entry = (label: string): UndoEntry =>
  ({ label, ids: ["a"], before: [{ name: "a" }], after: [{ name: "b" }], afterHashes: ["h"] }) as unknown as UndoEntry;

describe("editor store", () => {
  beforeEach(() => localStorage.clear());

  it("defaults to the OS theme and has no density preference", () => {
    const s = createEditorStore().getState();
    expect(s.theme).toBe("system");
    expect("density" in s).toBe(false);
    expect(s.workspace).toBe("entities");
  });

  it("remembers a theme override in localStorage", () => {
    const store = createEditorStore();
    store.getState().setTheme("dark");
    expect(localStorage.getItem("mq.theme")).toBe("dark");
    const again = createEditorStore().getState();
    expect(again.theme).toBe("dark");
  });

  it("ignores a corrupt remembered theme", () => {
    localStorage.setItem("mq.theme", "purple");
    expect(createEditorStore().getState().theme).toBe("system");
  });

  it("keeps drafts by element id", () => {
    const store = createEditorStore();
    store.getState().setDraft(draft("a", "A1"));
    store.getState().setDraft(draft("b", "B1"));
    store.getState().patchDraft("a", { status: "saving" });
    expect(store.getState().drafts.a.status).toBe("saving");
    expect((store.getState().drafts.a.json as { name: string }).name).toBe("A1");
    store.getState().removeDraft("a");
    expect(Object.keys(store.getState().drafts)).toEqual(["b"]);
  });

  it("clears the redo stack on a new edit and caps the undo stack", () => {
    const store = createEditorStore();
    store.getState().setStacks([entry("one")], [entry("undone")]);
    store.getState().pushUndo(entry("two"));
    expect(store.getState().redo).toHaveLength(0);
    expect(store.getState().undo.map((e) => e.label)).toEqual(["one", "two"]);
    for (let i = 0; i < 250; i++) store.getState().pushUndo(entry(`e${i}`));
    expect(store.getState().undo.length).toBeLessThanOrEqual(200);
    expect(store.getState().undo.at(-1)?.label).toBe("e249");
  });

  it("toggles panels and selects", () => {
    const store = createEditorStore();
    store.getState().toggle("inspector");
    expect(store.getState().inspectorCollapsed).toBe(true);
    store.getState().toggle("inspector", false);
    expect(store.getState().inspectorCollapsed).toBe(false);
    store.getState().select(["x", "y"], { pointer: "/name" });
    expect(store.getState().selection).toEqual(["x", "y"]);
  });
});
