import { currentLocale, formatNumber, useTranslation } from '@countflow/i18n';
import { InfoHelp } from '@countflow/ui';
import type { ReactNode } from 'react';

export function PageHead({
  title,
  count,
  help,
  children,
}: {
  title: string;
  count?: number | undefined;
  help?: ReactNode;
  children?: ReactNode;
}) {
  const { t } = useTranslation();

  return (
    <div className="mb-[var(--space-4)] flex items-baseline justify-between gap-[var(--space-4)]">
      <div className="flex min-w-0 items-center gap-[var(--space-1)]">
        <h2 className="m-0 text-[length:var(--text-xl)]">{title}</h2>
        {help ? <InfoHelp label={t('common.about', { topic: title })}>{help}</InfoHelp> : null}
      </div>
      <div className="flex items-center gap-[var(--space-4)]">
        {children}
        {count === undefined ? null : (
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)] tabular-nums">
            {/* A count is a value, so it is grouped for the reader's locale —
                1.234 in Turkish, 1,234 in English. */}
            {t('common.total', { total: formatNumber(count, currentLocale()) })}
          </span>
        )}
      </div>
    </div>
  );
}
