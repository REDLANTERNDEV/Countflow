import { translateCount, currentLocale, formatDateTime, formatNumber, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Numeric } from '@countflow/ui';
import { categorise, needsSupervisor, type RefusalCategory } from '../lib/outbox/refusals.js';
import type { QueuedCommand } from '../lib/outbox/types.js';
import { durable, outbox } from '../outbox.js';
import { useOutbox } from '../useOutbox.js';

interface QueueScreenProps {
  ownerUserId: string;
  onBack: () => void;
}

/**
 * What this device is holding, and what the warehouse did about it.
 *
 * The reconciliation screen, and the actual subject of the offline queue.
 * Replaying commands is a loop; being told that the pallet you carried to
 * A01-01-02-A was booked away by somebody else forty minutes ago is not, and it
 * is the only part an operator experiences.
 *
 * Three sentences per refusal, in this order and never fewer:
 *
 * 1. **What you did** — reconstructed from the device, because if the server
 *    refused the command there is nothing on the server to look it up from.
 * 2. **What the warehouse says** — grouped by what kind of news it is rather
 *    than by error code, because `work.task_not_yours` and
 *    `work.task_already_completed` mean the same thing to somebody in an aisle.
 * 3. **What to do now** — the part that makes it a reconciliation screen rather
 *    than an error log. "Count A01-01-02-A and tell your supervisor" is an
 *    instruction; "inventory.insufficient_stock" is a shrug.
 *
 * Nothing here is dismissed automatically and nothing pending can be deleted at
 * all. A queue that quietly drops refusals is worse than no queue: it converts a
 * discrepancy somebody could have walked back and fixed into stock that is
 * simply wrong, with no record that anyone ever knew.
 */
export function QueueScreen({ ownerUserId, onBack }: QueueScreenProps) {
  const { t } = useTranslation();
  const { commands, draining } = useOutbox();

  const mine = commands.filter((c) => c.ownerUserId === ownerUserId);
  const theirs = commands.filter((c) => c.ownerUserId !== ownerUserId);

  const attention = mine.filter((c) => c.state === 'refused' || c.state === 'stalled');
  const waiting = mine.filter((c) => c.state === 'pending' || c.state === 'sending');
  const sent = mine.filter((c) => c.state === 'settled').slice().reverse();

  const byOperator = new Map<string, QueuedCommand[]>();
  for (const command of theirs) {
    const bucket = byOperator.get(command.ownerOperatorCode) ?? [];
    bucket.push(command);
    byOperator.set(command.ownerOperatorCode, bucket);
  }

  const nothing = commands.length === 0;

  return (
    <div className="flex flex-col gap-[var(--space-4)]">
      <h1 className="m-0 text-[length:var(--text-2xl)] tracking-[var(--tracking-display)]">
        {t('queue.title')}
      </h1>

      {!durable ? (
        <Badge tone="warning" className="self-start">
          {t('queue.notDurable')}
        </Badge>
      ) : null}

      {nothing ? (
        <p className="m-0 text-[var(--color-text-secondary)]">{t('queue.empty')}</p>
      ) : null}

      {attention.length > 0 ? (
        <Section heading={t('queue.needsYouHeading')} blurb={t('queue.needsYouBlurb')} tone="danger">
          {attention.map((command) => (
            <RefusedCard key={command.id} command={command} />
          ))}
        </Section>
      ) : null}

      {waiting.length > 0 ? (
        <Section heading={t('queue.waitingHeading')} blurb={t('queue.waitingBlurb')} tone="warning">
          {waiting.map((command) => (
            <CommandCard key={command.id} command={command}>
              {command.state === 'sending' ? (
                <Badge tone="info" className="self-start">
                  {t('queue.sending')}
                </Badge>
              ) : null}
              {command.attempts > 0 ? (
                <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
                  {translateCount(t, 'queue.attempts', command.attempts)}
                </p>
              ) : null}
            </CommandCard>
          ))}

          <Button className="w-full" disabled={draining} onClick={() => void outbox.drain()}>
            {draining ? t('queue.sending') : t('queue.sendNow')}
          </Button>
        </Section>
      ) : null}

      {[...byOperator].map(([operator, theirCommands]) => (
        <Section
          key={operator}
          heading={t('queue.strandedHeading', { operator })}
          blurb={t('queue.strandedBlurb')}
          tone="neutral"
        >
          {theirCommands.map((command) => (
            <CommandCard key={command.id} command={command} />
          ))}
        </Section>
      ))}

      {sent.length > 0 ? (
        <Section heading={t('queue.sentHeading')} blurb={t('queue.sentBlurb')} tone="success">
          {sent.map((command) => (
            <CommandCard key={command.id} command={command} />
          ))}
        </Section>
      ) : null}

      <Button variant="ghost" className="mt-auto w-full" onClick={onBack}>
        {t('common.cancel')}
      </Button>
    </div>
  );
}

function Section(props: {
  heading: string;
  blurb: string;
  tone: 'danger' | 'warning' | 'success' | 'neutral';
  children: React.ReactNode;
}) {
  return (
    <section className="flex flex-col gap-[var(--space-2)]">
      <div className="flex items-baseline gap-[var(--space-2)]">
        <Badge tone={props.tone}>{props.heading}</Badge>
      </div>
      <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {props.blurb}
      </p>
      {props.children}
    </section>
  );
}

