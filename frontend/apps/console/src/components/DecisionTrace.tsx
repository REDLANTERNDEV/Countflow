import type { components } from '@countflow/api-client';
import {
  formatDateTime,
  currentLocale,
  translateRuleName,
  translateRuleNote,
  useTranslation,
  type RuleNote,
} from '@countflow/i18n';
import {
  Badge,
  Code,
  Numeric,
  Table,
  TableContainer,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@countflow/ui';

type Trace = components['schemas']['DecisionTraceView'];

/**
 * A stored decision, in full.
 *
 * The supervisor's half of ADR 0004. Where the handheld shows an operator the
 * shortlist, this shows the whole evaluation — every candidate that survived,
 * every rule that contributed to the ranking, and a sample of what was ruled out
 * and by which constraint. It is the screen that turns "the system sent them to
 * a stupid bin" into "the travel weight is too high", which is an argument
 * somebody can act on.
 */
export function DecisionTrace({ trace }: { trace: Trace }) {
  const { t } = useTranslation();
  const locale = currentLocale();

  const admissible = trace.candidates.filter((candidate) => candidate.isAdmissible);
  const excluded = trace.candidates.filter((candidate) => !candidate.isAdmissible);

  // Putaway chooses between empty bins; allocation chooses between holdings of
  // stock. One component either way — the shape of a trace does not change with
  // the decision — but a column headed "Location" over a list of batches is the
  // kind of small lie that makes a trace harder to read than what it explains.
  const aboutStock = trace.kind === 'Allocation';
  const candidateLabel = aboutStock ? t('rules.trace.candidateStock') : t('rules.trace.candidate');

  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <div className="flex flex-wrap items-center gap-[var(--space-3)]">
        <Badge tone="info">
          {t('rules.trace.ruleSet', { name: trace.ruleSetName, version: trace.ruleSetVersion })}
        </Badge>
        <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t(aboutStock ? 'rules.trace.consideredStock' : 'rules.trace.considered', {
            considered: trace.consideredCount,
            admissible: trace.admissibleCount,
          })}
        </span>
        <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {formatDateTime(trace.decidedAt, locale)}
        </span>
        {trace.selectedKey ? (
          <Badge tone="success">{t('rules.trace.chose', { location: trace.selectedKey })}</Badge>
        ) : (
          <Badge tone="warning">{t('rules.trace.choseNothing')}</Badge>
        )}
      </div>

      {trace.candidates.length === 0 ? (
        <p className="m-0 text-[var(--color-text-secondary)]">{t('rules.trace.empty')}</p>
      ) : null}

      {admissible.length > 0 ? (
        <section className="flex flex-col gap-[var(--space-2)]">
          <h3 className="m-0 text-[length:var(--text-sm)] font-semibold">
            {t('rules.trace.admissible')}
          </h3>
          <TableContainer className="max-h-[380px]">
            <Table>
              <THead>
                <TR>
                  <TH>{t('rules.trace.rank')}</TH>
                  <TH>{candidateLabel}</TH>
                  <TH numeric>{t('rules.trace.score')}</TH>
                  <TH>{t('rules.trace.breakdown')}</TH>
                </TR>
              </THead>
              <TBody>
                {admissible.map((candidate) => (
                  <TR key={candidate.candidateId}>
                    <TD className="tabular-nums">{candidate.rank}</TD>
                    <TD>
                      <Code>{candidate.candidateKey}</Code>
                    </TD>
                    <TD numeric>
                      <Numeric value={candidate.score} fractionDigits={3} locale={locale} />
                    </TD>
                    <TD>
                      <ul className="m-0 flex list-none flex-col gap-[2px] p-0">
                        {candidate.scores.map((score) => (
                          <li
                            key={score.ruleCode}
                            className="flex gap-[var(--space-2)] text-[length:var(--text-xs)]
                                       text-[var(--color-text-secondary)]"
                          >
                            {/* Contribution first: what this rule actually added
                                is the number that explains the ranking, and the
                                weight and raw value are the workings behind it. */}
                            <Numeric
                              value={score.contribution}
                              fractionDigits={3}
                              locale={locale}
                              className="w-[52px] shrink-0 text-right text-[var(--color-text-tertiary)]"
                            />
                            <span>{translateRuleNote(t, score.reason as RuleNote)}</span>
                          </li>
                        ))}
                      </ul>
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        </section>
      ) : null}

      {excluded.length > 0 ? (
        <section className="flex flex-col gap-[var(--space-2)]">
          <h3 className="m-0 text-[length:var(--text-sm)] font-semibold">
            {t('rules.trace.excluded')}
          </h3>
          <p className="m-0 text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {t('rules.trace.sampled')}
          </p>
          <TableContainer className="max-h-[320px]">
            <Table>
              <THead>
                <TR>
                  <TH>{candidateLabel}</TH>
                  <TH>{t('rules.trace.excludedBy')}</TH>
                </TR>
              </THead>
              <TBody>
                {excluded.map((candidate) => (
                  <TR key={candidate.candidateId}>
                    <TD>
                      <Code>{candidate.candidateKey}</Code>
                    </TD>
                    <TD className="text-[var(--color-text-secondary)]">
                      {candidate.excludedBy
                        ? translateRuleNote(t, candidate.excludedBy as RuleNote)
                        : null}
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        </section>
      ) : null}
    </div>
  );
}

/** The name of a rule implementation, shared by the rule-set listing. */
export function RuleName({ code }: { code: string }) {
  const { t } = useTranslation();

  return <span>{translateRuleName(t, code)}</span>;
}
