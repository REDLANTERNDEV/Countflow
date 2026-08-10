import { currentLocale, formatNumber, useTranslation } from '@countflow/i18n';
import {
  Badge,
  Button,
  Code,
  EmptyState,
  Input,
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
import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

export function ItemsPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [search, setSearch] = useState('');
  const [abcClass, setAbcClass] = useState('');
  const [temperature, setTemperature] = useState('');
  const [page, setPage] = useState(1);

  const pageSize = 50;

  const { data, isPending, error } = useQuery({
    queryKey: ['items', search, abcClass, temperature, page],
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/items', {
        params: {
          query: {
            page,
            pageSize,
            ...(search ? { search } : {}),
            ...(abcClass ? { abcClass: abcClass as 'A' | 'B' | 'C' } : {}),
            ...(temperature ? { temperature: temperature as 'Ambient' | 'Chilled' | 'Frozen' } : {}),
          },
        },
      });

      if (error) throw new Error('items');
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  return (
    <>
      <PageHead title={t('items.title')} count={total} />

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Input
          className="w-[320px]"
          // Searching by GTIN matters as much as by SKU: when a scan fails on
          // the floor, the first thing anyone does is read the digits out.
          placeholder={t('items.searchPlaceholder')}
          value={search}
          onChange={(event) => {
            setSearch(event.target.value);
            setPage(1);
          }}
        />

        <Select
          value={abcClass}
          onChange={(event) => {
            setAbcClass(event.target.value);
            setPage(1);
          }}
        >
          <option value="">{t('items.allAbc')}</option>
          <option value="A">A</option>
          <option value="B">B</option>
          <option value="C">C</option>
        </Select>

        <Select
          value={temperature}
          onChange={(event) => {
            setTemperature(event.target.value);
            setPage(1);
          }}
        >
          <option value="">{t('items.allTemperatures')}</option>
          <option value="Ambient">Ambient</option>
          <option value="Chilled">Chilled</option>
          <option value="Frozen">Frozen</option>
        </Select>
      </div>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data ? (
        <>
          <TableContainer className="max-h-[calc(100vh-260px)]">
            <Table>
              <THead>
                <TR>
                  <TH>{t('items.sku')}</TH>
                  <TH>{t('items.description')}</TH>
                  <TH>{t('items.abcClass')}</TH>
                  <TH>{t('items.temperature')}</TH>
                  <TH>{t('items.tracking')}</TH>
                  <TH numeric>{t('items.packLevels')}</TH>
                  <TH>{t('items.caseGtin')}</TH>
                </TR>
              </THead>
              <TBody>
                {data.items.length === 0 ? (
                  <TR>
                    <TD colSpan={7}>
                      <EmptyState title={t('items.empty')} />
                    </TD>
                  </TR>
                ) : (
                  data.items.map((item) => {
                    const casePack = item.packConfigurations.find((p) => p.level === 'Case');

                    return (
                      <TR key={item.id}>
                        <TD>
                          <Link
                            to="/items/$sku"
                            params={{ sku: item.sku }}
                            className="text-[var(--color-accent)] no-underline hover:underline"
                          >
                            <Code>{item.sku}</Code>
                          </Link>
                        </TD>
                        <TD>{item.description}</TD>
                        <TD>
                          <Badge tone={item.abcClass === 'A' ? 'info' : 'neutral'}>
                            {item.abcClass}
                          </Badge>
                        </TD>
                        <TD className="text-[var(--color-text-secondary)]">
                          {item.temperatureClass}
                        </TD>
                        <TD>
                          <div className="flex gap-[var(--space-1)]">
                            {item.isLotTracked ? <Badge>{t('items.lot')}</Badge> : null}
                            {item.isExpiryTracked ? (
                              <Badge tone="warning">{t('items.expiry')}</Badge>
                            ) : null}
                            {item.isSerialTracked ? <Badge>{t('items.serial')}</Badge> : null}
                            {item.isHazardous ? (
                              <Badge tone="danger">{t('items.hazard')}</Badge>
                            ) : null}
                          </div>
                        </TD>
                        <TD numeric>{formatNumber(item.packConfigurations.length, locale)}</TD>
                        <TD>
                          <Code>{casePack?.gtin ?? t('common.none')}</Code>
                        </TD>
                      </TR>
                    );
                  })
                )}
              </TBody>
            </Table>
          </TableContainer>

          <div className="mt-[var(--space-4)] flex items-center gap-[var(--space-4)]">
            <Button size="sm" disabled={page <= 1} onClick={() => setPage((p) => p - 1)}>
              {t('common.previous')}
            </Button>
            <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)] tabular-nums">
              {t('common.page', { page, total: lastPage })}
            </span>
            <Button size="sm" disabled={page >= lastPage} onClick={() => setPage((p) => p + 1)}>
              {t('common.next')}
            </Button>
          </div>
        </>
      ) : null}
    </>
  );
}
