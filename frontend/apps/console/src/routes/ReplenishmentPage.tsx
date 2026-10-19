import { disabledInDemo } from '@countflow/demo/capabilities';
import type { components } from '@countflow/api-client';
import { currentLocale, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, EmptyState, Field, Input, Numeric, Select, Spinner, Table, TableContainer, TBody, TD, TH, THead, TR } from '@countflow/ui';
import { keepPreviousData, useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { Fragment, useState } from 'react';
import { api } from '../api.js';
import { useAuth } from '../auth.js';
import { DecisionTrace } from '../components/DecisionTrace.js';
import { PageHead } from '../components/PageHead.js';

type Policy = components['schemas']['PickFacePolicyResponse'];

export function ReplenishmentPage() {
  const { t } = useTranslation();
  const { user } = useAuth();
  const locale = currentLocale();
  const editable = user?.roles.some(role => role === 'Admin' || role === 'Supervisor');
  const [warehouse, setWarehouse] = useState('');
  const [page, setPage] = useState(1);
  const [editing, setEditing] = useState<Policy | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [traceId, setTraceId] = useState<string | null>(null);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const queries = useQueryClient();
  const warehouses = useQuery({ queryKey: ['warehouses'], queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/warehouses'); if (error) throw error; return data;
  } });
  const policies = useQuery({ queryKey: ['pick-faces', warehouse, page], placeholderData: keepPreviousData, refetchInterval: 30_000,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/replenishment/pick-faces', { params: { query: { warehouse, page, pageSize: 25 } } });
      if (error) throw error; return data;
    } });
  const plan = useQuery({ queryKey: ['replenishment-plan', expanded], enabled: expanded !== null, queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/replenishment/plans/{id}', { params: { path: { id: expanded! } } });
    if (error) throw error; return data;
  } });
  const trace = useQuery({ queryKey: ['decision-trace', traceId], enabled: traceId !== null, queryFn: async () => {
    const { data, error } = await api.GET('/api/v1/rules/traces/{id}', { params: { path: { id: traceId! } } });
    if (error) throw error; return data;
  } });
  const cancel = useMutation({ mutationFn: async (id: string) => {
    setErrorCode(null);
    const { error } = await api.POST('/api/v1/replenishment/plans/{id}/cancel', { params: { path: { id } } });
    if (error) throw error;
  }, onSuccess: () => {
    void queries.invalidateQueries({ queryKey: ['pick-faces'] });
    void queries.invalidateQueries({ queryKey: ['replenishment-plan'] });
  }, onError: (error: unknown) => setErrorCode((error as { code?: string })?.code ?? 'unknown') });
  return <>
    <PageHead title={t('replenishment.title')} help={t('replenishment.help')} count={policies.data?.total} />
    {editable ? <PolicyEditor key={editing?.locationId ?? 'new'} policy={editing} onSaved={() => setEditing(null)} onCancel={() => setEditing(null)} /> : null}
    <Field label={t('nav.warehouses')} htmlFor="replenishment-warehouse">
      <Select id="replenishment-warehouse" value={warehouse} onChange={event => { setWarehouse(event.target.value); setPage(1); }}>
        <option value="">{t('common.all')}</option>
        {warehouses.data?.map(site => <option key={site.id} value={site.code}>{site.code}</option>)}
      </Select>
    </Field>
    {errorCode || policies.error || warehouses.error ? <p role="alert">{t(`errors.${errorCode ?? 'unknown'}`)}</p> : null}
    {policies.isPending ? <Spinner label={t('common.loading')} /> : policies.data?.items.length === 0 ? <EmptyState title={t('replenishment.empty')} /> : <TableContainer className="mt-[var(--space-4)]">
      <Table><THead><TR>
        <TH>{t('replenishment.face')}</TH><TH>{t('items.sku')}</TH><TH>{t('replenishment.minimum')}</TH><TH>{t('replenishment.maximum')}</TH>
        <TH>{t('replenishment.onHand')}</TH><TH>{t('replenishment.available')}</TH><TH>{t('replenishment.incoming')}</TH><TH>{t('replenishment.state')}</TH><TH>{t('replenishment.actions')}</TH>
      </TR></THead><TBody>{policies.data?.items.map(policy => <Fragment key={policy.locationId}>
        <TR>
          <TD><Code>{policy.locationCode}</Code></TD><TD><Code>{policy.sku}</Code><span className="ml-2 text-sm">{policy.baseUom}</span></TD>
          <TD><Numeric locale={locale} value={policy.minimum} maximumFractionDigits={4} /></TD><TD><Numeric locale={locale} value={policy.maximum} maximumFractionDigits={4} /></TD><TD><Numeric locale={locale} value={policy.onHandQuantity} maximumFractionDigits={4} /></TD>
          <TD><Numeric locale={locale} value={policy.availableQuantity} maximumFractionDigits={4} /></TD><TD><Numeric locale={locale} value={policy.incomingQuantity} maximumFractionDigits={4} /></TD>
          <TD><Badge tone={policy.pendingPlanId ? 'info' : policy.lastErrorCode ? 'warning' : 'neutral'}>{t(policy.pendingPlanId ? 'replenishment.pending' : policy.enabled ? 'replenishment.on' : 'replenishment.off')}</Badge>
            {policy.lastErrorCode ? <p>{t(`errors.${policy.lastErrorCode}`)}</p> : null}
            {policy.shortfallQuantity > 0 ? <p>{t('replenishment.short')}: <Numeric locale={locale} value={policy.shortfallQuantity} maximumFractionDigits={4} /></p> : null}
          </TD>
          <TD><div className="flex gap-2">
            {editable ? <Button size="sm" onClick={() => setEditing(policy)}>{t('replenishment.edit')}</Button> : null}
            {(policy.pendingPlanId ?? policy.lastPlanId) ? <Button size="sm" onClick={() => setExpanded(expanded === (policy.pendingPlanId ?? policy.lastPlanId) ? null : (policy.pendingPlanId ?? policy.lastPlanId))}>{t('replenishment.plan')}</Button> : null}
            {policy.lastDecisionTraceId ? <Button size="sm" onClick={() => setTraceId(traceId === policy.lastDecisionTraceId ? null : policy.lastDecisionTraceId)}>{t('work.whyHere')}</Button> : null}
          </div></TD>
        </TR>
        {expanded && expanded === (policy.pendingPlanId ?? policy.lastPlanId) ? <TR><TD colSpan={9}>
          {plan.isPending ? <Spinner label={t('common.loading')} /> : plan.error ? <p role="alert">{t('errors.unknown')}</p> : plan.data ? <>
            <div className="flex items-center gap-3"><Code>{plan.data.reference}</Code><Badge>{t(`replenishment.planState.${plan.data.status}`)}</Badge>
              {editable && plan.data.status === 'Open' ? <Button variant="danger" disabled={cancel.isPending} onClick={() => cancel.mutate(plan.data!.id)} {...disabledInDemo(t('demo.unavailable'))}>{t('replenishment.withdraw')}</Button> : null}
            </div>
            <ul>{plan.data.lines.map(line => <li key={line.id} className="flex flex-wrap items-center gap-3 py-2"><Code>{line.fromLocationCode}</Code><Numeric locale={locale} value={line.quantity} maximumFractionDigits={4} />
              <Badge>{line.refused ? t('replenishment.refused') : t(`work.status.${line.state ?? 'Ready'}`)}</Badge>
              {line.errorCode ? <span>{t(`errors.${line.errorCode}`)}</span> : null}
            </li>)}</ul>
          </> : null}
        </TD></TR> : null}
      </Fragment>)}</TBody></Table>
    </TableContainer>}
    {(policies.data?.total ?? 0) > 25 ? <div className="mt-3 flex gap-3"><Button disabled={page === 1} onClick={() => setPage(page - 1)}>{t('common.previous')}</Button><span>{page}</span><Button disabled={page * 25 >= (policies.data?.total ?? 0)} onClick={() => setPage(page + 1)}>{t('common.next')}</Button></div> : null}
    {traceId ? <section className="mt-[var(--space-4)]">{trace.isPending ? <Spinner label={t('common.loading')} /> : trace.error ? <p role="alert">{t('errors.unknown')}</p> : trace.data ? <DecisionTrace trace={trace.data} /> : null}</section> : null}
  </>;
}

