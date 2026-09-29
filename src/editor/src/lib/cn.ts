import { clsx, type ClassValue } from "clsx";
import { extendTailwindMerge } from "tailwind-merge";

// The theme's font sizes (`--text-11` to `--text-24` in index.css): without them tailwind-merge reads `text-12` as a
// colour and drops a button's `text-accent-foreground` when a size adds `text-12`.
const twMerge = extendTailwindMerge({
  extend: { theme: { text: ["11", "12", "13", "14", "16", "20", "24"] } },
});

/** Joins class names and lets later Tailwind utilities win over earlier ones. */
export function cn(...inputs: ClassValue[]): string {
  return twMerge(clsx(inputs));
}
