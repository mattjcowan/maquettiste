import { useState } from "react";
// The Storage tab (reference-types-seeds-localization.md 1.4 and 4.3): per database, the strategy in use and where it
// comes from (this type, the database, the project, or nothing: the packs decide), and the choice set for this type:
// the default, "Let the packs decide", or one of the strategies the project declares, whose description shows under
// it. A line above the table says what a strategy is. The engine knows no strategy; the packs realize the choice.
import { useElements, useIndex, usePacks, useSettings } from "@/api/queries";
import * as endpoints from "@/api/endpoints";
import { Button } from "@/components/ui/button";
import { firstCodes } from "./rowsModel";
import {
  databaseUnits,
  defaultOption,
  PACKS_DECIDE,
  PACKS_DECIDE_HELP,
  preferredUnit,
  previewFailure,
  snakeNames,
  strategyInUse,
  typeStatements,
  type DatabaseUnit,
} from "./storageChoices";
import type { ReferenceTypeDoc, StorageChoice } from "@/api/types";
import { Input, Select } from "@/components/ui/input";
import { Spinner } from "@/components/ui/misc";
import { useDraftDocument } from "@/inspector/useDraft";

const INHERIT = "__inherit";
const TEMPLATE = "__template";

interface Declared {
  description?: string | null;
  options?: Record<string, { type?: string; description?: string }> | null;
}

export interface Effective {
  strategy: string | null;
  source: "type" | "database" | "project" | null;
}

/** The effective choice for a database (RT 1.4): type[db id] → type["*"] → database settings → project → none. */
export function effectiveStorage(
  storage: Record<string, StorageChoice> | undefined,
  databaseId: string | null,
  databaseChoice: StorageChoice | undefined,
  projectChoice: StorageChoice | undefined,
): Effective {
  const own = (databaseId && storage?.[databaseId]) || storage?.["*"];
  if (own) return { strategy: own.strategy ?? null, source: "type" };
  if (databaseChoice) return { strategy: databaseChoice.strategy ?? null, source: "database" };
  if (projectChoice) return { strategy: projectChoice.strategy ?? null, source: "project" };
  return { strategy: null, source: null };
}

