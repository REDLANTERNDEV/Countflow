import { keepPreviousData, useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';

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
  const [search, setSearch] = useState('');
  const [type, setType] = useState('');
  const [pickableOnly, setPickableOnly] = useState(false);
  const [page, setPage] = useState(1);

  const pageSize = 50;

  const { data, isPending, error } = useQuery({
    queryKey: ['locations', search, type, pickableOnly, page],
    // Without this the table blanks out on every keystroke; the warehouse has
    // thousands of bins and the flicker makes it feel broken.
    placeholderData: keepPreviousData,
    queryFn: async () => {
      const { data, error } = await api.GET('/api/locations', {
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

      if (error) throw new Error('Could not load locations.');
      return data;
    },
  });

  const total = data?.total ?? 0;
  const lastPage = Math.max(1, Math.ceil(total / pageSize));

  function update<T>(setter: (value: T) => void) {
    return (value: T) => {
      setter(value);
      setPage(1);
    };
  }

  return (
    <>
      <div className="page-head">
        <h2>Locations</h2>
        <span className="cf-muted">{total.toLocaleString()} total</span>
      </div>

      <div className="filter-bar">
        <input
          className="cf-input"
          placeholder="Search by code, e.g. A07-03"
          value={search}
          onChange={(event) => update(setSearch)(event.target.value)}
        />

        <select
          className="cf-input"
          value={type}
          onChange={(event) => update(setType)(event.target.value)}
        >
          <option value="">All types</option>
          {LOCATION_TYPES.map((option) => (
            <option key={option} value={option}>
              {option}
            </option>
          ))}
        </select>

        <label className="filter-check">
          <input
            type="checkbox"
            checked={pickableOnly}
            onChange={(event) => update(setPickableOnly)(event.target.checked)}
          />
          Pickable only
        </label>
      </div>

      {error ? <p className="cf-error">{(error as Error).message}</p> : null}
      {isPending ? <p className="cf-muted">Loading…</p> : null}

      {data ? (
        <>
          <table className="cf-table">
            <thead>
              <tr>
                <th>Code</th>
                <th>Type</th>
                <th>Zone</th>
                <th>Aisle</th>
                <th>Bay</th>
                <th>Level</th>
                <th className="numeric">Seq</th>
                <th className="numeric">Max m³</th>
                <th className="numeric">Max kg</th>
                <th>Flags</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((location) => (
                <tr key={location.id}>
                  <td className="cf-mono">{location.code}</td>
                  <td>{location.type}</td>
                  <td>{location.zoneCode ?? <span className="cf-muted">virtual</span>}</td>
                  <td className="cf-mono">{location.aisle ?? '—'}</td>
                  <td className="cf-mono">{location.bay ?? '—'}</td>
                  <td className="cf-mono">{location.level ?? '—'}</td>
                  <td className="numeric cf-mono">{location.pickSequence}</td>
                  <td className="numeric cf-mono">{location.maxVolumeM3 ?? '—'}</td>
                  <td className="numeric cf-mono">{location.maxWeightKg ?? '—'}</td>
                  <td>
                    {location.isPickable ? <span className="pill pill-ok">pick</span> : null}
                    {location.isReplenishable ? <span className="pill">replen</span> : null}
                    {!location.isActive ? <span className="pill pill-warn">blocked</span> : null}
                  </td>
                </tr>
              ))}
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
