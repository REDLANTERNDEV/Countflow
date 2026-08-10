import { cva, type VariantProps } from 'class-variance-authority';
import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn.js';

/**
 * A small state marker.
 *
 * Every tone here means something operational — expired, blocked, quarantined,
 * a count variance. There is deliberately no "brand" or decorative tone: if
 * badges are used for flavour, an operator stops reading them as signal.
 */
const badge = cva(
  [
    'inline-flex items-center gap-[var(--space-1)]',
    'rounded-[var(--radius-pill)] border',
    'px-[var(--space-3)] py-[1px]',
    'text-[length:var(--text-xs)] font-semibold leading-[1.4]',
    'whitespace-nowrap',
  ],
  {
    variants: {
      tone: {
        neutral:
          'bg-[var(--color-surface-raised)] border-[var(--color-border-default)] text-[var(--color-text-secondary)]',
        success:
          'bg-[var(--color-success-muted)] border-transparent text-[var(--color-success)]',
        warning:
          'bg-[var(--color-warning-muted)] border-transparent text-[var(--color-warning)]',
        danger:
          'bg-[var(--color-danger-muted)] border-transparent text-[var(--color-danger)]',
        info: 'bg-[var(--color-info-muted)] border-transparent text-[var(--color-info)]',
      },
    },
    defaultVariants: { tone: 'neutral' },
  },
);

export interface BadgeProps
  extends HTMLAttributes<HTMLSpanElement>,
    VariantProps<typeof badge> {}

export function Badge({ className, tone, ...props }: BadgeProps) {
  return <span className={cn(badge({ tone }), className)} {...props} />;
}
