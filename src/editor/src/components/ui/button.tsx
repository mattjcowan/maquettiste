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

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement>, VariantProps<typeof buttonVariants> {}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(({ className, variant, size, type, ...props }, ref) => (
  <button ref={ref} type={type ?? "button"} className={cn(buttonVariants({ variant, size }), className)} {...props} />
));
Button.displayName = "Button";
