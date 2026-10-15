import type { components } from '@countflow/api-client';
import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, InfoHelp, Input, Spinner } from '@countflow/ui';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { useAuth } from '../auth.js';

type Schedule = components['schemas']['CountScheduleResponse'];

export function CountSchedules() {
  const { t } = useTranslation();
  const schedules = useQuery({ queryKey: ['count-schedules'], refetchInterval: 30_000, queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/counting/schedules');
    if (error) throw error;
    return data;
  } });
  return <section className="mb-[var(--space-5)] border-b border-[var(--color-border-subtle)] pb-[var(--space-4)]">
    <h2 className="flex items-center font-semibold">
      {t('counting.schedules')}
      <InfoHelp label={t('common.about', { topic: t('counting.schedules') })}>{t('counting.scheduleHelp')}</InfoHelp>
    </h2>
    {schedules.isPending ? <Spinner label={t('common.loading')} /> : null}
    {schedules.error ? <p role="alert">{t('errors.unknown')}</p> : null}
    {schedules.data?.map(schedule => <ScheduleEditor key={`${schedule.warehouseId}:${schedule.enabled}:${schedule.intervalMinutes}:${schedule.maxTasks}`} schedule={schedule} />)}
  </section>;
}

function ScheduleEditor({ schedule }: { schedule: Schedule }) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const queries = useQueryClient();
  const editable = user?.roles.some(role => role === 'Admin' || role === 'Supervisor');
  const [enabled, setEnabled] = useState(schedule.enabled);
  const [interval, setInterval] = useState(String(schedule.intervalMinutes));
  const [budget, setBudget] = useState(String(schedule.maxTasks));
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const save = useMutation({ mutationFn: async () => {
    setErrorCode(null);
    const { data, error } = await api.PUT('/api/v1/counting/schedules/{warehouseId}', {
      params: { path: { warehouseId: schedule.warehouseId } },
      body: { enabled, intervalMinutes: Number(interval), maxTasks: Number(budget) },
    });
    if (error) throw error;
    return data;
  }, onSuccess: () => { void queries.invalidateQueries({ queryKey: ['count-schedules'] }); },
  onError: (error: unknown) => setErrorCode((error as { code?: string })?.code ?? 'unknown') });
  return <form aria-label={`${t('counting.schedules')} ${schedule.warehouseCode}`} onSubmit={event => { event.preventDefault(); save.mutate(); }}>
    <div className="mt-[var(--space-3)] flex flex-wrap items-end gap-[var(--space-3)]">
      <Code>{schedule.warehouseCode}</Code>
      <label className="flex min-h-[44px] items-center gap-[var(--space-2)]">
        <input type="checkbox" checked={enabled} disabled={!editable || save.isPending} onChange={event => setEnabled(event.target.checked)} />
        {t('counting.scheduleEnabled')}
      </label>
      <Field label={t('counting.scheduleInterval')} htmlFor={`interval-${schedule.warehouseId}`} hint={t('counting.scheduleIntervalRange')} hintMode="instruction">
        <Input id={`interval-${schedule.warehouseId}`} type="number" min={1} max={10080} step={1} required value={interval} disabled={!editable || save.isPending} onChange={event => setInterval(event.target.value)} className="w-32" />
      </Field>
      <Field label={t('counting.scheduleBudget')} htmlFor={`budget-${schedule.warehouseId}`} hint="1–200" hintMode="instruction">
        <Input id={`budget-${schedule.warehouseId}`} type="number" min={1} max={200} step={1} required value={budget} disabled={!editable || save.isPending} onChange={event => setBudget(event.target.value)} className="w-24" />
      </Field>
      {editable ? <Button type="submit" disabled={save.isPending || (enabled === schedule.enabled && Number(interval) === schedule.intervalMinutes && Number(budget) === schedule.maxTasks)}>{t('common.save')}</Button> : null}
      <Badge tone={schedule.isPending ? 'info' : 'neutral'}>{t(schedule.isPending ? 'counting.schedulePending' : schedule.enabled ? 'counting.scheduleOn' : 'counting.scheduleOff')}</Badge>
      {schedule.nextRunAt ? <span className="text-sm">{t('counting.scheduleNext')}: {formatDateTime(schedule.nextRunAt, currentLocale())}</span> : null}
    </div>
    {errorCode || schedule.lastErrorCode ? <p role="alert" className="mt-[var(--space-2)] text-[var(--color-danger-text)]">{t(`errors.${errorCode ?? schedule.lastErrorCode}`)}</p> : null}
  </form>;
}
