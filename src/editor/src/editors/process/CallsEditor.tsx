// The Calls dialog of an action (processes-operations-design.md section 12), built on the Sets dialog's pattern: an Operation picker
// (operations on the process's subjects first, and None), then the Inputs grid of that operation's one document with Input, Type,
// Required and Value. Apply writes the call as one change; the host keeps the dialog open when the change is refused.
import { useState } from "react";
import { Info } from "lucide-react";
import { useElement, useElements, useIndex } from "@/api/queries";
import { inputPreviewRequest } from "@/api/expressionPreview";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Select } from "@/components/ui/input";
import { newId } from "@/lib/ids";
import { callFromRows, CALL_LABELS as L, givenSources, inputRows, isMissing, memberGivable, type GivenLookup, type InputRow } from "@/model/callInputs";
import { indexLookup } from "@/model/index";
import { operationsForSubjects } from "@/model/operationLinks";
import { subjectsOf, type CallDoc, type ProcessDoc } from "@/model/process";
import { ValueBox } from "../ValueBox";
import type { SetContext } from "./SetsEditor";

type Rec = Record<string, unknown>;

export function CallsDialog({
  process,
  action,
  context,
  onApply,
  onClose,
}: {
  process: ProcessDoc;
  action: string;
  context: SetContext;
  onApply: (call: CallDoc | null) => void;
  onClose: () => void;
}) {
  const doc = process.actions?.find((a) => a.id === action);
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const groups = operationsForSubjects(
    lookup.ofKind("operation"),
    subjectsOf(process).flatMap((s) => (s.entity ? [s.entity] : [])),
  );
  const [operation, setOperation] = useState(doc?.call?.operation ?? "");
  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent title={L.title(doc?.name ?? "")} wide>
        <div className="flex flex-col gap-2" data-testid="calls-dialog">
          <p className="flex items-start gap-1.5 rounded-control bg-app px-2 py-1 text-12 text-secondary" data-testid="calls-runs-nothing">
            <Info className="mt-0.5 size-3.5 shrink-0" aria-hidden="true" />
            <span>{L.runsNothing}</span>
          </p>
          <label className="flex items-center gap-2 text-11 text-secondary">
            <span className="w-24 shrink-0">{L.operation}</span>
            <Select
              aria-label={L.operation}
              className="h-6 flex-1"
              value={operation}
              onChange={(e) => setOperation(e.target.value)}
              data-testid="call-operation"
            >
              <option value="">{L.noOperation}</option>
              {operation && !lookup.byId.has(operation) ? <option value={operation}>?</option> : null}
              {groups.on.length ? (
                <optgroup label={L.noOperationsOnSubjects}>
                  {groups.on.map((o) => (
                    <option key={o.id} value={o.id}>
                      {o.name}
                    </option>
                  ))}
                </optgroup>
              ) : null}
              <optgroup label={groups.on.length ? L.otherOperations : L.operation}>
                {groups.other.map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.name}
                  </option>
                ))}
              </optgroup>
            </Select>
          </label>
          {/* Keyed by the operation: a new pick starts a new Inputs grid, since another operation has other members. */}
          <InputsGrid
            key={operation}
            action={action}
            operation={operation}
            stored={doc?.call?.operation === operation ? doc.call : undefined}
            context={context}
            onApply={onApply}
            onClose={onClose}
          />
        </div>
      </DialogContent>
    </Dialog>
  );
}

function InputsGrid({
  action,
  operation,
  stored,
  context,
  onApply,
  onClose,
}: {
  action: string;
  operation: string;
  stored: CallDoc | undefined;
  context: SetContext;
  onApply: (call: CallDoc | null) => void;
  onClose: () => void;
}) {
  const index = useIndex();
  const lookup = indexLookup(index.data);
  const op = useElement(operation || null);
  const members = ((op.data?.json as Rec | undefined)?.input as Rec[] | undefined) ?? [];
  const given: GivenLookup = {
    kindOf: (id) => lookup.byId.get(id)?.kind,
    baseOf: (id) => lookup.byId.get(id)?.base,
    doc: (id) => sources.get(id)?.json as Rec | undefined,
  };
  // Only the entities the keyed members point at (with their bases) and the scalar types the members use are read, never the model.
  const sources = useElements(givenSources(members, given)).byId;
  const [edits, setEdits] = useState<Record<string, string>>({});
  const loaded = !operation || !!op.data;
  const rows: InputRow[] = inputRows(members, stored, context.names, lookup.nameOf, (m) => memberGivable(m, given)).map((r) =>
    r.member in edits ? { ...r, text: edits[r.member] } : r,
  );
  const apply = () => onApply(callFromRows(operation || undefined, rows, newId, stored));
  return (
    <form
      className="flex flex-col gap-2"
      data-testid="call-inputs"
      onSubmit={(e) => {
        e.preventDefault();
        apply();
      }}
    >
      {operation ? (
        !loaded ? (
          <p className="text-12 text-secondary">{L.loading}</p>
        ) : !rows.length ? (
          <p className="text-12 text-secondary">{L.noInputs}</p>
        ) : (
          <>
            <div className="flex max-w-3xl items-center gap-1 text-11 text-secondary">
              <span className="w-36 shrink-0">{L.input}</span>
              <span className="w-28 shrink-0">{L.type}</span>
              <span className="w-14 shrink-0">{L.required}</span>
              <span className="flex-1">{L.value}</span>
            </div>
            {rows.map((r) => (
              <div key={r.member} className="flex max-w-3xl items-start gap-1" data-testid="call-input-row" data-member={r.name}>
                <span className="flex h-6 w-36 shrink-0 items-center truncate font-mono text-12" title={r.name}>
                  {r.name}
                </span>
                <span className="flex h-6 w-28 shrink-0 items-center truncate text-12 text-secondary" title={r.type}>
                  {r.type}
                </span>
                <span className="flex h-6 w-14 shrink-0 items-center text-12 text-secondary">{r.required ? "Yes" : "No"}</span>
                {r.supplied ? (
                  <span className="flex h-6 flex-1 items-center text-12 text-secondary" data-testid="call-input-supplied">
                    {L.suppliedBySystem}
                  </span>
                ) : (
                  <div className="flex min-w-0 flex-1 flex-col gap-0.5">
                    <ValueBox
                      id={`call-value-${r.member}`}
                      label={`${L.value} of ${r.name}`}
                      initial={r.initial}
                      placeholder={L.notGiven}
                      required={r.required}
                      onChange={(text) => setEdits((d) => ({ ...d, [r.member]: text }))}
                      request={(text) =>
                        text === undefined
                          ? r.inputId
                            ? inputPreviewRequest({ action, input: r.inputId, member: r.member })
                            : null
                          : text.trim()
                            ? inputPreviewRequest({ action, input: r.inputId ?? "", member: r.member, text })
                            : null
                      }
                    />
                    {isMissing(r) ? (
                      <span className="text-11 text-warning" data-testid="call-input-missing">
                        {L.missing}
                      </span>
                    ) : null}
                  </div>
                )}
              </div>
            ))}
          </>
        )
      ) : null}
      <div className="flex items-center justify-end gap-2">
        <Button type="button" onClick={onClose}>
          Cancel
        </Button>
        <Button type="submit" variant="primary" disabled={!!operation && !loaded} data-testid="calls-apply">
          Apply
        </Button>
      </div>
    </form>
  );
}
