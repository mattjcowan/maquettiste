// The Gates tab's approval model (signoff-design.md sections 4, 5 and 11): the rows of the signatures grid, one per named
// signature of an approval's stage, the conversion between a gate with one list of signers and an approval, and the edits the
// grid makes on a gate. Every edit mutates the gate it is given, so one grid gesture is one draft change and one undo step.
// Part 2 adds stages in rounds (side by side within a round, rounds one after another), a stage's Skip unless, and the signature's
// delegation, reminder, escalation and password settings.
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
  /** The stage that holds the signature. */
  stage: string;
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
  /** Part 2 cells: names joined, ISO 8601 text, empty when unset. */
  delegateTo: string;
  remindAfter: string;
  escalateAfter: string;
  escalateTo: string;
  reauthenticate: boolean;
  /** The Details cell: what part 2 settings the signature has, or the word for none. */
  details: string;
  /** The Details cell's text: how many part 2 settings the signature has, or the word for none; `details` is its tooltip. */
  detailsShort: string;
  delegateToIds: string[];
  escalateToIds: string[];
  signerIds: string[];
  requiredActorIds: string[];
  notByEntries: NotByDoc[];
}

/** The word a Not by cell shows while the signature excludes no one: anyone may give it. */
export const ANYONE = "Anyone";
/** The word a Details cell shows while the signature has no part 2 setting. */
export const NO_DETAILS = "None";
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

/** Whether a signature of the approvals of the event may be delegated (`delegateTo`); with no event, of any approval of the process. */
export const mayDelegate = (process: Pick<ProcessDoc, "transitions"> | undefined, event?: string): boolean =>
  (event === undefined ? (process?.transitions ?? []).flatMap((t) => (t.gate && isApproval(t.gate) ? [t.gate] : [])) : approvalsOfEvent(process, event)).some(
    (g) => signaturesOf(g).some((sg) => !!sg.delegateTo?.length),
  );

/** Whether some signature of the process states a reminder or an escalation. */
export const hasNotices = (process: Pick<ProcessDoc, "transitions"> | undefined): boolean =>
  (process?.transitions ?? []).some((t) => !!t.gate && signaturesOf(t.gate).some((sg) => !!sg.remindAfter || !!sg.escalateAfter));

/** The Details cell: the part 2 settings a signature has, in a few words. */
export function detailsText(sig: NamedSignatureDoc): string {
  const parts: string[] = [];
  if (sig.delegateTo?.length) parts.push("Delegation");
  if (sig.remindAfter) parts.push(`Remind ${sig.remindAfter}`);
  if (sig.escalateAfter || sig.escalateTo?.length) parts.push(`Escalate ${sig.escalateAfter ?? ""}`.trim());
  if (sig.reauthenticate) parts.push("Password");
  return parts.length ? parts.join(", ") : NO_DETAILS;
}

/** How many part 2 settings a signature has, as the narrow Details cell says it. */
export function detailsCount(sig: NamedSignatureDoc): string {
  const n = [sig.delegateTo?.length, sig.remindAfter, sig.escalateAfter || sig.escalateTo?.length, sig.reauthenticate].filter(Boolean).length;
  return n ? `${n} set` : NO_DETAILS;
}

const DURATION = /^P(?=\d|T\d)(?:(\d+)Y)?(?:(\d+)M)?(?:(\d+)W)?(?:(\d+)D)?(?:T(?=\d)(?:(\d+)H)?(?:(\d+)M)?(\d+(?:[.,]\d+)?S)?)?$/;

/**
 * An ISO 8601 duration in milliseconds, read as an `after` transition's delay is (a year is 365 days, a month 30); null when the
 * text is not one.
 */
