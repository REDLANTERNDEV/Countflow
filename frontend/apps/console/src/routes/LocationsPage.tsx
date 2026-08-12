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
import { useState } from 'react';
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

const LOCATION_TYPES = [
  'ReceivingDock',
  'Staging',
  'Bulk',
  'PickFace',
  'Packing',
  'ShippingDock',
  'Quarantine',
  'Supplier',
  'Customer',
  'Adjustment',
  'Scrap',
  'InTransit',
] as const;

export function LocationsPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [search, setSearch] = useState('');
  const [type, setType] = useState('');
  const [pickableOnly, setPickableOnly] = useState(false);
  const [page, setPage] = useState(1);

  const pageSize = 50;

  const { data, isPending, error } = useQuery({
    queryKey: ['locations', search, type, pickableOnly, page],
    // Without this the table blanks on every keystroke; with thousands of bins
    // the flicker reads as breakage.
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/locations', {
        params: {
          query: {
            page,
            pageSize,
            ...(search ? { search } : {}),
            ...(type ? { type: type as (typeof LOCATION_TYPES)[number] } : {}),
            ...(pickableOnly ? { pickable: true } : {}),
          },
        },
      });

      if (error) throw new Error('locations');
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  return (
    <>
      <PageHead title={t('locations.title')} count={total} />

      <div className="mb-[var(--space-4)] flex flex-wrap items-center gap-[var(--space-3)]">
        <Input
          className="w-[280px]"
          placeholder={t('locations.searchPlaceholder')}
          value={search}
          onChange={(event) => {
            setSearch(event.target.value);
            setPage(1);
          }}
        />

        <Select
          value={type}
          onChange={(event) => {
            setType(event.target.value);
            setPage(1);
          }}
        >
          <option value="">{t('locations.allTypes')}</option>
          {LOCATION_TYPES.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </Select>

        <label className="flex items-center gap-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          <input
            type="checkbox"
            checked={pickableOnly}
            onChange={(event) => {
              setPickableOnly(event.target.checked);
              setPage(1);
            }}
            className="accent-[var(--color-accent)]"
          />
          {t('locations.pickableOnly')}
        </label>
      </div>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data ? (
        <>
          <TableContainer className="max-h-[calc(100vh-260px)]">
            <Table>
              <THead>
                <TR>
                  <TH>{t('locations.code')}</TH>
                  <TH>{t('locations.type')}</TH>
                  <TH>{t('locations.zone')}</TH>
                  <TH>{t('locations.aisle')}</TH>
                  <TH>{t('locations.bay')}</TH>
                  <TH>{t('locations.level')}</TH>
                  <TH numeric>{t('locations.sequence')}</TH>
                  <TH numeric>{t('locations.maxVolume')}</TH>
                  <TH numeric>{t('locations.maxWeight')}</TH>
                  <TH>{t('locations.flags')}</TH>
                </TR>
              </THead>
              <TBody>
                {data.items.length === 0 ? (
                  <TR>
                    <TD colSpan={10}>
                      <EmptyState title={t('locations.empty')} />
                    </TD>
                  </TR>
                ) : (
                  data.items.map((location) => (
                    <TR key={location.id}>
                      {/* Identifiers: monospace, never locale-formatted. */}
                      <TD>
                        <Code>{location.code}</Code>
                      </TD>
                      <TD className="text-[var(--color-text-secondary)]">{location.type}</TD>
                      <TD>
                        {location.zoneCode ?? (
                          <span className="text-[var(--color-text-tertiary)]">
                            {t('locations.virtual')}
                          </span>
                        )}
                      </TD>
                      <TD>
                        <Code>{location.aisle ?? t('common.none')}</Code>
                      </TD>
                      <TD>
                        <Code>{location.bay ?? t('common.none')}</Code>
                      </TD>
                      <TD>
                        <Code>{location.level ?? t('common.none')}</Code>
                      </TD>
                      {/* Values: grouped for the reader's locale. */}
                      <TD numeric>{formatNumber(location.pickSequence, locale)}</TD>
                      <TD numeric>{formatNumber(location.maxVolumeM3, locale, { maximumFractionDigits: 2 })}</TD>
                      <TD numeric>{formatNumber(location.maxWeightKg, locale)}</TD>
                      <TD>
                        <div className="flex gap-[var(--space-1)]">
                          {location.isPickable ? (
                            <Badge tone="success">{t('locations.pick')}</Badge>
                          ) : null}
                          {location.isReplenishable ? <Badge>{t('locations.replen')}</Badge> : null}
                          {!location.isActive ? (
                            <Badge tone="danger">{t('locations.blocked')}</Badge>
                          ) : null}
                        </div>
                      </TD>
                    </TR>
                  ))
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
