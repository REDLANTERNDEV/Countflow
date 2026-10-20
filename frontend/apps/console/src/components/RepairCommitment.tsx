import { disabledInDemo } from '@countflow/demo/capabilities';
import type { components } from '@countflow/api-client';
import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, InfoHelp, Input, Numeric } from '@countflow/ui';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { useAuth } from '../auth.js';

type Line = components['schemas']['OrderLineResponse'];
type Intent = { key: string; reason: string };
function readIntent(key: string): Intent | null {
  try {
    const value: unknown = JSON.parse(sessionStorage.getItem(key) ?? 'null');
    if (value && typeof value === 'object' && 'key' in value && 'reason' in value &&
      typeof value.key === 'string' && typeof value.reason === 'string') return { key: value.key, reason: value.reason };
  } catch { /* Submit reports unavailable storage before sending. */ }
  return null;
}

export function RepairCommitment({ orderId, status, line }: { orderId: string; status: string; line: Line }) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const queries = useQueryClient();
  const locale = currentLocale();
  const storageKey = `countflow.repair:${user?.id}:${line.id}`;
  const [intent, setIntent] = useState<Intent | null>(() => readIntent(storageKey));
  const [expanded, setExpanded] = useState(false);
  const [reason, setReason] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [shortfall, setShortfall] = useState<number | null>(null);
  const pending = line.repairs?.find(row => !row.completedAt);
  const retry = pending ? { key: pending.idempotencyKey, reason: pending.reason } : intent;
  const editable = user?.roles.some(role => role === 'Admin' || role === 'Supervisor');
  const repair = useMutation({ mutationFn: async () => {
    setErrorCode(null);
    const request = retry ?? { key: crypto.randomUUID(), reason: reason.trim() };
    if (!pending) {
      try { sessionStorage.setItem(storageKey, JSON.stringify(request)); }
      catch { throw { code: 'outbound.repair_storage_unavailable' }; }
    }
    setIntent(request);
    const { data, error } = await api.POST('/api/v1/outbound/orders/{id}/lines/{lineId}/repair', {
      params: { path: { id: orderId, lineId: line.id }, header: { 'Idempotency-Key': request.key } }, body: { reason: request.reason },
    });
    if (error) throw error;
    return data;
  }, onSuccess: data => {
    try { sessionStorage.removeItem(storageKey); } catch { /* A replay is safe. */ }
    setIntent(null); setReason(''); setExpanded(false); setShortfall(data.shortfallQuantity);
    void queries.invalidateQueries({ queryKey: ['sales-orders'] });
    void queries.invalidateQueries({ queryKey: ['stranded-reservations'] });
    void queries.invalidateQueries({ queryKey: ['waves'] });
  }, onError: (failure: unknown) => {
    const code = (failure as { code?: string })?.code ?? 'unknown'; setErrorCode(code);
    // These refusals precede journal creation. Ambiguous failures keep their
    // original key and body; the server also publishes unfinished intents.
    if (!pending && ['outbound.no_stranded_commitment', 'outbound.picks_in_progress', 'outbound.repair_reason_required', 'outbound.order_not_releasable'].includes(code)) {
      try { sessionStorage.removeItem(storageKey); } catch { /* Preserve recovery. */ }
      setIntent(null);
    }
    void queries.invalidateQueries({ queryKey: ['sales-orders'] });
  } });
  const canStart = status === 'Released' && line.allocations.some(row => !row.supersededByRepairId && row.pickedQuantity === 0);
  if (!canStart && !line.repairs?.length && !intent) return null;
  return <div className="my-[var(--space-3)] border-t border-[var(--color-border-subtle)] pt-[var(--space-3)]">
    <div className="flex flex-wrap items-center gap-[var(--space-2)]">
      <Code>{line.sku}</Code><span>{t('outbound.repair.title')}</span>
      <InfoHelp label={t('common.about', { topic: t('outbound.repair.title') })}>{t('outbound.repair.help')}</InfoHelp>
      {pending ? <Badge tone="warning">{t('outbound.repair.pending')}</Badge> : null}
      {editable && (canStart || retry) && !expanded ? <Button size="sm" onClick={() => setExpanded(true)} {...disabledInDemo(t('demo.unavailable'))}>{retry ? t('outbound.repair.resume') : t('outbound.repair.start')}</Button> : null}
    </div>
    {editable && expanded ? <form className="mt-[var(--space-3)] flex flex-wrap items-end gap-[var(--space-3)]" aria-label={`${t('outbound.repair.title')} ${line.sku}`} onSubmit={event => { event.preventDefault(); repair.mutate(); }}>
      <Field label={t('outbound.repair.reason')} htmlFor={`repair-reason-${line.id}`}>
        <Input id={`repair-reason-${line.id}`} required maxLength={500} value={retry?.reason ?? reason} disabled={!!retry || repair.isPending} onChange={event => setReason(event.target.value)} />
      </Field>
      <Button type="submit" variant="primary" disabled={repair.isPending || !(retry?.reason ?? reason).trim()} {...disabledInDemo(t('demo.unavailable'))}>{repair.isPending ? t('outbound.repair.working') : retry ? t('outbound.repair.resume') : t('outbound.repair.start')}</Button>
      {!retry ? <Button type="button" onClick={() => setExpanded(false)}>{t('outbound.hideWhy')}</Button> : null}
    </form> : null}
    {errorCode ? <p role="alert" className="text-[var(--color-danger)]">{t([`errors.${errorCode}`, 'errors.unknown'])}</p> : null}
    {shortfall !== null ? <p role="status">{t('outbound.repair.done')} {shortfall > 0 ? <>{t('outbound.outstanding')}: <Numeric value={shortfall} locale={locale} /> {line.baseUom}</> : null}</p> : null}
    {line.repairs?.map(row => <p key={row.id} className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">{row.completedAt ? t('outbound.repair.done') : t('outbound.repair.pending')}{' · '}{row.reason}{' · '}{row.requestedByUserName}{' · '}{formatDateTime(row.createdAt, locale)}</p>)}
  </div>;
}
