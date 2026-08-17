import { useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, Input, LogoSymbol, ScanField, Spinner } from '@countflow/ui';
import { useCallback, useEffect, useState } from 'react';
import { api, tokenStore } from './api.js';
import { unanswered } from './lib/outbox/dispatch.js';
import { needsAttention, pendingFor, strandedFrom } from './lib/outbox/outbox.js';
import { outbox, startOutbox } from './outbox.js';
import { HomeScreen } from './screens/HomeScreen.js';
import { QueueScreen } from './screens/QueueScreen.js';
import { ReceiveScreen } from './screens/ReceiveScreen.js';
import { TaskScreen } from './screens/TaskScreen.js';
import { forgetOperator, recallOperator, rememberOperator, type CurrentUser } from './session.js';
import { useConnectivity } from './useConnectivity.js';
import { useOutbox } from './useOutbox.js';

/**
 * Where the operator is.
 *
 * Deliberately not a router. A handheld has a handful of screens and no address
 * bar; a URL-driven router would add a dependency, a bundle, and a back button
 * that means nothing on a device with a hardware one. Phase 4 added picking and
 * Phase 5 counting without adding one; Phase 8's queue is the first genuinely
 * new destination since receiving, and it is still not a router.
 */
type Screen = { name: 'home' } | { name: 'receive' } | { name: 'task'; taskId: string } | { name: 'queue' };

