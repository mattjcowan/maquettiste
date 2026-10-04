// The mock assistant (erratum E44): GET /api/assist/status, the conversations and a deterministic scripted chat, streamed as
// server-sent events like the functions' agent loop. "add an entity <Name>" reads the model index (a tool row) and proposes
// creating the entity in the billing domain; anything else gets a short answer. `?mock=noai` makes the site's AI not configured.
import { http, HttpResponse, type HttpHandler } from "msw";
import type { AssistEntry, AssistEvent, AssistProposal, AssistStatus } from "@/api/assist";
import type { Problem } from "@/api/types";
import { newId } from "@/lib/ids";
import type { MockModel } from "./model/store";
import { isUlid } from "./wire";

type Json = Record<string, unknown>;

export const BILLING_PACKAGE = "01J92P0V01KDRN8GX5PGYCNKSX";

interface Conversation {
  id: string;
  title: string;
  createdUtc: string;
  updatedUtc: string;
  entries: AssistEntry[];
}

export class MockAssist {
  configured: boolean;
  canApply = true;
  usedToday = 0;
  /** Milliseconds between streamed events (0 in unit tests). */
  stepMs = 15;
  readonly conversations = new Map<string, Conversation>();

  constructor(
    private readonly model: MockModel,
    configured: boolean,
  ) {
    this.configured = configured;
  }

  status(): AssistStatus {
    const a = (this.model.settingsJson.assistant ?? {}) as Json;
    return {
      configured: this.configured,
      model: this.configured ? "mock-model" : null,
      hostAiUrl: "http://localhost:8090/",
      canApply: this.canApply,
      maxTurns: typeof a.maxTurns === "number" ? a.maxTurns : 10,
      tokenBudgetPerRequest: typeof a.tokenBudgetPerRequest === "number" ? a.tokenBudgetPerRequest : 200_000,
      tokenBudgetPerDayPerUser: typeof a.tokenBudgetPerDayPerUser === "number" ? a.tokenBudgetPerDayPerUser : 2_000_000,
      usedToday: this.usedToday,
      hasInstructions: typeof a.instructions === "string" && a.instructions.trim().length > 0,
    };
  }

  /** The scripted answer to one message, the conversation updated as the functions store it. */
  answer(conversation: Conversation, message: string): AssistEvent[] {
    const events: AssistEvent[] = [{ type: "conversation", conversationId: conversation.id, title: conversation.title }];
    const add = /add an entity (\w+)/i.exec(message);
    let text: string;
    if (add) {
      const name = add[1];
      const packages = this.model.index().filter((e) => e.kind === "package").length;
      events.push({ type: "text", delta: "I will read the model first." });
      events.push({ type: "tool-started", id: "call_1", name: "get_model_index", arguments: { kind: "package" } });
      events.push({ type: "tool-finished", id: "call_1", name: "get_model_index", isError: false, summary: `${packages} packages` });
      const proposal = entityProposal(name);
      text = `I propose a new entity **${name}** in the billing domain, keyed by \`id\`. Review it and apply it when it looks right.`;
      events.push({ type: "text", delta: text });
      events.push({ type: "proposal", proposal });
    } else {
      text = "I can read the model with my tools and propose changes for you to review. Try *add an entity Foo*.";
      events.push({ type: "text", delta: text });
    }
    const input = 1200 + message.length;
    const output = 80;
    this.usedToday += input + output;
    events.push({
      type: "final",
      text,
      stopReason: "stop",
      turns: add ? 3 : 1,
      inputTokens: input,
      outputTokens: output,
      usedToday: this.usedToday,
      limited: null,
    });
    return events;
  }

  /** Stores the events as the conversation's entries. */
  record(conversation: Conversation, message: string, context: unknown, events: AssistEvent[]): void {
    conversation.entries.push({ type: "user", text: message, context: (context ?? null) as never });
    for (const e of events) {
      if (e.type === "text") conversation.entries.push({ type: "assistant", text: e.delta });
      else if (e.type === "tool-finished")
        conversation.entries.push({ type: "tool", id: e.id, name: e.name, arguments: { kind: "package" }, isError: e.isError, summary: e.summary });
      else if (e.type === "proposal") conversation.entries.push({ type: "proposal", proposal: e.proposal });
    }
    conversation.updatedUtc = new Date().toISOString();
  }

  proposal(conversation: Conversation, id: string): AssistProposal | null {
    for (const e of conversation.entries) if (e.type === "proposal" && e.proposal.id === id) return e.proposal;
    return null;
  }
}

function kebab(name: string): string {
  return name.replace(/([a-z0-9])([A-Z])/g, "$1-$2").toLowerCase();
}

/** A proposal creating entity `name` in the billing domain with one key attribute, as `propose_changes` pins it. */
export function entityProposal(name: string): AssistProposal {
  const id = newId();
  const key = newId();
  const element = {
    kind: "entity",
    id,
    name,
    package: BILLING_PACKAGE,
    key: { attributes: [key] },
    attributes: [{ id: key, name: "id", type: "uuid", required: true }],
  };
  const after = `${JSON.stringify({ $schema: "../../.schema/v1/entity.json", ...element }, null, 2)}\n`;
  return {
    id: newId(),
    toolCallId: "call_2",
    summary: `Add entity ${name} to the billing domain`,
    state: "pending",
    createdUtc: new Date().toISOString(),
    operations: [{ op: "create", element }] as unknown as AssistProposal["operations"],
    files: [{ path: `.maquettiste/model/entities/${kebab(name)}.json`, action: "created", id, kind: "entity", name, beforeHash: null, before: null, after }],
  };
}

