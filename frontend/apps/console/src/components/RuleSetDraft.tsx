import { disabledInDemo } from '@countflow/demo/capabilities';
import { translateRuleName, useTranslation } from '@countflow/i18n';
import { InfoHelp,  Badge, Button, Code, Input, Select } from '@countflow/ui';
import { useState } from 'react';
import { api } from '../api.js';
import type { components } from '@countflow/api-client';

type RuleSet = components['schemas']['RuleSetResponse'];
type Vocabulary = components['schemas']['RuleVocabularyResponse'];

interface DraftRule {
  code: string;
  order: number;
  weight: number;
  parameters: string;
  isEnabled: boolean;
  isConstraint: boolean;
}

/**
 * Authoring a rule set.
 *
 * The screen behind an API that has been complete and tested since Phase 6, and
 * the reason it could not wait any longer: replay compares a candidate rule set
 * against what actually ran, and with no way to author a candidate the replay
 * screen is a museum piece.
 *
 * Only a draft is editable, and the component does not soften that. An active
 * set is what the floor is running on and an archived one is what every trace it
 * ever produced points at, so editing either would rewrite the explanation behind
 * pallets that have already been moved. The path is clone → edit → replay →
 * promote, and every one of those is a step somebody should be taking
 * deliberately — so each is its own button rather than a form that saves on blur.
 */
