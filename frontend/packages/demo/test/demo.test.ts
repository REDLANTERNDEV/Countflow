import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createSeed, type DemoState } from '../src/data.js';
import { createDemoTransport } from '../src/transport.js';
import type { DemoStore } from '../src/store.js';
import { expireIdleState, IDLE_MS } from '../src/store.js';
import { readFileSync } from 'node:fs';
import { cleanOldDemoStorage } from '../src/index.js';

function environment() {
  let state: DemoState = createSeed();
  const store: DemoStore = { async transact(run) {
    const draft = structuredClone(state);
    const result = run(draft);
    state = draft;
    return result;
  } };
  const transport = createDemoTransport(store);
  let token = '';
  const request = async (path: string, method = 'GET', body?: unknown, key?: string) => {
    const headers: Record<string, string> = { Authorization: `Bearer ${token}` };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (key) headers['Idempotency-Key'] = key;
    const response = await transport(new Request(`https://demo.invalid/api/v1/${path}`, {
      method, headers, ...(body !== undefined ? { body: JSON.stringify(body) } : {}),
    }));
    const data = response.status === 204 ? undefined : await response.json();
    return { status: response.status, data };
  };
  const login = async (identifier = 'operator') => {
    const result = await request('auth/login', 'POST', { identifier, password: 'Countflow#2026' });
    assert.equal(result.status, 200);
    token = result.data.accessToken;
    return result.data;
  };
  return { store, request, login, get state() { return state; } };
}

test('visitors have isolated data; receipt and putaway update balances, tasks, and ledger; retries are idempotent', async () => {
  const a = environment(), b = environment();
  await a.login(); await b.login();
  const body = { sku: 'SKU-10001', quantity: 12, toLocationCode: 'DOCK-01' };
  const receipt = await a.request('inbound/receipts', 'POST', body, 'receipt-1');
  assert.equal(receipt.status, 200);
  assert.ok(receipt.data.workTaskId);
  const replay = await a.request('inbound/receipts', 'POST', body, 'receipt-1');
  assert.deepEqual(replay.data, receipt.data);
  assert.equal(a.state.movements.length, b.state.movements.length + 1);
  const changed = await a.request('inbound/receipts', 'POST', { ...body, quantity: 13 }, 'receipt-1');
  assert.equal(changed.status, 409);
  const task = receipt.data.workTaskId;
  assert.equal((await a.request(`work/tasks/${task}/claim`, 'POST')).status, 200);
  const done = await a.request(`work/tasks/${task}/complete`, 'POST', {
    toLocationCode: receipt.data.suggestedLocationCode,
    expectedDecisionTraceId: receipt.data.decisionTraceId,
  }, 'complete-1');
  assert.equal(done.status, 200);
  assert.equal(done.data.task.status, 'Completed');
  assert.equal(a.state.balances.find(row => row.sku === body.sku && row.locationCode === 'DOCK-01')?.quantity ?? 0, 0);
  assert.equal(a.state.movements.length, b.state.movements.length + 2);
  assert.equal((await a.request('inventory/reconciliation', 'POST')).data.isClean, true);
});

test('ready order can be released, picked, packed, and despatched with stock and ledger updates', async () => {
  const e = environment(); await e.login('supervisor');
  const order = e.state.orders.find(row => row.status === 'New')!;
  assert.equal((await e.request(`outbound/orders/${order.id}/release`, 'POST', {}, 'release-1')).status, 200);
  assert.equal((await e.request(`outbound/orders/${order.id}/pack`, 'POST', {})).status, 409);
  const tasks = e.state.tasks.filter(row => row.sourceReference === order.reference && row.kind === 'Pick');
  assert.ok(tasks.length > 0);
  await e.login();
  for (const task of tasks) {
    await e.request(`work/tasks/${task.id}/claim`, 'POST');
    assert.equal((await e.request(`work/tasks/${task.id}/complete`, 'POST', { toLocationCode: task.suggestedLocationCode }, task.id)).status, 200);
  }
  await e.login('supervisor');
  assert.equal((await e.request(`outbound/orders/${order.id}/pack`, 'POST', {})).status, 200);
  assert.equal((await e.request(`outbound/orders/${order.id}/despatch`, 'POST', {}, 'ship-1')).status, 200);
  assert.equal(e.state.orders.find(row => row.id === order.id)?.status, 'Despatched');
  assert.equal(e.state.movements.at(-1)?.type, 'Despatch');
});

