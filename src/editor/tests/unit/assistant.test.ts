// The assistant panel (erratum E44): the event-stream parser, the entries an answer builds, the proposal card's states, the
// small-change rule, the context chips, the conflict check, the Markdown renderer, Settings › Assistant's draft, and the
// mock assistant end to end (stream, apply as one undo step, proposal state, contract shapes).
import { describe, expect, it } from "vitest";
import { eventsOf, setAssistChatBase, SseParser, streamAssistChat, type AssistEvent, type AssistProposal } from "@/api/assist";
import type { ElementSummary, SettingsJson } from "@/api/types";
import {
  applyEvent,
  askAgainMessage,
  contextChips,
  contextOf,
  initialCardState,
  isOpen,
  isSmallProposal,
  nextCardState,
  settleRunning,
  staleElements,
  type ChatEntry,
  type ContextInput,
} from "@/assist/model";
import { parseInline, parseMarkdown } from "@/assist/markdown";
import { applyProposal, touchedIds } from "@/assist/apply";
import { entityProposal } from "@/mocks/assist";
import { validatorAt } from "@/mocks/contract";
import { NODE_BASE_URL } from "@/mocks/node";
import { assistantOf, assistantProblems, withAssistant } from "@/workspaces/settings/assistantSettings";
import { useMockApi } from "./harness";

const proposal = (operations: { op: string }[], extra: Partial<AssistProposal> = {}): AssistProposal => ({
  id: "01M3MNY0HTVJQSH4BX78RWN8F7",
  toolCallId: "call_1",
  summary: "Do it",
  state: "pending",
  createdUtc: "2026-10-03T00:00:00Z",
  operations: operations as never,
  files: [],
  ...extra,
});

describe("SseParser", () => {
  it("reads events split anywhere, with CRLF, CR, comments and several data lines", () => {
    const text = 'event: text\r\ndata: {"type":"text","delta":"a"}\r\n\r\n: keep-alive\n\nevent: final\ndata: line one\ndata: line two\r\r';
    const whole = new SseParser().push(text);
    expect(whole).toEqual([
      { event: "text", data: '{"type":"text","delta":"a"}' },
      { event: "final", data: "line one\nline two" },
    ]);
    // The same text one character at a time (a CRLF split between pieces counts once).
    const parser = new SseParser();
    const pieces = [...text].flatMap((c) => parser.push(c));
    expect([...pieces, ...parser.end()]).toEqual(whole);
  });

  it("flushes an event without its blank line at the end, and skips data that is not an event", () => {
    const parser = new SseParser();
    expect(parser.push('data: {"type":"text","delta":"x"}')).toEqual([]);
    const tail = parser.end();
    expect(eventsOf(tail)).toEqual([{ type: "text", delta: "x" }]);
    expect(
      eventsOf([
        { event: "message", data: "not json" },
        { event: "message", data: "{}" },
      ]),
    ).toEqual([]);
  });
});

describe("the entries of an answer", () => {
  it("appends text, tool rows, proposals and errors in order, and a final text no delta brought", () => {
    const p = proposal([{ op: "create" }]);
    const events: AssistEvent[] = [
      { type: "conversation", conversationId: p.id, title: "t" },
      { type: "text", delta: "Let me " },
      { type: "text", delta: "look." },
      { type: "tool-started", id: "call_1", name: "get_model_index", arguments: { kind: "entity" } },
      { type: "tool-finished", id: "call_1", name: "get_model_index", isError: false, summary: "5 entities" },
      { type: "proposal", proposal: p },
      { type: "final", text: "Done.", stopReason: "stop", turns: 2, inputTokens: 1, outputTokens: 1, usedToday: 2, limited: null },
    ];
    const entries = events.reduce<ChatEntry[]>((e, ev) => applyEvent(e, ev), []);
    expect(entries.map((e) => e.type)).toEqual(["assistant", "tool", "proposal", "assistant"]);
    expect(entries[0]).toEqual({ type: "assistant", text: "Let me look." });
    expect(entries[1]).toMatchObject({ name: "get_model_index", summary: "5 entities", running: false, arguments: { kind: "entity" } });
    expect(entries[3]).toEqual({ type: "assistant", text: "Done." });
    const running = applyEvent([], { type: "tool-started", id: "c", name: "validate", arguments: {} });
    expect(running[0]).toMatchObject({ running: true });
    expect(settleRunning(running)[0]).toMatchObject({ running: false });
  });
});

