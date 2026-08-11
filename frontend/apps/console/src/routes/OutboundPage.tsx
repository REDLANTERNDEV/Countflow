import { currentLocale, formatDate, formatDateTime, useTranslation } from '@countflow/i18n';
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
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { DecisionTrace } from '../components/DecisionTrace.js';
import { PageHead } from '../components/PageHead.js';

const STATUSES = ['New', 'Released', 'Packed', 'Despatched', 'Cancelled'] as const;

type Status = (typeof STATUSES)[number];

const statusTone = (status: string) =>
  status === 'Despatched'
    ? 'success'
    : status === 'Cancelled'
      ? 'danger'
      : status === 'Packed'
        ? 'info'
        : status === 'Released'
          ? 'warning'
          : 'neutral';

/** A new key per intent, not per attempt — the whole point of the header. */
const newKey = () => crypto.randomUUID();

/**
 * The order book, and the reasoning behind every commitment on it.
 *
 * The screen exists to make two things visible that a stock report cannot. The
 * first is the gap between what an order asked for and what the warehouse could
 * actually promise it — a shortfall is a phone call, and it should not need a
 * query to find. The second is why each line drew on the batch it did: the
 * "why this stock?" here is the same trace the picker sees on the handheld, which
 * is what stops the rules and the floor developing separate accounts of events.
 */
