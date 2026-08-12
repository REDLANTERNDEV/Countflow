import { useSyncExternalStore } from 'react';
import type { OutboxSnapshot } from './lib/outbox/outbox.js';
import { outbox } from './outbox.js';

/**
 * The queue, as React sees it.
 *
 * `useSyncExternalStore` rather than state lifted into a provider, because the
 * queue is genuinely external: a drain triggered by the `online` event, by the
 * retry timer or by the app coming back to the foreground has nothing to do with
 * a render, and every screen that shows a count has to see the same one.
 */
export function useOutbox(): OutboxSnapshot {
  return useSyncExternalStore(outbox.subscribe, outbox.getSnapshot);
}
