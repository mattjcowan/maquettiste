// The Calls dialog's conversions (processes-operations-design.md decisions 5 to 7 and section 12): the rows of an operation's one
// document, the call written back from them, and the one-line text of a call. Pure functions; the dialog holds only the draft.
import { treeText, type TreeNames } from "@/model/effects";
import type { CallDoc, Json } from "@/model/process";

type Rec = Record<string, unknown>;

export const CALL_LABELS = {
  column: "Calls",
  hint: "Runs nothing in the simulator; recorded in the trace",
  notRun: "not run",
  /** The clause at the top of the dialog: a call is a record, not a step the simulator takes. */
  runsNothing: "Runs nothing in the simulator. Your system makes this call; the simulator only records it.",
  none: "(none)",
  edit: (action: string) => `Edit the call of ${action}`,
  title: (action: string) => `Call of ${action}`,
  operation: "Operation",
  noOperation: "None",
  pickHint: "Your system is asked to run this operation when the action runs. The simulator runs nothing; it records the call in the trace.",
  inputs: "Inputs",
  input: "Input",
  type: "Type",
  required: "Required",
  value: "Value",
  suppliedBySystem: "Supplied by your system",
  notGiven: "Not given",
  noInputs: "This operation has no inputs.",
  loading: "Reading the operation…",
  /** MQ9026's text, shown in the row of a required input left empty. */
  missing: "MQ9026 Required and not given: your system must supply it, or give it a value here.",
  noOperationsOnSubjects: "On the process's subjects",
  otherOperations: "Other operations",
  refused: "The change was not accepted, so the dialog stays open. Fix it here, or cancel; the findings are in the Problems panel.",
} as const;

export interface InputRow {
  /** The operation's input member id. */
  member: string;
  name: string;
  type: string;
  required: boolean;
  hasDefault: boolean;
  /** A file, a collection, a record key with several attributes or none, or a type the expression language lacks: the team's system supplies it, a call can't give it (MQ9025). */
  supplied: boolean;
  /** The id of the stored call input, when the call gives this member. */
  inputId?: string;
  text: string;
  /** The text the row started with; unchanged text keeps the stored tree. */
  initial: string;
  stored: Json | null;
}

/** A member's type in plain words: the keyword, a referenced type's name, "<Entity> key", "file", or "list of ...". */
export function memberTypeText(member: Rec, nameOf: (id: string) => string | undefined): string {
  if (member.file === true) return "file";
  const base =
    typeof member.keyOf === "string"
      ? `${nameOf(member.keyOf) ?? "record"} key`
      : typeof member.type === "string"
        ? member.type
        : member.type && typeof member.type === "object" && typeof (member.type as Rec).ref === "string"
          ? (nameOf((member.type as Rec).ref as string) ?? "reference")
          : "";
  return member.collection === true ? `list of ${base}` : base;
}

/** The rows of the Inputs grid: one per input member of the operation, with what the call already gives. */
export function inputRows(
  members: readonly Rec[],
  call: CallDoc | undefined,
  names: TreeNames,
  nameOf: (id: string) => string | undefined,
  /** Whether a value can be given to the member at all (see `memberGivable`); files and collections are never givable. */
  canGive: (member: Rec) => boolean = () => true,
): InputRow[] {
  return members.map((m) => {
    const id = String(m.id);
    const given = call?.inputs?.find((x) => x.member === id);
    const text = given ? treeText(given.value, names) : "";
    return {
      member: id,
      name: String(m.name),
      type: memberTypeText(m, nameOf),
      required: m.required === true,
      hasDefault: m.default !== undefined,
      supplied: m.file === true || m.collection === true || !canGive(m),
      ...(given ? { inputId: given.id } : {}),
      text,
      initial: text,
      stored: given?.value ?? null,
    };
  });
}

/** The built-in keywords the expression language has (the others, such as float, binary and duration, can't be typed). */
const EXPRESSION_BUILTINS = new Set([
  "string",
  "text",
  "bool",
  "int16",
  "int32",
  "int64",
  "decimal",
  "double",
  "date",
  "time",
  "datetime",
  "datetimeoffset",
  "uuid",
  "ulid",
  "json",
]);

/** What `memberGivable` reads: the index rows and the documents of the entities and scalar types the members point at. */
export interface GivenLookup {
  /** The kind of an element, or undefined when unknown. */
  kindOf: (id: string) => string | undefined;
  /** An entity's base, or undefined. */
  baseOf: (id: string) => string | undefined;
  /** An entity or scalar type document, once read. */
  doc: (id: string) => Rec | undefined;
}

