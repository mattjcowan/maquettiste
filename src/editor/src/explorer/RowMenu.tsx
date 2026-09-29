// The context menu of a row or of a multi-selection (explorer-redesign.md 1.8), opened by a right click, the
// context-menu key or Shift+F10, at the pointer or at the row. The items come from menus.ts.
import { Fragment } from "react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/menu";
import type { MenuActionId, MenuItem } from "./menus";

export interface RowMenuState {
  x: number;
  y: number;
  title: string;
  items: MenuItem[];
}

export function RowMenu({ menu, onClose, onRun }: { menu: RowMenuState | null; onClose: () => void; onRun: (id: MenuActionId) => void }) {
  return (
    <DropdownMenu open={!!menu} onOpenChange={(open) => !open && onClose()} modal={false}>
      <DropdownMenuTrigger asChild>
        <span aria-hidden className="pointer-events-none fixed size-0" style={{ left: menu?.x ?? 0, top: menu?.y ?? 0 }} />
      </DropdownMenuTrigger>
      {menu ? (
        <DropdownMenuContent align="start" aria-label={`Actions: ${menu.title}`} data-testid="row-menu">
          <DropdownMenuLabel>{menu.title}</DropdownMenuLabel>
          {menu.items.length === 0 ? <p className="px-2 py-1 text-12 text-secondary">No action applies to every selected row</p> : null}
          {menu.items.map((item, i) => (
            <Fragment key={item.id}>
              {item.danger && i > 0 ? <DropdownMenuSeparator /> : null}
              <DropdownMenuItem className={item.danger ? "text-danger" : undefined} onSelect={() => onRun(item.id)}>
                {item.label}
              </DropdownMenuItem>
            </Fragment>
          ))}
        </DropdownMenuContent>
      ) : null}
    </DropdownMenu>
  );
}
