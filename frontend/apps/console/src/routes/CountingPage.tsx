import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import {
  Badge,
  Button,
  Code,
  EmptyState,
  Field,
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

const STATUSES = ['Open', 'Completed', 'Cancelled'] as const;

type Status = (typeof STATUSES)[number];

const statusTone = (status: string) =>
  status === 'Completed' ? 'success' : status === 'Cancelled' ? 'danger' : 'info';

function newIdempotencyKey() {
  return `plan-${crypto.randomUUID()}`;
}

/**
 * What the system decided to go and check, and what came back.
 *
 * There is deliberately no "count this bay" button. A count somebody chose by
 * hand has no policy behind it, so its trace would be empty and the accuracy
 * figures downstream would be measuring wherever a supervisor's attention
 * happened to fall. If the rules will not raise a bay, the fix is to change the
 * rules — and the preview on the Rules screen shows what else that changes.
 */
export function CountingPage() {
  const { t } = useTranslation();
  const locale = currentLocale();
  const queryClient = useQueryClient();

  const [status, setStatus] = useState('');
  const [page, setPage] = useState(1);
  const [maxTasks, setMaxTasks] = useState('20');
  const [notes, setNotes] = useState('');
  const [warehouseCode, setWarehouseCode] = useState('');
  const [expanded, setExpanded] = useState<string | null>(null);
  const [showTrace, setShowTrace] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);

  const pageSize = 25;

  const warehouses = useQuery({
    queryKey: ['warehouses'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/warehouses');
      if (error) throw error;
      return data;
    },
  });

  const selectedWarehouse = warehouseCode || warehouses.data?.[0]?.code || '';

  const plans = useQuery({
    queryKey: ['count-plans', status, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/counting/plans', {
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

  const detail = useQuery({
    queryKey: ['count-plan', expanded],
    enabled: expanded !== null,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/counting/plans/{id}', {
        params: { path: { id: expanded! } },
      });
      if (error) throw error;
      return data;
    },
  });

  const raise = useMutation({
    mutationFn: async () => {
      setErrorCode(null);

      const { data, error } = await api.POST('/api/counting/plans', {
        params: { header: { 'Idempotency-Key': newIdempotencyKey() } },
        body: {
          warehouseCode: selectedWarehouse,
          maxTasks: Number(maxTasks) || 20,
          ...(notes.trim() ? { notes: notes.trim() } : {}),
        },
      });

      if (error) throw error;
      return data;
    },
    onSuccess: (plan) => {
      setNotes('');
      setExpanded(plan.id);
      void queryClient.invalidateQueries({ queryKey: ['count-plans'] });
    },
    onError: (error: unknown) => {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
    },
  });

  const cancel = useMutation({
    mutationFn: async (id: string) => {
      const { data, error } = await api.POST('/api/counting/plans/{id}/cancel', {
        params: { path: { id } },
        body: {},
      });
      if (error) throw error;
      return data;
    },
    onSuccess: () => {
      void queryClient.invalidateQueries({ queryKey: ['count-plans'] });
      void queryClient.invalidateQueries({ queryKey: ['count-plan'] });
    },
    onError: (error: unknown) => {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
    },
  });

  const trace = useQuery({
    queryKey: ['decision-trace', detail.data?.decisionTraceId],
    enabled: showTrace && detail.data !== undefined,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/rules/traces/{id}', {
        params: { path: { id: detail.data!.decisionTraceId } },
      });
      if (error) throw error;
      return data;
    },
  });

  const total = plans.data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  return (
    <>
      <PageHead title={t('counting.title')} count={total} />

      <p className="mb-[var(--space-4)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {t('counting.subtitle')}
      </p>

      <section
        className="mb-[var(--space-5)] rounded-[var(--radius-surface)] border
                   border-[var(--color-border-default)] bg-[var(--color-surface-raised)]
                   p-[var(--space-4)]"
      >
        <p className="mt-0 mb-[var(--space-3)] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('counting.planHint')}
        </p>

        <div className="flex flex-wrap items-end gap-[var(--space-3)]">
          <Field label={t('nav.warehouses')} htmlFor="plan-warehouse">
            <Select
              id="plan-warehouse"
              value={selectedWarehouse}
              onChange={(event) => setWarehouseCode(event.target.value)}
            >
              {(warehouses.data ?? []).map((warehouse) => (
                <option key={warehouse.id} value={warehouse.code}>
                  {warehouse.code} — {warehouse.name}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={t('counting.maxTasks')} htmlFor="plan-max">
            <Input
              id="plan-max"
              inputMode="numeric"
              className="w-[8ch]"
              value={maxTasks}
              onChange={(event) => setMaxTasks(event.target.value)}
            />
          </Field>

          <Field
            label={`${t('counting.notes')} · ${t('counting.optional')}`}
            htmlFor="plan-notes"
          >
            <Input
              id="plan-notes"
              value={notes}
              onChange={(event) => setNotes(event.target.value)}
            />
          </Field>

          <Button
            variant="primary"
            disabled={raise.isPending || selectedWarehouse === ''}
            onClick={() => raise.mutate()}
          >
            {raise.isPending ? t('counting.planning') : t('counting.plan')}
          </Button>
        </div>

        {raise.data ? (
          <p className="mt-[var(--space-3)] mb-0 text-[length:var(--text-sm)]">
            {raise.data.raisedCount === 0
              ? t('counting.nothingDue')
              : t('counting.planned', {
                  raised: raise.data.raisedCount,
                  admissible: raise.data.requestedTasks,
                })}
          </p>
        ) : null}

        {errorCode ? (
          <p className="mt-[var(--space-3)] mb-0 text-[var(--color-danger)]">
            {t([`errors.${errorCode}`, 'errors.unknown'])}
          </p>
        ) : null}
      </section>

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Select
          value={status}
          onChange={(event) => {
            setStatus(event.target.value);
            setPage(1);
          }}
        >
          <option value="">{t('counting.allStatuses')}</option>
          {STATUSES.map((option) => (
            <option key={option} value={option}>
              {t(`counting.status.${option}`)}
            </option>
          ))}
        </Select>
      </div>

      {plans.isPending ? <Spinner label={t('common.loading')} /> : null}
      {plans.data?.items.length === 0 ? <EmptyState title={t('counting.empty')} /> : null}

      {plans.data?.items.map((plan) => (
        <section key={plan.id} className="mb-[var(--space-5)]">
          <div className="mb-[var(--space-3)] flex flex-wrap items-baseline gap-[var(--space-3)]">
            <Code className="text-[length:var(--text-base)]">{plan.reference}</Code>
            <Badge tone={statusTone(plan.status)}>{t(`counting.status.${plan.status}`)}</Badge>

            <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
              {t('counting.raised')} <Numeric value={plan.raisedCount} locale={locale} />
              {' · '}
              {t('counting.completed')} <Numeric value={plan.completedCount} locale={locale} />
            </span>

            {plan.varianceCount > 0 ? (
              <Badge tone="warning">
                {t('counting.variances')} <Numeric value={plan.varianceCount} locale={locale} />
              </Badge>
            ) : null}

            <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
              {formatDateTime(plan.createdAt, locale)} · {plan.createdByUserName}
            </span>

            <Button
              size="sm"
              variant="ghost"
              onClick={() => {
                setShowTrace(false);
                setExpanded(expanded === plan.id ? null : plan.id);
              }}
            >
              {expanded === plan.id ? t('counting.hideWhy') : t('counting.lines')}
            </Button>

            {plan.status === 'Open' ? (
              <Button
                size="sm"
                disabled={cancel.isPending}
                onClick={() => cancel.mutate(plan.id)}
              >
                {cancel.isPending ? t('counting.cancelling') : t('counting.cancel')}
              </Button>
            ) : null}
          </div>

          {expanded === plan.id ? (
            detail.isPending ? (
              <Spinner label={t('common.loading')} />
            ) : detail.data ? (
              <>
                <TableContainer>
                  <Table>
                    <THead>
                      <TR>
                        <TH numeric>{t('counting.rank')}</TH>
                        <TH>{t('counting.location')}</TH>
                        <TH>{t('counting.sku')}</TH>
                        <TH>{t('counting.lot')}</TH>
                        <TH numeric>{t('counting.expected')}</TH>
                        <TH numeric>{t('counting.found')}</TH>
                        <TH numeric>{t('counting.variance')}</TH>
                        <TH>{t('counting.state')}</TH>
                        <TH>{t('counting.countedBy')}</TH>
                        <TH numeric>{t('counting.score')}</TH>
                      </TR>
                    </THead>
                    <TBody>
                      {detail.data.lines.map((line) => (
                        <TR key={line.id}>
                          <TD numeric>
                            <Numeric value={line.rank} locale={locale} />
                          </TD>
                          <TD>
                            <Code>{line.locationCode}</Code>
                          </TD>
                          <TD>
                            <Code>{line.sku}</Code>
                          </TD>
                          <TD>{line.lotNumber ? <Code>{line.lotNumber}</Code> : null}</TD>
                          <TD numeric>
                            <Numeric value={line.expectedQuantity} locale={locale} />
                          </TD>
                          <TD numeric>
                            {line.countedQuantity === null || line.countedQuantity === undefined ? (
                              <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                                {t('counting.notCountedYet')}
                              </span>
                            ) : (
                              <Numeric value={line.countedQuantity} locale={locale} />
                            )}
                          </TD>
                          <TD numeric>
                            {line.variance === null || line.variance === undefined ? null : line.variance === 0 ? (
                              <Badge tone="success">{t('counting.agrees')}</Badge>
                            ) : (
                              <Badge tone="warning">
                                <Numeric value={line.variance} locale={locale} />
                              </Badge>
                            )}
                          </TD>
                          <TD>
                            {line.state ? (
                              <Badge tone={line.state === 'Completed' ? 'success' : 'neutral'}>
                                {t(`work.status.${line.state}`)}
                              </Badge>
                            ) : null}
                          </TD>
                          <TD>{line.countedByUserName}</TD>
                          <TD numeric>
                            <Numeric value={line.score} locale={locale} fractionDigits={3} />
                          </TD>
                        </TR>
                      ))}
                    </TBody>
                  </Table>
                </TableContainer>

                <Button
                  size="sm"
                  variant="ghost"
                  className="mt-[var(--space-3)]"
                  onClick={() => setShowTrace((open) => !open)}
                >
                  {showTrace ? t('counting.hideWhy') : t('counting.whyThis')}
                </Button>

                {showTrace && trace.data ? <DecisionTrace trace={trace.data} /> : null}
              </>
            ) : null
          ) : null}
        </section>
      ))}

      {lastPage > 1 ? (
        <div className="flex items-center gap-[var(--space-3)]">
          <Button size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
            {t('common.previous')}
          </Button>
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {page} / {lastPage}
          </span>
          <Button size="sm" disabled={page >= lastPage} onClick={() => setPage((p) => p + 1)}>
            {t('common.next')}
          </Button>
        </div>
      ) : null}
    </>
  );
}
