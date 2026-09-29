// The editor's HTTP client: openapi-fetch over the generated `paths` (phase2-design.md 4.3).
//
// - Every POST, PUT, PATCH and DELETE carries `Content-Type: application/json`: the sign-in gate
//   refuses a POST or PUT under /api/ with any other type (415), which is part of the forgery
//   defence of the credential-less local mode. Bodies are always JSON (an empty POST sends `{}`).
// - `application/problem+json` answers become a thrown ApiProblem, typed by its stable `code`.
// - Any other non-JSON answer from /api/ is an error (the host's SPA fallback answers an unknown
//   /api/ path with index.html), except `text/x-diff` from getPlanDiff.
// - Engine outcome records (SaveResult, BatchResult, SettingsSaveResult) are data at any status.
import createClient, { type Client, type Middleware } from "openapi-fetch";
import type { paths, Problem, ProblemCode } from "./types";

export class ApiProblem extends Error {
  readonly status: number;
  readonly code: ProblemCode | "unknown";
  readonly problem: Problem | null;
  constructor(status: number, problem: Problem | null, fallback?: string) {
    super(problem?.title ?? fallback ?? `HTTP ${status}`);
    this.name = "ApiProblem";
    this.status = status;
    this.code = problem?.code ?? "unknown";
    this.problem = problem;
  }
}

/** An answer that is not the JSON (or diff) the contract promises, such as the SPA's index.html. */
export class ApiUnexpectedResponse extends Error {
  readonly status: number;
  readonly contentType: string;
  constructor(url: string, status: number, contentType: string) {
    super(`Unexpected ${contentType || "untyped"} answer (HTTP ${status}) from ${new URL(url, "http://x").pathname}`);
    this.name = "ApiUnexpectedResponse";
    this.status = status;
    this.contentType = contentType;
  }
}

const JSON_TYPE = /^application\/(?:[\w.+-]+\+)?json\b/i;

export function isProblemResponse(contentType: string | null): boolean {
  return !!contentType && /^application\/problem\+json\b/i.test(contentType);
}

/** Listeners told about every ApiProblem the client throws (the shell shows 401 as "signed out"). */
type ProblemListener = (problem: ApiProblem) => void;
const problemListeners = new Set<ProblemListener>();
export function onApiProblem(listener: ProblemListener): () => void {
  problemListeners.add(listener);
  return () => problemListeners.delete(listener);
}

const contract: Middleware = {
  onRequest({ request }) {
    if (["POST", "PUT", "PATCH", "DELETE"].includes(request.method)) {
      request.headers.set("Content-Type", "application/json");
    }
    request.headers.set("Accept", "application/json, application/problem+json, text/x-diff, text/csv");
    return request;
  },
  async onResponse({ request, response }) {
    const contentType = response.headers.get("Content-Type") ?? "";
    if (isProblemResponse(contentType)) {
      let body: Problem | null = null;
      try {
        body = (await response.clone().json()) as Problem;
      } catch {
        body = null;
      }
      const problem = new ApiProblem(response.status, body);
      for (const listener of problemListeners) listener(problem);
      throw problem;
    }
    const path = new URL(request.url, "http://x").pathname;
    if (!path.startsWith("/api/")) return response;
    if (response.status === 204 || response.status === 304) return response;
    if (/\/api\/generate\/plan\/[^/]+\/diff$/.test(path) && /^text\/x-diff\b/i.test(contentType) && response.ok) {
      return response;
    }
    // A seed's CSV export (reference-types-seeds-localization.md 2.3) is text/csv.
    if (/\/api\/seeds\/[^/]+\/csv$/.test(path) && request.method === "GET" && /^text\/csv\b/i.test(contentType) && response.ok) {
      return response;
    }
    if (!JSON_TYPE.test(contentType)) {
      throw new ApiUnexpectedResponse(request.url, response.status, contentType);
    }
    return response;
  },
};

export type ApiClient = Client<paths>;

/** Creates a client; the SPA uses the same-origin default, tests pass an absolute base URL. */
export function createApiClient(baseUrl = ""): ApiClient {
  const client = createClient<paths>({ baseUrl });
  client.use(contract);
  return client;
}

let current: ApiClient = createApiClient("");

/** The client the app uses. */
export function api(): ApiClient {
  return current;
}

/** Replaces the app's client (tests running in Node need an absolute base URL). */
export function setApiClient(client: ApiClient): void {
  current = client;
}

/** The strong ETag's hash, without quotes or a weak prefix. */
export function etagHash(response: Response): string | null {
  const raw = response.headers.get("ETag");
  if (!raw) return null;
  return raw.replace(/^W\//, "").replace(/"/g, "");
}
