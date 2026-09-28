// The contract as JSON Schema: src/mocks/openapi.json (docs/api/openapi.yaml bundled with
// schemas/v1) loaded into ajv (draft 2020-12). The mock model checks documents against the kind
// schemas with it, and tests/unit/contract.test.ts validates every mock response against the
// response schemas of its operation.
import Ajv2020, { type ErrorObject, type ValidateFunction } from "ajv/dist/2020";
import addFormats from "ajv-formats";
import openapi from "./openapi.json";

export const OPENAPI_ID = "https://maquettiste.invalid/openapi.json";
export type OpenApiDocument = typeof openapi;
export { openapi };

/** Removes `$schema` meta keywords (each bundled schemas/v1 file carries one) below the root. */
function stripMeta(node: unknown, key?: string): unknown {
  if (Array.isArray(node)) return node.map((n) => stripMeta(n));
  if (!node || typeof node !== "object") return node;
  const out: Record<string, unknown> = {};
  for (const [k, v] of Object.entries(node as Record<string, unknown>)) {
    // A property *named* $schema (inside `properties`) is a schema, not the meta keyword.
    if (k === "$schema" && typeof v === "string" && key !== "properties") continue;
    out[k] = stripMeta(v, k);
  }
  return out;
}

let shared: Ajv2020 | null = null;

export function contractAjv(): Ajv2020 {
  if (shared) return shared;
  const ajv = new Ajv2020({ strict: false, allErrors: true, validateSchema: false, discriminator: false });
  addFormats(ajv);
  ajv.addKeyword({ keyword: "example", schemaType: ["string", "number", "boolean", "object", "array", "null"] });
  ajv.addSchema({ ...(stripMeta(openapi) as object), $id: OPENAPI_ID });
  shared = ajv;
  return ajv;
}

const cache = new Map<string, ValidateFunction>();

/** A validator for a JSON pointer inside the bundled contract, e.g. /components/schemas/entity. */
export function validatorAt(pointer: string): ValidateFunction {
  let fn = cache.get(pointer);
  if (!fn) {
    const ajv = contractAjv();
    const found = ajv.getSchema(`${OPENAPI_ID}#${pointer}`);
    if (!found) throw new Error(`No schema at ${pointer} in the contract.`);
    fn = found;
    cache.set(pointer, fn);
  }
  return fn;
}

export function schemaValidator(name: string): ValidateFunction {
  return validatorAt(`/components/schemas/${escape(name)}`);
}

export function escape(segment: string): string {
  return segment.replace(/~/g, "~0").replace(/\//g, "~1");
}

export function describeErrors(errors: ErrorObject[] | null | undefined): string[] {
  return (errors ?? []).map((e) => `${e.instancePath || "/"} ${e.keyword}: ${e.message ?? ""}`.trim());
}
