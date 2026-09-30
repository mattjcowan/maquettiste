// The attribute grid (inspector/AttributeGrid.tsx) over a process's other attribute lists: the context, an event's
// payload and a gate's audit attributes (phase-3-design.md 2.3: they use the attribute model unchanged).
import type { AttributeDoc, ModelJson } from "@/api/types";
import { AttributeGrid } from "@/inspector/AttributeGrid";
import { useDefinition } from "@/inspector/definition";
import { useVocabularies } from "@/inspector/fields";
import { TYPE_KINDS } from "@/model/model";
import type { ProcessDoc } from "@/model/process";
import type { ProcessContext } from "./shared";

export interface AttributeListAt {
  get: (p: ProcessDoc) => AttributeDoc[] | undefined;
  set: (p: ProcessDoc, list: AttributeDoc[] | undefined) => void;
}

export function ProcessAttributeGrid({
  pc,
  label,
  owner,
  pointerBase,
  at,
}: {
  pc: ProcessContext;
  label: string;
  owner: string;
  pointerBase: string;
  at: AttributeListAt;
}) {
  const vocab = useVocabularies("process");
  const definition = useDefinition();
  const typeOptions = TYPE_KINDS.flatMap((k) => vocab.lookup.ofKind(k));
  return (
    <AttributeGrid
      owner={owner}
      label={label}
      attributes={at.get(pc.process) ?? []}
      typeOptions={typeOptions}
      definition={definition}
      diagnostics={pc.problems}
      pointerBase={pointerBase}
      withKey={false}
      onChange={(update, commit) => {
        pc.draftEdit((p) => {
          const holder = { attributes: at.get(p) ?? [] };
          update(holder as unknown as ModelJson);
          at.set(p, holder.attributes.length ? holder.attributes : undefined);
        });
        if (commit) pc.commit();
      }}
    />
  );
}
