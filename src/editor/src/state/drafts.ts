// Drafts (phase2-design.md 4.6). An inspector, grid or canvas edit updates drafts[id] at once and
// the views render the draft; after the idle delay (or on blur, Enter or leaving the element) the
// draft is saved with If-Match: baseHash. One save per element is in flight at a time; edits made
// meanwhile stay in the draft and are saved next with the hash the save returned, so the editor's
// own write never comes back as a conflict.
import type { QueryClient } from "@tanstack/react-query";
import type { ElementDocument, ModelJson, SaveResult } from "@/api/types";
import { applySaveResult, keys } from "@/api/queries";
import { ApiProblem } from "@/api/client";
import { clone, jsonEqual } from "@/lib/json";
import type { Draft, EditorStore } from "./store";
import { SNAPSHOT_READ_ONLY, snapshotScope } from "@/api/snapshotScope";

export interface DraftDeps {
  store: EditorStore;
  queryClient: QueryClient;
  saveElement: (id: string, json: ModelJson, hash: string) => Promise<SaveResult>;
  saveDiagram: (id: string, json: ModelJson, hash: string) => Promise<SaveResult>;
  /** Idle delays in milliseconds: 600 for elements, 500 for diagram positions. */
  delays?: { element: number; diagram: number };
  setTimer?: (fn: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

export class DraftManager {
  private readonly timers = new Map<string, unknown>();
  private readonly inflight = new Map<string, Promise<void>>();
  private readonly retriedDiagram = new Set<string>();
  private readonly savedListeners = new Set<(id: string, before: ModelJson | null, after: ModelJson) => void>();
  private readonly delays: { element: number; diagram: number };
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;

  constructor(private readonly deps: DraftDeps) {
    this.delays = deps.delays ?? { element: 600, diagram: 500 };
    this.setTimer = deps.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
    this.clearTimer = deps.clearTimer ?? ((h) => clearTimeout(h as ReturnType<typeof setTimeout>));
  }

  private get state() {
    return this.deps.store.getState();
  }

  /** The document an editor shows for an element: its draft, else the cached server version. */
  current(id: string): ModelJson | undefined {
    return this.state.drafts[id]?.json ?? (this.deps.queryClient.getQueryData<ElementDocument>(keys.element(id))?.json as ModelJson | undefined);
  }

  /**
   * Applies an edit to the element's draft, creating the draft from the cached element (or `base`)
   * when there is none, and schedules the save.
   */
  edit(
    id: string,
    update: (json: ModelJson) => ModelJson | void,
    options: { channel?: Draft["channel"]; base?: ElementDocument; followUp?: boolean; label?: string } = {},
  ): void {
    // As of a snapshot nothing is edited (and nothing could be saved): the view keeps the snapshot's document.
    if (snapshotScope()) {
      this.state.notify(SNAPSHOT_READ_ONLY, "error");
      return;
    }
    let draft = this.state.drafts[id];
    const followUp = !!options.followUp && (draft ? draft.followUp === true : true);
    const label = options.label ?? draft?.label;
    if (!draft) {
      const doc = options.base ?? this.deps.queryClient.getQueryData<ElementDocument>(keys.element(id));
      if (!doc) throw new Error(`Element ${id} is not loaded; it cannot be edited yet.`);
      draft = {
        id,
        channel: options.channel ?? ((doc.json as { kind?: string }).kind === "diagram" ? "diagram" : "element"),
        baseHash: doc.hash,
        baseJson: clone(doc.json as ModelJson),
        json: clone(doc.json as ModelJson),
        status: "dirty",
        diagnostics: [],
        conflict: null,
        error: null,
      };
    }
    if (draft.status === "conflict") return; // the conflict dialog decides first
    const working = clone(draft.json);
    const next = (update(working) ?? working) as ModelJson;
    this.deps.store.getState().setDraft({
      ...draft,
      json: next,
      status: this.inflight.has(id) ? "saving" : "dirty",
      error: null,
      followUp,
      ...(label ? { label } : {}),
    });
    this.schedule(id);
  }

  /** Saves after the idle delay. */
  schedule(id: string, ms?: number): void {
    const draft = this.state.drafts[id];
    if (!draft) return;
    const existing = this.timers.get(id);
    if (existing !== undefined) this.clearTimer(existing);
    this.timers.set(
      id,
      this.setTimer(() => {
        this.timers.delete(id);
        void this.flush(id);
      }, ms ?? this.delays[draft.channel]),
    );
  }

  /** Saves now (blur, Enter, leaving the element). Resolves when that save has settled. */
  async flush(id: string): Promise<void> {
    const timer = this.timers.get(id);
    if (timer !== undefined) {
      this.clearTimer(timer);
      this.timers.delete(id);
    }
    const running = this.inflight.get(id);
    if (running) {
      // Edits made meanwhile are saved once this save answers (settle re-queues them); a flush
      // (blur, undo) must not return before they went out, so save them now.
      await running;
      const after = this.state.drafts[id];
      if (after && after.status === "dirty") return this.flush(id);
      return;
    }
    const draft = this.state.drafts[id];
    if (!draft || draft.status === "conflict") return;
    if (jsonEqual(draft.json, draft.baseJson)) {
      this.state.removeDraft(id);
      return;
    }
    const sent = clone(draft.json);
    this.state.patchDraft(id, { status: "saving" });
    const save = draft.channel === "diagram" ? this.deps.saveDiagram : this.deps.saveElement;
    const promise = save(id, sent, draft.baseHash)
      .then(
        (result) => this.settle(id, sent, result),
        (error: unknown) => {
          const message = error instanceof ApiProblem ? `${error.message} (${error.code})` : String((error as Error)?.message ?? error);
          this.state.patchDraft(id, { status: "dirty", error: message });
          this.requeueIfChanged(id, sent);
        },
      )
      .finally(() => this.inflight.delete(id));
    this.inflight.set(id, promise);
    await promise;
  }

  /**
   * Saves the element's draft now and tells whether the saved version is now what the editor shows: false while a
   * draft remains (refused as invalid, in conflict, or failed), so an operation on the saved version must not go on.
   */
  async flushSaved(id: string): Promise<boolean> {
    // An edit made while the save was in flight is saved next; a refusal or a failure ends the wait.
    for (let round = 0; round < 3; round++) {
      await this.flush(id);
      await this.whenSettled(id);
      const draft = this.state.drafts[id];
      if (!draft) return true;
      if (draft.error || (draft.status !== "dirty" && draft.status !== "saving")) return false;
    }
    return !this.state.drafts[id];
  }

  /** Saves every draft and waits until none is in flight or waiting to be saved. */
  async flushAll(): Promise<void> {
    await Promise.all(Object.keys(this.state.drafts).map((id) => this.flush(id)));
    for (let round = 0; round < 10; round++) {
      const pending = Object.values(this.state.drafts).filter((d) => this.inflight.has(d.id) || d.status === "dirty" || d.status === "saving");
      if (pending.length === 0) return;
      await Promise.all(pending.map((d) => this.flush(d.id)));
    }
  }

  /** Whether any draft is being saved or waits to be saved (invalid and conflicting drafts do not count). */
  hasPending(ids?: readonly string[]): boolean {
    return Object.values(this.state.drafts).some(
      (d) => (!ids || ids.includes(d.id)) && (this.inflight.has(d.id) || d.status === "dirty" || d.status === "saving"),
    );
  }

  /** After a save that did not succeed: an edit made while it was in flight is saved next. */
  private requeueIfChanged(id: string, sent: ModelJson): void {
    const draft = this.state.drafts[id];
    if (!draft || draft.status === "conflict" || jsonEqual(draft.json, sent)) return;
    this.state.patchDraft(id, { status: "dirty" });
    this.schedule(id, 0);
  }

  isInFlight(id: string): boolean {
    return this.inflight.has(id);
  }

  /** Resolves once the element has no save in flight. */
  async whenSettled(id: string): Promise<void> {
    while (this.inflight.has(id)) await this.inflight.get(id);
  }

  /**
   * Calls `listener` after each draft save the server accepted, once its undo step is recorded, with the version the
   * draft started from and the one saved: a follow-up edit made then (a process's diagram following its rename) joins
   * that step. Returns the unsubscribe.
   */
  onSaved(listener: (id: string, before: ModelJson | null, after: ModelJson) => void): () => void {
    this.savedListeners.add(listener);
    return () => this.savedListeners.delete(listener);
  }

  hasUnsaved(): boolean {
    return Object.keys(this.state.drafts).length > 0;
  }

  private settle(id: string, sent: ModelJson, result: SaveResult): void {
    const draft = this.state.drafts[id];
    switch (result.outcome) {
      case "saved": {
        applySaveResult(this.deps.queryClient, result);
        this.retriedDiagram.delete(id);
        const before = draft?.baseJson ?? null;
        // A pan or zoom (view state) and a follow-up edit (a card the canvas placed, a domain's diagram following its
        // domain) join the last undo step instead of taking one of their own: that step then undoes the user's action
        // and its consequence together. The step's `after` for this element is updated (or added), so it still undoes cleanly.
        const top = this.state.undo[this.state.undo.length - 1];
        const slot = top ? top.ids.indexOf(id) : -1;
        const join = !!draft && !!before && (draft.followUp === true || viewportOnly(before, sent));
        if (top && join && (slot < 0 || top.afterHashes[slot] === draft!.baseHash)) {
          const at = slot < 0 ? top.ids.length : slot;
          const ids = [...top.ids];
          const befores = [...top.before];
          const after = [...top.after];
          const afterHashes = [...top.afterHashes];
          if (slot < 0) {
            ids[at] = id;
            befores[at] = clone(before!);
          }
          after[at] = clone(sent);
          afterHashes[at] = result.hash;
          this.state.setStacks([...this.state.undo.slice(0, -1), { ...top, ids, before: befores, after, afterHashes }], this.state.redo);
        } else
          this.state.pushUndo({
            label: draft?.label ?? `Edit ${(sent as { name?: string }).name ?? id}`,
            ids: [id],
            before: [before],
            after: [clone(sent)],
            afterHashes: [result.hash],
          });
        for (const listener of this.savedListeners) listener(id, before, sent);
        if (!draft) return;
        const baseJson = (result.current?.json as ModelJson | undefined) ?? sent;
        if (jsonEqual(draft.json, sent)) {
          this.state.removeDraft(id);
        } else {
          this.state.setDraft({ ...draft, baseHash: result.hash!, baseJson: clone(baseJson), status: "dirty", diagnostics: [], error: null, label: undefined });
          this.schedule(id, 0);
        }
        return;
      }
      case "invalid":
        if (draft) this.state.patchDraft(id, { status: "invalid", diagnostics: result.diagnostics });
        this.requeueIfChanged(id, sent);
        return;
      case "conflict":
        if (!draft) return;
        if (draft.channel === "diagram" && result.current && !this.retriedDiagram.has(id)) {
          // Positions: re-apply ours onto the disk version and retry once (openapi saveDiagram).
          this.retriedDiagram.add(id);
          const merged = reapplyDiagram(result.current.json as ModelJson, draft.json, draft.baseJson);
          this.state.setDraft({
            ...draft,
            baseHash: result.current.hash,
            baseJson: clone(result.current.json as ModelJson),
            json: merged,
            status: "dirty",
          });
          this.schedule(id, 0);
          return;
        }
        this.retriedDiagram.delete(id);
        this.state.patchDraft(id, { status: "conflict", conflict: result.current ?? null, diagnostics: result.diagnostics });
        return;
      case "not-found":
        if (draft) this.state.patchDraft(id, { status: "invalid", diagnostics: result.diagnostics, error: "This element no longer exists." });
        this.requeueIfChanged(id, sent);
        return;
      case "referenced":
        if (draft) this.state.patchDraft(id, { status: "invalid", diagnostics: result.diagnostics, error: "Refused: other elements reference it." });
        this.requeueIfChanged(id, sent);
        return;
    }
  }

  // ---------------------------------------------------------------- conflict dialog

  /** "Keep mine": retry the draft against the disk version's hash. */
  keepMine(id: string): Promise<void> {
    const draft = this.state.drafts[id];
    if (!draft?.conflict) return Promise.resolve();
    this.state.setDraft({ ...draft, baseHash: draft.conflict.hash, baseJson: clone(draft.conflict.json as ModelJson), status: "dirty", conflict: null });
    return this.flush(id);
  }

  /** "Take theirs": drop the draft and show the disk version. */
  takeTheirs(id: string): void {
    const draft = this.state.drafts[id];
    if (draft?.conflict) this.deps.queryClient.setQueryData(keys.element(id), draft.conflict);
    this.state.removeDraft(id);
  }

  /** Manual merge: save the merged document against the disk version's hash. */
  merge(id: string, json: ModelJson): Promise<void> {
    const draft = this.state.drafts[id];
    if (!draft?.conflict) return Promise.resolve();
    this.state.setDraft({
      ...draft,
      json,
      baseHash: draft.conflict.hash,
      baseJson: clone(draft.conflict.json as ModelJson),
      status: "dirty",
      conflict: null,
    });
    return this.flush(id);
  }

  /** Drops a draft without saving (Escape on a pristine edit, or after a delete). */
  discard(id: string): void {
    const timer = this.timers.get(id);
    if (timer !== undefined) this.clearTimer(timer);
    this.timers.delete(id);
    this.state.removeDraft(id);
  }
}

/**
 * Our changes to a diagram over its disk version, after a 409 (openapi saveDiagram): only what our draft changed since
 * `base` (the document it started from) is re-applied, member by member and field by field (x, y, width, height,
 * collapsed), so a state another window moved keeps its new place. A missing `collapsed` reads as false, so an expand is
 * re-applied like a collapse. A member we added is added; one we had from the base and the disk version no longer has
 * (removed elsewhere) is not brought back. The viewport is ours when we changed it.
 */
export function reapplyDiagram(disk: ModelJson, ours: ModelJson, base: ModelJson): ModelJson {
  type Member = { element: string; x?: number; y?: number; collapsed?: boolean; width?: number; height?: number };
  const membersOf = (json: ModelJson) => new Map(((json as { members?: Member[] }).members ?? []).map((m) => [m.element, m]));
  const result = clone(disk) as ModelJson & { members?: Member[]; viewport?: unknown };
  const mine = membersOf(ours);
  const was = membersOf(base);
  const members = result.members ?? [];
  const fields = ["x", "y", "width", "height"] as const;
  for (const member of members) {
    const m = mine.get(member.element);
    if (!m) continue;
    const b = was.get(member.element);
    for (const f of fields) {
      if (m[f] === b?.[f]) continue;
      if (m[f] === undefined) delete member[f];
      else member[f] = m[f];
    }
    const collapsed = m.collapsed === true;
    if (collapsed !== (b?.collapsed === true)) {
      if (collapsed) member.collapsed = true;
      else delete member.collapsed;
    }
  }
  const present = new Set(members.map((m) => m.element));
  for (const m of mine.values()) if (!present.has(m.element) && !was.has(m.element)) members.push(clone(m));
  result.members = members;
  const viewport = (ours as { viewport?: unknown }).viewport;
  if (!jsonEqual(viewport ?? null, (base as { viewport?: unknown }).viewport ?? null)) {
    if (viewport === undefined) delete result.viewport;
    else result.viewport = clone(viewport);
  }
  return result;
}

/** True when two versions of a diagram differ only in their viewport. */
export function viewportOnly(before: ModelJson | null, after: ModelJson): boolean {
  if (!before || (after as { kind?: string }).kind !== "diagram") return false;
  const strip = (json: ModelJson) => {
    const copy = { ...(json as Record<string, unknown>) };
    delete copy.viewport;
    return copy as unknown as ModelJson;
  };
  return jsonEqual(strip(before), strip(after));
}
