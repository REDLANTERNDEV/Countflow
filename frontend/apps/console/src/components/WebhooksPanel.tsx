import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, InfoHelp, Input, Spinner } from '@countflow/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { useAuth } from '../auth.js';

export function WebhooksPanel() {
  const { user } = useAuth();
  return user?.roles.includes('Admin') ? <WebhookAdmin /> : null;
}
function WebhookAdmin() {
  const { t } = useTranslation(); const locale = currentLocale(); const queries = useQueryClient();
  const [name, setName] = useState(''); const [url, setUrl] = useState(''); const [cursor, setCursor] = useState('0');
  const [triggerName, setTriggerName] = useState(''); const [issued, setIssued] = useState<string | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null); const [errorCode, setErrorCode] = useState<string | null>(null);
  const subscriptions = useQuery({ queryKey: ['webhooks'], refetchInterval: 30_000, queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/webhooks/subscriptions'); if (error) throw error; return data;
  } });
  const triggers = useQuery({ queryKey: ['webhook-triggers'], refetchInterval: 30_000, queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/webhooks/triggers'); if (error) throw error; return data;
  } });
  const deliveries = useQuery({ queryKey: ['webhook-deliveries', expanded], enabled: expanded !== null, refetchInterval: 30_000, queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/webhooks/subscriptions/{id}/deliveries', { params: { path: { id: expanded! } } }); if (error) throw error; return data;
  } });
  const action = useMutation({ mutationFn: async (command: () => Promise<void>) => { setErrorCode(null); await command(); },
    onSuccess: () => { void queries.invalidateQueries({ queryKey: ['webhooks'] }); void queries.invalidateQueries({ queryKey: ['webhook-triggers'] }); void queries.invalidateQueries({ queryKey: ['webhook-deliveries'] }); },
    onError: (error: unknown) => setErrorCode((error as { code?: string }).code ?? 'unknown') });
  async function register() {
    const { data, error } = await api.POST('/api/v1/webhooks/subscriptions', { body: { name: name.trim(), url: url.trim(), afterSequence: Number(cursor) } });
    if (error) throw error; setIssued(data!.secret); setName(''); setUrl('');
  }
  async function trigger() {
    const { data, error } = await api.POST('/api/v1/webhooks/triggers', { body: { name: triggerName.trim() } });
    if (error) throw error; setIssued(new URL(data!.url, window.location.origin).href); setTriggerName('');
  }
  return <section className="mb-[var(--space-5)] border-b border-[var(--color-border-subtle)] pb-[var(--space-4)]">
    <div className="flex items-center gap-2"><h2>{t('webhooks.title')}</h2><InfoHelp label={t('common.about', { topic: t('webhooks.title') })}>{t('webhooks.help')}</InfoHelp></div>
    {issued ? <div className="mb-4 border border-[var(--color-accent)] p-3" role="status"><p>{t('integrations.issued.onlyOnce')}</p><Code className="block break-all">{issued}</Code><div className="mt-3 flex gap-2"><Button onClick={() => void navigator.clipboard?.writeText(issued)}>{t('integrations.issued.copy')}</Button><Button onClick={() => setIssued(null)}>{t('integrations.issued.dismiss')}</Button></div></div> : null}
    <form aria-label={t('webhooks.register')} onSubmit={event => { event.preventDefault(); action.mutate(register); }} className="flex flex-wrap items-end gap-3">
      <Field label={t('webhooks.name')} htmlFor="webhook-name"><Input id="webhook-name" required maxLength={100} value={name} onChange={e => setName(e.target.value)} /></Field>
      <Field label={t('webhooks.receiver')} htmlFor="webhook-url"><Input id="webhook-url" type="url" required maxLength={2048} value={url} onChange={e => setUrl(e.target.value)} /></Field>
      <Field label={t('webhooks.cursor')} htmlFor="webhook-cursor" hint={t('webhooks.cursorHelp')}><Input id="webhook-cursor" type="number" min={0} step={1} required value={cursor} onChange={e => setCursor(e.target.value)} className="w-28" /></Field>
      <Button disabled={action.isPending} type="submit">{t('webhooks.add')}</Button>
    </form>
    {errorCode || subscriptions.error || triggers.error ? <p role="alert">{t(`errors.${errorCode ?? 'unknown'}`)}</p> : null}
    {subscriptions.isPending ? <Spinner label={t('common.loading')} /> : <ul className="my-4">{subscriptions.data?.map(subscription => <li key={subscription.id} className="border-t border-[var(--color-border-subtle)] py-3">
      <div className="flex flex-wrap items-center gap-3"><strong>{subscription.name}</strong><Code className="break-all">{subscription.url}</Code><Badge tone={subscription.pendingState === 'DeadLetter' ? 'danger' : subscription.pendingState === 'Pending' ? 'warning' : 'neutral'}>{t(subscription.pendingState ? `webhooks.state.${subscription.pendingState}` : subscription.enabled ? 'webhooks.on' : subscription.verified ? 'webhooks.paused' : 'webhooks.unverified')}</Badge>
        {!subscription.verified ? <Button disabled={action.isPending} onClick={() => action.mutate(async () => { const { error } = await api.POST('/api/v1/webhooks/subscriptions/{id}/verify', { params: { path: { id: subscription.id } } }); if (error) throw error; })}>{t('webhooks.verify')}</Button> : <Button disabled={action.isPending} onClick={() => action.mutate(async () => { const { error } = await api.PUT('/api/v1/webhooks/subscriptions/{id}', { params: { path: { id: subscription.id } }, body: { enabled: !subscription.enabled } }); if (error) throw error; })}>{t(subscription.enabled ? 'webhooks.pause' : 'webhooks.enable')}</Button>}
        <Button onClick={() => setExpanded(expanded === subscription.id ? null : subscription.id)}>{t('webhooks.deliveries')}</Button>
        {subscription.pendingDeliveryId ? <Button disabled={action.isPending} onClick={() => action.mutate(async () => { const { error } = await api.POST('/api/v1/webhooks/subscriptions/{id}/retry', { params: { path: { id: subscription.id } } }); if (error) throw error; })}>{t('webhooks.retry')}</Button> : null}
      </div>
      {expanded === subscription.id ? deliveries.isPending ? <Spinner label={t('common.loading')} /> : deliveries.error ? <p role="alert">{t('errors.unknown')}</p> : <ul>{deliveries.data?.map(delivery => <li key={delivery.id} className="mt-2 flex flex-wrap items-center gap-3"><Code>{delivery.id}</Code><Badge tone={delivery.state === 'DeadLetter' ? 'danger' : delivery.state === 'Delivered' ? 'success' : 'warning'}>{t(`webhooks.state.${delivery.state}`)}</Badge><span>{t('webhooks.attempts')}: {delivery.attempts}</span><time>{formatDateTime(delivery.createdAt, locale)}</time>{delivery.lastErrorCode ? <span>{t(`errors.${delivery.lastErrorCode}`)}</span> : null}</li>)}</ul> : null}
    </li>)}</ul>}
    <div className="flex items-center gap-2"><h3>{t('webhooks.triggers')}</h3><InfoHelp label={t('common.about', { topic: t('webhooks.triggers') })}>{t('webhooks.triggerHelp')}</InfoHelp></div>
    <form aria-label={t('webhooks.triggers')} onSubmit={event => { event.preventDefault(); action.mutate(trigger); }} className="flex flex-wrap items-end gap-3">
      <Field label={t('webhooks.name')} htmlFor="webhook-trigger-name"><Input id="webhook-trigger-name" required maxLength={100} value={triggerName} onChange={e => setTriggerName(e.target.value)} /></Field><Button disabled={action.isPending} type="submit">{t('webhooks.add')}</Button>
    </form>
    <ul>{triggers.data?.map(trigger => <li key={trigger.id} className="mt-2 flex flex-wrap items-center gap-3"><strong>{trigger.name}</strong><Badge>{t(trigger.enabled ? 'webhooks.on' : 'webhooks.revoked')}</Badge>{trigger.pendingRequests > 0 ? <Badge tone="warning">{t('webhooks.state.Pending')}: {trigger.pendingRequests}</Badge> : null}{trigger.lastFinishedAt ? <time>{formatDateTime(trigger.lastFinishedAt, locale)}</time> : null}{trigger.enabled ? <Button disabled={action.isPending} onClick={() => action.mutate(async () => { const { error } = await api.DELETE('/api/v1/webhooks/triggers/{id}', { params: { path: { id: trigger.id } } }); if (error) throw error; })}>{t('integrations.keys.revoke')}</Button> : null}</li>)}</ul>
  </section>;
}
