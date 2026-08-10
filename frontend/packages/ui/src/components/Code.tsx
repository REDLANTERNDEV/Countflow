import type { HTMLAttributes } from 'react';
import { cn } from '../lib/cn.js';

/**
 * An identifier: a location code, GTIN, SSCC, SKU or lot number.
 *
 * Deliberately its own component rather than a utility class, because the rule
 * it enforces is easy to get wrong and expensive when you do. These are
 * identifiers that happen to contain digits — they are never grouped, never
 * locale-formatted, and always monospace. A GTIN rendered through a number
 * formatter under a Turkish locale becomes `5.098.760.010.303`, which is not a
 * barcode and will not scan.
 */
export function Code({ className, ...props }: HTMLAttributes<HTMLSpanElement>) {
  return (
    <span
      className={cn(
        'font-[family-name:var(--font-mono)] tabular-nums',
        'text-[length:var(--text-sm)] tracking-[var(--tracking-body)]',
        className,
      )}
      {...props}
    />
  );
}

/**
 * A quantity, weight or other measured value.
 *
 * The counterpart to {@link Code}: these *are* numbers and should be formatted
 * for the reader's locale. Tabular figures keep a column comparable at a glance,
 * which is the entire point of a quantity column.
 */
export function Numeric({
  value,
  fractionDigits,
  locale,
  className,
  ...props
}: HTMLAttributes<HTMLSpanElement> & {
  value: number | null | undefined;
  fractionDigits?: number;
  locale?: string;
}) {
  const formatted =
    value === null || value === undefined
      ? '—'
      : new Intl.NumberFormat(locale, {
          minimumFractionDigits: fractionDigits ?? 0,
          maximumFractionDigits: fractionDigits ?? 3,
        }).format(value);

  return (
    <span className={cn('tabular-nums', className)} {...props}>
      {formatted}
    </span>
  );
}
