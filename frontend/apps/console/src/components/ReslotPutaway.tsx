import type { components } from '@countflow/api-client';
import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import { Badge, Button, Field, InfoHelp, Input } from '@countflow/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { useAuth } from '../auth.js';
import { DecisionTrace } from './DecisionTrace.js';

type Detail = components['schemas']['WorkTaskDetailResponse'];
type Intent = { key: string; reason: string };
function readIntent(key: string): Intent | null {
  try {
    const value: unknown = JSON.parse(sessionStorage.getItem(key) ?? 'null');
    if (value && typeof value === 'object' && 'key' in value && 'reason' in value &&
      typeof value.key === 'string' && typeof value.reason === 'string') return { key: value.key, reason: value.reason };
  } catch { /* Submission reports storage failures before sending. */ }
  return null;
}

export function ReslotPutaway({ detail }: { detail: Detail }) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const queries = useQueryClient();
  const task = detail.task;
  const storageKey = `countflow.reslot:${user?.id}:${task.id}`;
  const [intent, setIntent] = useState<Intent | null>(() => readIntent(storageKey));
  const [reason, setReason] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [traceId, setTraceId] = useState<string | null>(null);
  const editable = user?.roles.some(role => role === 'Admin' || role === 'Supervisor');
  const history = detail.putawayHistory ?? [];
  const canStart = task.status === 'Ready' && task.pendingReceiptCount === 0 && !task.completionPending;
  const trace = useQuery({ queryKey: ['putaway-history-trace', traceId], enabled: traceId !== null,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/rules/traces/{id}', { params: { path: { id: traceId! } } });
      if (error) throw error;
      return data;
    } });
  const reslot = useMutation({ mutationFn: async () => {
    setErrorCode(null);
    const request = intent ?? { key: crypto.randomUUID(), reason: reason.trim() };
    try { sessionStorage.setItem(storageKey, JSON.stringify(request)); }
    catch { throw { code: 'work.reslot_storage_unavailable' }; }
    setIntent(request);
    const { data, error } = await api.POST('/api/v1/work/tasks/{id}/reslot', {
      params: { path: { id: task.id }, header: { 'Idempotency-Key': request.key } },
      body: { reason: request.reason },
    });
    if (error) throw error;
    return data;
  }, onSuccess: data => {
    try { sessionStorage.removeItem(storageKey); } catch { /* Retrying an old key remains safe. */ }
    setIntent(null); setReason(''); setTraceId(null);
    queries.setQueryData(['work-task', task.id], data.current);
    void queries.invalidateQueries({ queryKey: ['work-tasks'] });
  }, onError: (failure: unknown) => {
    const code = (failure as { code?: string })?.code ?? 'unknown';
    setErrorCode(code);
    // These are definite refusals before a revision can be appended.
    if (['work.completion_pending', 'work.receipt_pending', 'work.task_already_claimed', 'work.task_not_open',
      'work.pallet_empty', 'work.not_pallet_putaway', 'work.reslot_request_invalid', 'work.reslot_key_conflict'].includes(code)) {
      try { sessionStorage.removeItem(storageKey); } catch { /* Preserve recovery if storage is unavailable. */ }
      setIntent(null);
    }
    void queries.invalidateQueries({ queryKey: ['work-task', task.id] });
  } });
  if (task.kind !== 'Putaway' || !task.sscc) return null;
  const locale = currentLocale();
  const originalTrace = history[0]?.previousDecisionTraceId;
  return <section className="mb-[var(--space-5)] border-b border-[var(--color-border-subtle)] pb-[var(--space-4)]">
    <div className="flex flex-wrap items-center gap-[var(--space-2)]">
      <h4 className="m-0">{t('work.reslot.title')}</h4>
      <InfoHelp label={t('common.about', { topic: t('work.reslot.title') })}>{t('work.reslot.help')}</InfoHelp>
      {task.putawayAdviceStale ? <Badge tone="warning">{t('work.reslot.stale')}</Badge> : null}
    </div>
    {editable && (canStart || intent) ? <form className="my-[var(--space-3)] flex flex-wrap items-end gap-[var(--space-3)]" onSubmit={event => { event.preventDefault(); reslot.mutate(); }}>
      <Field label={t('work.reslot.reason')} htmlFor={`reslot-reason-${task.id}`}>
        <Input id={`reslot-reason-${task.id}`} required maxLength={500} value={intent?.reason ?? reason}
          disabled={!!intent || reslot.isPending} onChange={event => setReason(event.target.value)} />
      </Field>
      <Button type="submit" variant="primary" disabled={reslot.isPending || !(intent?.reason ?? reason).trim()}>
        {reslot.isPending ? t('work.reslot.working') : intent ? t('work.reslot.retry') : t('work.reslot.apply')}
      </Button>
    </form> : task.completionPending ? <p role="status">{t('errors.work.completion_pending')}</p> : editable && task.status === 'Assigned' ? <p>{t('work.reslot.handBack')}</p> : null}
    {errorCode ? <p role="alert" className="text-[var(--color-danger)]">{t([`errors.${errorCode}`, 'errors.unknown'])}</p> : null}
    {originalTrace ? <Button size="sm" onClick={() => setTraceId(originalTrace)}>{t('work.reslot.original')}</Button> : null}
    {history.length ? <ol className="my-[var(--space-3)] space-y-[var(--space-2)] pl-[var(--space-4)]">
      {history.map(row => <li key={row.revision}>
        <span>{row.reason}{' · '}{row.userName}{' · '}{formatDateTime(row.decidedAt, locale)}{' · '}</span>
        <Button size="sm" onClick={() => setTraceId(row.decisionTraceId)}>{t('work.reslot.revision', { revision: row.revision })}</Button>
      </li>)}
    </ol> : null}
    {traceId && trace.data ? <DecisionTrace trace={trace.data} /> : null}
    {traceId && trace.error ? <p role="alert">{t('errors.unknown')}</p> : null}
  </section>;
}
