import { useState, type FormEvent } from 'react';
import { useAuth } from '../auth.js';

export function LoginPage() {
  const { signIn } = useAuth();
  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setError(null);

    const failure = await signIn(identifier, password);
    setError(failure);
    setSubmitting(false);
  }

  return (
    <main className="login-shell">
      <form className="cf-card login-card" onSubmit={handleSubmit}>
        <h1 className="login-title">Countflow</h1>
        <p className="cf-muted login-subtitle">Warehouse console</p>

        <div className="cf-field">
          <label className="cf-label" htmlFor="identifier">
            Username or operator code
          </label>
          <input
            id="identifier"
            className="cf-input"
            value={identifier}
            onChange={(event) => setIdentifier(event.target.value)}
            autoComplete="username"
            required
          />
        </div>

        <div className="cf-field">
          <label className="cf-label" htmlFor="password">
            Password
          </label>
          <input
            id="password"
            type="password"
            className="cf-input"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
            required
          />
        </div>

        {error ? <p className="cf-error">{error}</p> : null}

        <button className="cf-button login-submit" type="submit" disabled={submitting}>
          {submitting ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </main>
  );
}
