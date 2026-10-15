import assert from 'node:assert/strict';
import { test } from 'node:test';
import { startCameraScan, type CameraPlatform } from '../src/lib/camera-scan.js';

function setup() {
  const frames: (() => void)[] = []; const scans: string[] = []; const failures: string[] = [];
  let stopped = 0; let requested = 0; let results = [{ rawValue: '010950110153000310LOT\u001d17270115' }];
  const stream = { getTracks: () => [{ stop: () => stopped++ }] } as unknown as MediaStream;
  const video = { srcObject: null, readyState: 4, play: async () => {}, pause: () => {} } as unknown as HTMLVideoElement;
  const platform: CameraPlatform = { secure: true, formats: async () => ['data_matrix', 'code_128'],
    createDetector: () => ({ detect: async () => results }), getStream: async () => { requested++; return stream; },
    schedule: action => { frames.push(action); return frames.length; }, cancel: () => {} };
  const controller = new AbortController();
  const start = () => startCameraScan(video, controller.signal, value => scans.push(value), code => failures.push(code), platform);
  return { platform, stream, video, controller, frames, scans, failures, start, setResults: (value: typeof results) => { results = value; },
    get stopped() { return stopped; }, get requested() { return requested; } };
}
const settle = () => new Promise(resolve => setImmediate(resolve));

test('captures exact GS1 separators once and releases the camera before delivery', async () => {
  const f = setup(); await f.start(); await settle();
  assert.deepEqual(f.scans, ['010950110153000310LOT\u001d17270115']); assert.equal(f.stopped, 1); assert.equal(f.video.srcObject, null);
  f.controller.abort(); assert.equal(f.stopped, 1); assert.equal(f.frames.length, 0);
});
test('refuses unsupported or insecure cameras without requesting permission', async () => {
  for (const secure of [false, true]) {
    const f = setup(); f.platform.secure = secure; f.platform.formats = async () => ['qr_code']; await f.start();
    assert.deepEqual(f.failures, [secure ? 'unsupported' : 'secure']); assert.equal(f.requested, 0);
  }
});
test('permission granted after dismissal is released and cannot emit a scan', async () => {
  const f = setup(); let grant!: (stream: MediaStream) => void; f.platform.getStream = () => new Promise(resolve => { grant = resolve; });
  const pending = f.start(); await settle(); f.controller.abort(); grant(f.stream); await pending;
  assert.equal(f.stopped, 1); assert.equal(f.video.srcObject, null); assert.deepEqual(f.scans, []); assert.deepEqual(f.failures, []);
});
test('multiple distinct barcodes require reframing and only one detection is in flight', async () => {
  const f = setup(); f.setResults([{ rawValue: 'first' }, { rawValue: 'second' }]); await f.start(); await settle();
  assert.deepEqual(f.scans, []); assert.deepEqual(f.failures, ['multiple']); assert.equal(f.frames.length, 1);
  f.setResults([{ rawValue: 'same' }, { rawValue: 'same' }]); f.frames.shift()!(); await settle();
  assert.deepEqual(f.scans, ['same']); assert.equal(f.stopped, 1);
});
test('permission and detector failures release resources with stable codes', async () => {
  const denied = setup(); denied.platform.getStream = async () => { throw new DOMException('denied', 'NotAllowedError'); }; await denied.start();
  assert.deepEqual(denied.failures, ['permission']);
  const broken = setup(); broken.platform.createDetector = () => ({ detect: async () => { throw new Error('native failure'); } });
  await broken.start(); await settle(); assert.equal(broken.stopped, 1); assert.deepEqual(broken.failures, ['failed']);
});
test('dismissal during native detection prevents late callbacks and stops tracks', async () => {
  const f = setup(); let finish!: (result: { rawValue: string }[]) => void;
  f.platform.createDetector = () => ({ detect: () => new Promise(resolve => { finish = resolve; }) });
  await f.start(); f.controller.abort(); finish([{ rawValue: 'late' }]); await settle();
  assert.equal(f.stopped, 1); assert.deepEqual(f.scans, []); assert.deepEqual(f.failures, []); assert.equal(f.frames.length, 0);
});
test('playback failure releases acquired tracks and reports failure', async () => {
  const f = setup(); f.video.play = async () => { throw new Error('playback failed'); }; await f.start();
  assert.equal(f.stopped, 1); assert.equal(f.video.srcObject, null); assert.deepEqual(f.failures, ['failed']);
});
test('dismissal while checking formats never requests a camera', async () => {
  const f = setup(); let supported!: (formats: string[]) => void; f.platform.formats = () => new Promise(resolve => { supported = resolve; });
  const pending = f.start(); f.controller.abort(); supported(['data_matrix']); await pending;
  assert.equal(f.requested, 0); assert.deepEqual(f.scans, []); assert.deepEqual(f.failures, []);
});
