import assert from 'node:assert/strict';
import { test } from 'node:test';
import { mergeTaskDecision } from '../src/lib/task-decision.js';

test('a list refresh preserves the explanation for the same trace', () => {
  const cached = { task: { decisionTraceId: 'one' }, decision: { id: 'one' } };
  assert.equal(mergeTaskDecision({ task: { decisionTraceId: 'one' }, decision: null }, cached).decision?.id, 'one');
});

test('a new pallet decision cannot inherit the cached explanation for the old trace', () => {
  const cached = { task: { decisionTraceId: 'one' }, decision: { id: 'one' } };
  assert.equal(mergeTaskDecision({ task: { decisionTraceId: 'two' }, decision: null }, cached).decision, null);
});

test('a full response always supplies its own explanation', () => {
  const cached = { task: { decisionTraceId: 'one' }, decision: { id: 'one' } };
  assert.equal(mergeTaskDecision({ task: { decisionTraceId: 'two' }, decision: { id: 'two' } }, cached).decision?.id, 'two');
});
