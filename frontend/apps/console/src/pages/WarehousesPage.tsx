import { useQuery } from '@tanstack/react-query';
import { api } from '../api.js';

export function WarehousesPage() {
  const { data, isPending, error } = useQuery({
    queryKey: ['warehouses'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/warehouses');
      if (error) throw new Error('Could not load warehouses.');
      return data;
    },
  });

  return (
    <>
      <div className="page-head">
        <h2>Warehouses</h2>
      </div>

      {isPending ? <p className="cf-muted">Loading…</p> : null}
      {error ? <p className="cf-error">{(error as Error).message}</p> : null}

      {data && data.length === 0 ? (
        <p className="cf-muted">
          No warehouses yet. Run the migrator with <code>Seed__DemoData=true</code>.
        </p>
      ) : null}

      {data && data.length > 0 ? (
        <table className="cf-table">
          <thead>
            <tr>
              <th>Code</th>
              <th>Name</th>
              <th>City</th>
              <th>Country</th>
              <th>Status</th>
            </tr>
          </thead>
          <tbody>
            {data.map((warehouse) => (
              <tr key={warehouse.id}>
                <td className="cf-mono">{warehouse.code}</td>
                <td>{warehouse.name}</td>
                <td>{warehouse.city ?? '—'}</td>
                <td>{warehouse.countryCode ?? '—'}</td>
                <td>{warehouse.isActive ? 'Active' : 'Inactive'}</td>
              </tr>
            ))}
          </tbody>
        </table>
      ) : null}
    </>
  );
}
