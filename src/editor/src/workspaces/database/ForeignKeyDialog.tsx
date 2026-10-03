// The foreign key dialog (fkEdits.ts holds the logic), one for the whole editor (`ForeignKeyDialogHost`, opened through the
// store by the diagram's drag and edge, the explorer's New foreign key, the Foreign keys tab and the inspector's Edit…): table
// A's referencing columns paired with the referenced table's columns (Add column pair for a composite key; Add column adds a
// column to A typed like the referenced one), the name (the convention's default until typed over), the on delete / on update
// rules and when the key is checked (Deferrable). Create or Save writes the key into A in one undo step; a table not stored as a
// table file yet is stored as one first, as the note says. The referenced columns must be the referenced table's primary key
// or one of its unique keys (MQ4059).
import { useEffect, useMemo, useState } from "react";
import { Plus, X } from "lucide-react";
import { useQueryClient } from "@tanstack/react-query";
import type { TableView } from "@/api/types";
import { useDatabaseView, useSettings } from "@/api/queries";
import { useServices } from "@/app/context";
import { useEditor } from "@/state/store";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Button } from "@/components/ui/button";
import { Field, Input, Select } from "@/components/ui/input";
import {
  deferrableNote,
  draftDefaultName,
  FK_ACTION_LABELS,
  followStoredDraft,
  FK_ACTIONS,
  FK_DEFERRABLE,
  FK_DEFERRABLE_LABELS,
  fkNamePattern,
  fkProblems,
  isLegacy,
  newColumnName,
  type FkAction,
  type FkDeferrable,
  type FkDraft,
} from "./fkEdits";
import { saveForeignKey } from "./foreignKeys";
import { storedFileOf, storedViewOf } from "./storeTables";

/** The dialog, mounted once by the app shell: the draft the store holds, saved through `saveForeignKey`. */
export function ForeignKeyDialogHost() {
  const services = useServices();
  const qc = useQueryClient();
  const request = useEditor(services.store, (s) => s.foreignKeyDialog);
  const view = useDatabaseView(request?.database ?? null);
  const settings = useSettings();
  const tables = useMemo(() => view.data?.view?.tables ?? [], [view.data]);
  if (!request) return null;
  const pattern = fkNamePattern(settings.data?.json, view.data?.view?.name ?? null);
  const dialect = view.data?.view?.dialect ?? "postgresql";
  const close = () => services.store.getState().requestForeignKeyDialog(null);
  return (
    <ForeignKeyDialog
      draft={request.draft}
      tables={tables}
      pattern={pattern}
      dialect={dialect}
      onClose={close}
      onSave={(draft) => saveForeignKey(services, qc, { database: request.database, draft, tables, pattern })}
    />
  );
}

const NEW = "\u0000new";

