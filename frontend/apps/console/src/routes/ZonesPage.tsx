import type { components } from '@countflow/api-client';
import { useTranslation } from '@countflow/i18n';
import { InfoHelp, Button, Code, Field, Select, Spinner } from '@countflow/ui';
import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { useAuth } from '../auth.js';
import { PageHead } from '../components/PageHead.js';

type Zone = components['schemas']['ZoneResponse'];
export function ZonesPage() {
  const { t } = useTranslation();
  const [warehouse, setWarehouse] = useState('');
  const warehouses = useQuery({ queryKey: ['warehouses'], queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/warehouses');
    if (error || !data) throw error; return data;
  }});
  const zones = useQuery({ queryKey: ['zones', warehouse], queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/zones', { params: { query: { ...(warehouse ? { warehouse } : {}) } } });
    if (error || !data) throw error; return data;
  }});
  return <>
    <PageHead title={t('zones.title')} count={zones.data?.length} />
    <Field label={t('zones.warehouse')} htmlFor="zone-warehouse">
      <Select id="zone-warehouse" value={warehouse} onChange={event => setWarehouse(event.target.value)}>
        <option value="">{t('common.all')}</option>
        {warehouses.data?.map(w => <option key={w.id} value={w.code}>{w.code}</option>)}
      </Select>
    </Field>
    {zones.isPending ? <Spinner label={t('common.loading')} /> : null}
    {zones.error || warehouses.error ? <p role="alert">{t('errors.unknown')}</p> : null}
    <div className="mt-[var(--space-4)] grid gap-[var(--space-4)]">
      {zones.data?.map(zone => <ZoneEditor key={`${warehouse}:${zone.id}:${zone.pickRouteStrategy}:${zone.hasRearCrossAisle}`} zone={zone} />)}
    </div>
  </>;
}
function ZoneEditor({ zone }: { zone: Zone }) {
  const { t } = useTranslation();
  const { user } = useAuth();
  const query = useQueryClient();
  const editable = user?.roles.some(role => role === 'Admin' || role === 'Supervisor');
  const [strategy, setStrategy] = useState(zone.pickRouteStrategy);
  const [rear, setRear] = useState(zone.hasRearCrossAisle);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  async function save() {
    setBusy(true); setError(null);
    try {
      const { error } = await api.PUT('/api/v1/zones/{id}/pick-route', {
        params: { path: { id: zone.id } }, body: { strategy, hasRearCrossAisle: rear },
        headers: { 'Idempotency-Key': crypto.randomUUID() },
      });
      if (error) { setError((error as { code?: string }).code ?? 'unknown'); return; }
      await query.invalidateQueries({ queryKey: ['zones'] });
    } catch { setError('unknown'); } finally { setBusy(false); }
  }
  const support = (code: string) => {
    const option = zone.routeOptions.find(option => option.code === code);
    return !!option?.hasRequiredGeometry && (!option.requiresRearCrossAisle || rear);
  };
  return <section className="border-b border-[var(--color-border-subtle)] pb-[var(--space-4)]">
    <h3 className="flex flex-wrap items-center"><Code>{zone.code}</Code> · {zone.name} · <Code>{zone.areaCode}</Code>
      <InfoHelp label={t('common.about', { topic: zone.code })}>
        {zone.routeOptions.find(option => option.code === 'route.return')?.hasRequiredGeometry
          ? t('zones.needsRear') : t('zones.noGeometry')}
      </InfoHelp>
    </h3>
    <div className="flex flex-wrap items-end gap-[var(--space-4)]">
      <Field label={t('zones.strategy')} htmlFor={`strategy-${zone.id}`}>
        <Select id={`strategy-${zone.id}`} value={strategy} disabled={!editable || busy} onChange={event => setStrategy(event.target.value)}>
          {zone.routeOptions.map(option => <option key={option.code} value={option.code} disabled={!support(option.code)}>
            {t(`zones.strategies.${option.code.replace('route.', '')}`)}
          </option>)}
        </Select>
      </Field>
      <label className="flex min-h-[44px] items-center gap-[var(--space-2)]">
        <input type="checkbox" checked={rear} disabled={!editable || busy} onChange={event => setRear(event.target.checked)} />
        {t('zones.rear')}
      </label>
      <span>{t('zones.geometry', { aisles: zone.aisles, bays: zone.bays })}</span>
      {editable ? <Button disabled={busy || !support(strategy) || (strategy === zone.pickRouteStrategy && rear === zone.hasRearCrossAisle)} onClick={() => void save()}>{t('common.save')}</Button> : null}
    </div>
    {error ? <p role="alert">{t([`errors.${error}`, 'errors.unknown'])}</p> : null}
  </section>;
}