export function durationMs(raw: string | null | undefined): number | null {
  if (!raw) return null;
  const m = DURATION.exec(raw);
  if (!m) return null;
  const n = (i: number) => (m[i] ? Number(m[i]) : 0);
  const days = n(1) * 365 + n(2) * 30 + n(3) * 7 + n(4);
  const seconds = m[7] ? Number(m[7].slice(0, -1).replace(",", ".")) : 0;
  return ((days * 24 + n(5)) * 60 + n(6)) * 60_000 + Math.round(seconds * 1000);
}

/** The stage's round: 1 when it names none. */
export const roundOf = (stage: Pick<GateStageDoc, "round">): number => stage.round ?? 1;

export interface RoundGroup {
  round: number;
  stages: GateStageDoc[];
}

/** The stages grouped by round, lowest round first, each round's stages in file order. */
export function roundGroups(gate: Pick<GateDoc, "stages">): RoundGroup[] {
  const groups = new Map<number, GateStageDoc[]>();
  for (const stage of gate.stages ?? []) groups.set(roundOf(stage), [...(groups.get(roundOf(stage)) ?? []), stage]);
  return [...groups].sort((a, b) => a[0] - b[0]).map(([round, stages]) => ({ round, stages }));
}

/** The rows of one approval's grid, in file order. `pointer` is the gate's pointer in the process document. */
export function signatureRows(gate: GateDoc, pointer: string, names: ApprovalNames): SignatureRow[] {
  const rows: SignatureRow[] = [];
  (gate.stages ?? []).forEach((stage, s) =>
    (stage.signatures ?? []).forEach((sig, j) => {
      const notBy = sig.notBy ?? [];
      rows.push({
        id: sig.id,
        stage: stage.id,
        pointer: `${pointer}/stages/${s}/signatures/${j}`,
        name: sig.name,
        signers: (sig.signers ?? []).map(names.actor).join(", "),
        requiredActors: (sig.requiredActors ?? []).map(names.actor).join(", "),
        required: sig.required ?? 1,
        repeat: sig.allowRepeatSigner === true,
        when: sig.when ? names.condition(sig.when) : "",
        notBy: notBy.length ? notBy.map((n) => (n.verb !== undefined ? names.verb(n.verb) : names.transition(n.transition ?? ""))).join(", ") : ANYONE,
        why: text(sig.description),
        delegateTo: (sig.delegateTo ?? []).map(names.actor).join(", "),
        remindAfter: sig.remindAfter ?? "",
        escalateAfter: sig.escalateAfter ?? "",
        escalateTo: (sig.escalateTo ?? []).map(names.actor).join(", "),
        reauthenticate: sig.reauthenticate === true,
        details: detailsText(sig),
        detailsShort: detailsCount(sig),
        delegateToIds: sig.delegateTo ?? [],
        escalateToIds: sig.escalateTo ?? [],
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
  const stage = stages[0];
  if (stage.when || roundOf(stage) !== 1) return false;
  if (sigs.length !== 1) return false;
  const sig = sigs[0];
  const part2 = !!(sig.delegateTo?.length || sig.remindAfter || sig.escalateAfter || sig.escalateTo?.length || sig.reauthenticate);
  return !sig.when && !(sig.notBy ?? []).length && !part2;
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

/**
 * Adds a signature to a stage (the last one when none is named), taking the signers of the one it follows; returns its id, or null
 * without such a stage.
 */
export function addSignature(gate: GateDoc, after?: string, stageId?: string): string | null {
  const stage = stageId ? gate.stages?.find((s) => s.id === stageId) : gate.stages?.[gate.stages.length - 1];
  if (!stage) return null;
  const at = after ? stage.signatures.findIndex((s) => s.id === after) : -1;
  const id = newId();
  const template = stage.signatures[at < 0 ? stage.signatures.length - 1 : at] ?? signaturesOf(gate).at(-1);
  stage.signatures.splice(at < 0 ? stage.signatures.length : at + 1, 0, { id, name: freeSignatureName(gate), signers: [...(template?.signers ?? [])] });
  return id;
}

/**
 * Removes a signature; the last one of the approval stays (an approval needs one: go back to one list of signers instead). A stage
 * left with no signature goes with it. Returns false when nothing was removed.
 */
export function removeSignature(gate: GateDoc, id: string): boolean {
  if (signaturesOf(gate).length <= 1) return false;
  for (const stage of gate.stages ?? []) stage.signatures = stage.signatures.filter((s) => s.id !== id);
  gate.stages = (gate.stages ?? []).filter((s) => s.signatures.length > 0);
  return true;
}

/** A name for a new stage that no stage of the gate has. */
export const freeStageName = (gate: GateDoc, base = "stage"): string =>
  uniqueName(
    (gate.stages ?? []).map((s) => s.name),
    base,
  );

/**
 * Add stage: a new stage in the next round after the last one, holding one signature that takes the signers of the last signature.
 * Returns the stage's id, or null when the gate is not an approval.
 */
export function addStage(gate: GateDoc): string | null {
  if (!isApproval(gate)) return null;
  const round = Math.max(...gate.stages!.map(roundOf)) + 1;
  const template = signaturesOf(gate).at(-1);
  const signature: NamedSignatureDoc = { id: newId(), name: freeSignatureName(gate), signers: [...(template?.signers ?? [])] };
  const stage: GateStageDoc = { id: newId(), name: freeStageName(gate), round, signatures: [signature] };
  gate.stages!.push(stage);
  return stage.id;
}

/** Removes a stage and its signatures; an approval keeps at least one stage. Returns false then. */
export function removeStage(gate: GateDoc, id: string): boolean {
  if (!gate.stages || gate.stages.length <= 1 || !gate.stages.some((s) => s.id === id)) return false;
  gate.stages = gate.stages.filter((s) => s.id !== id);
  return true;
}

/**
 * Moves a signature to the end of another stage. A stage left with no signature goes with it. Returns false when either is unknown or
 * the signature is already there.
 */
export function moveSignature(gate: GateDoc, id: string, stageId: string): boolean {
  const to = gate.stages?.find((s) => s.id === stageId);
  const from = gate.stages?.find((s) => s.signatures.some((x) => x.id === id));
  const sig = from?.signatures.find((x) => x.id === id);
  if (!to || !from || !sig || from === to) return false;
  from.signatures = from.signatures.filter((x) => x.id !== id);
  to.signatures.push(sig);
  if (!from.signatures.length) gate.stages = gate.stages!.filter((s) => s !== from);
  return true;
}

/** What a refused stage cell says, or null when it can take the value. */
export function stageRefusal(gate: GateDoc, id: string, key: string, value: string): string | null {
  const raw = value.trim();
  if (key === "name") {
    if (!raw) return "A stage needs a name.";
    if ((gate.stages ?? []).some((s) => s.id !== id && s.name.toLowerCase() === raw.toLowerCase())) return `Another stage is named '${raw}'.`;
  }
  if (key === "round") {
    const n = Number(raw);
    if (!Number.isInteger(n) || n < 1) return "Round is a whole number from 1.";
  }
  return null;
}

/**
 * One stage cell's commit: Name, Round (a change keeps the stages in round order) and Skip unless (the text of a condition). Returns
 * false when nothing changed.
 */
export function editStage(gate: GateDoc, id: string, key: string, value: string): boolean {
  const stage = gate.stages?.find((s) => s.id === id);
  if (!stage || stageRefusal(gate, id, key, value)) return false;
  const raw = value.trim();
  switch (key) {
    case "name":
      stage.name = raw;
      return true;
    case "round": {
      const n = Number(raw);
      if (n === 1) delete stage.round;
      else stage.round = n;
      reorderStages(gate);
      return true;
    }
    case "when":
      if (raw) stage.when = { text: raw };
      else delete stage.when;
      return true;
    default:
      return false;
  }
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
  /** Every actor, for Signers, May be delegated to and Escalate to. */
  actors: { id: string; name: string }[];
}

/** What a refused cell says, or null when the cell can take the value. */
export function refusal(sig: NamedSignatureDoc, key: string, value: string | boolean, options: SignatureEditOptions): string | null {
  const raw = String(value);
  if (key === "delegateTo" || key === "escalateTo") {
    const bad = unknownNames(raw, options.actors);
    if (bad.length) return `No actor is named ${bad.map((b) => `'${b}'`).join(", ")}. Pick from the project's actors.`;
  }
  if ((key === "remindAfter" || key === "escalateAfter") && raw.trim()) {
    const ms = durationMs(raw.trim());
    if (ms === null) return `'${raw.trim()}' is not an ISO 8601 duration. Write it like PT4H or P2D.`;
    if (ms <= 0) return "A reminder or escalation delay must be longer than zero.";
  }
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
    case "delegateTo": {
      const ids = idsOfNames(raw, options.actors);
      if (ids.length) sig.delegateTo = ids;
      else delete sig.delegateTo;
      return true;
    }
    case "escalateTo": {
      const ids = idsOfNames(raw, options.actors);
      if (ids.length) sig.escalateTo = ids;
      else {
        // An escalation with no one to tell goes with its delay (MQ8204).
        delete sig.escalateTo;
        delete sig.escalateAfter;
      }
      return true;
    }
    case "remindAfter":
    case "escalateAfter": {
      const t = raw.trim();
      if (t) sig[key] = t;
      else delete sig[key];
      return true;
    }
    case "reauthenticate":
      if (value === true || raw === "true") sig.reauthenticate = true;
      else delete sig.reauthenticate;
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

/**
 * MQ8204's fix: add the missing half of the escalation pair, or remove it. A missing `escalateAfter` is added, a day after the
 * reminder (or P1D); a missing `escalateTo` has no one to guess, so the lone `escalateAfter` goes. Returns false when the pair is whole.
 */
export function fixEscalationPair(sig: NamedSignatureDoc): boolean {
  if (sig.escalateTo?.length && !sig.escalateAfter) {
    const remind = durationMs(sig.remindAfter);
    sig.escalateAfter = remind && remind > 0 ? `PT${Math.ceil(remind / 1000) + 86_400}S` : "P1D";
    return true;
  }
  if (sig.escalateAfter && !sig.escalateTo?.length) {
    delete sig.escalateAfter;
    return true;
  }
  return false;
}

/** MQ8203's fix: the stages in round order, kept in their file order within a round. */
export function reorderStages(gate: GateDoc): boolean {
  const stages = gate.stages ?? [];
  const round = roundOf;
  const sorted = stages.map((s, i) => ({ s, i })).sort((a, b) => round(a.s) - round(b.s) || a.i - b.i);
  if (sorted.every((x, i) => x.i === i)) return false;
  gate.stages = sorted.map((x) => x.s);
  return true;
}

/** The actor ids that may give a signature that can be delegated (a signature with `delegateTo`) on a gate document: who may answer delegate. */
export function gateDelegatorIds(gate: Json | undefined): string[] {
  const ids = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
  const stages = Array.isArray(gate?.stages) ? (gate.stages as Json[]) : [];
  const all = stages.flatMap((st) =>
    (Array.isArray(st.signatures) ? (st.signatures as Json[]) : []).flatMap((sg) => (ids(sg.delegateTo).length ? ids(sg.signers) : [])),
  );
  return [...new Set(all)];
}

/** Every actor id that may sign a gate document (the mock's reading of one list of signers, or of the signatures of its stages). */
export function gateSignerIds(gate: Json | undefined): string[] {
  const ids = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
  const stages = Array.isArray(gate?.stages) ? (gate.stages as Json[]) : [];
  const own = ids(gate?.signers);
  const all = stages.flatMap((st) => (Array.isArray(st.signatures) ? (st.signatures as Json[]) : []).flatMap((sg) => ids(sg.signers)));
  return [...new Set([...own, ...all])];
}
