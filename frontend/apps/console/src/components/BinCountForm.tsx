import { useTranslation } from '@countflow/i18n';
import { Badge, Button, Field, InfoHelp, Input } from '@countflow/ui';
import { useRef, useState } from 'react';
import { api } from '../api.js';

export function BinCountForm() {
  const { t } = useTranslation(); const key = useRef(crypto.randomUUID());
  const [location, setLocation] = useState(''); const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false); const [raised, setRaised] = useState(false); const [error, setError] = useState<string | null>(null);
  async function raise() {
    setBusy(true); setRaised(false); setError(null);
    try {
      const { data, error: problem } = await api.POST('/api/v1/work/bin-counts', {
        params: { header: { 'Idempotency-Key': key.current } }, body: { locationCode: location.trim(), reason: reason.trim() } });
      if (!data) { setError((problem as { code?: string })?.code ?? 'unknown'); return; }
      setRaised(true); setLocation(''); setReason(''); key.current = crypto.randomUUID();
    } catch { setError('unknown'); } finally { setBusy(false); }
  }
  return <section className="mb-[var(--space-5)] flex flex-col gap-3 rounded-[var(--radius-surface)] border border-[var(--color-border-default)] p-[var(--space-4)]">
    <div className="flex items-center"><h3 className="m-0 text-[length:var(--text-lg)]">{t('work.kind.BinCount')}</h3><InfoHelp label={t('common.help')}>{t('work.bin.help')}</InfoHelp></div>
    <div className="flex flex-wrap items-end gap-3">
      <Field label={t('work.bin.location')} htmlFor="raise-bin"><Input id="raise-bin" value={location} onChange={event => { setLocation(event.target.value); setRaised(false); }} /></Field>
      <Field label={t('work.bin.reason')} htmlFor="raise-bin-reason"><Input id="raise-bin-reason" maxLength={120} required value={reason} onChange={event => setReason(event.target.value)} /></Field>
      <Button disabled={busy || !location.trim() || !reason.trim()} onClick={() => void raise()}>{t('work.bin.raise')}</Button>
    </div>
    {raised ? <Badge tone="success">{t('work.bin.raised')}</Badge> : null}
    {error ? <p role="alert" className="m-0 text-[var(--color-danger)]">{t([`errors.${error}`, 'errors.unknown'])}</p> : null}
  </section>;
}
