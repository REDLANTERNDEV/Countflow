import type { components } from '@countflow/api-client';
import { currentLocale, formatDateTime, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, Field, InfoHelp, Input, Numeric, ScanField, Select } from '@countflow/ui';
import { useEffect, useRef, useState } from 'react';
import { api } from '../api.js';
import { addBinObservation, binCountReady, validBinQuantity, type ObservedHolding } from '../lib/bin-count.js';
import { newIdempotencyKey } from '../lib/idempotency.js';
import { outbox } from '../outbox.js';
import { rememberTask, type TaskDetail } from '../tasks.js';

const CONDITIONS = ['Available', 'Quarantine', 'Damaged', 'OnHold', 'Expired'] as const;
export function BlindBinCount({ detail, ownerUserId, staleSince, onClaim, onChanged, onDone }: {
  detail: TaskDetail; ownerUserId: string; staleSince: string | null; onClaim: () => void;
  onChanged: (value: TaskDetail) => void; onDone: () => void;
}) {
  const { t } = useTranslation(); const locale = currentLocale(); const task = detail.task;
  const draftKey = `countflow.bin-count.${ownerUserId}.${task.id}.${task.binCountClaimId ?? 'unclaimed'}`;
  const [lines, setLines] = useState<ObservedHolding[]>(() => {
    try { const value: unknown = JSON.parse(localStorage.getItem(draftKey) ?? '[]');
      return Array.isArray(value) ? value.filter(line => typeof line?.sku === 'string' && typeof line?.quantity === 'number' && CONDITIONS.includes(line.condition)) : [];
    } catch { return []; }
  });
  const [scanned, setScanned] = useState(''); const [sku, setSku] = useState(''); const [quantity, setQuantity] = useState('');
  const [lot, setLot] = useState(''); const [expiry, setExpiry] = useState(''); const [sscc, setSscc] = useState('');
  const [condition, setCondition] = useState<components['schemas']['StockCondition']>('Available');
  const [item, setItem] = useState<components['schemas']['ScanResolutionResponse']['item']>(null);
  const [warnings, setWarnings] = useState<components['schemas']['ScanWarning'][]>([]);
  const quantityInput = useRef<HTMLInputElement>(null);
  const [checked, setChecked] = useState(false); const [busy, setBusy] = useState(false); const [error, setError] = useState<string | null>(null);
  const key = useRef(newIdempotencyKey());
  useEffect(() => { try { localStorage.setItem(draftKey, JSON.stringify(lines)); } catch { setError('work.bin_draft_storage'); } }, [draftKey, lines]);
  async function scan(barcode: string) {
    setError(null); setWarnings([]); setBusy(true);
    try {
      const { data, error: problem } = await api.POST('/api/v1/scan/resolve', { body: { barcode } });
      if (!data?.item) { setError((problem as { code?: string })?.code ?? 'inventory.item_not_found'); return; }
      setSku(data.item.sku); setItem(data.item); setWarnings(data.warnings); setLot(data.lotNumber ?? ''); setExpiry(data.expiryDate ?? '');
      setSscc(data.sscc ?? '');
      quantityInput.current?.focus();
    } catch { setError('offline'); } finally { setBusy(false); }
  }
  function add() {
    if (!quantity.trim()) return;
    if (!validBinQuantity(Number(quantity))) { setError('inventory.bin_count_invalid'); return; }
    const next = addBinObservation(lines, { sku, quantity: Number(quantity), condition,
      ...(item ? { description: item.description, uom: item.baseUom } : {}), ...(warnings.length ? { warnings } : {}),
      ...(lot.trim() ? { lotNumber: lot.trim() } : {}), ...(expiry ? { expiryDate: expiry } : {}), ...(sscc.trim() ? { sscc: sscc.trim() } : {}) });
    if (!next) { setError('inventory.bin_count_duplicate'); return; }
    setLines(next); setSku(''); setItem(null); setWarnings([]); setQuantity(''); setLot(''); setExpiry(''); setSscc(''); setChecked(false); setError(null);
  }
  async function restart() {
    setBusy(true); setError(null);
    try {
      const { data, error: problem } = await api.POST('/api/v1/work/tasks/{id}/bin-count/start', {
        params: { path: { id: task.id } }, body: { restart: true } });
      if (!data) { setError((problem as { code?: string })?.code ?? 'unknown'); return; }
      localStorage.removeItem(draftKey); await rememberTask(data, ownerUserId); onChanged(data);
    } catch { setError('offline'); } finally { setBusy(false); }
  }
  async function complete() {
    if (!binCountReady(scanned, task.fromLocationCode, task.binCountVersion, task.binCountClaimId, lines, checked)) return;
    setBusy(true);
    try {
      await outbox.submit({ kind: 'work.complete', key: key.current, request: { taskId: task.id,
        toLocationCode: scanned.trim().toUpperCase(), binCountVersion: task.binCountVersion!, binCountClaimId: task.binCountClaimId!, observed: lines },
        summary: { headline: 'work.kind.BinCount', sku: '', description: task.fromLocationCode, quantity: 0, uom: '',
          fromLocationCode: task.fromLocationCode, atLocationCode: scanned.trim().toUpperCase(), observed: lines } });
      localStorage.removeItem(draftKey);
    } catch { setError('work.bin_draft_storage'); } finally { setBusy(false); }
  }
  const ready = task.status === 'Assigned' && binCountReady(scanned, task.fromLocationCode, task.binCountVersion, task.binCountClaimId, lines, checked);
  const binMatches = scanned.trim().toUpperCase() === task.fromLocationCode.toUpperCase();
  if (task.status !== 'Assigned') return <div className="flex flex-col gap-4"><h1>{t('work.kind.BinCount')}</h1><Code>{task.fromLocationCode}</Code>
    <Badge>{t(`work.status.${task.status}`)}</Badge>{task.status === 'Ready' ? <Button onClick={onClaim}>{t('work.claim')}</Button> : null}
    <Button onClick={onDone}>{t('common.cancel')}</Button></div>;
  return <div className="flex flex-col gap-[var(--space-4)]">
    <div className="flex items-center gap-2"><h1 className="m-0 text-[length:var(--text-xl)]">{t('work.kind.BinCount')}</h1>
      <InfoHelp label={t('common.help')}>{t('work.bin.help')}</InfoHelp></div>
    <Code className="text-[length:var(--text-xl)]">{task.fromLocationCode}</Code>
    {staleSince ? <Badge tone="warning">{t('queue.cachedAt', { time: formatDateTime(staleSince, locale) })}</Badge> : null}
    <ScanField label={t('work.scanLocation')} autoFocus={!binMatches} onScan={setScanned} />
    {scanned ? <Badge tone={scanned.trim().toUpperCase() === task.fromLocationCode.toUpperCase() ? 'success' : 'danger'}>{scanned}</Badge> : null}
    <ScanField label={t('work.bin.scanItem')} autoFocus={binMatches && !sku} disabled={!binMatches || busy} onScan={barcode => void scan(barcode)} />
    <Field label={t('stock.sku')} htmlFor="bin-sku"><Input id="bin-sku" value={sku} onChange={event => { setSku(event.target.value); setItem(null); setWarnings([]); }} /></Field>
    {item ? <p className="m-0">{item.description}</p> : null}
    {item ? <p className="m-0 text-[var(--color-text-secondary)]">{item.packagingLevel} · <Numeric value={item.baseUnitQuantity} locale={locale} /> {item.baseUom}</p> : null}
    {warnings.map(warning => <p key={warning.code} role="alert" className="m-0 text-[var(--color-warning-text)]">{t(`scan.warning.${warning.code}`, warning.args)}</p>)}
    <Field label={`${t('work.bin.quantity')}${item ? ` · ${item.baseUom}` : ''}`} htmlFor="bin-quantity"><Input id="bin-quantity" ref={quantityInput} inputMode="decimal" value={quantity} onChange={event => setQuantity(event.target.value)} /></Field>
    <details><summary>{t('work.bin.details')}</summary><div className="mt-2 flex flex-col gap-3">
      <Field label={t('scan.batch')} htmlFor="bin-lot"><Input id="bin-lot" value={lot} onChange={event => setLot(event.target.value)} /></Field>
      <Field label={t('scan.expiry')} htmlFor="bin-expiry"><Input id="bin-expiry" type="date" value={expiry} onChange={event => setExpiry(event.target.value)} /></Field>
      <Field label={t('scan.sscc')} htmlFor="bin-sscc"><Input id="bin-sscc" value={sscc} onChange={event => setSscc(event.target.value)} /></Field>
      <Field label={t('work.bin.condition')} htmlFor="bin-condition"><Select id="bin-condition" value={condition} onChange={event => setCondition(event.target.value as typeof condition)}>
        {CONDITIONS.map(value => <option key={value} value={value}>{t(`stock.status_${value}`)}</option>)}
      </Select></Field></div></details>
    <Button disabled={busy || !sku.trim() || !quantity.trim()} onClick={add}>{t('work.bin.add')}</Button>
    {lines.map((line, index) => <div key={index} className="flex flex-wrap items-center gap-2 border-b border-[var(--color-border-subtle)] py-2">
      <Code>{line.sku}</Code><Numeric value={line.quantity} locale={locale} maximumFractionDigits={4} />{line.uom ? <span>{line.uom}</span> : null}
      {line.description ? <span className="basis-full">{line.description}</span> : null}
      {line.lotNumber ? <Code>{line.lotNumber}</Code> : null}{line.sscc ? <Code className="break-all">{line.sscc}</Code> : null}
      <Badge>{t(`stock.status_${line.condition}`)}</Badge>
      {line.warnings?.map(warning => <p key={warning.code} role="alert" className="m-0 basis-full text-[var(--color-warning-text)]">{t(`scan.warning.${warning.code}`, warning.args)}</p>)}
      <Button size="sm" onClick={() => { setLines(lines.filter((_, at) => at !== index)); setChecked(false); }}>{t('work.bin.remove')}</Button>
    </div>)}
    <label className="flex items-start gap-3"><input type="checkbox" checked={checked} onChange={event => setChecked(event.target.checked)} />
      <span>{t(lines.length ? 'work.bin.checked' : 'work.bin.empty')}</span></label>
    <p className="m-0 text-[var(--color-warning-text)]">{t('work.bin.warning')}</p>
    {error ? <p role="alert" className="m-0 text-[var(--color-danger)]">{error === 'offline' ? t('queue.offlineNoNewWork') : t([`errors.${error}`, 'errors.unknown'])}</p> : null}
    <Button variant="primary" disabled={!ready || busy} onClick={() => void complete()}>{t('work.confirm')}</Button>
    <Button disabled={busy} onClick={() => void restart()}>{t('work.bin.restart')}</Button>
    <Button variant="ghost" onClick={onDone}>{t('common.cancel')}</Button>
  </div>;
}
