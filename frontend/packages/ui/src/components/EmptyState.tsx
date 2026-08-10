import type { ReactNode } from 'react';
import { cn } from '../lib/cn.js';

/**
 * Shown when a query returns nothing.
 *
 * Says what is absent and what to do about it. An empty table with no
 * explanation is indistinguishable from a broken one, and on a warehouse floor
 * that ambiguity costs a phone call.
 */
export function EmptyState({
  title,
  hint,
  action,
  className,
}: {
  title: string;
  hint?: string;
  action?: ReactNode;
  className?: string;
}) {
  return (
    <div
      className={cn(
        'flex flex-col items-center justify-center gap-[var(--space-2)]',
        'px-[var(--space-5)] py-[var(--space-7)] text-center',
        className,
      )}
    >
      <p className="font-medium text-[var(--color-text-secondary)]">{title}</p>
      {hint ? (
        <p className="max-w-[46ch] text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {hint}
        </p>
      ) : null}
      {action ? <div className="mt-[var(--space-2)]">{action}</div> : null}
    </div>
  );
}
