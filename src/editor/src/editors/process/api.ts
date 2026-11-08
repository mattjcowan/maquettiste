// The process editor's operations (phase-3-design.md 4.4) over the generated client: the Sync enum plan and apply,
// verify one scenario (Replay), and Refresh expectations (the `refresh-scenario` batch operation). Each change is one
// undo step: the documents before and after the operation.
import type { QueryClient } from "@tanstack/react-query";
import { api, ApiProblem } from "@/api/client";
import type { components } from "@/api/schema";
import * as endpoints from "@/api/endpoints";
import { applyBatchResult, applySaveResult, indexQuery, keys } from "@/api/queries";
import type { ModelJson } from "@/api/types";
import { subjectsOf } from "@/model/process";
import { recordVerify, type VerifyResult } from "@/explorer/processApi";
import { applyQuickFix } from "@/problems/applyQuickFix";
import type { EditorStore } from "@/state/store";

export type SyncEnumResult = components["schemas"]["SyncEnumResult"];

export type SyncCodesResult = components["schemas"]["SyncCodesResult"];

/**
 * Sets, or with no `owner` clears, the owner of a process's one subject (actors-design.md decision 10). `owner` exists only in
 * the `subjects` form, so a process in the one-subject form is converted first (use-subjects). One undo step, from the document
 * as it was to the one written. Null when saved, else the refusal's message.
 */
export async function setSubjectOwner(
  qc: QueryClient,
  store: EditorStore,
  drafts: { flush(id: string): Promise<unknown> },
  change: { process: string; owner: string | undefined; label: string },
): Promise<string | null> {
  await drafts.flush(change.process);
  const before = await endpoints.getElement(change.process);
  const refusal = (result: Awaited<ReturnType<typeof endpoints.applyBatch>>): string | null => {
    if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
    if (result.outcome === "saved") return null;
    const refused = result.items.find((i) => i.outcome !== "saved");
    return refused?.diagnostics[0]?.message ?? `The change was refused (${result.outcome}).`;
  };
  let current = before;
  if (!Array.isArray((before.json as Record<string, unknown>).subjects)) {
    const converted = await endpoints.applyBatch({ operations: [{ op: "use-subjects", elementIds: [change.process] }] } as never);
    const why = refusal(converted);
    if (why) return why;
    if (endpoints.isBatchResult(converted)) applyBatchResult(qc, converted);
    current = await endpoints.getElement(change.process);
  }
  const json = structuredClone(current.json) as Record<string, unknown>;
  const entry = (json.subjects as Record<string, unknown>[] | undefined)?.[0];
  if (!entry) return "The process has no subject.";
  if (change.owner) entry.owner = change.owner;
  else delete entry.owner;
  const result = await endpoints.applyBatch({ operations: [{ op: "update", id: change.process, expectedHash: current.hash, element: json }] } as never);
  const why = refusal(result);
  if (why || !endpoints.isBatchResult(result)) return why ?? "The batch did not parse.";
  applyBatchResult(qc, result);
  store.getState().pushUndo({
    label: change.label,
    ids: [change.process],
    before: [before.json as ModelJson],
    after: [json as ModelJson],
    afterHashes: [result.items[0]?.hash ?? null],
  });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}

/**
 * The sync-enum plan (dry run) or its application; a 422 refusal answers with the result and its diagnostics. `entity`
 * names the subject whose enum is synced; absent, the lifecycle's one enum binding (decision 15).
 */
export async function syncEnum(process: string, dryRun: boolean, entity?: string): Promise<SyncEnumResult> {
  const { data, error, response } = await api().POST("/api/processes/{id}/sync-enum", {
    params: { path: { id: process } },
    body: { dryRun, ...(entity ? { entity } : {}) },
  });
  if (data) return data;
  if (error && typeof error === "object" && "added" in error) return error as SyncEnumResult;
  throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
}

export interface SyncCodesInput {
  entity: string;
  attribute: string;
  stateCodes?: Record<string, string | number>;
}

