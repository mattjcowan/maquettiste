import { forwardRef, type ButtonHTMLAttributes } from "react";
import { cva, type VariantProps } from "class-variance-authority";
import { cn } from "@/lib/cn";

export const buttonVariants = cva(
  "inline-flex items-center justify-center gap-1.5 whitespace-nowrap rounded-control font-medium mq-transition transition-colors disabled:pointer-events-none disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-1 focus-visible:outline-accent [&_svg]:size-4 [&_svg]:shrink-0",
  {
    variants: {
      variant: {
        primary: "bg-accent text-accent-foreground hover:opacity-90",
        secondary: "border border-input bg-surface text-primary hover:bg-accent-subtle",
        ghost: "text-primary hover:bg-accent-subtle",
        danger: "border border-danger bg-surface text-danger hover:bg-accent-subtle",
        link: "text-accent underline-offset-2 hover:underline",
      },
      size: {
        sm: "h-6 px-2 text-12",
        md: "h-7 px-2.5 text-12",
        icon: "size-7 text-12",
        "icon-sm": "size-7 text-12",
      },
    },
    defaultVariants: { variant: "secondary", size: "md" },
  },
);

/** The tooltip text of an icon-only control: its label, then its keyboard shortcut in parentheses. */
export const iconTitle = (label: string, shortcut?: string) => (shortcut ? `${label} (${shortcut})` : label);

/**
 * The owner's rule: a control whose visible content is an icon only shows a native tooltip (the title attribute)
 * and exposes the same text to assistive technology. Spread onto any icon-only element that is not a Button (rail
 * links, tab close buttons, menu triggers); an explicit title or aria-label on the element still wins.
 */
export const iconLabel = (label: string, shortcut?: string) => ({ title: iconTitle(label, shortcut), "aria-label": label });

type IconSize = "icon" | "icon-sm";

interface ButtonBaseProps extends ButtonHTMLAttributes<HTMLButtonElement>, Omit<VariantProps<typeof buttonVariants>, "size"> {
  /** The keyboard shortcut shown at the end of the tooltip, as "Collapse sidebar (Ctrl+B)". */
  shortcut?: string;
}

/**
 * An icon-sized button must name itself: `label` becomes its title (the tooltip) and its aria-label. Other sizes
 * may pass a label too when their content is an icon only.
 */
export type ButtonProps = ButtonBaseProps &
  ({ size: IconSize; label: string } | { size?: Exclude<VariantProps<typeof buttonVariants>["size"], IconSize>; label?: string });

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(({ className, variant, size, type, label, shortcut, title, ...props }, ref) => (
  <button
    ref={ref}
    type={type ?? "button"}
    className={cn(buttonVariants({ variant, size }), className)}
    {...props}
    title={title ?? (label ? iconTitle(label, shortcut) : undefined)}
    aria-label={props["aria-label"] ?? label}
  />
));
Button.displayName = "Button";
