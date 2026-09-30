// The process dialogs' models (phase-3-design.md 6.1): New process, New actor, New scenario and Import XState. Pure:
// processDialogs.tsx renders them and sends each result as one batch (one change, one undo entry).
import { IDENTIFIER } from "@/model/model";
import { PROCESS_LABELS } from "@/model/labels";

type Json = Record<string, unknown>;

export type BatchOp =
  { op: "create"; element: Json } | { op: "update"; id: string; expectedHash: string; element: Json } | { op: "delete"; id: string; expectedHash: string };

export type ProcessUse = "lifecycle" | "orchestration";
/** The bound attribute choice: an attribute id of the subject, or a new status attribute and enum. */
export const NEW_STATUS = "@new";

export interface EntityDocument {
  json: Json;
  hash: string;
}

export interface NewProcessInput {
  name: string;
  domain: string | null;
  use: ProcessUse;
  /** The subject entity's id (required for a lifecycle, optional for an orchestration). */
  subject: string | null;
  /** The subject's document (a lifecycle updates it: `lifecycle`, and a new status attribute). */
  subjectDoc?: EntityDocument | null;
  /** An enum-typed attribute id of the subject, or NEW_STATUS. */
  attribute?: string | null;
  /** The bound enum's member names (an existing attribute): the lifecycle starts with one state per member. */
  members?: readonly string[];
  /** The subject's current lifecycle process, when it has one: it turns back into an orchestration in the same batch. */
  previousDoc?: EntityDocument | null;
  /** Names already used by types in the subject's domain, so the new enum gets a free one. */
  takenTypeNames?: ReadonlySet<string>;
}

export interface EnumAttribute {
  id: string;
  name: string;
  enumId: string;
}

/** The subject's enum-typed attributes (the bound attribute picker). */
export function enumAttributes(entity: Json | null | undefined, isEnum: (id: string) => boolean): EnumAttribute[] {
  const list = Array.isArray(entity?.attributes) ? (entity.attributes as Json[]) : [];
  return list.flatMap((a) => {
    const ref = (a.type as Json | undefined)?.ref;
    return typeof ref === "string" && isEnum(ref) && typeof a.id === "string" ? [{ id: a.id, name: String(a.name), enumId: ref }] : [];
  });
}

export function processNameProblem(name: string): string | null {
  const trimmed = name.trim();
  if (!trimmed) return "Enter a name.";
  return IDENTIFIER.test(trimmed) ? null : "Use letters, digits and underscores, not starting with a digit.";
}

/** Why the New process dialog cannot save yet, or null. */
export function processProblem(input: NewProcessInput): string | null {
  const named = processNameProblem(input.name);
  if (named) return named;
  if (input.use === "lifecycle") {
    if (!input.subject) return "Choose the entity whose lifecycle this is.";
    if (!input.attribute) return "Choose the status attribute, or a new one.";
    if (!input.subjectDoc) return "Loading the entity…";
    const previous = input.subjectDoc.json.lifecycle;
    if (typeof previous === "string" && !input.previousDoc) return "Loading the entity's lifecycle…";
  }
  return null;
}

/**
 * The New process batch: the process (one state, Initial, or one root state per enum member), and for a lifecycle the
 * subject's update (its `lifecycle`, and a new status attribute) and a new enum when asked for.
 */
export function buildProcessBatch(input: NewProcessInput, newId: () => string): { ops: BatchOp[]; processId: string } {
  const processId = newId();
  const name = input.name.trim();
  const ops: BatchOp[] = [];
  let stateNames: readonly string[] = [PROCESS_LABELS.initialState];
  const process: Json = { kind: "process", id: processId, name, ...(input.domain ? { package: input.domain } : {}) };
  if (input.use === "lifecycle" && input.subject && input.subjectDoc && input.attribute) {
    const entity = structuredClone(input.subjectDoc.json);
    let attribute = input.attribute;
    if (attribute === NEW_STATUS) {
      const enumId = newId();
      attribute = newId();
      const taken = input.takenTypeNames ?? new Set<string>();
      let enumName = `${String(entity.name)}Status`;
      for (let n = 2; taken.has(enumName.toLowerCase()); n++) enumName = `${String(entity.name)}Status${n}`;
      ops.push({
        op: "create",
        element: {
          kind: "enum",
          id: enumId,
          name: enumName,
          ...(typeof entity.package === "string" ? { package: entity.package } : {}),
          members: [{ id: newId(), name: PROCESS_LABELS.initialState }],
        },
      });
      const attributes = Array.isArray(entity.attributes) ? (entity.attributes as Json[]) : [];
      const names = new Set(attributes.map((a) => String(a.name).toLowerCase()));
      let attributeName = "status";
      for (let n = 2; names.has(attributeName); n++) attributeName = `status${n}`;
      // The default is the initial state's member, so the new lifecycle starts clean (MQ9205).
      entity.attributes = [...attributes, { id: attribute, name: attributeName, type: { ref: enumId }, default: PROCESS_LABELS.initialState }];
    } else if (input.members?.length) stateNames = input.members;
    entity.lifecycle = processId;
    ops.push({ op: "update", id: String(entity.id), expectedHash: input.subjectDoc.hash, element: entity });
    // The entity's previous lifecycle turns back into an orchestration, as the set-lifecycle operation does (MQ9201).
    const previous = input.previousDoc;
    if (previous && previous.json.id === input.subjectDoc.json.lifecycle && previous.json.id !== processId) {
      const unbound = structuredClone(previous.json);
      delete unbound.use;
      delete unbound.boundAttribute;
      ops.push({ op: "update", id: String(unbound.id), expectedHash: previous.hash, element: unbound });
    }
    Object.assign(process, { use: "lifecycle", subject: input.subject, boundAttribute: attribute });
  } else if (input.subject) process.subject = input.subject;
  process.states = stateNames.map((n) => ({ id: newId(), name: n }));
  ops.unshift({ op: "create", element: process });
  return { ops, processId };
}