/** The ids of the elements `memberGivable` needs for these members: each keyed entity with its bases, each scalar type. */
export function givenSources(members: readonly Rec[], lookup: Pick<GivenLookup, "kindOf" | "baseOf">): string[] {
  const ids = new Set<string>();
  for (const m of members) {
    if (typeof m.keyOf === "string") for (const e of entityChain(m.keyOf, lookup.baseOf)) ids.add(e);
    const ref = refOf(m.type);
    if (ref && lookup.kindOf(ref) === "scalar-type") ids.add(ref);
  }
  return [...ids];
}

const refOf = (type: unknown): string | undefined =>
  type && typeof type === "object" && typeof (type as Rec).ref === "string" ? ((type as Rec).ref as string) : undefined;

function entityChain(id: string, baseOf: (id: string) => string | undefined): string[] {
  const out: string[] = [];
  for (let at: string | undefined = id; at && !out.includes(at); at = baseOf(at)) out.push(at);
  return out;
}

/** Whether the expression language has a type (mirrors the engine's `ExprType.IsAvailable` over a member's type). */
function typeAvailable(type: unknown, lookup: GivenLookup): boolean {
  if (typeof type === "string") return EXPRESSION_BUILTINS.has(type);
  const ref = refOf(type);
  if (!ref) return false;
  const kind = lookup.kindOf(ref);
  if (kind === "enum" || kind === "reference-type") return true;
  if (kind !== "scalar-type") return false;
  const base = lookup.doc(ref)?.base;
  // A scalar type not read yet counts as available: the engine decides at validation.
  return base === undefined || EXPRESSION_BUILTINS.has(String(base));
}

/**
 * Whether a call can give the member a value (mirrors the engine's `ExprSites.MemberType`): not a file or a collection, not a record key
 * of an entity with no key or a composite one (its own, else its nearest base's), not a type the expression language lacks.
 */
export function memberGivable(member: Rec, lookup: GivenLookup): boolean {
  if (member.file === true || member.collection === true) return false;
  if (typeof member.keyOf === "string") {
    const chain = entityChain(member.keyOf, lookup.baseOf);
    const owner = chain.map((id) => lookup.doc(id)).find((d) => d?.key !== undefined);
    const attributes = (owner?.key as Rec | undefined)?.attributes;
    if (!Array.isArray(attributes) || attributes.length !== 1) return false;
    const attribute = chain.flatMap((id) => (lookup.doc(id)?.attributes as Rec[] | undefined) ?? []).find((a) => a.id === attributes[0]);
    return !!attribute && typeAvailable(attribute.type, lookup);
  }
  return typeAvailable(member.type, lookup);
}

/** Whether a row shows MQ9026's text: required, no default, can be given, and empty. */
export const isMissing = (r: InputRow): boolean => r.required && !r.hasDefault && !r.supplied && !r.text.trim();

/**
 * The call the rows say, or null for none. An empty value is not given, so it is left out; a row whose text did not change keeps its
 * stored tree and its id; a row that can't be given is never written.
 */
export function callFromRows(operation: string | undefined, rows: readonly InputRow[], newId: () => string, stored?: CallDoc): CallDoc | null {
  if (!operation) return null;
  const written = new Map(
    rows
      .filter((r) => !r.supplied && r.text.trim())
      .map((r) => [r.member, { id: r.inputId ?? newId(), member: r.member, value: r.text === r.initial && r.stored ? r.stored : { text: r.text.trim() } }]),
  );
  const editable = new Set(rows.filter((r) => !r.supplied).map((r) => r.member));
  const used = new Set<string>();
  // Stored inputs keep their place; one with no row to edit it (a member that can't be given, or one the operation no longer has) is kept as it
  // is, since the Problems panel reports it and Apply must not remove what the dialog never showed.
  const inputs = (stored?.operation === operation ? (stored.inputs ?? []) : []).flatMap((x) => {
    if (!editable.has(x.member)) return [x];
    if (used.has(x.member)) return [];
    used.add(x.member);
    const next = written.get(x.member);
    return next ? [{ ...next, id: x.id }] : [];
  });
  for (const [member, input] of written) if (!used.has(member)) inputs.push(input);
  return { operation, ...(inputs.length ? { inputs } : {}) };
}

/** A call as one line: `issue(dueDays = context.termsDays)`. */
export function callText(call: CallDoc | undefined, operationName: string | undefined, memberName: (id: string) => string, names: TreeNames): string {
  if (!call) return "";
  const args = (call.inputs ?? []).map((x) => `${memberName(x.member)} = ${treeText(x.value, names) || "?"}`);
  return `${operationName ?? "?"}(${args.join(", ")})`;
}