function problem(status: number, code: Problem["code"], title: string): HttpResponse<Problem> {
  return HttpResponse.json({ type: "about:blank", title, status, code } as Problem, { status, headers: { "Content-Type": "application/problem+json" } });
}

const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

function stream(events: AssistEvent[], stepMs: number): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  let cancelled = false;
  return new ReadableStream<Uint8Array>({
    async start(controller) {
      for (const event of events) {
        if (cancelled) return;
        if (stepMs > 0) await wait(stepMs);
        if (cancelled) return;
        controller.enqueue(encoder.encode(`event: ${event.type}\ndata: ${JSON.stringify(event)}\n\n`));
      }
      controller.close();
    },
    cancel() {
      cancelled = true;
    },
  });
}

async function body(request: Request): Promise<Json | null> {
  try {
    const value = (await request.json()) as unknown;
    return value && typeof value === "object" && !Array.isArray(value) ? (value as Json) : null;
  } catch {
    return null;
  }
}

export function assistHandlers(assist: MockAssist, baseUrl = ""): HttpHandler[] {
  const find = (id: string) => (isUlid(id) ? (assist.conversations.get(id) ?? null) : null);
  return [
    http.get(`${baseUrl}/api/assist/status`, () => HttpResponse.json(assist.status())),
    http.post(`${baseUrl}/api/assist/chat`, async ({ request }) => {
      const value = await body(request);
      if (!value) return problem(400, "bad-request", "The request body is not a JSON object.");
      const message = value.message;
      if (typeof message !== "string" || message.trim().length === 0 || message.length > 16000)
        return problem(400, "bad-request", "message is required: 1 to 16000 characters.");
      if (!assist.configured) return problem(503, "assist-not-configured", "The site has no AI provider; set one in the host's AI settings.");
      let conversation: Conversation | null;
      if (value.conversationId === undefined || value.conversationId === null) {
        const now = new Date().toISOString();
        const line = message.trim().split(/\r?\n/)[0];
        conversation = { id: newId(), title: line.length > 60 ? `${line.slice(0, 59)}…` : line, createdUtc: now, updatedUtc: now, entries: [] };
        assist.conversations.set(conversation.id, conversation);
      } else {
        conversation = find(String(value.conversationId));
        if (!conversation) return problem(404, "not-found", `No conversation has the id ${String(value.conversationId)}.`);
      }
      const events = assist.answer(conversation, message);
      assist.record(conversation, message, value.context, events);
      return new HttpResponse(stream(events, assist.stepMs), {
        status: 200,
        headers: { "Content-Type": "text/event-stream; charset=utf-8", "Cache-Control": "no-cache" },
      });
    }),
    http.get(`${baseUrl}/api/assist/conversations`, () =>
      HttpResponse.json({
        items: [...assist.conversations.values()]
          .sort((a, b) => (a.updatedUtc < b.updatedUtc ? 1 : a.updatedUtc > b.updatedUtc ? -1 : a.id < b.id ? 1 : -1))
          .map((c) => ({ id: c.id, title: c.title, createdUtc: c.createdUtc, updatedUtc: c.updatedUtc, entryCount: c.entries.length })),
      }),
    ),
    http.get(`${baseUrl}/api/assist/conversations/:id`, ({ params }) => {
      const c = find(String(params.id));
      if (!c) return problem(404, "not-found", `No conversation has the id ${String(params.id)}.`);
      return HttpResponse.json({
        id: c.id,
        title: c.title,
        createdUtc: c.createdUtc,
        updatedUtc: c.updatedUtc,
        model: "mock-model",
        entries: c.entries,
        truncated: false,
      });
    }),
    http.delete(`${baseUrl}/api/assist/conversations/:id`, ({ params }) => {
      const c = find(String(params.id));
      if (!c) return problem(404, "not-found", `No conversation has the id ${String(params.id)}.`);
      assist.conversations.delete(c.id);
      return new HttpResponse(null, { status: 204 });
    }),
    http.put(`${baseUrl}/api/assist/conversations/:id/proposals/:proposalId`, async ({ params, request }) => {
      const c = find(String(params.id));
      if (!c) return problem(404, "not-found", `No conversation has the id ${String(params.id)}.`);
      const proposal = assist.proposal(c, String(params.proposalId));
      if (!proposal) return problem(404, "not-found", `No proposal has the id ${String(params.proposalId)}.`);
      const value = await body(request);
      const state = value?.state;
      if (state !== "applied" && state !== "discarded") return problem(400, "bad-request", "state must be applied or discarded.");
      if (proposal.state !== "pending") return problem(409, "conflict", `The proposal is already ${proposal.state}.`);
      proposal.state = state;
      c.updatedUtc = new Date().toISOString();
      return HttpResponse.json(proposal);
    }),
  ];
}
