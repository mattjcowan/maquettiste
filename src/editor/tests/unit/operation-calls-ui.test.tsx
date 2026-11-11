// The editor's parts of processes linked to operations (processes-operations-design.md sections 7 and 12), over the mock API: the Calls
// dialog's rows (given, empty, not givable), Apply keeping inputs it has no row for, the Fires section placing events without reading
// the model, and the quick fix of MQ9026 opening the Calls dialog once.
import { act, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, describe, expect, it, vi } from "vitest";
import { App } from "@/app/App";
import { ServicesProvider } from "@/app/context";
import { indexQuery } from "@/api/queries";
import { focusProcess } from "@/app/processFocus";
import { CallsDialog } from "@/editors/process/CallsEditor";
import { useSetContext } from "@/editors/process/SetsEditor";
import { FiresSection } from "@/editors/operation/FiresSection";
import { openTab } from "@/editors/tabs";
import type { CallDoc, ProcessDoc } from "@/model/process";
import { IDS, useMockApi } from "./harness";

// Monaco does not run under jsdom.
vi.mock("@/code", () => ({
  CodeView: ({ label, value }: { label: string; value: string }) => <textarea aria-label={label} defaultValue={value} readOnly />,
  CodeDiff: ({ label, modified }: { label: string; modified: string }) => <textarea aria-label={label} defaultValue={modified} readOnly />,
}));

const BILLING = "01J92P0V01KDRN8GX5PGYCNKSX";
const u = (n: number) => `01JR6CA${String(n).padStart(19, "0")}`;
const LOOSE = u(1);
const OP = u(2);
const PROC = u(3);
const E_ISSUE = u(4);
const S_DRAFT = u(5);
const ACT = u(6);
const T_ISSUE = u(7);
const OTHER_PROC = u(8);
const OTHER_EVENT = u(9);
const ONE = u(10);
const ONE_EVENT = u(11);

const members = [
  { id: u(20), name: "dueDays", type: "int32", required: true },
  { id: u(21), name: "note", type: "string" },
  { id: u(22), name: "attachment", file: true, required: true },
  { id: u(23), name: "loose", keyOf: LOOSE, required: true },
  { id: u(24), name: "picture", type: "binary", required: true },
];

function process(extra: Record<string, unknown> = {}): ProcessDoc {
  return {
    kind: "process",
    id: PROC,
    name: "InvoiceFlow",
    use: "lifecycle",
    subjects: [{ id: u(30), entity: IDS.invoice }],
    events: [{ id: E_ISSUE, name: "Issue" }],
    states: [
      { id: S_DRAFT, name: "Draft" },
      { id: u(31), name: "Issued" },
    ],
    initial: S_DRAFT,
    transitions: [{ id: T_ISSUE, source: S_DRAFT, trigger: "event", event: E_ISSUE, targets: [u(31)] }],
    actions: [{ id: ACT, name: "RequestIssue", call: { operation: OP } }],
    ...extra,
  } as unknown as ProcessDoc;
}

function setupGlobals() {
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
}

