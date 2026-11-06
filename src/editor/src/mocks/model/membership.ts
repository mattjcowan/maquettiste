// The mock's actor membership (the engine's Processes/ActorMembership.cs, actors-design.md decisions 3 to 5): an actor in a
// list stands for itself and its members at any depth. Only a memberOf entry that names an existing role, group or
// organization other than the actor itself counts, once; a cycle never puts an actor among its own members.
type Json = Record<string, unknown>;

export interface ActorDoc {
  id: string;
  json: Json;
}

const HOLDERS = new Set(["role", "group", "organization"]);
const ordinal = (a: string, b: string) => (a < b ? -1 : a > b ? 1 : 0);

export class Membership {
  private readonly memberOfMap = new Map<string, string[]>();
  private readonly allMemberOfMap = new Map<string, Set<string>>();
  private readonly allMembersMap = new Map<string, Set<string>>();
  private readonly names = new Map<string, string>();
  /** The id of the project's system actor when it has exactly one, else null. */
  readonly systemActor: string | null;
  /** Whether some actor has a membership that counts; without one every actor stands only for itself. */
  readonly any: boolean;

  constructor(actors: Iterable<ActorDoc>) {
    const all = [...actors];
    const byId = new Map(all.map((a) => [a.id, a]));
    const systems: string[] = [];
    for (const a of all) {
      this.names.set(a.id, String(a.json.name ?? a.id));
      if (a.json.type === "system") systems.push(a.id);
      const list: string[] = [];
      if (a.json.relative !== undefined) continue; // an actor from the object is never a member (decision 12)
      const entries = Array.isArray(a.json.memberOf) ? (a.json.memberOf as unknown[]) : [];
      for (const target of entries) {
        if (typeof target !== "string" || target === a.id || list.includes(target)) continue;
        const holder = byId.get(target);
        if (holder && holder.json.relative === undefined && HOLDERS.has(String(holder.json.type))) list.push(target);
      }
      if (list.length) this.memberOfMap.set(a.id, list);
    }
    this.systemActor = systems.length === 1 ? systems[0] : null;
    this.any = this.memberOfMap.size > 0;
    for (const id of this.memberOfMap.keys()) {
      const reached = new Set<string>();
      const stack = [...(this.memberOfMap.get(id) ?? [])];
      while (stack.length) {
        const next = stack.pop()!;
        if (next === id || reached.has(next)) continue;
        reached.add(next);
        stack.push(...(this.memberOfMap.get(next) ?? []));
      }
      this.allMemberOfMap.set(id, reached);
      for (const holder of reached) {
        const set = this.allMembersMap.get(holder) ?? new Set<string>();
        set.add(id);
        this.allMembersMap.set(holder, set);
      }
    }
  }

  /** The actors an actor is directly a member of, in file order, counted entries only. */
  memberOf(id: string): readonly string[] {
    return this.memberOfMap.get(id) ?? [];
  }

  allMemberOf(id: string): ReadonlySet<string> {
    return this.allMemberOfMap.get(id) ?? new Set();
  }

  allMembers(id: string): ReadonlySet<string> {
    return this.allMembersMap.get(id) ?? new Set();
  }

  /** Whether `actor` is `listed` or one of its members at any depth. */
  accepts(listed: string, actor: string): boolean {
    return listed === actor || (this.any && this.allMemberOf(actor).has(listed));
  }

  /** Whether `actor` satisfies one of the list's actors. */
  allows(list: readonly string[], actor: string): boolean {
    return list.some((l) => this.accepts(l, actor));
  }

  /** Each listed actor, then its members at any depth that the list does not hold, by (name, id): the Act-as list. */
  withMembers(listed: readonly string[]): string[] {
    if (!this.any || listed.length === 0) return [...listed];
    const result = [...listed];
    const seen = new Set(listed);
    for (const id of listed) {
      const members = [...this.allMembers(id)].sort((a, b) => ordinal(this.names.get(a) ?? a, this.names.get(b) ?? b) || ordinal(a, b));
      for (const m of members)
        if (!seen.has(m)) {
          seen.add(m);
          result.push(m);
        }
    }
    return result;
  }
}
