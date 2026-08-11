import { currentLocale, formatDateTime, translateRuleName, useTranslation } from '@countflow/i18n';
import {
  Badge,
  Button,
  Code,
  Input,
  Numeric,
  Spinner,
  Table,
  TableContainer,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@countflow/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { DecisionTrace } from '../components/DecisionTrace.js';
import { PageHead } from '../components/PageHead.js';
import type { components } from '@countflow/api-client';

type Preview = components['schemas']['PreviewPutawayResponse'];

const statusTone = (status: string) =>
  status === 'Active' ? 'success' : status === 'Archived' ? 'neutral' : 'info';

/**
 * The rules the warehouse is running, and a way to try a change before living
 * with it.
 *
 * ADR 0004's second promise, in its interactive form. Full shadow replay over
 * history is Phase 6; this is the version a supervisor uses ten seconds after
 * editing a weight, and it runs the same advisor the dock runs so the two cannot
 * disagree.
 */
export function RulesPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [sku, setSku] = useState('');
  const [quantity, setQuantity] = useState('48');
  const [from, setFrom] = useState('');
  const [preview, setPreview] = useState<Preview | null>(null);
  const [running, setRunning] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);

  const { data, isPending, error } = useQuery({
    queryKey: ['rule-sets'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/rules/sets', {});

      if (error) throw error;
      return data;
    },
  });

  async function run() {
    setRunning(true);
    setErrorCode(null);
    setPreview(null);

    const { data, error } = await api.POST('/api/rules/putaway/preview', {
      body: {
        sku: sku.trim(),
        quantity: Number(quantity),
        fromLocationCode: from.trim(),
      },
    });

    setRunning(false);

    if (error || !data) {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
      return;
    }

    setPreview(data);
  }

  const ready = sku.trim().length > 0 && from.trim().length > 0 && Number(quantity) > 0;

  return (
    <>
      <PageHead title={t('rules.title')} count={data?.items.length} />

      <p className="mb-[var(--space-5)] max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {t('rules.subtitle')}
      </p>

      {/* The preview sits above the rule sets deliberately: the question a
          supervisor arrives with is "what would it do", and the configuration is
          what they read afterwards to work out why. */}
      <section
        className="mb-[var(--space-6)] flex flex-col gap-[var(--space-3)]
                   rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                   bg-[var(--color-surface-raised)] p-[var(--space-4)]"
      >
        <h3 className="m-0 text-[length:var(--text-lg)]">{t('rules.preview.title')}</h3>
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('rules.preview.hint')}
        </p>

        <div className="flex flex-wrap items-center gap-[var(--space-3)]">
          <Input
            className="w-[200px]"
            placeholder={t('items.sku')}
            value={sku}
            onChange={(event) => setSku(event.target.value.toUpperCase())}
          />
          <Input
            className="w-[110px]"
            inputMode="decimal"
            placeholder={t('work.quantity')}
            value={quantity}
            onChange={(event) => setQuantity(event.target.value)}
          />
          <Input
            className="w-[280px]"
            placeholder={t('rules.preview.standingAt')}
            value={from}
            onChange={(event) => setFrom(event.target.value.toUpperCase())}
          />
          <Button variant="primary" disabled={!ready || running} onClick={() => void run()}>
            {running ? t('rules.preview.running') : t('rules.preview.run')}
          </Button>
        </div>

        {errorCode ? (
          <p className="m-0 text-[var(--color-danger)]">
            {t([`errors.${errorCode}`, 'errors.unknown'])}
          </p>
        ) : null}

        {preview ? (
          <div className="flex flex-col gap-[var(--space-3)]">
            {preview.locationCode ? (
              <Badge tone="success" className="self-start">
                {t('rules.preview.wouldGoTo', { location: preview.locationCode })}
              </Badge>
            ) : (
              <Badge tone="warning" className="self-start">
                {t('rules.preview.wouldGoNowhere')}
              </Badge>
            )}
            <DecisionTrace trace={preview.trace} />
          </div>
        ) : null}
      </section>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data?.items.map((set) => (
        <section key={set.id} className="mb-[var(--space-5)]">
          <div className="mb-[var(--space-3)] flex flex-wrap items-baseline gap-[var(--space-3)]">
            <h3 className="m-0 text-[length:var(--text-lg)]">{set.name}</h3>
            <Badge tone="neutral">{t('rules.version', { version: set.version })}</Badge>
            <Badge tone={statusTone(set.status)}>{t(`rules.status.${set.status}`)}</Badge>
            <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
              {set.warehouseId ? set.warehouseId : t('rules.everyWarehouse')}
            </span>
            {set.activatedAt ? (
              <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {formatDateTime(set.activatedAt, locale)}
              </span>
            ) : null}
          </div>

          {set.notes ? (
            <p className="mb-[var(--space-3)] mt-0 max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
              {set.notes}
            </p>
          ) : null}

          <TableContainer>
            <Table>
              <THead>
                <TR>
                  <TH numeric>{t('rules.order')}</TH>
                  <TH>{t('rules.rule')}</TH>
                  <TH>{t('rules.role.Constraint')}</TH>
                  <TH numeric>{t('rules.weight')}</TH>
                  <TH>{t('rules.parameters')}</TH>
                  <TH>{t('rules.enabled')}</TH>
                </TR>
              </THead>
              <TBody>
                {set.rules.map((rule) => (
                  <TR key={rule.code}>
                    <TD numeric className="text-[var(--color-text-tertiary)]">
                      {rule.order}
                    </TD>
                    <TD>
                      <div className="flex flex-col">
                        <span>{translateRuleName(t, rule.code)}</span>
                        <Code className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                          {rule.code}
                        </Code>
                      </div>
                    </TD>
                    <TD>
                      <Badge tone={rule.role === 'Constraint' ? 'danger' : 'info'}>
                        {t(`rules.role.${rule.role}`)}
                      </Badge>
                    </TD>
                    <TD numeric>
                      {/* A constraint's weight is meaningless — a hard rule that
                          could be outvoted is not a hard rule — so the column is
                          empty rather than showing a misleading zero. */}
                      {rule.role === 'Preference' ? (
                        <Numeric value={rule.weight} fractionDigits={2} locale={locale} />
                      ) : (
                        t('common.none')
                      )}
                    </TD>
                    <TD>
                      {rule.parameters ? (
                        <Code className="text-[length:var(--text-xs)]">{rule.parameters}</Code>
                      ) : (
                        t('common.none')
                      )}
                    </TD>
                    <TD>
                      <Badge tone={rule.isEnabled ? 'success' : 'neutral'}>
                        {rule.isEnabled ? t('rules.enabled') : t('rules.disabled')}
                      </Badge>
                    </TD>
                  </TR>
                ))}
              </TBody>
            </Table>
          </TableContainer>
        </section>
      ))}

      {data ? (
        <section className="mt-[var(--space-5)]">
          <h3 className="mb-[var(--space-2)] mt-0 text-[length:var(--text-base)]">
            {t('rules.available')}
          </h3>
          <p className="mb-[var(--space-3)] mt-0 max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('rules.availableHint')}
          </p>
          <div className="flex flex-wrap gap-[var(--space-2)]">
            {[...data.availableConstraints, ...data.availablePreferences].map((code) => (
              <Badge key={code} tone="neutral" title={code}>
                {translateRuleName(t, code)}
              </Badge>
            ))}
          </div>
        </section>
      ) : null}
    </>
  );
}