/** The sync-codes plan (dry run) or its application; a 422 refusal answers with the result and its diagnostics. */
export async function syncCodes(process: string, request: SyncCodesInput & { dryRun: boolean }): Promise<SyncCodesResult> {
  const { data, error, response } = await api().POST("/api/processes/{id}/sync-codes", { params: { path: { id: process } }, body: request });
  if (data) return data;
  if (error && typeof error === "object" && "codes" in error) return error as SyncCodesResult;
  throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
}

/** Applies sync-codes and records it as one undo step on the process (the binding it writes is in the process file). */
export async function applySyncCodes(qc: QueryClient, store: EditorStore, process: string, request: SyncCodesInput): Promise<SyncCodesResult> {
  const before = await endpoints.getElement(process);
  const result = await syncCodes(process, { ...request, dryRun: false });
  if (!result.applied) return result;
  const after = await refetch(qc, process);
  store
    .getState()
    .pushUndo({ label: "Sync codes", ids: [process], before: [before.json as ModelJson], after: [after.json as ModelJson], afterHashes: [after.hash] });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return result;
}

/** Verifies one scenario (Replay); the explorer's statuses follow. */
export async function verifyScenario(process: string, scenario: string): Promise<VerifyResult> {
  const { data, response } = await api().POST("/api/processes/{id}/verify", { params: { path: { id: process } }, body: { scenarios: [scenario] } });
  if (!data) throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
  recordVerify(data);
  return data;
}

async function refetch(qc: QueryClient, id: string) {
  const doc = await endpoints.getElement(id);
  qc.setQueryData(keys.element(id), doc);
  return doc;
}

/** Applies the Sync enum plan and records it as one undo step on the enum. */
export async function applySyncEnum(qc: QueryClient, store: EditorStore, process: string, enumId: string, entity?: string): Promise<SyncEnumResult> {
  const before = await endpoints.getElement(enumId);
  const result = await syncEnum(process, false, entity);
  if (!result.applied) return result;
  const after = await refetch(qc, enumId);
  store
    .getState()
    .pushUndo({ label: "Sync enum", ids: [enumId], before: [before.json as ModelJson], after: [after.json as ModelJson], afterHashes: [after.hash] });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return result;
}

/**
 * Rewrites a scenario's expectations from a replay (refresh-scenario); null when saved, else the refusal. `triggerPoints` also writes each
 * step's `expect.triggerPoints` in the same update (trigger-points-design.md decision 28); without it they are rewritten only where a step
 * already expects them.
 */
export async function refreshScenario(qc: QueryClient, store: EditorStore, scenario: string, triggerPoints = false): Promise<string | null> {
  const before = await endpoints.getElement(scenario);
  const result = await endpoints.applyBatch({
    operations: [{ op: "refresh-scenario", id: scenario, ...(triggerPoints ? { triggerPoints: true } : {}) }],
  } as never);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
  if (result.outcome !== "saved") return result.items.find((i) => i.outcome !== "saved")?.diagnostics[0]?.message ?? result.outcome;
  applyBatchResult(qc, result);
  const after = await refetch(qc, scenario);
  store.getState().pushUndo({
    label: "Refresh expectations",
    ids: [scenario],
    before: [before.json as ModelJson],
    after: [after.json as ModelJson],
    afterHashes: [after.hash],
  });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}

/** Deletes a scenario (nothing refers to one), as one undo step. */
export async function deleteScenario(qc: QueryClient, store: EditorStore, scenario: string, name: string): Promise<string | null> {
  const doc = qc.getQueryData<{ hash: string; json: ModelJson }>(keys.element(scenario)) ?? (await endpoints.getElement(scenario));
  const result = await endpoints.deleteElement(scenario, doc.hash, "refuse");
  if (result.outcome !== "saved") return result.diagnostics[0]?.message ?? `The delete was refused (${result.outcome}).`;
  applySaveResult(qc, result);
  store.getState().pushUndo({ label: `Delete ${name}`, ids: [scenario], before: [doc.json], after: [null], afterHashes: [null] });
  return null;
}

