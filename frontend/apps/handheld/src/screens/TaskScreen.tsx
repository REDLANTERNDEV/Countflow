import type { components } from '@countflow/api-client';
import { completionLocationAllowed } from '../lib/completion-location.js';
import { currentLocale, formatDateTime, formatNumber, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, Input, Numeric, ScanField, Spinner } from '@countflow/ui';
import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api.js';
import { WhyHere } from '../components/WhyHere.js';
import { BlindBinCount } from '../components/BlindBinCount.js';
import { newIdempotencyKey } from '../lib/idempotency.js';
import { unanswered } from '../lib/outbox/dispatch.js';
import { categorise, needsSupervisor } from '../lib/outbox/refusals.js';
import type { CommandSummary, QueuedCommand } from '../lib/outbox/types.js';
import { outbox } from '../outbox.js';
import { recallTask, rememberTask, type TaskDetail } from '../tasks.js';
import { useOutbox } from '../useOutbox.js';

type CountOutcome = components['schemas']['CountOutcome'];
type CompleteResponse = components['schemas']['CompleteWorkTaskResponse'];

interface TaskScreenProps {
  taskId: string;
  ownerUserId: string;
  onDone: () => void;
}

/**
 * One job: what to move, where the rules say it goes, and why.
 *
 * The confirmation is a scan of the location label rather than a "yes" button,
 * and that is the whole safety property of directed work. A button confirms that
 * the operator read the screen; a scan confirms they are standing there.
 *
 * Phase 4 added picking and did not add a screen. A pick is the same shape of job
 * as a putaway — take this from here, confirm it at there — so the only thing
 * that changes is the sentence above the destination. If it had needed more than
 * that, the task model would have been drawn wrong.
 *
 * Phase 5 added counting and did not add a screen either, which was less obvious:
 * a count is the first job whose completion is not a movement. It still scans a
 * location — the bay it is asking about — and it additionally asks for a number,
 * so the screen grows one field rather than a second life.
 *
 * The book figure is hidden behind a button on purpose. A counter shown "the
 * system says 40" before they count will find forty, every time, and the count
 * becomes an expensive way of confirming what was already believed. It is
 * available, because an operator who has counted eight and is looking at a
 * suspiciously round variance deserves to see it — but they have to ask.
 *
 * <b>Phase 8 changed how the confirmation leaves, and not what it says.</b> It
 * goes to the device's outbox rather than to `fetch`, online or not, and the
 * screen then has three endings rather than one: the warehouse took it, the
 * warehouse refused it, or the device is holding it. The third is emphatically
 * not shown as success — an operator told "done" for work the warehouse has
 * never heard of has been lied to, and will find out at the worst possible
 * moment, which is when somebody asks them about a pallet a week later.
 */
