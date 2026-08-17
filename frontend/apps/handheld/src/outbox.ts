import { api } from './api.js';
import { createDispatch, unanswered } from './lib/outbox/dispatch.js';
import { Outbox } from './lib/outbox/outbox.js';
import type { OutboxStore } from './lib/outbox/store.js';
import { createIndexedDbStore, createMemoryStore, openDatabase } from './lib/outbox/store.js';

/**
 * Whether the queue on this device actually survives a power cut.
 *
 * Read by the shell. A handheld that has fallen back to memory still queues and
 * still replays, and will still lose everything if the battery goes — so it says
 * so, in the header, permanently. An operator who believes their work is safe
 * and is wrong is worse off than one who knows to stay in signal.
 */
export let durable = true;

const opened: Promise<OutboxStore> = openDatabase()
  .then(createIndexedDbStore)
  .catch((cause: unknown) => {
    // A private window, an origin whose storage was evicted, a browser with
    // IndexedDB disabled. Rare on a managed scanner and not impossible.
    console.error('Countflow: the durable command queue is unavailable.', cause);
    durable = false;
    return createMemoryStore();
  });

/**
 * The store, usable before it has finished opening.
 *
 * Every method on the interface is already async, so waiting for the database is
 * a `await opened` at the front of each rather than an initialisation phase the
 * app has to sequence around. It matters because the first thing that happens
 * after a cold start is a restore, and the operator may be scanning before it
 * finishes.
 */
const store: OutboxStore = {
  all: async () => (await opened).all(),
  get: async (id) => (await opened).get(id),
  add: async (command) => (await opened).add(command),
  put: async (command) => (await opened).put(command),
  remove: async (id) => (await opened).remove(id),
  nextSeq: async () => (await opened).nextSeq(),
  cacheTask: async (task) => (await opened).cacheTask(task),
  readTask: async (id) => (await opened).readTask(id),
  readTasks: async (ownerUserId) => (await opened).readTasks(ownerUserId),
  forgetTask: async (id) => (await opened).forgetTask(id),
  clearTasks: async () => (await opened).clearTasks(),
};

export const outbox = new Outbox(store, createDispatch(api));

/**
 * How often a device with unsent work tries again.
 *
 * The `online` event is the signal that matters and this is the safety net
 * behind it: the event does not fire when the access point is up but the API is
 * not, and it does not fire when a device wakes with a link it never noticed
 * losing. Fifteen seconds is short enough that an operator walking out of a dead
 * aisle sees their queue drain before they reach the next bay.
 */
const RETRY_INTERVAL_MS = 15_000;

/**
 * Ask the warehouse whether it is there, when nothing else is going to.
 *
 * A drain only learns anything if it had something of the signed-in operator's
 * to send, so on a device with an empty queue — or one holding only the
 * previous operator's stranded work — a single unanswered request would latch
 * the badge to offline for good. Every screen that gates on it is then stuck
 * too, including "next job".
 *
 * `/auth/me` because a 401 is an answer: it says the warehouse is reachable and
 * the session is not, which is exactly the distinction the queue is built on.
 */
async function probe(): Promise<void> {
  try {
    const { response } = await api.GET('/api/v1/auth/me');
    outbox.noteReachable(!unanswered(response.status));
  } catch {
    outbox.noteReachable(false);
  }
}

export function startOutbox(): () => void {
  const kick = () => {
    void outbox.drain();
  };

  const timer = setInterval(() => {
    const { commands, draining, reachable } = outbox.getSnapshot();
    if (draining) return;
    if (commands.some((c) => c.state === 'pending')) kick();
    if (reachable === false) void probe();
  }, RETRY_INTERVAL_MS);

  const wake = () => {
    if (document.visibilityState === 'visible') kick();
  };

  window.addEventListener('online', kick);
  document.addEventListener('visibilitychange', wake);

  return () => {
    clearInterval(timer);
    window.removeEventListener('online', kick);
    document.removeEventListener('visibilitychange', wake);
  };
}
