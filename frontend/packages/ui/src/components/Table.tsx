import type {
  HTMLAttributes,
  TdHTMLAttributes,
  ThHTMLAttributes,
} from 'react';
import { cn } from '../lib/cn.js';

/**
 * Table primitives tuned for operational density.
 *
 * A supervisor reviewing 2,245 locations needs rows, not cards. The craft here
 * is making that density calm: a sticky header that stays legible while the
 * body scrolls under it, alignment chosen by data type, and tabular figures so
 * a column of quantities can be scanned for the odd one out.
 */

export function TableContainer({ className, ...props }: HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn(
        'relative overflow-auto',
        'rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]',
        className,
      )}
      {...props}
    />
  );
}

export function Table({ className, ...props }: HTMLAttributes<HTMLTableElement>) {
  return (
    <table
      className={cn('w-full border-collapse text-[length:var(--text-sm)]', className)}
      {...props}
    />
  );
}

export function THead({ className, ...props }: HTMLAttributes<HTMLTableSectionElement>) {
  return (
    <thead
      className={cn(
        // Translucent rather than opaque, so rows visibly pass beneath it —
        // the header reads as a floating layer over the data instead of a lid
        // sitting on top of it.
        'sticky top-0 z-10',
        'bg-[color-mix(in_srgb,var(--color-surface-base)_88%,transparent)]',
        'backdrop-blur-[8px]',
        className,
      )}
      {...props}
    />
  );
}

export function TH({
  className,
  numeric = false,
  ...props
}: ThHTMLAttributes<HTMLTableCellElement> & { numeric?: boolean }) {
  return (
    <th
      className={cn(
        'px-[var(--space-4)] py-[var(--space-3)]',
        'text-left align-middle font-semibold',
        'text-[length:var(--text-xs)] uppercase',
        'tracking-[0.05em] text-[var(--color-text-tertiary)]',
        'border-b border-[var(--color-border-default)]',
        'whitespace-nowrap',
        numeric && 'text-right',
        className,
      )}
      {...props}
    />
  );
}

export function TBody({ className, ...props }: HTMLAttributes<HTMLTableSectionElement>) {
  return <tbody className={cn(className)} {...props} />;
}

export function TR({
  className,
  interactive = false,
  ...props
}: HTMLAttributes<HTMLTableRowElement> & { interactive?: boolean }) {
  return (
    <tr
      className={cn(
        'border-b border-[var(--color-border-subtle)] last:border-b-0',
        interactive &&
          'cursor-pointer hover:bg-[var(--color-surface-hover)] focus-visible:bg-[var(--color-surface-hover)]',
        className,
      )}
      {...props}
    />
  );
}

export function TD({
  className,
  numeric = false,
  ...props
}: TdHTMLAttributes<HTMLTableCellElement> & { numeric?: boolean }) {
  return (
    <td
      className={cn(
        'px-[var(--space-4)]',
        'h-[var(--row-height)] align-middle',
        'text-[var(--color-text-primary)]',
        // Right-aligned so digits line up and magnitudes are comparable down
        // the column. Left-aligned numbers defeat the purpose of the column.
        numeric && 'text-right tabular-nums',
        className,
      )}
      {...props}
    />
  );
}

export function TableEmpty({
  colSpan,
  children,
}: {
  colSpan: number;
  children: React.ReactNode;
}) {
  return (
    <tr>
      <td
        colSpan={colSpan}
        className="px-[var(--space-4)] py-[var(--space-7)] text-center text-[var(--color-text-tertiary)]"
      >
        {children}
      </td>
    </tr>
  );
}