/**
 * What the operator did, from the device's own record.
 *
 * Everything on this card was written when the scan happened. None of it is
 * fetched, because the case that matters most is the one where there is nothing
 * to fetch it from.
 */
function CommandCard({
  command,
  children,
}: {
  command: QueuedCommand;
  children?: React.ReactNode;
}) {
  const { t } = useTranslation();
  const locale = currentLocale();
  const summary = command.summary;

  return (
    <div
      className="flex flex-col gap-[var(--space-2)] rounded-[var(--radius-surface)]
                 border border-[var(--color-border-default)] bg-[var(--color-surface-raised)]
                 p-[var(--space-4)]"
    >
      <div className="flex items-center gap-[var(--space-2)]">
        <Badge>{t(summary.headline)}</Badge>
        <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
          {t('queue.scannedAt', { time: formatDateTime(command.createdAt, locale) })}
        </span>
      </div>

      {summary.observed === undefined ? <p className="m-0 flex flex-wrap items-baseline gap-[var(--space-2)] text-[length:var(--text-lg)]">
        <Numeric value={summary.quantity} locale={locale} />
        <span className="text-[var(--color-text-secondary)]">×</span>
        <Code className="text-[length:var(--text-lg)]">{summary.sku}</Code>
      </p> : summary.observed.length ? summary.observed.map((line, index) => <div key={index} className="flex flex-col gap-1">
        <p className="m-0 flex flex-wrap gap-2"><Code>{line.sku}</Code><Numeric value={line.quantity} locale={locale} maximumFractionDigits={4} />{line.uom ? <span>{line.uom}</span> : null}
          {line.lotNumber ? <Code>{line.lotNumber}</Code> : null}{line.sscc ? <Code className="break-all">{line.sscc}</Code> : null}<Badge>{t(`stock.status_${line.condition}`)}</Badge></p>
        {line.description ? <p className="m-0">{line.description}</p> : null}
        {line.warnings?.map(warning => <p key={warning.code} role="alert" className="m-0 text-[var(--color-warning-text)]">{t(`scan.warning.${warning.code}`, warning.args)}</p>)}
      </div>) : <p className="m-0">{t('work.bin.empty')}</p>}
      {summary.observed === undefined ? <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
        {summary.description}
      </p> : null}

      {summary.fromLocationCode && summary.observed === undefined ? (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('queue.from', { location: summary.fromLocationCode })}
        </p>
      ) : null}

      {summary.atLocationCode ? (
        <p className="m-0">
          <Code>{summary.atLocationCode}</Code>
        </p>
      ) : null}

      {summary.countedQuantity !== undefined ? (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          {t('queue.counted', { quantity: formatNumber(summary.countedQuantity, locale) })}
        </p>
      ) : null}

      {summary.reference ? (
        <p className="m-0 text-[length:var(--text-sm)] text-[var(--color-text-secondary)]">
          <Code>{summary.reference}</Code>
        </p>
      ) : null}

      {children}
    </div>
  );
}

/**
 * A refusal, and what it means for the goods.
 *
 * The advice line names a location wherever one applies, because "count the bay"
 * is only actionable if the operator is told which. Which bay depends on the
 * news: stock that was not there is a question about where it was taken *from*,
 * while a job somebody else confirmed is a question about where it ended up.
 */
function RefusedCard({ command }: { command: QueuedCommand }) {
  const { t } = useTranslation();

  const category: RefusalCategory =
    command.state === 'stalled' ? 'stalled' : categorise(command.refusal?.code);

  const location =
    category === 'stock_missing'
      ? command.summary.fromLocationCode ?? command.summary.atLocationCode
      : command.summary.atLocationCode ?? command.summary.fromLocationCode;

  return (
    <CommandCard command={command}>
      <div className="flex flex-col gap-[var(--space-1)] border-t border-[var(--color-border-subtle)] pt-[var(--space-3)]">
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
          {t(`queue.advice.${category}`, { location: location ?? '' })}
        </p>
      </div>

      {command.refusal ? (
        <p className="m-0 text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
          {t('queue.code')} <Code className="text-[length:var(--text-xs)]">{command.refusal.code}</Code>
        </p>
      ) : null}

      {/* A stall is the automatic retry giving up, not the command being
          answered — so the operator gets the retry the automatic one stopped
          doing. Without it the only button here would be the one that deletes
          a movement the warehouse never heard about. */}
      {command.state === 'stalled' ? (
        <Button variant="primary" className="w-full" onClick={() => void outbox.retry(command.id)}>
          {t('queue.tryAgain')}
        </Button>
      ) : null}

      {/* The only thing on this screen that removes a record, and it is only ever
          offered for a command the warehouse has finished with. A pending one has
          no button at all: it is the device's record of a pallet somebody moved,
          and an operator who can make it disappear can lose stock silently. */}
      <Button className="w-full" onClick={() => void outbox.acknowledge(command.id)}>
        {t('queue.acknowledge')}
      </Button>
    </CommandCard>
  );
}
