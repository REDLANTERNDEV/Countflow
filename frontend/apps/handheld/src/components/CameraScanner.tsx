import { useTranslation } from '@countflow/i18n';
import { Button, InfoHelp, Sheet } from '@countflow/ui';
import { useEffect, useRef, useState, type RefObject } from 'react';
import { startCameraScan, type CameraFailure } from '../lib/camera-scan.js';

export function CameraScanner({ onScan, returnFocusRef }: { onScan: (raw: string) => void; returnFocusRef: RefObject<HTMLElement | null> }) {
  const { t } = useTranslation(); const [open, setOpen] = useState(false);
  return <Sheet open={open} onOpenChange={setOpen} title={t('scan.camera.title')} closeLabel={t('common.close')}
    side="right" returnFocusRef={returnFocusRef} className="w-[min(100vw,480px)]" trigger={<Button data-scan-control>{t('scan.camera.open')}</Button>}>
    <CameraPreview onScan={raw => { setOpen(false); onScan(raw); }} onClose={() => setOpen(false)} />
  </Sheet>;
}

function CameraPreview({ onScan, onClose }: { onScan: (raw: string) => void; onClose: () => void }) {
  const { t } = useTranslation();
  const [error, setError] = useState<CameraFailure | null>(null); const [attempt, setAttempt] = useState(0);
  const video = useRef<HTMLVideoElement>(null); const capture = useRef(onScan); capture.current = onScan;
  const close = useRef(onClose); close.current = onClose;
  useEffect(() => {
    if (!video.current) return;
    const controller = new AbortController();
    const leave = () => { controller.abort(); close.current(); };
    const hidden = () => { if (document.hidden) leave(); };
    document.addEventListener('visibilitychange', hidden); window.addEventListener('pagehide', leave);
    setError(null);
    void startCameraScan(video.current, controller.signal, raw => capture.current(raw), setError);
    return () => { controller.abort(); document.removeEventListener('visibilitychange', hidden); window.removeEventListener('pagehide', leave); };
  }, [attempt]);
  return <div className="flex flex-col gap-[var(--space-3)]">
      <div className="flex items-center"><span>{t('scan.camera.aim')}</span><InfoHelp label={t('common.about', { topic: t('scan.camera.title') })}>{t('scan.camera.help')}</InfoHelp></div>
      <video ref={video} autoPlay muted playsInline hidden={Boolean(error && error !== 'multiple')} aria-label={t('scan.camera.preview')}
        className="aspect-[3/4] max-h-[60dvh] w-full rounded-[var(--radius-control)] bg-black object-cover" />
      {error ? <p role="alert" className="m-0 text-[var(--color-danger-text)]">{t(`scan.camera.${error}`)}</p> : null}
      {error && error !== 'multiple' ? <Button onClick={() => setAttempt(value => value + 1)}>{t('scan.camera.retry')}</Button> : null}
    </div>;
}
