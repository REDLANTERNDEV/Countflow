import { useTranslation } from '@countflow/i18n';
import { Badge, Code } from '@countflow/ui';
import { Link } from '@tanstack/react-router';
import { PageHead } from '../components/PageHead.js';

/**
 * What this system is, for somebody who arrived at a URL with no walkthrough.
 *
 * Written as a route rather than as a README because the demo's whole promise is
 * that the application explains itself — a hosted instance somebody has to read a
 * repository to understand has not been demonstrated, it has been deployed. Every
 * stop below links to the screen it describes, so the tour is a way *into* the
 * product rather than a page about it.
 */
export function TourPage() {
  const { t } = useTranslation();

  const stops = [
    { to: '/ledger', key: 'ledger', pillar: true },
    { to: '/rules', key: 'rules', pillar: true },
    { to: '/replay', key: 'replay', pillar: true },
    { to: '/inbound', key: 'inbound', pillar: false },
    { to: '/outbound', key: 'outbound', pillar: false },
    { to: '/counting', key: 'counting', pillar: false },
    { to: '/accuracy', key: 'accuracy', pillar: false },
    { to: '/integrations', key: 'integrations', pillar: false },
  ] as const;

  return (
    <>
      <PageHead help={t('tour.intro')} title={t('tour.title')} />

      <section className="mb-[var(--space-6)] grid gap-[var(--space-4)] [grid-template-columns:repeat(auto-fill,minmax(320px,1fr))]">
        {stops.map((stop) => (
          <Link
            key={stop.key}
            to={stop.to}
            className="flex flex-col gap-[var(--space-2)] rounded-[var(--radius-surface)]
                       border border-[var(--color-border-subtle)] bg-[var(--color-surface-raised)]
                       p-[var(--space-4)] no-underline
                       transition-colors duration-[var(--duration-fast)]
                       hover:border-[var(--color-accent)]"
          >
            <div className="flex items-baseline gap-[var(--space-2)]">
              <h3 className="m-0 text-[length:var(--text-lg)] text-[var(--color-text-primary)]">
                {t(`tour.stops.${stop.key}.title`)}
              </h3>
              {/* The three pillars are marked, because the difference between
                  "this product has a stock ledger" and "this product is a stock
                  ledger" is the whole of what makes it worth choosing. */}
              {stop.pillar ? <Badge tone="info">{t('tour.pillar')}</Badge> : null}
            </div>
            <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
              {t(`tour.stops.${stop.key}.body`)}
            </p>
          </Link>
        ))}
      </section>

      <section
        className="max-w-[72ch] rounded-[var(--radius-surface)]
                   border border-[var(--color-border-subtle)] p-[var(--space-4)]"
      >
        <h3 className="mb-[var(--space-2)] mt-0 text-[length:var(--text-base)]">
          {t('tour.integrate.title')}
        </h3>
        <p className="mb-[var(--space-3)] mt-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('tour.integrate.body')}
        </p>
        <Code className="text-[length:var(--text-xs)]">GET /api/v1/inventory/changes?afterSequence=0</Code>
      </section>
    </>
  );
}
