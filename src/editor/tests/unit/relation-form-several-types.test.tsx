// The relation form's Type choice per end (several-types-design.md section 11): One entity, Several types with chips and a picker (entities, then
// a Stereotypes group), the limits with their tooltips, and the way back to One entity.
import { useState } from "react";
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { MemoryRouter } from "react-router";
import { describe, expect, it } from "vitest";
import { ServicesProvider } from "@/app/context";
import { indexQuery } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { RelationFields } from "@/inspector/fields";
import { IDS, useMockApi } from "./harness";

type Json = Record<string, unknown>;

/** The form over a document held in state, so an edit shows at once as it does in the editor. */
function Form({ initial, onJson }: { initial: Json; onJson: (json: Json) => void }) {
  const [json, setJson] = useState<Json>(initial);
  return (
    <RelationFields
      id="R"
      json={json as unknown as ModelJson}
      doc={undefined}
      diagnostics={[]}
      flush={() => undefined}
      withAttributes={false}
      edit={(update) =>
        setJson((current) => {
          const next = structuredClone(current);
          update(next as unknown as ModelJson);
          onJson(next);
          return next;
        })
      }
    />
  );
}

describe("the relation form's Type choice", () => {
  const api = useMockApi();

  async function mount(ends: Json[], extra: Json = {}) {
    const rows = await api.services.queryClient.fetchQuery(indexQuery);
    const entities = rows.filter((r) => r.kind === "entity");
    let last: Json = {};
    const initial = { kind: "relation", id: "R", name: "Link", relationKind: "association", ...extra, ends };
    render(
      <ServicesProvider services={api.services}>
        <QueryClientProvider client={api.services.queryClient}>
          <MemoryRouter>
            <Form initial={initial} onJson={(j) => (last = j)} />
          </MemoryRouter>
        </QueryClientProvider>
      </ServicesProvider>,
    );
    await waitFor(() => expect(screen.getAllByLabelText("Type")).toHaveLength(ends.length));
    return { entities, last: () => last };
  }

  const end = (id: string, extra: Json): Json => ({ id, role: `r${id}`, ...extra });

  it("shows One entity for a one-type end and writes a list when Several types is chosen", async () => {
    const { last } = await mount([end("a", { entity: IDS.invoice }), end("b", { entity: IDS.payment })]);
    const [first] = screen.getAllByLabelText("Type") as HTMLSelectElement[];
    expect(first.value).toBe("one");
    expect(
      within(first)
        .getAllByRole("option")
        .map((o) => o.textContent),
    ).toEqual(["One entity", "Several types", "Any entity"]);
    fireEvent.change(first, { target: { value: "several" } });
    // Nothing is written yet: a list of one entity is a warning (MQ3042), so the end waits for its second entry.
    expect((last().ends as Json[] | undefined)?.[0]).toBeUndefined();
    // The chip and the picker are there; the picker offers entities, then a Stereotypes group when stereotypes apply to entities.
    expect(screen.getAllByTestId("end-0-chip")).toHaveLength(1);
    const picker = screen.getByLabelText("Add a type to end 1") as HTMLSelectElement;
    fireEvent.change(picker, { target: { value: `entity:${IDS.payment}` } });
    expect(((last().ends as Json[])[0].types as Json[]).map((t) => t.entity)).toEqual([IDS.invoice, IDS.payment]);
    expect(screen.getAllByTestId("end-0-chip").map((c) => c.textContent)).toEqual(["Invoice", "Payment"]);
  });

  it("opens a relation with several types at both ends, with no limit text", async () => {
    await mount([
      end("a", { types: [{ entity: IDS.invoice }, { entity: IDS.payment }] }),
      end("b", { types: [{ entity: IDS.invoice }, { entity: IDS.payment }] }),
    ]);
    const [first, second] = screen.getAllByLabelText("Type") as HTMLSelectElement[];
    expect([first.value, second.value]).toEqual(["several", "several"]);
    expect(screen.queryByText("Only one end may have several types.")).toBeNull();
  });

  it("offers every choice on the other end once one end has several types", async () => {
    await mount([end("a", { entity: IDS.invoice }), end("b", { types: [{ entity: IDS.invoice }, { entity: IDS.payment }] })]);
    const [first, second] = screen.getAllByLabelText("Type") as HTMLSelectElement[];
    expect(second.value).toBe("several");
    const options = within(first).getAllByRole("option") as HTMLOptionElement[];
    expect(options.map((o) => [o.textContent, o.disabled])).toEqual([
      ["One entity", false],
      ["Several types", false],
      ["Any entity", false],
    ]);
    expect(first.title).toBe("");
    expect(screen.queryByTestId("end-0-type-limit")).toBeNull();
  });

  it("keeps only One entity on an n-ary relation", async () => {
    await mount([end("a", { entity: IDS.invoice }), end("b", { entity: IDS.payment }), end("c", { entity: IDS.invoice })], { relationKind: "n-ary" });
    const [first] = screen.getAllByLabelText("Type") as HTMLSelectElement[];
    expect(first.title).toBe("An n-ary relation's ends name one entity each.");
    expect((within(first).getByRole("option", { name: "Several types" }) as HTMLOptionElement).disabled).toBe(true);
  });

  it("leaves Any entity out of a composition's part end", async () => {
    await mount([end("a", { entity: IDS.invoice, max: "*" }), end("b", { entity: IDS.payment, max: 1 })], { relationKind: "composition" });
    const [part, owner] = screen.getAllByLabelText("Type") as HTMLSelectElement[];
    expect((within(part).getByRole("option", { name: "Any entity" }) as HTMLOptionElement).disabled).toBe(true);
    expect((within(part).getByRole("option", { name: "Several types" }) as HTMLOptionElement).disabled).toBe(false);
    expect((within(owner).getByRole("option", { name: "Any entity" }) as HTMLOptionElement).disabled).toBe(false);
  });

  it("goes back to One entity: a single entry writes entity, several entries need a pick", async () => {
    const { last } = await mount([end("a", { types: [{ entity: IDS.invoice }, { entity: IDS.payment }] }), end("b", { entity: IDS.payment })]);
    const [first] = screen.getAllByLabelText("Type") as HTMLSelectElement[];
    fireEvent.change(first, { target: { value: "one" } });
    // The file keeps its list until a pick; the entity picker asks for one.
    expect(last().ends).toBeUndefined();
    const entity = (await screen.findByLabelText("Entity", { selector: "#end-0-entity" })) as HTMLSelectElement;
    expect(entity.value).toBe("");
    fireEvent.change(entity, { target: { value: IDS.payment } });
    expect((last().ends as Json[])[0]).toEqual({ id: "a", role: "ra", entity: IDS.payment });
  });

  it("removes a chip, and keeps the last one", async () => {
    const { last } = await mount([end("a", { entity: IDS.invoice }), end("b", { types: [{ entity: IDS.invoice }, { entity: IDS.payment }] })]);
    const chips = screen.getAllByTestId("end-1-chip");
    fireEvent.click(within(chips[0]).getByRole("button"));
    expect(((last().ends as Json[])[1].types as Json[]).map((t) => t.entity)).toEqual([IDS.payment]);
    const only = screen.getByTestId("end-1-chip");
    expect(within(only).getByRole("button")).toBeDisabled();
    expect(within(only).getByRole("button")).toHaveAttribute("title", "An end needs at least one type. Choose One entity or Any entity instead.");
  });
});
