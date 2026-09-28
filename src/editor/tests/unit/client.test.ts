// The API client's contract middleware: problem+json → ApiProblem, non-JSON /api/ answers refused,
// the plan diff read as text, ETag parsing.
import { http, HttpResponse } from "msw";
import { describe, expect, it } from "vitest";
import { ApiProblem, ApiUnexpectedResponse, api, etagHash, isProblemResponse, onApiProblem } from "@/api/client";
import * as endpoints from "@/api/endpoints";
import { useMockApi } from "./harness";

describe("API client", () => {
  const mock = useMockApi();

  it("maps problem+json to a typed ApiProblem and tells listeners", async () => {
    const seen: string[] = [];
    const off = onApiProblem((p) => seen.push(p.code));
    const error = await endpoints.getElement("01J00000000000000000000000").catch((e: unknown) => e);
    off();
    expect(error).toBeInstanceOf(ApiProblem);
    expect((error as ApiProblem).status).toBe(404);
    expect((error as ApiProblem).code).toBe("not-found");
    expect(seen).toEqual(["not-found"]);
  });

  it("refuses the SPA fallback's HTML for an unknown /api/ path", async () => {
    mock.server.use(http.get(mock.url("/api/project"), () => new HttpResponse("<!doctype html><p>app</p>", { headers: { "Content-Type": "text/html" } })));
    const error = await endpoints.getProject().catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiUnexpectedResponse);
    expect((error as ApiUnexpectedResponse).contentType).toContain("text/html");
  });

  it("accepts text/x-diff only from the plan diff", async () => {
    mock.server.use(
      http.get(mock.url("/api/generate/plan/:id/diff"), () => new HttpResponse("--- a/x\n+++ b/x\n", { headers: { "Content-Type": "text/x-diff" } })),
      http.get(mock.url("/api/jobs"), () => new HttpResponse("--- a\n", { headers: { "Content-Type": "text/x-diff" } })),
    );
    const diff = await endpoints.getPlanDiff("01J00000000000000000000000", "x");
    expect(diff).toContain("+++ b/x");
    await expect(endpoints.listJobs()).rejects.toBeInstanceOf(ApiUnexpectedResponse);
  });

  it("sends JSON bodies with the JSON content type", async () => {
    let contentType: string | null = null;
    mock.server.use(
      http.post(mock.url("/api/validate"), ({ request }) => {
        contentType = request.headers.get("Content-Type");
        return HttpResponse.json({ diagnostics: [], truncated: false });
      }),
    );
    await api().POST("/api/validate", { body: {} as never });
    expect(contentType).toBe("application/json");
  });

  it("reads the strong ETag hash and recognises problem types", () => {
    expect(etagHash(new Response(null, { headers: { ETag: '"abc123"' } }))).toBe("abc123");
    expect(etagHash(new Response(null, { headers: { ETag: 'W/"abc"' } }))).toBe("abc");
    expect(etagHash(new Response(null))).toBeNull();
    expect(isProblemResponse("application/problem+json; charset=utf-8")).toBe(true);
    expect(isProblemResponse("application/json")).toBe(false);
  });
});
