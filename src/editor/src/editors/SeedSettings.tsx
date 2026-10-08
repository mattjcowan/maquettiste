// How a seed's rows are applied (table seeds, 2026-10-07), one line per seed under the Seed data and Data grids: the
// environments it belongs to (none: every environment), insert once or keep as the model has it (converge), whether a
// converging seed deletes the rows it does not hold, a table seed's row key, and whether its rows live in a CSV file beside
// the seed file. Each change is one save of the seed.
import { useElements } from "@/api/queries";
import type { SeedDoc } from "@/api/types";
import { CheckboxField } from "@/components/ui/checkbox";
import { Input, Select } from "@/components/ui/input";
import { useDraftDocument } from "@/inspector/useDraft";

type SeedSettingsDoc = SeedDoc & { environments?: string[]; apply?: "once" | "converge"; delete?: boolean; key?: string; rowsFrom?: { file: string } };
type TableLike = { kind?: string; uniques?: { id: string; name?: string }[] };

const kebab = (name: string) =>
  name
    .replace(/([a-z0-9])([A-Z])/g, "$1-$2")
    .replace(/[^A-Za-z0-9]+/g, "-")
    .replace(/^-|-$/g, "")
    .toLowerCase() || "rows";

export function SeedSettings({ seeds, target }: { seeds: string[]; target: string }) {
  const table = useElements([target]).byId.get(target)?.json as unknown as TableLike | undefined;
  if (!seeds.length) return null;
  return (
    <section className="flex flex-col gap-1 border-t border-default px-2 py-1" aria-label="Seed settings" data-testid="seed-settings">
      {seeds.map((id) => (
        <SeedSettingsRow key={id} id={id} table={table?.kind === "table" ? table : undefined} />
      ))}
    </section>
  );
}

function SeedSettingsRow({ id, table }: { id: string; table?: TableLike }) {
  const { json, edit, flush } = useDraftDocument(id);
  const seed = json as unknown as SeedSettingsDoc | undefined;
  if (!seed) return null;
  const set = (change: (s: SeedSettingsDoc) => void) => {
    edit((j) => change(j as unknown as SeedSettingsDoc));
    void flush();
  };
  const converge = seed.apply === "converge";
  return (
    <div className="flex flex-wrap items-center gap-x-3 gap-y-1 text-12" data-testid={`seed-settings-${seed.name}`}>
      <span className="w-40 truncate font-medium" title={seed.name}>
        {seed.name}
      </span>
      <label className="flex items-center gap-1">
        <span className="text-secondary">Environments</span>
        <Input
          aria-label={`Environments of ${seed.name}`}
          className="h-6 w-44"
          placeholder="every environment"
          defaultValue={(seed.environments ?? []).join(", ")}
          title="Comma-separated names (dev, test, staging); empty: every environment"
          onBlur={(e) => {
            const names = [
              ...new Set(
                e.target.value
                  .split(",")
                  .map((n) => n.trim())
                  .filter(Boolean),
              ),
            ];
            set((s) => {
              if (names.length) s.environments = names;
              else delete s.environments;
            });
          }}
        />
      </label>
      <label className="flex items-center gap-1">
        <span className="text-secondary">Apply</span>
        <Select
          aria-label={`How the rows of ${seed.name} are applied`}
          className="h-6 w-72"
          value={converge ? "converge" : "once"}
          onChange={(e) =>
            set((s) => {
              if (e.target.value === "converge") s.apply = "converge";
              else {
                delete s.apply;
                delete s.delete;
              }
            })
          }
        >
          <option value="once">Insert once (leave existing rows)</option>
          <option value="converge">Keep as the model has them (update)</option>
        </Select>
      </label>
      {converge ? (
        <CheckboxField
          id={`seed-delete-${id}`}
          label="Delete rows the seed does not hold"
          checked={seed.delete === true}
          onChange={(v) =>
            set((s) => {
              if (v) s.delete = true;
              else delete s.delete;
            })
          }
        />
      ) : null}
      {table ? (
        <label className="flex items-center gap-1">
          <span className="text-secondary">Row key</span>
          <Select
            aria-label={`Row key of ${seed.name}`}
            className="h-6 w-44"
            value={seed.key ?? ""}
            onChange={(e) =>
              set((s) => {
                if (e.target.value) s.key = e.target.value;
                else delete s.key;
              })
            }
          >
            <option value="">Primary key</option>
            {(table.uniques ?? []).map((u) => (
              <option key={u.id} value={u.id}>
                {u.name || u.id}
              </option>
            ))}
          </Select>
        </label>
      ) : null}
      <CheckboxField
        id={`seed-csv-${id}`}
        label="Rows in a CSV file"
        checked={!!seed.rowsFrom}
        onChange={(v) =>
          set((s) => {
            if (v) s.rowsFrom = { file: `${kebab(seed.name)}.csv` };
            else delete s.rowsFrom;
          })
        }
      />
      {seed.rowsFrom ? <span className="font-mono text-11 text-secondary">{seed.rowsFrom.file}</span> : null}
    </div>
  );
}
