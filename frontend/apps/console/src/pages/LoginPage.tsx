import { useTranslation } from '@countflow/i18n';
import { Badge, Button, Field, Input, LogoSymbol } from '@countflow/ui';
import { useState, type FormEvent } from 'react';
import { useAuth } from '../auth.js';
import { LanguageSwitcher } from '../components/LanguageSwitcher.js';
import { ThemeSwitcher } from '../components/ThemeSwitcher.js';

/**
 * The demo accounts, when this build is one.
 *
 * Read from the build-time environment rather than from the API, deliberately.
 * An endpoint that answered "which credentials work here" would exist in every
 * deployment and be one misconfiguration away from answering it in production;
 * a variable that is simply absent from a production build cannot. `VITE_`-prefixed
 * values are inlined by Vite at build time, so the demo image carries them and the
 * real one does not.
 */
const demoAccounts = (import.meta.env.VITE_DEMO_ACCOUNTS ?? '')
  .split(',')
  .map((entry: string) => entry.trim())
  .filter(Boolean)
  .map((entry: string) => {
    const [identifier, ...rest] = entry.split(':');
    return { identifier, password: rest.join(':') };
  })
  .filter((account: { identifier: string; password: string }) => account.password !== '');

export function LoginPage() {
  const { signIn } = useAuth();
  const { t } = useTranslation();

  const [identifier, setIdentifier] = useState('');
  const [password, setPassword] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(event: FormEvent) {
    event.preventDefault();
    setSubmitting(true);
    setErrorCode(null);

    const failure = await signIn(identifier, password);
    setErrorCode(failure);
    setSubmitting(false);
  }

  async function signInAs(account: { identifier: string; password: string }) {
    setSubmitting(true);
    setErrorCode(null);
    setIdentifier(account.identifier);
    setPassword(account.password);

    setErrorCode(await signIn(account.identifier, account.password));
    setSubmitting(false);
  }

  return (
    <main className="grid min-h-screen place-items-center p-[var(--space-5)]">
      {/* Language and theme are reachable before signing in — an operator who
          cannot read English needs to switch before they can find anything. */}
      <div className="absolute right-[var(--space-5)] top-[var(--space-5)] flex gap-[var(--space-3)]">
        <LanguageSwitcher />
        <ThemeSwitcher />
      </div>

      <div className="flex flex-wrap items-start justify-center gap-[var(--space-6)]">
      <form
        onSubmit={handleSubmit}
        className="w-[min(360px,100%)] rounded-[var(--radius-surface)]
                   border border-[var(--color-border-subtle)]
                   bg-[var(--color-surface-raised)] p-[var(--space-6)]"
      >
        <LogoSymbol size={40} className="mb-[var(--space-4)]" />
        <h1 className="m-0 text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
          {t('auth.title')}
        </h1>
        <p className="mb-[var(--space-6)] mt-[var(--space-1)] text-[var(--color-text-tertiary)]">
          {t('auth.subtitle')}
        </p>

        <div className="flex flex-col gap-[var(--space-4)]">
          <Field label={t('auth.identifier')} htmlFor="identifier">
            <Input
              id="identifier"
              value={identifier}
              onChange={(event) => setIdentifier(event.target.value)}
              autoComplete="username"
              required
            />
          </Field>

          <Field label={t('auth.password')} htmlFor="password">
            <Input
              id="password"
              type="password"
              value={password}
              onChange={(event) => setPassword(event.target.value)}
              autoComplete="current-password"
              required
            />
          </Field>

          {errorCode ? (
            <p className="text-[length:var(--text-sm)] text-[var(--color-danger)]">
              {/* The server sent a code; the wording is ours, in the reader's
                  language. */}
              {t([`errors.${errorCode}`, 'errors.unknown'])}
            </p>
          ) : null}

          <Button type="submit" variant="primary" disabled={submitting} className="w-full">
            {submitting ? t('common.signingIn') : t('common.signIn')}
          </Button>

          {demoAccounts.length > 0 ? (
            <div className="flex flex-col gap-[var(--space-2)] border-t border-[var(--color-border-subtle)] pt-[var(--space-4)]">
              <span className="text-[length:var(--text-xs)] uppercase tracking-[var(--tracking-label)] text-[var(--color-text-tertiary)]">
                {t('demo.signInAs')}
              </span>
              <div className="flex flex-wrap gap-[var(--space-2)]">
                {demoAccounts.map((account: { identifier: string; password: string }) => (
                  <Button
                    key={account.identifier}
                    type="button"
                    size="sm"
                    disabled={submitting}
                    onClick={() => void signInAs(account)}
                  >
                    {account.identifier}
                  </Button>
                ))}
              </div>
              <p className="m-0 text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
                {t('demo.resetWarning')}
              </p>
            </div>
          ) : null}
        </div>
      </form>

      {/* Only on a demo build. On a real deployment the login screen is a door,
          and a door does not need to explain the building. */}
      {demoAccounts.length > 0 ? (
        <aside
          className="w-[min(420px,100%)] rounded-[var(--radius-surface)]
                     border border-[var(--color-border-subtle)]
                     bg-[var(--color-surface-raised)] p-[var(--space-6)]"
        >
          <Badge tone="info" className="mb-[var(--space-3)]">
            {t('demo.badge')}
          </Badge>
          <h2 className="m-0 text-[length:var(--text-xl)] tracking-[var(--tracking-display)]">
            {t('demo.headline')}
          </h2>
          <p className="mb-[var(--space-4)] mt-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('demo.blurb')}
          </p>

          <ul className="m-0 flex list-none flex-col gap-[var(--space-3)] p-0">
            {(['ledger', 'rules', 'replay'] as const).map((pillar) => (
              <li key={pillar} className="flex flex-col">
                <span className="text-[length:var(--text-sm)] font-semibold">
                  {t(`tour.stops.${pillar}.title`)}
                </span>
                <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                  {t(`tour.stops.${pillar}.body`)}
                </span>
              </li>
            ))}
          </ul>

          <p className="mb-0 mt-[var(--space-4)] text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {t('demo.tourHint')}
          </p>
        </aside>
      ) : null}
      </div>
    </main>
  );
}