describe("the proposal card", () => {
  it("moves pending → applying → applied, and stays applied", () => {
    let s = initialCardState(proposal([]));
    expect(s).toEqual({ kind: "pending" });
    s = nextCardState(s, { type: "apply" });
    expect(s.kind).toBe("applying");
    expect(nextCardState(s, { type: "discard" }).kind).toBe("applying");
    s = nextCardState(s, { type: "applied" });
    expect(s.kind).toBe("applied");
    expect(nextCardState(s, { type: "discard" }).kind).toBe("applied");
    expect(isOpen(s)).toBe(false);
  });

  it("goes to conflict (still open, can be applied again or discarded) and to discarded", () => {
    let s = nextCardState(nextCardState({ kind: "pending" }, { type: "apply" }), { type: "conflict", message: "Invoice changed since the proposal." });
    expect(s).toEqual({ kind: "conflict", message: "Invoice changed since the proposal." });
    expect(isOpen(s)).toBe(true);
    s = nextCardState(s, { type: "discard" });
    expect(s.kind).toBe("discarded");
    expect(nextCardState(s, { type: "apply" }).kind).toBe("discarded");
    expect(initialCardState(proposal([], { state: "applied" })).kind).toBe("applied");
  });

  it("calls a proposal small when it has at most three operations and no delete", () => {
    expect(isSmallProposal(proposal([{ op: "create" }]))).toBe(true);
    expect(isSmallProposal(proposal([{ op: "create" }, { op: "update" }, { op: "update" }]))).toBe(true);
    expect(isSmallProposal(proposal([{ op: "create" }, { op: "update" }, { op: "update" }, { op: "update" }]))).toBe(false);
    expect(isSmallProposal(proposal([{ op: "delete" }]))).toBe(false);
    expect(isSmallProposal(proposal([]))).toBe(false);
  });

  it("finds the elements that changed since the proposal", () => {
    const row = (id: string, name: string, hash: string) => ({ id, name, hash }) as unknown as ElementSummary;
    const p = proposal([{ op: "update" }], {
      files: [
        { path: "a.json", action: "changed", id: "A", kind: "entity", name: "A", beforeHash: "h1", before: "{}", after: "{}" },
        { path: "b.json", action: "deleted", id: "B", kind: "entity", name: "B", beforeHash: "h2", before: "{}", after: null },
        { path: "c.json", action: "created", id: "C", kind: "entity", name: "C", beforeHash: null, before: null, after: "{}" },
      ],
    });
    expect(staleElements(p, new Map([["A", row("A", "Invoice", "h1")]]))).toEqual([]);
    const index = new Map([
      ["A", row("A", "Invoice", "changed")],
      ["B", row("B", "Payment", "h2")],
      ["C", row("C", "Taken", "x")],
    ]);
    expect(staleElements(p, index)).toEqual(["Invoice", "Taken"]);
    expect(askAgainMessage(p, ["Invoice"])).toContain('"Do it" could not be applied: Invoice changed');
    expect(touchedIds(p)).toEqual({ existing: ["A", "B"], created: ["C"] });
  });
});

