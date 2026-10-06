// "As of" a snapshot (docs/engineering/snapshots.md sections 4 and 10): the one scope the HTTP client and the query client
// read. The as-of reads carry ?snapshot=, the neutral requests go out unchanged, everything else is refused before it leaves
// the page; query keys are hashed with the scope, so a snapshot's documents and the working model's never share an entry;
// the URL's ?snapshot= is the source, and leaving a snapshot drops its cache and reads the working model again.
import { afterEach, describe, expect, it } from "vitest";
import { ApiProblem } from "@/api/client";
import * as endpoints from "@/api/endpoints";
import { createQueryClient, keys } from "@/api/queries";
import {
  AS_OF_READS,
  classifyRequest,
  scopedKeyHash,
  scopedUrl,
  scopeOfHash,
  setSnapshotScope,
  snapshotFromSearch,
  snapshotScope,
  withSnapshotParam,
} from "@/api/snapshotScope";
import { applySnapshotScope, allows, deniedReason } from "@/snapshots/state";
import { filterChanges } from "@/snapshots/CompareView";
import { IDS, useMockApi } from "./harness";

afterEach(() => setSnapshotScope(null));

describe("requests in a snapshot scope", () => {
  it("adds ?snapshot= to the fourteen as-of reads only", () => {
    expect(AS_OF_READS).toHaveLength(14);
    expect(classifyRequest("GET", "/api/model/index")).toBe("as-of");
    expect(classifyRequest("GET", `/api/model/elements/${IDS.invoice}`)).toBe("as-of");
    expect(classifyRequest("POST", "/api/model/elements/read")).toBe("as-of");
    expect(classifyRequest("POST", "/api/validate")).toBe("as-of");
    expect(classifyRequest("GET", "/api/databases/x/tables/y")).toBe("as-of");
    expect(classifyRequest("POST", "/api/templates/preview")).toBe("as-of");
    expect(classifyRequest("GET", "/api/project/settings")).toBe("as-of");
    // Neutral: the same whichever model is shown.
    expect(classifyRequest("GET", "/api/session")).toBe("neutral");
    expect(classifyRequest("GET", "/api/project")).toBe("neutral");
    expect(classifyRequest("PUT", "/api/presence")).toBe("neutral");
    expect(classifyRequest("POST", "/api/snapshots/a-20261005-120000/restore")).toBe("neutral");
    // Refused, with the codes the sign-in gate answers.
    expect(classifyRequest("PUT", `/api/model/elements/${IDS.invoice}`)).toBe("read-only");
    expect(classifyRequest("POST", "/api/model/batch")).toBe("read-only");
    expect(classifyRequest("PUT", "/api/project/settings")).toBe("read-only");
    expect(classifyRequest("GET", "/api/localization")).toBe("unsupported");
    expect(classifyRequest("GET", "/api/model/queries/x/sql")).toBe("unsupported");
  });

  it("rewrites the URL of an as-of read and keeps its other parameters", () => {
    expect(scopedUrl("GET", "http://h/api/model/index?locale=fr", "s-20261005-120000").url).toBe(
      "http://h/api/model/index?locale=fr&snapshot=s-20261005-120000",
    );
    expect(scopedUrl("GET", "/api/model/kinds", "s-1").url).toBe("/api/model/kinds?snapshot=s-1");
    expect(scopedUrl("GET", "/api/session", "s-1")).toEqual({ url: "/api/session", refused: null });
    expect(scopedUrl("DELETE", "/api/model/elements/x", "s-1").refused).toBe("read-only");
    expect(scopedUrl("GET", "/api/model/index", null)).toEqual({ url: "/api/model/index", refused: null });
  });
});

describe("the URL", () => {
  it("carries the snapshot and keeps every other parameter", () => {
    expect(snapshotFromSearch("?sel=a&snapshot=release-1-20261005-120000")).toBe("release-1-20261005-120000");
    expect(snapshotFromSearch("?snapshot=Not%20an%20id")).toBeNull();
    expect(withSnapshotParam("?sel=a", "s-1")).toBe("?sel=a&snapshot=s-1");
    expect(withSnapshotParam("?sel=a&snapshot=s-1", null)).toBe("?sel=a");
    expect(withSnapshotParam("?snapshot=s-1", null)).toBe("");
  });
});

describe("query keys", () => {
  it("hash model keys with the scope and leave the neutral ones alone", () => {
    expect(scopedKeyHash(keys.index, null)).toBe(JSON.stringify(["index"]));
    const asOf = scopedKeyHash(keys.element(IDS.invoice), "s-1");
    expect(asOf).not.toBe(scopedKeyHash(keys.element(IDS.invoice), null));
    expect(scopeOfHash(asOf)).toBe("s-1");
    expect(scopeOfHash(scopedKeyHash(keys.element(IDS.invoice), null))).toBeNull();
    expect(scopedKeyHash(keys.snapshots, "s-1")).toBe(scopedKeyHash(keys.snapshots, null));
    expect(scopedKeyHash(keys.session, "s-1")).toBe(scopedKeyHash(keys.session, null));
  });

  it("keep a snapshot's cache apart from the working model's, and drop it on the way back", () => {
    const qc = createQueryClient();
    qc.setQueryData(keys.element(IDS.invoice), "working");
    applySnapshotScope(qc, "s-1");
    expect(snapshotScope()).toBe("s-1");
    expect(qc.getQueryData(keys.element(IDS.invoice))).toBeUndefined();
    qc.setQueryData(keys.element(IDS.invoice), "as of s-1");
    expect(qc.getQueryData(keys.element(IDS.invoice))).toBe("as of s-1");
    applySnapshotScope(qc, null);
    expect(qc.getQueryData(keys.element(IDS.invoice))).toBe("working");
    // The snapshot's entries are gone; the working ones are stale, so the views read them again.
    expect(
      qc
        .getQueryCache()
        .findAll()
        .filter((q) => scopeOfHash(q.queryHash) === "s-1"),
    ).toHaveLength(0);
    expect(
      qc
        .getQueryCache()
        .find({ queryKey: keys.element(IDS.invoice), exact: true })
        ?.isStale(),
    ).toBe(true);
  });
});

