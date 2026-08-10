import { currentLocale, formatNumber, useTranslation } from '@countflow/i18n';
import type { ReactNode } from 'react';

export function PageHead({
  title,
  count,
  children,
}: {
  title: string;
  count?: number | undefined;
  children?: ReactNode;
}) {
  const { t } = useTranslation();

  return (
    <div className="mb-[var(--space-4)] flex items-baseline justify-between gap-[var(--space-4)]">
      <h2 className="m-0 text-[length:var(--text-xl)]">{title}</h2>
      <div className="flex items-center gap-[var(--space-4)]">
        {children}
        {count === undefined ? null : (
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)] tabular-nums">
            {/* A count is a value, so it is grouped for the reader's locale —
                1.234 in Turkish, 1,234 in English. */}
            {t('common.total', { count: formatNumber(count, currentLocale()) as unknown as number })}
          </span>
        )}
      </div>
    </div>
  );
}
