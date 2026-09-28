// Recorded engine responses replace the in-house resolver while the mock model is still the fixture.
import { describe, expect, it } from "vitest";
import { setupServer } from "msw/node";
import { MockBackend } from "@/mocks/backend";
import { statefulHandlers } from "@/mocks/handlers";
import { mentions, replayable, type Recording } from "@/mocks/recorded";

const view: Recording = {
  operationId: "getDatabaseView",
  status: 200,
  contentType: "application/json",
  body: { view: { id: "db-1", recorded: true } },
  file: "x",
};
const recorded = new Map([["getDatabaseView", view]]);

describe("replayable recordings", () => {
  it("replays only while the model is pristine and the recording matches", () => {
    expect(replayable(recorded, "getDatabaseView", { pristine: true }, (r) => mentions(r, "db-1"))).toBe(view);
    expect(replayable(recorded, "getDatabaseView", { pristine: true }, (r) => mentions(r, "db-2"))).toBeUndefined();
    expect(replayable(recorded, "getDatabaseView", { pristine: false })).toBeUndefined();
    expect(replayable(recorded, "getPlan", { pristine: true })).toBeUndefined();
  });

  it("the database-view handler serves the recording, then the resolver after an edit", async () => {
    const base = "http://recorded.test";
    const backend = new MockBackend();
    const database = backend.model.index().find((e) => e.kind === "database")!;
    const rec: Recording = { ...view, body: { view: { id: database.id, recorded: true } } };
    const server = setupServer(...statefulHandlers(backend, base, new Map([["getDatabaseView", rec]])));
    server.listen({ onUnhandledRequest: "error" });
    try {
      const first = await (await fetch(`${base}/api/databases/${database.id}/view`)).json();
      expect(first).toEqual(rec.body);
      const entity = backend.model.index().find((e) => e.kind === "entity")!;
      backend.model.externalEdit(entity.id, (json) => {
        json.description = "changed";
      });
      const second = (await (await fetch(`${base}/api/databases/${database.id}/view`)).json()) as { view?: { recorded?: boolean } };
      expect(second.view?.recorded).toBeUndefined();
    } finally {
      server.close();
    }
  });
});
