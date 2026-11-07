// The mock's trigger point rules (trigger-points-design.md section 6, the texts of RuleCatalog.cs): MQ8101 a trigger point's shape;
// MQ8102 a narrowing key that does not fit; MQ8103 a replace on a verb that does not allow it; MQ8104 two replaces that can apply
// to one verb on one entity; MQ8105 an after-commit action with sets; MQ8106 a with-change action that reaches outside systems;
// MQ8107 a trigger point that can never run. MQ3001 gains two cases: two trigger points of one lifecycle with one name, and a trigger
// point named like a guard of its process. The rules that read an expression need the expression engine, which the mock lacks, so a
// `when`, a condition or a value is only checked for the ids it names (refs.ts).
import type { Diagnostic } from "@/api/types";
import { builtInVerb } from "@/model/verbList";
import { commonFields, entityChain, objectFields } from "@/model/objectFields";
import { subjectsOf } from "@/model/process";
import { LINK_VERBS } from "@/model/rights";
import { allowsReplace, isMoveVerb, REPLACE_VERBS } from "@/model/triggerPoints";
import { Coverage } from "./coverage";
import { fieldSourcesOf } from "./relativeRules";
import type { ModelEntry } from "./validate";

type Json = Record<string, unknown>;
type Emit = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string, elementId?: string) => void;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const strings = (v: unknown): string[] => (Array.isArray(v) ? v.filter((x): x is string => typeof x === "string") : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);

/** The verbs no input can do yet (MQ8107), unless an event names one. */
const NO_INPUT: ReadonlySet<string> = new Set(["override", "set-state", "claim", "reject", "ignore", "withdraw", "delegate", "route"]);

/** Every state id of a lifecycle, of any type. */
function allStates(json: Json): Set<string> {
  const ids = new Set<string>();
  const walk = (list: unknown) =>
    arr(list).forEach((s) => {
      if (str(s.id)) ids.add(s.id as string);
      walk(s.states);
    });
  walk(json.states);
  return ids;
}

/** Whether a point has team code for a body: a check with no condition, an action or a replace with no sets. */
const teamCode = (t: Json): boolean => (t.type === "check" ? t.condition === undefined : arr(t.sets).length === 0);

/** The entities a point covers: those it names, else the lifecycle's entity subjects. A stereotype entry names none, so nothing is known. */
function covered(point: Json, process: Json): string[] {
  const own = strings(point.entities);
  return own.length ? own : subjectsOf(process).flatMap((s) => s.entity ?? []);
}

