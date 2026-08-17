import assert from 'node:assert/strict';
import { test } from 'node:test';
import { classify, unanswered } from '../src/lib/outbox/dispatch.js';
import {
  Outbox,
  STALL_AFTER,
  staleTaskIds,
  type Send,
  type SendOutcome,
} from '../src/lib/outbox/outbox.js';
import { categorise, needsSupervisor } from '../src/lib/outbox/refusals.js';
import { createMemoryStore, type OutboxStore } from '../src/lib/outbox/store.js';
import type { QueuedCommand } from '../src/lib/outbox/types.js';

/**
 * The queue's behaviour, tested where it can be tested.
 *
 * These cover the driver: what it sends, in what order, whose work it will not
 * touch, and what it does with each of the four answers a request can come back
 * with. They do not — and cannot — cover durability, which is a claim about
 * IndexedDB and a browser being killed, and is verified by using the product
 * with the network off and the process killed. A test that asserted durability
 * against an in-memory store would be asserting a fiction, which is the failure
 * mode this project has found once per phase.
 */

const OP1 = { userId: 'user-1', operatorCode: 'OP001' };
const OP2 = { userId: 'user-2', operatorCode: 'OP002' };

function completion(n: number) {
  return {
    kind: 'work.complete' as const,
    key: `key-${n}`,
    request: { taskId: `task-${n}`, toLocationCode: `WH1-STOR-AMB-A01-01-0${n}-A` },
    summary: {
      headline: 'work.kind.Putaway',
      sku: `SKU-1000${n}`,
      description: 'A carton of something',
      quantity: 24,
      uom: 'EA',
      fromLocationCode: 'WH1-RECV-DOCK-DOOR-00-00-D01',
      atLocationCode: `WH1-STOR-AMB-A01-01-0${n}-A`,
    },
  };
}

/** A network that is not there. Nothing reaches a server, so nothing is an answer. */
const unreachable: Send = async () => ({ kind: 'unreachable' });

function recorder(answer: (command: QueuedCommand) => SendOutcome) {
  const seen: QueuedCommand[] = [];

  const send: Send = async (command) => {
    seen.push(command);
    return answer(command);
  };

  return { send, seen };
}

async function outboxWith(send: Send, store: OutboxStore = createMemoryStore()) {
  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);
  return { outbox, store };
}

test('a command made with no network is kept, not lost', async () => {
  const { outbox, store } = await outboxWith(unreachable);

  const command = await outbox.submit(completion(1));

  assert.equal(command.state, 'pending');
  assert.equal((await store.all()).length, 1);
});

test('the idempotency key the scan was made under is the one that is replayed', async () => {
  // The whole feature rests on this. A key regenerated on restore turns a replay
  // into a second command, and against an append-only ledger that is a pallet
  // booked away twice with nothing to show it was once.
  const store = createMemoryStore();
  const offline = await outboxWith(unreachable, store);
  await offline.outbox.submit(completion(1));

  // A new device session over the same store: a reload, a crash, a flat battery.
  const { send, seen } = recorder(() => ({ kind: 'accepted', result: { ok: true } }));
  const restored = new Outbox(store, send);
  await restored.setOwner(OP1);
  await restored.recover();
  await restored.drain();

  assert.equal(seen.length, 1);
  assert.equal(seen[0]!.id, 'key-1');
});

test('a command caught in flight by a power cut is sent again rather than abandoned', async () => {
  const store = createMemoryStore();

  // A device that died between the request leaving and the answer arriving.
  await store.add({
    id: 'key-mid-flight',
    seq: 1,
    kind: 'work.complete',
    ownerUserId: OP1.userId,
    ownerOperatorCode: OP1.operatorCode,
    createdAt: new Date().toISOString(),
    request: { taskId: 'task-1', toLocationCode: 'WH1-STOR-AMB-A01-01-01-A' },
    summary: completion(1).summary,
    state: 'sending',
    attempts: 1,
  });

  const { send, seen } = recorder(() => ({ kind: 'accepted', result: {} }));
  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);
  await outbox.recover();
  await outbox.drain();

  assert.deepEqual(seen.map((c) => c.id), ['key-mid-flight']);
});

