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

/**
 * MQ2002 on a scenario step's assume keys, typed by their value (trigger-points-design.md decision 26): a boolean names a guard, a word
 * names a trigger point. The engine words an entry of a lifecycle's trigger points as "a trigger point". Only the two kinds an assume can
 * mix up are told apart; any other id is MQ2001's.
 */
export function assumeKindDiagnostics(json: Json, all: () => Iterable<ModelEntry>, emit: Emit): void {
  if (json.kind !== "scenario") return;
  const guards = new Set<string>();
  const checks = new Set<string>();
  for (const e of all()) {
    if (e.json.kind !== "process") continue;
    arr(e.json.guards).forEach((g) => str(g.id) && guards.add(g.id as string));
    arr(e.json.triggerPoints).forEach((t) => str(t.id) && checks.add(t.id as string));
  }
  arr(json.steps).forEach((step, i) => {
    const assume = step.assume;
    if (!assume || typeof assume !== "object") return;
    for (const [key, value] of Object.entries(assume as Json)) {
      const word = typeof value === "string";
      const at = `/steps/${i}/assume/${key}`;
      if (word && guards.has(key)) emit("MQ2002", "error", `'assume' references '${key}', a guard; expected a trigger point.`, at);
      else if (!word && checks.has(key)) emit("MQ2002", "error", `'assume' references '${key}', a trigger point; expected a guard.`, at);
    }
  });
}

export function triggerDiagnostics(json: Json, all: () => Iterable<ModelEntry>, emit: Emit): void {
  if (json.kind !== "process") return;
  const entries = [...all()];
  const byId = new Map(entries.map((e) => [e.id, e.json]));
  const nameOf = (id: string) => String(byId.get(id)?.name ?? id);
  const name = String(json.name ?? "");
  const points = arr(json.triggerPoints);
  if (!points.length) return;
  if (json.use !== "lifecycle") {
    // One finding for each point, as the engine reports (an empty list is no finding).
    points.forEach((t, i) =>
      emit(
        "MQ8101",
        "error",
        `Trigger point '${String(t.name ?? "")}' is on ${json.use === "library" ? "a library" : "an orchestration"}; only lifecycles hold trigger points. Remove it.`,
        `/triggerPoints/${i}`,
        str(t.id) ?? undefined,
      ),
    );
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
  // The guards of the process as it runs: its own, and the library guards its transitions name (the engine's effective process).
  const usedGuards = new Set(arr(json.transitions).flatMap((t) => (str(t.guard) ? [t.guard as string] : [])));
  const guardNames = new Set([
    ...arr(json.guards).flatMap((g) => (str(g.name) ? [String(g.name)] : [])),
    ...entries.flatMap((e) =>
      e.json.kind === "process" && e.json.use === "library"
        ? arr(e.json.guards).flatMap((g) => (usedGuards.has(String(g.id)) && str(g.name) ? [String(g.name)] : []))
        : [],
    ),
  ]);
  const taken = new Map<string, number>();
  // The entities each lifecycle governs, and which of them a point runs for: its entities and what derives from them (decision 12).
  const entities = entries.filter((e) => e.json.kind === "entity");
  const governedBy = (lifecycle: Json) => entities.filter((e) => coverage.governs(lifecycle, e.id)).map((e) => e.id);
  const chainIds = (e: string) => entityChain(e, (id) => byId.get(id)).map((d) => String(d.id));
  const coversOf = (t: Json, governed: string[]) => {
    const named = strings(t.entities);
    return named.length ? governed.filter((e) => chainIds(e).some((x) => named.includes(x))) : governed;
  };
  const mineGoverned = governedBy(json);
  const replacedDelete = new Set(
    entries.flatMap((e) =>
      e.json.kind === "process" && e.json.use === "lifecycle"
        ? arr(e.json.id === json.id ? points : e.json.triggerPoints)
            .filter((t) => t.type === "replace" && strings(t.verbs).includes("delete"))
            .flatMap((t) => coversOf(t, e.json.id === json.id ? mineGoverned : governedBy(e.json)))
        : [],
    ),
  );
  points.forEach((t, i) => {
    const at = `/triggerPoints/${i}`;
    const id = str(t.id) ?? undefined;
    const label = `The trigger point '${String(t.name ?? "")}'`;
    const type = t.type;
    const verbs = strings(t.verbs);
    // MQ3001: names are unique among the lifecycle's trigger points and apart from its guards.
    // Names compare exactly, and one point gets one finding (the engine's CheckNames).
    const key = String(t.name ?? "");
    if (key) {
      if (taken.has(key))
        emit(
          "MQ3001",
          "error",
          `Trigger point name '${String(t.name)}' is used twice in '${name}' (also by trigger point ${taken.get(key)}).`,
          `${at}/name`,
          id,
        );
      else {
        taken.set(key, i + 1);
        if (guardNames.has(key))
          emit(
            "MQ3001",
            "error",
            `Trigger point name '${String(t.name)}' is also the name of a guard of '${name}', so a step's assume could not tell them apart.`,
            `${at}/name`,
            id,
          );
      }
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
      // Some replace on delete, of this or another lifecycle, must cover every entity this point covers (derived entities included).
      if (coversOf(t, mineGoverned).some((e) => !replacedDelete.has(e)))
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
      verbs.forEach((v, k) => {
        if (!REPLACE_VERBS.has(v))
          emit(
            "MQ8103",
            "error",
            `${label} replaces ${v}, whose effect cannot be replaced; only delete, disconnect, copy and revise allow a replace. Make it an action.`,
            `${at}/verbs/${k}`,
            id,
          );
      });
    // MQ8105 and MQ8106: the moment against what the point does.
    if (type === "action" && t.timing === "after-commit" && arr(t.sets).length)
      emit(
        "MQ8105",
        "error",
        `${label} runs after commit, but sets values: once the change is saved, setting the object is a new change. Make it a with-change action.`,
        `${at}/sets`,
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
    if (verbs.length && neverRuns(t, verbs, json))
      emit(
        "MQ8107",
        "warning",
        `${label} can never run: no event or transition of its lifecycle does its verbs, or no input can do them yet. Remove it.`,
        `${at}/verbs`,
        id,
      );
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
        `/triggerPoints/${i}`,
        str(t.id) ?? undefined,
      );
  });
}

/**
 * Whether a trigger point can never run: every one of its verbs fails. A verb runs when an event of the lifecycle does it (through a
 * transition it names, if it names any); a move verb also when a transition does it; any other verb unless no input can do it yet.
 * This is the engine's CanRun, so `sign` on a point narrowed by transitions is the designed signature place and never dead.
 */
function neverRuns(t: Json, verbs: string[], json: Json): boolean {
  const named = strings(t.transitions);
  const transitions = arr(json.transitions);
  const canRun = (verb: string): boolean => {
    const byEvent = arr(json.events).some(
      (e) => e.verb === verb && (!named.length || transitions.some((tr) => tr.event === e.id && named.includes(String(tr.id)))),
    );
    if (byEvent) return true;
    if (isMoveVerb(verb)) return transitions.some((tr) => tr.verb === verb && (!named.length || named.includes(String(tr.id))));
    return !NO_INPUT.has(verb);
  };
  return !verbs.some(canRun);
}
