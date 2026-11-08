// The mock's approval rules (signoff-design.md section 6, part 1): MQ8201 an approval's shape (a gate with stages that keeps the
// counting keys the signatures carry, or allowSignerAcrossSignatures without stages), MQ8202 a named signature's key that does not fit
// (a notBy transition that is not the lifecycle's, a notBy transition in a gate policy, a notBy verb the lifecycle's runs cannot see,
// a when on a gate policy's signature), and the MQ3001 cases (two stages of one gate, or two signatures of one gate, with one name).
// The rules that read a `when` need the expression engine, which the mock lacks, so only its place is checked.
import type { Diagnostic } from "@/api/types";
import { notByVerbKeys } from "@/model/approvals";
import type { ProcessDoc } from "@/model/process";
import { builtInVerbs } from "@/model/verbs";

type Json = Record<string, unknown>;
type Emit = (rule: string, severity: Diagnostic["severity"], message: string, pointer: string, elementId?: string) => void;

const arr = (v: unknown): Json[] => (Array.isArray(v) ? (v as Json[]) : []);
const str = (v: unknown): string | null => (typeof v === "string" && v !== "" ? v : null);

/** The keys a gate with stages leaves to its signatures. */
const COUNTING_KEYS = ["required", "signers", "requiredActors", "allowRepeatSigner"] as const;

const BUILT_IN = new Map(builtInVerbs.map((v) => [v.key, v.group]));

export function approvalDiagnostics(json: Json, emit: Emit): void {
  if (json.kind !== "process") return;
  const library = json.use === "library";
  const transitions = new Set(arr(json.transitions).map((t) => String(t.id)));
  // The verbs a lifecycle's runs see: move verbs, signature verbs, complete and the verbs its events name. A library has no events,
  // so a verb that is not built in is left to the lifecycles that use it.
  const seen = new Set(notByVerbKeys({ events: arr(json.events) as unknown as ProcessDoc["events"] }));
  const checkGate = (gate: Json, at: string) => {
    const gateName = String(gate.name ?? "");
    const stages = arr(gate.stages);
    const holder = str(gate.id) ?? undefined;
    if (stages.length) {
      for (const key of COUNTING_KEYS)
        if (gate[key] !== undefined)
          emit(
            "MQ8201",
            "error",
            `The gate '${gateName}' has stages, so its named signatures carry '${key}'; the gate has none. Remove the key.`,
            `${at}/${key}`,
            holder,
          );
    } else if (gate.allowSignerAcrossSignatures !== undefined)
      emit(
        "MQ8201",
        "error",
        `The gate '${gateName}' has no stages, so allowSignerAcrossSignatures has no signatures to apply across. Remove the key.`,
        `${at}/allowSignerAcrossSignatures`,
        holder,
      );
    const stageNames = new Map<string, number>();
    const signatureNames = new Map<string, string>();
    stages.forEach((stage, s) => {
      const sAt = `${at}/stages/${s}`;
      const stageName = String(stage.name ?? "");
      if (stageName) {
        if (stageNames.has(stageName))
          emit(
            "MQ3001",
            "error",
            `Stage name '${stageName}' is used twice in the gate '${gateName}' (also by stage ${stageNames.get(stageName)}).`,
            `${sAt}/name`,
            str(stage.id) ?? holder,
          );
        else stageNames.set(stageName, s + 1);
      }
      arr(stage.signatures).forEach((sig, j) => {
        const at2 = `${sAt}/signatures/${j}`;
        const id = str(sig.id) ?? holder;
        const key = String(sig.name ?? "");
        if (key) {
          if (signatureNames.has(key))
            emit(
              "MQ3001",
              "error",
              `Signature name '${key}' is used twice in the gate '${gateName}' (also by signature ${signatureNames.get(key)}).`,
              `${at2}/name`,
              id,
            );
          else signatureNames.set(key, `${s + 1}.${j + 1}`);
        }
        const label = `The signature '${key}' of the gate '${gateName}'`;
        if (library && sig.when !== undefined)
          emit("MQ8202", "error", `${label} is in a gate policy, which has no subject for a when to read. Remove the key.`, `${at2}/when`, id);
        arr(sig.notBy).forEach((n, k) => {
          const nAt = `${at2}/notBy/${k}`;
          const transition = str(n.transition);
          const verb = str(n.verb);
          if (transition !== null) {
            if (library)
              emit("MQ8202", "error", `${label} names a transition in notBy, but a gate policy belongs to no single lifecycle. Remove the entry.`, nAt, id);
            else if (!transitions.has(transition))
              emit(
                "MQ8202",
                "error",
                `${label} names a transition in notBy that is not a transition of '${String(json.name ?? "")}'. Remove the entry.`,
                nAt,
                id,
              );
          }
          if (verb !== null) {
            const group = BUILT_IN.get(verb);
            const visible = group !== undefined ? seen.has(verb) : library || seen.has(verb);
            // An own verb no event names, or a built-in verb of another group, is never done in a run the lifecycle sees.
            if (!visible)
              emit(
                "MQ8202",
                "error",
                `${label} names the verb '${verb}' in notBy, which the runs of '${String(json.name ?? "")}' cannot see: only move verbs, signature verbs, complete and verbs its events name. Remove the entry.`,
                nAt,
                id,
              );
          }
        });
      });
    });
  };
  arr(json.transitions).forEach((t, i) => {
    if (t.gate) checkGate(t.gate as Json, `/transitions/${i}/gate`);
  });
  arr(json.gates).forEach((g, i) => checkGate(g, `/gates/${i}`));
}