test('commands are replayed in the order the work happened', async () => {
  const store = createMemoryStore();
  const offline = await outboxWith(unreachable, store);
  await offline.outbox.submit(completion(1));
  await offline.outbox.submit(completion(2));
  await offline.outbox.submit(completion(3));

  const { send, seen } = recorder(() => ({ kind: 'accepted', result: {} }));
  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);
  await outbox.drain();

  assert.deepEqual(seen.map((c) => c.id), ['key-1', 'key-2', 'key-3']);
});

test('a refusal is kept and put in front of the operator, never dropped', async () => {
  // The one behaviour the phase exists for. A queue that silently discards what
  // the warehouse refused turns a discrepancy somebody could have walked back
  // and fixed into stock that is simply wrong, with no record anybody knew.
  const { outbox, store } = await outboxWith(async () => ({
    kind: 'refused',
    code: 'work.task_already_completed',
    status: 409,
  }));

  const command = await outbox.submit(completion(1));

  assert.equal(command.state, 'refused');
  assert.equal(command.refusal?.code, 'work.task_already_completed');

  const kept = await store.get('key-1');
  assert.equal(kept?.state, 'refused');
  // And it carries what the operator did, because nothing on the server does.
  assert.equal(kept?.summary.atLocationCode, 'WH1-STOR-AMB-A01-01-01-A');
});

test('a refusal does not stop the commands behind it', async () => {
  const store = createMemoryStore();
  const offline = await outboxWith(unreachable, store);
  await offline.outbox.submit(completion(1));
  await offline.outbox.submit(completion(2));

  const { send, seen } = recorder((command) =>
    command.id === 'key-1'
      ? { kind: 'refused', code: 'work.task_already_completed', status: 409 }
      : { kind: 'accepted', result: {} },
  );

  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);
  await outbox.drain();

  assert.deepEqual(seen.map((c) => c.id), ['key-1', 'key-2']);
  assert.equal((await store.get('key-2'))?.state, 'settled');
});

test('a dead network stops the drain rather than burning through the queue', async () => {
  const store = createMemoryStore();
  const offline = await outboxWith(unreachable, store);
  await offline.outbox.submit(completion(1));
  await offline.outbox.submit(completion(2));

  const { send, seen } = recorder(() => ({ kind: 'unreachable' }));
  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);
  await outbox.drain();

  // One attempt, not two: nothing behind a command that could not reach a server
  // is going to reach one either, and a scanner's battery is finite.
  assert.deepEqual(seen.map((c) => c.id), ['key-1']);
  assert.equal((await store.get('key-1'))?.state, 'pending');
  assert.equal((await store.get('key-2'))?.state, 'pending');
});

test('a server that keeps faulting eventually steps aside instead of blocking the shift', async () => {
  const store = createMemoryStore();
  const offline = await outboxWith(unreachable, store);
  await offline.outbox.submit(completion(1));
  await offline.outbox.submit(completion(2));

  const { send } = recorder((command) =>
    command.id === 'key-1' ? { kind: 'unwell', status: 500 } : { kind: 'accepted', result: {} },
  );

  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);

  for (let attempt = 0; attempt < STALL_AFTER; attempt += 1) {
    await outbox.drain();
  }

  const poison = await store.get('key-1');
  assert.equal(poison?.state, 'stalled');
  // And the one behind it got through on the drain that stepped over it.
  assert.equal((await store.get('key-2'))?.state, 'settled');
});

test('a stalled command is not abandoned — the operator can send it when the warehouse is back', async () => {
  // The hole this closes was found by stopping the API for four minutes, which
  // is shorter than a real outage. `drain` walks pending commands only, so a
  // stalled one was unreachable by every automatic path *and* by the "send
  // them now" button, and the single action left on its card was the one that
  // deletes it. A movement the warehouse never heard about, thrown away by the
  // person it happened to.
  const store = createMemoryStore();
  let answer: SendOutcome = { kind: 'unwell', status: 502 };

  const { send, seen } = recorder(() => answer);
  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);
  await outbox.submit(completion(1));

  for (let attempt = 0; attempt < STALL_AFTER; attempt += 1) await outbox.drain();
  assert.equal((await store.get('key-1'))?.state, 'stalled');

  // The warehouse comes back. A drain still will not touch it — that is what
  // stalling means — so without `retry` the work is stuck for good.
  answer = { kind: 'accepted', result: {} };
  const attempted = seen.length;
  await outbox.drain();
  assert.equal(seen.length, attempted, 'a stalled command was picked up by an ordinary drain');

  await outbox.retry('key-1');

  const sent = await store.get('key-1');
  assert.equal(sent?.state, 'settled');
  assert.equal(sent?.attempts, 0, 'the attempt count measures one outage, not the command');
  // Same key, so the warehouse recognises a replay rather than a second pallet.
  assert.equal(seen.at(-1)?.id, 'key-1');
});

