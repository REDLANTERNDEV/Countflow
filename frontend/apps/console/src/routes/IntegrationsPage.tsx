import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import { InfoHelp,
  Badge,
  Button,
  Code,
  EmptyState,
  Input,
  Select,
  Spinner,
  Table,
  TableContainer,
  TBody,
  TD,
  TH,
  THead,
  TR,
} from '@countflow/ui';
import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { api } from '../api.js';
import { WebhooksPanel } from '../components/WebhooksPanel.js';
import { PageHead } from '../components/PageHead.js';

/**
 * The integrations that hold a machine credential, and the keys they hold.
 *
 * Two things on this screen are deliberate and easy to get wrong later.
 *
 * The **secret appears exactly once**, in a panel that has to be dismissed. There
 * is no endpoint that reads a key back — only its hash is stored — so this is not
 * a convenience that could be improved on; it is the only moment the value exists
 * anywhere but in the administrator's clipboard.
 *
 * **Revoked keys stay in the list.** A movement posted by an integration in March
 * names the account, and a key row that vanished would make the credential in that
 * log line unattributable. Revoking is not deleting anywhere in this system.
 */
export function IntegrationsPage() {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [name, setName] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [role, setRole] = useState('Operator');
  const [busy, setBusy] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);

  /** The one secret this application ever shows. Held in memory only. */
  const [issued, setIssued] = useState<{ prefix: string; secret: string } | null>(null);

  const { data, isPending, error, refetch } = useQuery({
    queryKey: ['service-accounts'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/auth/service-accounts', {});

      if (error) throw error;
      return data;
    },
  });

  async function run(call: () => Promise<{ error?: unknown }>) {
    setBusy(true);
    setErrorCode(null);

    const { error } = await call();
    setBusy(false);

    if (error) {
      setErrorCode((error as { code?: string }).code ?? 'unknown');
      return false;
    }

    void refetch();
    return true;
  }

  async function create() {
    const created = await run(async () =>
      api.POST('/api/v1/auth/service-accounts', {
        body: { name: name.trim(), displayName: displayName.trim(), roles: [role] },
      }),
    );

    if (created) {
      setName('');
      setDisplayName('');
    }
  }

  async function issue(accountId: string, keyName: string) {
    setBusy(true);
    setErrorCode(null);

    const { data, error } = await api.POST('/api/v1/auth/service-accounts/{id}/keys', {
      params: { path: { id: accountId } },
      body: { name: keyName },
    });

    setBusy(false);

    if (error || !data) {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
      return;
    }

    setIssued({ prefix: data.key.prefix, secret: data.secret });
    void refetch();
  }

  return (
    <>
      <PageHead help={t('integrations.subtitle')} title={t('integrations.title')} count={data?.length} />
      <WebhooksPanel />

      {issued ? (
        <section
          className="mb-[var(--space-5)] flex flex-col gap-[var(--space-3)]
                     rounded-[var(--radius-surface)] border border-[var(--color-accent)]
                     bg-[var(--color-surface-raised)] p-[var(--space-4)]"
        >
          <h3 className="m-0 text-[length:var(--text-lg)]">{t('integrations.issued.title')}</h3>
          <p className="m-0 max-w-[72ch] text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('integrations.issued.onlyOnce')}
          </p>
          <Code className="break-all rounded-[var(--radius-control)] bg-[var(--color-surface-sunken)] p-[var(--space-3)] text-[length:var(--text-sm)]">
            {issued.secret}
          </Code>
          <div className="flex gap-[var(--space-2)]">
            <Button
              variant="primary"
              size="sm"
              onClick={() => void navigator.clipboard?.writeText(issued.secret)}
            >
              {t('integrations.issued.copy')}
            </Button>
            <Button size="sm" onClick={() => setIssued(null)}>
              {t('integrations.issued.dismiss')}
            </Button>
          </div>
        </section>
      ) : null}

      <section
        className="mb-[var(--space-6)] flex flex-col gap-[var(--space-3)]
                   rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                   bg-[var(--color-surface-raised)] p-[var(--space-4)]"
      >
        <h3 className="m-0 text-[length:var(--text-lg)]">{t('integrations.create.title')} <InfoHelp label={t('common.about', { topic: t('integrations.create.title') })}>{t('integrations.create.hint')}</InfoHelp></h3>

        <div className="flex flex-wrap items-center gap-[var(--space-3)]">
          <Input
            className="w-[220px]"
            placeholder={t('integrations.create.name')}
            value={name}
            onChange={(event) => setName(event.target.value)}
          />
          <Input
            className="w-[260px]"
            placeholder={t('integrations.create.displayName')}
            value={displayName}
            onChange={(event) => setDisplayName(event.target.value)}
          />
          {/* The same three roles a person holds. There is no scope vocabulary
              here because there is none on the server: an integration that may
              release a wave is a Supervisor, judged by the identical check. */}
          <Select
            className="w-[180px]"
            value={role}
            onChange={(event) => setRole(event.target.value)}
            aria-label={t('integrations.create.role')}
          >
            <option value="Operator">{t('integrations.role.Operator')}</option>
            <option value="Supervisor">{t('integrations.role.Supervisor')}</option>
            <option value="Admin">{t('integrations.role.Admin')}</option>
          </Select>
          <Button
            variant="primary"
            disabled={busy || name.trim() === '' || displayName.trim() === ''}
            onClick={() => void create()}
          >
            {t('integrations.create.submit')}
          </Button>
        </div>

        {errorCode ? (
          <p className="m-0 text-[var(--color-danger)]">
            {t([`errors.${errorCode}`, 'errors.unknown'])}
          </p>
        ) : null}
      </section>

      {error ? <p className="text-[var(--color-danger)]">{t('errors.unknown')}</p> : null}
      {isPending ? <Spinner label={t('common.loading')} /> : null}

      {data?.length === 0 ? <EmptyState title={t('integrations.empty')} /> : null}

      {data?.map((account) => (
        <ServiceAccount
          key={account.id}
          account={account}
          busy={busy}
          onIssue={(keyName) => void issue(account.id, keyName)}
          onRevoke={(keyId) =>
            void run(async () =>
              api.POST('/api/v1/auth/service-accounts/keys/{keyId}/revoke', {
                params: { path: { keyId } },
              }),
            )
          }
          locale={locale}
        />
      ))}
    </>
  );
}