/**
 * Binds a process as an entity's lifecycle, or clears the entity's lifecycle (no `target`), with the `set-lifecycle`
 * batch operation: both sides and the previous partners change in one batch and one undo step (the Use and Subject
 * fields). Pending edits of the documents it reads are saved first. Null when saved, else the refusal's message.
 */
export async function setLifecycle(
  qc: QueryClient,
  store: EditorStore,
  drafts: { flush(id: string): Promise<unknown> },
  change: { process: string; entity: string; target?: string; label: string },
): Promise<string | null> {
  await drafts.flush(change.process);
  await drafts.flush(change.entity);
  const process = (await endpoints.getElement(change.process)).json as Record<string, unknown>;
  const entity = (await endpoints.getElement(change.entity)).json as Record<string, unknown>;
  const str = (v: unknown) => (typeof v === "string" ? v : null);
  // Every document the batch may rewrite: the process, the entity, the entity's named lifecycle, the entities the process
  // governs, and every other process that lists the entity (it may lose the entry, or become an orchestration).
  const rows = await qc.ensureQueryData(indexQuery);
  const peers = rows.filter((r) => r.kind === "process" && (r.subject === change.entity || r.subjects?.includes(change.entity))).map((r) => r.id);
  const ids = [
    ...new Set([change.process, change.entity, str(entity.lifecycle), ...subjectsOf(process).map((e) => e.entity), ...peers].filter((x): x is string => !!x)),
  ];
  for (const id of ids.slice(2)) await drafts.flush(id);
  return applyQuickFix(qc, store, { kind: "operation", op: "set-lifecycle", id: change.entity, target: change.target, label: change.label, ids });
}

/**
 * Makes a lifecycle that uses `subjects` an orchestration (lifecycle-design.md decision 10): its subjects stay and their
 * bindings go. An entity that names it as its lifecycle lets go of it, so it does not name an orchestration (MQ9201). One
 * batch, one undo step. Null when saved, else the refusal's message.
 */
export async function demoteLifecycle(
  qc: QueryClient,
  store: EditorStore,
  drafts: { flush(id: string): Promise<unknown> },
  change: { process: string; label: string },
): Promise<string | null> {
  await drafts.flush(change.process);
  const before = await endpoints.getElement(change.process);
  const process = structuredClone(before.json) as Record<string, unknown>;
  delete process.use;
  delete process.boundAttribute;
  // Only lifecycles hold rights (MQ9901): they stay, as in the engine, and the finding offers to remove them.
  if (Array.isArray(process.subjects))
    process.subjects = (process.subjects as Record<string, unknown>[]).map(({ boundAttribute: _unbound, ...entry }) => entry);
  const docs = [{ id: change.process, before: before.json as ModelJson, hash: before.hash, json: process as ModelJson }];
  for (const entity of subjectsOf(before.json as Record<string, unknown>).flatMap((e) => e.entity ?? [])) {
    await drafts.flush(entity);
    const doc = await endpoints.getElement(entity);
    if ((doc.json as Record<string, unknown>).lifecycle !== change.process) continue;
    const json = structuredClone(doc.json) as Record<string, unknown>;
    delete json.lifecycle;
    docs.push({ id: entity, before: doc.json as ModelJson, hash: doc.hash, json: json as ModelJson });
  }
  const result = await endpoints.applyBatch({
    operations: docs.map((d) => ({ op: "update", id: d.id, expectedHash: d.hash, element: d.json })),
  } as never);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
  if (result.outcome !== "saved") {
    const refused = result.items.find((i) => i.outcome !== "saved");
    return refused?.diagnostics[0]?.message ?? `The change was refused (${result.outcome}).`;
  }
  applyBatchResult(qc, result);
  store.getState().pushUndo({
    label: change.label,
    ids: docs.map((d) => d.id),
    before: docs.map((d) => d.before),
    after: docs.map((d) => d.json),
    afterHashes: docs.map((_, i) => result.items[i]?.hash ?? null),
  });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}

