// The domain editor (explorer-redesign.md 1.11): opening a domain row (Enter, a double click, the menu's Open) shows
// its General fields and the domain's own Tags and Categories, each listing the vocabularies it inherits read-only.
// The row still expands into its sub-domains and kind folders.
import { CommonFields } from "@/inspector/fields";
import { CategoryTreeEditor, TagVocabularyEditor } from "@/vocabularies/VocabularyEditors";
import { domIdOf, EditorLayout, useEditorContext } from "./EditorFrame";

export const DOMAIN_EDITOR_TABS = { general: "General", tags: "Tags", categories: "Categories" } as const;

export function DomainEditor({ id }: { id: string }) {
  const { ctx, fallback, draft } = useEditorContext(id, "package");
  if (!ctx) return <>{fallback}</>;
  const form = { ...ctx, id: domIdOf(ctx.id) };
  return (
    <EditorLayout
      ctx={ctx}
      draft={draft}
      controls={<p className="text-12 text-secondary">Tags and categories declared here apply to this domain and the domains nested under it.</p>}
      tabs={[
        { value: "general", label: DOMAIN_EDITOR_TABS.general, content: <CommonFields {...form} inEditorHeader /> },
        { value: "tags", label: DOMAIN_EDITOR_TABS.tags, content: <TagVocabularyEditor scope={ctx.id} /> },
        { value: "categories", label: DOMAIN_EDITOR_TABS.categories, content: <CategoryTreeEditor scope={ctx.id} /> },
      ]}
    />
  );
}
