// The mock's actor rules (ProcessRules.cs CheckActor, actors-design.md section 5): MQ9601 a memberOf entry that names an
// actor that holds no members (or is an actor from the object), the actor itself, or the same actor twice; MQ9602 a membership
// cycle; MQ9603 a malformed relative; MQ9605 a second system actor.
import type { Diagnostic } from "@/api/types";
import { Membership } from "./membership";
import { relativeForms, type RelativeDoc } from "@/model/objectFields";

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
          `Actor '${name}' is the Automatic actor, as ${others.length === 1 ? "is" : "are"} ${others.join(", ")}; a project has at most one. Change the type of all but one, for example to external-system.`,
          "/type",
        ),
      );
  }

  out.push(...relativeDiagnostics(json, name, byId, diag));

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
    if (holder.json.relative !== undefined) {
      out.push(
        diag(
          "MQ9601",
          `Actor '${name}' is a member of '${nameOf(target)}', an actor taken from the object; it is worked out per object, so it has no members. Remove the entry.`,
          pointer,
        ),
      );
      return;
    }
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

/** MQ9603: a relative with no form or several, within without membersOf, membersOf naming other than a plain role, group or organization, memberOf on it, or type system. */
function relativeDiagnostics(
  json: Json,
  name: string,
  byId: Map<string, Entry>,
  diag: (rule: string, message: string, pointer: string) => Diagnostic,
): Diagnostic[] {
  if (json.relative === undefined || json.relative === null || typeof json.relative !== "object") return [];
  const relative = json.relative as RelativeDoc;
  const out: Diagnostic[] = [];
  const bad = (message: string, pointer: string) => out.push(diag("MQ9603", message, pointer));
  const forms = relativeForms(relative);
  if (forms.length === 0 && relative.within === undefined)
    bad(`Actor '${name}' is taken from the object but gives no form; give exactly one of owner, path or membersOf.`, "/relative");
  else if (forms.length > 1)
    bad(`Actor '${name}' gives ${forms.length} forms (${forms.join(", ")}); give exactly one of owner, path or membersOf.`, "/relative");
  if (relative.within !== undefined && relative.membersOf === undefined)
    bad(
      `Actor '${name}' gives within without membersOf; within says where a role is held, so name the role with membersOf, or remove within.`,
      "/relative/within",
    );
  if (typeof relative.membersOf === "string") {
    const holder = byId.get(relative.membersOf);
    if (holder) {
      const type = String(holder.json.type);
      const holds = type === "role" || type === "group" || type === "organization";
      if (!holds || holder.json.relative !== undefined)
        bad(
          `Actor '${name}' names '${String(holder.json.name ?? holder.id)}' in membersOf, which is ${holds ? "itself taken from the object" : (TYPE_TEXT[type] ?? type)}; name a plain role, group or organization.`,
          "/relative/membersOf",
        );
    }
  }
  if (Array.isArray(json.memberOf) && json.memberOf.length > 0)
    bad(`Actor '${name}' is taken from the object, so it cannot be a member of anything; remove memberOf.`, "/memberOf");
  if (json.type === "system")
    bad(`Actor '${name}' is the system actor, which is not worked out from the object; remove relative, or change the type.`, "/type");
  return out;
}