export function StorageTab({ typeId }: { typeId: string }) {
  const { json, edit, flush } = useDraftDocument(typeId);
  const settings = useSettings();
  const index = useIndex();
  if (!json || settings.isLoading) return <Spinner />;
  const type = json as unknown as ReferenceTypeDoc;
  const project = settings.data?.json as
    | {
        conventions?: { referenceStorage?: StorageChoice };
        databases?: Record<string, { referenceStorage?: StorageChoice }>;
        referenceData?: { strategies?: Record<string, Declared> };
      }
    | undefined;
  const strategies = project?.referenceData?.strategies ?? {};
  const storage = (type.storage ?? {}) as Record<string, StorageChoice>;
  const databases = (index.data ?? []).filter((r) => r.kind === "database").sort((a, b) => a.name.localeCompare(b.name));
  const projectChoice = project?.conventions?.referenceStorage;

  const setChoice = (key: string, value: string) => {
    edit((j) => {
      const t = j as unknown as ReferenceTypeDoc;
      const next = { ...((t.storage ?? {}) as Record<string, StorageChoice>) };
      if (value === INHERIT) delete next[key];
      else next[key] = { strategy: value === TEMPLATE ? null : value };
      if (Object.keys(next).length) (t as { storage?: unknown }).storage = next;
      else delete (t as { storage?: unknown }).storage;
    });
    void flush();
  };
  const setOption = (key: string, option: string, value: string) =>
    edit((j) => {
      const t = j as unknown as ReferenceTypeDoc;
      const choice = ((t.storage ?? {}) as Record<string, StorageChoice>)[key];
      if (!choice) return;
      const options = { ...(choice.options ?? {}) };
      if (value === "") delete options[option];
      else options[option] = value;
      if (Object.keys(options).length) choice.options = options;
      else delete choice.options;
    });

  const rowsOf = [
    { key: "*", label: "All databases", id: null as string | null, name: null as string | null },
    ...databases.map((d) => ({ key: d.id, label: d.name, id: d.id, name: d.name })),
  ];
  const declaredCount = Object.keys(strategies).length;

  return (
    <div className="flex max-w-3xl flex-col gap-2" data-testid="reference-storage">
      <p className="text-12 text-secondary" data-testid="storage-explanation">
        A storage strategy says how the packs store this type&apos;s codes in a database, for example as a lookup table or a check constraint. The project
        declares its strategies in{" "}
        <span title="referenceData.strategies in the project settings" className="underline decoration-dotted">
          Settings › Conventions
        </span>{" "}
        and may choose a default for every type there; a database&apos;s settings can choose another. Here you can choose one for this type, for every database
        or for one. &ldquo;
        {PACKS_DECIDE}&rdquo; chooses none: the templates decide how the type is stored.
      </p>
      <p className="text-12 text-secondary" data-testid="storage-project-default">
        Project default: <span className="font-mono">{projectChoice?.strategy ?? "none (the packs decide)"}</span>
        {declaredCount ? "" : ". The project declares no strategies yet, so the packs decide; Settings › Conventions declares the standard ones."}
      </p>
      <table className="w-full border-collapse text-12" aria-label="Storage per database">
        <thead>
          <tr className="bg-app text-left text-11 font-semibold text-secondary">
            <th className="h-7 px-1.5">Database</th>
            <th className="px-1.5">Strategy in use</th>
            <th className="px-1.5">Set for this type</th>
          </tr>
        </thead>
        <tbody>
          {rowsOf.map((row) => {
            const databaseChoice = row.name ? project?.databases?.[row.name]?.referenceStorage : undefined;
            const effective =
              row.id === null ? effectiveStorage(storage, null, undefined, projectChoice) : effectiveStorage(storage, row.id, databaseChoice, projectChoice);
            const rest = Object.fromEntries(Object.entries(storage).filter(([k]) => k !== row.key));
            const fallback =
              row.id === null ? effectiveStorage(undefined, null, undefined, projectChoice) : effectiveStorage(rest, row.id, databaseChoice, projectChoice);
            const own = storage[row.key];
            const value = own ? (own.strategy ?? TEMPLATE) : INHERIT;
            const declared = own?.strategy ? strategies[own.strategy] : undefined;
            return (
              <tr key={row.key} className="border-t border-default align-top" data-testid={`storage-${row.label}`}>
                <td className="h-6 px-1 py-1">{row.label}</td>
                <td className="px-1.5 py-1" data-testid="strategy-in-use">
                  {strategyInUse(effective)}
                </td>
                <td className="px-1.5 py-1">
                  <Select aria-label={`Storage for ${row.label}`} className="h-7 text-12" value={value} onChange={(e) => setChoice(row.key, e.target.value)}>
                    <option value={INHERIT}>{defaultOption(fallback)}</option>
                    <option value={TEMPLATE}>{PACKS_DECIDE}</option>
                    {Object.keys(strategies).map((key) => (
                      <option key={key} value={key}>
                        {key}
                      </option>
                    ))}
                  </Select>
                  {own?.strategy && declared?.description ? (
                    <p className="mt-0.5 text-11 text-secondary" data-testid="strategy-help">
                      {declared.description}
                    </p>
                  ) : own?.strategy && !declared ? (
                    <p className="mt-0.5 text-11 text-danger" title="referenceData.strategies in the project settings">
                      The project does not declare {own.strategy}; declare it in Settings › Conventions.
                    </p>
                  ) : own && !own.strategy ? (
                    <p className="mt-0.5 text-11 text-secondary">{PACKS_DECIDE_HELP}</p>
                  ) : null}
                  {declared?.options
                    ? Object.entries(declared.options).map(([option, schema]) => (
                        <label key={option} className="mt-1 flex items-center gap-1 text-11 text-secondary">
                          <span className="w-20 shrink-0 font-mono">{option}</span>
                          <Input
                            className="h-6 text-12"
                            title={schema.description}
                            value={String(own?.options?.[option] ?? "")}
                            onChange={(e) => setOption(row.key, option, e.target.value)}
                            onBlur={() => void flush()}
                          />
                        </label>
                      ))
                    : null}
                </td>
              </tr>
            );
          })}
        </tbody>
      </table>
      <StoragePreview typeId={typeId} databases={databases} />
    </div>
  );
}

/**
 * Preview output: one database-scoped unit of a pack (from the pack list), rendered for one database as generation
 * would (nothing is written), narrowed to the statements that realize this type, so the effect of the storage choice
 * shows here. When it does not render, the engine's explanation (the explain endpoint) says why.
 */
