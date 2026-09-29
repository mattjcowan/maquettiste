import { Tabs as TabsPrimitive } from "radix-ui";
import type { ComponentPropsWithoutRef } from "react";
import { cn } from "@/lib/cn";

export const Tabs = TabsPrimitive.Root;

export function TabsList({ className, ...props }: ComponentPropsWithoutRef<typeof TabsPrimitive.List>) {
  return <TabsPrimitive.List className={cn("flex h-7 items-end gap-1 border-b border-default px-2", className)} {...props} />;
}

export function TabsTrigger({ className, ...props }: ComponentPropsWithoutRef<typeof TabsPrimitive.Trigger>) {
  return (
    <TabsPrimitive.Trigger
      className={cn(
        "-mb-px inline-flex h-7 items-center gap-1.5 border-b-2 border-transparent px-2 text-13 font-medium text-secondary hover:text-primary focus-visible:outline-2 focus-visible:outline-accent data-[state=active]:border-accent data-[state=active]:text-primary data-[disabled]:cursor-not-allowed data-[disabled]:opacity-50 [&_svg]:size-4",
        className,
      )}
      {...props}
    />
  );
}

export function TabsContent({ className, ...props }: ComponentPropsWithoutRef<typeof TabsPrimitive.Content>) {
  return (
    <TabsPrimitive.Content
      className={cn("min-h-0 flex-1 focus-visible:outline-2 focus-visible:-outline-offset-2 focus-visible:outline-accent", className)}
      {...props}
    />
  );
}
