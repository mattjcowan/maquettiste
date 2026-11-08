// The Gates tab's approval model (signoff-design.md sections 4, 5 and 11): the rows of the signatures grid, one per named
// signature of an approval's stage, the conversion between a gate with one list of signers and an approval, and the edits the
// grid makes on a gate. Every edit mutates the gate it is given, so one grid gesture is one draft change and one undo step.
// Part 1 holds exactly one stage, so there is no stage column and no round.
import { newId } from "@/lib/ids";
import { idsOfNames, uniqueName, type GateDoc, type GateStageDoc, type Json, type NamedSignatureDoc, type NotByDoc, type ProcessDoc } from "./process";
import { builtInVerbs } from "./verbs";

const text = (v: unknown): string => (typeof v === "string" ? v : "");

/** The names a row prints ids as. */
export interface ApprovalNames {
  actor(id: string): string;
  /** A move of the lifecycle, as the grid prints it. */
  transition(id: string): string;
  verb(key: string): string;
  /** A stored condition as text. */
  condition(tree: Json | undefined): string;
}

export interface SignatureRow {
  id: string;
  /** Where the signature sits in the process document. */
  pointer: string;
  name: string;
  /** Cell texts: names joined. */
  signers: string;
  requiredActors: string;
  required: number;
  repeat: boolean;
  when: string;
  notBy: string;
  why: string;
  signerIds: string[];
  requiredActorIds: string[];
  notByEntries: NotByDoc[];
}

/** The word a Not by cell shows while the signature excludes no one: anyone may give it. */
export const ANYONE = "Anyone";
/** The word a Needed when cell shows while the signature is always needed. */
export const ALWAYS = "Always";

/** Whether the gate is an approval: it has stages. */
export const isApproval = (gate: Pick<GateDoc, "stages">): boolean => !!gate.stages?.length;

/** The approvals an event's moves are gated by, in file order. */
export const approvalsOfEvent = (process: Pick<ProcessDoc, "transitions"> | undefined, event: string | undefined): GateDoc[] =>
  event ? (process?.transitions ?? []).flatMap((t) => (t.event === event && t.gate && isApproval(t.gate) ? [t.gate] : [])) : [];

/** Whether some move of the process is gated by an approval. */
export const hasApproval = (process: Pick<ProcessDoc, "transitions"> | undefined): boolean =>
  (process?.transitions ?? []).some((t) => !!t.gate && isApproval(t.gate));

/** An approval's signatures in file order (stage, then signature). */
export const signaturesOf = (gate: Pick<GateDoc, "stages">): NamedSignatureDoc[] => (gate.stages ?? []).flatMap((s) => s.signatures ?? []);

/** The rows of one approval's grid, in file order. `pointer` is the gate's pointer in the process document. */
export function signatureRows(gate: GateDoc, pointer: string, names: ApprovalNames): SignatureRow[] {
  const rows: SignatureRow[] = [];
  (gate.stages ?? []).forEach((stage, s) =>
    (stage.signatures ?? []).forEach((sig, j) => {
      const notBy = sig.notBy ?? [];
      rows.push({
        id: sig.id,
        pointer: `${pointer}/stages/${s}/signatures/${j}`,
        name: sig.name,
        signers: (sig.signers ?? []).map(names.actor).join(", "),
        requiredActors: (sig.requiredActors ?? []).map(names.actor).join(", "),
        required: sig.required ?? 1,
        repeat: sig.allowRepeatSigner === true,
        when: sig.when ? names.condition(sig.when) : "",
        notBy: notBy.length ? notBy.map((n) => (n.verb !== undefined ? names.verb(n.verb) : names.transition(n.transition ?? ""))).join(", ") : ANYONE,
        why: text(sig.description),
        signerIds: sig.signers ?? [],
        requiredActorIds: sig.requiredActors ?? [],
        notByEntries: notBy,
      });
    }),
  );
  return rows;
}

/** A name for a new signature that no signature of the gate has. */
export const freeSignatureName = (gate: GateDoc, base = "signature"): string =>
  uniqueName(
    signaturesOf(gate).map((s) => s.name),
    base,
  );

