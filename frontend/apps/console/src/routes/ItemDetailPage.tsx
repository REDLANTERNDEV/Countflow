import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from '@tanstack/react-router';
import { api } from '../api.js';

export function ItemDetailPage() {
  const { sku } = useParams({ from: '/items/$sku' });

  const { data, isPending, error } = useQuery({
    queryKey: ['item', sku],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/items/{sku}', {
        params: { path: { sku } },
      });

      if (error) throw new Error(`No item with SKU ${sku}.`);
      return data;
    },
  });

  if (isPending) return <p className="cf-muted">Loading…</p>;
  if (error) return <p className="cf-error">{(error as Error).message}</p>;
  if (!data) return null;

  return (
    <>
      <div className="page-head">
        <div>
          <Link to="/items" className="cf-muted">
            ← Items
          </Link>
          <h2 className="detail-title">
            <span className="cf-mono">{data.sku}</span> {data.description}
          </h2>
        </div>
      </div>

      <div className="detail-grid">
        <Fact label="ABC class" value={data.abcClass} />
        <Fact label="Temperature" value={data.temperatureClass} />
        <Fact label="Base unit" value={data.baseUom} />
        <Fact label="Lot tracked" value={data.isLotTracked ? 'Yes' : 'No'} />
        <Fact label="Expiry tracked" value={data.isExpiryTracked ? 'Yes' : 'No'} />
        <Fact label="Serial tracked" value={data.isSerialTracked ? 'Yes' : 'No'} />
        <Fact label="Shelf life" value={data.shelfLifeDays ? `${data.shelfLifeDays} days` : '—'} />
        <Fact label="Hazardous" value={data.isHazardous ? 'Yes' : 'No'} />
      </div>

      <h3>Packaging hierarchy</h3>
      <p className="cf-muted detail-note">
        A GTIN identifies a packaging level, not the item. Scanning the case
        barcode is what tells receiving it is holding twelve units rather than one.
      </p>

      <table className="cf-table">
        <thead>
          <tr>
            <th>Level</th>
            <th className="numeric">Base units</th>
            <th>GTIN</th>
            <th className="numeric">L×W×H (mm)</th>
            <th className="numeric">Volume m³</th>
            <th className="numeric">Gross kg</th>
            <th>Role</th>
          </tr>
        </thead>
        <tbody>
          {data.packConfigurations.map((pack) => (
            <tr key={pack.level}>
              <td>{pack.level}</td>
              <td className="numeric cf-mono">{pack.baseUnitQuantity}</td>
              <td className="cf-mono">{pack.gtin ?? <span className="cf-muted">none</span>}</td>
              <td className="numeric cf-mono">
                {pack.lengthMm && pack.widthMm && pack.heightMm
                  ? `${pack.lengthMm}×${pack.widthMm}×${pack.heightMm}`
                  : '—'}
              </td>
              <td className="numeric cf-mono">
                {pack.volumeM3 ? pack.volumeM3.toFixed(5) : '—'}
              </td>
              <td className="numeric cf-mono">{pack.grossWeightKg ?? '—'}</td>
              <td>
                {pack.isReceivingUnit ? <span className="pill pill-ok">receiving</span> : null}
                {pack.isPickUnit ? <span className="pill">pick</span> : null}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </>
  );
}

function Fact({ label, value }: { label: string; value: string }) {
  return (
    <div className="fact">
      <span className="cf-label">{label}</span>
      <span>{value}</span>
    </div>
  );
}