test('only a stalled command can be retried', async () => {
  // A refusal is an answer, and sending it again earns the identical no.
  const store = createMemoryStore();
  const outbox = new Outbox(store, async () => ({
    kind: 'refused',
    code: 'work.task_already_completed',
    status: 409,
  }));
  await outbox.setOwner(OP1);
  await outbox.submit(completion(1));

  await outbox.retry('key-1');

  assert.equal((await store.get('key-1'))?.state, 'refused');
});

test('another operator’s work is never sent under the signed-in operator', async () => {
  // Not a preference. The ledger records the credential that posted a movement,
  // so replaying OP001's putaway on OP002's token writes a name into an
  // append-only record against work that person never did.
  const store = createMemoryStore();
  const first = new Outbox(store, unreachable);
  await first.setOwner(OP1);
  await first.submit(completion(1));

  const { send, seen } = recorder(() => ({ kind: 'accepted', result: {} }));
  const second = new Outbox(store, send);
  await second.setOwner(OP2);
  await second.submit(completion(2));

  assert.deepEqual(seen.map((c) => c.id), ['key-2']);
  assert.equal((await store.get('key-1'))?.state, 'pending');
  assert.equal((await store.get('key-1'))?.ownerOperatorCode, 'OP001');
});

test('signing out does not throw away unsent work', async () => {
  const { outbox, store } = await outboxWith(unreachable);
  await outbox.submit(completion(1));

  await outbox.setOwner(null);

  assert.equal((await store.all()).length, 1);
  assert.equal((await store.get('key-1'))?.state, 'pending');
});

test('a pending command cannot be cleared from the device; a refused one can', async () => {
  // The asymmetry is the point. A pending command is the device's record of a
  // pallet somebody moved, and an operator who can make it disappear can lose
  // stock quietly. A refused one has been answered, and clearing it is the
  // operator saying they have dealt with it.
  const store = createMemoryStore();
  const stuck = new Outbox(store, unreachable);
  await stuck.setOwner(OP1);
  await stuck.submit(completion(1));

  await stuck.acknowledge('key-1');
  assert.equal((await store.get('key-1'))?.state, 'pending', 'a pending command was cleared');

  const refusing = new Outbox(store, async () => ({
    kind: 'refused',
    code: 'inventory.insufficient_stock',
    status: 409,
  }));
  await refusing.setOwner(OP1);
  await refusing.drain();
  await refusing.acknowledge('key-1');

  assert.equal(await store.get('key-1'), undefined);
});

test('one intent submitted twice is one command, and the second does not reset the first', async () => {
  // A double-tap on a scanner with a sticky screen puts two submits in flight
  // under one key. The store is keyed by the key, so there was only ever going
  // to be one row — what matters is which one wins. The later enqueue arriving
  // after the first has already been refused must not overwrite the refusal with
  // a fresh pending record, or the news is lost and the command is sent again to
  // earn the identical no.
  const store = createMemoryStore();
  const { send, seen } = recorder(() => ({
    kind: 'refused',
    code: 'work.task_already_completed',
    status: 409,
  }));

  const outbox = new Outbox(store, send);
  await outbox.setOwner(OP1);

  await outbox.submit(completion(1));
  await outbox.submit(completion(1));

  assert.equal((await store.all()).length, 1);
  assert.equal((await store.get('key-1'))?.state, 'refused');
  assert.equal(seen.length, 1, 'the refused command was sent a second time');
});

test('an expired session stops the drain without refusing anything', async () => {
  const store = createMemoryStore();
  const offline = await outboxWith(unreachable, store);
  await offline.outbox.submit(completion(1));

  const outbox = new Outbox(store, async () => ({ kind: 'unauthenticated' }));
  await outbox.setOwner(OP1);
  await outbox.drain();

  // Still the operator's work, still unsent. Signing in again sends it.
  assert.equal((await store.get('key-1'))?.state, 'pending');
});