/**
 * Changes the Subject of a lifecycle that has one entry in the `subjects` form: edits the entry in place, drops
 * `lifecycle` from the old entity when it names this process, and gives the new entity this process as its lifecycle when
 * it names none. One batch, one undo step. Null when saved, else the refusal's message.
 */
export async function replaceSoleSubject(
  qc: QueryClient,
  store: EditorStore,
  drafts: { flush(id: string): Promise<unknown> },
  change: { process: string; entity?: string; stereotype?: string; ownAttributes?: string[]; label: string },
): Promise<string | null> {
  await drafts.flush(change.process);
  const before = await endpoints.getElement(change.process);
  const process = structuredClone(before.json) as Record<string, unknown>;
  const entries = (process.subjects as Record<string, unknown>[] | undefined) ?? [];
  const old =
    typeof entries[0]?.entity === "string" ? (entries[0].entity as string) : typeof process.subject === "string" ? (process.subject as string) : undefined;
  if (change.stereotype) {
    // A stereotype entry keeps its bound attribute and owner only when they are the stereotype's own attributes (decision 6).
    const own = (v: unknown) => (typeof v === "string" && change.ownAttributes?.includes(v) ? v : undefined);
    const bound = own(entries[0]?.boundAttribute ?? process.boundAttribute);
    const owner = own(entries[0]?.owner);
    delete process.subject;
    delete process.boundAttribute;
    process.subjects = [{ id: entries[0]?.id, stereotype: change.stereotype, ...(bound ? { boundAttribute: bound } : {}), ...(owner ? { owner } : {}) }];
  } else process.subjects = [{ id: entries[0]?.id, entity: change.entity }];
  const docs = [{ id: change.process, before: before.json as ModelJson, hash: before.hash, json: process as ModelJson }];
  if (old && old !== change.entity) {
    await drafts.flush(old);
    const doc = await endpoints.getElement(old);
    if ((doc.json as Record<string, unknown>).lifecycle === change.process) {
      const json = structuredClone(doc.json) as Record<string, unknown>;
      delete json.lifecycle;
      docs.push({ id: old, before: doc.json as ModelJson, hash: doc.hash, json: json as ModelJson });
    }
  }
  const next = change.entity ? (await drafts.flush(change.entity), await endpoints.getElement(change.entity)) : undefined;
  if (next && change.entity && !(next.json as Record<string, unknown>).lifecycle) {
    const json = structuredClone(next.json) as Record<string, unknown>;
    json.lifecycle = change.process;
    docs.push({ id: change.entity!, before: next.json as ModelJson, hash: next.hash, json: json as ModelJson });
  }
  const result = await endpoints.applyBatch({
    operations: docs.map((d) => ({ op: "update", id: d.id, expectedHash: d.hash, element: d.json })),
  } as never);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
  if (result.outcome !== "saved") {
    const refused = result.items.find((i) => i.outcome !== "saved");
    return refused?.diagnostics[0]?.message ?? `The change was refused (${result.outcome}).`;
  }
  applyBatchResult(qc, result);
  store.getState().pushUndo({
    label: change.label,
    ids: docs.map((d) => d.id),
    before: docs.map((d) => d.before),
    after: docs.map((d) => d.json),
    afterHashes: docs.map((_, i) => result.items[i]?.hash ?? null),
  });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}

/**
 * sync-enum and sync-codes on a stereotype entry go through the batch: the REST endpoints are entity-only (stereotype-sets-design.md
 * decision 10). One undo step on the enum (sync-enum, `enumId`) or the process (sync-codes). Null when saved, else the refusal's message.
 */
