import type { HTMLAttributes, ReactNode } from "react";
import { cn } from "@/lib/cn";

export function Badge({
  className,
  tone = "neutral",
  ...props
}: HTMLAttributes<HTMLSpanElement> & { tone?: "neutral" | "accent" | "danger" | "warning" | "success" }) {
  return (
    <span
      className={cn(
        "inline-flex h-5 items-center gap-1 whitespace-nowrap rounded-control border px-1.5 text-11 font-medium",
        tone === "neutral" && "border-default bg-app text-secondary",
        tone === "accent" && "border-accent bg-accent-subtle text-primary",
        tone === "danger" && "border-danger bg-surface text-danger",
        tone === "warning" && "border-warning bg-surface text-warning",
        tone === "success" && "border-success bg-surface text-success",
        className,
      )}
      {...props}
    />
  );
}

export function Kbd({ children }: { children: ReactNode }) {
  return <kbd className="rounded-[4px] border border-default bg-app px-1 font-mono text-11 text-secondary">{children}</kbd>;
}

export function Spinner({ label = "Loading" }: { label?: string }) {
  return (
    <span role="status" className="inline-flex items-center gap-2 text-12 text-secondary">
      <span className="size-3 animate-spin rounded-full border-2 border-default border-t-accent motion-reduce:animate-none" aria-hidden />
      {label}
    </span>
  );
}

export function EmptyState({ title, children, className }: { title: string; children?: ReactNode; className?: string }) {
  return (
    <div className={cn("flex h-full min-h-24 flex-col items-center justify-center gap-1 p-6 text-center", className)}>
      <p className="text-13 font-medium text-primary">{title}</p>
      {children ? <div className="max-w-md text-12 text-secondary">{children}</div> : null}
    </div>
  );
}

export function SectionTitle({ children, actions }: { children: ReactNode; actions?: ReactNode }) {
  return (
    <div className="flex h-8 items-center justify-between gap-2">
      <h3 className="text-11 font-semibold uppercase tracking-wide text-secondary">{children}</h3>
      {actions}
    </div>
  );
}

export function Toolbar({ children, className, label }: { children: ReactNode; className?: string; label: string }) {
  return (
    <div
      role="toolbar"
      aria-label={label}
      className={cn("flex min-h-11 shrink-0 flex-wrap items-center gap-2 border-b border-default bg-surface px-3 py-1.5 [&>*]:shrink-0", className)}
    >
      {children}
    </div>
  );
}
