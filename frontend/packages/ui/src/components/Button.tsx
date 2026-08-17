import { Slot } from '@radix-ui/react-slot';
import { cva, type VariantProps } from 'class-variance-authority';
import type { ButtonHTMLAttributes } from 'react';
import { cn } from '../lib/cn.js';

const button = cva(
  [
    'inline-flex items-center justify-center gap-[var(--space-3)] whitespace-nowrap',
    'rounded-[var(--radius-control)] font-medium select-none',
    'transition-[background-color,border-color,color,transform]',
    'duration-[var(--duration-fast)] ease-[var(--ease-standard)]',
    'disabled:pointer-events-none disabled:opacity-45',
    // Feedback on press, not on release. A control that waits for pointer-up to
    // acknowledge feels dead, and an operator who thinks a tap missed will tap
    // again — which against an append-only ledger is a correctness problem.
    'active:scale-[0.985]',
    '[&_svg]:pointer-events-none [&_svg]:shrink-0',
  ],
  {
    variants: {
      variant: {
        // A filled variant takes the `-on` token, never the `-text` one. The
        // `-text` values are terracotta and red darkened (or lightened) to be
        // legible *on a surface*; putting one on its own fill is 1.34:1 in light
        // and 1.50:1 in dark, which is how the primary action shipped unreadable
        // in both themes. See theme.css for the arithmetic.
        primary:
          'bg-[var(--color-accent)] text-[var(--color-accent-on)] hover:bg-[var(--color-accent-hover)]',
        secondary:
          'bg-[var(--color-surface-raised)] text-[var(--color-text-primary)] border border-[var(--color-border-default)] hover:bg-[var(--color-surface-hover)] hover:border-[var(--color-border-strong)]',
        ghost:
          'bg-transparent text-[var(--color-text-secondary)] hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text-primary)]',
        // An explicit hover fill rather than `brightness-110`: a filter lightens
        // the fill under a white label, so the one state where the pointer is
        // definitely on a destructive button was its least readable one.
        danger:
          'bg-[var(--color-danger-fill)] text-[var(--color-danger-on)] hover:bg-[var(--color-danger-fill-hover)]',
      },
      size: {
        // Heights come from the density scale, so the same component is 30px in
        // the console and 52px on a handheld without a second definition.
        md: 'h-[var(--control-height)] px-[var(--space-4)] text-[length:var(--control-text)]',
        sm: 'h-[var(--control-height-sm)] px-[var(--space-3)] text-[length:var(--control-text-sm)]',
        icon: 'h-[var(--control-height)] w-[var(--control-height)] p-0',
      },
    },
    defaultVariants: { variant: 'secondary', size: 'md' },
  },
);

export interface ButtonProps
  extends ButtonHTMLAttributes<HTMLButtonElement>,
    VariantProps<typeof button> {
  /** Render as the child element instead of a `<button>` — for links styled as buttons. */
  asChild?: boolean;
}

export function Button({ className, variant, size, asChild = false, ...props }: ButtonProps) {
  const Component = asChild ? Slot : 'button';

  return (
    <Component
      className={cn(button({ variant, size }), className)}
      // Buttons inside a form default to submit, which silently submits forms
      // they were never meant to. Explicit unless the caller says otherwise.
      {...(asChild ? {} : { type: props.type ?? 'button' })}
      {...props}
    />
  );
}

export { button as buttonVariants };
