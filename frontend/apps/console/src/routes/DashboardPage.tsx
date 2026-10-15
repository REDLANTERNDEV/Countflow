import type { components } from '@countflow/api-client';
import { currentLocale, formatDateTime, formatNumber, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, EmptyState, Field, InfoHelp, Numeric, Select, Spinner, Table, TableContainer, TBody, TD, TH, THead, TR } from '@countflow/ui';
import { useQuery } from '@tanstack/react-query';
import { useEffect, useRef, useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

type Day = components['schemas']['DailyThroughput'];
export function DashboardPage() {
  const { t } = useTranslation(); const locale = currentLocale();
  const [warehouseId, setWarehouseId] = useState(''); const [days, setDays] = useState(7);
  const warehouses = useQuery({ queryKey: ['warehouses'], queryFn: async () => { const { data, error } = await api.GET('/api/v1/warehouses'); if (error) throw error; return data; } });
  const selected = warehouses.data?.some(site => site.id === warehouseId && site.isActive) ? warehouseId : warehouses.data?.find(site => site.isActive)?.id || '';
  const throughput = useQuery({ queryKey: ['dashboard-throughput', selected, days], enabled: Boolean(selected), refetchInterval: 30_000,
    queryFn: async () => { const { data, error } = await api.GET('/api/v1/work/throughput', { params: { query: { warehouseId: selected, days } } }); if (error) throw error; return data; } });
  const density = useQuery({ queryKey: ['dashboard-density', selected], enabled: Boolean(selected), refetchInterval: 30_000,
    queryFn: async () => { const { data, error } = await api.GET('/api/v1/inventory/zone-density', { params: { query: { warehouseId: selected } } }); if (error) throw error; return data; } });
  const accuracy = useQuery({ queryKey: ['dashboard-accuracy', selected, throughput.data?.from, throughput.data?.to], enabled: Boolean(selected && throughput.data),
    queryFn: async () => { const { data, error } = await api.GET('/api/v1/inventory/accuracy', { params: { query: { warehouseId: selected, from: throughput.data!.from, to: throughput.data!.to } } }); if (error) throw error; return data; } });
  const failed = warehouses.error || throughput.error || density.error || accuracy.error;
  const percent = (ratio: number | null | undefined) => ratio == null ? '—' : `${formatNumber(ratio * 100, locale, { maximumFractionDigits: 1 })}%`;
  const refresh = () => { void warehouses.refetch(); if (selected) { void throughput.refetch(); void density.refetch(); if (throughput.data) void accuracy.refetch(); } };
  const observations = [throughput.data?.observedAt, density.data?.observedAt].filter((value): value is string => Boolean(value));
  const observedAt = observations.length ? new Date(Math.min(...observations.map(value => Date.parse(value)))).toISOString() : undefined;
  const metric = (label: string, value: number | string | undefined, detail?: string) => <div className="border-b border-[var(--color-border-subtle)] pb-4">
    <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">{label}</span><p className="m-0 mt-1 text-[length:var(--text-2xl)]">{typeof value === 'number' ? <Numeric value={value} locale={locale} /> : value ?? '—'}</p>{detail ? <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">{detail}</span> : null}
  </div>;
  return <>
    <PageHead title={t('dashboard.title')} help={t('dashboard.help')} />
    <div className="mb-5 flex flex-wrap items-end gap-3">
      <Field label={t('nav.warehouses')} htmlFor="dashboard-warehouse"><Select id="dashboard-warehouse" value={selected} onChange={event => setWarehouseId(event.target.value)}>{warehouses.data?.filter(site => site.isActive).map(site => <option key={site.id} value={site.id}>{site.code}</option>)}</Select></Field>
      <Field label={t('dashboard.window')} htmlFor="dashboard-window"><Select id="dashboard-window" value={days} onChange={event => setDays(Number(event.target.value))}>{[7, 30, 90].map(value => <option key={value} value={value}>{t(`dashboard.windows.${value}`)}</option>)}</Select></Field>
      <Button onClick={refresh} disabled={warehouses.isFetching}>{t('dashboard.refresh')}</Button>
      {observedAt ? <time className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]" dateTime={observedAt}>{t('dashboard.updated')}: {formatDateTime(observedAt, locale)}</time> : null}
      {failed ? <Badge tone="danger" role="alert">{t('dashboard.refreshFailed')}</Badge> : null}
    </div>
    {warehouses.isPending || (selected && (throughput.isPending || density.isPending || accuracy.isPending)) ? <Spinner label={t('common.loading')} /> : null}
    {!warehouses.isPending && !warehouses.error && !selected ? <EmptyState title={t('dashboard.noWarehouse')} /> : null}
    {selected ? <>
      <div className="grid grid-cols-[repeat(auto-fit,minmax(140px,1fr))] gap-5">
        {metric(t('dashboard.completed'), throughput.data?.completed)}{metric(t('dashboard.ready'), throughput.data?.ready)}{metric(t('dashboard.assigned'), throughput.data?.assigned)}
        {metric(t('dashboard.accuracy'), percent(accuracy.data?.overall.accuracyRate), accuracy.data ? `${t('dashboard.counted')}: ${formatNumber(accuracy.data.overall.counted, locale)}` : undefined)}
        {metric(t('dashboard.occupied'), percent(density.data?.overall.occupancyRate), density.data ? `${formatNumber(density.data.overall.occupied, locale)}/${formatNumber(density.data.overall.locations, locale)}` : undefined)}
      </div>
      <section className="mt-6"><div className="flex flex-wrap items-center gap-2"><h2 className="m-0 text-[length:var(--text-xl)]">{t('dashboard.throughput')}</h2><InfoHelp label={t('common.about', { topic: t('dashboard.throughput') })}>{t('dashboard.throughputHelp')}</InfoHelp><Badge>UTC</Badge></div>
        {throughput.data ? <><ThroughputChart days={throughput.data.days} />
          <details className="mt-3"><summary className="cursor-pointer rounded-[var(--radius-control)] py-3 focus-visible:outline-2 focus-visible:outline-[var(--color-accent)]">{t('dashboard.dailyTotals')}</summary><TableContainer><Table><THead><TR><TH>{t('dashboard.date')}</TH><TH>{t('dashboard.completed')}</TH></TR></THead><TBody>{throughput.data.days.map(day => <TR key={day.date}><TD><time dateTime={day.date}>{new Intl.DateTimeFormat(locale, { dateStyle: 'medium', timeZone: 'UTC' }).format(new Date(`${day.date}T12:00:00Z`))}</time></TD><TD><Numeric value={day.completed} locale={locale} /></TD></TR>)}</TBody></Table></TableContainer></details>
          {throughput.data.byKind.length ? <div className="mt-3 flex flex-wrap gap-4">{throughput.data.byKind.map(kind => <span key={kind.kind}>{t(`work.kind.${kind.kind}`)}: <Numeric value={kind.completed} locale={locale} /></span>)}</div> : <EmptyState title={t('dashboard.noCompletions')} />}
        </> : null}
      </section>
      <section className="mt-6"><div className="flex items-center gap-2"><h2 className="m-0 text-[length:var(--text-xl)]">{t('dashboard.density')}</h2><InfoHelp label={t('common.about', { topic: t('dashboard.density') })}>{t('dashboard.densityHelp')}</InfoHelp></div>
        {density.data?.zones.length ? <TableContainer><Table><THead><TR><TH>{t('dashboard.zone')}</TH><TH>{t('dashboard.slots')}</TH><TH>{t('dashboard.occupied')}</TH><TH>{t('dashboard.cube')}</TH><TH>{t('dashboard.capacityKnown')}</TH></TR></THead><TBody>{density.data.zones.map(zone => <TR key={zone.zoneId ?? 'unzoned'}><TD><Code>{zone.zoneId ? [zone.areaCode, zone.zoneCode].filter(Boolean).join(' · ') : t('dashboard.unzoned')}</Code></TD><TD><Numeric value={zone.occupied} locale={locale} />/<Numeric value={zone.locations} locale={locale} /></TD><TD>{percent(zone.occupancyRate)}</TD><TD>{zone.cubeUtilization != null && zone.cubeUtilization > 1 ? <Badge tone="warning">{percent(zone.cubeUtilization)}</Badge> : percent(zone.cubeUtilization)}</TD><TD><Numeric value={zone.knownCapacityLocations} locale={locale} />/<Numeric value={zone.locations} locale={locale} /></TD></TR>)}</TBody></Table></TableContainer> : density.data ? <EmptyState title={t('dashboard.noStorage')} /> : null}
      </section>
    </> : null}
  </>;
}
function ThroughputChart({ days }: { days: Day[] }) {
  const { t } = useTranslation(); const locale = currentLocale(); const maximum = Math.max(1, ...days.map(day => day.completed));
  const chart = useRef<SVGSVGElement>(null); const [width, setWidth] = useState(700);
  useEffect(() => {
    if (!chart.current) return;
    const observer = new ResizeObserver(([entry]) => { if (entry && entry.contentRect.width > 0) setWidth(Math.round(entry.contentRect.width)); });
    observer.observe(chart.current);
    return () => observer.disconnect();
  }, []);
  const step = width / Math.max(1, days.length); const labelEvery = Math.max(1, Math.ceil(days.length / Math.max(2, Math.min(6, Math.floor(width / 100)))));
  const date = (value: string) => new Intl.DateTimeFormat(locale, { day: '2-digit', month: 'short', timeZone: 'UTC' }).format(new Date(`${value}T12:00:00Z`));
  return <svg ref={chart} role="img" aria-label={t('dashboard.throughput')} viewBox={`0 0 ${width} 180`} className="h-[180px] w-full text-[var(--color-text-secondary)]">
    <line x1="0" y1="135" x2={width} y2="135" stroke="var(--color-border-default)" />
    {days.map((day, index) => { const height = day.completed / maximum * 120; return <g key={day.date}>
      <rect x={index * step + step * 0.12} y={135 - height} width={step * 0.76} height={height} fill="var(--color-accent)"><title>{date(day.date)}: {formatNumber(day.completed, locale)}</title></rect>
      {(index % labelEvery === 0 || index === days.length - 1) ? <text x={Math.max(32, Math.min(width - 32, index * step + step / 2))} y="165" textAnchor="middle" fill="currentColor" fontSize="var(--text-sm)">{date(day.date)}</text> : null}
    </g>; })}
  </svg>;
}
