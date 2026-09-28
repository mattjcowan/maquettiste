// Engine responses recorded by the functions tests (phase2-design.md 3.9, written by P2-F with
// MAQUETTISTE_RECORD=1 into src/mocks/recorded/). When a recording exists for an operation, the
// baseline answers with it instead of the OpenAPI example; when the folder is empty or absent,
// the examples are used. Accepted file shapes, named `<operationId>[.<variant>].json`:
//   - an envelope: { "operationId": "...", "status": 200, "contentType": "...", "body": ... }
//   - or the bare response body (status 200, application/json).
// The first file in name order wins for an operation.
const files = import.meta.glob("./recorded/*.json", { eager: true, import: "default" }) as Record<string, unknown>;

export interface Recording {
  operationId: string;
  status: number;
  contentType: string;
  body: unknown;
  file: string;
}

function parse(file: string, value: unknown): Recording | null {
  const name = file.replace("./recorded/", "").replace(/\.json$/, "");
  const operationId = name.split(".")[0];
  if (!operationId) return null;
  if (value && typeof value === "object" && !Array.isArray(value) && "body" in (value as object) && "status" in (value as object)) {
    const env = value as { operationId?: string; status: number; contentType?: string; body: unknown };
    return { operationId: env.operationId ?? operationId, status: env.status, contentType: env.contentType ?? "application/json", body: env.body, file };
  }
  return { operationId, status: 200, contentType: "application/json", body: value, file };
}

export function recordings(): Map<string, Recording> {
  const out = new Map<string, Recording>();
  for (const file of Object.keys(files).sort()) {
    const r = parse(file, files[file]);
    if (r && !out.has(r.operationId)) out.set(r.operationId, r);
  }
  return out;
}

/**
 * The recording the stateful handlers replay for an operation (phase2-design.md 4.4): only while the
 * mock model is still the recorded fixture (no write since the seed, no scenario that swaps the
 * seed), and only when `matches` accepts it (for example the recording is of the requested id).
 * Otherwise the handler answers from its own resolver.
 */
export function replayable(
  recorded: ReadonlyMap<string, Recording>,
  operationId: string,
  state: { pristine: boolean },
  matches: (recording: Recording) => boolean = () => true,
): Recording | undefined {
  if (!state.pristine) return undefined;
  const recording = recorded.get(operationId);
  return recording && matches(recording) ? recording : undefined;
}

/** Whether a recording mentions a value (an id, a path) anywhere in its body. */
export function mentions(recording: Recording, value: string): boolean {
  const text = typeof recording.body === "string" ? recording.body : JSON.stringify(recording.body);
  return text.includes(value);
}
