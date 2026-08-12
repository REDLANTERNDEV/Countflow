import { currentLocale, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Numeric, Spinner } from '@countflow/ui';
import { useEffect, useState } from 'react';
import { api } from '../api.js';
import { unanswered } from '../lib/outbox/dispatch.js';
import { needsAttention } from '../lib/outbox/outbox.js';
import { outbox } from '../outbox.js';
import type { CurrentUser } from '../session.js';
import { recallMyTasks, reconcileTasks, rememberTask, type TaskDetail } from '../tasks.js';
import { useOutbox } from '../useOutbox.js';

interface HomeScreenProps {
  user: CurrentUser;
  online: boolean;
  onReceive: () => void;
  onOpenTask: (taskId: string) => void;
  onOpenQueue: () => void;
  onSignOut: () => void;
}

/**
 * What an operator sees between jobs.
 *
 * Two verbs and nothing else, still. Phase 8 adds a third thing that is not a
 * verb: what the device is holding on the operator's behalf. It sits above the
 * buttons rather than behind a menu, and it does not go away on its own — a
 * refusal an operator has not seen is a discrepancy nobody is going to walk back
 * and fix.
 *
 * **"Next job" needs a signal and says so.** Claiming is a server decision —
 * `FOR UPDATE SKIP LOCKED` over one queue is exactly what stops thirty operators
 * being handed the same pallet — so a device that answered it locally would be
 * inventing work. What the device *can* do offline is give back the jobs it was
 * already given, which is the honest half of the same idea. See ADR 0008.
 */
