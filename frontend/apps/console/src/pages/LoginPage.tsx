import { useTranslation } from '@countflow/i18n';
import { Button, Field, Input, LogoSymbol } from '@countflow/ui';
import { useState, type FormEvent } from 'react';
import { useAuth } from '../auth.js';
import { LanguageSwitcher } from '../components/LanguageSwitcher.js';
import { ThemeSwitcher } from '../components/ThemeSwitcher.js';

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

  return (
    <main className="grid min-h-screen place-items-center p-[var(--space-5)]">
      {/* Language and theme are reachable before signing in — an operator who
          cannot read English needs to switch before they can find anything. */}
      <div className="absolute right-[var(--space-5)] top-[var(--space-5)] flex gap-[var(--space-3)]">
        <LanguageSwitcher />
        <ThemeSwitcher />
      </div>

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
        </div>
      </form>
    </main>
  );
}
