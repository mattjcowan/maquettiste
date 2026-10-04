// The assistant drawer (SPEC Section 14 "Assist", erratum E44): a conversation with the site's AI over the host, streamed
// from POST /api/assist/chat. Tool calls show as compact rows; model changes arrive as proposals the user reviews and
// applies through the model batch (one undo step) or discards. The provider and its key are the host's, never the project's.
import { useCallback, useEffect, useMemo, useRef, useState, type KeyboardEvent } from "react";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, ChevronRight, Trash2, Wrench, X } from "lucide-react";
import {
  deleteAssistConversation,
  getAssistConversation,
  getAssistStatus,
  listAssistConversations,
  setAssistProposalState,
  streamAssistChat,
  type AssistProposal,
  type AssistStatus,
} from "@/api/assist";
import { ApiProblem } from "@/api/client";
import { useIndex, useValidation } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditorNavigation } from "@/app/navigation";
import { Button } from "@/components/ui/button";
import { CheckboxField } from "@/components/ui/checkbox";
import { Spinner } from "@/components/ui/misc";
import { activeTab } from "@/editors/tabs";
import { local } from "@/lib/storage";
import { cn } from "@/lib/cn";
import { indexLookup } from "@/model/index";
import { SCREEN_LABELS } from "@/model/labels";
import { useEditor } from "@/state/store";
import { applyProposal } from "./apply";
import { Markdown } from "./markdown";
import { ProposalCard } from "./ProposalCard";
import {
  applyEvent,
  askAgainMessage,
  contextChips,
  contextOf,
  initialCardState,
  isSmallProposal,
  nextCardState,
  settleRunning,
  titleOf,
  type CardAction,
  type CardState,
  type ChatEntry,
  type ChipKey,
  type ContextInput,
} from "./model";

export const assistKeys = {
  status: ["assist", "status"] as const,
  conversations: ["assist", "conversations"] as const,
};

/** The per-user "Apply small changes without asking" choice (this browser). */
export const AUTO_APPLY_KEY = "mq.assistant.autoApply";

/** The drawer, when open, on the right of the shell. */
export function AssistantDrawer() {
  const { store } = useServices();
  const open = useEditor(store, (s) => s.assistantOpen);
  if (!open) return null;
  return (
    <aside
      id="mq-assistant"
      aria-label="Assistant"
      data-testid="assistant-panel"
      className="flex min-h-0 w-[400px] max-w-[45vw] shrink-0 flex-col border-l border-default bg-surface"
    >
      <AssistantPanel />
    </aside>
  );
}

function useContextInput(): ContextInput {
  const { store } = useServices();
  const workspace = useEditor(store, (s) => s.workspace);
  const selection = useEditor(store, (s) => s.selection);
  const tab = useEditor(store, (s) => activeTab(s.editors));
  const index = useIndex();
  const validation = useValidation();
  return useMemo(() => {
    const lookup = indexLookup(index.data);
    const id = tab?.id ?? selection[0] ?? null;
    const row = id ? lookup.byId.get(id) : undefined;
    const report = validation.data;
    return {
      workspace,
      workspaceLabel: SCREEN_LABELS[workspace],
      element: id ? { id, name: row?.name ?? null, kind: row?.kind ?? null } : null,
      selection,
      problems: report ? { errors: report.errors, warnings: report.warnings, infos: report.infos, diagnostics: report.diagnostics } : null,
    };
  }, [workspace, selection, tab, index.data, validation.data]);
}

