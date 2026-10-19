import { currentLocale, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Numeric, Spinner } from '@countflow/ui';
import { useEffect, useState } from 'react';
import type { components } from '@countflow/api-client';
import { api, storageKey as scopedStorageKey } from '../api.js';
import { outbox } from '../outbox.js';
import { rememberTask, type TaskDetail } from '../tasks.js';
import { hasUnsentTourWork, tourStops } from '../lib/pick-tours.js';
import { unanswered } from '../lib/outbox/dispatch.js';
import { useOutbox } from '../useOutbox.js';

type Task = components['schemas']['WorkTaskResponse'];
export function PickTourScreen({ tourId, ownerUserId, online, onOpenTask, onBack }: {
  tourId: string; ownerUserId: string; online: boolean;
  onOpenTask: (taskId: string) => void; onBack: () => void;
}) {
  const { t } = useTranslation();
  const { commands } = useOutbox();
  const [tasks, setTasks] = useState<Task[]>([]);
  const [loading, setLoading] = useState(true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [stale, setStale] = useState(false);
  const [saved, setSaved] = useState<number | null>(null);
  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const { data, response, error } = await api.GET('/api/v1/work/tours/{id}', {
          params: { path: { id: tourId } },
        });
        outbox.noteReachable(!unanswered(response.status));
        if (data) {
          const cached = await outbox.store.readTasks(ownerUserId);
          for (const row of cached) {
            const task = (row.detail as TaskDetail).task;
            if (task.pickTourId === tourId && !data.stops.some(stop => stop.id === task.id)
                && !hasUnsentTourWork([task.id], outbox.getSnapshot().commands)) {
              await outbox.store.forgetTask(task.id);
            }
          }
          for (const task of data.stops) await rememberTask({ task, decision: null }, ownerUserId);
          if (!cancelled) { setTasks(data.stops); setSaved(data.stepsSaved ?? null); setStale(false); }
          return;
        }
        if (!unanswered(response.status)) {
          if (!cancelled) setError((error as { code?: string })?.code ?? 'unknown');
          return;
        }
      } catch { outbox.noteReachable(false); }
      const cached = await outbox.store.readTasks(ownerUserId);
      if (!cancelled) {
        setTasks(cached.map(row => (row.detail as TaskDetail).task).filter(task => task.pickTourId === tourId));
        setStale(true);
      }
    }
    void load().finally(() => { if (!cancelled) setLoading(false); });
    return () => { cancelled = true; };
  }, [tourId, ownerUserId, commands]);
  const mine = commands.filter(command => command.ownerUserId === ownerUserId);
  const ids = tasks.map(task => task.id);
  const pending = hasUnsentTourWork(ids, mine);
  const recorded = new Set(mine.filter(command => command.kind === 'work.complete')
    .map(command => (command.request as { taskId: string }).taskId));
  const remaining = tourStops(tasks, tourId).filter(task => !recorded.has(task.id));
  async function release() {
    setBusy(true); setError(null);
    try {
      // Re-check physical work immediately before releasing, not just at render.
      if (hasUnsentTourWork(ids, outbox.getSnapshot().commands.filter(c => c.ownerUserId === ownerUserId))) return;
      const storageKey = scopedStorageKey(`countflow.tour.release.${ownerUserId}.${tourId}`);
      const key = localStorage.getItem(storageKey) ?? crypto.randomUUID();
      localStorage.setItem(storageKey, key);
      const { response, error } = await api.POST('/api/v1/work/tours/{id}/release', {
        params: { path: { id: tourId } }, headers: { 'Idempotency-Key': key },
      });
      outbox.noteReachable(!unanswered(response.status));
      if (!response.ok) { setError(unanswered(response.status) ? 'offline' : (error as { code?: string })?.code ?? 'unknown'); return; }
      for (const task of tasks.filter(task => task.status === 'Assigned')) await outbox.store.forgetTask(task.id);
      localStorage.removeItem(storageKey);
      onBack();
    } catch { outbox.noteReachable(false); setError('offline'); }
    finally { setBusy(false); }
  }
  return <>
    <h1 className="m-0 text-[length:var(--text-xl)]">{t('pickTours.title')}</h1>
    {loading ? <Spinner label={t('common.loading')} /> : null}
    {stale ? <Badge tone="warning">{t('pickTours.cached')}</Badge> : null}
    {saved !== null ? <span>{t('pickTours.saved', { count: saved })}</span> : null}
    {pending ? <Badge tone="warning">{t('pickTours.pending')}</Badge> : null}
    {error ? <p role="alert">{t([`errors.${error}`, 'errors.unknown'])}</p> : null}
    {!loading && !error && remaining.length === 0 ? <p>{t(pending ? 'pickTours.pending' : 'pickTours.noStops')}</p> : null}
    {remaining.map(task => <Button key={task.id}
      className="h-auto w-full flex-col items-start whitespace-normal py-[var(--space-3)] text-left"
      onClick={() => onOpenTask(task.id)}>
      <span className="flex min-w-0 gap-[var(--space-2)]"><span>{task.tourSequence}.</span>
        <Code className="break-all">{task.fromLocationCode}</Code></span>
      <span><Code>{task.sku}</Code> · <Numeric value={task.quantity} locale={currentLocale()} /> {task.baseUom}</span>
    </Button>)}
    <Button disabled={!online || busy || loading || pending || remaining.length === 0 || !!error}
      onClick={() => void release()}>{t('pickTours.release')}</Button>
    <Button variant="ghost" onClick={onBack}>{t('common.close')}</Button>
  </>;
}
