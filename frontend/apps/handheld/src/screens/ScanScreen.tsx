import type { components } from '@countflow/api-client';
import { currentLocale, formatDate, useTranslation } from '@countflow/i18n';
import { Badge, Button, Code, InfoHelp, Numeric, ScanField, Spinner } from '@countflow/ui';
import { useEffect, useRef, useState } from 'react';
import { api } from '../api.js';
import { CameraScanner } from '../components/CameraScanner.js';
import { unanswered } from '../lib/outbox/dispatch.js';

type Resolution = components['schemas']['ScanResolutionResponse'];
export function ScanScreen({ online, onBack }: { online: boolean; onBack: () => void }) {
  const { t } = useTranslation(); const locale = currentLocale();
  const [raw, setRaw] = useState(''); const [result, setResult] = useState<Resolution | null>(null);
  const [loading, setLoading] = useState(false); const [error, setError] = useState<string | null>(null);
  const request = useRef<AbortController | null>(null);
  const scanInput = useRef<HTMLInputElement>(null);
  useEffect(() => () => request.current?.abort(), []);
  async function resolve(barcode: string) {
    request.current?.abort(); const controller = new AbortController(); request.current = controller;
    setRaw(barcode); setResult(null); setError(null); setLoading(false);
    if (!online || !barcode.trim()) return;
    setLoading(true);
    try {
      const { data, error: problem, response } = await api.POST('/api/v1/scan/resolve', { body: { barcode }, signal: controller.signal });
      if (controller.signal.aborted) return;
      if (data) setResult(data);
      else setError(unanswered(response.status) ? 'scan.camera.connection' : (problem as { code?: string })?.code ?? 'unknown');
    } catch { if (!controller.signal.aborted) setError('scan.camera.connection'); }
    finally { if (!controller.signal.aborted) setLoading(false); }
  }
  const fact = (label: string, value: string | number | null | undefined) => value == null ? null : <div className="flex flex-wrap justify-between gap-2 border-b border-[var(--color-border-subtle)] py-2">
    <dt className="text-[var(--color-text-secondary)]">{label}</dt><dd className="m-0 break-all">{typeof value === 'number' ? <Numeric value={value} locale={locale} /> : <Code>{value}</Code>}</dd>
  </div>;
  return <div className="flex flex-col gap-[var(--space-4)]">
    <div className="flex items-center"><h1 className="m-0 text-[length:var(--text-xl)]">{t('scan.camera.scan')}</h1><InfoHelp label={t('common.about', { topic: t('scan.camera.scan') })}>{t('scan.camera.readOnly')}</InfoHelp></div>
    <ScanField inputRef={scanInput} label={t('scan.placeholder')} onScan={barcode => { void resolve(barcode); }} />
    <CameraScanner returnFocusRef={scanInput} onScan={barcode => { void resolve(barcode); }} />
    {raw ? <>
      <Code className="break-all">{raw.replaceAll('\u001d', '␝')}</Code>
      {!online ? <Badge tone="warning">{t('scan.camera.online')}</Badge> : null}
      <Button disabled={!online || loading} onClick={() => { void resolve(raw); }}>{loading ? <Spinner label={t('scan.decoding')} /> : t('scan.decode')}</Button>
    </> : null}
    {error ? <p role="alert" className="m-0 text-[var(--color-danger-text)]">{error === 'scan.camera.connection' ? t(error) : t([`errors.${error}`, 'errors.unknown'])}</p> : null}
    {result ? <>
      {result.item ? <div><Code>{result.item.sku}</Code><p className="m-0">{result.item.description}</p></div> : <Badge tone="warning">{t('scan.notRecognised')}</Badge>}
      <dl className="m-0">{fact('GTIN', result.gtin)}{fact(t('scan.packagingLevel'), result.item?.packagingLevel)}
        {fact(t('scan.unitsPerPack'), result.item?.baseUnitQuantity)}{fact(t('scan.scannedQuantity'), result.scannedQuantity)}{fact(t('scan.totalBaseUnits'), result.baseUnitQuantity)}
        {fact(t('scan.batch'), result.lotNumber)}{fact(t('scan.expiry'), result.expiryDate ? formatDate(result.expiryDate, locale) : null)}
        {fact(t('scan.serial'), result.serialNumber)}{fact(t('scan.sscc'), result.sscc)}</dl>
      {result.warnings.map(warning => <p key={warning.code} role="alert" className="m-0 text-[var(--color-warning-text)]">{t(`scan.warning.${warning.code}`, warning.args)}</p>)}
    </> : null}
    <Button className="mt-auto" onClick={onBack}>{t('scan.camera.back')}</Button>
  </div>;
}