export function AssistantPanel() {
  const { store, queryClient: qc } = useServices();
  const { openSettings } = useEditorNavigation();
  const status = useQuery({ queryKey: assistKeys.status, queryFn: getAssistStatus });
  const configured = status.data?.configured === true;
  const conversations = useQuery({ queryKey: assistKeys.conversations, queryFn: listAssistConversations, enabled: configured });
  const [conversationId, setConversationId] = useState<string | null>(null);
  const [entries, setEntries] = useState<ChatEntry[]>([]);
  const [cards, setCards] = useState<Record<string, CardState>>({});
  const [streaming, setStreaming] = useState(false);
  const [input, setInput] = useState("");
  const [removed, setRemoved] = useState<Set<ChipKey>>(new Set());
  const [autoApply, setAutoApply] = useState(() => local.get(AUTO_APPLY_KEY) === "true");
  const conversationRef = useRef<string | null>(null);
  const abortRef = useRef<AbortController | null>(null);
  const autoApplyRef = useRef(autoApply);
  const canApplyRef = useRef(false);
  const listRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLTextAreaElement>(null);
  const contextInput = useContextInput();
  const chips = contextChips(contextInput).filter((c) => !removed.has(c.key));
  const canApply = status.data?.canApply === true;
  canApplyRef.current = canApply;
  autoApplyRef.current = autoApply;

  useEffect(() => inputRef.current?.focus(), [configured]);
  useEffect(() => {
    const list = listRef.current;
    if (list) list.scrollTop = list.scrollHeight;
  }, [entries]);
  useEffect(() => () => abortRef.current?.abort(), []);

  const dispatch = useCallback((proposal: AssistProposal, action: CardAction) => {
    setCards((c) => ({ ...c, [proposal.id]: nextCardState(c[proposal.id] ?? initialCardState(proposal), action) }));
  }, []);

  const apply = useCallback(
    async (proposal: AssistProposal) => {
      dispatch(proposal, { type: "apply" });
      try {
        const outcome = await applyProposal(qc, store, proposal);
        if (!outcome.ok) {
          dispatch(proposal, { type: outcome.conflict ? "conflict" : "failed", message: outcome.message });
          return;
        }
        dispatch(proposal, { type: "applied" });
        store.getState().notify(`Applied: ${proposal.summary}`);
        const id = conversationRef.current;
        if (id) await setAssistProposalState(id, proposal.id, "applied").catch(() => undefined);
      } catch (e) {
        dispatch(proposal, { type: "failed", message: e instanceof Error ? e.message : String(e) });
      }
    },
    [qc, store, dispatch],
  );

  const discard = useCallback(
    async (proposal: AssistProposal) => {
      dispatch(proposal, { type: "discard" });
      const id = conversationRef.current;
      if (id) await setAssistProposalState(id, proposal.id, "discarded").catch(() => undefined);
    },
    [dispatch],
  );

  const send = useCallback(
    async (text: string) => {
      const message = text.trim();
      if (!message || abortRef.current) return;
      const context = contextOf(contextInput, removed);
      setEntries((e) => [...e, { type: "user", text: message, context }]);
      setInput("");
      setRemoved(new Set());
      const controller = new AbortController();
      abortRef.current = controller;
      setStreaming(true);
      try {
        await streamAssistChat(
          { conversationId: conversationRef.current, message, context },
          (event) => {
            if (event.type === "conversation") {
              conversationRef.current = event.conversationId;
              setConversationId(event.conversationId);
              return;
            }
            setEntries((e) => applyEvent(e, event));
            if (event.type === "proposal") {
              const proposal = event.proposal;
              setCards((c) => ({ ...c, [proposal.id]: initialCardState(proposal) }));
              if (autoApplyRef.current && canApplyRef.current && proposal.state === "pending" && isSmallProposal(proposal)) void apply(proposal);
            } else if (event.type === "final") {
              qc.setQueryData(assistKeys.status, (s: AssistStatus | undefined) => (s ? { ...s, usedToday: event.usedToday } : s));
            }
          },
          controller.signal,
        );
      } catch (e) {
        if (controller.signal.aborted) setEntries((x) => [...x, { type: "error", code: "stopped", message: "Stopped." }]);
        else if (e instanceof ApiProblem) {
          setEntries((x) => [...x, { type: "error", code: e.code, message: e.message }]);
          if (e.code === "assist-not-configured") void qc.invalidateQueries({ queryKey: assistKeys.status });
        } else setEntries((x) => [...x, { type: "error", code: "internal", message: e instanceof Error ? e.message : String(e) }]);
      } finally {
        setEntries((x) => settleRunning(x));
        abortRef.current = null;
        setStreaming(false);
        void qc.invalidateQueries({ queryKey: assistKeys.conversations });
      }
    },
    [contextInput, removed, qc, apply],
  );

  const stop = () => abortRef.current?.abort();

  const startNew = () => {
    conversationRef.current = null;
    setConversationId(null);
    setEntries([]);
    setCards({});
    inputRef.current?.focus();
  };

  const load = async (id: string) => {
    if (!id) return startNew();
    try {
      const conversation = await getAssistConversation(id);
      conversationRef.current = conversation.id;
      setConversationId(conversation.id);
      setEntries(conversation.entries);
      const states: Record<string, CardState> = {};
      for (const entry of conversation.entries) if (entry.type === "proposal") states[entry.proposal.id] = initialCardState(entry.proposal);
      setCards(states);
    } catch (e) {
      store.getState().notify(e instanceof Error ? e.message : String(e), "error");
    }
  };

  const clear = async () => {
    const id = conversationRef.current;
    if (!id) return startNew();
    try {
      await deleteAssistConversation(id);
    } catch (e) {
      store.getState().notify(e instanceof Error ? e.message : String(e), "error");
      return;
    }
    startNew();
    void qc.invalidateQueries({ queryKey: assistKeys.conversations });
  };

  const onKey = (e: KeyboardEvent<HTMLTextAreaElement>) => {
    if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
      e.preventDefault();
      void send(input);
    }
  };

  const items = conversations.data ?? [];
  const known = conversationId && !items.some((c) => c.id === conversationId);

  return (
    <div className="flex min-h-0 flex-1 flex-col">
      <div className="flex h-8 shrink-0 items-center gap-1 border-b border-default px-2">
        <h2 className="text-13 font-semibold">Assistant</h2>
        {configured ? (
          <>
            <select
              aria-label="Conversation"
              className="ml-1 h-6 min-w-0 flex-1 rounded-control border border-input bg-app px-1 text-12"
              value={conversationId ?? ""}
              disabled={streaming}
              onChange={(e) => void load(e.target.value)}
              data-testid="assistant-conversations"
            >
              <option value="">New conversation</option>
              {known ? <option value={conversationId}>{titleOf(entries.find((x) => x.type === "user")?.text ?? "This conversation")}</option> : null}
              {items.map((c) => (
                <option key={c.id} value={c.id}>
                  {c.title}
                </option>
              ))}
            </select>
            <Button size="sm" onClick={startNew} disabled={streaming} data-testid="assistant-new">
              New
            </Button>
            <Button
              variant="ghost"
              size="icon"
              label="Clear conversation"
              onClick={() => void clear()}
              disabled={streaming || (!conversationId && entries.length === 0)}
              data-testid="assistant-clear"
            >
              <Trash2 />
            </Button>
          </>
        ) : (
          <span className="flex-1" />
        )}
        <Button variant="ghost" size="icon" label="Close the assistant" shortcut="Ctrl+I" onClick={() => store.getState().setAssistantOpen(false)}>
          <X />
        </Button>
      </div>

      {status.isPending ? (
        <div className="p-2">
          <Spinner label="Loading the assistant" />
        </div>
      ) : status.isError ? (
        <p role="alert" className="p-2 text-12 text-danger">
          The assistant's status could not be read: {status.error instanceof Error ? status.error.message : String(status.error)}
        </p>
      ) : !configured ? (
        <div className="flex flex-col gap-2 p-2 text-13" data-testid="assistant-not-configured">
          <p className="font-medium">The assistant is not configured.</p>
          <p className="text-12 text-secondary">
            It uses the AI provider of the host that serves this editor, never a key stored in Maquettiste. In the host's management UI, add a provider and its
            key under AI › providers, then pick it and the model for this site under Sites › your site › AI.
          </p>
          <div>
            <Button size="sm" onClick={() => openSettings("assistant")} data-testid="assistant-open-settings">
              Open Settings › Assistant
            </Button>
          </div>
        </div>
      ) : (
        <>
          <div ref={listRef} className="flex min-h-0 flex-1 flex-col gap-2 overflow-y-auto p-2" aria-live="polite" data-testid="assistant-messages">
            {entries.length === 0 ? (
              <p className="text-12 text-secondary">
                Ask about the model, or describe a change: the assistant reads the model with its tools and proposes changes you review before anything is
                saved.
              </p>
            ) : null}
            {entries.map((entry, i) => (
              <Entry
                key={i}
                entry={entry}
                cards={cards}
                canApply={canApply}
                busy={streaming}
                onApply={(p) => void apply(p)}
                onDiscard={(p) => void discard(p)}
                onAskAgain={(p, message) => void send(askAgainMessage(p, message ? [message.replace(/ changed since the proposal\.$/, "")] : []))}
              />
            ))}
            {streaming && !entries.some((e) => e.type === "tool" && e.running) ? (
              <span className="text-12 text-secondary" data-testid="assistant-thinking">
                Thinking…
              </span>
            ) : null}
          </div>
          <div className="flex shrink-0 flex-col gap-1 border-t border-default p-2">
            {chips.length ? (
              <ul className="flex flex-wrap gap-1" aria-label="Context sent with the message">
                {chips.map((chip) => (
                  <li
                    key={chip.key}
                    className="flex items-center gap-0.5 rounded-full border border-default bg-app pl-2 pr-0.5 text-11"
                    data-testid={`assistant-chip-${chip.key}`}
                  >
                    <span className="max-w-40 truncate">{chip.label}</span>
                    <Button
                      variant="ghost"
                      size="icon-row"
                      label={`Remove ${chip.label} from the context`}
                      onClick={() => setRemoved((r) => new Set(r).add(chip.key))}
                    >
                      <X />
                    </Button>
                  </li>
                ))}
              </ul>
            ) : null}
            <textarea
              ref={inputRef}
              aria-label="Message the assistant"
              className="min-h-14 w-full resize-y rounded-control border border-input bg-app p-1.5 text-13"
              placeholder="Ask, or describe a change (Enter sends, Shift+Enter for a new line)"
              value={input}
              onChange={(e) => setInput(e.target.value)}
              onKeyDown={onKey}
              data-testid="assistant-input"
            />
            <div className="flex items-center gap-2">
              <CheckboxField
                id="assistant-auto-apply"
                label="Apply small changes without asking"
                checked={autoApply}
                onChange={(v) => {
                  setAutoApply(v);
                  local.set(AUTO_APPLY_KEY, v ? "true" : "false");
                }}
              />
              <span className="ml-auto" />
              {streaming ? (
                <Button size="sm" onClick={stop} data-testid="assistant-stop">
                  Stop
                </Button>
              ) : (
                <Button size="sm" variant="primary" onClick={() => void send(input)} disabled={!input.trim()} data-testid="assistant-send">
                  Send
                </Button>
              )}
            </div>
            {status.data ? (
              <span className="text-11 text-secondary" data-testid="assistant-usage">
                {status.data.model ?? "The site's model"} · {status.data.usedToday.toLocaleString()} of {status.data.tokenBudgetPerDayPerUser.toLocaleString()}{" "}
                tokens today
              </span>
            ) : null}
          </div>
        </>
      )}
    </div>
  );
}

