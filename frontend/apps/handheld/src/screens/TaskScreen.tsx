import type { components } from '@countflow/api-client';
import { currentLocale, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, Input, Numeric, ScanField, Spinner } from '@countflow/ui';
import { useCallback, useEffect, useRef, useState } from 'react';
import { api } from '../api.js';
import { WhyHere } from '../components/WhyHere.js';
import { newIdempotencyKey } from '../lib/idempotency.js';

type TaskDetail = components['schemas']['WorkTaskDetailResponse'];

interface TaskScreenProps {
  taskId: string;
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
 */
export function TaskScreen({ taskId, onDone }: TaskScreenProps) {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [detail, setDetail] = useState<TaskDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [showWhy, setShowWhy] = useState(false);
  const [confirmed, setConfirmed] = useState('');
  const [overrideReason, setOverrideReason] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [completed, setCompleted] = useState<string | null>(null);

  const idempotencyKey = useRef(newIdempotencyKey());

  const load = useCallback(async () => {
    setLoading(true);

    const { data, error } = await api.GET('/api/work/tasks/{id}', {
      params: { path: { id: taskId } },
    });

    setLoading(false);

    if (error || !data) {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
      return;
    }

    setDetail(data);
  }, [taskId]);

  useEffect(() => {
    void load();
  }, [load]);

  // Claiming is separate from opening, so a supervisor can look at a job on the
  // console without taking it out of somebody's queue.
  const claim = useCallback(async () => {
    const { data } = await api.POST('/api/work/tasks/{id}/claim', {
      params: { path: { id: taskId } },
    });

    if (data) setDetail(data);
  }, [taskId]);

  async function complete() {
    setSubmitting(true);
    setErrorCode(null);

    const { data, error } = await api.POST('/api/work/tasks/{id}/complete', {
      params: {
        path: { id: taskId },
        header: { 'Idempotency-Key': idempotencyKey.current },
      },
      body: {
        toLocationCode: confirmed.trim(),
        ...(overrideReason.trim() ? { overrideReason: overrideReason.trim() } : {}),
      },
    });

    setSubmitting(false);

    if (error || !data) {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
      return;
    }

    setCompleted(data.task.completedAtLocationCode ?? confirmed.trim());
  }

  if (loading && !detail) {
    return <Spinner label={t('common.loading')} />;
  }

  if (!detail) {
    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <p className="m-0 text-[var(--color-danger)]">
          {t([`errors.${errorCode ?? 'unknown'}`, 'errors.unknown'])}
        </p>
        <Button onClick={onDone}>{t('common.cancel')}</Button>
      </div>
    );
  }

  const task = detail.task;

  if (completed) {
    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <Badge tone="success" className="self-start">
          {t('work.status.Completed')}
        </Badge>
        <p className="m-0 text-[length:var(--text-lg)]">
          {t('work.doneAt', { location: completed })}
        </p>
        {task.wasOverridden ? (
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

  const suggestion = task.suggestedLocationCode;
  const scanned = confirmed.trim().toUpperCase();
  const overriding = scanned.length > 0 && suggestion !== null && scanned !== suggestion;
  const isPick = task.kind === 'Pick';

  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <div className="flex items-center gap-[var(--space-2)]">
        <Badge>{t(`work.kind.${task.kind}`)}</Badge>
        <Badge tone={task.status === 'Assigned' ? 'info' : 'neutral'}>
          {t(`work.status.${task.status}`)}
        </Badge>
      </div>

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
        <p className="m-0 flex items-baseline gap-[var(--space-2)]">
          <Numeric
            value={task.quantity}
            locale={locale}
            className="text-[length:var(--text-xl)]"
          />
          <span className="text-[var(--color-text-secondary)]">{task.baseUom}</span>
        </p>
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
              the operator has to walk to is the first half of the job. */}
          {isPick ? t('work.pickFrom') : t('work.from')}
        </span>
        <Code className="text-[length:var(--text-base)]">{task.fromLocationCode}</Code>
      </div>

      {isPick && task.sourceReference ? (
        <div className="flex flex-col gap-[var(--space-1)]">
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('work.forOrder')}
          </span>
          <Code>{task.sourceReference}</Code>
        </div>
      ) : null}

      <div
        className="flex flex-col gap-[var(--space-2)] rounded-[var(--radius-surface)]
                   border border-[var(--color-border-default)] bg-[var(--color-surface-raised)]
                   p-[var(--space-4)]"
      >
        <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {isPick ? t('work.takeTo') : t('work.putAwayAt')}
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

      {showWhy && detail.decision ? <WhyHere trace={detail.decision} /> : null}

      <ScanField
        label={isPick ? t('work.scanDrop') : t('work.scanLocation')}
        placeholder={suggestion ?? 'WH1-STOR-AMB-A01-01-01-A'}
        clearOnScan={false}
        onScan={(value) => setConfirmed(value.toUpperCase())}
      />

      {scanned ? (
        <p className="m-0">
          <Code className="text-[length:var(--text-base)]">{scanned}</Code>
        </p>
      ) : null}

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
          {t([`errors.${errorCode}`, 'errors.unknown'])}
        </p>
      ) : null}

      {task.status === 'Ready' ? (
        <Button onClick={() => void claim()}>{t('work.claim')}</Button>
      ) : null}

      <Button
        variant="primary"
        disabled={scanned.length === 0 || submitting}
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
