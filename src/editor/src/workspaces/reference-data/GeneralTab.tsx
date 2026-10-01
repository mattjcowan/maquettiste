// The General tab of the Reference data screen: the type's common fields (name, display name, plural name,
// description, category, stereotypes, tags) through the inspector's CommonFields, since the screen has no inspector.
// A reference type has no domain (RS3), so there is no domain field. The name commits as Rename does: one batch that
// renames the type's seed with it, so one undo step. With two or more declared locales, the inspector's collapsed
// Translations section translates the display name, plural name and description.
import type { ModelJson } from "@/api/types";
import { useServices } from "@/app/context";
import { Spinner } from "@/components/ui/misc";
import { CommonFields } from "@/inspector/fields";
import { TranslationsSection } from "@/l10n/TranslationsSection";
import { useDraftDocument } from "@/inspector/useDraft";
import { useEditor } from "@/state/store";
import { renameReferenceType } from "./actions";
import type { RefTypeItem } from "./listModel";
import { typeNameProblem } from "./typeMenu";

export function GeneralTab({ item, taken }: { item: RefTypeItem; taken: readonly string[] }) {
  const services = useServices();
  const { json, element, edit, flush } = useDraftDocument(item.id);
  const diagnostics = useEditor(services.store, (s) => s.drafts[item.id]?.diagnostics) ?? [];
  if (!json) return <Spinner />;
  const displayName = (json as { displayName?: string }).displayName ?? "";
  return (
    <div className="flex max-w-xl flex-col gap-2" data-testid="reference-general">
      <p className="text-12 text-secondary">
        The name is the identifier templates and files use; the display name is what lists and headers show. The type&apos;s rows live in a seed named after it,
        renamed with it.
      </p>
      <CommonFields
        id={item.id}
        json={json as ModelJson}
        doc={element.data}
        edit={edit}
        flush={() => void flush()}
        diagnostics={diagnostics}
        rename={(name) => renameReferenceType(services, item, name, displayName)}
        nameProblem={(name) => typeNameProblem(name, item.name, taken)}
      />
      <TranslationsSection id={item.id} kind="reference-type" />
    </div>
  );
}