test('a rate-limited or timed-out request is not an answer about the command', () => {
  // A device coming out of a dead aisle with fifteen queued commands is exactly
  // what trips the per-credential limiter, and treating 429 as a refusal would
  // throw away a shift's work at the moment it finally had a signal.
  assert.equal(classify(429, { code: 'rate_limit.exceeded' }).kind, 'unwell');
  assert.equal(classify(408, undefined).kind, 'unwell');
  assert.equal(classify(503, undefined).kind, 'unwell');
  assert.equal(classify(401, undefined).kind, 'unauthenticated');

  const refused = classify(409, { code: 'work.task_already_completed' });
  assert.equal(refused.kind, 'refused');
  assert.equal(refused.kind === 'refused' ? refused.code : null, 'work.task_already_completed');
});

test('a proxy answering for a dead API is "nobody answered", not a refusal', () => {
  // Found by running the real stack rather than a mock, and it was a defect: the
  // handheld is served by Caddy with the API behind it, so stopping the API
  // produces a **502 from the proxy** and not a rejected `fetch`. Every screen
  // that decided "we must be offline" by catching an exception therefore never
  // decided it — and a reload against a dead API signed the operator out with
  // their queued work still on the device, which is the one state this whole
  // phase exists to prevent.
  assert.equal(unanswered(502), true);
  assert.equal(unanswered(503), true);
  assert.equal(unanswered(504), true);
  // Nothing at all: the thrown case.
  assert.equal(unanswered(undefined), true);

  // And the other side of the line, so the predicate is not simply "true".
  assert.equal(unanswered(409), false);
  assert.equal(unanswered(404), false);
  assert.equal(unanswered(400), false);

  // A 502 must never consume a command, because the warehouse never saw it.
  assert.equal(classify(502, undefined).kind, 'unwell');
});

test('the warehouse being unreachable is reported separately from the radio', async () => {
  const { outbox } = await outboxWith(unreachable);
  assert.equal(outbox.getSnapshot().reachable, null, 'nothing has asked yet');

  await outbox.submit(completion(1));
  assert.equal(outbox.getSnapshot().reachable, false);

  // A refusal is the warehouse talking, so it counts as reached — an operator
  // told "offline" while the server is arguing with them would go looking for a
  // signal they already have.
  const answering = new Outbox(createMemoryStore(), async () => ({
    kind: 'refused',
    code: 'work.task_already_completed',
    status: 409,
  }));
  await answering.setOwner(OP1);
  await answering.submit(completion(2));
  assert.equal(answering.getSnapshot().reachable, true);
});

test('a refusal with no code is still shown rather than swallowed', () => {
  const outcome = classify(400, {});
  assert.equal(outcome.kind, 'refused');
  assert.equal(outcome.kind === 'refused' ? outcome.code : null, 'unknown');
});

test('the task cache is reconciled against the warehouse, not merged into', async () => {
  // Found by using the product: the cache only ever grew, so an operator was
  // shown two putaways as "jobs on this device" that the warehouse had finished
  // with twenty minutes earlier. A stale job on a handheld is an instruction to
  // move a pallet somebody has already moved.
  const store = createMemoryStore();
  const outbox = new Outbox(store, unreachable);
  await outbox.setOwner(OP1);

  // A confirmation the device is holding for a job it has already done.
  await outbox.submit({
    ...completion(9),
    request: { taskId: 'confirmed-and-unsent', toLocationCode: 'WH1-STOR-AMB-A01-01-09-A' },
  });

  const cached = ['done-elsewhere', 'still-mine', 'confirmed-and-unsent'];
  const authoritative = ['still-mine'];

  const stale = staleTaskIds(cached, authoritative, outbox.getSnapshot().commands);

  // The finished job goes. The one the warehouse still calls ours stays. And the
  // one with work in flight stays, because that entry is the device's record and
  // not the warehouse's to withdraw.
  assert.deepEqual(stale, ['done-elsewhere']);
});

test('the three refusals the phase was written for are different news', () => {
  assert.equal(categorise('work.task_already_completed'), 'already_done');
  assert.equal(categorise('inventory.insufficient_stock'), 'stock_missing');
  assert.equal(categorise('inventory.reservation_not_open'), 'claim_spent');

  // All three leave goods in a state somebody has to look at.
  assert.ok(needsSupervisor('already_done'));
  assert.ok(needsSupervisor('stock_missing'));
  assert.ok(needsSupervisor('claim_spent'));

  // A mistyped SKU posted nothing, so it is annoying rather than dangerous.
  assert.equal(categorise('inbound.item_not_found'), 'not_recognised');
  assert.equal(needsSupervisor('not_recognised'), false);
});