export function HomeScreen(props: HomeScreenProps) {
  const { user, online, onReceive, onOpenTask, onOpenQueue, onSignOut } = props;
  const { t } = useTranslation();
  const locale = currentLocale();
  const { commands } = useOutbox();

  const [finding, setFinding] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [held, setHeld] = useState<TaskDetail[]>([]);

  const attention = needsAttention(commands).filter((c) => c.ownerUserId === user.id);

  // What this operator is holding, from the device. Refreshed from the server
  // whenever there is one, so the cache is warm before it is needed rather than
  // built at the moment the network goes — which is the moment it cannot be.
  useEffect(() => {
    let cancelled = false;

    async function load() {
      if (online) {
        try {
          const { data, response } = await api.GET('/api/v1/work/tasks', {
            params: { query: { mine: true, status: 'Assigned' } },
          });

          outbox.noteReachable(!unanswered(response.status));

          // Reconciled against the warehouse rather than merged into, so a job
          // somebody else finished stops being advertised on this device.
          if (data) await reconcileTasks(data.items, user.id);
        } catch {
          // The link went between the check and the call. The cache below is
          // what this whole path exists for.
          outbox.noteReachable(false);
        }
      }

      const cached = await recallMyTasks(user.id);
      if (!cancelled) setHeld(cached);
    }

    void load();
    return () => {
      cancelled = true;
    };
  }, [online, user.id, commands]);

  async function takeNext() {
    setFinding(true);
    setErrorCode(null);

    try {
      const { data, error, response } = await api.POST('/api/v1/work/tasks/claim-next', { body: {} });

      outbox.noteReachable(!unanswered(response.status));

      if (error || !data) {
        // "No work available" is not a failure — it is the answer, and on a quiet
        // shift it is the usual one. A 502 from the proxy in front of a stopped
        // API is a different thing entirely and must not be read as "no work".
        setErrorCode(
          unanswered(response.status)
            ? 'offline'
            : (error as { code?: string } | undefined)?.code ?? 'unknown',
        );
        return;
      }

      await rememberTask(data, user.id);
      onOpenTask(data.task.id);
    } catch {
      // Claiming is the one thing on this screen that cannot be queued: it asks
      // the warehouse to decide something rather than telling it what happened.
      outbox.noteReachable(false);
      setErrorCode('offline');
    } finally {
      setFinding(false);
    }
  }

  return (
    <>
      <h1 className="m-0 mt-[var(--space-2)] text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
        {user.displayName}
      </h1>
      <p className="mb-[var(--space-4)] mt-0 text-[var(--color-text-secondary)]">
        <Code>{user.operatorCode}</Code> · {user.roles.join(', ')}
      </p>

      {/* Above the buttons on purpose. An operator who has to go looking for the
          news that their putaway was refused will not go looking for it. */}
      {attention.length > 0 ? (
        <button
          type="button"
          onClick={onOpenQueue}
          className="flex w-full flex-col items-start gap-[var(--space-2)] rounded-[var(--radius-surface)]
                     border border-[var(--color-danger)] bg-[var(--color-danger-muted)]
                     p-[var(--space-4)] text-left"
        >
          <Badge tone="danger">{t('queue.needsYou', { count: attention.length })}</Badge>
          <span className="text-[var(--color-text-secondary)]">{t('queue.needsYouBlurb')}</span>
        </button>
      ) : null}

      <Button
        variant="primary"
        className="w-full"
        disabled={finding || !online}
        onClick={() => void takeNext()}
      >
        {finding ? <Spinner label={t('work.takingJob')} /> : t('work.nextJob')}
      </Button>

      {!online ? (
        <p className="m-0 -mt-[var(--space-1)] text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {t('queue.offlineNoNewWork')}
        </p>
      ) : null}

      <Button className="w-full" onClick={onReceive}>
        {t('inbound.receive.title')}
      </Button>

      <Button className="w-full" onClick={onOpenQueue}>
        {t('queue.open')}
      </Button>

      {/* The jobs already given to this operator. Workable with no network,
          because the device has them; and stale, because the device has them. */}
      {held.length > 0 ? (
        <div className="mt-[var(--space-3)] flex flex-col gap-[var(--space-2)]">
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('queue.myTasks')}
          </span>

          {held.map((detail) => (
            <button
              key={detail.task.id}
              type="button"
              onClick={() => onOpenTask(detail.task.id)}
              className="flex w-full flex-col items-start gap-[var(--space-1)]
                         rounded-[var(--radius-surface)] border border-[var(--color-border-default)]
                         bg-[var(--color-surface-raised)] p-[var(--space-4)] text-left"
            >
              <Badge>{t(`work.kind.${detail.task.kind}`)}</Badge>
              <span className="flex items-baseline gap-[var(--space-2)] text-[length:var(--text-lg)]">
                <Numeric value={detail.task.quantity} locale={locale} />
                <span className="text-[var(--color-text-secondary)]">×</span>
                <Code className="text-[length:var(--text-lg)]">{detail.task.sku}</Code>
              </span>
              <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                <Code>{detail.task.fromLocationCode}</Code>
                {detail.task.suggestedLocationCode ? (
                  <>
                    {' → '}
                    <Code>{detail.task.suggestedLocationCode}</Code>
                  </>
                ) : null}
              </span>
            </button>
          ))}
        </div>
      ) : !online ? (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {t('queue.noCachedTasks')}
        </p>
      ) : null}

      {errorCode ? (
        <div
          className="rounded-[var(--radius-surface)] border border-[var(--color-border-subtle)]
                     bg-[var(--color-surface-raised)] p-[var(--space-5)] text-center"
        >
          <p className="m-0 text-[var(--color-text-secondary)]">
            {errorCode === 'offline'
              ? t('queue.offlineNoNewWork')
              : t([`errors.${errorCode}`, 'errors.unknown'])}
          </p>
          {errorCode === 'work.no_work_available' ? (
            <p className="mb-0 mt-[var(--space-2)] text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
              {t('work.noWorkHint')}
            </p>
          ) : null}
        </div>
      ) : null}

      <Button className="mt-auto w-full" onClick={onSignOut}>
        {t('common.signOut')}
      </Button>
    </>
  );
}
