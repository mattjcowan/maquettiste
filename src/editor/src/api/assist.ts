// The assistant's operations (tag `assist`, erratum E44): the status, the conversations, the proposal state, and the chat,
// whose answer is a stream of server-sent events read with fetch and a ReadableStream (EventSource cannot POST).
import { api, ApiProblem, isProblemResponse } from "./client";
import type { components, Problem } from "./types";

type S = components["schemas"];
export type AssistStatus = S["AssistStatus"];
export type AssistContext = S["AssistContext"];
export type AssistChatRequest = S["AssistChatRequest"];
export type AssistEvent = S["AssistEvent"];
export type AssistProposal = S["AssistProposal"];
export type AssistProposalFile = S["AssistProposalFile"];
export type AssistConversation = S["AssistConversation"];
export type AssistConversationSummary = S["AssistConversationSummary"];
export type AssistEntry = S["AssistEntry"];

export async function getAssistStatus(): Promise<AssistStatus> {
  const { data, response } = await api().GET("/api/assist/status");
  if (!data) throw new ApiProblem(response.status, null);
  return data;
}

export async function listAssistConversations(): Promise<AssistConversationSummary[]> {
  const { data, response } = await api().GET("/api/assist/conversations");
  if (!data) throw new ApiProblem(response.status, null);
  return data.items;
}

export async function getAssistConversation(id: string): Promise<AssistConversation> {
  const { data, response } = await api().GET("/api/assist/conversations/{id}", { params: { path: { id } } });
  if (!data) throw new ApiProblem(response.status, null);
  return data;
}

export async function deleteAssistConversation(id: string): Promise<void> {
  await api().DELETE("/api/assist/conversations/{id}", { params: { path: { id } } });
}

export async function setAssistProposalState(id: string, proposalId: string, state: "applied" | "discarded"): Promise<AssistProposal> {
  const { data, response } = await api().PUT("/api/assist/conversations/{id}/proposals/{proposalId}", {
    params: { path: { id, proposalId } },
    body: { state },
  });
  if (!data) throw new ApiProblem(response.status, null);
  return data;
}

/** One server-sent event: its name (`event:`, "message" by default) and its data lines joined with newlines. */
export interface SseMessage {
  event: string;
  data: string;
}

/**
 * An incremental `text/event-stream` parser: feed it text in pieces split anywhere (inside a line, between CR and LF) and
 * it returns each event once its blank line arrives. Comment lines (`:`) and unknown fields are skipped; several `data:`
 * lines join with newlines, as the specification says.
 */
export class SseParser {
  private buffer = "";
  private event = "";
  private data: string[] = [];
  private pendingCr = false;

  push(text: string): SseMessage[] {
    let chunk = text;
    // A CR ended the previous piece: an LF starting this one belongs to the same line break.
    if (this.pendingCr && chunk.startsWith("\n")) chunk = chunk.slice(1);
    this.pendingCr = false;
    this.buffer += chunk;
    const out: SseMessage[] = [];
    for (;;) {
      const match = /\r\n|\r|\n/.exec(this.buffer);
      if (!match) break;
      // A CR at the very end may be the first half of CRLF: keep the line, and drop a following LF.
      if (match[0] === "\r" && match.index === this.buffer.length - 1) this.pendingCr = true;
      const line = this.buffer.slice(0, match.index);
      this.buffer = this.buffer.slice(match.index + match[0].length);
      this.line(line, out);
    }
    return out;
  }

  /** The event left without its blank line when the stream ended, if any. */
  end(): SseMessage[] {
    const out: SseMessage[] = [];
    if (this.buffer.length > 0) this.line(this.buffer, out);
    this.buffer = "";
    this.line("", out);
    return out;
  }

  private line(line: string, out: SseMessage[]): void {
    if (line === "") {
      if (this.data.length > 0) out.push({ event: this.event || "message", data: this.data.join("\n") });
      this.event = "";
      this.data = [];
      return;
    }
    if (line.startsWith(":")) return;
    const colon = line.indexOf(":");
    const field = colon < 0 ? line : line.slice(0, colon);
    let value = colon < 0 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "event") this.event = value;
    else if (field === "data") this.data.push(value);
  }
}

/** The events of a chat answer, parsed; a data line that is not JSON with a `type` is skipped. */
export function eventsOf(messages: SseMessage[]): AssistEvent[] {
  const out: AssistEvent[] = [];
  for (const m of messages) {
    try {
      const value = JSON.parse(m.data) as AssistEvent;
      if (value && typeof value === "object" && typeof value.type === "string") out.push(value);
    } catch {
      // not an event of the contract
    }
  }
  return out;
}

/**
 * Sends one message and calls `onEvent` for each event of the answer until the stream ends. A non-200 answer is thrown as
 * an ApiProblem (`assist-not-configured`, `assist-budget-exhausted`, `assist-busy`, `bad-request`, `not-found`); an abort
 * through `signal` rejects with the AbortError.
 */
export async function streamAssistChat(request: AssistChatRequest, onEvent: (event: AssistEvent) => void, signal?: AbortSignal): Promise<void> {
  const response = await fetch(assistChatUrl(), {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "text/event-stream, application/problem+json" },
    body: JSON.stringify(request),
    signal,
    credentials: "same-origin",
  });
  const contentType = response.headers.get("Content-Type");
  if (!response.ok || isProblemResponse(contentType)) {
    let problem: Problem | null = null;
    try {
      problem = (await response.json()) as Problem;
    } catch {
      problem = null;
    }
    throw new ApiProblem(response.status, problem);
  }
  if (!response.body) return;
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader();
  // A transport that does not end the body on abort still stops here: the pending read is cancelled and nothing more is delivered.
  const aborted = () => new DOMException("The answer was stopped.", "AbortError");
  const onAbort = () => void reader.cancel().catch(() => undefined);
  signal?.addEventListener("abort", onAbort);
  try {
    const parser = new SseParser();
    for (;;) {
      if (signal?.aborted) throw aborted();
      const { value, done } = await reader.read();
      if (signal?.aborted) throw aborted();
      if (done) break;
      for (const event of eventsOf(parser.push(value))) {
        onEvent(event);
        if (signal?.aborted) throw aborted();
      }
    }
    for (const event of eventsOf(parser.end())) onEvent(event);
  } finally {
    signal?.removeEventListener("abort", onAbort);
  }
}

let chatBase = "";

/** The chat URL: same origin in the SPA; tests running in Node set an absolute base. */
export function assistChatUrl(): string {
  return `${chatBase}/api/assist/chat`;
}

export function setAssistChatBase(base: string): void {
  chatBase = base;
}
