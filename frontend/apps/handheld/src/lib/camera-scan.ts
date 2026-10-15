export interface CameraPlatform {
  secure: boolean;
  formats: () => Promise<string[]>;
  createDetector: (formats: string[]) => { detect: (video: HTMLVideoElement) => Promise<{ rawValue: string }[]> };
  getStream: () => Promise<MediaStream>;
  schedule: (action: () => void) => number;
  cancel: (handle: number) => void;
}
export type CameraFailure = 'secure' | 'unsupported' | 'permission' | 'unavailable' | 'failed' | 'multiple';

const warehouseFormats = ['data_matrix', 'code_128', 'qr_code', 'ean_13', 'ean_8', 'upc_a', 'upc_e'];
function browserCamera(): CameraPlatform {
  const Detector = (globalThis as unknown as { BarcodeDetector?: {
    new(options: { formats: string[] }): ReturnType<CameraPlatform['createDetector']>;
    getSupportedFormats(): Promise<string[]>;
  } }).BarcodeDetector;
  return {
    secure: globalThis.isSecureContext,
    formats: () => Detector && typeof navigator.mediaDevices?.getUserMedia === 'function' ? Detector.getSupportedFormats() : Promise.resolve([]),
    createDetector: formats => new Detector!({ formats }),
    getStream: () => navigator.mediaDevices.getUserMedia({ audio: false, video: { facingMode: { ideal: 'environment' } } }),
    schedule: action => window.setTimeout(action, 180), cancel: handle => window.clearTimeout(handle),
  };
}

/** Owns permission, one detection at a time, and every track until capture or cancellation. */
export async function startCameraScan(video: HTMLVideoElement, signal: AbortSignal, onScan: (raw: string) => void,
  onFailure: (code: CameraFailure) => void, platform = browserCamera()): Promise<void> {
  let active = !signal.aborted; let stream: MediaStream | undefined; let timer: number | undefined;
  const stop = () => {
    active = false; if (timer !== undefined) platform.cancel(timer);
    stream?.getTracks().forEach(track => track.stop()); stream = undefined;
    video.pause(); video.srcObject = null; signal.removeEventListener('abort', stop);
  };
  const fail = (code: CameraFailure) => { if (!active) return; stop(); onFailure(code); };
  signal.addEventListener('abort', stop, { once: true });
  if (!active) { stop(); return; }
  if (!platform.secure) { fail('secure'); return; }
  try {
    const supported = await platform.formats(); if (!active) return;
    if (!supported.includes('data_matrix')) { fail('unsupported'); return; }
    const detector = platform.createDetector(warehouseFormats.filter(format => supported.includes(format)));
    const acquired = await platform.getStream();
    if (!active) { acquired.getTracks().forEach(track => track.stop()); return; }
    stream = acquired; video.srcObject = stream; await video.play();
    if (!active) return;
    const detect = async () => {
      if (!active) return;
      try {
        if (video.readyState >= 2) {
          const found = await detector.detect(video); if (!active) return;
          const values = [...new Set(found.map(barcode => barcode.rawValue).filter(value => value.trim().length > 0))];
          if (values.length === 1) { stop(); onScan(values[0]!); return; }
          if (values.length > 1) onFailure('multiple');
        }
        if (active) timer = platform.schedule(() => { void detect(); });
      } catch { fail('failed'); }
    };
    void detect();
  } catch (error) {
    const name = (error as { name?: string }).name;
    fail(name === 'NotAllowedError' || name === 'SecurityError' ? 'permission' : name === 'NotFoundError' || name === 'NotReadableError' ? 'unavailable' : 'failed');
  }
}