describe("processes linked to operations, editor parts", () => {
  const api = useMockApi();
  beforeAll(setupGlobals);
  afterEach(() => localStorage.clear());

  const seed = async () => {
    const create = (json: Record<string, unknown>) => {
      const r = api.backend.model.create(json as never);
      expect(JSON.stringify(r), String(json.name)).toContain('"saved"');
    };
    create({ kind: "entity", id: LOOSE, name: "Loose", package: BILLING, abstract: true, attributes: [] });
    create({
      kind: "operation",
      id: OP,
      name: "issue",
      package: BILLING,
      subject: IDS.invoice,
      effect: "change",
      input: members,
      outcomes: [{ id: u(25), name: "applied" }],
    });
    await api.services.queryClient.fetchQuery(indexQuery);
  };

  const Host = ({ children }: { children: React.ReactNode }) => (
    <ServicesProvider services={api.services}>
      <QueryClientProvider client={api.services.queryClient}>{children}</QueryClientProvider>
    </ServicesProvider>
  );

  function Dialog({ doc, onApply }: { doc: ProcessDoc; onApply: (call: CallDoc | null) => void }) {
    const context = useSetContext(doc);
    return <CallsDialog process={doc} action={ACT} context={context} onApply={onApply} onClose={() => undefined} />;
  }

  it("the Calls dialog says it runs nothing, shows Yes and No, and offers a value only where a call can give one", async () => {
    await seed();
    render(
      <Host>
        <Dialog doc={process()} onApply={() => undefined} />
      </Host>,
    );
    expect(screen.getByTestId("calls-runs-nothing")).toHaveTextContent(
      "Runs nothing in the simulator. Your system makes this call; the simulator only records it.",
    );
    const rows = await screen.findAllByTestId("call-input-row");
    await waitFor(() => expect(screen.getAllByTestId("call-input-supplied")).toHaveLength(3));
    const row = (name: string) => rows.find((r) => r.getAttribute("data-member") === name)!;
    expect(within(row("dueDays")).getByText("Yes")).toBeTruthy();
    expect(within(row("note")).getByText("No")).toBeTruthy();
    // A file, the key of an entity with no key, and a binary member are the team's to supply.
    for (const name of ["attachment", "loose", "picture"])
      expect(within(row(name)).getByTestId("call-input-supplied")).toHaveTextContent("Supplied by your system");
    // A required input left empty shows MQ9026's text; an optional one does not.
    expect(within(row("dueDays")).getByTestId("call-input-missing")).toBeTruthy();
    expect(within(row("note")).queryByTestId("call-input-missing")).toBeNull();
  });

  it("Apply keeps a stored input the dialog has no row for, and gives the value typed", async () => {
    await seed();
    const stored: CallDoc = { operation: OP, inputs: [{ id: u(40), member: u(99), value: { value: 1 } }] };
    const onApply = vi.fn();
    render(
      <Host>
        <Dialog doc={process({ actions: [{ id: ACT, name: "RequestIssue", call: stored }] })} onApply={onApply} />
      </Host>,
    );
    await userEvent.type(await screen.findByRole("textbox", { name: "Value of dueDays" }), "30");
    await userEvent.click(screen.getByTestId("calls-apply"));
    expect(onApply).toHaveBeenCalledTimes(1);
    const call = onApply.mock.calls[0][0] as CallDoc;
    expect(call.inputs).toEqual([
      { id: u(40), member: u(99), value: { value: 1 } },
      { id: expect.any(String), member: u(20), value: { text: "30" } },
    ]);
  });

  it("the Fires section places each fired event by reading its one holder, never the other processes", async () => {
    await seed();
    const create = (json: Record<string, unknown>) => expect(JSON.stringify(api.backend.model.create(json as never))).toContain('"saved"');
    const flow = (id: string, name: string, entity: string, event: string, extra: Record<string, unknown> = {}) =>
      create({
        kind: "process",
        id,
        name,
        use: "lifecycle",
        subjects: [{ entity }],
        events: [{ id: event, name: `${name}Event`, payload: [{ id: u(60), name: "amount", type: "int32", required: true }] }],
        states: [{ id: `${id}S`.slice(-26).padStart(26, "0"), name: "Only" }],
        initial: `${id}S`.slice(-26).padStart(26, "0"),
        ...extra,
      });
    // Two fired events sit in processes about other entities; a third process holds nothing the operation fires.
    flow(OTHER_PROC, "PaymentFlow", IDS.payment, OTHER_EVENT);
    flow(ONE, "LooseFlow", LOOSE, ONE_EVENT);
    flow(u(70), "UnrelatedFlow", IDS.invoice, u(71));
    await api.services.queryClient.invalidateQueries();
    await api.services.queryClient.fetchQuery(indexQuery);
    const edit = vi.fn();
    render(
      <Host>
        <FiresSection operation={OP} json={{ fires: [OTHER_EVENT, ONE_EVENT] }} subject={IDS.invoice} problems={[]} edit={edit} />
      </Host>,
    );
    await waitFor(() => expect(screen.getAllByTestId("fired-event")[0]).toHaveTextContent("PaymentFlow › PaymentFlowEvent"), { timeout: 5000 });
    await waitFor(() => expect(screen.getAllByTestId("fired-event")[1]).toHaveTextContent("LooseFlow › LooseFlowEvent"), { timeout: 5000 });
    expect(screen.getAllByTestId("fired-event")[0]).toHaveTextContent("amount (int32, required)");
    // Documents read: the two holders only (queries keyed by the fired ids), and nothing for any process of the index.
    const cached = api.services.queryClient
      .getQueryCache()
      .getAll()
      .filter((q) => q.queryKey[0] === "element" && q.state.data !== undefined)
      .map((q) => String(q.queryKey[1]));
    expect(cached.sort()).toEqual([OTHER_EVENT, ONE_EVENT].sort());
    // Stop firing removes only the clicked entry.
    await userEvent.click(within(screen.getAllByTestId("fired-event")[0]).getByRole("button", { name: /Stop firing/ }));
    const j: Record<string, unknown> = {};
    (edit.mock.calls[0][0] as (j: Record<string, unknown>) => void)(j);
    expect(j.fires).toEqual([ONE_EVENT]);
  });

  it("a duplicate fired id gets its own row, and Stop firing removes the clicked one only", async () => {
    await seed();
    const edit = vi.fn();
    render(
      <Host>
        <FiresSection operation={OP} json={{ fires: [E_ISSUE, E_ISSUE] }} subject={IDS.invoice} problems={[]} edit={edit} />
      </Host>,
    );
    const rows = await screen.findAllByTestId("fired-event");
    expect(rows).toHaveLength(2);
    await userEvent.click(within(rows[1]).getByRole("button", { name: /Stop firing/ }));
    const j: Record<string, unknown> = {};
    (edit.mock.calls[0][0] as (j: Record<string, unknown>) => void)(j);
    expect(j.fires).toEqual([E_ISSUE]);
  });

  it("the quick fix of MQ9026 opens the Calls dialog once, again when used again, and not when the tab is revisited", async () => {
    await seed();
    expect(api.backend.model.create({ kind: "process", ...(process() as unknown as Record<string, unknown>) } as never).outcome).toBe("saved");
    await api.services.queryClient.invalidateQueries();
    await api.services.queryClient.fetchQuery({ ...indexQuery, staleTime: 0 });
    window.history.replaceState(null, "", "/");
    render(<App services={api.services} />);
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    act(() => api.services.store.getState().updateEditors((s) => openTab(s, { id: PROC, kind: "process" }, { pin: true })));
    await screen.findByRole("region", { name: "Editor: InvoiceFlow" }, { timeout: 8000 });
    act(() => void focusProcess(PROC, "transitions", ACT));
    expect(await screen.findByTestId("calls-dialog", undefined, { timeout: 5000 })).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Cancel" }));
    await waitFor(() => expect(screen.queryByTestId("calls-dialog")).toBeNull());
    // Away and back: nothing opens.
    await userEvent.click(screen.getByRole("tab", { name: "Events" }));
    await userEvent.click(screen.getByRole("tab", { name: "Transitions" }));
    await screen.findByRole("grid", { name: /Actions of InvoiceFlow/ });
    expect(screen.queryByTestId("calls-dialog")).toBeNull();
    // The same fix used again opens it again.
    act(() => void focusProcess(PROC, "transitions", ACT));
    expect(await screen.findByTestId("calls-dialog", undefined, { timeout: 5000 })).toBeTruthy();
  });
});
