import type { SelectHTMLAttributes } from 'react';
import { cn } from '../lib/cn.js';

export interface SelectProps extends SelectHTMLAttributes<HTMLSelectElement> {}

/**
 * A native select.
 *
 * Deliberately not a Radix listbox. For a plain list of filter options the
 * native control is better on every axis that matters here: it is keyboard
 * accessible for free, it renders as the platform picker on a handheld, and it
 * costs nothing in bundle size. Radix earns its place where the native element
 * genuinely cannot do the job — menus, dialogs, comboboxes — not everywhere.
 */
export function Select({ className, children, ...props }: SelectProps) {
  return (
    <div className="relative inline-flex">
      <select
        className={cn(
          'appearance-none w-full',
          'rounded-[var(--radius-control)] border border-[var(--color-border-default)]',
          'bg-[var(--color-surface-raised)] text-[var(--color-text-primary)]',
          'h-[var(--control-height)] pl-[var(--space-3)] pr-[var(--space-6)]',
          'text-[length:var(--control-text)]',
          'transition-colors duration-[var(--duration-fast)]',
          'hover:border-[var(--color-border-strong)]',
          'focus:outline-2 focus:outline-[var(--color-accent)] focus:-outline-offset-1',
          'disabled:opacity-45',
          className,
        )}
        {...props}
      >
        {children}
      </select>
      <svg
        aria-hidden="true"
        viewBox="0 0 12 12"
        className="pointer-events-none absolute right-[var(--space-3)] top-1/2 h-3 w-3 -translate-y-1/2 text-[var(--color-text-tertiary)]"
      >
        <path d="M3 4.5 6 7.5 9 4.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
      </svg>
    </div>
  );
}