export function App() {
  const { t } = useTranslation();
  const online = useConnectivity();
  const { commands, lastDrain, reachable } = useOutbox();

  // What the operator needs to know is whether the warehouse can hear them, which
  // is not the same question as whether the radio is on. `navigator.onLine` is
  // wrong in both directions: an access point with a dead API behind it reads as
  // online, and a device that has not noticed losing its link reads as online
  // too. So the link is the fast path and an actual unanswered request overrides
  // it — a distinction the container run turned from a comment into a bug report.
  const connected = online && reachable !== false;

  const [user, setUser] = useState<CurrentUser | null>(null);
  const [screen, setScreen] = useState<Screen>({ name: 'home' });
  const [initialising, setInitialising] = useState(true);
  const [operatorCode, setOperatorCode] = useState('');
  const [password, setPassword] = useState('');
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);
  const [confirmingSignOut, setConfirmingSignOut] = useState(false);

  useEffect(() => {
    async function restore() {
      if (!tokenStore.read()) {
        setInitialising(false);
        return;
      }

      // A handheld that reloads in a dead aisle — a crash, a service-worker
      // update, a battery swap — must not lose its operator: it is holding queued
      // work only that operator may send, and a sign-in screen it cannot get past
      // strands every command on it. The refresh token is still what authorises
      // anything; the cache is only who they are.
      //
      // `unanswered` rather than a `catch`, and that distinction was a defect
      // found by running this against the real stack. Caddy sits in front of the
      // API, so a dead API answers **502** instead of failing to answer — nothing
      // throws, and a version of this that only caught exceptions signed the
      // operator out with their work still on the device.
      try {
        const { data, response } = await api.GET('/api/v1/auth/me');
        outbox.noteReachable(!unanswered(response.status));

        if (data) {
          rememberOperator(data as CurrentUser);
          setUser(data as CurrentUser);
        } else if (unanswered(response.status)) {
          setUser(recallOperator());
        }
      } catch {
        outbox.noteReachable(false);
        setUser(recallOperator());
      }

      setInitialising(false);
    }

    void restore();
  }, []);

  // The queue belongs to whoever is signed in, and to nobody when nobody is.
  useEffect(() => {
    void outbox.setOwner(user ? { userId: user.id, operatorCode: user.operatorCode } : null);
  }, [user]);

  // Anything left mid-flight by a power cut goes back to pending, under the same
  // idempotency key it always had. Once, at start-up, before anything is sent.
  useEffect(() => {
    void outbox.recover();
    return startOutbox();
  }, []);

  // The "3 sent" line is news for a moment and clutter after that.
  useEffect(() => {
    if (!lastDrain) return;
    const timer = setTimeout(() => outbox.clearLastDrain(), 8_000);
    return () => clearTimeout(timer);
  }, [lastDrain]);

  const signIn = useCallback(async (code: string, secret: string) => {
    setSubmitting(true);
    setErrorCode(null);

    const { data, error, response } = await api.POST('/api/v1/auth/login', {
      body: { identifier: code, password: secret },
    });

    // A sign-in is the most conclusive answer the warehouse ever gives: the
    // device just spoke to it. Not saying so left the badge reading offline
    // after a shift change on a device whose previous operator's queue had
    // marked it unreachable — with "next job" disabled and nothing on the
    // screen able to disagree.
    outbox.noteReachable(!unanswered(response.status));

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

    rememberOperator(data.user as CurrentUser);
    setUser(data.user as CurrentUser);
    setPassword('');
    setSubmitting(false);
  }, []);

  /**
   * Signing out does not empty the queue.
   *
   * A queued command is attributed to the operator who made it, because the
   * ledger records who posted a movement and replaying OP001's putaway on
   * OP002's token would write a name into an append-only record that never
   * happened. So the commands stay on the device, owned, and wait — and the
   * operator is told that before they walk away, not after.
   */
  async function signOut() {
    const session = tokenStore.read();
    if (session) {
      try {
        await api.POST('/api/v1/auth/logout', { body: { refreshToken: session.refreshToken } });
      } catch {
        // Ending a shift in a dead zone still ends the shift. The refresh token
        // outlives it server-side and expires on its own.
      }
    }

    // The task cache is this operator's view of their jobs, and the next person
    // to pick the device up is not them. The queue is not cleared: see above.
    await outbox.store.clearTasks();

    tokenStore.clear();
    forgetOperator();
    setUser(null);
    setScreen({ name: 'home' });
    setOperatorCode('');
    setConfirmingSignOut(false);
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
    const stranded = strandedFrom(commands, null);

    return (
      <main className={shell}>
        <ConnectivityBadge online={connected} />

        <LogoSymbol size={52} className="mt-[var(--space-3)]" />
        <h1 className="m-0 mt-[var(--space-2)] text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
          {t('auth.title')}
        </h1>
        <p className="mb-[var(--space-5)] mt-0 text-[var(--color-text-secondary)]">
          {t('auth.scanBadge')}
        </p>

        {/* Unsent work belonging to whoever was here last. Shown on the sign-in
            screen because that is where the person who can send it will be
            standing, and because a device holding somebody's confirmations
            should never look empty. */}
        {stranded.length > 0 ? (
          <div
            className="flex flex-col gap-[var(--space-1)] rounded-[var(--radius-surface)]
                       border border-[var(--color-border-default)] bg-[var(--color-surface-raised)]
                       p-[var(--space-4)]"
          >
            <Badge tone="warning" className="self-start">
              {t('queue.strandedHeading', { operator: stranded[0]!.ownerOperatorCode })}
            </Badge>
            <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
              {t('queue.toSend', { count: stranded.length })} · {t('queue.strandedBlurb')}
            </p>
          </div>
        ) : null}

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

  const unsent = pendingFor(commands, user.id);
  const attention = needsAttention(commands).filter((c) => c.ownerUserId === user.id);

  if (confirmingSignOut) {
    return (
      <main className={shell}>
        <ConnectivityBadge online={connected} />

        <h1 className="m-0 mt-[var(--space-3)] text-[length:var(--text-xl)]">
          {t('queue.signOutBlocked', { count: unsent.length })}
        </h1>
        <p className="m-0 text-[var(--color-text-secondary)]">{t('queue.signOutBlockedHint')}</p>

        <Button
          variant="primary"
          className="mt-[var(--space-3)] w-full"
          disabled={!connected}
          onClick={() => void outbox.drain()}
        >
          {t('queue.sendNow')}
        </Button>
        {/* Leaves the confirmation as well as choosing the screen behind it.
            Without that this button appears to do nothing — the queue is set
            and then covered by this very screen, which is what it looked like
            the first time somebody pressed it. */}
        <Button
          className="w-full"
          onClick={() => {
            setScreen({ name: 'queue' });
            setConfirmingSignOut(false);
          }}
        >
          {t('queue.open')}
        </Button>
        <Button className="w-full" onClick={() => void signOut()}>
          {t('queue.signOutAnyway')}
        </Button>
        <Button variant="ghost" className="w-full" onClick={() => setConfirmingSignOut(false)}>
          {t('common.cancel')}
        </Button>
      </main>
    );
  }

  return (
    <main className={shell}>
      <div className="flex flex-wrap items-center gap-[var(--space-2)]">
        <ConnectivityBadge online={connected} />

        {attention.length > 0 ? (
          <Button variant="ghost" size="sm" onClick={() => setScreen({ name: 'queue' })}>
            <Badge tone="danger">{t('queue.needsYou', { count: attention.length })}</Badge>
          </Button>
        ) : null}

        {unsent.length > 0 ? (
          <Button variant="ghost" size="sm" onClick={() => setScreen({ name: 'queue' })}>
            <Badge tone="warning">{t('queue.toSend', { count: unsent.length })}</Badge>
          </Button>
        ) : null}

        {lastDrain && lastDrain.sent > 0 ? (
          <Badge tone="success">{t('queue.sent', { count: lastDrain.sent })}</Badge>
        ) : null}
      </div>

      {screen.name === 'home' ? (
        <HomeScreen
          user={user}
          online={connected}
          onReceive={() => setScreen({ name: 'receive' })}
          onOpenTask={(taskId) => setScreen({ name: 'task', taskId })}
          onOpenQueue={() => setScreen({ name: 'queue' })}
          onSignOut={() => {
            if (unsent.length > 0) {
              setConfirmingSignOut(true);
              return;
            }

            void signOut();
          }}
        />
      ) : null}

      {screen.name === 'receive' ? (
        <ReceiveScreen
          onBack={() => setScreen({ name: 'home' })}
          onOpenTask={(taskId) => setScreen({ name: 'task', taskId })}
        />
      ) : null}

      {screen.name === 'task' ? (
        <TaskScreen
          taskId={screen.taskId}
          ownerUserId={user.id}
          onDone={() => setScreen({ name: 'home' })}
        />
      ) : null}

      {screen.name === 'queue' ? (
        <QueueScreen ownerUserId={user.id} onBack={() => setScreen({ name: 'home' })} />
      ) : null}
    </main>
  );
}

function ConnectivityBadge({ online }: { online: boolean }) {
  const { t } = useTranslation();

  // Permanent rather than a transient toast: an operator needs to know at a
  // glance whether their last scan actually reached the server, and warehouse
  // coverage drops often enough that a popup would become noise.
  //
  // It reports the device's link and nothing more. Whether a command landed is
  // answered by the queue beside it, because the flag is wrong in both
  // directions — an access point with a dead API behind it reads as online, and
  // a device that has not noticed losing its link reads as online too.
  return (
    <Badge tone={online ? 'success' : 'warning'}>
      {online ? t('common.online') : t('common.offline')}
    </Badge>
  );
}
