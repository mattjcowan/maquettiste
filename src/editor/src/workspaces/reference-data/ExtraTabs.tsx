// The Code generation and JSON tabs of the Reference data screen: the same tabs the element editors have, built from
// the same parts (the editors' Code generation content and the inspector's JSON view), over the type's draft so a
// change saves like every other tab's.
import type { ModelJson } from "@/api/types";
import { Spinner } from "@/components/ui/misc";
import { useCodeGenerationTab, type EditorContext } from "@/editors/EditorFrame";
import type { EditorKind } from "@/editors/tabs";
import { JsonTab } from "@/inspector/Inspector";
import { useDraftDocument } from "@/inspector/useDraft";
import { useEditor } from "@/state/store";
import { useServices } from "@/app/context";

function useTypeContext(id: string): EditorContext | null {
  const { store } = useServices();
  const { json, element, edit, flush } = useDraftDocument(id);
  const diagnostics = useEditor(store, (s) => s.drafts[id]?.diagnostics) ?? [];
  if (!json) return null;
  // A reference type has no editor of its own; the frame's Code generation content only reads its kind as a string.
  return { id, kind: "reference-type" as EditorKind, json, doc: element.data, edit, flush: () => void flush(), diagnostics, summary: undefined, name: "" };
}

function CodeGenerationContent({ ctx }: { ctx: EditorContext }) {
  return <>{useCodeGenerationTab(ctx).content}</>;
}

export function CodeGenerationTab({ typeId }: { typeId: string }) {
  const ctx = useTypeContext(typeId);
  return ctx ? <CodeGenerationContent ctx={ctx} /> : <Spinner />;
}

export function JsonViewTab({ typeId }: { typeId: string }) {
  const ctx = useTypeContext(typeId);
  return ctx ? <JsonTab json={ctx.json} onChange={(next: ModelJson) => ctx.edit(() => next)} /> : <Spinner />;
}
