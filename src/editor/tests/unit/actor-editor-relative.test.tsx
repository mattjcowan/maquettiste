// The actor editor's "Taken from the object" section and the process editor's Owner picker (actors-design.md section 9), on the mock.
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { openTab } from "@/editors/tabs";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const ID = {
  order: "01JR5NE0000000000000000B01",
  project: "01JR5NE0000000000000000B02",
  manager: "01JR5NE0000000000000000B03",
  notes: "01JR5NE0000000000000000B04",
  lead: "01JR5NE0000000000000000B05",
  projectEnd: "01JR5NE0000000000000000B06",
  orderEnd: "01JR5NE0000000000000000B07",
  relation: "01JR5NE0000000000000000B08",
  actor: "01JR5NE0000000000000000B09",
  reviewer: "01JR5NE0000000000000000B0A",
  process: "01JR5NE0000000000000000B0B",
  entry: "01JR5NE0000000000000000B0C",
  event: "01JR5NE0000000000000000B0D",
  orderKey: "01JR5NE0000000000000000B0E",
  projectKey: "01JR5NE0000000000000000B0F",
  state: "01JR5NE0000000000000000B0G",
};

describe("taken from the object, in the editors", () => {
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

  const json = (id: string) => api.backend.model.get(id)!.json as Json;
  const make = (doc: Json) => {
    const made = api.backend.model.create(doc);
    if (made.outcome !== "saved") throw new Error(JSON.stringify(made.diagnostics));
  };
  function seed(oneSubjectForm = false) {
    make({
      kind: "entity",
      id: ID.order,
      name: "Order",
      key: { attributes: [ID.orderKey] },
      attributes: [
        { id: ID.orderKey, name: "id", type: "int64" },
        { id: ID.manager, name: "accountManager", type: "string" },
        { id: ID.notes, name: "notes", type: "string", collection: true },
      ],
    });
    make({
      kind: "entity",
      id: ID.project,
      name: "Project",
      key: { attributes: [ID.projectKey] },
      attributes: [
        { id: ID.projectKey, name: "id", type: "int64" },
        { id: ID.lead, name: "lead", type: "string" },
      ],
    });
    make({
      kind: "relation",
      id: ID.relation,
      name: "OrderProject",
      relationKind: "association",
      ends: [
        { id: ID.orderEnd, entity: ID.order, role: "order", min: 0, max: "*" },
        { id: ID.projectEnd, entity: ID.project, role: "project", min: 0, max: 1 },
      ],
    });
    make({ kind: "actor", id: ID.reviewer, name: "Reviewer", type: "role" });
    make({ kind: "actor", id: ID.actor, name: "Approver", type: "person" });
    make({ kind: "process", id: ID.process, name: "Review", use: "library" });
    api.backend.model.externalEdit(ID.process, (j) => {
      Object.assign(j, {
        use: "orchestration",
        ...(oneSubjectForm ? { subject: ID.order } : { subjects: [{ id: ID.entry, entity: ID.order }] }),
        events: [{ id: ID.event, name: "go", actors: [ID.actor] }],
        states: [{ id: ID.state, name: "A" }],
      });
    });
  }
  async function open(id: string, kind: string, name: string) {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    act(() => api.services.store.getState().updateEditors((s) => openTab(s, { id, kind } as never, { pin: true })));
    return screen.findByRole("region", { name: `Editor: ${name}` }, { timeout: 5000 });
  }

  it("picks the owner, a field path and a role within a context, says what each means, and clears memberOf", async () => {
    seed();
    api.backend.model.externalEdit(ID.actor, (j) => void (j.memberOf = [ID.reviewer]));
    const editor = await open(ID.actor, "actor", "Approver");
    await act(async () => {
      await userEvent.click(within(editor).getByRole("tab", { name: "General" }));
    });
    const section = await within(editor).findByTestId("actor-relative");
    const form = within(section).getByTestId("actor-relative-form");

    await act(async () => {
      await userEvent.selectOptions(form, "owner");
    });
    await waitFor(() => expect(json(ID.actor).relative).toEqual({ owner: true }), { timeout: 5000 });
    expect(json(ID.actor)).not.toHaveProperty("memberOf");
    expect((await within(section).findByTestId("actor-relative-meaning")).textContent).toBe("Means the order's owner.");

    // A path, step by step from the subject's fields: single-valued ones only, ends marked as leading on.
    await act(async () => {
      await userEvent.selectOptions(form, "path");
    });
    const step = await within(section).findByTestId("actor-relative-step");
    expect([...(step as HTMLSelectElement).options].map((o) => o.text)).toEqual(["(choose)", "id", "accountManager", "project →"]);
    await act(async () => {
      await userEvent.selectOptions(step, ID.projectEnd);
    });
    const next = await within(section).findByTestId("actor-relative-step-1");
    expect([...(next as HTMLSelectElement).options].map((o) => o.text)).toEqual(["(then…)", "id", "lead", "order →"].filter((t) => t !== "order →"));
    await act(async () => {
      await userEvent.selectOptions(next, ID.lead);
    });
    await waitFor(() => expect(json(ID.actor).relative).toEqual({ path: [ID.projectEnd, ID.lead] }), { timeout: 5000 });
    expect((await within(section).findByTestId("actor-relative-meaning")).textContent).toBe("Means the order's project's lead.");

    // Members of a role within a context.
    await act(async () => {
      await userEvent.selectOptions(form, "members-of");
    });
    await act(async () => {
      await userEvent.selectOptions(await within(section).findByTestId("actor-relative-role"), ID.reviewer);
    });
    await waitFor(() => expect(json(ID.actor).relative).toEqual({ membersOf: ID.reviewer }), { timeout: 5000 });
    const within1 = await within(section).findByTestId("actor-relative-within");
    expect([...(within1 as HTMLSelectElement).options].map((o) => o.text)).toEqual(["(the object itself)", "project →"]);
    await act(async () => {
      await userEvent.selectOptions(within1, ID.projectEnd);
    });
    await waitFor(() => expect(json(ID.actor).relative).toEqual({ membersOf: ID.reviewer, within: [ID.projectEnd] }), { timeout: 5000 });
    expect((await within(section).findByTestId("actor-relative-meaning")).textContent).toBe("Means the Reviewer members of the order's project.");

    // Back to a plain actor.
    await act(async () => {
      await userEvent.selectOptions(form, "none");
    });
    await waitFor(() => expect(json(ID.actor)).not.toHaveProperty("relative"), { timeout: 5000 });
  });

  it("sets a subject's owner from the process editor, converting the one-subject form first, and clears it", async () => {
    seed(true);
    const editor = await open(ID.process, "process", "Review");
    await act(async () => {
      await userEvent.click(within(editor).getByRole("button", { name: /Show the details/ }));
    });
    const owner = (await within(editor).findByLabelText("Owner")) as HTMLSelectElement;
    await waitFor(() => expect([...owner.options].map((o) => o.text)).toEqual(["(none)", "id", "accountManager", "project (relation)"]), { timeout: 5000 });
    await act(async () => {
      await userEvent.selectOptions(owner, ID.manager);
    });
    await waitFor(() => expect((json(ID.process).subjects as Json[])?.[0]?.owner).toBe(ID.manager), { timeout: 5000 });
    expect(json(ID.process)).not.toHaveProperty("subject");
    expect(api.services.store.getState().undo.at(-1)?.label).toBe("Set the owner of Review");
    await act(async () => {
      await userEvent.selectOptions(owner, "");
    });
    await waitFor(() => expect((json(ID.process).subjects as Json[])[0]).not.toHaveProperty("owner"), { timeout: 5000 });
  });
});