export function TaskScreen({ taskId, ownerUserId, onDone }: TaskScreenProps) {
  const { t } = useTranslation();
  const locale = currentLocale();
  const { commands } = useOutbox();

  const [detail, setDetail] = useState<TaskDetail | null>(null);
  const [staleSince, setStaleSince] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [showWhy, setShowWhy] = useState(false);
  const [confirmed, setConfirmed] = useState('');
  const [overrideReason, setOverrideReason] = useState('');
  const [counted, setCounted] = useState('');
  const [revealBook, setRevealBook] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);

  // One key per intent, minted once and never again — including across the
  // reload a service-worker update or a flat battery causes, because from the
  // moment it is submitted the key lives in IndexedDB as the command's own
  // identity rather than in this ref.
  const idempotencyKey = useRef(newIdempotencyKey());

  /** This task's confirmation, wherever it has got to. */
  const command: QueuedCommand | undefined = commands.find(
    (c) => c.kind === 'work.complete' && (c.request as { taskId: string }).taskId === taskId,
  );

  const load = useCallback(async () => {
    setLoading(true);

    // A job the device was already given is workable out of signal; a job it has
    // never seen is not, and there is no honest way around that — see ADR 0008.
    const fromCache = async () => {
      const cached = await recallTask(taskId);

      if (!cached) {
        setErrorCode('offline');
        return;
      }

      setDetail(cached.detail);
      setStaleSince(cached.fetchedAt);
    };

    try {
      const { data, error, response } = await api.GET('/api/v1/work/tasks/{id}', {
        params: { path: { id: taskId } },
      });

      outbox.noteReachable(!unanswered(response.status));

      if (error || !data) {
        // 502 from the proxy in front of a stopped API is "nobody answered", not
        // "no such task". Deciding that by catching an exception was a defect.
        if (unanswered(response.status)) {
          await fromCache();
        } else {
          setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
        }
        return;
      }

      await rememberTask(data, ownerUserId);
      setDetail(data);
      setStaleSince(null);
    } catch {
      outbox.noteReachable(false);
      await fromCache();
    } finally {
      setLoading(false);
    }
  }, [taskId, ownerUserId]);

  useEffect(() => {
    void load();
  }, [load]);

  // Claiming is separate from opening, so a supervisor can look at a job on the
  // console without taking it out of somebody's queue.
  const claim = useCallback(async () => {
    try {
      const { data, error, response } = await api.POST('/api/v1/work/tasks/{id}/claim', {
        params: { path: { id: taskId } },
      });

      outbox.noteReachable(!unanswered(response.status));

      if (data) {
        await rememberTask(data, ownerUserId);
        setDetail(data);
        return;
      }

      // Claiming is not queueable — it asks the warehouse to decide something
      // rather than telling it what happened (ADR 0008) — so a failure here is
      // shown rather than saved.
      setErrorCode(
        unanswered(response.status)
          ? 'offline'
          : (error as { code?: string } | undefined)?.code ?? 'unknown',
      );
    } catch {
      outbox.noteReachable(false);
      setErrorCode('offline');
    }
  }, [taskId, ownerUserId]);

  async function complete() {
    if (!detail) return;

    const task = detail.task;
    if (!completionLocationAllowed(task.kind, confirmed, task.fromLocationCode, task.suggestedLocationCode)) {
      setErrorCode(task.kind === 'Replenishment' ? 'work.replenishment_destination_mismatch' : task.kind === 'CycleCount' ? 'work.count_location_mismatch' : 'work.location_not_found');
      return;
    }
    setSubmitting(true);
    setErrorCode(null);

    const isCount = task.kind === 'CycleCount';
    const at = confirmed.trim().toUpperCase();
    const reason = overrideReason.trim();
    const found = counted.trim();

    const summary: CommandSummary = {
      headline: `work.kind.${task.kind}`,
      sku: task.sku,
      description: task.description,
      quantity: task.quantity,
      uom: task.baseUom,
      fromLocationCode: task.fromLocationCode,
      atLocationCode: at,
      ...(task.lotNumber ? { lotNumber: task.lotNumber } : {}),
      ...(task.sscc ? { sscc: task.sscc } : {}),
      ...(task.sourceReference ? { reference: task.sourceReference } : {}),
      ...(isCount && found ? { countedQuantity: Number(found) } : {}),
    };

    await outbox.submit({
      kind: 'work.complete',
      key: idempotencyKey.current,
      request: {
        taskId,
        ...(task.pickTourId ? { pickTourId: task.pickTourId } : {}),
        toLocationCode: at,
        ...(reason ? { overrideReason: reason } : {}),
        ...(isCount && found ? { countedQuantity: Number(found) } : {}),
      },
      summary,
    });

    setSubmitting(false);
  }

  if (loading && !detail) {
    return <Spinner label={t('common.loading')} />;
  }

  // A confirmation that has already been made is shown in whatever state it
  // reached, even if this screen was closed and reopened in between.
  if (command) {
    return (
      <ConfirmationOutcome command={command} onDone={onDone} />
    );
  }

  if (!detail) {
    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <p className="m-0 text-[var(--color-danger)]">
          {errorCode === 'offline'
            ? t('queue.noCachedTasks')
            : t([`errors.${errorCode ?? 'unknown'}`, 'errors.unknown'])}
        </p>
        <Button onClick={onDone}>{t('common.cancel')}</Button>
      </div>
    );
  }

  const task = detail.task;
  if (task.kind === 'BinCount') return <BlindBinCount key={task.binCountClaimId} detail={detail} ownerUserId={ownerUserId} staleSince={staleSince} onClaim={() => void claim()} onChanged={setDetail} onDone={onDone} />;
  const suggestion = task.suggestedLocationCode;
  const scanned = confirmed.trim().toUpperCase();
  const isPick = task.kind === 'Pick';
  const isCount = task.kind === 'CycleCount';
  const isReplenishment = task.kind === 'Replenishment';
  const locationMatches = completionLocationAllowed(task.kind, scanned, task.fromLocationCode, suggestion);

  // A count has nothing to override — the operator is not choosing a destination,
  // and the bay is fixed. Their disagreement with the system is the variance.
  const overriding = !isCount && !isReplenishment && scanned.length > 0 && suggestion !== null && scanned !== suggestion;

  const countedValue = counted.trim();
  const countIsValid = countedValue.length > 0 && Number.isFinite(Number(countedValue)) && Number(countedValue) >= 0;
  const canConfirm = locationMatches && (!isCount || countIsValid);

  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <div className="flex items-center gap-[var(--space-2)]">
        <Badge>{t(`work.kind.${task.kind}`)}</Badge>
        <Badge tone={task.status === 'Assigned' ? 'info' : 'neutral'}>
          {t(`work.status.${task.status}`)}
        </Badge>
      </div>

      {/* Shown from the device rather than the warehouse, and said so. The claim
          may have been released while this device was away, and there is no way
          to find out until the confirmation is sent — which is precisely what
          the reconciliation screen exists to tell them. */}
      {staleSince ? (
        <Badge tone="warning" className="self-start">
          {t('queue.cachedAt', { time: formatDateTime(staleSince, locale) })}
        </Badge>
      ) : null}

      {/* What to move. The largest thing on the screen, because an operator
          glances at this from arm's length with a pallet in the way. */}
      <div className="flex flex-col gap-[var(--space-1)]">
        <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('work.item')}
        </span>
        <p className="m-0 text-[length:var(--text-xl)]">
          <Code className="text-[length:var(--text-xl)]">{task.sku}</Code>
        </p>
        <p className="m-0 text-[var(--color-text-secondary)]">{task.description}</p>
        {/* The expected quantity is the instruction on every other job and the
            answer on this one, so a count hides it behind a button rather than
            printing it above the box the operator is about to type into. */}
        {isCount ? (
          revealBook ? (
            <p className="m-0 text-[var(--color-text-secondary)]">
              {t('work.bookSays', { quantity: formatNumber(task.quantity, locale) })}
            </p>
          ) : (
            <Button
              variant="ghost"
              size="sm"
              className="self-start"
              onClick={() => setRevealBook(true)}
            >
              {t('work.revealBook')}
            </Button>
          )
        ) : (
          <p className="m-0 flex items-baseline gap-[var(--space-2)]">
            <Numeric
              value={task.quantity}
              maximumFractionDigits={4}
              locale={locale}
              className="text-[length:var(--text-xl)]"
            />
            <span className="text-[var(--color-text-secondary)]">{task.baseUom}</span>
          </p>
        )}
        {task.lotNumber ? (
          <p className="m-0 text-[var(--color-text-secondary)]">
            <Code>{task.lotNumber}</Code>
          </p>
        ) : null}
        {task.sscc ? (
          <p className="m-0 text-[var(--color-text-secondary)]">
            {t('work.pallet')} <Code>{task.sscc}</Code>
          </p>
        ) : null}
      </div>

      <div className="flex flex-col gap-[var(--space-1)]">
        <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {/* On a pick this is the instruction rather than background: the bin
              the operator has to walk to is the first half of the job. On a count
              it is the whole of it. */}
          {isCount ? t('work.countAt') : isPick ? t('work.pickFrom') : t('work.from')}
        </span>
        <Code
          className={
            isCount ? 'text-[length:var(--text-2xl)]' : 'text-[length:var(--text-base)]'
          }
        >
          {task.fromLocationCode}
        </Code>
      </div>

      {(isPick || isReplenishment) && task.sourceReference ? (
        <div className="flex flex-col gap-[var(--space-1)]">
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {isReplenishment ? t('replenishment.plan') : t('work.forOrder')}
          </span>
          <Code>{task.sourceReference}</Code>
        </div>
      ) : null}

      {/* A count has no destination panel: the bay above is where it happens and
          where it ends. The "why?" button still belongs, because the policy that
          sent somebody here is exactly as answerable as the rules that place a
          pallet. */}
      {isCount ? (
        <div
          className="flex flex-col gap-[var(--space-3)] rounded-[var(--radius-surface)]
                     border border-[var(--color-border-default)] bg-[var(--color-surface-raised)]
                     p-[var(--space-4)]"
        >
          <Field label={t('work.countedQuantity')} hint={t('work.countedHint')} hintMode="instruction" htmlFor="counted-quantity">
            <Input
              id="counted-quantity"
              inputMode="decimal"
              autoComplete="off"
              value={counted}
              onChange={(event) => setCounted(event.target.value)}
              className="text-[length:var(--text-2xl)]"
            />
          </Field>

          {detail.decision ? (
            <Button
              variant="ghost"
              size="sm"
              className="self-start"
              onClick={() => setShowWhy((open) => !open)}
            >
              {showWhy ? t('work.hideWhy') : t('work.whyHere')}
            </Button>
          ) : null}
        </div>
      ) : (
        <div
          className="flex flex-col gap-[var(--space-2)] rounded-[var(--radius-surface)]
                     border border-[var(--color-border-default)] bg-[var(--color-surface-raised)]
                     p-[var(--space-4)]"
        >
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {isReplenishment ? t('replenishment.face') : isPick ? t('work.takeTo') : t('work.putAwayAt')}
          </span>

          {suggestion ? (
            <p className="m-0 text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
              <Code className="text-[length:var(--text-2xl)]">{suggestion}</Code>
            </p>
          ) : (
            <>
              <Badge tone="warning" className="self-start">
                {t('work.noSuggestion')}
              </Badge>
              <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
                {t('work.noSuggestionHint')}
              </p>
            </>
          )}

          {detail.decision ? (
            <Button
              variant="ghost"
              size="sm"
              className="self-start"
              onClick={() => setShowWhy((open) => !open)}
            >
              {showWhy ? t('work.hideWhy') : t('work.whyHere')}
            </Button>
          ) : null}
        </div>
      )}

      {showWhy && detail.decision ? <WhyHere trace={detail.decision} /> : null}

      {/* Scanned on a count too, and for the reason the scan exists at all: a
          button confirms the operator read the screen, a scan confirms they are
          standing at the bay they are about to report a number for. */}
      <ScanField
        label={isCount ? t('work.scanCounted') : isPick ? t('work.scanDrop') : t('work.scanLocation')}
        placeholder={(isCount ? task.fromLocationCode : suggestion) ?? 'WH1-STOR-AMB-A01-01-01-A'}
        clearOnScan={false}
        onScan={(value) => setConfirmed(value.toUpperCase())}
      />

      {scanned ? (
        <p className="m-0">
          <Code className="text-[length:var(--text-base)]">{scanned}</Code>
        </p>
      ) : null}

      {scanned && !locationMatches ? <p role="alert" className="text-[var(--color-danger-text)]">
        {t(`errors.${isReplenishment ? 'work.replenishment_destination_mismatch' : 'work.count_location_mismatch'}`)}
      </p> : null}

      {overriding ? (
        <div className="flex flex-col gap-[var(--space-2)]">
          <Badge tone="warning" className="self-start">
            {t('work.overriding')}
          </Badge>
          {/* Optional on purpose. A mandatory free-text box on a scanner
              collects the letter x, and a reason nobody wrote is more honest
              than one nobody meant. */}
          <Field
            label={`${t('work.overrideReason')} · ${t('inbound.receive.optional')}`}
            htmlFor="override-reason"
          >
            <Input
              id="override-reason"
              value={overrideReason}
              onChange={(event) => setOverrideReason(event.target.value)}
            />
          </Field>
        </div>
      ) : null}

      {errorCode ? (
        <p className="m-0 text-[var(--color-danger)]">
          {errorCode === 'offline'
            ? t('queue.offlineNoNewWork')
            : t([`errors.${errorCode}`, 'errors.unknown'])}
        </p>
      ) : null}

      {task.status === 'Ready' ? (
        <Button onClick={() => void claim()}>{t('work.claim')}</Button>
      ) : null}

      <Button
        variant="primary"
        disabled={!canConfirm || submitting}
        onClick={() => void complete()}
      >
        {submitting ? <Spinner label={t('work.confirming')} /> : t('work.confirm')}
      </Button>

      <Button variant="ghost" onClick={onDone}>
        {t('common.cancel')}
      </Button>
    </div>
  );
}

