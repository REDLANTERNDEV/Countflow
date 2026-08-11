import type { components } from '@countflow/api-client';
import { currentLocale, formatDateTime, formatNumber, useTranslation } from '@countflow/i18n';
import {
  Badge,
  Button,
  Code,
  EmptyState,
  Numeric,
  Select,
  Spinner,
  Table,
  TableContainer,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@countflow/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

type AccuracySlice = components['schemas']['AccuracySlice'];

/**
 * The screen the product's central claim is made on.
 *
 * Four panels from three modules, fetched separately and composed here rather
 * than joined on the server. That is not a shortcut — count accuracy and
 * stranded commitments belong to Inventory, override rates belong to Work, and
 * an endpoint that returned all three would have to reach across two schemas
 * (ADR 0002). Three parallel fetches is the honest price of the boundary.
 *
 * The reconciliation panel reads what the reconciler produced rather than
 * recomputing drift. Two answers to "is the stock right" is exactly the problem
 * the reconciler exists to prevent, and a dashboard is the obvious place for the
 * second one to creep in.
 */
export function AccuracyPage() {
  const { t } = useTranslation();
  const locale = currentLocale();
  const queryClient = useQueryClient();

  const [warehouseId, setWarehouseId] = useState('');

  const warehouses = useQuery({
    queryKey: ['warehouses'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/warehouses');
      if (error) throw error;
      return data;
    },
  });

  // The first warehouse until somebody chooses otherwise. A dashboard that
  // opened on an empty selector would make the reader do work before it told
  // them anything.
  const selected = warehouseId || warehouses.data?.[0]?.id || '';

  const accuracy = useQuery({
    queryKey: ['accuracy', selected],
    enabled: selected !== '',
    queryFn: async () => {
      const { data, error } = await api.GET('/api/inventory/accuracy', {
        params: { query: { warehouseId: selected } },
      });
      if (error) throw error;
      return data;
    },
  });

  const overrides = useQuery({
    queryKey: ['work-statistics', selected],
    enabled: selected !== '',
    queryFn: async () => {
      const { data, error } = await api.GET('/api/work/statistics', {
        params: { query: { warehouseId: selected } },
      });
      if (error) throw error;
      return data;
    },
  });

  const drift = useQuery({
    queryKey: ['reconciliation-history'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/inventory/reconciliation/history', {
        params: { query: { limit: 20 } },
      });
      if (error) throw error;
      return data;
    },
  });

  const stranded = useQuery({
    queryKey: ['stranded-reservations'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/inventory/reservations/stranded');
      if (error) throw error;
      return data;
    },
  });

  const reconcile = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.POST('/api/inventory/reconciliation');
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['reconciliation-history'] });
    },
  });

  const runs = drift.data ?? [];
  const everClean = runs.length > 0 && runs.every((run) => run.isClean);

  return (
    <>
      <PageHead title={t('accuracy.title')} />

      <p className="mb-[var(--space-4)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {t('accuracy.subtitle')}
      </p>

      <div className="mb-[var(--space-5)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Select value={selected} onChange={(event) => setWarehouseId(event.target.value)}>
          {(warehouses.data ?? []).map((warehouse) => (
            <option key={warehouse.id} value={warehouse.id}>
              {warehouse.code} — {warehouse.name}
            </option>
          ))}
        </Select>
        <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
          {t('accuracy.counts.window')}
        </span>
      </div>

      {/* ---- Count accuracy ---- */}

      <section className="mb-[var(--space-6)]">
        <h2 className="mb-[var(--space-1)] text-[length:var(--text-lg)]">
          {t('accuracy.counts.title')}
        </h2>
        <p className="mb-[var(--space-3)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('accuracy.counts.hint')}
        </p>

        {accuracy.isPending ? <Spinner label={t('common.loading')} /> : null}

        {accuracy.data && accuracy.data.overall.counted === 0 ? (
          <EmptyState title={t('accuracy.counts.empty')} />
        ) : null}

        {accuracy.data && accuracy.data.overall.counted > 0 ? (
          <>
            <div className="mb-[var(--space-4)]">
              <SliceTable
                caption={t('accuracy.counts.overall')}
                slices={[accuracy.data.overall]}
                labelHeading={t('accuracy.counts.slice')}
                locale={locale}
              />
              <p className="mt-[var(--space-2)] max-w-[72ch] text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {t('accuracy.counts.netHint')}
              </p>
            </div>

            <div className="grid gap-[var(--space-5)] lg:grid-cols-2">
              <SliceTable
                caption={t('accuracy.counts.byZone')}
                slices={accuracy.data.byZone}
                labelHeading={t('accuracy.counts.slice')}
                locale={locale}
              />
              <SliceTable
                caption={t('accuracy.counts.byClass')}
                slices={accuracy.data.byClass}
                labelHeading={t('accuracy.counts.slice')}
                locale={locale}
              />
            </div>
          </>
        ) : null}
      </section>

      {/* ---- Override rates ---- */}

      <section className="mb-[var(--space-6)]">
        <h2 className="mb-[var(--space-1)] text-[length:var(--text-lg)]">
          {t('accuracy.overrides.title')}
        </h2>
        <p className="mb-[var(--space-3)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('accuracy.overrides.hint')}
        </p>

        {overrides.isPending ? <Spinner label={t('common.loading')} /> : null}

        {overrides.data?.byKind.length === 0 ? (
          <EmptyState title={t('accuracy.overrides.empty')} />
        ) : null}

        {overrides.data && overrides.data.byKind.length > 0 ? (
          <TableContainer>
            <Table>
              <THead>
                <TR>
                  <TH>{t('accuracy.overrides.kind')}</TH>
                  <TH numeric>{t('accuracy.overrides.completed')}</TH>
                  <TH numeric>{t('accuracy.overrides.overridden')}</TH>
                  <TH numeric>{t('accuracy.overrides.rate')}</TH>
                  <TH numeric>{t('accuracy.overrides.open')}</TH>
                  <TH numeric>{t('accuracy.overrides.cancelled')}</TH>
                </TR>
              </THead>
              <TBody>
                {overrides.data.byKind.map((row) => (
                  <TR key={row.kind}>
                    <TD>{t(`work.kind.${row.kind}`)}</TD>
                    <TD numeric>
                      <Numeric value={row.completed} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Numeric value={row.overridden} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Rate value={row.overrideRate} locale={locale} invert />
                    </TD>
                    <TD numeric>
                      <Numeric value={row.open} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Numeric value={row.cancelled} locale={locale} />
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        ) : null}
      </section>

      {/* ---- Reconciliation ---- */}

      <section className="mb-[var(--space-6)]">
        <div className="mb-[var(--space-1)] flex flex-wrap items-baseline gap-[var(--space-3)]">
          <h2 className="text-[length:var(--text-lg)]">{t('accuracy.drift.title')}</h2>
          <Button size="sm" disabled={reconcile.isPending} onClick={() => reconcile.mutate()}>
            {reconcile.isPending ? t('accuracy.drift.running') : t('accuracy.drift.run')}
          </Button>
        </div>
        <p className="mb-[var(--space-3)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('accuracy.drift.hint')}
        </p>

        {drift.isPending ? <Spinner label={t('common.loading')} /> : null}

        {runs.length === 0 && !drift.isPending ? (
          <EmptyState title={t('accuracy.drift.empty')} />
        ) : null}

        {runs.length > 0 ? (
          <>
            {everClean ? (
              <p className="mb-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                {t('accuracy.drift.allClean')}
              </p>
            ) : null}

            <TableContainer>
              <Table>
                <THead>
                  <TR>
                    <TH>{t('accuracy.drift.runAt')}</TH>
                    <TH>{t('accuracy.drift.trigger')}</TH>
                    <TH numeric>{t('accuracy.drift.movements')}</TH>
                    <TH numeric>{t('accuracy.drift.holdings')}</TH>
                    <TH numeric>{t('accuracy.drift.driftCount')}</TH>
                    <TH numeric>{t('accuracy.drift.worstDrift')}</TH>
                    <TH numeric>{t('accuracy.drift.elapsed')}</TH>
                  </TR>
                </THead>
                <TBody>
                  {runs.map((run) => (
                    <TR key={run.id}>
                      <TD>{formatDateTime(run.runAt, locale)}</TD>
                      <TD>{t(`accuracy.drift.triggerKind.${run.trigger}`)}</TD>
                      <TD numeric>
                        <Numeric value={run.movementsReplayed} locale={locale} />
                      </TD>
                      <TD numeric>
                        <Numeric value={run.holdingsChecked} locale={locale} />
                      </TD>
                      <TD numeric>
                        {run.isClean ? (
                          <Badge tone="success">{t('accuracy.drift.clean')}</Badge>
                        ) : run.ledgerSum !== 0 ? (
                          <Badge tone="danger">
                            {t('accuracy.drift.ledgerImbalance', {
                              sum: formatNumber(run.ledgerSum, locale),
                            })}
                          </Badge>
                        ) : (
                          <Badge tone="danger">
                            <Numeric value={run.driftCount} locale={locale} />
                          </Badge>
                        )}
                      </TD>
                      <TD numeric>
                        <Numeric value={run.worstDrift} locale={locale} />
                      </TD>
                      <TD numeric>
                        <Numeric value={run.elapsedMilliseconds} locale={locale} /> ms
                      </TD>
                    </TR>
                  ))}
                </TBody>
              </Table>
            </TableContainer>
          </>
        ) : null}
      </section>

      {/* ---- Stranded commitments ---- */}

      <section>
        <h2 className="mb-[var(--space-1)] text-[length:var(--text-lg)]">
          {t('accuracy.stranded.title')}
        </h2>
        <p className="mb-[var(--space-3)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('accuracy.stranded.hint')}
        </p>

        {stranded.isPending ? <Spinner label={t('common.loading')} /> : null}

        {stranded.data?.length === 0 ? (
          <EmptyState title={t('accuracy.stranded.empty')} />
        ) : null}

        {stranded.data && stranded.data.length > 0 ? (
          <TableContainer>
            <Table>
              <THead>
                <TR>
                  <TH>{t('accuracy.stranded.order')}</TH>
                  <TH>{t('outbound.allocationLocation')}</TH>
                  <TH>{t('counting.sku')}</TH>
                  <TH>{t('counting.lot')}</TH>
                  <TH numeric>{t('accuracy.stranded.reserved')}</TH>
                  <TH numeric>{t('accuracy.stranded.onHand')}</TH>
                  <TH numeric>{t('accuracy.stranded.shortfall')}</TH>
                  <TH />
                </TR>
              </THead>
              <TBody>
                {stranded.data.map((row) => (
                  <TR key={row.reservationId}>
                    <TD>
                      <Code>{row.ownerReference}</Code>
                    </TD>
                    <TD>
                      <Code>{row.locationCode}</Code>
                    </TD>
                    <TD>
                      <Code>{row.sku}</Code>
                    </TD>
                    <TD>{row.lotNumber ? <Code>{row.lotNumber}</Code> : null}</TD>
                    <TD numeric>
                      <Numeric value={row.reservedQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Numeric value={row.onHandQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Badge tone="danger">
                        <Numeric value={row.shortfall} locale={locale} />
                      </Badge>
                    </TD>
                    <TD>
                      <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                        {t('accuracy.stranded.age', { days: row.ageDays })}
                      </span>
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        ) : null}
      </section>
    </>
  );
}

interface SliceTableProps {
  caption: string;
  slices: AccuracySlice[];
  labelHeading: string;
  locale: string;
}

function SliceTable({ caption, slices, labelHeading, locale }: SliceTableProps) {
  const { t } = useTranslation();

  return (
    <div>
      <h3 className="mb-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {caption}
      </h3>
      <TableContainer>
        <Table>
          <THead>
            <TR>
              <TH>{labelHeading}</TH>
              <TH numeric>{t('accuracy.counts.counted')}</TH>
              <TH numeric>{t('accuracy.counts.accurate')}</TH>
              <TH numeric>{t('accuracy.counts.rate')}</TH>
              <TH numeric>{t('accuracy.counts.netVariance')}</TH>
              <TH numeric>{t('accuracy.counts.absoluteVariance')}</TH>
            </TR>
          </THead>
          <TBody>
            {slices.map((slice) => (
              <TR key={slice.key}>
                <TD>{slice.key}</TD>
                <TD numeric>
                  <Numeric value={slice.counted} locale={locale} />
                </TD>
                <TD numeric>
                  <Numeric value={slice.accurate} locale={locale} />
                </TD>
                <TD numeric>
                  <Rate value={slice.accuracyRate} locale={locale} />
                </TD>
                <TD numeric>
                  <Numeric value={slice.netVariance} locale={locale} />
                </TD>
                <TD numeric>
                  <Numeric value={slice.absoluteVariance} locale={locale} />
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      </TableContainer>
    </div>
  );
}

/**
 * A rate on [0, 1], or the fact that there is not one.
 *
 * Null is rendered as "not counted" rather than as 100% or as a dash, which is
 * the whole discipline of this screen: an unmeasured slice reported as perfect
 * is the failure it exists to prevent. Colour is never the only signal — the
 * number is always there beside the badge.
 */
function Rate({
  value,
  locale,
  invert = false,
}: {
  value: number | null | undefined;
  locale: string;
  invert?: boolean;
}) {
  const { t } = useTranslation();

  if (value === null || value === undefined) {
    return (
      <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
        {t('accuracy.counts.unmeasured')}
      </span>
    );
  }

  const good = invert ? value <= 0.1 : value >= 0.98;
  const bad = invert ? value > 0.25 : value < 0.9;

  return (
    <Badge tone={good ? 'success' : bad ? 'danger' : 'warning'}>
      {formatNumber(value * 100, locale, { maximumFractionDigits: 1 })}%
    </Badge>
  );
}