/**
 * Use named signatures: the gate becomes an approval with one stage, named after the gate, holding one signature that takes the
 * gate's signers, count, required actors and repeat setting. Returns false when the gate already has stages.
 */
export function toApproval(gate: GateDoc): boolean {
  if (isApproval(gate)) return false;
  const signature: NamedSignatureDoc = { id: newId(), name: "signature", signers: gate.signers ?? [] };
  if (gate.required !== undefined && gate.required !== 1) signature.required = gate.required;
  if (gate.requiredActors?.length) signature.requiredActors = gate.requiredActors;
  if (gate.allowRepeatSigner) signature.allowRepeatSigner = true;
  delete gate.signers;
  delete gate.required;
  delete gate.requiredActors;
  delete gate.allowRepeatSigner;
  gate.stages = [{ id: newId(), name: gate.name, signatures: [signature] }];
  return true;
}

/** Whether an approval can go back to one list of signers: one stage, one signature, with no `when` or `notBy`. */
export function canRevert(gate: GateDoc): boolean {
  const stages = gate.stages ?? [];
  if (stages.length !== 1) return false;
  const sigs = stages[0].signatures ?? [];
  return sigs.length === 1 && !sigs[0].when && !(sigs[0].notBy ?? []).length;
}

/** Back to one list of signers: the signature's keys move back onto the gate. Returns false when the approval holds more than that. */
export function revertToSigners(gate: GateDoc): boolean {
  if (!canRevert(gate)) return false;
  const sig = gate.stages![0].signatures[0];
  gate.signers = sig.signers ?? [];
  if (sig.required !== undefined && sig.required !== 1) gate.required = sig.required;
  if (sig.requiredActors?.length) gate.requiredActors = sig.requiredActors;
  if (sig.allowRepeatSigner) gate.allowRepeatSigner = true;
  delete gate.stages;
  delete gate.allowSignerAcrossSignatures;
  return true;
}

/** Adds a signature to the approval's last stage, taking the signers of the one it follows; returns its id, or null without a stage. */
export function addSignature(gate: GateDoc, after?: string): string | null {
  const stage = gate.stages?.[gate.stages.length - 1];
  if (!stage) return null;
  const at = after ? stage.signatures.findIndex((s) => s.id === after) : -1;
  const id = newId();
  const template = stage.signatures[at < 0 ? stage.signatures.length - 1 : at];
  stage.signatures.splice(at < 0 ? stage.signatures.length : at + 1, 0, { id, name: freeSignatureName(gate), signers: [...(template?.signers ?? [])] });
  return id;
}

/** Removes a signature; the last one stays (an approval needs one: go back to one list of signers instead). Returns false then. */
export function removeSignature(gate: GateDoc, id: string): boolean {
  if (signaturesOf(gate).length <= 1) return false;
  for (const stage of gate.stages ?? []) stage.signatures = stage.signatures.filter((s) => s.id !== id);
  return true;
}

/** The names in a typed list that no one of `options` has. */
export function unknownNames(raw: string, options: { id: string; name: string }[]): string[] {
  const known = new Set(options.map((o) => o.name.toLowerCase()));
  return raw
    .split(",")
    .map((p) => p.trim())
    .filter((p) => p && !known.has(p.toLowerCase()));
}

export interface SignatureEditOptions {
  /** Every actor, for Signers. */
  actors: { id: string; name: string }[];
}

/** What a refused cell says, or null when the cell can take the value. */
export function refusal(sig: NamedSignatureDoc, key: string, value: string | boolean, options: SignatureEditOptions): string | null {
  const raw = String(value);
  if (key === "signers" || key === "requiredActors") {
    const bad = unknownNames(raw, options.actors);
    if (bad.length) return `No actor is named ${bad.map((b) => `'${b}'`).join(", ")}. Pick from the project's actors.`;
    if (key === "signers" && !idsOfNames(raw, options.actors).length) return "A signature needs at least one signer.";
    if (key === "requiredActors") {
      const among = (sig.signers ?? []).map((id) => options.actors.find((a) => a.id === id)).filter((a): a is { id: string; name: string } => !!a);
      const stray = unknownNames(raw, among);
      if (stray.length) return `${stray.map((b) => `'${b}'`).join(", ")} must be one of the signers to be a required actor.`;
    }
  }
  if (key === "required") {
    const n = Number(raw.trim());
    if (!Number.isInteger(n) || n < 1) return "Required is a whole number from 1.";
  }
  if (key === "name" && !raw.trim()) return "A signature needs a name.";
  return null;
}

