import { cn } from '../lib/cn.js';

/**
 * The Countflow mark: two interlocking arcs, Espresso over Terracotta.
 *
 * Inlined as SVG rather than an `<img>` so it inherits the current theme —
 * the espresso arc has to become bone on a dark surface, and an image file
 * cannot do that. `currentColor` on the primary arc is what makes it work in
 * both modes from one asset.
 *
 * Geometry matches countflow-mark.svg in the logo pack exactly; only the
 * colour binding differs.
 */
export function LogoSymbol({ className, size = 20 }: { className?: string; size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 152 152"
      role="img"
      aria-label="Countflow"
      className={cn('shrink-0', className)}
    >
      <path
        d="M 71.89 24.13 A 43.00 43.00 0 1 1 71.89 103.87"
        fill="none"
        stroke="currentColor"
        strokeWidth="13"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
      <path
        d="M 80.11 48.13 A 43.00 43.00 0 1 0 80.11 127.87"
        fill="none"
        stroke="var(--color-brand-terracotta)"
        strokeWidth="13"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  );
}

/** The mark plus the wordmark, for headers and the sign-in screen. */
export function Logo({ className, size = 22 }: { className?: string; size?: number }) {
  return (
    <span className={cn('inline-flex items-center gap-[var(--space-3)]', className)}>
      <LogoSymbol size={size} />
      <span
        className="font-semibold tracking-[var(--tracking-heading)]"
        style={{ fontSize: size * 0.82 }}
      >
        Countflow
      </span>
    </span>
  );
}
