// Actor types and membership helpers (actors-design.md section 3 and 9): who may hold members, direct and deep
// members read from the index rows' memberOf, and the one-system-actor rule. Pure, so the explorer, the editor and tests share them.
import { MEMBER_HOLDER_TYPES, type ActorType } from "./labels";

/** The part of an index row these helpers read. */
export interface ActorRow {
  id: string;
  kind: string;
  name: string;
  actorType?: ActorType;
  memberOf?: readonly string[];
}

export const isMemberHolder = (type: string | undefined): boolean => MEMBER_HOLDER_TYPES.includes(type as ActorType);

/** The refusal for a second Automatic actor (MQ9605), or null. `selfId` is the actor being edited. */
export function systemActorProblem(rows: Iterable<ActorRow>, selfId?: string): string | null {
  for (const r of rows)
    if (r.kind === "actor" && r.actorType === "system" && r.id !== selfId)
      return `This project already has an Automatic actor (${r.name}). There can be only one.`;
  return null;
}

/** The actors that list `holder` in their memberOf, in the order given. */
export function directMembers<T extends ActorRow>(rows: Iterable<T>, holder: string): T[] {
  return [...rows].filter((r) => r.kind === "actor" && r.memberOf?.includes(holder));
}

/** How many actors are members of `holder` at any depth; each counted once, a cycle never loops. */
export function memberCount(rows: Iterable<ActorRow>, holder: string): number {
  const all = [...rows];
  const seen = new Set<string>([holder]);
  const walk = (id: string) => {
    for (const m of directMembers(all, id))
      if (!seen.has(m.id)) {
        seen.add(m.id);
        walk(m.id);
      }
  };
  walk(holder);
  return seen.size - 1;
}

/** The actors that may be picked in a Member of field for `self`: roles, groups and organizations, never itself. */
export function memberOfChoices<T extends ActorRow>(rows: Iterable<T>, self: string): T[] {
  return [...rows].filter((r) => r.kind === "actor" && r.id !== self && isMemberHolder(r.actorType)).sort((a, b) => a.name.localeCompare(b.name));
}

/** The text after "member of" in an actor's row: the holders' names, in file order, unknown ids skipped. */
export function memberOfText(memberOf: readonly string[] | undefined, nameOf: (id: string) => string | undefined): string {
  const names = (memberOf ?? []).map(nameOf).filter((n): n is string => !!n);
  return names.length ? `member of ${names.join(", ")}` : "";
}

/** Would `candidate` as a new member of `holder` close a cycle (holder is already a member of candidate at any depth)? */
export function wouldCycle(rows: Iterable<ActorRow>, holder: string, candidate: string): boolean {
  if (holder === candidate) return true;
  const all = [...rows];
  const byId = new Map(all.map((r) => [r.id, r]));
  const seen = new Set<string>();
  const up = (id: string): boolean => {
    if (id === candidate) return true;
    if (seen.has(id)) return false;
    seen.add(id);
    return (byId.get(id)?.memberOf ?? []).some(up);
  };
  return up(holder);
}
