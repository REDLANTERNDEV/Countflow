import type { components } from '@countflow/api-client';
import { currentLocale, formatNumber, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, InfoHelp, Input, Numeric, ScanField, Spinner } from '@countflow/ui';
import { useRef, useState } from 'react';
import { newIdempotencyKey } from '../lib/idempotency.js';
import { categorise, needsSupervisor } from '../lib/outbox/refusals.js';
import type { QueuedCommand } from '../lib/outbox/types.js';
import { outbox } from '../outbox.js';
import { useOutbox } from '../useOutbox.js';

type Receipt = components['schemas']['DockReceiptResponse'];

interface ReceiveScreenProps {
  onBack: () => void;
  onOpenTask: (taskId: string) => void;
}

/**
 * Booking goods off a truck.
 *
 * The scan comes first and everything else is optional, because that is the
 * order the job happens in: the operator has a carton in one hand and a scanner
 * in the other, and anything that makes them tap a field before they can scan is
 * a field they will work around.
 *
 * <b>A receipt can be queued, and a putaway suggestion cannot.</b> The two halves
 * of this endpoint are different kinds of thing: "these goods came off a truck
 * onto this dock" is an assertion of fact the operator is entitled to make with
 * no network, while "and they should go to A01-01-02-A" is a decision the rules
 * engine makes over a warehouse this device cannot see. So booking in offline
 * works and answers nothing — the suggestion arrives when the receipt does, on
 * the queue, for whoever presses "next job". That is a genuine loss of
 * immediacy and it is the honest one; a device that guessed a location would be
 * inventing a decision trace, which is the one thing ADR 0004 forbids outright.
 *
 * It is also where the *other* half of the reconciliation screen earns its keep.
 * A mistyped SKU is refused instantly when there is a signal and forty minutes
 * later when there is not — and forty minutes later, the only thing in the world
 * that remembers what the operator typed is this device.
 */
