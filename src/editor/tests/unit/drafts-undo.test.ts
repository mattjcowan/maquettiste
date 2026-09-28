// Drafts, saves, conflicts and undo/redo through the real endpoints against the mock backend.
import { describe, expect, it } from "vitest";
import * as endpoints from "@/api/endpoints";
import { keys } from "@/api/queries";
import { IDS, useMockApi } from "./harness";

type Named = { name: string };

describe("drafts and undo over the mock API", () => {
  const api = useMockApi();

  async function load(id: string) {
    const doc = await endpoints.getElement(id);
    api.services.queryClient.setQueryData(keys.element(id), doc);
    return doc;
  }

  it("saves an edit with If-Match, clears the draft and records an undo entry", async () => {
    const { drafts, store } = api.services;
    await load(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Bill";
    });
    expect(store.getState().drafts[IDS.invoice]?.status).toBe("dirty");
    await drafts.flush(IDS.invoice);
    expect(store.getState().drafts[IDS.invoice]).toBeUndefined();
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Bill");
    expect(store.getState().undo).toHaveLength(1);
  });

  it("undoes and redoes a save as a batch", async () => {
    const { drafts, undo, store } = api.services;
    await load(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Bill";
    });
    await drafts.flush(IDS.invoice);

    const undone = await undo.undo();
    expect(undone.ok).toBe(true);
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Invoice");
    expect(store.getState().redo).toHaveLength(1);

    const redone = await undo.redo();
    expect(redone.ok).toBe(true);
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Bill");
    expect(store.getState().undo).toHaveLength(1);
  });

  it("refuses an undo when the element changed since, leaving the stacks alone", async () => {
    const { drafts, undo, store } = api.services;
    await load(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Bill";
    });
    await drafts.flush(IDS.invoice);
    api.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Changed on disk";
    });
    const result = await undo.undo();
    expect(result.ok).toBe(false);
    expect(store.getState().undo).toHaveLength(1);
    expect(store.getState().redo).toHaveLength(0);
  });

  it("turns a stale base hash into a conflict; Keep mine saves over the disk version", async () => {
    const { drafts, store } = api.services;
    await load(IDS.invoice);
    api.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Theirs";
    });
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Mine";
    });
    await drafts.flush(IDS.invoice);
    const draft = store.getState().drafts[IDS.invoice];
    expect(draft?.status).toBe("conflict");
    expect((draft?.conflict?.json as Named).name).toBe("Theirs");
    await drafts.keepMine(IDS.invoice);
    await drafts.whenSettled(IDS.invoice);
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Mine");
  });

  it("Take theirs drops the draft and keeps the disk version", async () => {
    const { drafts, store } = api.services;
    await load(IDS.invoice);
    api.backend.model.externalEdit(IDS.invoice, (json) => {
      json.description = "edited elsewhere";
    });
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Mine";
    });
    await drafts.flush(IDS.invoice);
    expect(store.getState().drafts[IDS.invoice]?.status).toBe("conflict");
    drafts.takeTheirs(IDS.invoice);
    expect(store.getState().drafts[IDS.invoice]).toBeUndefined();
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Invoice");
  });

  it("drops a draft that returns to its base without saving", async () => {
    const { drafts, store } = api.services;
    await load(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Temp";
    });
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Invoice";
    });
    await drafts.flush(IDS.invoice);
    expect(store.getState().drafts[IDS.invoice]).toBeUndefined();
    expect(store.getState().undo).toHaveLength(0);
  });

  it("saves an edit made while an invalid save was in flight", async () => {
    const { drafts, store } = api.services;
    await load(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "";
    });
    const first = drafts.flush(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Bill";
    });
    await drafts.flush(IDS.invoice);
    await first;
    await drafts.whenSettled(IDS.invoice);
    expect(store.getState().drafts[IDS.invoice]).toBeUndefined();
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Bill");
  });

  it("undo waits for a save queued behind an in-flight one instead of racing it", async () => {
    const { drafts, undo, store } = api.services;
    await load(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Bill";
    });
    void drafts.flush(IDS.invoice);
    drafts.edit(IDS.invoice, (json) => {
      (json as Named).name = "Billx";
    });
    const undone = await undo.undo();
    expect(undone).toEqual({ ok: true, label: "Edit Billx" });
    expect(store.getState().drafts[IDS.invoice]).toBeUndefined();
    expect((api.backend.model.get(IDS.invoice)!.json as Named).name).toBe("Bill");
    expect(store.getState().undo).toHaveLength(1);
  });
});
