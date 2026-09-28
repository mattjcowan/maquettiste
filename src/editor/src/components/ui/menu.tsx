import { DropdownMenu as Menu } from "radix-ui";
import { Check } from "lucide-react";
import type { ComponentPropsWithoutRef, ReactNode } from "react";
import { cn } from "@/lib/cn";

export const DropdownMenu = Menu.Root;
export const DropdownMenuTrigger = Menu.Trigger;

export function DropdownMenuContent({ children, className, ...props }: ComponentPropsWithoutRef<typeof Menu.Content>) {
  return (
    <Menu.Portal>
      <Menu.Content
        sideOffset={4}
        className={cn("z-50 min-w-44 rounded-panel border border-default bg-raised p-1 text-13 text-primary shadow-float", className)}
        {...props}
      >
        {children}
      </Menu.Content>
    </Menu.Portal>
  );
}

const itemClass =
  "relative flex h-7 cursor-default select-none items-center gap-2 rounded-control px-2 outline-none data-[highlighted]:bg-accent-subtle data-[disabled]:opacity-50 [&_svg]:size-4";

export function DropdownMenuItem({ className, ...props }: ComponentPropsWithoutRef<typeof Menu.Item>) {
  return <Menu.Item className={cn(itemClass, className)} {...props} />;
}

export function DropdownMenuCheckboxItem({ children, className, ...props }: ComponentPropsWithoutRef<typeof Menu.CheckboxItem>) {
  return (
    <Menu.CheckboxItem className={cn(itemClass, "pl-7", className)} onSelect={(e) => e.preventDefault()} {...props}>
      <Menu.ItemIndicator className="absolute left-2">
        <Check />
      </Menu.ItemIndicator>
      {children}
    </Menu.CheckboxItem>
  );
}

export function DropdownMenuRadioGroup(props: ComponentPropsWithoutRef<typeof Menu.RadioGroup>) {
  return <Menu.RadioGroup {...props} />;
}

export function DropdownMenuRadioItem({ children, className, ...props }: ComponentPropsWithoutRef<typeof Menu.RadioItem>) {
  return (
    <Menu.RadioItem className={cn(itemClass, "pl-7", className)} {...props}>
      <Menu.ItemIndicator className="absolute left-2">
        <Check />
      </Menu.ItemIndicator>
      {children}
    </Menu.RadioItem>
  );
}

export function DropdownMenuLabel({ children }: { children: ReactNode }) {
  return <Menu.Label className="px-2 py-1 text-11 font-semibold uppercase tracking-wide text-secondary">{children}</Menu.Label>;
}

export function DropdownMenuSeparator() {
  return <Menu.Separator className="my-1 h-px bg-default" />;
}