test('counting updates stock, plan progress, accuracy, and ledger; operator cannot create plans', async () => {
  const e = environment(); await e.login();
  assert.equal((await e.request('counting/plans', 'POST', { warehouseCode: 'WH1', maxTasks: 1 })).status, 403);
  await e.login('supervisor');
  const result = await e.request('counting/plans', 'POST', { warehouseCode: 'WH1', maxTasks: 1 });
  assert.equal(result.status, 200);
  const task = e.state.tasks.find(row => row.id === result.data.lines[0].workTaskId)!;
  const balance = e.state.balances.find(row => row.sku === task.sku && row.locationCode === task.fromLocationCode)!;
  const expected = balance.quantity;
  await e.login();
  await e.request(`work/tasks/${task.id}/claim`, 'POST');
  const done = await e.request(`work/tasks/${task.id}/complete`, 'POST', { toLocationCode: task.fromLocationCode, countedQuantity: expected - 1 }, 'count-1');
  assert.equal(done.status, 200);
  assert.equal(done.data.count.variance, -1);
  assert.equal(e.state.balances.find(row => row.sku === task.sku && row.locationCode === task.fromLocationCode)?.quantity, expected - 1);
  assert.equal(e.state.plans.find(row => row.id === result.data.id)?.status, 'Completed');
  assert.equal(e.state.movements.at(-1)?.type, 'CountAdjustment');
});

test('pagination and filters work; unsupported operations fail closed; reset invalidates old sessions', async () => {
  const e = environment(); await e.login('admin');
  const first = await e.request('items?page=1&pageSize=10');
  const second = await e.request('items?page=2&pageSize=10');
  assert.equal(first.data.items.length, 10);
  assert.notEqual(first.data.items[0].sku, second.data.items[0].sku);
  const filter = await e.request('items?abcClass=A&temperature=Ambient');
  assert.ok(filter.data.items.every((row: { abcClass: string; temperatureClass: string }) => row.abcClass === 'A' && row.temperatureClass === 'Ambient'));
  assert.equal((await e.request('webhooks/subscriptions', 'POST', { url: 'https://example.com' })).status, 403);
  assert.equal((await e.request('not-a-real-route')).status, 404);
  await e.store.transact(state => { Object.assign(state, createSeed()); });
  assert.equal((await e.request('auth/me')).status, 401);
});

test('inactivity rotates only expired state and invalidates sessions at the boundary', () => {
  const state = createSeed(1000), epoch = state.epoch;
  assert.equal(expireIdleState(state, 1000 + IDLE_MS - 1), false);
  assert.equal(state.epoch, epoch);
  assert.equal(expireIdleState(state, 1000 + IDLE_MS), true);
  assert.notEqual(state.epoch, epoch);
  assert.equal(state.activityAt, 1000 + IDLE_MS);
});

test('all demo screen responses obey the published OpenAPI schemas', async () => {
  const document = JSON.parse(readFileSync(new URL('../../../openapi.json', import.meta.url), 'utf8'));
  const e = environment(); await e.login('admin');
  function validate(value: unknown, schema: Record<string, any>, at = 'response'): void {
    if (schema.$ref) return validate(value, document.components.schemas[schema.$ref.split('/').at(-1)], at);
    if (schema.oneOf) {
      assert.ok(schema.oneOf.some((candidate: Record<string, any>) => { try { validate(value, candidate, at); return true; } catch { return false; } }), at);
      return;
    }
    if (schema.type) {
      const types = Array.isArray(schema.type) ? schema.type : [schema.type];
      const type = value === null ? 'null' : Array.isArray(value) ? 'array' : typeof value;
      assert.ok(types.includes(type) || (types.includes('integer') && Number.isInteger(value)), `${at}: ${type} expected ${types}`);
    }
    if (value === null) return;
    if (schema.enum) assert.ok(schema.enum.includes(value), `${at}: enum ${value}`);
    if (Array.isArray(value)) value.forEach((item, i) => validate(item, schema.items, `${at}[${i}]`));
    if (typeof value === 'object' && value) {
      for (const key of schema.required ?? []) assert.ok(Object.hasOwn(value, key), `${at}.${key} is required`);
      for (const [key, child] of Object.entries(value)) if (schema.properties?.[key]) validate(child, schema.properties[key], `${at}.${key}`);
    }
  }
  const routes = ['auth/me', 'auth/service-accounts', 'warehouses', 'zones', 'items', 'locations', 'inventory/balances',
    'inventory/movements', 'inventory/accuracy', 'inventory/zone-density', 'inventory/reconciliation/history',
    'inventory/reservations/stranded', 'work/tasks', 'work/tours', 'work/statistics', 'work/throughput',
    'inbound/asns', 'outbound/orders', 'outbound/waves', 'counting/plans', 'counting/schedules',
    'rules/sets', 'replay/runs', 'replenishment/pick-faces', 'webhooks/subscriptions', 'webhooks/triggers'];
  for (const route of routes) {
    const response = await e.request(route);
    assert.equal(response.status, 200, route);
    validate(response.data, document.paths[`/api/v1/${route}`].get.responses['200'].content['application/json'].schema, route);
  }
});

