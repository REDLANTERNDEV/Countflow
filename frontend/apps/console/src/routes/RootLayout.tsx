import { Link, Outlet } from '@tanstack/react-router';
import { useAuth } from '../auth.js';

export function RootLayout() {
  const { user, signOut } = useAuth();

  return (
    <div className="app-shell">
      <header className="app-header">
        <div className="app-brand">
          <strong>Countflow</strong> <span className="cf-muted">console</span>
        </div>

        <nav className="app-nav">
          <Link to="/" activeOptions={{ exact: true }} className="app-nav-link">
            Warehouses
          </Link>
          <Link to="/locations" className="app-nav-link">
            Locations
          </Link>
          <Link to="/items" className="app-nav-link">
            Items
          </Link>
          <Link to="/scan" className="app-nav-link">
            Scan test
          </Link>
        </nav>

        <div className="app-header-user">
          <span className="cf-muted">
            {user?.displayName} · {user?.roles.join(', ')}
          </span>
          <button className="cf-button" type="button" onClick={() => void signOut()}>
            Sign out
          </button>
        </div>
      </header>

      <main className="app-main">
        <Outlet />
      </main>
    </div>
  );
}
