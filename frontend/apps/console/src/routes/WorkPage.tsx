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
import { DecisionTrace } from '../components/DecisionTrace.js';
import { PageHead } from '../components/PageHead.js';

const STATUSES = ['Ready', 'Assigned', 'Completed', 'Cancelled'] as const;
const KINDS = ['Putaway', 'Replenishment', 'Pick', 'CycleCount', 'BinCount'] as const;

type Status = (typeof STATUSES)[number];
type Kind = (typeof KINDS)[number];

const statusTone = (status: string) =>
  status === 'Completed' ? 'success' : status === 'Cancelled' ? 'danger' : status === 'Assigned' ? 'info' : 'neutral';

/**
 * The work queue, and the reasoning behind each job.
 *
 * The column that matters most is the one comparing the suggestion to where the
 * operator actually went. Overrides are the measurable disagreement between the
 * rule set and the floor — ADR 0004's argument is that unexplained overrides
 * becoming routine is what degrades directed work, so they are visible here
 * rather than buried in a report.
 */
export function WorkPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [status, setStatus] = useState('');
  const [kind, setKind] = useState('');
  const [page, setPage] = useState(1);
  const [openTaskId, setOpenTaskId] = useState<string | null>(null);

  const pageSize = 50;

  const { data, isPending, error } = useQuery({
    queryKey: ['work-tasks', status, kind, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/work/tasks', {
        params: {
          query: {
            page,
            pageSize,
            ...(status ? { status: status as Status } : {}),
            ...(kind ? { kind: kind as Kind } : {}),
          },
        },
      });

      if (error) throw error;
      return data;
    },
  });

  // Fetched on demand rather than with the list: a page of fifty jobs would drag
  // fifty full traces across the wire to show one.
  const { data: detail } = useQuery({
    queryKey: ['work-task', openTaskId],
    enabled: openTaskId !== null,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/work/tasks/{id}', {
        params: { path: { id: openTaskId! } },
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
      <PageHead title={t('work.title')} count={total} />

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Select
          value={status}
          onChange={(event) => {
            setStatus(event.target.value);
            reset();
          }}
        >
          <option value="">{t('work.allStatuses')}</option>
          {STATUSES.map((option) => (
            <option key={option} value={option}>
              {t(`work.status.${option}`)}
            </option>
          ))}
        </Select>

        <Select
          value={kind}
          onChange={(event) => {
            setKind(event.target.value);
            reset();
          }}
        >
          <option value="">{t('work.allKinds')}</option>
          {KINDS.map((option) => (
            <option key={option} value={option}>
              {t(`work.kind.${option}`)}
            </option>
          ))}
        </Select>
      </div>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data ? (
        <>
          <TableContainer className="max-h-[calc(100vh-320px)]">
            <Table>
              <THead>
                <TR>
                  <TH>{t('work.status.Ready')}</TH>
                  <TH>{t('work.item')}</TH>
                  <TH numeric>{t('work.quantity')}</TH>
                  <TH>{t('work.pallet')}</TH>
                  <TH>{t('work.from')}</TH>
                  <TH>{t('work.suggested')}</TH>
                  <TH>{t('work.completedAt')}</TH>
                  <TH>{t('work.assignedTo')}</TH>
                  <TH>{t('work.created')}</TH>
                  <TH />
                </TR>
              </THead>
              <TBody>
                {data.items.length === 0 ? (
                  <TR>
                    <TD colSpan={10}>
                      <EmptyState title={t('work.empty')} />
                    </TD>
                  </TR>
                ) : null}

                {data.items.map((task) => (
                  <TR key={task.id}>
                    <TD>
                      <div className="flex items-center gap-[var(--space-2)]">
                        <Badge tone={statusTone(task.status)}>{t(`work.status.${task.status}`)}</Badge>
                        {task.wasOverridden ? (
                          <Badge tone="warning" title={task.overrideReason ?? undefined}>
                            {t('work.wasOverridden')}
                          </Badge>
                        ) : null}
                      </div>
                    </TD>
                    <TD>
                      {task.kind === 'BinCount' ? t('common.none') : <Code>{task.sku}</Code>}
                    </TD>
                    <TD numeric>
                      {task.kind === 'BinCount' ? '—' : <Numeric value={task.quantity} locale={locale} />}
                    </TD>
                    <TD>{task.sscc ? <Code>{task.sscc}</Code> : t('common.none')}</TD>
                    <TD>
                      <Code>{task.fromLocationCode}</Code>
                    </TD>
                    <TD>
                      {task.suggestedLocationCode ? (
                        <Code>{task.suggestedLocationCode}</Code>
                      ) : (
                        <Badge tone="warning">{t('work.noSuggestion')}</Badge>
                      )}
                    </TD>
                    <TD>
                      {task.completedAtLocationCode ? (
                        <Code>{task.completedAtLocationCode}</Code>
                      ) : (
                        t('common.none')
                      )}
                    </TD>
                    <TD className="text-[var(--color-text-secondary)]">
                      {task.assignedToUserName ?? t('common.none')}
                    </TD>
                    <TD className="text-[var(--color-text-tertiary)] whitespace-nowrap">
                      {formatDateTime(task.createdAt, locale)}
                    </TD>
                    <TD>
                      {task.decisionTraceId ? (
                        <Button
                          size="sm"
                          onClick={() => setOpenTaskId(openTaskId === task.id ? null : task.id)}
                        >
                          {openTaskId === task.id ? t('work.hideWhy') : t('work.whyHere')}
                        </Button>
                      ) : null}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>

          <div className="mt-[var(--space-4)] flex items-center gap-[var(--space-3)]">
            <Button size="sm" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>
              {t('common.previous')}
            </Button>
            <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
              {t('common.page', { page, total: lastPage })}
            </span>
            <Button
              size="sm"
              disabled={page >= lastPage}
              onClick={() => setPage((value) => value + 1)}
            >
              {t('common.next')}
            </Button>
          </div>
        </>
      ) : null}

      {openTaskId && detail?.decision ? (
        <section className="mt-[var(--space-5)]">
          <h3 className="mb-[var(--space-3)] mt-0 text-[length:var(--text-lg)]">
            {t('rules.trace.title')}
          </h3>
          <DecisionTrace trace={detail.decision} />
        </section>
      ) : null}
    </>
  );
}
