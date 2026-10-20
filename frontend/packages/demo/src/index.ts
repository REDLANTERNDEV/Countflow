import { createSeed } from './data.js';
import { createBrowserStore, expireIdleState, IDLE_MS } from './store.js';
import { createDemoTransport } from './transport.js';

const GENERATION = 'countflow.demo.generation';
const CHANGES = 'countflow.demo.changes';
const store = createBrowserStore();
const transport = createDemoTransport(store);

export function cleanOldDemoStorage(epoch: string, previous: string | undefined, stores: Storage[], deleteDatabase: (name: string) => unknown): void {
  const prefix = `countflow.demo.${epoch}.`;
  for (const storage of stores) {
    for (const key of Object.keys(storage)) {
      if (key.startsWith('countflow.demo.') && key !== GENERATION && key !== CHANGES && !key.startsWith(prefix)) storage.removeItem(key);
    }
  }
  if (previous && previous !== epoch) deleteDatabase(`countflow.demo.${previous}.handheld`);
}

/** Called before either SPA creates its client, session store, or command queue. */
export async function initializeDemo() {
  try { return await configureDemo(); }
  catch {
    // Render the ordinary login with an actionable error even if storage is denied.
    return {
      fetch: (async () => Response.json({ code: 'demo.storage_unavailable' }, { status: 503 })) as typeof globalThis.fetch,
      storageKey: (key: string) => `countflow.demo.unavailable.${key}`,
      reset: async () => { window.location.reload(); },
    };
  }
}

async function configureDemo() {
  let previous = localStorage.getItem(GENERATION) ?? undefined;
  const epoch = await store.transact(state => {
    if (Date.now() - state.activityAt >= IDLE_MS) {
      previous = state.epoch;
      expireIdleState(state, Date.now());
    }
    state.activityAt = Date.now();
    return state.epoch;
  });
  const prefix = `countflow.demo.${epoch}.`;
  const storageKey = (key: string) => `${prefix}${key.replace(/^countflow\./, '')}`;

  cleanOldDemoStorage(epoch, previous, [localStorage, sessionStorage], name => indexedDB.deleteDatabase(name));
  localStorage.setItem(GENERATION, epoch);

  async function reset(onlyIfIdle = false) {
    const next = await store.transact(state => {
      if (state.epoch !== epoch) return state.epoch;
      if (onlyIfIdle && Date.now() - state.activityAt < IDLE_MS) {
        state.activityAt = Date.now();
        return undefined;
      }
      Object.assign(state, createSeed()); return state.epoch;
    });
    if (!next) return;
    localStorage.setItem(GENERATION, next);
    // Close this page's queue connection on reload; onversionchange closes other tabs.
    indexedDB.deleteDatabase(storageKey('countflow.handheld'));
    for (const storage of [localStorage, sessionStorage]) {
      for (const key of Object.keys(storage)) if (key.startsWith(prefix)) storage.removeItem(key);
    }
    window.location.reload();
  }

  window.addEventListener('storage', event => {
    if (event.key === GENERATION && event.newValue && event.newValue !== epoch) window.location.reload();
    if (event.key === CHANGES) window.dispatchEvent(new Event('countflow:demo-change'));
  });
  let touchedAt = 0;
  const touch = () => {
    if (Date.now() - touchedAt < 10000) return;
    touchedAt = Date.now();
    void store.transact(state => { if (state.epoch === epoch) state.activityAt = Date.now(); }).catch(() => {});
  };
  window.addEventListener('pointerdown', touch, { passive: true });
  window.addEventListener('keydown', touch, { passive: true });
  window.addEventListener('focus', () => {
    // The inactivity check and rotation share a transaction: another active tab
    // cannot be reset between a separate read and write.
    void reset(true).catch(() => {});
  });
  const fetch: typeof globalThis.fetch = async (input, init) => {
    const result = await transport(input, init);
    const method = init?.method ?? (input instanceof Request ? input.method : 'GET');
    if (result.ok && method !== 'GET') {
      localStorage.setItem(CHANGES, crypto.randomUUID());
      window.dispatchEvent(new Event('countflow:demo-change'));
    }
    return result;
  };
  return { fetch, storageKey, reset };
}
