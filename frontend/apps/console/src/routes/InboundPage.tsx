import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
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
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

const STATUSES = ['Expected', 'Receiving', 'Complete', 'Closed', 'Cancelled'] as const;

type Status = (typeof STATUSES)[number];

const statusTone = (status: string) =>
  status === 'Complete' ? 'success' : status === 'Cancelled' ? 'danger' : status === 'Receiving' ? 'info' : 'neutral';

/**
 * What is expected, and how much of it has turned up.
 *
 * The outstanding column is the point of the screen. An advice note is not
 * paperwork — it is the comparison that turns "we got some boxes" into "we got
 * four fewer than we were told", while the driver is still on site.
 */
export function InboundPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);

  const pageSize = 25;

  const { data, isPending, error } = useQuery({
    queryKey: ['asns', status, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/inbound/asns', {
        params: {
          query: {
            page,
            pageSize,
            ...(status ? { status: status as Status } : {}),
          },
        },
      });

      if (error) throw error;
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  return (
    <>
      <PageHead help={t('inbound.subtitle')} title={t('inbound.title')} count={total} />

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Select
          value={status}
          onChange={(event) => {
            setStatus(event.target.value);
            setPage(1);
          }}
        >
          <option value="">{t('inbound.allStatuses')}</option>
          {STATUSES.map((option) => (
            <option key={option} value={option}>
              {t(`inbound.status.${option}`)}
            </option>
          ))}
        </Select>
      </div>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data?.items.length === 0 ? <EmptyState title={t('inbound.empty')} /> : null}

      {data?.items.map((asn) => (
        <section key={asn.id} className="mb-[var(--space-5)]">
          <div className="mb-[var(--space-3)] flex flex-wrap items-baseline gap-[var(--space-3)]">
            <Code className="text-[length:var(--text-base)]">{asn.reference}</Code>
            <Badge tone={statusTone(asn.status)}>{t(`inbound.status.${asn.status}`)}</Badge>
            {asn.supplierName ? (
              <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                {asn.supplierName}
              </span>
            ) : null}
            {asn.expectedAt ? (
              <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {t('inbound.expectedAt')} {formatDateTime(asn.expectedAt, locale)}
              </span>
            ) : null}
            {asn.carrierReference ? (
              <Code className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {asn.carrierReference}
              </Code>
            ) : null}
          </div>

          <TableContainer>
            <Table>
              <THead>
                <TR>
                  <TH numeric>#</TH>
                  <TH>{t('items.sku')}</TH>
                  <TH>{t('items.description')}</TH>
                  <TH>{t('stock.lot')}</TH>
                  <TH numeric>{t('inbound.expected')}</TH>
                  <TH numeric>{t('inbound.received')}</TH>
                  <TH numeric>{t('inbound.outstanding')}</TH>
                </TR>
              </THead>
              <TBody>
                {asn.lines.map((line) => (
                  <TR key={line.lineNumber}>
                    <TD numeric className="text-[var(--color-text-tertiary)]">
                      {line.lineNumber}
                    </TD>
                    <TD>
                      <Code>{line.sku}</Code>
                    </TD>
                    <TD className="text-[var(--color-text-secondary)]">{line.description}</TD>
                    <TD>{line.expectedLotNumber ? <Code>{line.expectedLotNumber}</Code> : t('common.none')}</TD>
                    <TD numeric>
                      <Numeric value={line.expectedQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Numeric value={line.receivedQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      {/* Zero is the good outcome and reads as such; anything
                          else is a conversation to have with the supplier. */}
                      {line.outstandingQuantity === 0 ? (
                        <Badge tone="success">{t('inbound.complete')}</Badge>
                      ) : (
                        <Badge tone={line.outstandingQuantity > 0 ? 'warning' : 'info'}>
                          <Numeric value={line.outstandingQuantity} locale={locale} />
                        </Badge>
                      )}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        </section>
      ))}

      {data ? (
        <div className="mt-[var(--space-4)] flex items-center gap-[var(--space-3)]">
          <Button size="sm" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>
            {t('common.previous')}
          </Button>
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
            {t('common.page', { page, total: lastPage })}
          </span>
          <Button size="sm" disabled={page >= lastPage} onClick={() => setPage((value) => value + 1)}>
            {t('common.next')}
          </Button>
        </div>
      ) : null}
    </>
  );
}