export function ReceiveScreen({ onBack, onOpenTask }: ReceiveScreenProps) {
  const { t } = useTranslation();
  const { commands } = useOutbox();

  const [sku, setSku] = useState('');
  const [quantity, setQuantity] = useState('');
  const [dock, setDock] = useState('');
  const [sscc, setSscc] = useState('');
  const [asnReference, setAsnReference] = useState('');

  const [submitting, setSubmitting] = useState(false);
  const [submittedId, setSubmittedId] = useState<string | null>(null);

  // Held across retries so a request that timed out but committed is recognised
  // as the same command rather than posted a second time. From the moment it is
  // submitted the key is the queued command's own identity, so it survives a
  // reload as a matter of storage layout rather than of care.
  const idempotencyKey = useRef(newIdempotencyKey());

  const command = submittedId ? commands.find((c) => c.id === submittedId) : undefined;

  const parsedQuantity = Number(quantity);
  const ready =
    sku.trim().length > 0 && dock.trim().length > 0 && Number.isFinite(parsedQuantity) && parsedQuantity > 0;

  async function book() {
    setSubmitting(true);

    const key = idempotencyKey.current;

    await outbox.submit({
      kind: 'inbound.receipt',
      key,
      request: {
        sku: sku.trim(),
        quantity: parsedQuantity,
        toLocationCode: dock.trim(),
        ...(sscc.trim() ? { sscc: sscc.trim() } : {}),
        ...(asnReference.trim() ? { asnReference: asnReference.trim() } : {}),
      },
      summary: {
        headline: 'queue.kind.receipt',
        sku: sku.trim(),
        description: '',
        quantity: parsedQuantity,
        uom: '',
        atLocationCode: dock.trim(),
        ...(sscc.trim() ? { sscc: sscc.trim() } : {}),
        ...(asnReference.trim() ? { reference: asnReference.trim() } : {}),
      },
    });

    setSubmittedId(key);
    setSubmitting(false);
  }

  function receiveAnother() {
    // A fresh key, because this is a genuinely new command. Reusing the previous
    // one would make the second pallet look like a retry of the first and post
    // nothing at all.
    idempotencyKey.current = newIdempotencyKey();
    setSubmittedId(null);
    setSku('');
    setQuantity('');
    setSscc('');
  }

  if (command) {
    return (
      <ReceiptOutcome
        command={command}
        onOpenTask={onOpenTask}
        onAgain={receiveAnother}
        onBack={onBack}
      />
    );
  }

  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <div className="flex items-center">
        <h1 className="m-0 text-[length:var(--text-xl)]">{t('inbound.receive.title')}</h1>
        <InfoHelp label={t('common.about', { topic: t('inbound.receive.title') })}>{t('inbound.subtitle')}</InfoHelp>
      </div>
      <ScanField
        label={t('inbound.receive.scanItem')}
        placeholder="SKU-00001"
        clearOnScan={false}
        onScan={setSku}
      />

      {sku ? (
        <p className="m-0">
          <Code>{sku}</Code>
        </p>
      ) : (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {t('inbound.receive.scanItemHint')}
        </p>
      )}

      <Field label={t('inbound.receive.quantity')} htmlFor="receive-quantity">
        <Input
          id="receive-quantity"
          inputMode="decimal"
          value={quantity}
          onChange={(event) => setQuantity(event.target.value)}
        />
      </Field>

      <Field label={t('inbound.receive.dock')} htmlFor="receive-dock">
        <Input
          id="receive-dock"
          value={dock}
          autoComplete="off"
          autoCapitalize="characters"
          onChange={(event) => setDock(event.target.value.toUpperCase())}
        />
      </Field>

      <Field
        label={`${t('inbound.receive.pallet')} · ${t('inbound.receive.optional')}`}
        htmlFor="receive-sscc"
        hint={t('inbound.receive.palletHint')}
        hintMode="instruction"
      >
        <Input
          id="receive-sscc"
          inputMode="numeric"
          value={sscc}
          onChange={(event) => setSscc(event.target.value)}
        />
      </Field>

      <Field
        label={`${t('inbound.receive.asnReference')} · ${t('inbound.receive.optional')}`}
        htmlFor="receive-asn"
      >
        <Input
          id="receive-asn"
          value={asnReference}
          autoComplete="off"
          autoCapitalize="characters"
          onChange={(event) => setAsnReference(event.target.value.toUpperCase())}
        />
      </Field>

      <Button variant="primary" disabled={!ready || submitting} onClick={() => void book()}>
        {submitting ? <Spinner label={t('inbound.receive.booking')} /> : t('inbound.receive.book')}
      </Button>

      <Button variant="ghost" onClick={onBack}>
        {t('common.cancel')}
      </Button>
    </div>
  );
}