test('invalid stock operations roll back and forged sessions never authenticate', async () => {
  const e = environment(); await e.login();
  const before = structuredClone(e.state);
  const result = await e.request('inbound/receipts', 'POST', { sku: 'SKU-10001', quantity: -1, toLocationCode: 'DOCK-01' }, 'bad-qty');
  assert.equal(result.status, 400);
  assert.deepEqual(e.state, before);
  const transport = createDemoTransport(e.store);
  assert.equal((await transport(new Request('https://demo.invalid/api/v1/auth/me', { headers: { Authorization: 'Bearer __proto__' } }))).status, 401);
});

test('stock history and mine filter respect the requested snapshot and signed-in operator', async () => {
  const e = environment(); await e.login();
  const past = await e.request(`inventory/balances?asOf=${encodeURIComponent(new Date(0).toISOString())}`);
  assert.equal(past.data.total, 0);
  assert.equal(past.data.asOf, new Date(0).toISOString());
  const task = e.state.tasks.find(t => t.status === 'Ready')!;
  await e.request(`work/tasks/${task.id}/claim`, 'POST');
  await e.login('supervisor');
  assert.equal((await e.request('work/tasks?mine=true&status=Assigned')).data.total, 0);
});

test('pick tour claims are idempotent and releasing returns the remaining jobs to the queue', async () => {
  const e = environment(); await e.login('supervisor');
  const order = e.state.orders[0]!;
  await e.request(`outbound/orders/${order.id}/release`, 'POST', {}, 'release');
  await e.login();
  const first = await e.request('work/tours/claim-next', 'POST', { maxStops: 12 }, 'tour-1');
  const retry = await e.request('work/tours/claim-next', 'POST', { maxStops: 12 }, 'tour-1');
  assert.deepEqual(first.data, retry.data);
  assert.equal((await e.request(`work/tours/${first.data.id}/release`, 'POST', undefined, 'release-tour')).status, 204);
  assert.equal(e.state.tasks.find(t => t.id === first.data.stops[0].id)?.status, 'Ready');
});

test('opening another tab or reloading preserves the current queue, tokens, language, and theme', () => {
  const storage: Record<string, unknown> = { 'countflow.demo.current.handheld.session': 'token',
    'countflow.demo.old.handheld.session': 'stale', 'countflow.handheld.session': 'real-token',
    theme: 'dark', language: 'tr', removeItem(key: string) { delete storage[key]; } };
  const deleted: string[] = [];
  cleanOldDemoStorage('current', 'current', [storage as unknown as Storage], name => deleted.push(name));
  assert.deepEqual(deleted, []);
  assert.equal(storage['countflow.demo.current.handheld.session'], 'token');
  assert.equal(storage['countflow.handheld.session'], 'real-token');
  assert.equal(storage.theme, 'dark'); assert.equal(storage.language, 'tr');
  assert.equal(storage['countflow.demo.old.handheld.session'], undefined);
  cleanOldDemoStorage('current', 'old', [storage as unknown as Storage], name => deleted.push(name));
  assert.deepEqual(deleted, ['countflow.demo.old.handheld']);
});
