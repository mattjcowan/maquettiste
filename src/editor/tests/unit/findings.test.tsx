// How findings show outside the Problems panel: the one-line summary under an editor's title, the inspector's list for the
// selected part, severity styling, Problems grouping a part's finding under its element, and the Sync codes dialog.
import { render, screen, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { describe, expect, it } from "vitest";
import type { Diagnostic, ElementSummary } from "@/api/types";
import { ServicesProvider } from "@/app/context";
import { Dialog } from "@/components/ui/dialog";
import { selectedPointer } from "@/inspector/Inspector";
import { FindingsList, FindingsSummary, findingsFor, summaryText } from "@/problems/findings";
import { groupProblems } from "@/problems/group";
import { SyncCodesDialog } from "@/problems/QuickFixButton";
import { codesDescription, codesView } from "@/problems/syncCodesView";
import { IDS, useMockApi } from "./harness";

const d = (
  rule: string,
  severity: Diagnostic["severity"],
  jsonPointer: string | null,
  elementId: string | null = "P",
  filePath: string | null = "p.json",
): Diagnostic => ({ rule, severity, message: `${rule} says`, elementId, filePath, jsonPointer, line: null, column: null }) as Diagnostic;

describe("the one-line summary", () => {
  it("counts each severity that is there, and says nothing when there are none", () => {
    const list = [d("A", "error", "/name"), d("B", "warning", "/states/0"), d("C", "warning", "/states/1"), d("D", "info", null)];
    expect(summaryText(list)).toBe("1 error · 2 warnings · 1 note");
    expect(summaryText([d("B", "warning", null), ...list.slice(2, 3)])).toBe("2 warnings");
    expect(summaryText([])).toBe("");
  });
});

describe("FindingsSummary", () => {
  const api = useMockApi();
  const list = [d("A", "error", "/name"), d("B", "warning", "/states/0"), d("C", "warning", "/states/1")];

  it("shows one line with the right icons and opens the panel on click", async () => {
    const { default: userEvent } = await import("@testing-library/user-event");
    render(
      <ServicesProvider services={api.services}>
        <FindingsSummary diagnostics={list} element="P" />
      </ServicesProvider>,
    );
    const line = screen.getByTestId("editor-findings");
    expect(line).toHaveTextContent("1 error · 2 warnings");
    expect(within(line).getByLabelText("error")).toHaveClass("text-danger");
    expect(within(line).getByLabelText("warning")).toHaveClass("text-warning");
    expect(within(line).queryByLabelText("info")).toBeNull();
    api.services.store.getState().setBottomTab("output");
    await userEvent.click(line);
    expect(api.services.store.getState().bottomTab).toBe("problems");
  });

  it("is absent with no findings", () => {
    render(
      <ServicesProvider services={api.services}>
        <FindingsSummary diagnostics={[]} element="P" />
      </ServicesProvider>,
    );
    expect(screen.queryByTestId("editor-findings")).toBeNull();
  });
});

describe("the inspector's findings for the selection", () => {
  const json = {
    id: "P",
    states: [
      { id: "S0", name: "A", states: [{ id: "S00", name: "A1" }] },
      { id: "S1", name: "B" },
    ],
    transitions: [{ id: "T0" }, { id: "T1" }],
    subjects: [{ id: "X0" }],
  };
  const all = [
    d("N", "error", "/name"),
    d("W", "warning", "/boundAttribute"),
    d("S0", "warning", "/states/0/code"),
    d("S00", "warning", "/states/0/states/0"),
    d("S1", "warning", "/states/1"),
    d("T1", "warning", "/transitions/1/guard"),
    d("SUB", "warning", "/subjects/0/boundAttribute"),
  ];
  const rules = (list: Diagnostic[]) => list.map((x) => x.rule);

  it("shows a part's findings at or under its pointer, and not those of a sibling or a nested sibling", () => {
    expect(rules(findingsFor(all, "/states/0", json))).toEqual(["S0", "S00"]);
    expect(rules(findingsFor(all, "/states/1", json))).toEqual(["S1"]);
    expect(rules(findingsFor(all, "/states/0/states/0", json))).toEqual(["S00"]);
    expect(rules(findingsFor(all, "/transitions/1", json))).toEqual(["T1"]);
    expect(rules(findingsFor(all, "/subjects/0", json))).toEqual(["SUB"]);
    expect(findingsFor(all, "/transitions/0", json)).toEqual([]);
  });

  it("shows the element itself only the findings at its top level that belong to no part", () => {
    expect(rules(findingsFor(all, null, json))).toEqual(["N", "W"]);
    expect(rules(findingsFor([d("X", "info", null)], null, json))).toEqual(["X"]);
  });

  it("finds the pointer of the selected state or transition", () => {
    expect(selectedPointer(json, { process: "P", kind: "state", id: "S00" })).toBe("/states/0/states/0");
    expect(selectedPointer(json, { process: "P", kind: "transition", id: "T1" })).toBe("/transitions/1");
    expect(selectedPointer(json, null)).toBeNull();
    expect(selectedPointer(json, { process: "P", kind: "state", id: "gone" })).toBeNull();
  });
});

describe("severity styling", () => {
  it("shows a warning as a warning and an error as an error, in one list", () => {
    render(<FindingsList diagnostics={[d("E1", "error", "/name"), d("W1", "warning", "/states/0"), d("I1", "info", null)]} />);
    const items = screen.getAllByRole("listitem");
    expect(items.map((li) => li.getAttribute("data-severity"))).toEqual(["error", "warning", "info"]);
    expect(within(items[0]).getByLabelText("error")).toHaveClass("text-danger");
    expect(within(items[1]).getByLabelText("warning")).toHaveClass("text-warning");
    expect(within(items[1]).getByLabelText("warning")).not.toHaveClass("text-danger");
    expect(items[1].querySelector(".text-danger")).toBeNull();
    expect(items[0].querySelector(".text-danger")).not.toBeNull();
  });

  it("renders nothing for no findings", () => {
    const { container } = render(<FindingsList diagnostics={[]} />);
    expect(container).toBeEmptyDOMElement();
  });
});

describe("Problems grouping", () => {
  const row = (id: string, name: string, path: string) => ({ id, kind: "process", name, path }) as unknown as ElementSummary;
  const byId = new Map([["P", row("P", "SalesFlow", ".maquettiste/model/processes/sales.json")]]);

  it("groups a finding at a state's id under the process the file path names", () => {
    const path = ".maquettiste/model/processes/sales.json";
    const groups = groupProblems(
      [d("MQ9210", "warning", "/states/2", "STATE-ID", path), d("MQ9207", "error", "/states/0/code", "STATE-ID", path), d("MQ1", "info", "/name", "P", path)],
      [],
      byId,
    );
    expect(groups).toHaveLength(1);
    expect(groups[0]).toMatchObject({ label: "SalesFlow", elementId: "P" });
    expect(groups[0].diagnostics.map((x) => x.rule)).toEqual(["MQ9207", "MQ9210", "MQ1"]);
  });

  it("falls back to the file path with no element, and to the path of a finding with an unknown id", () => {
    const groups = groupProblems([d("MQ0", "error", null, null, "other.json"), d("MQ0", "error", null, "UNKNOWN", "other2.json")], [], byId);
    expect(groups.map((g) => g.label).sort()).toEqual(["other.json", "other2.json"]);
    expect(groups.every((g) => g.elementId === null)).toBe(true);
  });
});

describe("the Sync codes dialog", () => {
  const api = useMockApi();
  const plan = {
    process: "P",
    entity: IDS.invoice,
    attribute: "A",
    binds: false,
    applied: false,
    diagnostics: [],
    codes: [
      { state: "S0", path: "Draft", code: "DRF", source: "kept" },
      { state: "S1", path: "Issued", code: "ISS", source: "name" },
      { state: "S2", path: "Paid", code: "PD", source: "label" },
    ],
    unmatched: [{ state: "S3", path: "Void", candidates: [] }],
  } as never;

  it("says in words where each code comes from, and lists the states that already have one", () => {
    const view = codesView(plan, undefined);
    expect(view.proposed.map((c) => [c.path, c.from])).toEqual([
      ["Issued", "same as the state's name"],
      ["Paid", 'from the row labeled "Paid"'],
    ]);
    expect(view.kept).toEqual([{ path: "Draft", code: "DRF" }]);
    expect(codesDescription(view, "Invoice.status")).toBe("2 states get a code for Invoice.status. States that already have a code keep it.");
    expect(codesDescription({ ...view, binds: true }, "Invoice.status")).toContain("the lifecycle will also bind this attribute");
  });

  it("names the binding in its title and does not say 'name' as jargon", async () => {
    render(
      <QueryClientProvider client={api.services.queryClient}>
        <Dialog open>
          <SyncCodesDialog
            pending={{ process: "P", entity: IDS.invoice, attribute: "A", plan }}
            error={null}
            busy={false}
            onCancel={() => {}}
            onApply={() => {}}
          />
        </Dialog>
      </QueryClientProvider>,
    );
    const dialog = await screen.findByRole("dialog", { name: /^Codes for Invoice\./ });
    const body = within(dialog).getByTestId("quick-fix-codes");
    expect(
      within(body)
        .getAllByTestId("quick-fix-code-new")
        .map((li) => li.textContent),
    ).toEqual(['Issued gets "ISS", same as the state\'s name', 'Paid gets "PD", from the row labeled "Paid"']);
    expect(within(body).getByTestId("quick-fix-code-none")).toHaveTextContent("Void gets no code");
    expect(within(body).getByTestId("quick-fix-code-kept")).toHaveTextContent('Draft: "DRF"');
    expect(dialog.textContent).not.toContain("(name)");
    expect(dialog.textContent).not.toContain("(label)");
  });
});
