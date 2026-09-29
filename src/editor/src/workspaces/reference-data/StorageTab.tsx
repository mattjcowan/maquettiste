// The Storage tab (reference-types-seeds-localization.md 1.4 and 4.3): per database, the effective strategy and
// where it comes from (this type, the database's settings, the project default, or none: template-defined), and an
// override chosen from the strategies the project declares, or Template-defined. The engine knows no strategy; the
// packs realize the choice.
import { useIndex, useSettings } from "@/api/queries";
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

const SOURCE_TEXT: Record<NonNullable<Effective["source"]>, string> = {
  type: "set on this type",
  database: "from the database's settings",
  project: "from project settings",
};

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
  const describe = (e: Effective) => `${e.strategy ?? "Template-defined"}${e.source ? ` (${SOURCE_TEXT[e.source]})` : " (the packs decide)"}`;

  return (
    <div className="flex max-w-3xl flex-col gap-2" data-testid="reference-storage">
      <p className="text-12 text-secondary">
        Project default: <span className="font-mono">{projectChoice ? (projectChoice.strategy ?? "Template-defined") : "Template-defined"}</span>
        {Object.keys(strategies).length ? "" : ". The project declares no storage strategies (Settings, referenceData.strategies), so the packs decide."}
      </p>
      <table className="w-full border-collapse text-12" aria-label="Storage per database">
        <thead>
          <tr className="bg-app text-left text-11 font-semibold text-secondary">
            <th className="h-7 px-1.5">Database</th>
            <th className="px-1.5">Effective</th>
            <th className="px-1.5">Override</th>
          </tr>
        </thead>
        <tbody>
          {rowsOf.map((row) => {
            const databaseChoice = row.name ? project?.databases?.[row.name]?.referenceStorage : undefined;
            const effective =
              row.id === null ? effectiveStorage(storage, null, undefined, projectChoice) : effectiveStorage(storage, row.id, databaseChoice, projectChoice);
            const own = storage[row.key];
            const value = own ? (own.strategy ?? TEMPLATE) : INHERIT;
            const declared = own?.strategy ? strategies[own.strategy] : undefined;
            return (
              <tr key={row.key} className="border-t border-default align-top" data-testid={`storage-${row.label}`}>
                <td className="h-6 px-1 py-0.5">{row.label}</td>
                <td className="px-1.5 py-1 font-mono">{describe(effective)}</td>
                <td className="px-1.5 py-1">
                  <Select aria-label={`Storage for ${row.label}`} className="h-7 text-12" value={value} onChange={(e) => setChoice(row.key, e.target.value)}>
                    <option value={INHERIT}>{row.id === null ? "Project default" : "Inherit"}</option>
                    <option value={TEMPLATE}>Template-defined</option>
                    {Object.entries(strategies).map(([key, s]) => (
                      <option key={key} value={key} title={s.description ?? undefined}>
                        {key}
                      </option>
                    ))}
                  </Select>
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
    </div>
  );
}
