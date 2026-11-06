// The actor editor's type picker, Member of field, Members section and Add members (actors-design.md section 9), on the mock.
import { act, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { afterEach, beforeAll, describe, expect, it } from "vitest";
import { App } from "@/app/App";
import { openTab } from "@/editors/tabs";
import { useMockApi } from "./harness";

type Json = Record<string, unknown>;

const ID = {
  ann: "01JR5NE0000000000000000A01",
  bob: "01JR5NE0000000000000000A02",
  reviewer: "01JR5NE0000000000000000A03",
  auto: "01JR5NE0000000000000000A04",
  other: "01JR5NE0000000000000000A05",
};

const actor = (id: string, name: string, type: string): Json => ({ kind: "actor", id, name, type });

describe("actor editor membership", () => {
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

  async function open(id: string, name: string) {
    window.history.replaceState(null, "", "/generate");
    render(<App services={api.services} />);
    await waitFor(() => expect(api.services.queryClient.getQueryData(["index"])).toBeTruthy(), { timeout: 5000 });
    act(() => api.services.store.getState().updateEditors((s) => openTab(s, { id, kind: "actor" }, { pin: true })));
    return screen.findByRole("region", { name: `Editor: ${name}` }, { timeout: 5000 });
  }
  const json = (id: string) => api.backend.model.get(id)!.json as Json;

  it("offers the six types, picks a holder in Member of and saves memberOf; Automatic is refused when the project has one", async () => {
    for (const a of [
      actor(ID.ann, "Ann", "person"),
      actor(ID.reviewer, "Reviewer", "role"),
      actor(ID.auto, "Scheduler", "system"),
      actor(ID.other, "Robot", "external-system"),
    ])
      expect(api.backend.model.create(a).outcome).toBe("saved");
    const editor = await open(ID.ann, "Ann");
    const type = within(editor).getByLabelText("Type") as HTMLSelectElement;
    expect([...type.options].map((o) => o.text)).toEqual(["person", "role", "group", "organization", "external system", "Automatic (the system itself)"]);

    // Member of lists roles, groups and organizations with their type, never the actor itself.
    const add = within(editor).getByLabelText("Add to Member of") as HTMLSelectElement;
    const choices = [...add.options].map((o) => o.text);
    expect(choices).toContain("Reviewer (role)");
    expect(choices.some((c) => c.startsWith("Ann"))).toBe(false);
    expect(choices.every((c) => c === "add…" || /\((role|group|organization)\)$/.test(c))).toBe(true);
    await act(async () => {
      await userEvent.selectOptions(add, ID.reviewer);
    });
    await waitFor(() => expect(json(ID.ann).memberOf).toEqual([ID.reviewer]), { timeout: 5000 });

    // A second Automatic actor is refused with the reason, and the type stays.
    await act(async () => {
      await userEvent.selectOptions(type, "system");
    });
    expect((await within(editor).findByTestId("actor-type-refused")).textContent).toContain("Scheduler");
    expect(json(ID.ann).type).toBe("person");
    await act(async () => {
      await userEvent.selectOptions(type, "group");
    });
    await waitFor(() => expect(json(ID.ann).type).toBe("group"), { timeout: 5000 });
    await waitFor(() => expect(within(editor).queryByTestId("actor-type-refused")).toBeNull());
  });

  it("lists a role's members, adds members in one batch that one undo reverts, and offers no cycle", async () => {
    for (const a of [actor(ID.reviewer, "Reviewer", "role"), actor(ID.ann, "Ann", "person"), actor(ID.bob, "Bob", "person"), actor(ID.other, "Board", "group")])
      expect(api.backend.model.create(a).outcome).toBe("saved");
    // Reviewer is a member of Board, so Board cannot be added to Reviewer.
    api.backend.model.externalEdit(ID.reviewer, (j) => void (j.memberOf = [ID.other]));
    api.backend.model.externalEdit(ID.ann, (j) => void (j.memberOf = [ID.reviewer]));
    const editor = await open(ID.reviewer, "Reviewer");
    await act(async () => {
      await userEvent.click(within(editor).getByRole("tab", { name: "General" }));
    });
    const members = await within(editor).findByTestId("actor-members");
    expect(within(members).getByTestId("actor-members-count").textContent).toBe("1");
    expect(within(members).getByTestId("actor-members-list").textContent).toContain("Ann");

    await act(async () => {
      await userEvent.click(within(members).getByTestId("actor-add-members"));
    });
    const dialog = await screen.findByTestId("add-members-dialog");
    expect(within(dialog).queryByTestId("add-member-Board")).toBeNull();
    expect(within(dialog).queryByTestId("add-member-Ann")).toBeNull();
    expect(within(dialog).queryByTestId("add-member-Reviewer")).toBeNull();
    await act(async () => {
      await userEvent.click(within(dialog).getByTestId("add-member-Bob"));
      await userEvent.click(within(dialog).getByTestId("add-members-apply"));
    });
    await waitFor(() => expect(json(ID.bob).memberOf).toEqual([ID.reviewer]), { timeout: 5000 });
    await waitFor(() => expect(within(editor).getByTestId("actor-members-count").textContent).toBe("2"), { timeout: 5000 });

    // One undo step reverts the batch.
    expect(api.services.store.getState().undo.at(-1)?.label).toBe("Add members to Reviewer");
  });
});
