import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { Link } from '@tanstack/react-router';
import { useState } from 'react';
import { api } from '../api.js';

export function ItemsPage() {
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
            ...(temperature
              ? { temperature: temperature as 'Ambient' | 'Chilled' | 'Frozen' }
              : {}),
          },
        },
      });

      if (error) throw new Error('Could not load items.');
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  return (
    <>
      <div className="page-head">
        <h2>Items</h2>
        <span className="cf-muted">{total.toLocaleString()} total</span>
      </div>

      <div className="filter-bar">
        <input
          className="cf-input"
          // Searching by GTIN is deliberate: when a scan fails on the floor the
          // first thing anyone does is read the barcode digits out loud.
          placeholder="Search by SKU, description or GTIN"
          value={search}
          onChange={(event) => {
            setSearch(event.target.value);
            setPage(1);
          }}
        />

        <select
          className="cf-input"
          value={abcClass}
          onChange={(event) => {
            setAbcClass(event.target.value);
            setPage(1);
          }}
        >
          <option value="">All ABC classes</option>
          <option value="A">A — fast</option>
          <option value="B">B — medium</option>
          <option value="C">C — slow</option>
        </select>

        <select
          className="cf-input"
          value={temperature}
          onChange={(event) => {
            setTemperature(event.target.value);
            setPage(1);
          }}
        >
          <option value="">All temperatures</option>
          <option value="Ambient">Ambient</option>
          <option value="Chilled">Chilled</option>
          <option value="Frozen">Frozen</option>
        </select>
      </div>

      {error ? <p className="cf-error">{(error as Error).message}</p> : null}
      {isPending ? <p className="cf-muted">Loading…</p> : null}

      {data ? (
        <>
          <table className="cf-table">
            <thead>
              <tr>
                <th>SKU</th>
                <th>Description</th>
                <th>ABC</th>
                <th>Temp</th>
                <th>Tracking</th>
                <th className="numeric">Pack levels</th>
                <th>Case GTIN</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((item) => {
                const casePack = item.packConfigurations.find((p) => p.level === 'Case');

                return (
                  <tr key={item.id}>
                    <td className="cf-mono">
                      <Link to="/items/$sku" params={{ sku: item.sku }}>
                        {item.sku}
                      </Link>
                    </td>
                    <td>{item.description}</td>
                    <td>
                      <span className={`pill pill-abc-${item.abcClass.toLowerCase()}`}>
                        {item.abcClass}
                      </span>
                    </td>
                    <td>{item.temperatureClass}</td>
                    <td>
                      {item.isLotTracked ? <span className="pill">lot</span> : null}
                      {item.isExpiryTracked ? <span className="pill pill-warn">expiry</span> : null}
                      {item.isSerialTracked ? <span className="pill">serial</span> : null}
                      {item.isHazardous ? <span className="pill pill-danger">hazard</span> : null}
                    </td>
                    <td className="numeric cf-mono">{item.packConfigurations.length}</td>
                    <td className="cf-mono">{casePack?.gtin ?? '—'}</td>
                  </tr>
                );
              })}
            </tbody>
          </table>

          <div className="pager">
            <button
              className="cf-button"
              type="button"
              disabled={page <= 1}
              onClick={() => setPage((current) => current - 1)}
            >
              Previous
            </button>
            <span className="cf-muted">
              Page {page} of {lastPage}
            </span>
            <button
              className="cf-button"
              type="button"
              disabled={page >= lastPage}
              onClick={() => setPage((current) => current + 1)}
            >
              Next
            </button>
          </div>
        </>
      ) : null}
    </>
  );
}