/**
 * The three endings.
 *
 * Rendered from the queued command rather than from a response, so it survives a
 * reload and reads the same whether the confirmation went in a millisecond ago
 * or has been sitting on the device since the last aisle. The distinction the
 * operator has to be able to make at a glance is between "the warehouse has
 * this" and "this device has this", and it is carried by tone, badge and words
 * all three — colour is never the only signal.
 */
function ConfirmationOutcome({
  command,
  onDone,
}: {
  command: QueuedCommand;
  onDone: () => void;
}) {
  const { t } = useTranslation();
  const locale = currentLocale();

  if (command.state === 'settled') {
    const response = command.result as CompleteResponse | undefined;
    const outcome: CountOutcome | null = response?.count ?? null;
    const at = response?.task.completedAtLocationCode ?? command.summary.atLocationCode ?? '';

    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <Badge tone="success" className="self-start">
          {t('work.status.Completed')}
        </Badge>
        <p className="m-0 text-[length:var(--text-lg)]">{t('work.doneAt', { location: at })}</p>

        {/* A count's result is the sentence, because there is often no movement
            to point at. "You and the system agree" is the whole answer, and it
            is the answer most of the time. */}
        {outcome ? (
          <p className="m-0 text-[length:var(--text-lg)]">
            {outcome.variance === 0
              ? t('work.countAgrees', { quantity: formatNumber(outcome.countedQuantity, locale) })
              : t(outcome.variance > 0 ? 'work.countOver' : 'work.countShort', {
                  variance: formatNumber(Math.abs(outcome.variance), locale),
                })}
          </p>
        ) : null}

        {response?.task.wasOverridden ? (
          <Badge tone="warning" className="self-start">
            {t('work.wasOverridden')}
          </Badge>
        ) : null}

        <Button variant="primary" onClick={onDone}>
          {t('work.nextJob')}
        </Button>
      </div>
    );
  }

  if (command.state === 'refused' || command.state === 'stalled') {
    const category = command.state === 'stalled' ? 'stalled' : categorise(command.refusal?.code);
    const location = command.summary.atLocationCode ?? command.summary.fromLocationCode ?? '';

    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <Badge tone="danger" className="self-start">
          {t('queue.needsYouHeading')}
        </Badge>

        <div className="flex flex-col gap-[var(--space-1)]">
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('queue.said')}
          </span>
          <p className="m-0 text-[length:var(--text-lg)]">{t(`queue.cause.${category}`)}</p>
        </div>

        <div className="flex flex-col gap-[var(--space-1)]">
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('queue.doNow')}
          </span>
          <p
            className={
              needsSupervisor(category)
                ? 'm-0 text-[length:var(--text-lg)] text-[var(--color-danger)]'
                : 'm-0 text-[length:var(--text-lg)]'
            }
          >
            {t(`queue.advice.${category}`, { location })}
          </p>
        </div>

        {command.refusal ? (
          <p className="m-0 text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {t('queue.code')}{' '}
            <Code className="text-[length:var(--text-xs)]">{command.refusal.code}</Code>
          </p>
        ) : null}

        {/* A stalled confirmation has not been answered, only given up on, so
            the operator is offered the retry rather than only the button that
            throws the record away. See `Outbox.retry`. */}
        {command.state === 'stalled' ? (
          <Button variant="primary" onClick={() => void outbox.retry(command.id)}>
            {t('queue.tryAgain')}
          </Button>
        ) : null}

        <Button
          variant={command.state === 'stalled' ? 'secondary' : 'primary'}
          onClick={() => {
            void outbox.acknowledge(command.id);
            onDone();
          }}
        >
          {t('queue.acknowledge')}
        </Button>
      </div>
    );
  }

  // Saved, not sent. Never dressed as success.
  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <Badge tone="warning" className="self-start">
        {t('queue.queuedTitle')}
      </Badge>
      <p className="m-0 text-[length:var(--text-lg)]">
        {t('work.doneAt', { location: command.summary.atLocationCode ?? '' })}
      </p>
      <p className="m-0 text-[var(--color-text-secondary)]">{t('queue.queuedBlurb')}</p>

      <Button variant="primary" onClick={onDone}>
        {t('work.nextJob')}
      </Button>
    </div>
  );
}
