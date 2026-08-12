import { outbox } from './outbox.js';

/**
 * When a new build is allowed to take over the device.
 *
 * The handheld used to run `registerType: 'autoUpdate'`, which reloads the page
 * the moment a new service worker claims it. That was fine for six phases and
 * stopped being fine when the app started holding the operator's unsent work.
 *
 * <b>It was never a durability problem.</b> A reload mid-drain is safe by
 * construction: the command is already in IndexedDB, it carries the idempotency
 * key it was born with, and anything caught in flight comes back as `pending` on
 * the next start and is sent again to be recognised as a replay. That is the
 * whole design, and a service-worker update is simply a crash the browser
 * schedules on purpose.
 *
 * It is a *rudeness* problem, and two of them are real. Reloading takes away a
 * half-typed count from somebody standing at a bay — work with no record
 * anywhere, unlike everything in the outbox. And `skipWaiting` lets the incoming
 * worker drop the old build's precache while the old page is still running, so a
 * page that ever code-splits would start missing chunks it had not fetched yet;
 * the handheld does not today, and "does not today" is a property of the bundle
 * rather than a guarantee.
 *
 * So the update is taken under our own hand: applied when the device is quiet —
 * nothing queued, nothing draining — and otherwise held until it is. A handheld
 * is quiet many times an hour.
 *
 * Registered against the browser API directly rather than through
 * `virtual:pwa-register`, which pulls in `workbox-window` — roughly what the
 * whole of Phase 8 cost the bundle (5.6 kB gzipped, measured), to wrap the
 * thirty lines below. The thirty lines are the part with the judgement in them
 * anyway, and the judgement is the point of the file.
 */

// Both derived from Vite's own base rather than written out, because the app is
// served under /handheld/ by Caddy and a hardcoded copy of that is a second
// place for it to be wrong. In development vite-plugin-pwa serves a module
// worker from `dev-sw.js?dev-sw`; the build emits a classic one at `sw.js`.
const BASE = import.meta.env.BASE_URL;
const SW_URL = import.meta.env.DEV ? `${BASE}dev-sw.js?dev-sw` : `${BASE}sw.js`;

/** How often a device checks whether there is a newer build to take. */
const UPDATE_CHECK_MS = 60 * 60 * 1000;

export function registerServiceWorker(): void {
  if (!('serviceWorker' in navigator)) return;

  void navigator.serviceWorker.register(SW_URL, {
    type: import.meta.env.DEV ? 'module' : 'classic',
    scope: BASE,
  }).then(watch, (cause: unknown) => {
    // A handheld with no service worker still works; it just will not start in a
    // dead zone. Worth a line in the console and nothing on the operator's screen.
    console.error('Countflow: the offline shell could not be installed.', cause);
  });
}

function watch(registration: ServiceWorkerRegistration): void {
  // The page reloads exactly once, when the incoming worker takes control.
  let reloading = false;
  navigator.serviceWorker.addEventListener('controllerchange', () => {
    if (reloading) return;
    reloading = true;
    window.location.reload();
  });

  const apply = () => {
    const next = registration.waiting;
    if (!next || !quiet()) return;

    // `registerType: 'prompt'` leaves the new worker waiting and has it listen
    // for this message rather than calling `skipWaiting` on its own. That is the
    // whole reason the mode was changed: the moment of handover is ours.
    next.postMessage({ type: 'SKIP_WAITING' });
  };

  registration.addEventListener('updatefound', () => {
    const installing = registration.installing;
    if (!installing) return;

    installing.addEventListener('statechange', () => {
      // No controller means this is the first install rather than an update,
      // and there is nothing to interrupt.
      if (installing.state === 'installed' && navigator.serviceWorker.controller) apply();
    });
  });

  // A build that arrived while the device was busy, and the one that was already
  // waiting when the app started.
  outbox.subscribe(apply);
  apply();

  setInterval(() => void registration.update(), UPDATE_CHECK_MS);
}

/** Nothing of the operator's is in the air. */
function quiet(): boolean {
  const { commands, draining } = outbox.getSnapshot();
  if (draining) return false;
  return !commands.some((c) => c.state === 'pending' || c.state === 'sending');
}
