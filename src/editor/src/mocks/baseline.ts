// The mock baseline (phase2-design.md 4.4, layer 1): one MSW handler per operation of the
// contract, answering with the operation's recorded engine response (src/mocks/recorded/) when
// there is one, else its first example, else a deterministic minimal instance of its response
// schema. Every endpoint therefore answers from the day it enters docs/api/openapi.yaml; the
// stateful handlers (handlers.ts) override it for everything the editor uses.
//
// This replaces @mswjs/source's fromOpenApi, which generates missing examples with random
// (faker) data: randomness would break test determinism, and that dependency carried a
// high-severity advisory at the time of writing.
import { http, HttpResponse, type HttpHandler } from "msw";
import { openapi } from "./contract";
import { recordings } from "./recorded";

type Json = Record<string, unknown>;
const doc = openapi as unknown as { paths: Record<string, Record<string, Json>>; components: { schemas: Record<string, Json> } };
const METHODS = ["get", "put", "post", "delete", "patch"] as const;

export interface BaselineOperation {
  operationId: string;
  method: (typeof METHODS)[number];
  path: string;
  status: number;
  contentType: string | null;
  body: unknown;
  source: "recorded" | "example" | "schema" | "empty";
}

function resolve(schema: Json | undefined): Json | undefined {
  let current = schema;
  for (let i = 0; current && typeof current.$ref === "string" && i < 32; i++) {
    const ref = current.$ref as string;
    current = ref.startsWith("#/components/schemas/") ? doc.components.schemas[ref.slice(21)] : undefined;
  }
  return current;
}

const PATTERN_SAMPLES: [RegExp, string][] = [
  [/\[0-7\]\[0-9A-HJKMNP-TV-Z\]\{25\}/, "00000000000000000000000000"],
  [/\[0-9a-f\]\{64\}/, "0".repeat(64)],
  [/\\d\{2\}:\\d\{2\}:\\d\{2\}/, "00:00:00"],
  [/\^MQ/, "MQ1001"],
];

/** A minimal instance of a schema: required members only, first enum values, fixed scalars. */
export function sample(schemaIn: Json | undefined, depth = 0): unknown {
  const schema = resolve(schemaIn);
  if (!schema || depth > 12) return null;
  if ("example" in schema) return schema.example;
  if ("const" in schema) return schema.const;
  if (Array.isArray(schema.enum)) return schema.enum[0];
  if (Array.isArray(schema.oneOf)) {
    const nonNull = (schema.oneOf as Json[]).find((s) => resolve(s)?.type !== "null");
    return (schema.oneOf as Json[]).some((s) => resolve(s)?.type === "null") ? null : sample(nonNull, depth + 1);
  }
  if (Array.isArray(schema.anyOf)) return sample((schema.anyOf as Json[])[0], depth + 1);
  if (Array.isArray(schema.allOf)) {
    const parts = (schema.allOf as Json[]).map((s) => sample(s, depth + 1));
    return parts.every((p) => p && typeof p === "object" && !Array.isArray(p)) ? Object.assign({}, ...parts) : parts[0];
  }
  const type = Array.isArray(schema.type) ? (schema.type.includes("null") ? "null" : schema.type[0]) : schema.type;
  switch (type) {
    case "null":
      return null;
    case "object":
    case undefined: {
      if (!schema.properties && type === undefined) return {};
      const out: Json = {};
      const properties = (schema.properties as Record<string, Json> | undefined) ?? {};
      for (const name of (schema.required as string[] | undefined) ?? []) out[name] = sample(properties[name], depth + 1);
      return out;
    }
    case "array":
      return [];
    case "integer":
    case "number":
      return typeof schema.minimum === "number" ? schema.minimum : 0;
    case "boolean":
      return false;
    case "string": {
      if (schema.format === "date-time") return "2026-01-01T00:00:00+00:00";
      if (schema.format === "uri") return "about:blank";
      if (typeof schema.pattern === "string") {
        for (const [p, v] of PATTERN_SAMPLES) if (p.test(schema.pattern)) return v;
      }
      return "x".repeat(typeof schema.minLength === "number" ? schema.minLength : 0);
    }
    default:
      return null;
  }
}

function firstExample(media: Json | undefined): { found: boolean; value: unknown } {
  if (!media) return { found: false, value: undefined };
  if ("example" in media) return { found: true, value: media.example };
  const examples = media.examples as Record<string, Json> | undefined;
  if (examples) {
    const first = Object.values(examples)[0];
    if (first && "value" in first) return { found: true, value: first.value };
  }
  return { found: false, value: undefined };
}

/** What the baseline answers for every operation. */
export function baselineOperations(): BaselineOperation[] {
  const recorded = recordings();
  const out: BaselineOperation[] = [];
  for (const [path, item] of Object.entries(doc.paths)) {
    for (const method of METHODS) {
      const operation = item[method];
      if (!operation) continue;
      const operationId = String(operation.operationId);
      const responses = operation.responses as Record<string, Json>;
      const status = Object.keys(responses).find((s) => /^2\d\d$/.test(s)) ?? "200";
      const response = responses[status] ?? {};
      const content = response.content as Record<string, Json> | undefined;
      const contentType = content ? Object.keys(content)[0] : null;
      const rec = recorded.get(operationId);
      if (rec) {
        out.push({ operationId, method, path, status: rec.status, contentType: rec.contentType, body: rec.body, source: "recorded" });
        continue;
      }
      if (!contentType) {
        out.push({ operationId, method, path, status: Number(status), contentType: null, body: null, source: "empty" });
        continue;
      }
      const media = content![contentType];
      const example = firstExample(media);
      out.push({
        operationId,
        method,
        path,
        status: Number(status),
        contentType,
        body: example.found ? example.value : sample(media.schema as Json),
        source: example.found ? "example" : "schema",
      });
    }
  }
  return out;
}

export function mswPath(path: string): string {
  return path.replace(/\{([^}]+)\}/g, ":$1");
}

export function baselineHandlers(baseUrl = ""): HttpHandler[] {
  return baselineOperations().map((op) =>
    http[op.method](baseUrl + mswPath(op.path), () => {
      if (op.contentType === null) return new HttpResponse(null, { status: op.status });
      if (!/json/.test(op.contentType)) return new HttpResponse(String(op.body ?? ""), { status: op.status, headers: { "Content-Type": op.contentType } });
      return HttpResponse.json(op.body as never, { status: op.status, headers: { "Content-Type": op.contentType } });
    }),
  );
}
