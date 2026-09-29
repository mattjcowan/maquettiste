// The Reference data screen's dialogs: New reference type (name, display name, category; RT 4.5 "without leaving
// the entity" reuses it) and Import CSV (RT 2.3): pick or paste a file, preview what it adds, changes and removes,
// then apply it as one save of the seed.
import { useState } from "react";
import type { ElementDocument, SeedDoc } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { IDENTIFIER } from "@/model/model";
import { applyCsvImport, createReferenceType, currentSeed } from "./actions";
import { previewSummary, type PreviewSummary } from "./csvPreview";
import type { CategoryInfo } from "./listModel";

export function NewReferenceTypeDialog({
  open,
  onOpenChange,
  categories,
  onCreated,
}: {
  open: boolean;
  onOpenChange(open: boolean): void;
  categories: readonly CategoryInfo[];
  onCreated(id: string): void;
}) {
  const services = useServices();
  const [name, setName] = useState("");
  const [displayName, setDisplayName] = useState("");
  const [category, setCategory] = useState("");
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const valid = IDENTIFIER.test(name);
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent title="New reference type" description="A set of rows managed as data, with a code and a label; its rows live in a seed created with it.">
        <form
          className="flex flex-col gap-3"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!valid || busy) return;
            setBusy(true);
            const result = await createReferenceType(services, { name, displayName, category: category || null });
            setBusy(false);
            if (!result.ok) {
              setError(result.reason);
              return;
            }
            setName("");
            setDisplayName("");
            setCategory("");
            setError(null);
            onCreated(result.id);
          }}
        >
          <Field label="Name" htmlFor="new-reference-type-name" hint="A PascalCase identifier, such as UnitOfMeasure.">
            <Input
              id="new-reference-type-name"
              autoFocus
              value={name}
              onChange={(e) => setName(e.target.value)}
              aria-invalid={(name !== "" && !valid) || undefined}
            />
          </Field>
          <Field label="Display name" htmlFor="new-reference-type-display">
            <Input id="new-reference-type-display" value={displayName} placeholder="Unit of measure" onChange={(e) => setDisplayName(e.target.value)} />
          </Field>
          <Field label="Category" htmlFor="new-reference-type-category">
            <Select id="new-reference-type-category" value={category} onChange={(e) => setCategory(e.target.value)}>
              <option value="">No category</option>
              {categories.map((c) => (
                <option key={c.value} value={c.value}>
                  {c.label}
                </option>
              ))}
            </Select>
          </Field>
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button type="submit" variant="primary" disabled={!valid || busy}>
              Create
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export function ImportCsvDialog({ open, onOpenChange, seed }: { open: boolean; onOpenChange(open: boolean): void; seed: Pick<SeedDoc, "id" | "name"> }) {
  const services = useServices();
  const [content, setContent] = useState("");
  const [mode, setMode] = useState<"merge" | "replace">("merge");
  const [preview, setPreview] = useState<{ summary: PreviewSummary; before: ElementDocument } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const reset = () => {
    setContent("");
    setPreview(null);
    setError(null);
  };
  const run = async (work: () => Promise<void>) => {
    setBusy(true);
    setError(null);
    try {
      await work();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o) reset();
        onOpenChange(o);
      }}
    >
      <DialogContent
        title={`Import CSV into ${seed.name}`}
        description="Rows match by @id, else by @code; new rows get new ids. Preview first: applying is one save you can undo."
      >
        <div className="flex flex-col gap-3">
          <Field label="CSV file" htmlFor="import-csv-file">
            <Input
              id="import-csv-file"
              type="file"
              accept=".csv,text/csv"
              onChange={async (e) => {
                const file = e.target.files?.[0];
                if (!file) return;
                setContent(await file.text());
                setPreview(null);
              }}
            />
          </Field>
          <Field label="Or paste the CSV text" htmlFor="import-csv-text">
            <Textarea
              id="import-csv-text"
              rows={5}
              className="font-mono text-12"
              value={content}
              placeholder={"@code,@label\nkg,Kilogram"}
              onChange={(e) => {
                setContent(e.target.value);
                setPreview(null);
              }}
            />
          </Field>
          <Field label="Mode" htmlFor="import-csv-mode" hint="Merge updates and adds; Replace also removes rows missing from the file.">
            <Select
              id="import-csv-mode"
              value={mode}
              onChange={(e) => {
                setMode(e.target.value as "merge" | "replace");
                setPreview(null);
              }}
            >
              <option value="merge">Merge</option>
              <option value="replace">Replace</option>
            </Select>
          </Field>
          {preview ? <PreviewView summary={preview.summary} /> : null}
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button
              disabled={!content.trim() || busy}
              onClick={() =>
                run(async () => {
                  const before = await currentSeed(services, seed.id);
                  const answer = await endpoints.importSeedCsv(seed.id, content, { mode, dryRun: true });
                  setPreview({ summary: previewSummary(answer), before });
                })
              }
            >
              Preview
            </Button>
            <Button
              variant="primary"
              disabled={!preview?.summary.canApply || busy}
              onClick={() =>
                run(async () => {
                  const result = await applyCsvImport(services, seed.id, content, mode, preview!.before);
                  if (!result.ok) {
                    setError(result.reason);
                    return;
                  }
                  services.store.getState().notify(`Imported: ${preview!.summary.headline}.`);
                  reset();
                  onOpenChange(false);
                })
              }
            >
              Apply
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  );
}

function PreviewView({ summary }: { summary: PreviewSummary }) {
  return (
    <div className="flex max-h-64 flex-col gap-1 overflow-auto rounded-control border border-default p-2 text-12" data-testid="csv-preview">
      <p className="font-semibold">{summary.headline}</p>
      {summary.ignoredHeaders.length ? <p className="text-secondary">Ignored columns: {summary.ignoredHeaders.join(", ")}</p> : null}
      {summary.blocked ? <p className="text-secondary">{summary.blocked} rows kept because other seeds name them.</p> : null}
      {summary.changed.slice(0, 50).map((c) => (
        <p key={c.id} className="font-mono text-11">
          {c.fields.map((f) => `${f.name}: ${f.before || "∅"} → ${f.after || "∅"}`).join(" · ")}
        </p>
      ))}
      {summary.changed.length > 50 ? <p className="text-secondary">and {summary.changed.length - 50} more changed rows</p> : null}
      {summary.errors.map((e) => (
        <p key={e} role="alert" className="text-danger">
          {e}
        </p>
      ))}
      {summary.warnings.map((w) => (
        <p key={w} className="text-secondary">
          {w}
        </p>
      ))}
    </div>
  );
}
