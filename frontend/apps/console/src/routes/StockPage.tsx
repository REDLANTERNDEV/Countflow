import { currentLocale, formatDate, useTranslation } from '@countflow/i18n';
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
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

const STATUSES = ['Available', 'Quarantine', 'Damaged', 'OnHold', 'Expired'] as const;

type Status = (typeof STATUSES)[number];

/**
 * Anything other than Available is stock the warehouse is holding but cannot
 * sell, so it reads as a warning rather than a failure — damaged goods are a
 * cost, not an error.
 */
const statusTone = (status: string) =>
  status === 'Available' ? 'success' : status === 'Damaged' ? 'danger' : 'warning';

export function StockPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [sku, setSku] = useState('');
  const [location, setLocation] = useState('');
  const [lot, setLot] = useState('');
  const [sscc, setSscc] = useState('');
  const [status, setStatus] = useState('');
  const [includeEmpty, setIncludeEmpty] = useState(false);
  const [asOf, setAsOf] = useState('');
  const [page, setPage] = useState(1);

  const pageSize = 50;

  const { data, isPending, error } = useQuery({
    queryKey: ['stock', sku, location, lot, sscc, status, includeEmpty, asOf, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/inventory/balances', {
        params: {
          query: {
            page,
            pageSize,
            ...(sku ? { sku } : {}),
            ...(location ? { location } : {}),
            ...(lot ? { lot } : {}),
            ...(sscc ? { sscc } : {}),
            ...(status ? { status: status as Status } : {}),
            ...(includeEmpty ? { includeEmpty: true } : {}),
            // datetime-local gives a wall-clock string with no zone. Treating it
            // as local time is what the person typing it means.
            ...(asOf ? { asOf: new Date(asOf).toISOString() } : {}),
          },
        },
      });

      if (error) throw error;
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  const reset = () => setPage(1);

  return (
    <>
      <PageHead help={`${t('stock.intro')} ${t('stock.availableNote')}`} title={t('stock.title')} count={total} />

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
        <Input
          className="w-[190px]"
          placeholder={t('stock.ssccPlaceholder')}
          value={sscc}
          onChange={(event) => {
            setSscc(event.target.value);
            reset();
          }}
        />

        <Select
          value={status}
          onChange={(event) => {
            setStatus(event.target.value);
            reset();
          }}
        >
          <option value="">{t('stock.allStatuses')}</option>
          {STATUSES.map((option) => (
            <option key={option} value={option}>
              {t(`stock.status_${option}`)}
            </option>
          ))}
        </Select>

        <label className="flex items-center gap-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          <input
            type="checkbox"
            checked={includeEmpty}
            onChange={(event) => {
              setIncludeEmpty(event.target.checked);
              reset();
            }}
            className="accent-[var(--color-accent)]"
          />
          {t('stock.includeEmpty')}
        </label>
      </div>

      {/* Point-in-time reconstruction. Separated from the filters above because
          it changes what the numbers *are*, not merely which of them are shown. */}
      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <label className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('stock.asOf')}
        </label>
        <Input
          type="datetime-local"
          className="w-[230px]"
          value={asOf}
          onChange={(event) => {
            setAsOf(event.target.value);
            reset();
          }}
        />
        {asOf ? (
          <Button
            size="sm"
            onClick={() => {
              setAsOf('');
              reset();
            }}
          >
            {t('stock.asOfClear')}
          </Button>
        ) : null}

        {data?.asOfSequence ? (
          <Badge tone="info">
            {t('stock.asOfNotice', { sequence: data.asOfSequence })}
          </Badge>
        ) : null}
      </div>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data ? (
        <>
          <TableContainer className="max-h-[calc(100vh-360px)]">
            <Table>
              <THead>
                <TR>
                  <TH>{t('stock.sku')}</TH>
                  <TH>{t('stock.description')}</TH>
                  <TH>{t('stock.location')}</TH>
                  <TH>{t('stock.lot')}</TH>
                  <TH>{t('stock.expiry')}</TH>
                  <TH>{t('stock.sscc')}</TH>
                  <TH>{t('stock.status')}</TH>
                  <TH numeric>{t('stock.quantity')}</TH>
                  <TH numeric>{t('stock.reserved')}</TH>
                  <TH numeric>{t('stock.available')}</TH>
                  <TH>{t('stock.uom')}</TH>
                </TR>
              </THead>
              <TBody>
                {data.items.length === 0 ? (
                  <TR>
                    <TD colSpan={11}>
                      <EmptyState title={t('stock.empty')} />
                    </TD>
                  </TR>
                ) : (
                  data.items.map((row, index) => (
                    <TR key={`${row.sku}-${row.locationCode}-${row.lotNumber ?? ''}-${row.status}-${index}`}>
                      {/* Identifiers stay verbatim; a grouped SSCC is not a
                          barcode. Quantities are localised. */}
                      <TD>
                        <Code>{row.sku}</Code>
                      </TD>
                      <TD className="text-[var(--color-text-secondary)]">{row.description}</TD>
                      <TD>
                        <Code>{row.locationCode}</Code>
                      </TD>
                      <TD>
                        <Code>{row.lotNumber ?? t('common.none')}</Code>
                      </TD>
                      <TD>{row.expiryDate ? formatDate(row.expiryDate, locale) : t('common.none')}</TD>
                      <TD>
                        <Code>{row.sscc ?? t('common.none')}</Code>
                      </TD>
                      <TD>
                        <Badge tone={statusTone(row.status)}>{t(`stock.status_${row.status}`)}</Badge>
                      </TD>
                      <TD numeric>
                        <Numeric value={row.quantity} locale={locale} />
                      </TD>
                      {/* On hand, committed, available — the distinction the
                          outbound loop turns on, and the only place most people
                          will ever meet it. Blank rather than zero when the
                          figures are historical: commitments are current state,
                          so a replayed balance genuinely has none to report. */}
                      <TD numeric className="text-[var(--color-text-secondary)]">
                        {row.reservedQuantity == null ? (
                          t('common.none')
                        ) : (
                          <Numeric value={row.reservedQuantity} locale={locale} />
                        )}
                      </TD>
                      <TD numeric>
                        {row.availableQuantity == null ? (
                          t('common.none')
                        ) : (
                          <Numeric value={row.availableQuantity} locale={locale} />
                        )}
                      </TD>
                      <TD className="text-[var(--color-text-tertiary)]">{row.baseUom}</TD>
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
