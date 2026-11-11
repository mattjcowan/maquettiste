// The operation editor (next-phase-2-design.md section 4.1, first cut): an operation opens with General and JSON tabs,
// its subject, level and effect read-only, and an edit in its JSON saves; operations are listed under their domain, and
// `each operation` is a unit scope.
import { act, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { App } from "@/app/App";
import { openTab, hasEditor } from "@/editors/tabs";
import { buildForest, nodeOf } from "@/explorer/tree";
import { operationFacts } from "@/editors/operation/OperationEditor";
import { REQUIRE_KINDS } from "@/l10n/model";
import { SCOPE_OPTIONS, scopeHelp } from "@/workspaces/generate/unitsModel";
import { unitScope } from "@/workspaces/generate/previewScope";
import { IDS, useMockApi } from "./harness";

// Monaco does not run under jsdom: the JSON tab's code view is a textarea that reports each change.
vi.mock("@/code", () => ({
  CodeView: ({ label, value, onChange }: { label: string; value: string; onChange?: (value: string) => void }) => (
    <textarea aria-label={label} value={value} onChange={(e) => onChange?.(e.target.value)} />
  ),
  CodeDiff: ({ label, modified }: { label: string; modified: string }) => <textarea aria-label={label} defaultValue={modified} readOnly />,
}));

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const SEND = "01JR5NE0000000000000000010";

const send = {
  kind: "operation",
  id: SEND,
  name: "send",
  package: BILLING,
  subject: IDS.invoice,
  effect: "change",
  input: [{ id: "01JR5NE0000000000000000011", name: "recipient", type: "string", length: 254, required: true }],
  outcomes: [{ id: "01JR5NE0000000000000000012", name: "applied", returns: { entity: IDS.invoice } }],
  errors: [{ id: "01JR5NE0000000000000000015", name: "already_sent" }],
};

describe("operation editor", () => {
  const api = useMockApi();
  beforeAll(() => {
    globalThis.ResizeObserver ??= class {
      observe() {}
      unobserve() {}
      disconnect() {}
    } as unknown as typeof ResizeObserver;
    window.matchMedia ??= ((query: string) => ({
      matches: false,
      media: query,
      addEventListener() {},
      removeEventListener() {},
      addListener() {},
      removeListener() {},
      onchange: null,
      dispatchEvent: () => false,
    })) as unknown as typeof window.matchMedia;
    Element.prototype.scrollTo ??= () => undefined;
  });
  afterEach(() => localStorage.clear());

  it("opens send with General and JSON, edits its JSON and saves", async () => {
    expect(api.backend.model.create(send).outcome).toBe("saved");
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    const store = api.services.store;
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    act(() => store.getState().updateEditors((s) => openTab(s, { id: SEND, kind: "operation" }, { pin: true })));
    const editor = await screen.findByRole("region", { name: "Editor: send" }, { timeout: 5000 });
    expect(editor.dataset.kind).toBe("operation");
    for (const name of ["General", "JSON", "Code generation", "References"]) expect(within(editor).getByRole("tab", { name })).toBeTruthy();
    expect((within(editor).getByLabelText("Subject") as HTMLInputElement).value).toBe("Invoice");
    expect((within(editor).getByLabelText("Level") as HTMLInputElement).value).toBe("record");
    expect((within(editor).getByLabelText("Effect") as HTMLInputElement).value).toBe("change");

    await act(async () => {
      await userEvent.click(within(editor).getByRole("tab", { name: "JSON" }));
    });
    const code = (await within(editor).findByLabelText("Element JSON", {}, { timeout: 5000 })) as HTMLTextAreaElement;
    const edited = { ...(JSON.parse(code.value) as Record<string, unknown>), effect: "read", description: "Sends the invoice to its recipient." };
    act(() => {
      fireEvent.change(code, { target: { value: JSON.stringify(edited, null, 2) } });
    });
    await waitFor(
      () => {
        const saved = api.backend.model.get(SEND)!.json as Record<string, unknown>;
        expect(saved.description).toBe("Sends the invoice to its recipient.");
        expect(saved.effect).toBe("read");
      },
      { timeout: 5000 },
    );
  });

  it("lists an operation under its domain, else its subject's, with the subject and effect beside it", () => {
    const rows = api.backend.model.index();
    const row = (id: string, extra: Record<string, unknown>) =>
      ({ id, kind: "operation", name: id.slice(-4), package: null, tags: [], category: null, stereotypes: [], hash: "h", path: "p", ...extra }) as never;
    const forest = buildForest({
      rows: [
        ...rows,
        row(SEND, { package: BILLING, subject: IDS.invoice, effect: "change" }),
        row("01JR5NE0000000000000000031", { subject: IDS.invoice, effect: "read" }),
      ],
    });
    for (const id of [SEND, "01JR5NE0000000000000000031"]) {
      const node = nodeOf(forest, id)!;
      const folder = forest.parent.get(forest.place.get(id) ?? id)!;
      expect(nodeOf(forest, folder)?.label).toBe("Operations");
      expect(forest.parent.get(folder)).toBe(BILLING);
      expect(node.secondary).toMatch(/^Invoice · (change|read)$/);
    }
  });

  it("lists operations as editable, offers each operation as a scope and counts operation parts in completeness", () => {
    expect(hasEditor("operation")).toBe(true);
    expect(hasEditor("process", { use: "lifecycle" })).toBe(true);
    expect(hasEditor("process", { use: "library" })).toBe(false);
    expect(SCOPE_OPTIONS).toContain("each operation");
    expect(scopeHelp("each operation")).toContain("once per operation");
    expect(unitScope("each operation")).toMatchObject({ indexKind: "operation", variable: "operation", once: false });
    expect(REQUIRE_KINDS).toEqual(expect.arrayContaining(["operation", "operation-member", "outcome", "error"]));
    expect(operationFacts({ kind: "operation", id: SEND, name: "closeMonth" } as never, undefined)).toEqual({ subject: "None", level: "", effect: "read" });
  });
  it("adds a fired event through the two-step picker, lists it with its payload, and stops firing it", async () => {
    const PROCESS = "01JR5NE0000000000000000040";
    const EVENT = "01JR5NE0000000000000000041";
    const STATE = "01JR5NE0000000000000000042";
    expect(api.backend.model.create({ ...send, outcomes: [{ id: "01JR5NE0000000000000000012", name: "applied" }] }).outcome).toBe("saved");
    expect(
      api.backend.model.create({
        kind: "process",
        id: PROCESS,
        name: "InvoiceFlow",
        use: "lifecycle",
        subjects: [{ entity: IDS.invoice }],
        events: [{ id: EVENT, name: "Issue", payload: [{ id: "01JR5NE0000000000000000043", name: "dueDays", type: "int32", required: true }] }],
        states: [{ id: STATE, name: "Draft" }],
        initial: STATE,
      }).outcome,
    ).toBe("saved");
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    act(() => api.services.store.getState().updateEditors((s) => openTab(s, { id: SEND, kind: "operation" }, { pin: true })));
    const editor = await screen.findByRole("region", { name: "Editor: send" }, { timeout: 5000 });
    const fires = within(editor).getByRole("region", { name: "Fires" });
    expect(within(fires).getByText("This operation fires no events.")).toBeTruthy();

    await userEvent.click(within(fires).getByRole("button", { name: /Add event/ }));
    const dialog = await screen.findByTestId("add-event-dialog");
    // The process governing Invoice comes first, under "On Invoice".
    expect(within(within(dialog).getByRole("region", { name: "On Invoice" })).getByRole("button", { name: "InvoiceFlow" })).toBeTruthy();
    await userEvent.click(within(dialog).getByRole("button", { name: "InvoiceFlow" }));
    await userEvent.click(await within(dialog).findByRole("button", { name: /Issue/ }));
    await waitFor(() => expect((api.backend.model.get(SEND)!.json as Record<string, unknown>).fires).toEqual([EVENT]), { timeout: 5000 });
    await waitFor(() => expect(within(fires).getByText("InvoiceFlow › Issue")).toBeTruthy(), { timeout: 5000 });
    expect(within(fires).getByText("dueDays (int32, required)")).toBeTruthy();

    await userEvent.click(within(fires).getByRole("button", { name: "Stop firing Issue" }));
    await waitFor(() => expect((api.backend.model.get(SEND)!.json as Record<string, unknown>).fires ?? []).toEqual([]), { timeout: 5000 });
  });
});
