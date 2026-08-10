import { RouterProvider } from '@tanstack/react-router';
import { useAuth } from './auth.js';
import { LoginPage } from './pages/LoginPage.js';
import { router } from './router.js';

export function App() {
  const { user, initialising } = useAuth();

  if (initialising) {
    return <p className="cf-muted boot-message">Starting Countflow…</p>;
  }

  // The router only mounts once there is a session, so no route has to defend
  // itself against an unauthenticated render.
  return user ? <RouterProvider router={router} /> : <LoginPage />;
}
