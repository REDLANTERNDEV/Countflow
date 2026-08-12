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
import { PageHead } from '../components/PageHead.js';

const KINDS = ['Putaway', 'Allocation', 'CycleCount'] as const;

const outcomeTone = (outcome: string) =>
  outcome === 'Changed' ? 'warning' : outcome === 'Lost' ? 'danger' : outcome === 'Gained' ? 'info' : 'neutral';

const fidelityTone = (fidelity: string) =>
  fidelity === 'Exact' ? 'success' : fidelity === 'Unreplayable' ? 'danger' : 'warning';

function daysAgo(days: number) {
  return new Date(Date.now() - days * 86_400_000).toISOString();
}

/**
 * What a candidate rule set would have done to work the warehouse has already
 * done (ADR 0004).
 *
 * The screen is arranged around the two questions a supervisor actually has, in
 * the order they have them. First: can I believe this? — which is why the
 * reproduced count sits in the summary beside the differences rather than
 * somewhere further down, because a run that changed four decisions means one
 * thing when it reproduced all two hundred and something much weaker when it
 * reproduced sixty. Second: what changed and why? — which is the table, and the
 * influence panel underneath each row that names the rule responsible.
 *
 * Everything is shown, not only the differences. A run where nothing changed is
 * a real and useful answer — it is what "this tuning is safe" looks like — and a
 * screen that only listed differences would make it indistinguishable from a
 * broken run.
 */
