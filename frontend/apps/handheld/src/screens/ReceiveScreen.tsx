import type { components } from '@countflow/api-client';
import { currentLocale, formatNumber, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, Input, Numeric, ScanField, Spinner } from '@countflow/ui';
import { useRef, useState } from 'react';
import { api } from '../api.js';
import { newIdempotencyKey } from '../lib/idempotency.js';

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
 */
export function ReceiveScreen({ onBack, onOpenTask }: ReceiveScreenProps) {
  const { t } = useTranslation();
  const locale = currentLocale();

  const [sku, setSku] = useState('');
  const [quantity, setQuantity] = useState('');
  const [dock, setDock] = useState('');
  const [sscc, setSscc] = useState('');
  const [asnReference, setAsnReference] = useState('');

  const [submitting, setSubmitting] = useState(false);
  const [errorCode, setErrorCode] = useState<string | null>(null);
  const [receipt, setReceipt] = useState<Receipt | null>(null);

  // Held across retries so a request that timed out but committed is recognised
  // as the same command rather than posted a second time.
  const idempotencyKey = useRef(newIdempotencyKey());

  const parsedQuantity = Number(quantity);
  const ready =
    sku.trim().length > 0 && dock.trim().length > 0 && Number.isFinite(parsedQuantity) && parsedQuantity > 0;

  async function book() {
    setSubmitting(true);
    setErrorCode(null);

    const { data, error } = await api.POST('/api/v1/inbound/receipts', {
      params: { header: { 'Idempotency-Key': idempotencyKey.current } },
      body: {
        sku: sku.trim(),
        quantity: parsedQuantity,
        toLocationCode: dock.trim(),
        ...(sscc.trim() ? { sscc: sscc.trim() } : {}),
        ...(asnReference.trim() ? { asnReference: asnReference.trim() } : {}),
      },
    });

    setSubmitting(false);

    if (error || !data) {
      setErrorCode((error as { code?: string } | undefined)?.code ?? 'unknown');
      return;
    }

    setReceipt(data);
  }

  function receiveAnother() {
    // A fresh key, because this is a genuinely new command. Reusing the previous
    // one would make the second pallet look like a retry of the first and post
    // nothing at all.
    idempotencyKey.current = newIdempotencyKey();
    setReceipt(null);
    setSku('');
    setQuantity('');
    setSscc('');
    setErrorCode(null);
  }

  if (receipt) {
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

          {receipt.suggestedLocationCode ? (
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

        <Button onClick={receiveAnother}>{t('inbound.receive.receiveAnother')}</Button>
        <Button variant="ghost" onClick={onBack}>
          {t('common.cancel')}
        </Button>
      </div>
    );
  }

  return (
    <div className="flex flex-col gap-[var(--space-4)]">
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
      >
        <Input
          id="receive-sscc"
          inputMode="numeric"
          value={sscc}
          onChange={(event) => setSscc(event.target.value)}
        />
      </Field>
      <p className="m-0 -mt-[var(--space-2)] text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
        {t('inbound.receive.palletHint')}
      </p>

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

      {errorCode ? (
        <p className="m-0 text-[var(--color-danger)]">
          {t([`errors.${errorCode}`, 'errors.unknown'])}
        </p>
      ) : null}

      <Button variant="primary" disabled={!ready || submitting} onClick={() => void book()}>
        {submitting ? <Spinner label={t('inbound.receive.booking')} /> : t('inbound.receive.book')}
      </Button>

      <Button variant="ghost" onClick={onBack}>
        {t('common.cancel')}
      </Button>
    </div>
  );
}