export function triggerDiagnostics(json: Json, all: () => Iterable<ModelEntry>, emit: Emit): void {
  if (json.kind !== "process") return;
  const entries = [...all()];
  const byId = new Map(entries.map((e) => [e.id, e.json]));
  const nameOf = (id: string) => String(byId.get(id)?.name ?? id);
  const name = String(json.name ?? "");
  const points = arr(json.triggerPoints);
  if (json.triggerPoints === undefined) return;
  if (json.use !== "lifecycle") {
    emit("MQ8101", "error", `'${name}' is not a lifecycle, so it holds no trigger points; only lifecycles do. Remove the key.`, "/triggerPoints");
    return;
  }
  const states = allStates(json);
  const transitions = new Set(arr(json.transitions).map((t) => String(t.id)));
  const events = arr(json.events);
  const subjects = subjectsOf(json);
  const coverage = new Coverage(entries);
  const src = fieldSourcesOf(entries);
  // The fields every entity of a list holds under one id (decision 12).
  const held = (ids: string[], kind: "attribute" | "end"): Set<string> =>
    new Set(ids.length ? commonFields(ids.map((e) => objectFields(e, src).filter((f) => f.kind === kind))).map((f) => f.id) : []);
  const eventVerbs = new Set(events.flatMap((e) => (str(e.verb) ? [e.verb as string] : [])));
  const moveVerbs = new Set([...eventVerbs, ...arr(json.transitions).flatMap((t) => (str(t.verb) ? [t.verb as string] : []))]);
  const guardNames = new Set(arr(json.guards).flatMap((g) => (str(g.name) ? [String(g.name).toLowerCase()] : [])));
  const taken = new Map<string, number>();
  const replacesDelete = points.filter((t) => t.type === "replace" && strings(t.verbs).includes("delete"));
  points.forEach((t, i) => {
    const at = `/triggerPoints/${i}`;
    const id = str(t.id) ?? undefined;
    const label = `The trigger point '${String(t.name ?? "")}'`;
    const type = t.type;
    const verbs = strings(t.verbs);
    // MQ3001: names are unique among the lifecycle's trigger points and apart from its guards.
    const key = String(t.name ?? "").toLowerCase();
    if (key) {
      if (taken.has(key))
        emit(
          "MQ3001",
          "error",
          `Trigger point name '${String(t.name)}' is used twice in '${name}' (also by trigger point ${taken.get(key)}).`,
          `${at}/name`,
          id,
        );
      else taken.set(key, i + 1);
      if (guardNames.has(key))
        emit(
          "MQ3001",
          "error",
          `Trigger point name '${String(t.name)}' is also the name of a guard of '${name}', so a step's assume could not tell them apart.`,
          `${at}/name`,
          id,
        );
    }
    // MQ8101: the shape.
    if (type === "action" && t.timing === undefined)
      emit("MQ8101", "error", `${label} is an action, so it needs a timing: before, with-change or after-commit.`, at, id);
    if (type !== "action" && t.timing !== undefined)
      emit("MQ8101", "error", `${label} is not an action, so it has no timing. Remove the key.`, `${at}/timing`, id);
    if (type !== "check" && t.severity !== undefined)
      emit("MQ8101", "error", `${label} is not a check, so it has no severity. Remove the key.`, `${at}/severity`, id);
    if (type !== "check" && t.condition !== undefined)
      emit("MQ8101", "error", `${label} is not a check, so it has no condition. Remove the key.`, `${at}/condition`, id);
    if (type === "check" && arr(t.sets).length) emit("MQ8101", "error", `${label} is a check, so it sets nothing. Remove the key.`, `${at}/sets`, id);
    if (t.outside === true && !teamCode(t))
      emit(
        "MQ8101",
        "error",
        `${label} says outside, but its body is not the team's code, so it reaches no outside system. Remove the key.`,
        `${at}/outside`,
        id,
      );
    if (type === "action" && t.timing === "with-change" && arr(t.sets).length && verbs.includes("delete")) {
      const mine = strings(t.entities);
      // A replace on delete covers it when it covers every entity this point covers: one with no entities covers all.
      const covers = replacesDelete.some((r) => {
        const theirs = strings(r.entities);
        return theirs.length === 0 || (mine.length > 0 && mine.every((e) => theirs.includes(e)));
      });
      if (!covers)
        emit(
          "MQ8101",
          "error",
          `${label} sets values with the change on delete, but no replace on delete covers it, and a deleted object holds none. Remove the sets.`,
          `${at}/sets`,
          id,
        );
    }
    // MQ8102: narrowing keys that do not fit.
    strings(t.states).forEach((s, j) => {
      if (!states.has(s))
        emit("MQ8102", "error", `${label} names '${nameOf(s)}' as a state, which is not a state of '${name}'. Remove it.`, `${at}/states/${j}`, id);
    });
    strings(t.targets).forEach((s, j) => {
      if (!states.has(s))
        emit("MQ8102", "error", `${label} names '${nameOf(s)}' as a target, which is not a state of '${name}'. Remove it.`, `${at}/targets/${j}`, id);
    });
    strings(t.transitions).forEach((s, j) => {
      if (!transitions.has(s))
        emit("MQ8102", "error", `${label} names a transition that is not a transition of '${name}'. Remove it.`, `${at}/transitions/${j}`, id);
    });
    const moves = verbs.every((v) => isMoveVerb(v) || v === "sign" || (!builtInVerb(v) && eventVerbs.has(v)));
    for (const k of ["targets", "transitions"] as const)
      if (strings(t[k]).length && !moves)
        emit(
          "MQ8102",
          "error",
          `${label} names ${k}, but it has a verb that is not a move verb, sign or an own verb an event of '${name}' names. Remove the ${k}.`,
          `${at}/${k}`,
          id,
        );
    const attributes = strings(t.attributes);
    const ends = strings(t.ends);
    if (attributes.length && verbs.some((v) => v !== "read" && v !== "modify"))
      emit("MQ8102", "error", `${label} names attributes, but it has a verb other than read and modify. Remove the attributes.`, `${at}/attributes`, id);
    if (attributes.length && ends.length)
      emit(
        "MQ8102",
        "error",
        `${label} names attributes beside ends: with ends it speaks of links, which hold none of the subject's attributes. Remove one.`,
        `${at}/attributes`,
        id,
      );
    if (ends.length && verbs.some((v) => !LINK_VERBS.has(v)))
      emit("MQ8102", "error", `${label} names ends, but it has a verb that cannot act on a link. Remove the ends.`, `${at}/ends`, id);
    strings(t.entities).forEach((e, j) => {
      if (!coverage.governs(json, e))
        emit("MQ8102", "error", `${label} names the entity '${nameOf(e)}', which '${name}' does not govern. Remove it.`, `${at}/entities/${j}`, id);
    });
    const covers = covered(t, json);
    const wholeKnown = covers.length > 0 && (strings(t.entities).length > 0 || subjects.every((s) => s.entity !== undefined));
    if (wholeKnown) {
      const have = held(covers, "attribute");
      const haveEnds = held(covers, "end");
      attributes.forEach((a, j) => {
        if (!have.has(a))
          emit(
            "MQ8102",
            "error",
            `${label} names the attribute '${nameOf(a)}', which not every entity it covers holds. Remove it.`,
            `${at}/attributes/${j}`,
            id,
          );
      });
      ends.forEach((e, j) => {
        if (!haveEnds.has(e))
          emit("MQ8102", "error", `${label} names the end '${nameOf(e)}', which not every entity it covers holds. Remove it.`, `${at}/ends/${j}`, id);
      });
      arr(t.sets).forEach((s, j) => {
        const a = str(s.attribute);
        if (a && !have.has(a))
          emit(
            "MQ8102",
            "error",
            `${label} sets the attribute '${nameOf(a)}', which not every entity it covers holds. Remove the set.`,
            `${at}/sets/${j}/attribute`,
            id,
          );
      });
    }
    // MQ8103: a replace on a verb that does not allow it.
    if (type === "replace" && !allowsReplace(verbs))
      emit(
        "MQ8103",
        "error",
        `${label} replaces ${verbs.filter((v) => !REPLACE_VERBS.has(v)).join(", ")}, but only delete, disconnect, copy and revise allow a replace. Make it an action.`,
        `${at}/type`,
        id,
      );
    // MQ8105 and MQ8106: the moment against what the point does.
    if (type === "action" && t.timing === "after-commit" && arr(t.sets).length)
      emit(
        "MQ8105",
        "error",
        `${label} runs after commit, but sets values: once the change is saved, setting the object is a new change. Make it a with-change action.`,
        `${at}/timing`,
        id,
      );
    if (type === "action" && t.timing === "with-change" && t.outside === true)
      emit(
        "MQ8106",
        "warning",
        `${label} reaches outside systems with the change, and a rollback cannot undo the outside call. Make it after commit.`,
        `${at}/timing`,
        id,
      );
    // MQ8107: a trigger point that can never run.
    if (verbs.length && neverRuns(t, verbs, json, eventVerbs, moveVerbs))
      emit("MQ8107", "warning", `${label} can never run: ${deadReason(t, verbs)}. Remove it.`, at, id);
  });
  // MQ8104: two replaces that can apply to one verb on one entity, in this lifecycle or another that governs it.
  const mine = points.map((t, i) => ({ t, i })).filter(({ t }) => t.type === "replace");
  const chainOf = (e: string) => new Set(entityChain(e, (id) => byId.get(id)).map((d) => String(d.id)));
  const meets = (a: string[], b: string[]) => a.some((x) => b.some((y) => chainOf(x).has(y) || chainOf(y).has(x)));
  const others = entries.filter((e) => e.json.kind === "process" && e.id !== json.id && e.json.use === "lifecycle");
  const clash = (a: Json, b: Json, other: Json): boolean => {
    const shared = strings(a.verbs).filter((v) => strings(b.verbs).includes(v));
    return shared.length > 0 && meets(covered(a, json), covered(b, other));
  };
  mine.forEach(({ t, i }) => {
    const earlier = mine.find((m) => m.i < i && clash(t, m.t, json));
    const elsewhere = earlier
      ? undefined
      : others
          .filter((o) => String(o.path) < String(entries.find((x) => x.id === json.id)?.path ?? ""))
          .flatMap((o) =>
            arr(o.json.triggerPoints)
              .filter((r) => r.type === "replace" && clash(t, r, o.json))
              .map((r) => ({ r, o })),
          )[0];
    if (earlier || elsewhere)
      emit(
        "MQ8104",
        "error",
        `The trigger point '${String(t.name)}' replaces a verb on an entity that '${String(earlier ? earlier.t.name : elsewhere!.r.name)}'${elsewhere ? ` of '${String(elsewhere.o.json.name)}'` : ""} already replaces. Only one replace can apply to one verb on one entity. Narrow one with entities.`,
        `/triggerPoints/${i}/verbs`,
        str(t.id) ?? undefined,
      );
  });
}

/** Whether a trigger point can run: a verb that something can do, and a move among the transitions it names. */
function neverRuns(t: Json, verbs: string[], json: Json, eventVerbs: ReadonlySet<string>, moveVerbs: ReadonlySet<string>): boolean {
  const dead = (v: string) => (isMoveVerb(v) ? !moveVerbs.has(v) : NO_INPUT.has(v) ? !eventVerbs.has(v) : false);
  if (verbs.every(dead)) return true;
  const named = strings(t.transitions);
  if (!named.length) return false;
  const events = new Map(arr(json.events).map((e) => [String(e.id), e]));
  return !arr(json.transitions).some((tr) => {
    if (!named.includes(String(tr.id))) return false;
    const verb = str(tr.verb) ?? str(events.get(String(tr.event))?.verb);
    return verb !== null && verbs.includes(verb);
  });
}

function deadReason(t: Json, verbs: string[]): string {
  return strings(t.transitions).length
    ? `none of the moves it names has ${verbs.join(" or ")} as its verb`
    : `no event or transition of its lifecycle does ${verbs.join(" or ")}`;
}
