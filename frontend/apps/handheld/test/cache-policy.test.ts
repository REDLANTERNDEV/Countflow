import assert from 'node:assert/strict';
import { test } from 'node:test';
import { evictableTaskIds } from '../src/lib/cache-policy.js';

test('assigned tour stops survive the historical cache limit', () => {
  const tasks = Array.from({ length: 60 }, (_, i) => ({ id: String(i), fetchedAt: String(i), detail: { task: { status: 'Assigned' } } }));
  assert.deepEqual(evictableTaskIds(tasks, [], 50), []);
});
test('old completed tasks can be evicted but unresolved confirmations remain', () => {
  const tasks = [
    { id: 'old', fetchedAt: '2020', detail: { task: { status: 'Completed' } } },
    { id: 'pending', fetchedAt: '2021', detail: { task: { status: 'Completed' } } },
    { id: 'new', fetchedAt: '2022', detail: { task: { status: 'Completed' } } },
  ];
  assert.deepEqual(evictableTaskIds(tasks, ['pending'], 2), ['old']);
});
