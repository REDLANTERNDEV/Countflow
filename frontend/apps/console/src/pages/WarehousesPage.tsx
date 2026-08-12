import { useTranslation } from '@countflow/i18n';
import {
  Badge,
  Code,
  EmptyState,
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
import { api } from '../api.js';
import { PageHead } from '../components/PageHead.js';

export function WarehousesPage() {
  const { t } = useTranslation();

  const { data, isPending, error } = useQuery({
    queryKey: ['warehouses'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/warehouses');
      if (error) throw new Error('warehouses');
      return data;
    },
  });

  return (
    <>
      <PageHead title={t('nav.warehouses')} count={data?.length} />

      {isPending ? <Spinner label={t('common.loading')} /> : null}
      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}

      {data ? (
        <TableContainer>
          <Table>
            <THead>
              <TR>
                <TH>{t('locations.code')}</TH>
                <TH>{t('items.description')}</TH>
                <TH>City</TH>
                <TH>Country</TH>
                <TH>Status</TH>
              </TR>
            </THead>
            <TBody>
              {data.length === 0 ? (
                <TR>
                  <TD colSpan={5}>
                    <EmptyState title={t('locations.empty')} />
                  </TD>
                </TR>
              ) : (
                data.map((warehouse) => (
                  <TR key={warehouse.id}>
                    <TD>
                      <Code>{warehouse.code}</Code>
                    </TD>
                    <TD>{warehouse.name}</TD>
                    <TD>{warehouse.city ?? t('common.none')}</TD>
                    <TD>{warehouse.countryCode ?? t('common.none')}</TD>
                    <TD>
                      <Badge tone={warehouse.isActive ? 'success' : 'neutral'}>
                        {warehouse.isActive ? 'Active' : 'Inactive'}
                      </Badge>
                    </TD>
                  </TR>
                ))
              )}
            </TBody>
          </Table>
        </TableContainer>
      ) : null}
    </>
  );
}