export function OutboundPage() {
  const { t } = useTranslation();
  const locale = currentLocale();
  const queryClient = useQueryClient();

  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [openTraceId, setOpenTraceId] = useState<string | null>(null);
  const [cancelReason, setCancelReason] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);

  const pageSize = 25;

  const { data, isPending, error } = useQuery({
    queryKey: ['sales-orders', status, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/outbound/orders', {
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

  // Fetched on demand rather than with the list: a page of orders would drag a
  // full trace per allocation across the wire to show one.
  const { data: trace } = useQuery({
    queryKey: ['decision-trace', openTraceId],
    enabled: openTraceId !== null,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/rules/traces/{id}', {
        params: { path: { id: openTraceId! } },
      });

      if (error) throw error;
      return data;
    },
  });

  const { data: waves } = useQuery({
    queryKey: ['waves'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/outbound/waves', {});

      if (error) throw error;
      return data;
    },
  });

  const releaseWave = useMutation({
    mutationFn: async (id: string) => {
      setErrorCode(null);

      const { data, error } = await api.POST('/api/outbound/waves/{id}/release', {
        params: { path: { id }, header: { 'Idempotency-Key': newKey() } },
        body: {},
      });

      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['waves'] });
      void queryClient.invalidateQueries({ queryKey: ['sales-orders'] });
    },
    onError: (failure) => {
      setErrorCode((failure as { code?: string } | undefined)?.code ?? 'unknown');
    },
  });

  const act = useMutation({
    mutationFn: async ({ id, action }: { id: string; action: 'release' | 'pack' | 'despatch' | 'cancel' }) => {
      setErrorCode(null);

      const request =
        action === 'cancel'
          ? api.POST('/api/outbound/orders/{id}/cancel', {
              params: { path: { id } },
              body: { reason: cancelReason.trim() || 'Cancelled from the console' },
            })
          : action === 'pack'
            ? api.POST('/api/outbound/orders/{id}/pack', {
                params: { path: { id } },
                body: {},
              })
            : action === 'release'
              ? api.POST('/api/outbound/orders/{id}/release', {
                  params: { path: { id }, header: { 'Idempotency-Key': newKey() } },
                  body: {},
                })
              : api.POST('/api/outbound/orders/{id}/despatch', {
                  params: { path: { id }, header: { 'Idempotency-Key': newKey() } },
                });

      const { data, error } = await request;
      if (error) throw error;

      return data;
    },
    onSuccess: () => {
      setCancelReason('');
      void queryClient.invalidateQueries({ queryKey: ['sales-orders'] });
    },
    onError: (failure) => {
      setErrorCode((failure as { code?: string } | undefined)?.code ?? 'unknown');
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  return (
    <>
      <PageHead title={t('outbound.title')} count={total} />

      <p className="mb-[var(--space-4)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {t('outbound.subtitle')}
      </p>

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Select
          value={status}
          onChange={(event) => {
            setStatus(event.target.value);
            setPage(1);
          }}
        >
          <option value="">{t('outbound.allStatuses')}</option>
          {STATUSES.map((option) => (
            <option key={option} value={option}>
              {t(`outbound.status.${option}`)}
            </option>
          ))}
        </Select>

        <Input
          className="w-[260px]"
          placeholder={t('outbound.cancelReason')}
          value={cancelReason}
          onChange={(event) => setCancelReason(event.target.value)}
        />
      </div>

      {/* Waves come before the order book, because releasing one is the first
          thing a supervisor does at the start of a shift. Grouping orders into a
          wave is an API call for now — the same place advice notes are created —
          and the rule set that would choose the grouping is named but unwritten,
          for the reasons in the Wave entity. */}
      {waves ? (
        <section className="mb-[var(--space-5)]">
          <h3 className="mb-[var(--space-1)] mt-0 text-[length:var(--text-base)]">
            {t('outbound.waves.title')}
          </h3>
          <p className="mb-[var(--space-3)] mt-0 max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('outbound.waves.hint')}
          </p>

          <TableContainer>
            <Table>
              <THead>
                <TR>
                  <TH>{t('outbound.waves.reference')}</TH>
                  <TH>{t('outbound.waves.statusLabel')}</TH>
                  <TH>{t('outbound.waves.contents')}</TH>
                  <TH>{t('outbound.waves.releasedAt')}</TH>
                  <TH />
                </TR>
              </THead>
              <TBody>
                {waves.length === 0 ? (
                  <TR>
                    <TD colSpan={5}>
                      <EmptyState title={t('outbound.waves.empty')} />
                    </TD>
                  </TR>
                ) : null}

                {waves.map((wave) => (
                  <TR key={wave.id}>
                    <TD>
                      <Code>{wave.reference}</Code>
                    </TD>
                    <TD>
                      <Badge tone={wave.status === 'Released' ? 'success' : 'neutral'}>
                        {t(`outbound.waves.status.${wave.status}`)}
                      </Badge>
                    </TD>
                    <TD className="text-[var(--color-text-secondary)]">
                      {t('outbound.waves.orders', { count: wave.orders.length })}
                    </TD>
                    <TD className="text-[var(--color-text-tertiary)] whitespace-nowrap">
                      {wave.releasedAt ? formatDateTime(wave.releasedAt, locale) : t('common.none')}
                    </TD>
                    <TD>
                      {wave.status === 'Planned' ? (
                        <Button
                          size="sm"
                          variant="primary"
                          disabled={releaseWave.isPending}
                          onClick={() => releaseWave.mutate(wave.id)}
                        >
                          {releaseWave.isPending
                            ? t('outbound.releasing')
                            : t('outbound.waves.release')}
                        </Button>
                      ) : null}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        </section>
      ) : null}

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {errorCode ? (
        <p className="text-[var(--color-danger)]">{t([`errors.${errorCode}`, 'errors.unknown'])}</p>
      ) : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data?.items.length === 0 ? <EmptyState title={t('outbound.empty')} /> : null}

      {data?.items.map((order) => (
        <section key={order.id} className="mb-[var(--space-5)]">
          <div className="mb-[var(--space-3)] flex flex-wrap items-baseline gap-[var(--space-3)]">
            <Code className="text-[length:var(--text-base)]">{order.reference}</Code>
            <Badge tone={statusTone(order.status)}>{t(`outbound.status.${order.status}`)}</Badge>
            {order.customerName ? (
              <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                {order.customerName}
              </span>
            ) : null}
            {order.shipBy ? (
              <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {t('outbound.shipBy')} {formatDate(order.shipBy, locale)}
              </span>
            ) : null}
            {order.stagingLocationCode ? (
              <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {t('outbound.staging')} <Code>{order.stagingLocationCode}</Code>
              </span>
            ) : null}
            {order.despatchedAt ? (
              <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {formatDateTime(order.despatchedAt, locale)}
              </span>
            ) : null}

            <div className="ml-auto flex gap-[var(--space-2)]">
              {/* One button per legal transition, and the server decides in the
                  end — these only hide what would certainly be refused. */}
              {order.status === 'New' || order.status === 'Released' ? (
                <Button
                  size="sm"
                  variant="primary"
                  disabled={act.isPending}
                  onClick={() => act.mutate({ id: order.id, action: 'release' })}
                >
                  {act.isPending ? t('outbound.releasing') : t('outbound.release')}
                </Button>
              ) : null}

              {order.status === 'Released' ? (
                <Button
                  size="sm"
                  disabled={act.isPending}
                  onClick={() => act.mutate({ id: order.id, action: 'pack' })}
                >
                  {t('outbound.pack')}
                </Button>
              ) : null}

              {order.status === 'Packed' ? (
                <Button
                  size="sm"
                  variant="primary"
                  disabled={act.isPending}
                  onClick={() => act.mutate({ id: order.id, action: 'despatch' })}
                >
                  {act.isPending ? t('outbound.despatching') : t('outbound.despatch')}
                </Button>
              ) : null}

              {order.status === 'New' || order.status === 'Released' ? (
                <Button
                  size="sm"
                  disabled={act.isPending}
                  onClick={() => act.mutate({ id: order.id, action: 'cancel' })}
                >
                  {t('outbound.cancel')}
                </Button>
              ) : null}
            </div>
          </div>

          <TableContainer>
            <Table>
              <THead>
                <TR>
                  <TH numeric>#</TH>
                  <TH>{t('items.sku')}</TH>
                  <TH>{t('items.description')}</TH>
                  <TH numeric>{t('outbound.ordered')}</TH>
                  <TH numeric>{t('outbound.allocated')}</TH>
                  <TH numeric>{t('outbound.picked')}</TH>
                  <TH numeric>{t('outbound.outstanding')}</TH>
                </TR>
              </THead>
              <TBody>
                {order.lines.map((line) => (
                  <TR key={line.id}>
                    <TD numeric className="text-[var(--color-text-tertiary)]">
                      {line.lineNumber}
                    </TD>
                    <TD>
                      <Code>{line.sku}</Code>
                    </TD>
                    <TD className="text-[var(--color-text-secondary)]">{line.description}</TD>
                    <TD numeric>
                      <Numeric value={line.orderedQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Numeric value={line.allocatedQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      <Numeric value={line.pickedQuantity} locale={locale} />
                    </TD>
                    <TD numeric>
                      {/* Zero is the good outcome and reads as such; anything
                          else is a phone call to make before the trailer goes. */}
                      {line.outstandingQuantity === 0 ? (
                        <Badge tone="success">{t('inbound.complete')}</Badge>
                      ) : (
                        <Badge tone="warning">
                          <Numeric value={line.outstandingQuantity} locale={locale} />
                        </Badge>
                      )}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>

          {order.lines.some((line) => line.allocations.length > 0) ? (
            <div className="mt-[var(--space-3)]">
              <h4 className="mb-[var(--space-2)] mt-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                {t('outbound.allocations')}
              </h4>
              <TableContainer>
                <Table>
                  <THead>
                    <TR>
                      <TH>{t('items.sku')}</TH>
                      <TH>{t('outbound.allocationLocation')}</TH>
                      <TH>{t('outbound.allocationLot')}</TH>
                      <TH>{t('outbound.allocationExpiry')}</TH>
                      <TH numeric>{t('outbound.allocationQuantity')}</TH>
                      <TH>{t('outbound.allocationTask')}</TH>
                      <TH>{t('outbound.allocationPickedAt')}</TH>
                      <TH />
                    </TR>
                  </THead>
                  <TBody>
                    {order.lines.flatMap((line) =>
                      line.allocations.map((allocation) => (
                        <TR key={allocation.id}>
                          <TD>
                            <Code>{line.sku}</Code>
                          </TD>
                          <TD>
                            <Code>{allocation.locationCode}</Code>
                          </TD>
                          <TD>
                            <Code>{allocation.lotNumber ?? t('common.none')}</Code>
                          </TD>
                          <TD>
                            {allocation.expiryDate
                              ? formatDate(allocation.expiryDate, locale)
                              : t('common.none')}
                          </TD>
                          <TD numeric>
                            <Numeric value={allocation.quantity} locale={locale} />
                          </TD>
                          <TD>
                            {allocation.taskState ? (
                              <Badge
                                tone={allocation.taskState === 'Completed' ? 'success' : 'neutral'}
                              >
                                {t(`work.status.${allocation.taskState}`)}
                              </Badge>
                            ) : (
                              t('common.none')
                            )}
                          </TD>
                          <TD>
                            {allocation.pickedAtLocationCode ? (
                              <Code>{allocation.pickedAtLocationCode}</Code>
                            ) : (
                              t('common.none')
                            )}
                          </TD>
                          <TD>
                            <Button
                              size="sm"
                              onClick={() =>
                                setOpenTraceId(
                                  openTraceId === allocation.decisionTraceId
                                    ? null
                                    : allocation.decisionTraceId,
                                )
                              }
                            >
                              {openTraceId === allocation.decisionTraceId
                                ? t('outbound.hideWhy')
                                : t('outbound.whyThisStock')}
                            </Button>
                          </TD>
                        </TR>
                      )),
                    )}
                  </TBody>
                </Table>
              </TableContainer>
            </div>
          ) : null}
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

      {openTraceId && trace ? (
        <section className="mt-[var(--space-5)]">
          <h3 className="mb-[var(--space-3)] mt-0 text-[length:var(--text-lg)]">
            {t('rules.trace.title')}
          </h3>
          <DecisionTrace trace={trace} />
        </section>
      ) : null}
    </>
  );
}