/**
 * One cell's commit. Signers and Required actors come as typed names (an unknown name is refused, see `refusal`), Required as
 * a number. Returns false when nothing changed.
 */
export function editSignature(gate: GateDoc, id: string, key: string, value: string | boolean, options: SignatureEditOptions): boolean {
  const sig = signaturesOf(gate).find((s) => s.id === id);
  if (!sig || refusal(sig, key, value, options)) return false;
  const raw = String(value);
  const doc = sig as unknown as Json;
  switch (key) {
    case "name":
      sig.name = raw.trim();
      return true;
    case "signers": {
      sig.signers = idsOfNames(raw, options.actors);
      // A required actor that is no longer a signer leaves the list, as it does on a plain gate.
      if (sig.requiredActors) sig.requiredActors = sig.requiredActors.filter((a) => sig.signers.includes(a));
      if (!sig.requiredActors?.length) delete sig.requiredActors;
      return true;
    }
    case "requiredActors": {
      const ids = idsOfNames(raw, options.actors);
      if (ids.length) sig.requiredActors = ids;
      else delete sig.requiredActors;
      return true;
    }
    case "required": {
      const n = Number(raw.trim());
      if (n === 1) delete sig.required;
      else sig.required = n;
      return true;
    }
    case "repeat":
      if (value === true || raw === "true") sig.allowRepeatSigner = true;
      else delete sig.allowRepeatSigner;
      return true;
    case "when": {
      const t = raw.trim();
      if (t) doc.when = { text: t };
      else delete sig.when;
      return true;
    }
    case "why": {
      const t = raw.trim();
      if (t) sig.description = t;
      else delete sig.description;
      return true;
    }
    default:
      return false;
  }
}

/** Sets a signature's Not by entries as the popover gives them; an empty list is left out. */
export function setNotBy(gate: GateDoc, id: string, entries: NotByDoc[]): boolean {
  const sig = signaturesOf(gate).find((s) => s.id === id);
  if (!sig) return false;
  if (entries.length) sig.notBy = entries;
  else delete sig.notBy;
  return true;
}

/**
 * The verbs a Not by may name (MQ8202): those the lifecycle's own runs see, which are move verbs, signature verbs, complete and the
 * verbs its events name.
 */
export function notByVerbKeys(process: Pick<ProcessDoc, "events">): string[] {
  const keys = new Set(builtInVerbs.filter((v) => v.group === "move" || v.group === "signature" || v.key === "complete").map((v) => v.key));
  for (const e of process.events ?? []) if (e.verb) keys.add(e.verb);
  return [...keys];
}

/** The stage of an approval, for its heading. */
export const stageOf = (gate: GateDoc): GateStageDoc | undefined => gate.stages?.[0];

/** MQ8203's fix: the stages in round order, kept in their file order within a round (part 2 lets a stage name its round). */
export function reorderStages(gate: GateDoc): boolean {
  const stages = gate.stages ?? [];
  const round = (s: GateStageDoc) => ((s as unknown as Json).round as number | undefined) ?? 1;
  const sorted = stages.map((s, i) => ({ s, i })).sort((a, b) => round(a.s) - round(b.s) || a.i - b.i);
  if (sorted.every((x, i) => x.i === i)) return false;
  gate.stages = sorted.map((x) => x.s);
  return true;
}

/** Every actor id that may sign a gate document (the mock's reading of one list of signers, or of the signatures of its stages). */
export function gateSignerIds(gate: Json | undefined): string[] {
  const ids = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
  const stages = Array.isArray(gate?.stages) ? (gate.stages as Json[]) : [];
  const own = ids(gate?.signers);
  const all = stages.flatMap((st) => (Array.isArray(st.signatures) ? (st.signatures as Json[]) : []).flatMap((sg) => ids(sg.signers)));
  return [...new Set([...own, ...all])];
}
