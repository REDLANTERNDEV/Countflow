import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createCountflowClient } from '@countflow/api-client';
import { createDispatch } from '../src/lib/outbox/dispatch.js';
import type { QueuedCommand } from '../src/lib/outbox/types.js';

test('a queued pallet confirmation sends its original decision on every retry', async () => {
  const originalFetch = globalThis.fetch;
  const sent: { body: unknown; key: string | null }[] = [];
  globalThis.fetch = async input => {
    const request = input as Request;
    sent.push({ body: await request.json(), key: request.headers.get('Idempotency-Key') });
    return new Response('{}', { status: sent.length === 1 ? 503 : 200, headers: { 'Content-Type': 'application/json' } });
  };
  try {
    const client = createCountflowClient({ baseUrl: 'http://warehouse.test', tokenStore: {
      read: () => null, write: () => {}, clear: () => {},
    } });
    const dispatch = createDispatch(client);
    const command = { id: 'physical-confirmation', kind: 'work.complete', request: {
      taskId: 'pallet-job', toLocationCode: 'BAY-01', expectedDecisionTraceId: 'decision-seen-at-rack',
    } } as QueuedCommand;
    assert.equal((await dispatch(command)).kind, 'unwell');
    assert.equal((await dispatch(command)).kind, 'accepted');
    assert.deepEqual(sent, [
      { key: 'physical-confirmation', body: { toLocationCode: 'BAY-01', expectedDecisionTraceId: 'decision-seen-at-rack' } },
      { key: 'physical-confirmation', body: { toLocationCode: 'BAY-01', expectedDecisionTraceId: 'decision-seen-at-rack' } },
    ]);
  } finally { globalThis.fetch = originalFetch; }
});