function Entry({
  entry,
  cards,
  canApply,
  busy,
  onApply,
  onDiscard,
  onAskAgain,
}: {
  entry: ChatEntry;
  cards: Record<string, CardState>;
  canApply: boolean;
  busy: boolean;
  onApply: (p: AssistProposal) => void;
  onDiscard: (p: AssistProposal) => void;
  onAskAgain: (p: AssistProposal, message: string | null) => void;
}) {
  switch (entry.type) {
    case "user":
      return (
        <div className="ml-6 self-end rounded-control bg-accent-subtle px-2 py-1 text-13 whitespace-pre-wrap break-words" data-testid="assistant-user">
          {entry.text}
        </div>
      );
    case "assistant":
      return (
        <div data-testid="assistant-answer">
          <Markdown text={entry.text} />
        </div>
      );
    case "tool":
      return <ToolRow entry={entry} />;
    case "proposal": {
      const state = cards[entry.proposal.id] ?? initialCardState(entry.proposal);
      return (
        <ProposalCard
          proposal={entry.proposal}
          state={state}
          canApply={canApply}
          busy={busy}
          onApply={() => onApply(entry.proposal)}
          onDiscard={() => onDiscard(entry.proposal)}
          onAskAgain={() => onAskAgain(entry.proposal, state.kind === "conflict" ? state.message : null)}
        />
      );
    }
    case "error":
      return (
        <p
          role={entry.code === "stopped" ? undefined : "alert"}
          className={cn("text-12", entry.code === "stopped" ? "text-secondary" : "text-danger")}
          data-testid="assistant-error"
        >
          {entry.message}
        </p>
      );
  }
}

