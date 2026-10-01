// The Reference data screen's dialogs: New reference type (name, display name, category and "Stored as", the type's
// storage choice, preselecting a check constraint; RT 4.5 "without leaving the entity" reuses it) and Import CSV (RT 2.3): pick or paste a file, preview what it adds, changes and removes,
// then apply it as one save of the seed.
import { useState } from "react";
import { useSettings } from "@/api/queries";
import type { ElementDocument, SeedDoc, StorageChoice } from "@/api/types";
import * as endpoints from "@/api/endpoints";
import { useServices } from "@/app/context";
import { Button } from "@/components/ui/button";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select, Textarea } from "@/components/ui/input";
import { IDENTIFIER, identifierHint } from "@/model/model";
import { applyCsvImport, createReferenceType, currentSeed } from "./actions";
import { previewSummary, type PreviewSummary } from "./csvPreview";
import type { CategoryInfo } from "./listModel";
import { preselectedStorage, storageFor, storageOptions, type DeclaredStrategies } from "./storageChoices";
import { applySeedImport, previewSeedImport, readImportFiles, type SeedImportItem } from "./seedBundle";

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
  const settings = useSettings();
  const project = settings.data?.json as
    { conventions?: { referenceStorage?: StorageChoice }; referenceData?: { strategies?: DeclaredStrategies } } | undefined;
  const strategies = project?.referenceData?.strategies ?? {};
  const [storedAs, setStoredAs] = useState<string | null>(null);
  const stored = storedAs ?? preselectedStorage(strategies, project?.conventions?.referenceStorage);
  const options = storageOptions(strategies);
  const chosenHelp = options.find((o) => o.value === stored)?.help;
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const valid = IDENTIFIER.test(name);
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent title="New reference type" description="A set of rows managed as data, with a code and a label; its rows live in a seed created with it.">
        <form
          className="flex flex-col gap-2"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!valid || busy) return;
            setBusy(true);
            const result = await createReferenceType(services, { name, displayName, category: category || null, storage: storageFor(stored, strategies) });
            setBusy(false);
            if (!result.ok) {
              setError(result.reason);
              return;
            }
            setName("");
            setDisplayName("");
            setCategory("");
            setStoredAs(null);
            setError(null);
            onCreated(result.id);
          }}
        >
          <Field label="Name" htmlFor="new-reference-type-name" hint={identifierHint("UnitOfMeasure", "car_models")}>
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
          <Field
            label="Stored as"
            htmlFor="new-reference-type-storage"
            hint={
              Object.keys(strategies).length
                ? "For every database; the Storage tab sets it per database."
                : "The project declares no storage strategies yet (Settings › Conventions declares the standard ones), so the packs decide."
            }
          >
            <Select id="new-reference-type-storage" value={stored} onChange={(e) => setStoredAs(e.target.value)} data-testid="new-reference-type-storage">
              {options.map((o) => (
                <option key={o.value} value={o.value}>
                  {o.label}
                </option>
              ))}
            </Select>
            {chosenHelp ? (
              <p className="text-11 text-secondary" data-testid="new-reference-type-storage-help">
                {chosenHelp}
              </p>
            ) : null}
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
        <div className="flex flex-col gap-2">
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

/**
 * Import seed data…: a ZIP of CSVs (as Export all seed data writes it) or several CSV files, each matched to a seed by
 * its file name; the preview lists every seed's dry run, then Apply imports them all as one undo step.
 */
export function ImportSeedsDialog({ open, onOpenChange }: { open: boolean; onOpenChange(open: boolean): void }) {
  const services = useServices();
  const [files, setFiles] = useState<File[]>([]);
  const [mode, setMode] = useState<"merge" | "replace">("merge");
  const [preview, setPreview] = useState<{ items: SeedImportItem[]; skipped: string[] } | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const reset = () => {
    setFiles([]);
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
  const changing = preview?.items.filter((i) => i.summary.canApply).length ?? 0;
  const blocked = preview?.items.some((i) => i.summary.errors.length) ?? false;
  return (
    <Dialog
      open={open}
      onOpenChange={(o) => {
        if (!o) reset();
        onOpenChange(o);
      }}
    >
      <DialogContent
        title="Import seed data"
        description="A ZIP of CSV files (as Export all seed data writes it) or several CSV files. Each file goes to the seed its name names (<seed>.csv). Preview first: applying is one step you can undo."
      >
        <div className="flex flex-col gap-2">
          <Field label="ZIP or CSV files" htmlFor="import-seeds-files">
            <Input
              id="import-seeds-files"
              type="file"
              multiple
              accept=".zip,.csv,application/zip,text/csv"
              onChange={(e) => {
                setFiles([...(e.target.files ?? [])]);
                setPreview(null);
              }}
            />
          </Field>
          <Field label="Mode" htmlFor="import-seeds-mode" hint="Merge updates and adds; Replace also removes rows missing from a seed's file.">
            <Select
              id="import-seeds-mode"
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
          {preview ? (
            <div className="flex max-h-72 flex-col gap-2 overflow-auto" data-testid="seeds-preview">
              {preview.items.length === 0 ? <p className="text-12 text-secondary">No file matches a seed.</p> : null}
              {preview.items.map((i) => (
                <div key={i.seed.id} className="flex flex-col gap-1">
                  <p className="text-12 font-semibold">
                    {i.seed.name} <span className="font-normal text-secondary">← {i.file}</span>
                  </p>
                  <PreviewView summary={i.summary} />
                </div>
              ))}
              {preview.skipped.map((m) => (
                <p key={m} className="text-12 text-secondary">
                  Skipped {m}
                </p>
              ))}
            </div>
          ) : null}
          {error ? (
            <p role="alert" className="text-12 text-danger">
              {error}
            </p>
          ) : null}
          <div className="flex justify-end gap-2">
            <Button onClick={() => onOpenChange(false)}>Cancel</Button>
            <Button
              disabled={!files.length || busy}
              onClick={() =>
                run(async () => {
                  setPreview(await previewSeedImport(services, await readImportFiles(files), mode));
                })
              }
            >
              Preview
            </Button>
            <Button
              variant="primary"
              disabled={!changing || blocked || busy}
              onClick={() =>
                run(async () => {
                  const result = await applySeedImport(services, preview!.items, mode);
                  if (!result.ok) {
                    setError(result.reason);
                    return;
                  }
                  services.store.getState().notify(`Imported seed data into ${result.applied} ${result.applied === 1 ? "seed" : "seeds"}.`);
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