function StoragePreview({ typeId, databases }: { typeId: string; databases: { id: string; name: string }[] }) {
  const [database, setDatabase] = useState<string>("");
  const [picked, setPicked] = useState<string>("");
  const [result, setResult] = useState<{ text: string[]; path?: string; error?: string; message?: string; unit: DatabaseUnit } | null>(null);
  const [busy, setBusy] = useState(false);
  const index = useIndex();
  const packs = usePacks();
  const units = databaseUnits(packs.data ?? []);
  const unit = units.find((u) => `${u.pack}/${u.unit}` === picked) ?? preferredUnit(units);
  const seedIds = (index.data ?? []).filter((r) => r.kind === "seed" && r.target === typeId).map((r) => r.id);
  const loaded = useElements([typeId, ...seedIds]);
  const chosen = database || databases[0]?.id || "";
  if (databases.length === 0) return null;
  const nameOf = (id: string) => databases.find((d) => d.id === id)?.name ?? id;
  const preview = async () => {
    const type = loaded.byId.get(typeId)?.json as unknown as ReferenceTypeDoc | undefined;
    if (!type || !unit) return;
    setBusy(true);
    const explain = async (message: string) => {
      const answer = await endpoints.explainUnit({ pack: unit.pack, unit: unit.unit, elementId: chosen }).catch(() => null);
      return { text: [], unit, error: previewFailure(message, answer), message };
    };
    try {
      const seeds = seedIds.map((id) => loaded.byId.get(id)?.json as unknown as Parameters<typeof firstCodes>[0][number] | undefined).filter((d) => !!d);
      const codes = firstCodes(seeds, Number.MAX_SAFE_INTEGER).codes;
      const answer = await endpoints.previewTemplate({ pack: unit.pack, unit: unit.unit, elementId: chosen });
      const failed = answer.diagnostics?.find((d) => d.severity === "error");
      if (failed) {
        setResult(await explain(failed.message));
        return;
      }
      const file = answer.files?.[0];
      const statements = file ? typeStatements(file.text, snakeNames(type.name, type.pluralName), codes) : [];
      setResult({ text: statements, path: file?.path, unit });
    } catch (e) {
      setResult(await explain((e as Error).message));
    } finally {
      setBusy(false);
    }
  };
  const unitName = unit ? `${unit.unit} (${unit.pack})` : "";
  return (
    <section aria-label="Preview output" className="mt-3 flex flex-col gap-1">
      <div className="flex flex-wrap items-center gap-2">
        <span className="text-12 font-semibold">Preview output</span>
        {units.length ? (
          <Select
            aria-label="Unit to preview"
            className="w-auto"
            value={unit ? `${unit.pack}/${unit.unit}` : ""}
            onChange={(e) => setPicked(e.target.value)}
            data-testid="storage-preview-unit"
          >
            {units.map((u) => (
              <option key={`${u.pack}/${u.unit}`} value={`${u.pack}/${u.unit}`} title={u.for}>
                {u.unit} ({u.pack})
              </option>
            ))}
          </Select>
        ) : null}
        <span className="text-12 text-secondary">for</span>
        <Select aria-label="Database to preview" className="w-auto" value={chosen} onChange={(e) => setDatabase(e.target.value)}>
          {databases.map((d) => (
            <option key={d.id} value={d.id}>
              {d.name}
            </option>
          ))}
        </Select>
        <Button size="sm" variant="secondary" disabled={busy || !unit} onClick={() => void preview()}>
          Preview output
        </Button>
      </div>
      {packs.isSuccess && !units.length ? (
        <p className="text-12 text-secondary">No enabled pack has a unit rendered per database, so there is nothing to preview here.</p>
      ) : unit ? (
        <p className="text-11 text-secondary" data-testid="storage-preview-note">
          Renders the {unitName} unit for {nameOf(chosen)} without writing it, and shows the statements for this type.
        </p>
      ) : null}
      {result?.error ? (
        <div role="alert" className="flex flex-col gap-0.5 text-12" data-testid="storage-preview-error">
          <p className="text-danger">
            The {result.unit.unit} unit of {result.unit.pack} does not render for {nameOf(chosen)}: {result.error}
          </p>
          {result.message && result.message !== result.error ? (
            <details className="text-11 text-secondary">
              <summary>Message from the preview</summary>
              {result.message}
            </details>
          ) : null}
        </div>
      ) : null}
      {result && !result.error ? (
        <pre data-testid="storage-preview" className="max-h-80 overflow-auto rounded-control border border-default bg-app p-2 font-mono text-12">
          {result.text.length
            ? `-- ${result.path ?? result.unit.unit} (${result.unit.pack} · ${result.unit.unit}), the statements for this type\n\n${result.text.join("\n\n")}`
            : `The ${result.unit.unit} unit of ${result.unit.pack} has no statement for this type in this database.`}
        </pre>
      ) : null}
    </section>
  );
}