export async function syncStereotypeEntry(
  qc: QueryClient,
  store: EditorStore,
  change: { process: string; stereotype: string; op: "sync-enum" | "sync-codes"; enumId?: string; attribute?: string; label: string },
): Promise<string | null> {
  const target = change.op === "sync-enum" ? change.enumId : change.process;
  if (!target) return "The process has no bound enum.";
  const before = await endpoints.getElement(target);
  const operation = { op: change.op, id: change.process, stereotype: change.stereotype, ...(change.attribute ? { attribute: change.attribute } : {}) };
  const result = await endpoints.applyBatch({ operations: [operation] } as never);
  if (!endpoints.isBatchResult(result)) return result.diagnostics[0]?.message ?? "The batch did not parse.";
  if (result.outcome !== "saved")
    return result.items.find((i) => i.outcome !== "saved")?.diagnostics[0]?.message ?? `The sync was refused (${result.outcome}).`;
  applyBatchResult(qc, result);
  const after = await refetch(qc, target);
  store
    .getState()
    .pushUndo({ label: change.label, ids: [target], before: [before.json as ModelJson], after: [after.json as ModelJson], afterHashes: [after.hash] });
  void qc.invalidateQueries({ queryKey: keys.index });
  void qc.invalidateQueries({ queryKey: keys.validation });
  return null;
}

/** The plan Sync enum shows for a stereotype entry: the states against the bound enum's members, read from the two documents. */
export async function stereotypeEnumPlan(process: string, enumId: string): Promise<SyncEnumResult> {
  const states = ((await endpoints.getElement(process)).json as { states?: { name: string; type?: string }[] }).states ?? [];
  const members = ((await endpoints.getElement(enumId)).json as { members?: { name: string }[] }).members ?? [];
  const wanted = states.filter((s) => s.type !== "history" && s.type !== "choice").map((s) => s.name);
  const have = members.map((m) => m.name);
  const kept = have.filter((n) => wanted.includes(n));
  return {
    process,
    enum: enumId,
    added: wanted.filter((n) => !have.includes(n)),
    removed: have.filter((n) => !wanted.includes(n)),
    reordered: kept.join("|") !== wanted.filter((n) => kept.includes(n)).join("|"),
    refused: [],
    applied: false,
    diagnostics: [],
  };
}

export type RightsAnswer = { rightsDefault: "allow" | "deny"; rows: components["schemas"]["RightsRow"][] };

export type TriggerSequenceAnswer = components["schemas"]["TriggerSequence"];

/** What runs, in order, when a verb happens in a lifecycle (`get_trigger_sequence`): the answer is shown as given, never worked out here. */
export async function getTriggerSequence(
  process: string,
  query: { verb: string; state?: string; entity?: string },
  signal?: AbortSignal,
): Promise<TriggerSequenceAnswer> {
  const { data, response } = await api().GET("/api/processes/{id}/trigger-sequence", {
    params: {
      path: { id: process },
      query: { verb: query.verb, ...(query.state ? { state: query.state } : {}), ...(query.entity ? { entity: query.entity } : {}) },
    },
    signal,
  });
  if (!data) throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
  return data;
}

/** The entities a named trigger point runs for, in name order (`GET /api/model/trigger-points/{id}/covers`): shown as given, never worked out here. */
export async function getTriggerPointCovers(id: string, signal?: AbortSignal): Promise<string[]> {
  const { data, response } = await api().GET("/api/model/trigger-points/{id}/covers", { params: { path: { id } }, signal });
  if (!data) throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
  return data.entities;
}

/** Who can do what in a lifecycle (`get_rights`): every page of rows, read from the saved lifecycle. */
export async function getRights(process: string, signal?: AbortSignal, frozen = false): Promise<RightsAnswer> {
  const rows: RightsAnswer["rows"] = [];
  let cursor: string | undefined;
  let rightsDefault: RightsAnswer["rightsDefault"] = "allow";
  do {
    const { data, response } = await api().GET("/api/processes/{id}/rights", {
      params: { path: { id: process }, query: { limit: 1000, ...(frozen ? { frozen: true } : {}), ...(cursor ? { cursor } : {}) } },
      signal,
    });
    if (!data) throw new ApiProblem(response.status, null, `HTTP ${response.status}`);
    rightsDefault = data.rightsDefault;
    rows.push(...data.rows);
    cursor = data.next ?? undefined;
  } while (cursor);
  return { rightsDefault, rows };
}
