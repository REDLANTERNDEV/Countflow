import { translateCount, currentLocale, formatNumber, useTranslation } from '@countflow/i18n';
import { InfoHelp,
  Badge,
  Code,
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
import { Link, useParams } from '@tanstack/react-router';
import { api } from '../api.js';

export function ItemDetailPage() {
  const { sku } = useParams({ from: '/items/$sku' });
  const { t } = useTranslation();
  const locale = currentLocale();

  const { data, isPending, error } = useQuery({
    queryKey: ['item', sku],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/items/{sku}', {
        params: { path: { sku } },
      });

      if (error) throw new Error('item.not_found');
      return data;
    },
  });

  if (isPending) return <Spinner label={t('common.loading')} />;
  if (error) {
    return (
      <p className="text-[var(--color-danger)]">
        {t([`errors.${(error as Error).message}`, 'errors.unknown'])}
      </p>
    );
  }
  if (!data) return null;

  return (
    <>
      <div className="mb-[var(--space-5)]">
        <Link
          to="/items"
          className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)] no-underline hover:underline"
        >
          ← {t('items.title')}
        </Link>
        <h2 className="m-0 mt-[var(--space-2)] text-[length:var(--text-xl)]">
          <Code className="text-[length:var(--text-xl)]">{data.sku}</Code>{' '}
          <span className="font-normal">{data.description}</span>
        </h2>
      </div>

      <dl className="mb-[var(--space-6)] grid grid-cols-[repeat(auto-fill,minmax(170px,1fr))] gap-[var(--space-4)]">
        <Fact label={t('items.abcClass')} value={data.abcClass} />
        <Fact label={t('items.temperature')} value={data.temperatureClass} />
        <Fact label={t('items.baseUnit')} value={data.baseUom} />
        <Fact label={t('items.lotTracked')} value={data.isLotTracked ? t('common.yes') : t('common.no')} />
        <Fact label={t('items.expiryTracked')} value={data.isExpiryTracked ? t('common.yes') : t('common.no')} />
        <Fact label={t('items.serialTracked')} value={data.isSerialTracked ? t('common.yes') : t('common.no')} />
        <Fact
          label={t('items.shelfLife')}
          value={
            data.shelfLifeDays
              ? translateCount(t, 'items.shelfLifeDays', data.shelfLifeDays)
              : t('common.none')
          }
        />
        <Fact label={t('items.hazardous')} value={data.isHazardous ? t('common.yes') : t('common.no')} />
      </dl>

      <h3 className="mb-[var(--space-2)] text-[length:var(--text-lg)]">{t('items.packaging')}</h3>
      <InfoHelp label={t('items.description')}>{t('items.packagingNote')}</InfoHelp>

      <TableContainer>
        <Table>
          <THead>
            <TR>
              <TH>{t('locations.level')}</TH>
              <TH numeric>{t('items.baseUnits')}</TH>
              <TH>GTIN</TH>
              <TH numeric>{t('items.dimensions')}</TH>
              <TH numeric>{t('items.volume')}</TH>
              <TH numeric>{t('items.grossWeight')}</TH>
              <TH>{t('items.role')}</TH>
            </TR>
          </THead>
          <TBody>
            {data.packConfigurations.map((pack) => (
              <TR key={pack.level}>
                <TD>{pack.level}</TD>
                <TD numeric>{formatNumber(pack.baseUnitQuantity, locale)}</TD>
                <TD>
                  {/* Never number-formatted: a grouped GTIN is not a barcode. */}
                  <Code>{pack.gtin ?? t('common.none')}</Code>
                </TD>
                <TD numeric>
                  {pack.lengthMm && pack.widthMm && pack.heightMm ? (
                    <Code>{`${pack.lengthMm}×${pack.widthMm}×${pack.heightMm}`}</Code>
                  ) : (
                    t('common.none')
                  )}
                </TD>
                <TD numeric>{formatNumber(pack.volumeM3, locale, { maximumFractionDigits: 5 })}</TD>
                <TD numeric>{formatNumber(pack.grossWeightKg, locale, { maximumFractionDigits: 3 })}</TD>
                <TD>
                  <div className="flex gap-[var(--space-1)]">
                    {pack.isReceivingUnit ? (
                      <Badge tone="success">{t('items.receiving')}</Badge>
                    ) : null}
                    {pack.isPickUnit ? <Badge>{t('locations.pick')}</Badge> : null}
                  </div>
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      </TableContainer>
    </>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="flex flex-col gap-[2px]">
      <dt className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">{label}</dt>
      <dd className="m-0">{value}</dd>
    </div>
  );
}