describe("the context chips", () => {
  const input: ContextInput = {
    workspace: "entities",
    workspaceLabel: "Domain model",
    element: { id: "01J92P0V0FJ23CGSNKM7P1W5V7", name: "Invoice", kind: "entity" },
    selection: ["01J92P0V0FJ23CGSNKM7P1W5V7", "01J92P0V0HEGSC6MW92CST5KA6"],
    problems: {
      errors: 1,
      warnings: 0,
      infos: 0,
      diagnostics: [{ rule: "MQ3005", severity: "error", message: "No key.", elementId: "X", filePath: null, jsonPointer: null, line: null, column: null }],
    },
  };

  it("offers one chip per part, and sends only the chips that were kept", () => {
    expect(contextChips(input).map((c) => [c.key, c.label])).toEqual([
      ["workspace", "Domain model"],
      ["element", "Invoice"],
      ["selection", "2 selected"],
      ["problems", "1 errors, 0 warnings"],
    ]);
    expect(contextOf(input, new Set())).toEqual({
      workspace: "entities",
      elementId: "01J92P0V0FJ23CGSNKM7P1W5V7",
      elementName: "Invoice",
      elementKind: "entity",
      selection: input.selection,
      problems: { errors: 1, warnings: 0, infos: 0, top: [{ rule: "MQ3005", message: "No key.", elementId: "X" }] },
    });
    expect(contextOf(input, new Set(["element", "problems"]))).toEqual({ workspace: "entities", selection: input.selection });
    expect(contextOf(input, new Set(["workspace", "element", "selection", "problems"]))).toBeNull();
    expect(contextChips({ ...input, element: null, selection: [], problems: null }).map((c) => c.key)).toEqual(["workspace"]);
  });
});

describe("the Markdown renderer", () => {
  it("parses headings, lists, code and paragraphs", () => {
    const blocks = parseMarkdown('# Title\n\nSome **bold** and `code`.\n\n- one\n- two\n\n1. first\n\n```json\n{ "a": 1 }\n```');
    expect(blocks.map((b) => b.type)).toEqual(["heading", "paragraph", "list", "list", "code"]);
    expect(blocks[2]).toMatchObject({ ordered: false, items: [[{ type: "text", text: "one" }], [{ type: "text", text: "two" }]] });
    expect(blocks[3]).toMatchObject({ ordered: true });
    expect(blocks[4]).toEqual({ type: "code", language: "json", text: '{ "a": 1 }' });
  });

  it("keeps only safe links and never makes markup of text", () => {
    const spans = parseInline("[ok](https://example.org) [bad](javascript:alert(1)) <b>x</b> [rel](/settings/assistant)");
    expect(spans.filter((s) => s.type === "link").map((s) => (s as { href: string }).href)).toEqual(["https://example.org", "/settings/assistant"]);
    const text = spans.map((s) => (s.type === "text" ? s.text : "")).join("");
    expect(text).toContain("bad");
    expect(text).toContain("<b>x</b>");
  });
});

describe("Settings › Assistant", () => {
  it("reads defaults, leaves defaults out when saving and checks the numbers", () => {
    const json = { formatVersion: 1 } as unknown as SettingsJson;
    const draft = assistantOf(json);
    expect(draft).toEqual({ instructions: "", maxTurns: "10", tokenBudgetPerRequest: "200000", tokenBudgetPerDayPerUser: "2000000" });
    expect(withAssistant(json, draft)).toEqual({ formatVersion: 1 });
    expect(withAssistant(json, { ...draft, instructions: "Singular names.", maxTurns: "6" })).toEqual({
      formatVersion: 1,
      assistant: { instructions: "Singular names.", maxTurns: 6 },
    });
    expect(Object.keys(assistantProblems({ ...draft, maxTurns: "30", tokenBudgetPerRequest: "10" }))).toEqual(["maxTurns", "tokenBudgetPerRequest"]);
  });
});

