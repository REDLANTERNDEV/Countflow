import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import {
  Badge,
  Button,
  Code,
  EmptyState,
  Input,
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
import { keepPreviousData, useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

const MOVEMENT_TYPES = [
  'Receipt',
  'Putaway',
  'Transfer',
  'Replenishment',
  'Pick',
  'Despatch',
  'Adjustment',
  'CountAdjustment',
  'StatusChange',
  'Scrap',
  'Reversal',
] as const;

type MovementType = (typeof MOVEMENT_TYPES)[number];

export function LedgerPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [sku, setSku] = useState('');
  const [location, setLocation] = useState('');
  const [lot, setLot] = useState('');
  const [type, setType] = useState('');
  const [page, setPage] = useState(1);

  const pageSize = 50;

  const { data, isPending, error } = useQuery({
    queryKey: ['ledger', sku, location, lot, type, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/inventory/movements', {
        params: {
          query: {
            page,
            pageSize,
            ...(sku ? { sku } : {}),
            ...(location ? { location } : {}),
            ...(lot ? { lot } : {}),
            ...(type ? { type: type as MovementType } : {}),
          },
        },
      });

      if (error) throw error;
      return data;
    },
  });

  // The proof, on demand. Replays the whole ledger and compares it with the
  // stored balances — the guarantee ADR 0001 makes, shown rather than asserted.
  const reconcile = useMutation({
    mutationFn: async () => {
      const { data, error } = await api.POST('/api/inventory/reconciliation', {});
      if (error) throw error;
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  const reset = () => setPage(1);

  return (
    <>
      <PageHead title={t('ledger.title')} count={total} />

      <p className="mb-[var(--space-4)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {t('ledger.intro')}
      </p>

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Input
          className="w-[180px]"
          placeholder={t('stock.skuPlaceholder')}
          value={sku}
          onChange={(event) => {
            setSku(event.target.value);
            reset();
          }}
        />
        <Input
          className="w-[240px]"
          placeholder={t('stock.locationPlaceholder')}
          value={location}
          onChange={(event) => {
            setLocation(event.target.value);
            reset();
          }}
        />
        <Input
          className="w-[150px]"
          placeholder={t('stock.lotPlaceholder')}
          value={lot}
          onChange={(event) => {
            setLot(event.target.value);
            reset();
          }}
        />

        <Select
          value={type}
          onChange={(event) => {
            setType(event.target.value);
            reset();
          }}
        >
          <option value="">{t('ledger.allTypes')}</option>
          {MOVEMENT_TYPES.map((option) => (
            <option key={option} value={option}>
              {t(`ledger.type_${option}`)}
            </option>
          ))}
        </Select>

        <Button size="sm" onClick={() => reconcile.mutate()} disabled={reconcile.isPending}>
          {reconcile.isPending ? t('ledger.reconciling') : t('ledger.reconcile')}
        </Button>
      </div>

      {reconcile.data ? (
        <div className="mb-[var(--space-4)] flex flex-col gap-[var(--space-2)]">
          <div>
            <Badge tone={reconcile.data.isClean ? 'success' : 'danger'}>
              {reconcile.data.isClean
                ? t('ledger.reconcileClean', {
                    holdings: reconcile.data.holdingsChecked,
                    movements: reconcile.data.movementsReplayed,
                  })
                : t('ledger.reconcileDrift', { count: reconcile.data.drift.length })}
            </Badge>
          </div>
          <p className="m-0 max-w-[72ch] text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {t('ledger.reconcileNote')}
          </p>
        </div>
      ) : null}

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data ? (
        <>
          <TableContainer className="max-h-[calc(100vh-330px)]">
            <Table>
              <THead>
                <TR>
                  <TH numeric>{t('ledger.entry')}</TH>
                  <TH>{t('ledger.when')}</TH>
                  <TH>{t('ledger.type')}</TH>
                  <TH>{t('ledger.sku')}</TH>
                  <TH>{t('ledger.lot')}</TH>
                  <TH numeric>{t('ledger.quantity')}</TH>
                  <TH>{t('ledger.from')}</TH>
                  <TH>{t('ledger.to')}</TH>
                  <TH>{t('ledger.reason')}</TH>
                  <TH>{t('ledger.user')}</TH>
                </TR>
              </THead>
              <TBody>
                {data.items.length === 0 ? (
                  <TR>
                    <TD colSpan={10}>
                      <EmptyState title={t('ledger.empty')} />
                    </TD>
                  </TR>
                ) : (
                  data.items.map((entry) => (
                    <TR key={entry.id}>
                      {/* The sequence number is an identifier, not a quantity:
                          entry #1.234 would be nonsense. */}
                      <TD numeric>
                        <Code>{entry.sequence}</Code>
                      </TD>
                      <TD className="whitespace-nowrap text-[var(--color-text-secondary)]">
                        {formatDateTime(entry.occurredAt, locale)}
                      </TD>
                      <TD>
                        <div className="flex items-center gap-[var(--space-1)]">
                          {t(`ledger.type_${entry.type}`)}
                          {/* A reversed entry that looks identical to a live one
                              is how a reader draws the wrong conclusion from a
                              perfectly correct ledger. */}
                          {entry.isReversed ? <Badge tone="warning">{t('ledger.reversed')}</Badge> : null}
                          {entry.reversesMovementId ? (
                            <Badge tone="info">{t('ledger.reversal')}</Badge>
                          ) : null}
                        </div>
                      </TD>
                      <TD>
                        <Code>{entry.sku}</Code>
                      </TD>
                      <TD>
                        <Code>{entry.lotNumber ?? t('common.none')}</Code>
                      </TD>
                      <TD numeric>
                        <Numeric value={entry.quantity} locale={locale} />
                      </TD>
                      <TD>
                        <Code>{entry.fromLocationCode}</Code>
                      </TD>
                      <TD>
                        <Code>{entry.toLocationCode}</Code>
                      </TD>
                      <TD className="text-[var(--color-text-secondary)]">{entry.reason}</TD>
                      <TD className="text-[var(--color-text-tertiary)]">{entry.userName}</TD>
                    </TR>
                  ))
                )}
              </TBody>
            </Table>
          </TableContainer>

          <div className="mt-[var(--space-4)] flex items-center gap-[var(--space-4)]">
            <Button size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              {t('common.previous')}
            </Button>
            <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)] tabular-nums">
              {t('common.page', { page, total: lastPage })}
            </span>
            <Button size="sm" disabled={page >= lastPage} onClick={() => setPage((p) => p + 1)}>
              {t('common.next')}
            </Button>
          </div>
        </>
      ) : null}
    </>
  );
}
