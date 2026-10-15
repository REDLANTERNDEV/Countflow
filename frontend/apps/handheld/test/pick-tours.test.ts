import assert from 'node:assert/strict';
import { test } from 'node:test';
import { tourStops, hasUnsentTourWork } from '../src/lib/pick-tours.js';

test('a tour resumes only its assigned stops in walking order', () => {
  const stops = [
    { id: 'b', pickTourId: 'tour', tourSequence: 2, status: 'Assigned' },
    { id: 'other', pickTourId: 'other', tourSequence: 1, status: 'Assigned' },
    { id: 'done', pickTourId: 'tour', tourSequence: 3, status: 'Completed' },
    { id: 'a', pickTourId: 'tour', tourSequence: 1, status: 'Assigned' },
  ];
  assert.deepEqual(tourStops(stops, 'tour').map(s => s.id), ['a', 'b']);
});

test('queued, refused, and stalled completions prevent releasing physical work', () => {
  for (const state of ['pending', 'sending', 'refused', 'stalled']) {
    assert.equal(hasUnsentTourWork(['a'], [
      { kind: 'work.complete', state, request: { taskId: 'a' } },
    ]), true);
  }
  assert.equal(hasUnsentTourWork(['a'], [
    { kind: 'work.complete', state: 'settled', request: { taskId: 'a' } },
    { kind: 'work.complete', state: 'pending', request: { taskId: 'other' } },
  ]), false);
});
