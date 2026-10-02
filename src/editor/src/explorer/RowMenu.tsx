// The context menu of a row or of a multi-selection (explorer-redesign.md 1.8), opened by a right click, the
// context-menu key or Shift+F10, at the pointer or at the row. The items come from menus.ts; the editor tabs reuse it
// with their own items.
import { Fragment } from "react";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuLabel, DropdownMenuSeparator, DropdownMenuTrigger } from "@/components/ui/menu";
import type { MenuActionId, MenuItem } from "./menus";

/** What the menu needs of an item: menus.ts's items have it, and so do the editor tabs' own. */
export interface RowMenuItem<Id extends string = string> {
  id: Id;
  label: string;
  /** Drawn in the danger colour, after a separator. */
  danger?: boolean;
  /** Shown but not offered: the note says why. */
  disabledNote?: string;
  /** Shown but not offered, without a note. */
  disabled?: boolean;
  /** A separator comes before the item. */
  separator?: boolean;
}

export interface RowMenuState<Item extends RowMenuItem = MenuItem> {
  x: number;
  y: number;
  title: string;
  items: Item[];
}

export function RowMenu<Id extends string = MenuActionId>({
  menu,
  onClose,
  onRun,
  testId = "row-menu",
  onCloseAutoFocus,
}: {
  menu: RowMenuState<RowMenuItem<Id>> | null;
  onClose: () => void;
  onRun: (id: Id) => void;
  testId?: string;
  /** Where focus goes when the menu closes; by default, back to the hidden anchor's place. */
  onCloseAutoFocus?: (event: Event) => void;
}) {
  return (
    <DropdownMenu open={!!menu} onOpenChange={(open) => !open && onClose()} modal={false}>
      <DropdownMenuTrigger asChild>
        <span aria-hidden className="pointer-events-none fixed size-0" style={{ left: menu?.x ?? 0, top: menu?.y ?? 0 }} />
      </DropdownMenuTrigger>
      {menu ? (
        <DropdownMenuContent align="start" aria-label={`Actions: ${menu.title}`} data-testid={testId} onCloseAutoFocus={onCloseAutoFocus}>
          <DropdownMenuLabel>{menu.title}</DropdownMenuLabel>
          {menu.items.length === 0 ? <p className="px-2 py-1 text-12 text-secondary">No action applies to every selected row</p> : null}
          {menu.items.map((item, i) => (
            <Fragment key={item.id}>
              {(item.danger || item.separator) && i > 0 ? <DropdownMenuSeparator /> : null}
              <DropdownMenuItem
                className={item.danger ? "text-danger" : undefined}
                disabled={!!item.disabledNote || !!item.disabled}
                title={item.disabledNote}
                onSelect={() => onRun(item.id)}
              >
                {item.label}
                {item.disabledNote ? <span className="ml-2 text-12 text-secondary">{item.disabledNote}</span> : null}
              </DropdownMenuItem>
            </Fragment>
          ))}
        </DropdownMenuContent>
      ) : null}
    </DropdownMenu>
  );
}
