import type { ComponentPropsWithRef } from 'react';
import { cn } from '../lib/cn.js';

// `ComponentPropsWithRef` rather than `InputHTMLAttributes` so callers can hold a
// ref: React 19 passes one through as an ordinary prop, but the attribute types
// alone do not declare it, and moving focus between fields needs it.
export interface InputProps extends ComponentPropsWithRef<'input'> {
  /** Renders in the monospace face. Use for codes, GTINs, SKUs — never for prose. */
  mono?: boolean;
}

export function Input({ className, mono = false, ...props }: InputProps) {
  return (
    <input
      className={cn(
        'w-full rounded-[var(--radius-control)] border border-[var(--color-border-default)]',
        'bg-[var(--color-surface-sunken)] text-[var(--color-text-primary)]',
        'h-[var(--control-height)] px-[var(--space-3)] text-[length:var(--control-text)]',
        'placeholder:text-[var(--color-text-tertiary)]',
        'transition-colors duration-[var(--duration-fast)]',
        'hover:border-[var(--color-border-strong)]',
        'focus:outline-2 focus:outline-[var(--color-accent)] focus:-outline-offset-1',
        'disabled:opacity-45 disabled:cursor-not-allowed',
        mono && 'font-[family-name:var(--font-mono)] tabular-nums',
        className,
      )}
      {...props}
    />
  );
}

export function Label({
  className,
  ...props
}: React.LabelHTMLAttributes<HTMLLabelElement>) {
  return (
    <label
      className={cn(
        'block text-[length:var(--label-text)] font-medium',
        'text-[var(--color-text-secondary)]',
        'tracking-[var(--tracking-caption)]',
        className,
      )}
      {...props}
    />
  );
}

export function Field({
  label,
  htmlFor,
  hint,
  error,
  children,
}: {
  label: string;
  htmlFor?: string;
  hint?: string;
  error?: string;
  children: React.ReactNode;
}) {
  return (
    <div className="flex flex-col gap-[var(--space-2)]">
      <Label htmlFor={htmlFor}>{label}</Label>
      {children}
      {/* Validation sits next to the control it belongs to, not collected at the
          bottom of the form — proximity is what makes the mapping obvious. */}
      {error ? (
        <p className="text-[length:var(--label-text)] text-[var(--color-danger)]">{error}</p>
      ) : hint ? (
        <p className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">{hint}</p>
      ) : null}
    </div>
  );
}
