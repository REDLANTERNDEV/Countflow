import { useCallback, useEffect, useState } from 'react';
import { ScanField } from '@countflow/ui';
import { api, tokenStore } from './api.js';
import { useConnectivity } from './useConnectivity.js';

interface CurrentUser {
  displayName: string;
  operatorCode: string;
  roles: string[];
}

export function App() {
  const online = useConnectivity();
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [initialising, setInitialising] = useState(true);
  const [operatorCode, setOperatorCode] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  useEffect(() => {
    async function restore() {
      if (!tokenStore.read()) {
        setInitialising(false);
        return;
      }

      const { data } = await api.GET('/api/auth/me');
      setUser((data as CurrentUser | undefined) ?? null);
      setInitialising(false);
    }

    void restore();
  }, []);

  const signIn = useCallback(async (code: string, secret: string) => {
    setSubmitting(true);
    setError(null);

    const { data, error: failure } = await api.POST('/api/auth/login', {
      body: { identifier: code, password: secret },
    });

    if (failure || !data) {
      const problem = failure as { detail?: string } | undefined;
      setError(problem?.detail ?? 'Sign in failed.');
      setSubmitting(false);
      return;
    }

    tokenStore.write({
      accessToken: data.accessToken,
      accessTokenExpiresAt: data.accessTokenExpiresAt,
      refreshToken: data.refreshToken,
    });

    setUser(data.user as CurrentUser);
    setPassword('');
    setSubmitting(false);
  }, []);

  async function signOut() {
    const session = tokenStore.read();
    if (session) {
      await api.POST('/api/auth/logout', { body: { refreshToken: session.refreshToken } });
    }

    tokenStore.clear();
    setUser(null);
    setOperatorCode('');
  }

  if (initialising) {
    return <p className="boot">Starting…</p>;
  }

  if (!user) {
    return (
      <main className="hh-shell">
        <ConnectivityBadge online={online} />

        <h1 className="hh-title">Countflow</h1>
        <p className="hh-subtitle">Scan your badge or key your operator code</p>

        {/* Badge scan fills the code and submits in one motion when a password
            is already keyed; otherwise it just captures the code. */}
        <ScanField
          label="Operator code"
          placeholder="OP001"
          clearOnScan={false}
          onScan={(value) => {
            setOperatorCode(value);
            if (password) void signIn(value, password);
          }}
        />

        {operatorCode ? (
          <p className="hh-captured">
            Code: <span className="cf-mono">{operatorCode}</span>
          </p>
        ) : null}

        <div className="cf-field">
          <label className="cf-label" htmlFor="hh-password">
            Password
          </label>
          <input
            id="hh-password"
            type="password"
            className="cf-input hh-input"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
          />
        </div>

        {error ? <p className="cf-error">{error}</p> : null}

        <button
          className="cf-button hh-button"
          type="button"
          disabled={submitting || !operatorCode || !password}
          onClick={() => void signIn(operatorCode, password)}
        >
          {submitting ? 'Signing in…' : 'Sign in'}
        </button>
      </main>
    );
  }

  return (
    <main className="hh-shell">
      <ConnectivityBadge online={online} />

      <h1 className="hh-title">{user.displayName}</h1>
      <p className="hh-subtitle">
        <span className="cf-mono">{user.operatorCode}</span> · {user.roles.join(', ')}
      </p>

      <div className="cf-card hh-empty">
        <p className="cf-muted">No work assigned.</p>
        <p className="cf-muted hh-small">
          Receiving and putaway tasks arrive in Phase 3, along with the offline
          command queue.
        </p>
      </div>

      <button className="cf-button hh-button hh-secondary" type="button" onClick={() => void signOut()}>
        Sign out
      </button>
    </main>
  );
}

function ConnectivityBadge({ online }: { online: boolean }) {
  return (
    <div className={`hh-connectivity ${online ? 'is-online' : 'is-offline'}`}>
      {online ? 'Online' : 'Offline — work is queued'}
    </div>
  );
}
