import { translateRuleNote, useTranslation, type RuleNote } from '@countflow/i18n';
import { Badge, Code } from '@countflow/ui';
import type { components } from '@countflow/api-client';

type Trace = components['schemas']['DecisionTraceView'];

/**
 * The answer to "why here?", on the screen of the person being told where to go.
 *
 * ADR 0004's first argument is that a supervisor who cannot see the reasoning
 * starts overriding the system, and once overrides are routine directed work
 * degrades. The operator standing at the rack has exactly the same problem, so
 * the trace is shown here rather than only on the console.
 *
 * Kept to the shortlist and a sample of what was ruled out. An operator does not
 * want two thousand rows; they want to know that the obvious bin was looked at
 * and what was wrong with it.
 */
export function WhyHere({ trace }: { trace: Trace }) {
  const { t } = useTranslation();

  const admissible = trace.candidates.filter((candidate) => candidate.isAdmissible).slice(0, 4);
  const excluded = trace.candidates.filter((candidate) => !candidate.isAdmissible).slice(0, 6);
  const winner = admissible[0];

  return (
    <div
      className="flex flex-col gap-[var(--space-4)] rounded-[var(--radius-surface)]
                 border border-[var(--color-border-subtle)] bg-[var(--color-surface-sunken)]
                 p-[var(--space-4)]"
    >
      <div className="flex flex-col gap-[var(--space-1)]">
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('rules.trace.ruleSet', {
            name: trace.ruleSetName,
            version: trace.ruleSetVersion,
          })}
        </p>
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {t('rules.trace.considered', {
            considered: trace.consideredCount,
            admissible: trace.admissibleCount,
          })}
        </p>
      </div>

      {winner ? (
        <section className="flex flex-col gap-[var(--space-2)]">
          <h3 className="m-0 text-[length:var(--text-sm)] font-semibold">
            {t('rules.trace.chose', { location: winner.candidateKey })}
          </h3>
          <ul className="m-0 flex list-none flex-col gap-[var(--space-1)] p-0">
            {winner.scores.map((score) => (
              <li
                key={score.ruleCode}
                className="flex items-start justify-between gap-[var(--space-3)]
                           text-[length:var(--text-sm)]"
              >
                <span className="text-[var(--color-text-secondary)]">
                  {translateRuleNote(t, score.reason as RuleNote)}
                </span>
                {/* Contribution, not raw score: what this rule actually added to
                    the ranking is the number that explains the outcome. */}
                <span className="shrink-0 tabular-nums text-[var(--color-text-tertiary)]">
                  {score.contribution.toFixed(2)}
                </span>
              </li>
            ))}
          </ul>
        </section>
      ) : (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('rules.trace.choseNothing')}
        </p>
      )}

      {admissible.length > 1 ? (
        <section className="flex flex-col gap-[var(--space-2)]">
          <h3 className="m-0 text-[length:var(--text-sm)] font-semibold">
            {t('rules.trace.admissible')}
          </h3>
          {admissible.slice(1).map((candidate) => (
            <div
              key={candidate.candidateId}
              className="flex items-center justify-between gap-[var(--space-3)]"
            >
              <Code>{candidate.candidateKey}</Code>
              <span className="tabular-nums text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
                {candidate.score.toFixed(2)}
              </span>
            </div>
          ))}
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
          {excluded.map((candidate) => (
            <div key={candidate.candidateId} className="flex flex-col gap-[var(--space-1)]">
              <Code>{candidate.candidateKey}</Code>
              <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                {candidate.excludedBy
                  ? translateRuleNote(t, candidate.excludedBy as RuleNote)
                  : null}
              </span>
            </div>
          ))}
        </section>
      ) : null}

      {trace.candidates.length === 0 ? (
        <Badge tone="warning">{t('rules.trace.empty')}</Badge>
      ) : null}
    </div>
  );
}