describe("roles", () => {
  it("let viewers open and compare, editors take and export, maintainers restore, delete and import", () => {
    expect(allows("viewer", "open")).toBe(true);
    expect(allows("viewer", "compare")).toBe(true);
    expect(allows("viewer", "take")).toBe(false);
    expect(allows("editor", "take")).toBe(true);
    expect(allows("editor", "export")).toBe(true);
    expect(allows("editor", "restore")).toBe(false);
    expect(allows("maintainer", "restore")).toBe(true);
    expect(allows("maintainer", "import")).toBe(true);
    expect(allows("admin", "delete")).toBe(true);
    expect(deniedReason("editor", "delete")).toBe("Needs the maintainer role (you are editor).");
  });
});

describe("the compare list's filters", () => {
  const rows = [
    { id: "1", kind: "entity", name: "Invoice", change: "changed" as const, path: "model/a.json", previousName: "Bill" },
    { id: "2", kind: "entity", name: "Customer", change: "added" as const, path: "model/b.json" },
    { id: "3", kind: "enum", name: "Status", change: "removed" as const, path: "model/c.json" },
  ];
  it("filter by kind, change (a rename on its own) and name, the previous name included", () => {
    expect(filterChanges(rows, "entity", "all", "").map((r) => r.id)).toEqual(["1", "2"]);
    expect(filterChanges(rows, "", "renamed", "").map((r) => r.id)).toEqual(["1"]);
    expect(filterChanges(rows, "", "removed", "").map((r) => r.id)).toEqual(["3"]);
    expect(filterChanges(rows, "", "all", "bill").map((r) => r.id)).toEqual(["1"]);
  });
});

describe("the client against the mock", () => {
  const mock = useMockApi();

  it("reads a snapshot as of, refuses writes before they leave, and reads the working model again after", async () => {
    const taken = await endpoints.createSnapshot({ name: "Before the rename", includePacks: null, description: null });
    expect(taken.includesPacks).toBe(false);
    mock.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Bill";
    });
    setSnapshotScope(taken.id);
    const old = await endpoints.getElement(IDS.invoice);
    expect((old.json as { name: string }).name).toBe("Invoice");
    const index = await endpoints.getModelIndex();
    expect(index.find((r) => r.id === IDS.invoice)?.name).toBe("Invoice");
    await expect(endpoints.saveElement(IDS.invoice, old.json, old.hash)).rejects.toMatchObject({ code: "snapshot-read-only", status: 409 });
    await expect(endpoints.getLocalization()).rejects.toBeInstanceOf(ApiProblem);
    // Neutral: the snapshot list and the session answer as before.
    expect((await endpoints.listSnapshots()).map((s) => s.id)).toEqual([taken.id]);
    setSnapshotScope(null);
    expect(((await endpoints.getElement(IDS.invoice)).json as { name: string }).name).toBe("Bill");
  });

  it("refuses a request that carries ?snapshot= outside the as-of reads, as the gate does", async () => {
    const taken = await endpoints.createSnapshot({ name: "Gate", includePacks: false, description: null });
    const read = await fetch(mock.url(`/api/localization?snapshot=${taken.id}`));
    expect(read.status).toBe(400);
    expect(((await read.json()) as { code: string }).code).toBe("snapshot-unsupported");
    const missing = await fetch(mock.url("/api/model/index?snapshot=nope-20261005-120000"));
    expect(missing.status).toBe(404);
    const asOf = await fetch(mock.url(`/api/model/kinds?snapshot=${taken.id}`));
    expect(asOf.headers.get("X-Maquettiste-Snapshot")).toBe(taken.id);
  });

  it("compares, restores after a safety snapshot, and undoes by restoring that one", async () => {
    const taken = await endpoints.createSnapshot({ name: "Release 1", includePacks: false, description: null });
    mock.backend.model.externalEdit(IDS.invoice, (json) => {
      json.name = "Bill";
    });
    const comparison = await endpoints.compareSnapshots(taken.id, "working");
    expect(comparison.changed).toBe(1);
    expect(comparison.elements[0]).toMatchObject({ id: IDS.invoice, previousName: "Invoice", name: "Bill" });
    const diff = await endpoints.compareSnapshotElement(taken.id, "working", IDS.invoice);
    expect(diff.fields).toContainEqual({ pointer: "/name", change: "changed", before: "Invoice", after: "Bill" });
    const restored = await endpoints.restoreSnapshot(taken.id, false);
    expect(restored.safety?.id).toMatch(/^before-restore-\d{8}-\d{6}/);
    expect(mock.backend.model.entries.get(IDS.invoice)?.json.name).toBe("Invoice");
    await endpoints.restoreSnapshot(restored.safety!.id, false);
    expect(mock.backend.model.entries.get(IDS.invoice)?.json.name).toBe("Bill");
  });
});
