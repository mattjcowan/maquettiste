import { forwardRef, type InputHTMLAttributes, type SelectHTMLAttributes, type TextareaHTMLAttributes } from "react";
import { cn } from "@/lib/cn";
import { useReadOnly } from "./readOnly";

export const controlClass =
  "h-7 w-full min-w-0 rounded-control border border-input bg-surface px-2 text-13 text-primary placeholder:text-secondary focus-visible:outline-2 focus-visible:outline-offset-0 focus-visible:outline-accent disabled:opacity-60 aria-[invalid=true]:border-danger";

// In a read-only region (a snapshot shown as of) a text field takes readOnly, except a search or filter field.
export const Input = forwardRef<HTMLInputElement, InputHTMLAttributes<HTMLInputElement>>(({ className, ...props }, ref) => {
  const regionReadOnly = useReadOnly() && props.type !== "search";
  return <input ref={ref} className={cn(controlClass, className)} {...props} readOnly={props.readOnly || regionReadOnly || undefined} />;
});
Input.displayName = "Input";

export const Textarea = forwardRef<HTMLTextAreaElement, TextareaHTMLAttributes<HTMLTextAreaElement>>(({ className, ...props }, ref) => {
  const regionReadOnly = useReadOnly();
  return (
    <textarea ref={ref} className={cn(controlClass, "h-auto min-h-16 py-1.5", className)} {...props} readOnly={props.readOnly || regionReadOnly || undefined} />
  );
});
Textarea.displayName = "Textarea";

/** A native select: fully keyboard accessible and usable inside grids. */
export const Select = forwardRef<HTMLSelectElement, SelectHTMLAttributes<HTMLSelectElement>>(({ className, ...props }, ref) => (
  <select ref={ref} className={cn(controlClass, "pr-6", className)} {...props} />
));
Select.displayName = "Select";

export function Field({
  label,
  htmlFor,
  hint,
  children,
  className,
}: {
  label: string;
  htmlFor?: string;
  hint?: string;
  children: React.ReactNode;
  className?: string;
}) {
  return (
    <div className={cn("flex flex-col gap-1", className)}>
      <label htmlFor={htmlFor} className="text-12 font-medium text-secondary">
        {label}
      </label>
      {children}
      {hint ? <p className="text-11 text-secondary">{hint}</p> : null}
    </div>
  );
}