function PolicyEditor({ policy, onSaved, onCancel }: { policy: Policy | null; onSaved: () => void; onCancel: () => void }) {
  const { t } = useTranslation();
  const queries = useQueryClient();
  const [location, setLocation] = useState(policy?.locationCode ?? '');
  const [sku, setSku] = useState(policy?.sku ?? '');
  const [minimum, setMinimum] = useState(String(policy?.minimum ?? 10));
  const [maximum, setMaximum] = useState(String(policy?.maximum ?? 50));
  const [enabled, setEnabled] = useState(policy?.enabled ?? false);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const save = useMutation({ mutationFn: async () => {
    setErrorCode(null);
    const { error } = await api.PUT('/api/v1/replenishment/pick-faces/{locationCode}', {
      params: { path: { locationCode: location.trim() } }, body: { sku: sku.trim(), minimum: Number(minimum), maximum: Number(maximum), enabled },
    });
    if (error) throw error;
  }, onSuccess: () => { void queries.invalidateQueries({ queryKey: ['pick-faces'] }); onSaved(); if (!policy) { setLocation(''); setSku(''); } },
  onError: (error: unknown) => setErrorCode((error as { code?: string })?.code ?? 'unknown') });
  return <form onSubmit={event => { event.preventDefault(); save.mutate(); }} aria-label={t('replenishment.configure')} className="mb-[var(--space-4)] border-b border-[var(--color-border-subtle)] pb-[var(--space-4)]">
    <div className="flex flex-wrap items-end gap-[var(--space-3)]">
      <Field label={t('replenishment.face')} htmlFor="replenishment-face"><Input id="replenishment-face" required value={location} disabled={policy !== null || save.isPending} onChange={event => setLocation(event.target.value)} /></Field>
      <Field label={t('items.sku')} htmlFor="replenishment-sku"><Input id="replenishment-sku" required value={sku} disabled={save.isPending} onChange={event => setSku(event.target.value)} /></Field>
      <Field label={t('replenishment.minimum')} htmlFor="replenishment-min" hint={t('replenishment.units')} hintMode="instruction"><Input id="replenishment-min" type="number" min={0.0001} max={1_000_000_000} step={0.0001} required value={minimum} disabled={save.isPending} onChange={event => setMinimum(event.target.value)} className="w-28" /></Field>
      <Field label={t('replenishment.maximum')} htmlFor="replenishment-max" hint={t('replenishment.maxHint')} hintMode="instruction"><Input id="replenishment-max" type="number" min={Number(minimum) + 0.0001} max={1_000_000_000} step={0.0001} required value={maximum} disabled={save.isPending} onChange={event => setMaximum(event.target.value)} className="w-28" /></Field>
      <label className="flex min-h-[44px] items-center gap-2"><input type="checkbox" checked={enabled} disabled={save.isPending} onChange={event => setEnabled(event.target.checked)} />{t('replenishment.enabled')}</label>
      <Button type="submit" disabled={save.isPending} {...disabledInDemo(t('demo.unavailable'))}>{t('common.save')}</Button>
      {policy ? <Button type="button" onClick={onCancel}>{t('common.cancel')}</Button> : null}
    </div>
    {errorCode ? <p role="alert">{t(`errors.${errorCode}`)}</p> : null}
  </form>;
}
