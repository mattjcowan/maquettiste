// The mock's actor rules (ProcessRules.cs CheckActor, actors-design.md section 5): MQ9601 a memberOf entry that names an
// actor that holds no members, the actor itself, or the same actor twice; MQ9602 a membership cycle; MQ9605 a second system actor.
import type { Diagnostic } from "@/api/types";
import { Membership } from "./membership";

type Json = Record<string, unknown>;
interface Entry {
  id: string;
  path: string;
  json: Json;
}

const TYPE_TEXT: Record<string, string> = {
  person: "a person",
  "external-system": "an external system",
  system: "the system actor",
  role: "a role",
  group: "a group",
  organization: "an organization",
};

export function actorDiagnostics(json: Json, entry: Entry, all: Iterable<Entry>): Diagnostic[] {
  const actors = [...all].filter((e) => e.json.kind === "actor");
  const byId = new Map(actors.map((a) => [a.id, a]));
  const name = String(json.name ?? entry.id);
  const nameOf = (id: string) => String(byId.get(id)?.json.name ?? id);
  const out: Diagnostic[] = [];
  const diag = (rule: string, message: string, pointer: string): Diagnostic => ({
    rule,
    severity: "error",
    message,
    elementId: entry.id,
    filePath: entry.path,
    jsonPointer: pointer,
    line: null,
    column: null,
  });

  if (json.type === "system") {
    const others = actors
      .filter((a) => a.json.type === "system" && a.id !== entry.id)
      .map((a) => `'${String(a.json.name ?? a.id)}'`)
      .sort();
    if (others.length)
      out.push(
        diag(
          "MQ9605",
          `Actor '${name}' is of type system, as ${others.length === 1 ? "is" : "are"} ${others.join(", ")}; a project has one system actor, the system itself (shown as Automatic). Change the type of all but one, for example to external-system.`,
          "/type",
        ),
      );
  }

  const membership = new Membership(actors);
  const seen = new Set<string>();
  let cycle: string | null = null;
  const entries = Array.isArray(json.memberOf) ? (json.memberOf as unknown[]) : [];
  entries.forEach((target, i) => {
    if (typeof target !== "string") return;
    const pointer = `/memberOf/${i}`;
    if (seen.has(target)) {
      out.push(diag("MQ9601", `Actor '${name}' lists '${nameOf(target)}' twice in memberOf; remove the repeated entry.`, pointer));
      return;
    }
    seen.add(target);
    if (target === entry.id) {
      out.push(diag("MQ9601", `Actor '${name}' names itself in memberOf; an actor already stands for itself. Remove the entry.`, pointer));
      return;
    }
    const holder = byId.get(target);
    if (!holder) return; // MQ2001, MQ2002
    const type = String(holder.json.type);
    if (type !== "role" && type !== "group" && type !== "organization") {
      out.push(
        diag(
          "MQ9601",
          `Actor '${name}' is a member of '${nameOf(target)}', which is ${TYPE_TEXT[type] ?? type}; only a role, a group or an organization has members. Remove the entry, or name a role, group or organization.`,
          pointer,
        ),
      );
      return;
    }
    if (cycle === null && membership.allMemberOf(target).has(entry.id)) {
      cycle = cyclePath(membership, entry.id, target, nameOf);
      if (cycle)
        out.push(diag("MQ9602", `Memberships form a cycle: ${cycle}; an actor cannot be a member of itself. Remove one of these memberships.`, pointer));
    }
  });
  return out;
}

/** The shortest way back from `target` to the actor along counted memberships, in file order: "A -> B -> A" by name. */
function cyclePath(membership: Membership, actor: string, target: string, nameOf: (id: string) => string): string | null {
  const previous = new Map<string, string | null>([[target, null]]);
  const queue = [target];
  while (queue.length) {
    const current = queue.shift()!;
    for (const next of membership.memberOf(current)) {
      if (next === actor) {
        const back: string[] = [];
        for (let step: string | null = current; step !== null; step = previous.get(step) ?? null) back.push(step);
        return [actor, ...back.reverse(), actor].map(nameOf).join(" -> ");
      }
      if (!previous.has(next)) {
        previous.set(next, current);
        queue.push(next);
      }
    }
  }
  return null;
}
