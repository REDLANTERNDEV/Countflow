import assert from 'node:assert/strict';
import { test } from 'node:test';
import { completionLocationAllowed } from '../src/lib/completion-location.js';

test('cached replenishment refuses a different face or missing target', () => {
  assert.equal(completionLocationAllowed('Replenishment', 'FACE-2', 'BULK-1', 'FACE-1'), false);
  assert.equal(completionLocationAllowed('Replenishment', 'FACE-1', 'BULK-1', null), false);
  assert.equal(completionLocationAllowed('Replenishment', ' face-1 ', 'BULK-1', 'FACE-1'), true);
});
test('a count stays at its holding while ordinary moves retain destination overrides', () => {
  assert.equal(completionLocationAllowed('BinCount', 'OTHER', 'FACE-1', 'FACE-1'), false);
  assert.equal(completionLocationAllowed('BinCount', ' face-1 ', 'FACE-1', null), true);
  assert.equal(completionLocationAllowed('CycleCount', 'OTHER', 'FACE-1', 'FACE-1'), false);
  assert.equal(completionLocationAllowed('CycleCount', 'FACE-1', 'FACE-1', null), true);
  assert.equal(completionLocationAllowed('Putaway', 'OTHER', 'DOCK', 'SHELF'), true);
  assert.equal(completionLocationAllowed('Pick', '', 'SHELF', 'BENCH'), false);
});
