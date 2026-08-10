import { cn } from '../lib/cn.js';

/**
 * An indeterminate progress indicator.
 *
 * Respects reduced motion by falling back to a static ring rather than
 * disappearing — the affordance still needs to say "working", it just stops
 * spinning.
 */
export function Spinner({ className, label }: { className?: string; label?: string }) {
  return (
    <span
      role="status"
      aria-label={label ?? 'Loading'}
      className={cn('inline-flex items-center gap-[var(--space-2)]', className)}
    >
      <svg viewBox="0 0 16 16" className="h-4 w-4 motion-safe:animate-spin" aria-hidden="true">
        <circle
          cx="8"
          cy="8"
          r="6.5"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          className="opacity-20"
        />
        <path
          d="M8 1.5A6.5 6.5 0 0 1 14.5 8"
          fill="none"
          stroke="currentColor"
          strokeWidth="2"
          strokeLinecap="round"
        />
      </svg>
      {label ? <span className="text-[var(--color-text-tertiary)]">{label}</span> : null}
    </span>
  );
}
