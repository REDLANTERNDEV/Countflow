import { currentLocale, formatDate, formatNumber, useTranslation } from '@countflow/i18n';
import {
  Badge,
  Button,
  Code,
  Input,
  Spinner,
  Table,
  TableContainer,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@countflow/ui';
import { useMutation, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

/** ASCII 29 — what a scanner actually transmits for FNC1. */
const GS = '';

export function ScanPage() {
  const { t } = useTranslation();
  const locale = currentLocale();
  const [barcode, setBarcode] = useState('');

  // Examples are built from a real seeded item, so they always resolve. Static
  // sample barcodes rot the moment the catalogue changes.
  const { data: sample } = useQuery({
    queryKey: ['scan-sample'],
    queryFn: async () => {
      const { data } = await api.GET('/api/v1/items', {
        params: { query: { temperature: 'Chilled', pageSize: 1 } },
      });

      const item = data?.items[0];
      const caseGtin = item?.packConfigurations.find((p) => p.level === 'Case')?.gtin;
      return caseGtin ? { sku: item!.sku, gtin: caseGtin } : null;
    },
  });

  const resolve = useMutation({
    mutationFn: async (raw: string) => {
      const { data, error } = await api.POST('/api/v1/scan/resolve', { body: { barcode: raw } });
      if (error) {
        const problem = error as { code?: string };
        throw new Error(problem.code ?? 'unknown');
      }
      return data;
    },
  });

  const examples = sample
    ? [
        { key: 'exampleCase', value: `01${sample.gtin}10LOT-4711${GS}1727011537000012` },
        { key: 'exampleBracketed', value: `(01)${sample.gtin}(10)LOT-4711(17)270115` },
        { key: 'exampleMissingLot', value: `01${sample.gtin}` },
        { key: 'exampleExpired', value: `01${sample.gtin}10OLD-1${GS}17200101` },
        { key: 'exampleUnknown', value: '0109501101530003' },
      ]
    : [];

  const result = resolve.data;

  return (
    <>
      <PageHead title={t('scan.title')} />

      <p className="mb-[var(--space-5)] max-w-[78ch] text-[var(--color-text-secondary)]">
        {t('scan.intro')}
      </p>

      <div className="mb-[var(--space-4)] flex gap-[var(--space-3)]">
        <Input
          mono
          className="flex-1"
          placeholder={t('scan.placeholder')}
          value={barcode}
          onChange={(event) => setBarcode(event.target.value)}
          onKeyDown={(event) => {
            if (event.key === 'Enter' && barcode.trim()) resolve.mutate(barcode.trim());
          }}
        />
        <Button
          variant="primary"
          disabled={!barcode.trim() || resolve.isPending}
          onClick={() => resolve.mutate(barcode.trim())}
        >
          {resolve.isPending ? t('scan.decoding') : t('scan.decode')}
        </Button>
      </div>

      {examples.length > 0 ? (
        <div className="mb-[var(--space-6)] flex flex-wrap items-center gap-[var(--space-2)]">
          <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {t('scan.tryOne')}
          </span>
          {examples.map((example) => (
            <button
              key={example.key}
              type="button"
              onClick={() => {
                setBarcode(example.value);
                resolve.mutate(example.value);
              }}
              className="rounded-[var(--radius-pill)] border border-[var(--color-border-default)]
                         bg-[var(--color-surface-raised)] px-[var(--space-4)] py-[var(--space-1)]
                         text-[length:var(--text-xs)] text-[var(--color-text-secondary)]
                         transition-colors duration-[var(--duration-fast)]
                         hover:border-[var(--color-accent)] hover:text-[var(--color-text-primary)]"
            >
              {t(`scan.${example.key}`)}
            </button>
          ))}
        </div>
      ) : null}

      {resolve.isPending ? <Spinner label={t('scan.decoding')} /> : null}

      {resolve.isError ? (
        <div className="rounded-[var(--radius-surface)] border border-[var(--color-danger)]
                        bg-[var(--color-danger-muted)] p-[var(--space-4)]">
          <p className="m-0 text-[var(--color-danger)]">
            {t([`errors.${(resolve.error as Error).message}`, 'errors.unknown'])}
          </p>
        </div>
      ) : null}

      {result ? (
        <div className="grid grid-cols-1 items-start gap-[var(--space-5)] xl:grid-cols-2">
          <section className="rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                              bg-[var(--color-surface-raised)] p-[var(--space-5)]">
            <h3 className="m-0 mb-[var(--space-4)] text-[length:var(--text-base)]">
              {t('scan.resolution')}
            </h3>

            {result.itemRecognised && result.item ? (
              <>
                <div className="mb-[var(--space-4)] flex flex-col gap-[2px]
                                border-b border-[var(--color-border-subtle)] pb-[var(--space-4)]">
                  <Code className="text-[length:var(--text-lg)] font-bold text-[var(--color-accent)]">
                    {result.item.sku}
                  </Code>
                  <span>{result.item.description}</span>
                </div>

                <dl className="grid grid-cols-[repeat(auto-fill,minmax(150px,1fr))] gap-[var(--space-4)]">
                  <Fact label={t('scan.packagingLevel')} value={result.item.packagingLevel} />
                  <Fact
                    label={t('scan.unitsPerPack')}
                    value={formatNumber(result.item.baseUnitQuantity, locale)}
                  />
                  <Fact
                    label={t('scan.scannedQuantity')}
                    value={formatNumber(result.scannedQuantity, locale)}
                  />
                  <Fact
                    label={t('scan.totalBaseUnits')}
                    value={formatNumber(result.baseUnitQuantity, locale)}
                  />
                  {/* Identifiers stay raw; the date is a value and gets localised. */}
                  <Fact label={t('scan.batch')} value={result.lotNumber ?? t('common.none')} mono />
                  <Fact label={t('scan.expiry')} value={formatDate(result.expiryDate, locale)} />
                  <Fact label={t('scan.serial')} value={result.serialNumber ?? t('common.none')} mono />
                  <Fact label={t('scan.sscc')} value={result.sscc ?? t('common.none')} mono />
                </dl>
              </>
            ) : (
              <p className="text-[var(--color-text-tertiary)]">{t('scan.notRecognised')}</p>
            )}

            {result.warnings.length > 0 ? (
              <ul className="mt-[var(--space-4)] flex list-none flex-col gap-[var(--space-2)] p-0">
                {result.warnings.map((warning) => (
                  <li
                    key={`${warning.code}-${JSON.stringify(warning.args)}`}
                    className="flex items-start gap-[var(--space-3)]
                               text-[length:var(--text-sm)] text-[var(--color-warning)]"
                  >
                    <Badge tone="warning">!</Badge>
                    {/* The server sent a code and arguments; every word here is
                        ours, in the reader's language — including the date,
                        which arrives as ISO precisely so the client can decide
                        how to write it. */}
                    <span>
                      {t(`scan.warning.${warning.code}`, {
                        ...warning.args,
                        ...(warning.args['date']
                          ? { date: formatDate(warning.args['date'], locale) }
                          : {}),
                        defaultValue: warning.code,
                      })}
                    </span>
                  </li>
                ))}
              </ul>
            ) : null}
          </section>

          <section className="rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                              bg-[var(--color-surface-raised)] p-[var(--space-5)]">
            <h3 className="m-0 mb-[var(--space-3)] text-[length:var(--text-base)]">
              {t('scan.elements')}
            </h3>
            <TableContainer className="border-0">
              <Table>
                <THead>
                  <TR>
                    <TH>{t('scan.ai')}</TH>
                    <TH>{t('scan.meaning')}</TH>
                    <TH>{t('scan.value')}</TH>
                  </TR>
                </THead>
                <TBody>
                  {result.elements.map((element) => (
                    <TR key={`${element.ai}-${element.value}`}>
                      <TD>
                        <Code>{element.ai}</Code>
                      </TD>
                      <TD className="text-[var(--color-text-secondary)]">{element.title}</TD>
                      <TD>
                        <Code>{element.value}</Code>
                      </TD>
                    </TR>
                  ))}
                </TBody>
              </Table>
            </TableContainer>
          </section>
        </div>
      ) : null}
    </>
  );
}

function Fact({ label, value, mono = false }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex flex-col gap-[2px]">
      <dt className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">{label}</dt>
      <dd className={`m-0 ${mono ? 'font-[family-name:var(--font-mono)] tabular-nums' : 'tabular-nums'}`}>
        {value}
      </dd>
    </div>
  );
}