describe("the mock assistant", () => {
  const mock = useMockApi();

  it("streams a scripted answer with a tool row and a proposal, applied as one undo step", async () => {
    setAssistChatBase(NODE_BASE_URL);
    mock.backend.assist.stepMs = 0;
    const status = await (await fetch(mock.url("/api/assist/status"))).json();
    expect(validatorAt("/components/schemas/AssistStatus")(status)).toBe(true);
    expect(status.configured).toBe(true);

    const events: AssistEvent[] = [];
    await streamAssistChat({ message: "Please add an entity Foo", conversationId: null, context: { workspace: "entities" } }, (e) => events.push(e));
    expect(events.map((e) => e.type)).toEqual(["conversation", "text", "tool-started", "tool-finished", "text", "proposal", "final"]);
    const validate = validatorAt("/components/schemas/AssistEvent");
    for (const e of events) expect(validate(e), JSON.stringify(validate.errors?.slice(0, 2))).toBe(true);
    const conversationId = (events[0] as { conversationId: string }).conversationId;
    const proposal = (events.find((e) => e.type === "proposal") as { proposal: AssistProposal }).proposal;

    const { queryClient, store } = mock.services;
    expect(await applyProposal(queryClient, store, proposal)).toEqual({ ok: true });
    const created = mock.backend.model.index().find((e) => e.name === "Foo");
    expect(created?.kind).toBe("entity");
    const undo = store.getState().undo.at(-1)!;
    expect(undo.label).toBe("Assistant: Add entity Foo to the billing domain");
    expect(undo.before).toEqual([null]);
    expect(undo.ids).toEqual([created!.id]);

    const put = await fetch(mock.url(`/api/assist/conversations/${conversationId}/proposals/${proposal.id}`), {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ state: "applied" }),
    });
    expect(put.status).toBe(200);
    expect((await put.json()).state).toBe("applied");
    const conversation = await (await fetch(mock.url(`/api/assist/conversations/${conversationId}`))).json();
    expect(validatorAt("/components/schemas/AssistConversation")(conversation)).toBe(true);
    expect(conversation.entries.map((e: { type: string }) => e.type)).toEqual(["user", "assistant", "tool", "assistant", "proposal"]);
    const list = await (await fetch(mock.url("/api/assist/conversations"))).json();
    expect(validatorAt("/components/schemas/AssistConversationList")(list)).toBe(true);
    expect((await fetch(mock.url(`/api/assist/conversations/${conversationId}`), { method: "DELETE" })).status).toBe(204);
  });

  it("refuses a proposal whose element changed since, before writing", async () => {
    const p = entityProposal("Bar");
    const { queryClient, store } = mock.services;
    const index = await (await fetch(mock.url("/api/model/index"))).json();
    queryClient.setQueryData(["index"], index);
    const invoice = (index as ElementSummary[]).find((e) => e.name === "Invoice")!;
    const stale = {
      ...p,
      files: [
        ...p.files,
        {
          path: invoice.path,
          action: "changed" as const,
          id: invoice.id,
          kind: "entity",
          name: "Invoice",
          beforeHash: "0".repeat(64),
          before: "{}",
          after: "{}",
        },
      ],
    };
    const outcome = await applyProposal(queryClient, store, stale);
    expect(outcome).toMatchObject({ ok: false, conflict: true, changed: ["Invoice"] });
    expect(mock.backend.model.index().some((e) => e.name === "Bar")).toBe(false);
  });

  it("stops reading when the request is aborted (the Stop button)", async () => {
    setAssistChatBase(NODE_BASE_URL);
    mock.backend.assist.stepMs = 30;
    const controller = new AbortController();
    const events: AssistEvent[] = [];
    const run = streamAssistChat(
      { message: "add an entity Stop" },
      (e) => {
        events.push(e);
        if (e.type === "conversation") controller.abort();
      },
      controller.signal,
    );
    await expect(run).rejects.toMatchObject({ name: "AbortError" });
    expect(events.map((e) => e.type)).toEqual(["conversation"]);
  });

  it("answers 503 assist-not-configured when the site has no AI", async () => {
    setAssistChatBase(NODE_BASE_URL);
    mock.backend.assist.configured = false;
    await expect(streamAssistChat({ message: "hi" }, () => undefined)).rejects.toMatchObject({ status: 503, code: "assist-not-configured" });
  });
});