export function RuleSetDraft({
  set,
  vocabulary,
  canPromote,
  onChanged,
}: {
  set: RuleSet;
  vocabulary: Vocabulary | undefined;
  canPromote: boolean;
  onChanged: () => void;
}) {
  const { t } = useTranslation();

  const [editing, setEditing] = useState(false);
  const [name, setName] = useState(set.name);
  const [rules, setRules] = useState<DraftRule[]>(() => toDraft(set, vocabulary));
  const [adding, setAdding] = useState('');
  const [busy, setBusy] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);

  const isDraft = set.status === 'Draft';

  function begin() {
    setName(set.name);
    setRules(toDraft(set, vocabulary));
    setErrorCode(null);
    setEditing(true);
  }

  async function call(run: () => Promise<{ error?: unknown }>) {
    setBusy(true);
    setErrorCode(null);

    const { error } = await run();
    setBusy(false);

    if (error) {
      setErrorCode((error as { code?: string }).code ?? 'unknown');
      return false;
    }

    onChanged();
    return true;
  }

  async function clone() {
    await call(async () =>
      api.POST('/api/v1/rules/sets', {
        body: {
          kind: set.kind,
          name: t('rules.draft.copyOf', { name: set.name }),
          // A warehouse-scoped set clones to a warehouse-scoped set, and a shared
          // one to a shared one. Silently widening a copy of "WH1's counting
          // policy" to every warehouse is how somebody promotes a draft and
          // changes a building they were not looking at.
          warehouseId: set.warehouseId,
          cloneFromRuleSetId: set.id,
        },
      }),
    );
  }

  async function save() {
    const saved = await call(async () =>
      api.PUT('/api/v1/rules/sets/{id}', {
        params: { path: { id: set.id } },
        body: {
          name: name.trim(),
          // Sent whole rather than as a patch, and the server replaces the lot.
          // A rule set is judged as a set — order decides which refusal an
          // operator is shown, and weights only mean anything relative to the
          // other weights — so there is no such thing as changing one rule.
          rules: rules.map((rule) => ({
            code: rule.code,
            order: rule.order,
            weight: rule.isConstraint ? 0 : rule.weight,
            parameters: rule.parameters.trim() === '' ? null : rule.parameters.trim(),
            isEnabled: rule.isEnabled,
          })),
        },
      }),
    );

    if (saved) setEditing(false);
  }

  const available = vocabulary
    ? [...vocabulary.constraints, ...vocabulary.preferences].filter(
        (code) => !rules.some((rule) => rule.code === code),
      )
    : [];

  return (
    <div className="flex flex-col gap-[var(--space-3)]">
      <div className="flex flex-wrap items-center gap-[var(--space-2)]">
        {!editing ? (
          <>
            {/* Offered on every set, including archived ones. Reviving what the
                warehouse ran last quarter as a draft to compare against is
                exactly what replay is for. */}
            <Button size="sm" disabled={busy} onClick={() => void clone()} {...disabledInDemo(t('demo.unavailable'))}>
              {t('rules.draft.clone')}
            </Button>

            {isDraft ? (
              <>
                <Button size="sm" variant="primary" disabled={busy} onClick={begin} {...disabledInDemo(t('demo.unavailable'))}>
                  {t('rules.draft.edit')}
                </Button>
                <Button
                  size="sm"
                  disabled={busy || !canPromote}
                  title={canPromote ? undefined : t('rules.draft.promoteNeedsAdmin')}
                  onClick={() =>
                    void call(async () =>
                      api.POST('/api/v1/rules/sets/{id}/promote', {
                        params: { path: { id: set.id } },
                      }),
                    )
                  } {...disabledInDemo(t('demo.unavailable'))}
                >
                  {t('rules.draft.promote')}
                </Button>
              </>
            ) : null}

            {set.status !== 'Archived' ? (
              <Button
                size="sm"
                disabled={busy}
                onClick={() =>
                  void call(async () =>
                    api.POST('/api/v1/rules/sets/{id}/archive', {
                      params: { path: { id: set.id } },
                    }),
                  )
                } {...disabledInDemo(t('demo.unavailable'))}
              >
                {t('rules.draft.archive')}
              </Button>
            ) : null}
          </>
        ) : (
          <>
            <Input
              className="w-[280px]"
              value={name}
              onChange={(event) => setName(event.target.value)}
              aria-label={t('rules.draft.name')}
            />
            <Button size="sm" variant="primary" disabled={busy} onClick={() => void save()} {...disabledInDemo(t('demo.unavailable'))}>
              {busy ? t('rules.draft.saving') : t('rules.draft.save')}
            </Button>
            <Button size="sm" disabled={busy} onClick={() => setEditing(false)} {...disabledInDemo(t('demo.unavailable'))}>
              {t('common.cancel')}
            </Button>
          </>
        )}
      </div>

      {errorCode ? (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-danger)]">
          {t([`errors.${errorCode}`, 'errors.unknown'])}
        </p>
      ) : null}

      {editing ? (
        <div className="flex flex-col gap-[var(--space-2)]">
          <InfoHelp label={t('common.help')}>{t('rules.draft.hint')}</InfoHelp>

          {rules
            .slice()
            .sort((a, b) => a.order - b.order)
            .map((rule) => (
              <div
                key={rule.code}
                className="flex flex-wrap items-center gap-[var(--space-2)]
                           rounded-[var(--radius-control)] border border-[var(--color-border-subtle)]
                           px-[var(--space-3)] py-[var(--space-2)]"
              >
                <Input
                  className="w-[70px]"
                  inputMode="numeric"
                  value={String(rule.order)}
                  aria-label={t('rules.order')}
                  onChange={(event) =>
                    setRules(patch(rules, rule.code, { order: Number(event.target.value) || 0 }))
                  }
                />

                <div className="flex min-w-[220px] flex-col">
                  <span className="text-[length:var(--text-sm)]">
                    {translateRuleName(t, rule.code)}
                  </span>
                  <Code className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                    {rule.code}
                  </Code>
                </div>

                <Badge tone={rule.isConstraint ? 'danger' : 'info'}>
                  {t(`rules.role.${rule.isConstraint ? 'Constraint' : 'Preference'}`)}
                </Badge>

                {/* A constraint has no weight, and the field is absent rather
                    than disabled: a hard rule that could be outvoted is not a
                    hard rule, and a greyed-out box invites the question anyway. */}
                {rule.isConstraint ? null : (
                  <Input
                    className="w-[90px]"
                    inputMode="decimal"
                    value={String(rule.weight)}
                    aria-label={t('rules.weight')}
                    onChange={(event) =>
                      setRules(patch(rules, rule.code, { weight: Number(event.target.value) || 0 }))
                    }
                  />
                )}

                <Input
                  className="min-w-[200px] flex-1"
                  placeholder={t('rules.draft.parametersPlaceholder')}
                  value={rule.parameters}
                  aria-label={t('rules.parameters')}
                  onChange={(event) =>
                    setRules(patch(rules, rule.code, { parameters: event.target.value }))
                  }
                />

                <label className="flex items-center gap-[var(--space-2)] text-[length:var(--text-sm)]">
                  <input
                    type="checkbox"
                    checked={rule.isEnabled}
                    onChange={(event) =>
                      setRules(patch(rules, rule.code, { isEnabled: event.target.checked }))
                    }
                  />
                  {t('rules.enabled')}
                </label>

                <Button
                  size="sm"
                  onClick={() => setRules(rules.filter((row) => row.code !== rule.code))} {...disabledInDemo(t('demo.unavailable'))}
                >
                  {t('rules.draft.remove')}
                </Button>
              </div>
            ))}

          {available.length > 0 ? (
            <div className="flex items-center gap-[var(--space-2)]">
              {/* Only this kind's vocabulary. A putaway rule is written about an
                  empty bin and an allocation rule about a holding of stock, so
                  offering one where the other belongs would offer a rule set the
                  server is certain to refuse. */}
              <Select
                className="w-[320px]"
                value={adding}
                onChange={(event) => setAdding(event.target.value)}
                aria-label={t('rules.draft.add')}
              >
                <option value="">{t('rules.draft.add')}</option>
                {available.map((code) => (
                  <option key={code} value={code}>
                    {translateRuleName(t, code)}
                  </option>
                ))}
              </Select>
              <Button
                size="sm"
                disabled={adding === ''}
                onClick={() => {
                  const isConstraint = vocabulary?.constraints.includes(adding) ?? false;

                  setRules([
                    ...rules,
                    {
                      code: adding,
                      order: Math.max(0, ...rules.map((rule) => rule.order)) + 10,
                      weight: isConstraint ? 0 : 0.1,
                      parameters: '',
                      isEnabled: true,
                      isConstraint,
                    },
                  ]);

                  setAdding('');
                }} {...disabledInDemo(t('demo.unavailable'))}
              >
                {t('rules.draft.addRule')}
              </Button>
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  );
}

function toDraft(set: RuleSet, vocabulary: Vocabulary | undefined): DraftRule[] {
  return set.rules.map((rule) => ({
    code: rule.code,
    order: rule.order,
    weight: rule.weight,
    parameters: rule.parameters ?? '',
    isEnabled: rule.isEnabled,
    // The server already says which it is; the vocabulary is the fallback for a
    // rule the response somehow did not classify.
    isConstraint: rule.role === 'Constraint' || (vocabulary?.constraints.includes(rule.code) ?? false),
  }));
}

function patch(rules: DraftRule[], code: string, change: Partial<DraftRule>): DraftRule[] {
  return rules.map((rule) => (rule.code === code ? { ...rule, ...change } : rule));
}