function ToolRow({ entry }: { entry: Extract<ChatEntry, { type: "tool" }> }) {
  const [open, setOpen] = useState(false);
  return (
    <div className={cn("rounded-control border border-default text-12", entry.isError && "border-danger")} data-testid="assistant-tool" data-tool={entry.name}>
      <button
        type="button"
        className="flex w-full items-center gap-1 px-1.5 py-0.5 text-left hover:bg-accent-subtle"
        aria-expanded={open}
        title={open ? "Hide the call" : "Show the call's arguments and result"}
        onClick={() => setOpen(!open)}
      >
        {open ? <ChevronDown className="size-3.5 shrink-0" aria-hidden /> : <ChevronRight className="size-3.5 shrink-0" aria-hidden />}
        <Wrench className="size-3.5 shrink-0 text-secondary" aria-hidden />
        <span className="shrink-0 font-mono">{entry.name}</span>
        <span className={cn("truncate", entry.isError ? "text-danger" : "text-secondary")}>{entry.running ? "running…" : entry.summary}</span>
      </button>
      {open ? (
        <div className="flex flex-col gap-1 border-t border-default p-1.5">
          <pre className="max-h-40 overflow-auto whitespace-pre-wrap break-all font-mono text-11">{JSON.stringify(entry.arguments, null, 2)}</pre>
          {entry.summary ? <p className={cn(entry.isError ? "text-danger" : "text-secondary")}>{entry.summary}</p> : null}
        </div>
      ) : null}
    </div>
  );
}
