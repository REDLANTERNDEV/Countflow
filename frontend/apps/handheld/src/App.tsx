import { useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, Input, LogoSymbol, ScanField, Spinner } from '@countflow/ui';
import { useCallback, useEffect, useState } from 'react';
import { api, tokenStore } from './api.js';
import { useConnectivity } from './useConnectivity.js';

interface CurrentUser {
  displayName: string;
  operatorCode: string;
  roles: string[];
}

export function App() {
  const { t } = useTranslation();
  const online = useConnectivity();

  const [user, setUser] = useState<CurrentUser | null>(null);
  const [initialising, setInitialising] = useState(true);
  const [operatorCode, setOperatorCode] = useState('');
  const [password, setPassword] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);
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
    setErrorCode(null);

    const { data, error } = await api.POST('/api/auth/login', {
      body: { identifier: code, password: secret },
    });

    if (error || !data) {
      const problem = error as { code?: string } | undefined;
      setErrorCode(problem?.code ?? 'unknown');
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
    return (
      <main className="grid min-h-screen place-items-center">
        <Spinner label={t('common.loading')} />
      </main>
    );
  }

  const shell =
    'flex min-h-screen flex-col gap-[var(--space-3)] p-[var(--space-5)] ' +
    'pt-[calc(var(--space-5)+env(safe-area-inset-top))]';

  if (!user) {
    return (
      <main className={shell}>
        <ConnectivityBadge online={online} />

        <LogoSymbol size={52} className="mt-[var(--space-3)]" />
        <h1 className="m-0 mt-[var(--space-2)] text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
          {t('auth.title')}
        </h1>
        <p className="mb-[var(--space-5)] mt-0 text-[var(--color-text-secondary)]">
          {t('auth.scanBadge')}
        </p>

        {/* Badge scan fills the code and submits in one motion when a password
            is already keyed; otherwise it just captures the code. */}
        <ScanField
          label={t('auth.operatorCode')}
          placeholder="OP001"
          clearOnScan={false}
          onScan={(value) => {
            setOperatorCode(value);
            if (password) void signIn(value, password);
          }}
        />

        {operatorCode ? (
          <p className="m-0 text-[var(--color-text-secondary)]">
            <Code>{operatorCode}</Code>
          </p>
        ) : null}

        <Field label={t('auth.password')} htmlFor="hh-password">
          <Input
            id="hh-password"
            type="password"
            value={password}
            onChange={(event) => setPassword(event.target.value)}
            autoComplete="current-password"
          />
        </Field>

        {errorCode ? (
          <p className="m-0 text-[var(--color-danger)]">
            {t([`errors.${errorCode}`, 'errors.unknown'])}
          </p>
        ) : null}

        <Button
          variant="primary"
          className="mt-[var(--space-3)] w-full"
          disabled={submitting || !operatorCode || !password}
          onClick={() => void signIn(operatorCode, password)}
        >
          {submitting ? t('common.signingIn') : t('common.signIn')}
        </Button>
      </main>
    );
  }

  return (
    <main className={shell}>
      <ConnectivityBadge online={online} />

      <h1 className="m-0 mt-[var(--space-2)] text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
        {user.displayName}
      </h1>
      <p className="mb-[var(--space-5)] mt-0 text-[var(--color-text-secondary)]">
        <Code>{user.operatorCode}</Code> · {user.roles.join(', ')}
      </p>

      <div className="rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                      bg-[var(--color-surface-raised)] p-[var(--space-5)] text-center">
        <p className="m-0 text-[var(--color-text-secondary)]">No work assigned.</p>
        <p className="mb-0 mt-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          Receiving and putaway tasks arrive in Phase 3, along with the offline
          command queue.
        </p>
      </div>

      <Button className="mt-auto w-full" onClick={() => void signOut()}>
        {t('common.signOut')}
      </Button>
    </main>
  );
}

function ConnectivityBadge({ online }: { online: boolean }) {
  const { t } = useTranslation();

  // Permanent rather than a transient toast: an operator needs to know at a
  // glance whether their last scan actually reached the server, and warehouse
  // coverage drops often enough that a popup would become noise.
  return (
    <Badge tone={online ? 'success' : 'warning'} className="self-start">
      {online ? t('common.online') : t('common.offline')}
    </Badge>
  );
}