export function ReplayPage() {
  const { t } = useTranslation();
  const locale = currentLocale();
  const queryClient = useQueryClient();

  const [kind, setKind] = useState<string>('Putaway');
  const [candidateId, setCandidateId] = useState('');
  const [warehouseCode, setWarehouseCode] = useState('');
  const [from, setFrom] = useState(() => daysAgo(30).slice(0, 10));
  const [to, setTo] = useState(() => new Date().toISOString().slice(0, 10));
  const [openRun, setOpenRun] = useState<string | null>(null);
  const [outcomeFilter, setOutcomeFilter] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);

  const warehouses = useQuery({
    queryKey: ['warehouses'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/warehouses');
      if (error) throw error;
      return data;
    },
  });

  const selectedWarehouse = warehouseCode || warehouses.data?.[0]?.code || '';

  const ruleSets = useQuery({
    queryKey: ['rule-sets', kind],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/rules/sets', {
        params: { query: { kind: kind as 'Putaway' } },
      });
      if (error) throw error;
      return data;
    },
  });

  const runs = useQuery({
    queryKey: ['replay-runs', kind],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/replay/runs', {
        params: { query: { kind: kind as 'Putaway', pageSize: 25 } },
      });
      if (error) throw error;
      return data;
    },
  });

  const detail = useQuery({
    queryKey: ['replay-run', openRun, outcomeFilter],
    enabled: openRun !== null,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/replay/runs/{id}', {
        params: {
          path: { id: openRun as string },
          query: outcomeFilter ? { outcome: outcomeFilter as 'Changed' } : {},
        },
      });
      if (error) throw error;
      return data;
    },
  });

  const run = useMutation({
    mutationFn: async () => {
      setErrorCode(null);

      const { data, error } = await api.POST('/api/v1/replay/runs', {
        body: {
          warehouseCode: selectedWarehouse,
          kind: kind as 'Putaway',
          candidateRuleSetId: candidateId,
          from: new Date(`${from}T00:00:00Z`).toISOString(),
          to: new Date(`${to}T23:59:59Z`).toISOString(),
          maxDecisions: 200,
        },
      });

      if (error) throw error;
      return data;
    },
    onSuccess: async (created) => {
      setOpenRun(created?.id ?? null);
      await queryClient.invalidateQueries({ queryKey: ['replay-runs'] });
    },
    onError: (error: { code?: string }) => setErrorCode(error?.code ?? 'replay.failed'),
  });

  const candidates = (ruleSets.data?.items ?? []).filter((set) => set.status !== 'Archived');

  return (
    <>
      <PageHead title={t('replay.title')}>
        <p className="cf-muted">{t('replay.subtitle')}</p>
      </PageHead>

      <section className="cf-panel">
        <div className="cf-filters">
          <Field label={t('replay.warehouse')}>
            <Select value={selectedWarehouse} onChange={(event) => setWarehouseCode(event.target.value)}>
              {(warehouses.data ?? []).map((warehouse) => (
                <option key={warehouse.id} value={warehouse.code}>
                  {warehouse.code}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={t('replay.kind')}>
            <Select
              value={kind}
              onChange={(event) => {
                setKind(event.target.value);
                setCandidateId('');
                setOpenRun(null);
              }}
            >
              {KINDS.map((option) => (
                <option key={option} value={option}>
                  {t(`replay.kinds.${option}`)}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={t('replay.candidate')} hint={t('replay.candidateHint')}>
            <Select value={candidateId} onChange={(event) => setCandidateId(event.target.value)}>
              <option value="">{t('replay.choose')}</option>
              {candidates.map((set) => (
                <option key={set.id} value={set.id}>
                  {set.name} · {set.status === 'Draft' ? t('replay.draft') : `v${set.version}`}
                </option>
              ))}
            </Select>
          </Field>

          <Field label={t('replay.from')}>
            <Input type="date" value={from} onChange={(event) => setFrom(event.target.value)} />
          </Field>

          <Field label={t('replay.to')}>
            <Input type="date" value={to} onChange={(event) => setTo(event.target.value)} />
          </Field>

          <Button onClick={() => run.mutate()} disabled={!candidateId || run.isPending}>
            {run.isPending ? t('replay.running') : t('replay.run')}
          </Button>
        </div>

        {errorCode ? <p className="cf-error">{t(`errors.${errorCode}`)}</p> : null}
      </section>

      {runs.isPending ? <Spinner /> : null}

      {!runs.isPending && (runs.data?.items ?? []).length === 0 ? (
        <EmptyState title={t('replay.empty')} hint={t('replay.emptyHint')} />
      ) : null}

      {(runs.data?.items ?? []).map((item) => (
        <section key={item.id} className="cf-panel">
          <header className="cf-panel__head">
            <div>
              <Code>{item.reference}</Code>{' '}
              <strong>
                {item.candidateRuleSetName}
                {item.candidateRuleSetVersion > 0 ? ` v${item.candidateRuleSetVersion}` : ''}
              </strong>{' '}
              <span className="cf-muted">
                {t('replay.against', {
                  baseline: item.baselineRuleSetName,
                  version: String(item.baselineRuleSetVersion),
                })}
              </span>
            </div>

            <Button
              variant="ghost"
              onClick={() => {
                setOpenRun(openRun === item.id ? null : item.id);
                setOutcomeFilter('');
              }}
            >
              {openRun === item.id ? t('replay.hide') : t('replay.show')}
            </Button>
          </header>

          {/*
            The credibility figure sits with the conclusions rather than under
            them. "Four changed out of two hundred" is a different claim
            depending on how many of those two hundred the original rules
            reproduced when they were run again over the rebuilt warehouse.
          */}
          <dl className="cf-stats">
            <div>
              <dt>{t('replay.replayed')}</dt>
              <dd>
                <Numeric value={item.replayedCount} locale={locale} />
              </dd>
            </div>
            <div>
              <dt>{t('replay.reproduced')}</dt>
              <dd>
                <Numeric value={item.exactCount} locale={locale} />
                {item.approximateCount > 0 ? (
                  <span className="cf-muted">
                    {' '}
                    {t('replay.approximate', { count: String(item.approximateCount) })}
                  </span>
                ) : null}
              </dd>
            </div>
            <div>
              <dt>{t('replay.changed')}</dt>
              <dd>
                <Numeric value={item.changedCount} locale={locale} />
              </dd>
            </div>
            <div>
              <dt>{t('replay.reordered')}</dt>
              <dd>
                <Numeric value={item.reorderedCount} locale={locale} />
              </dd>
            </div>
            <div>
              <dt>{t('replay.lost')}</dt>
              <dd>
                <Numeric value={item.lostCount} locale={locale} />
              </dd>
            </div>
            <div>
              <dt>{t('replay.elapsed')}</dt>
              <dd>
                <Numeric value={item.elapsedMilliseconds} locale={locale} /> ms
              </dd>
            </div>
          </dl>

          <p className="cf-muted">
            {formatDateTime(item.createdAt, locale)} · {item.requestedByUserName}
          </p>

          {openRun === item.id ? (
            <>
              <h3 className="cf-subhead">{t('replay.influence')}</h3>
              <p className="cf-muted">{t('replay.influenceHint')}</p>

              <TableContainer>
                <Table>
                  <THead>
                    <TR>
                      <TH>{t('replay.rule')}</TH>
                      <TH align="right">{t('replay.weight')}</TH>
                      <TH align="right">{t('replay.asked')}</TH>
                      <TH align="right">{t('replay.did')}</TH>
                      <TH align="right">{t('replay.spread')}</TH>
                      <TH>{t('replay.findings')}</TH>
                    </TR>
                  </THead>
                  <TBody>
                    {(item.influence ?? []).map((line) => (
                      <TR key={line.ruleCode}>
                        <TD>
                          <Code>{line.ruleCode}</Code>
                        </TD>
                        <TD align="right">
                          <Numeric value={line.weight} locale={locale} fractionDigits={2} />
                        </TD>
                        <TD align="right">
                          <Numeric value={Math.round(line.meanNominalShare * 100)} locale={locale} />%
                        </TD>
                        <TD align="right">
                          <strong>
                            <Numeric value={Math.round(line.meanRealisedShare * 100)} locale={locale} />%
                          </strong>
                        </TD>
                        <TD align="right">
                          <Numeric
                            value={line.meanValueSpread}
                            locale={locale}
                            fractionDigits={3}
                          />
                        </TD>
                        <TD>
                          {Object.entries(line.flags ?? {}).map(([finding, count]) => (
                            <Badge key={finding} tone="warning">
                              {t(`replay.flags.${finding}`)} ×{count}
                            </Badge>
                          ))}
                        </TD>
                      </TR>
                    ))}
                  </TBody>
                </Table>
              </TableContainer>

              <h3 className="cf-subhead">{t('replay.decisions')}</h3>

              <div className="cf-filters">
                <Field label={t('replay.outcome')}>
                  <Select value={outcomeFilter} onChange={(event) => setOutcomeFilter(event.target.value)}>
                    <option value="">{t('replay.allOutcomes')}</option>
                    {['Unchanged', 'Reordered', 'Changed', 'Gained', 'Lost'].map((option) => (
                      <option key={option} value={option}>
                        {t(`replay.outcomes.${option}`)}
                      </option>
                    ))}
                  </Select>
                </Field>
              </div>

              {detail.isPending ? <Spinner /> : null}

              <TableContainer>
                <Table>
                  <THead>
                    <TR>
                      <TH>{t('replay.when')}</TH>
                      <TH>{t('replay.subject')}</TH>
                      <TH>{t('replay.outcome')}</TH>
                      <TH>{t('replay.chose')}</TH>
                      <TH>{t('replay.wouldChoose')}</TH>
                      <TH align="right">{t('replay.rank')}</TH>
                      <TH align="right">{t('replay.score')}</TH>
                      <TH>{t('replay.fidelity')}</TH>
                    </TR>
                  </THead>
                  <TBody>
                    {(detail.data?.decisions ?? []).map((decision) => (
                      <TR key={decision.decisionTraceId}>
                        <TD>{formatDateTime(decision.decidedAt, locale)}</TD>
                        <TD>{decision.subject}</TD>
                        <TD>
                          <Badge tone={outcomeTone(decision.outcome)}>
                            {t(`replay.outcomes.${decision.outcome}`)}
                          </Badge>
                        </TD>
                        <TD>{decision.baselineKey ? <Code>{decision.baselineKey}</Code> : '—'}</TD>
                        <TD>{decision.candidateKey ? <Code>{decision.candidateKey}</Code> : '—'}</TD>
                        <TD align="right">
                          {/*
                            Where the new winner sat in the old ranking. A change
                            from rank 2 is the same rule set thinking slightly
                            differently; a change from rank 40 is a different
                            policy, and this column is the cheapest way to tell
                            those apart. Nothing shown when it was not admissible
                            at all before, which is the loudest version of the
                            same signal.
                          */}
                          {decision.candidateRankUnderBaseline > 0
                            ? `#${decision.candidateRankUnderBaseline}`
                            : '—'}
                        </TD>
                        <TD align="right">
                          <Numeric
                            value={decision.baselineScore}
                            locale={locale}
                            fractionDigits={3}
                          />
                          {' → '}
                          <Numeric
                            value={decision.candidateScore}
                            locale={locale}
                            fractionDigits={3}
                          />
                        </TD>
                        <TD>
                          <Badge tone={fidelityTone(decision.fidelity)}>
                            {t(`replay.fidelities.${decision.fidelity}`)}
                          </Badge>
                          {decision.fidelityNote ? (
                            <span className="cf-muted"> {t(`replay.notes.${decision.fidelityNote}`)}</span>
                          ) : null}
                        </TD>
                      </TR>
                    ))}
                  </TBody>
                </Table>
              </TableContainer>

              {(detail.data?.decisions ?? []).length === 0 && !detail.isPending ? (
                <EmptyState title={t('replay.noDecisions')} hint={t('replay.noDecisionsHint')} />
              ) : null}
            </>
          ) : null}
        </section>
      ))}
    </>
  );
}
