import { Popover as PopoverPrimitive } from "radix-ui";
import type { ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/cn";
import { POPOVER_MARGIN } from "@/ui/usePopoverPlacement";

export const Popover = PopoverPrimitive.Root;
export const PopoverTrigger = PopoverPrimitive.Trigger;

export function PopoverContent({ className, ...props }: ComponentPropsWithoutRef<typeof PopoverPrimitive.Content>) {
  return (
    <PopoverPrimitive.Portal>
      <PopoverPrimitive.Content
        sideOffset={6}
        collisionPadding={POPOVER_MARGIN}
        className={cn(
          "mq-scroll z-50 max-h-(--radix-popover-content-available-height) overflow-y-auto rounded-panel border border-default bg-raised p-2 text-12 text-primary shadow-float",
          className,
        )}
        {...props}
      />
    </PopoverPrimitive.Portal>
  );
}
