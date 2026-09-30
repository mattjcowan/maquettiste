import { Slot, Tooltip as TooltipPrimitive } from "radix-ui";
import type { ReactNode } from "react";

export const TooltipProvider = TooltipPrimitive.Provider;

/**
 * A tooltip over its child. Text content is a native tooltip (the title attribute, the owner's rule: no custom
 * tooltip layer for a plain label; a title already on the child wins); rich content (a card) keeps the floating layer.
 */
export function Tooltip({ content, children, side = "bottom" }: { content: ReactNode; children: ReactNode; side?: "top" | "right" | "bottom" | "left" }) {
  if (typeof content === "string") return <Slot.Root title={content}>{children}</Slot.Root>;
  return (
    <TooltipPrimitive.Root delayDuration={400}>
      <TooltipPrimitive.Trigger asChild>{children}</TooltipPrimitive.Trigger>
      <TooltipPrimitive.Portal>
        <TooltipPrimitive.Content
          side={side}
          sideOffset={6}
          className="z-50 rounded-control border border-default bg-raised px-2 py-1 text-12 text-primary shadow-float"
        >
          {content}
        </TooltipPrimitive.Content>
      </TooltipPrimitive.Portal>
    </TooltipPrimitive.Root>
  );
}