export const ACTOR_TYPES = ["person", "role", "external-system"] as const;
export type ActorType = (typeof ACTOR_TYPES)[number];

export interface NewActorInput {
  name: string;
  type: ActorType;
  stereotypes: readonly string[];
  /** The persona goals, one per line, when a chosen stereotype's extension declares `goals`. */
  goals?: string;
}

export function buildActor(input: NewActorInput, newId: () => string): Json {
  const goals = (input.goals ?? "")
    .split("\n")
    .map((g) => g.trim())
    .filter(Boolean);
  return {
    kind: "actor",
    id: newId(),
    name: input.name.trim(),
    type: input.type,
    ...(input.stereotypes.length ? { stereotypes: [...input.stereotypes] } : {}),
    ...(goals.length ? { properties: { goals } } : {}),
  };
}

/** The stereotype keys whose extension adds a goals list to actors (the persona field). */
export function goalsStereotypes(extensions: readonly { appliesTo?: { kinds?: string[]; stereotypes?: string[] }; properties?: Json }[]): Set<string> {
  const out = new Set<string>();
  for (const e of extensions)
    if (e.appliesTo?.kinds?.includes("actor") && e.properties && "goals" in e.properties) for (const s of e.appliesTo.stereotypes ?? []) out.add(s);
  return out;
}

export type ScenarioStart = "record" | "empty";

/** An empty scenario: one step (a scenario has at least one), on the process's first event when it has one. */
export function buildScenario(name: string, process: Json, newId: () => string): Json {
  const firstEvent = Array.isArray(process.events) ? (process.events as Json[])[0]?.id : undefined;
  return {
    kind: "scenario",
    id: newId(),
    name: name.trim(),
    process: process.id,
    steps: [{ id: newId(), ...(typeof firstEvent === "string" ? { event: firstEvent } : {}) }],
  };
}

/** Deleting a process deletes its scenarios in the same batch (the delete dialog says so). */
export function deleteProcessOps(process: { id: string; hash: string }, scenarios: readonly { id: string; hash: string }[]): BatchOp[] {
  return [...scenarios.map((s) => ({ op: "delete" as const, id: s.id, expectedHash: s.hash })), { op: "delete", id: process.id, expectedHash: process.hash }];
}

export interface ImportPreviewLine {
  created: number;
  removed: number;
  states: string[];
  events: string[];
  errors: number;
  warnings: number;
}

/** The Import dialog's preview of a dry run: what the import creates and removes, and its diagnostics. */
export function importPreview(result: { document: Json | null; created: string[]; removed: string[]; diagnostics: { severity: string }[] }): ImportPreviewLine {
  const doc = result.document ?? {};
  const states: string[] = [];
  const walk = (list: unknown) => (Array.isArray(list) ? (list as Json[]) : []).forEach((s) => (states.push(String(s.name)), walk(s.states)));
  walk(doc.states);
  return {
    created: result.created.length,
    removed: result.removed.length,
    states,
    events: (Array.isArray(doc.events) ? (doc.events as Json[]) : []).map((e) => String(e.name)),
    errors: result.diagnostics.filter((d) => d.severity === "error").length,
    warnings: result.diagnostics.filter((d) => d.severity === "warning").length,
  };
}

/** Reads the Import dialog's text: a JSON object, or a problem. */
export function parseConfig(text: string): { config: Json } | { problem: string } {
  if (!text.trim()) return { problem: "Paste the machine config, or choose a file." };
  try {
    const value = JSON.parse(text) as unknown;
    if (!value || typeof value !== "object" || Array.isArray(value)) return { problem: "The config must be a JSON object." };
    return { config: value as Json };
  } catch (error) {
    return { problem: `The config is not valid JSON: ${(error as Error).message}` };
  }
}
