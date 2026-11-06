// A scenario's participants (actors-design.md decisions 14 to 17): the rows of the grid, the text of a holding and the
// choices of the pickers. Pure: no React, no I/O.
import { PARTICIPANT_LABELS as L, type ActorType } from "./labels";
import type { HoldingDoc, ParticipantDoc, ScenarioDoc } from "./process";

export interface ActorChoice {
  id: string;
  name: string;
  type?: ActorType;
  /** An actor taken from the object: it is never a participant's actor or holding. */
  relative?: boolean;
}

export interface ParticipantRow {
  id: string;
  index: number;
  pointer: string;
  name: string;
  displayName: string;
  identity: string;
  actor: string;
  holds: string;
  object: string;
}

const HOLDERS: readonly ActorType[] = ["role", "group", "organization"];
const STANDS_FOR: readonly ActorType[] = ["person", "external-system", "system"];

/** The actors a participant may stand for: a person, an external system or the system actor, never one from the object. */
export const standsFor = (actors: readonly ActorChoice[]): ActorChoice[] => actors.filter((a) => !a.relative && !!a.type && STANDS_FOR.includes(a.type));

/** The actors a participant may hold: a role, group or organization, never one from the object. */
export const holdable = (actors: readonly ActorChoice[]): ActorChoice[] => actors.filter((a) => !a.relative && !!a.type && HOLDERS.includes(a.type));

/** A holding as text: the role's name, then `within` and the object's name when it has a context. */
export function holdsText(holds: readonly HoldingDoc[] | undefined, actorNames: ReadonlyMap<string, string>, objectNames: ReadonlyMap<string, string>): string {
  return (holds ?? [])
    .map((h) => {
      const actor = actorNames.get(h.actor) ?? "?";
      return h.within ? `${actor} within ${objectNames.get(h.within) ?? "?"}` : actor;
    })
    .join(", ");
}

/** Text back to holdings, by name (case-insensitive). A holding with the same actor and context keeps its id; others get a new one.
 * Returns the first part that names nothing known as `error`. */
export function parseHolds(
  text: string,
  holdable: readonly ActorChoice[],
  objects: readonly { id: string; name: string }[],
  previous: readonly HoldingDoc[] | undefined,
  mint: () => string,
): { holds: HoldingDoc[]; error: string | null } {
  const holds: HoldingDoc[] = [];
  for (const raw of text.split(",")) {
    const part = raw.trim();
    if (!part) continue;
    const at = part.search(/\s+(?:in|within)\s+/i);
    const actorName = (at < 0 ? part : part.slice(0, at)).trim().toLowerCase();
    const objectName =
      at < 0
        ? null
        : part
            .slice(at)
            .replace(/^\s+(?:in|within)\s+/i, "")
            .trim()
            .toLowerCase();
    const actor = holdable.find((a) => a.name.toLowerCase() === actorName);
    const object = objectName === null ? null : (objects.find((o) => o.name.toLowerCase() === objectName) ?? null);
    if (!actor || (objectName !== null && !object)) return { holds: [], error: L.holdsUnknown(part) };
    const within = object?.id;
    const same = previous?.find((h) => h.actor === actor.id && h.within === within && !holds.some((x) => x.id === h.id));
    holds.push({ id: same?.id ?? mint(), actor: actor.id, ...(within ? { within } : {}) });
  }
  return { holds, error: null };
}

/** One row per participant, names where the file holds ids. */
export function participantRows(scenario: Pick<ScenarioDoc, "participants" | "objects">, actorNames: ReadonlyMap<string, string>): ParticipantRow[] {
  const objectNames = new Map((scenario.objects ?? []).map((o) => [o.id, o.name]));
  return (scenario.participants ?? []).map((p: ParticipantDoc, index) => ({
    id: p.id,
    index,
    pointer: `/participants/${index}`,
    name: p.name,
    displayName: p.displayName ?? "",
    identity: p.identity === undefined ? "" : String(p.identity),
    actor: p.actor ? (actorNames.get(p.actor) ?? "?") : "",
    holds: holdsText(p.holds, actorNames, objectNames),
    object: p.object ? (objectNames.get(p.object) ?? "?") : "",
  }));
}

/** The next free participant name: `participant`, `participant2`, … */
export function newParticipantName(scenario: Pick<ScenarioDoc, "participants">): string {
  const used = new Set((scenario.participants ?? []).map((p) => p.name.toLowerCase()));
  let name: string = L.singular;
  for (let n = 2; used.has(name); n++) name = `${L.singular}${n}`;
  return name;
}

/** An identity as the file keeps it: an integer when the text is one, else the text; none when empty. */
export function identityOf(text: string): string | number | undefined {
  const t = text.trim();
  if (!t) return undefined;
  return /^-?\d+$/.test(t) && Number.isSafeInteger(Number(t)) ? Number(t) : t;
}

/** What a removed participant leaves behind: steps and objects that named it forget it. */
export function forgetParticipant(scenario: ScenarioDoc, id: string): void {
  scenario.participants = (scenario.participants ?? []).filter((p) => p.id !== id);
  if (!scenario.participants.length) delete scenario.participants;
  for (const s of scenario.steps) if (s.participant === id) delete s.participant;
  for (const o of scenario.objects ?? []) if (o.owner === id) delete o.owner;
}