export function ForeignKeyDialog({
  draft: initial,
  tables,
  pattern,
  dialect = "postgresql",
  onClose,
  onSave,
}: {
  draft: FkDraft | null;
  tables: readonly TableView[];
  pattern: string;
  dialect?: string;
  onClose: () => void;
  onSave: (draft: FkDraft) => Promise<boolean>;
}) {
  const [draft, setDraft] = useState<FkDraft | null>(initial);
  // The name follows the columns until it is typed over.
  const [nameTyped, setNameTyped] = useState(false);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    setDraft(initial);
    setNameTyped(!!initial?.editing);
  }, [initial]);
  // A table of the draft stored as a table file while the dialog is open (another edit stored it): the draft follows it there.
  useEffect(() => {
    setDraft((current) => (current ? (followStoredDraft(current, tables, storedFileOf, storedViewOf) ?? current) : current));
  }, [tables]);
  const a = useMemo(() => tables.find((t) => t.key === draft?.table) ?? null, [tables, draft?.table]);
  const b = useMemo(() => tables.find((t) => t.key === draft?.referencedTable) ?? null, [tables, draft?.referencedTable]);
  if (!draft) return null;
  const problems = fkProblems(tables, draft, dialect);
  const note = deferrableNote(dialect);
  const change = (next: FkDraft) => setDraft(nameTyped ? next : { ...next, name: draftDefaultName(tables, next, pattern) });
  const setPair = (i: number, patch: Partial<FkDraft["pairs"][number]>) =>
    change({ ...draft, pairs: draft.pairs.map((p, j) => (j === i ? { ...p, ...patch } : p)) });
  const addColumn = (i: number) => {
    const ref = b?.columns.find((c) => c.key === draft.pairs[i].referenced) ?? null;
    const taken = [...(a?.columns.map((c) => c.name) ?? []), ...draft.pairs.map((p) => p.newColumn ?? "")];
    setPair(i, { column: null, newColumn: b && ref ? newColumnName(b, ref, taken) : "column_id" });
  };
  const save = async () => {
    if (problems.errors.length || busy) return;
    setBusy(true);
    try {
      if (await onSave(draft)) onClose();
    } finally {
      setBusy(false);
    }
  };
  const editing = !!draft.editing;

  return (
    <Dialog open onOpenChange={(open) => !open && onClose()}>
      <DialogContent
        title={editing ? `Edit foreign key ${draft.editing!.name}` : `New foreign key on ${a?.name ?? "the table"}`}
        className="w-[min(92vw,560px)]"
      >
        <form
          className="flex flex-col gap-2"
          data-testid="fk-dialog"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <div className="grid grid-cols-2 gap-2">
            <Field label="Table">
              <Input value={a ? `${a.schema ? `${a.schema}.` : ""}${a.name}` : ""} readOnly aria-label="Table" data-testid="fk-table" />
            </Field>
            <Field label="References table" htmlFor="fk-referenced-table">
              <Select
                id="fk-referenced-table"
                value={draft.referencedTable}
                onChange={(e) => {
                  const next = tables.find((t) => t.key === e.target.value);
                  const key = next?.primaryKey?.columns ?? [];
                  const pairs = (key.length ? key : [""]).map((referenced, i) => ({ column: draft.pairs[i]?.column ?? null, referenced }));
                  change({ ...draft, referencedTable: e.target.value, pairs });
                }}
              >
                <option value="">Choose a table</option>
                {tables.map((t) => (
                  <option key={t.key} value={t.key}>
                    {t.schema ? `${t.schema}.` : ""}
                    {t.name}
                  </option>
                ))}
              </Select>
            </Field>
          </div>
          <fieldset className="flex flex-col gap-1">
            <legend className="mb-1 text-12 font-medium text-secondary">Columns</legend>
            {draft.pairs.map((pair, i) => (
              <div key={i} className="flex items-center gap-1" data-testid="fk-pair">
                {pair.newColumn !== undefined ? (
                  <Input
                    className="min-w-0 flex-1"
                    aria-label={`New column ${i + 1}`}
                    value={pair.newColumn}
                    onChange={(e) => setPair(i, { newColumn: e.target.value })}
                    data-testid="fk-new-column"
                  />
                ) : (
                  <Select
                    className="min-w-0 flex-1"
                    aria-label={`Column ${i + 1}`}
                    value={pair.column ?? ""}
                    onChange={(e) => (e.target.value === NEW ? addColumn(i) : setPair(i, { column: e.target.value || null }))}
                    data-testid="fk-column"
                  >
                    <option value="">Choose a column</option>
                    {a?.columns.map((c) => (
                      <option key={c.key} value={c.key}>
                        {c.name} ({c.nativeType})
                      </option>
                    ))}
                    <option value={NEW}>New column…</option>
                  </Select>
                )}
                <span className="text-secondary" aria-hidden>
                  →
                </span>
                <Select
                  className="min-w-0 flex-1"
                  aria-label={`Referenced column ${i + 1}`}
                  value={pair.referenced}
                  onChange={(e) => setPair(i, { referenced: e.target.value })}
                  data-testid="fk-referenced-column"
                >
                  <option value="">Choose a column</option>
                  {b?.columns.map((c) => (
                    <option key={c.key} value={c.key}>
                      {c.name} ({c.nativeType})
                    </option>
                  ))}
                </Select>
                {pair.newColumn === undefined ? (
                  <Button
                    size="sm"
                    variant="ghost"
                    onClick={() => addColumn(i)}
                    title={`Add a column to ${a?.name ?? "the table"} typed like the referenced column`}
                    data-testid="fk-add-column"
                  >
                    Add column
                  </Button>
                ) : (
                  <Button size="sm" variant="ghost" onClick={() => setPair(i, { newColumn: undefined })} title="Pick an existing column instead">
                    Existing
                  </Button>
                )}
                <Button
                  size="icon-row"
                  variant="ghost"
                  label={`Remove column pair ${i + 1}`}
                  disabled={draft.pairs.length === 1}
                  onClick={() => change({ ...draft, pairs: draft.pairs.filter((_, j) => j !== i) })}
                >
                  <X />
                </Button>
              </div>
            ))}
            <div>
              <Button
                size="sm"
                variant="ghost"
                onClick={() => change({ ...draft, pairs: [...draft.pairs, { column: null, referenced: "" }] })}
                data-testid="fk-add-pair"
              >
                <Plus /> Add column pair
              </Button>
            </div>
          </fieldset>
          <Field label="Name" htmlFor="fk-name" hint={nameTyped ? undefined : "Follows the foreign key naming convention until you type another."}>
            <Input
              id="fk-name"
              value={draft.name}
              onChange={(e) => {
                setNameTyped(true);
                setDraft({ ...draft, name: e.target.value });
              }}
            />
          </Field>
          <div className="grid grid-cols-2 gap-2">
            {(["onDelete", "onUpdate"] as const).map((rule) => (
              <Field key={rule} label={rule === "onDelete" ? "On delete" : "On update"} htmlFor={`fk-${rule}`}>
                <Select id={`fk-${rule}`} value={draft[rule]} onChange={(e) => change({ ...draft, [rule]: e.target.value as FkAction })}>
                  {FK_ACTIONS.map((x) => (
                    <option key={x} value={x}>
                      {FK_ACTION_LABELS[x]}
                    </option>
                  ))}
                </Select>
              </Field>
            ))}
          </div>
          <Field label="Deferrable" htmlFor="fk-deferrable" hint="When the key is checked: on each statement, or at commit.">
            <Select
              id="fk-deferrable"
              title={note}
              value={draft.deferrable ?? "not-deferrable"}
              onChange={(e) => change({ ...draft, deferrable: e.target.value as FkDeferrable })}
            >
              {FK_DEFERRABLE.map((x) => (
                <option key={x} value={x}>
                  {FK_DEFERRABLE_LABELS[x]}
                </option>
              ))}
            </Select>
          </Field>
          {note && (draft.deferrable ?? "not-deferrable") !== "not-deferrable" ? (
            <p className="text-11 text-secondary" data-testid="fk-dialog-deferrable-note">
              {note}
            </p>
          ) : null}
          {a && isLegacy(a) ? (
            <p className="rounded-control border border-default bg-app px-2 py-1 text-12" data-testid="fk-store-note">
              {a.name} is not stored as a table file yet: {editing ? "saving" : "creating"} the key stores it as one first (one undo step).
            </p>
          ) : null}
          {problems.errors.length || problems.warnings.length ? (
            <ul className="flex flex-col gap-0.5 text-12" data-testid="fk-problems">
              {problems.errors.map((p) => (
                <li key={p} className="text-danger">
                  {p}
                </li>
              ))}
              {problems.warnings.map((p) => (
                <li key={p} className="text-warning">
                  {p}
                </li>
              ))}
            </ul>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button variant="ghost" onClick={onClose}>
              Cancel
            </Button>
            <Button type="submit" variant="primary" disabled={!!problems.errors.length || busy} data-testid="fk-save">
              {editing ? "Save" : "Create"}
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