function ServiceAccount({
  account,
  busy,
  onIssue,
  onRevoke,
  locale,
}: {
  account: { id: string; name: string; displayName: string; roles: string[]; activeKeyCount: number };
  busy: boolean;
  onIssue: (keyName: string) => void;
  onRevoke: (keyId: string) => void;
  locale: string;
}) {
  const { t } = useTranslation();
  const [keyName, setKeyName] = useState('');

  const { data, refetch } = useQuery({
    queryKey: ['api-keys', account.id],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/v1/auth/service-accounts/{id}/keys', {
        params: { path: { id: account.id } },
      });

      if (error) throw error;
      return data;
    },
  });

  return (
    <section className="mb-[var(--space-5)]">
      <div className="mb-[var(--space-3)] flex flex-wrap items-baseline gap-[var(--space-3)]">
        <h3 className="m-0 text-[length:var(--text-lg)]">{account.displayName}</h3>
        <Code>{account.name}</Code>
        {account.roles.map((role) => (
          <Badge key={role} tone="info">
            {t(`integrations.role.${role}`)}
          </Badge>
        ))}
        <Badge tone={account.activeKeyCount > 0 ? 'success' : 'neutral'}>
          {t('integrations.liveKeys', { count: account.activeKeyCount })}
        </Badge>
      </div>

      <div className="mb-[var(--space-3)] flex flex-wrap items-center gap-[var(--space-2)]">
        <Input
          className="w-[260px]"
          placeholder={t('integrations.keys.namePlaceholder')}
          value={keyName}
          onChange={(event) => setKeyName(event.target.value)}
        />
        <Button
          size="sm"
          variant="primary"
          disabled={busy || keyName.trim() === ''}
          onClick={() => {
            onIssue(keyName.trim());
            setKeyName('');
            void refetch();
          }}
        >
          {t('integrations.keys.issue')}
        </Button>
      </div>

      <TableContainer>
        <Table>
          <THead>
            <TR>
              <TH>{t('integrations.keys.name')}</TH>
              <TH>{t('integrations.keys.prefix')}</TH>
              <TH>{t('integrations.keys.created')}</TH>
              <TH>{t('integrations.keys.lastUsed')}</TH>
              <TH>{t('integrations.keys.state')}</TH>
              <TH />
            </TR>
          </THead>
          <TBody>
            {(data ?? []).map((key) => (
              <TR key={key.id}>
                <TD>{key.name}</TD>
                <TD>
                  <Code>{key.prefix}…</Code>
                </TD>
                <TD>{formatDateTime(key.createdAt, locale)}</TD>
                <TD className="text-[var(--color-text-tertiary)]">
                  {key.lastUsedAt ? formatDateTime(key.lastUsedAt, locale) : t('integrations.keys.never')}
                </TD>
                <TD>
                  <Badge tone={key.revokedAt ? 'neutral' : 'success'}>
                    {key.revokedAt ? t('integrations.keys.revoked') : t('integrations.keys.live')}
                  </Badge>
                </TD>
                <TD>
                  {key.revokedAt ? null : (
                    <Button
                      size="sm"
                      disabled={busy}
                      onClick={() => {
                        onRevoke(key.id);
                        void refetch();
                      }}
                    >
                      {t('integrations.keys.revoke')}
                    </Button>
                  )}
                </TD>
              </TR>
            ))}
          </TBody>
        </Table>
      </TableContainer>
    </section>
  );
}
