// The assistant panel's logic, kept apart from React so it is tested alone: the entries an answer's events build, the
// proposal card's states, the "small change" rule of auto-apply, the context chips and the conflict check before Apply.
import type { AssistContext, AssistEntry, AssistEvent, AssistProposal } from "@/api/assist";
import type { Diagnostic, ElementSummary } from "@/api/types";

/** An entry as the panel shows it: a tool row also says whether it is still running. */
export type ChatEntry = AssistEntry & { running?: boolean };

/** The entries after one event of an answer. `conversation` and `final` change nothing but a final text no delta brought. */
export function applyEvent(entries: readonly ChatEntry[], event: AssistEvent): ChatEntry[] {
  const last = entries[entries.length - 1];
  switch (event.type) {
    case "text": {
      if (last?.type === "assistant") return [...entries.slice(0, -1), { ...last, text: last.text + event.delta }];
      return [...entries, { type: "assistant", text: event.delta }];
    }
    case "tool-started":
      return [...entries, { type: "tool", id: event.id, name: event.name, arguments: event.arguments, isError: false, summary: "", running: true }];
    case "tool-finished": {
      const at = entries.findIndex((e) => e.type === "tool" && e.id === event.id);
      const row: ChatEntry = {
        type: "tool",
        id: event.id,
        name: event.name,
        arguments: at >= 0 ? (entries[at] as { arguments: unknown }).arguments : null,
        isError: event.isError,
        summary: event.summary,
        running: false,
      };
      if (at < 0) return [...entries, row];
      return entries.map((e, i) => (i === at ? row : e));
    }
    case "proposal":
      return [...entries, { type: "proposal", proposal: event.proposal }];
    case "error":
      return [...entries, { type: "error", code: event.code, message: event.message }];
    case "final": {
      if (event.text && !(last?.type === "assistant")) return [...entries, { type: "assistant", text: event.text }];
      return [...entries];
    }
    default:
      return [...entries];
  }
}

/** Every tool row still running stops (the stream ended or was stopped). */
export function settleRunning(entries: readonly ChatEntry[]): ChatEntry[] {
  return entries.map((e) => (e.type === "tool" && e.running ? { ...e, running: false } : e));
}

/** A proposal card's state: the stored state, or what the panel is doing with it. */
export type CardState =
  | { kind: "pending" }
  | { kind: "applying" }
  | { kind: "applied" }
  | { kind: "discarded" }
  | { kind: "conflict"; message: string }
  | { kind: "failed"; message: string };

export type CardAction =
  { type: "apply" } | { type: "applied" } | { type: "conflict"; message: string } | { type: "failed"; message: string } | { type: "discard" };

export function initialCardState(proposal: AssistProposal): CardState {
  return { kind: proposal.state };
}

/** The card's next state; an applied or discarded card stays so, and only a pending (or retried) card starts applying. */
export function nextCardState(state: CardState, action: CardAction): CardState {
  if (state.kind === "applied" || state.kind === "discarded") return state;
  switch (action.type) {
    case "apply":
      return state.kind === "applying" ? state : { kind: "applying" };
    case "applied":
      return state.kind === "applying" ? { kind: "applied" } : state;
    case "conflict":
      return { kind: "conflict", message: action.message };
    case "failed":
      return { kind: "failed", message: action.message };
    case "discard":
      return state.kind === "applying" ? state : { kind: "discarded" };
  }
}

/** Whether the card can still be applied or discarded. */
export const isOpen = (state: CardState): boolean => state.kind === "pending" || state.kind === "conflict" || state.kind === "failed";

/** The most operations a "small" proposal has; auto-apply takes only those, and never a delete. */
export const SMALL_OPERATIONS = 3;

export function isSmallProposal(proposal: AssistProposal): boolean {
  const ops = proposal.operations as { op?: unknown }[];
  return ops.length > 0 && ops.length <= SMALL_OPERATIONS && !ops.some((o) => o.op === "delete");
}

/**
 * The elements a proposal saw that are no longer as it saw them: a changed or deleted file whose element's index hash is not
 * the proposal's `beforeHash`, or a created element whose id is taken now. Names come from the index, else the file.
 */
export function staleElements(proposal: AssistProposal, index: ReadonlyMap<string, ElementSummary>): string[] {
  const out: string[] = [];
  for (const file of proposal.files) {
    if (!file.id) continue;
    const row = index.get(file.id);
    if (file.action === "created") {
      if (row) out.push(row.name ?? file.name ?? file.id);
    } else if (file.beforeHash && row && row.hash !== file.beforeHash) out.push(row.name ?? file.name ?? file.id);
  }
  return [...new Set(out)];
}

/** The message "Ask again" sends after a conflict. */
export function askAgainMessage(proposal: AssistProposal, changed: string[]): string {
  const what = changed.length ? `${changed.join(", ")} changed` : "the model changed";
  return `Your proposal "${proposal.summary}" could not be applied: ${what} since you proposed it. Read the current documents again and propose the change again.`;
}

/** A context chip the user can remove before sending. */
export type ChipKey = "workspace" | "element" | "selection" | "problems";

export interface ContextChip {
  key: ChipKey;
  label: string;
}

export interface ContextInput {
  workspace: string | null;
  workspaceLabel: string | null;
  element: { id: string; name: string | null; kind: string | null } | null;
  selection: string[];
  problems: { errors: number; warnings: number; infos: number; diagnostics: readonly Diagnostic[] } | null;
}

/** The chips the panel offers for what the user is looking at. */
export function contextChips(input: ContextInput): ContextChip[] {
  const chips: ContextChip[] = [];
  if (input.workspace) chips.push({ key: "workspace", label: input.workspaceLabel ?? input.workspace });
  if (input.element) chips.push({ key: "element", label: input.element.name ?? input.element.id });
  if (input.selection.length > 0) chips.push({ key: "selection", label: `${input.selection.length} selected` });
  if (input.problems) chips.push({ key: "problems", label: `${input.problems.errors} errors, ${input.problems.warnings} warnings` });
  return chips;
}

/** The context sent with a message: only the chips the user kept; null when none is left. */
export function contextOf(input: ContextInput, removed: ReadonlySet<ChipKey>): AssistContext | null {
  const keep = new Set(
    contextChips(input)
      .map((c) => c.key)
      .filter((k) => !removed.has(k)),
  );
  if (keep.size === 0) return null;
  const context: AssistContext = {};
  if (keep.has("workspace")) context.workspace = input.workspace;
  if (keep.has("element") && input.element) {
    context.elementId = input.element.id;
    context.elementName = input.element.name;
    context.elementKind = input.element.kind;
  }
  if (keep.has("selection")) context.selection = input.selection.slice(0, 200);
  if (keep.has("problems") && input.problems) {
    const p = input.problems;
    context.problems = {
      errors: p.errors,
      warnings: p.warnings,
      infos: p.infos,
      top: p.diagnostics.slice(0, 20).map((d) => ({ rule: d.rule, message: d.message, elementId: d.elementId ?? null })),
    };
  }
  return context;
}

/** A short title for a conversation the server has not named yet. */
export function titleOf(message: string): string {
  const line = message.trim().split(/\r?\n/)[0] ?? "";
  return line.length > 60 ? `${line.slice(0, 59)}…` : line;
}

/** Pretty JSON for the diff, or the text as it is when it is not JSON (a sidecar). */
export function prettyText(text: string | null): string {
  if (text === null) return "";
  try {
    return `${JSON.stringify(JSON.parse(text), null, 2)}\n`;
  } catch {
    return text;
  }
}
