import {
  ArrowLeftRight,
  Box,
  Database,
  Eye,
  FolderTree,
  Gem,
  GitBranch,
  Hash,
  Library,
  Link2,
  ListChecks,
  ListOrdered,
  Package,
  Rows3,
  Stamp,
  Table2,
  Tags,
  Type,
  UserRound,
  Workflow,
  type LucideIcon,
} from "lucide-react";
import type { ElementKind } from "@/api/types";

export const KIND_ICONS: Record<ElementKind, LucideIcon> = {
  package: Package,
  entity: Box,
  "value-object": Gem,
  "scalar-type": Type,
  enum: ListOrdered,
  relation: Link2,
  database: Database,
  table: Table2,
  view: Eye,
  sequence: Hash,
  mapping: ArrowLeftRight,
  diagram: Workflow,
  "tag-vocabulary": Tags,
  "category-tree": FolderTree,
  stereotype: Stamp,
  "reference-type": Library,
  seed: Rows3,
  process: GitBranch,
  actor: UserRound,
  scenario: ListChecks,
};

export function KindIcon({ kind, className }: { kind: ElementKind; className?: string }) {
  const Icon = KIND_ICONS[kind] ?? Box;
  return <Icon aria-hidden className={className ?? "size-4 shrink-0 text-secondary"} />;
}
