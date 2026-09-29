import { useState } from "react";
import type { ElementSummary } from "@/api/types";
import { Dialog, DialogContent } from "@/components/ui/dialog";
import { Field, Input, Select } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { defaultEndRoles, IDENTIFIER } from "@/model/model";
import { GROUP_LABELS, KIND_LABELS } from "@/model/labels";

export function NewEntityDialog({
  open,
  onOpenChange,
  packages,
  defaultPackage,
  onCreate,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  packages: ElementSummary[];
  defaultPackage: string | null;
  onCreate: (name: string, packageId: string | null) => Promise<string | null>;
}) {
  const [name, setName] = useState("");
  const [pkg, setPkg] = useState(defaultPackage ?? "");
  const [error, setError] = useState<string | null>(null);
  const valid = IDENTIFIER.test(name);
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent title="New entity" description="Creates the entity with a uuid key and adds it to this diagram, in one batch.">
        <form
          className="flex flex-col gap-3"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!valid) return;
            const failure = await onCreate(name, pkg || null);
            if (failure) setError(failure);
            else {
              setName("");
              setError(null);
            }
          }}
        >
          <Field label="Name" htmlFor="new-entity-name" hint="A PascalCase identifier, such as Shipment.">
            <Input id="new-entity-name" autoFocus value={name} onChange={(e) => setName(e.target.value)} aria-invalid={(name !== "" && !valid) || undefined} />
          </Field>
          <Field label={KIND_LABELS.package} htmlFor="new-entity-package">
            <Select id="new-entity-package" value={pkg} onChange={(e) => setPkg(e.target.value)}>
              <option value="">{GROUP_LABELS.notInDomain}</option>
              {packages.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
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
            <Button type="submit" variant="primary" disabled={!valid}>
              Create
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}

export interface NewRelationInput {
  name: string;
  kind: "association" | "aggregation" | "composition";
  sourceRole: string;
  targetRole: string;
  sourceMin: 0 | 1;
  sourceMax: 1 | "*";
  targetMin: 0 | 1;
  targetMax: 1 | "*";
  onDelete: "none" | "cascade" | "restrict" | "set-null";
}

export function NewRelationDialog({
  open,
  onOpenChange,
  sourceName,
  targetName,
  onCreate,
}: {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  sourceName: string;
  targetName: string;
  onCreate: (input: NewRelationInput) => Promise<string | null>;
}) {
  const [input, setInput] = useState<NewRelationInput>({
    name: "",
    kind: "association",
    ...defaultEndRoles(sourceName, targetName),
    sourceMin: 1,
    sourceMax: 1,
    targetMin: 0,
    targetMax: "*",
    onDelete: "restrict",
  });
  const [error, setError] = useState<string | null>(null);
  const set = <K extends keyof NewRelationInput>(key: K, value: NewRelationInput[K]) => setInput((i) => ({ ...i, [key]: value }));
  const valid = input.name.trim() !== "" && IDENTIFIER.test(input.sourceRole) && IDENTIFIER.test(input.targetRole);
  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent title={`New relation: ${sourceName} → ${targetName}`} description="Creates the relation and adds it to this diagram, in one batch.">
        <form
          className="flex flex-col gap-3"
          onSubmit={async (e) => {
            e.preventDefault();
            if (!valid) return;
            const failure = await onCreate(input);
            setError(failure);
          }}
        >
          <div className="grid grid-cols-2 gap-2">
            <Field label="Name" htmlFor="rel-name">
              <Input id="rel-name" autoFocus value={input.name} onChange={(e) => set("name", e.target.value)} />
            </Field>
            <Field label="Kind" htmlFor="rel-kind">
              <Select id="rel-kind" value={input.kind} onChange={(e) => set("kind", e.target.value as NewRelationInput["kind"])}>
                <option value="association">association</option>
                <option value="aggregation">aggregation</option>
                <option value="composition">composition</option>
              </Select>
            </Field>
          </div>
          <fieldset className="grid grid-cols-3 gap-2 rounded-control border border-default p-2">
            <legend className="px-1 text-12 text-secondary">{sourceName} end</legend>
            <Field label="Role" htmlFor="rel-src-role">
              <Input id="rel-src-role" value={input.sourceRole} onChange={(e) => set("sourceRole", e.target.value)} />
            </Field>
            <Field label="Min" htmlFor="rel-src-min">
              <Select id="rel-src-min" value={String(input.sourceMin)} onChange={(e) => set("sourceMin", e.target.value === "1" ? 1 : 0)}>
                <option value="0">0</option>
                <option value="1">1</option>
              </Select>
            </Field>
            <Field label="Max" htmlFor="rel-src-max">
              <Select id="rel-src-max" value={String(input.sourceMax)} onChange={(e) => set("sourceMax", e.target.value === "1" ? 1 : "*")}>
                <option value="1">1</option>
                <option value="*">*</option>
              </Select>
            </Field>
          </fieldset>
          <fieldset className="grid grid-cols-3 gap-2 rounded-control border border-default p-2">
            <legend className="px-1 text-12 text-secondary">{targetName} end</legend>
            <Field label="Role" htmlFor="rel-tgt-role">
              <Input id="rel-tgt-role" value={input.targetRole} onChange={(e) => set("targetRole", e.target.value)} />
            </Field>
            <Field label="Min" htmlFor="rel-tgt-min">
              <Select id="rel-tgt-min" value={String(input.targetMin)} onChange={(e) => set("targetMin", e.target.value === "1" ? 1 : 0)}>
                <option value="0">0</option>
                <option value="1">1</option>
              </Select>
            </Field>
            <Field label="Max" htmlFor="rel-tgt-max">
              <Select id="rel-tgt-max" value={String(input.targetMax)} onChange={(e) => set("targetMax", e.target.value === "1" ? 1 : "*")}>
                <option value="1">1</option>
                <option value="*">*</option>
              </Select>
            </Field>
          </fieldset>
          <Field label={`On delete of the ${sourceName}`} htmlFor="rel-ondelete">
            <Select id="rel-ondelete" value={input.onDelete} onChange={(e) => set("onDelete", e.target.value as NewRelationInput["onDelete"])}>
              {["none", "cascade", "restrict", "set-null"].map((v) => (
                <option key={v} value={v}>
                  {v}
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
            <Button type="submit" variant="primary" disabled={!valid}>
              Create relation
            </Button>
          </div>
        </form>
      </DialogContent>
    </Dialog>
  );
}