/** Booked, refused, or held on the device — the same three endings a job has. */
function ReceiptOutcome({
  command,
  onOpenTask,
  onAgain,
  onBack,
}: {
  command: QueuedCommand;
  onOpenTask: (taskId: string) => void;
  onAgain: () => void;
  onBack: () => void;
}) {
  const { t } = useTranslation();
  const locale = currentLocale();

  const again = (
    <>
      <Button onClick={onAgain}>{t('inbound.receive.receiveAnother')}</Button>
      <Button variant="ghost" onClick={onBack}>
        {t('common.cancel')}
      </Button>
    </>
  );

  if (command.state === 'settled') {
    const receipt = command.result as Receipt;

    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <Badge tone="success" className="self-start">
          {t('inbound.receive.booked')}
        </Badge>

        {/* Quantity through Numeric and SKU through Code, because they are
            different kinds of thing: one is a value the locale formats, the
            other an identifier that must never be grouped. */}
        <p className="m-0 flex flex-wrap items-baseline gap-[var(--space-2)] text-[length:var(--text-lg)]">
          <Numeric value={receipt.receipt.quantity} locale={locale} />
          <span className="text-[var(--color-text-secondary)]">×</span>
          <Code className="text-[length:var(--text-lg)]">{receipt.sku}</Code>
          <span className="text-[var(--color-text-secondary)]">→</span>
          <Code className="text-[length:var(--text-lg)]">{receipt.toLocationCode}</Code>
        </p>

        {receipt.asnLine ? (
          <p className="m-0 text-[var(--color-text-secondary)]">
            {receipt.asnLine.outstandingQuantity > 0
              ? t('inbound.receive.shortBy', {
                  quantity: formatNumber(receipt.asnLine.outstandingQuantity, locale),
                  reference: receipt.asnLine.reference,
                })
              : t('inbound.receive.lineComplete', { reference: receipt.asnLine.reference })}
          </p>
        ) : null}

        <div
          className="flex flex-col gap-[var(--space-2)] rounded-[var(--radius-surface)]
                     border border-[var(--color-border-subtle)] bg-[var(--color-surface-raised)]
                     p-[var(--space-4)]"
        >
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
            {t('inbound.receive.taskRaised')}
          </span>

          {receipt.completionPending ? (
            <p role="status" className="m-0 text-[var(--color-warning)]">
              {t('errors.work.completion_pending')}
            </p>
          ) : receipt.putawayAdviceStale ? (
            <p role="status" className="m-0 text-[var(--color-warning)]">
              {t('errors.work.putaway_contents_changed')}
            </p>
          ) : receipt.suggestedLocationCode ? (
            <p className="m-0 text-[length:var(--text-xl)]">
              <Code>{receipt.suggestedLocationCode}</Code>
            </p>
          ) : (
            <Badge tone="warning" className="self-start">
              {t('work.noSuggestion')}
            </Badge>
          )}
        </div>

        {receipt.workTaskId ? (
          <Button variant="primary" onClick={() => onOpenTask(receipt.workTaskId!)}>
            {t('inbound.receive.openTask')}
          </Button>
        ) : null}

        {again}
      </div>
    );
  }

  if (command.state === 'refused' || command.state === 'stalled') {
    const category = command.state === 'stalled' ? 'stalled' : categorise(command.refusal?.code);

    return (
      <div className="flex flex-col gap-[var(--space-4)]">
        <Badge tone="danger" className="self-start">
          {t('queue.needsYouHeading')}
        </Badge>

        <p className="m-0 flex flex-wrap items-baseline gap-[var(--space-2)] text-[length:var(--text-lg)]">
          <Numeric value={command.summary.quantity} locale={locale} />
          <span className="text-[var(--color-text-secondary)]">×</span>
          <Code className="text-[length:var(--text-lg)]">{command.summary.sku}</Code>
        </p>

        <p className="m-0 text-[length:var(--text-lg)]">{t(`queue.cause.${category}`)}</p>
        <p
          className={
            needsSupervisor(category)
              ? 'm-0 text-[length:var(--text-lg)] text-[var(--color-danger)]'
              : 'm-0 text-[length:var(--text-lg)]'
          }
        >
          {t(`queue.advice.${category}`, { location: command.summary.atLocationCode ?? '' })}
        </p>

        {command.refusal ? (
          <p className="m-0 text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {t('queue.code')}{' '}
            <Code className="text-[length:var(--text-xs)]">{command.refusal.code}</Code>
          </p>
        ) : null}

        <Button
          variant="primary"
          onClick={() => {
            void outbox.acknowledge(command.id);
            onAgain();
          }}
        >
          {t('queue.acknowledge')}
        </Button>

        <Button variant="ghost" onClick={onBack}>
          {t('common.cancel')}
        </Button>
      </div>
    );
  }

  // Saved, not sent. There is no suggestion to show, because nothing has decided
  // one — and printing a location the device made up would be worse than
  // printing none.
  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <Badge tone="warning" className="self-start">
        {t('queue.queuedTitle')}
      </Badge>

      <p className="m-0 flex flex-wrap items-baseline gap-[var(--space-2)] text-[length:var(--text-lg)]">
        <Numeric value={command.summary.quantity} locale={locale} />
        <span className="text-[var(--color-text-secondary)]">×</span>
        <Code className="text-[length:var(--text-lg)]">{command.summary.sku}</Code>
        <span className="text-[var(--color-text-secondary)]">→</span>
        <Code className="text-[length:var(--text-lg)]">{command.summary.atLocationCode}</Code>
      </p>

      <p className="m-0 text-[var(--color-text-secondary)]">{t('queue.queuedBlurb')}</p>

      {again}
    </div>
  );
}
