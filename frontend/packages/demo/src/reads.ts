import { WAREHOUSE_ID, type DemoState, type S } from './data.js';
import { DemoError, found, requireThat } from './actions.js';

const ruleId = '00000000-0000-4000-8000-000000009001';
const replayId = '00000000-0000-4000-8000-000000009002';
export function page<T>(items: T[], query: URLSearchParams) {
  const number = Number(query.get('page')) || 1, size = Number(query.get('pageSize')) || 25;
  const page = Math.max(1, Math.floor(number)), pageSize = Math.max(1, Math.min(100, Math.floor(size)));
  return { items: items.slice((page - 1) * pageSize, page * pageSize), total: items.length, page, pageSize };
}

function filtered<T extends object>(rows: T[], query: URLSearchParams, keys: Record<string, keyof T> = {}) {
  return rows.filter(row => {
    for (const [queryKey, field] of Object.entries(keys)) {
      const value = query.get(queryKey);
      if (value && String(row[field]).toLowerCase() !== value.toLowerCase()) return false;
    }
    const search = query.get('search')?.toLowerCase();
    return !search || JSON.stringify(row).toLowerCase().includes(search);
  });
}

export function reconciliation(s: DemoState): S['ReconciliationResponse'] {
  const replayed = new Map<string, number>();
  const add = (sku: string, code: string, quantity: number) => {
    if (['SUPPLIER', 'CUSTOMER', 'ADJUSTMENT'].includes(code)) return;
    const key = `${sku}|${code}`; replayed.set(key, (replayed.get(key) ?? 0) + quantity);
  };
  for (const m of s.movements) { add(m.sku, m.fromLocationCode, -m.quantity); add(m.sku, m.toLocationCode, m.quantity); }
  const drift: S['BalanceDriftResponse'][] = [];
  for (const b of s.balances) {
    const quantity = replayed.get(`${b.sku}|${b.locationCode}`) ?? 0;
    if (Math.abs(b.quantity - quantity) > 0.00001) drift.push({ sku: b.sku, lotNumber: null, locationCode: b.locationCode,
      status: b.status, projected: b.quantity, replayed: quantity, difference: b.quantity - quantity, lastMovementSequence: s.movements.at(-1)?.sequence ?? 0 });
  }
  return { runAt: new Date().toISOString(), movementsReplayed: s.movements.length, holdingsChecked: s.balances.length,
    ledgerSum: 0, isClean: drift.length === 0, drift };
}

export function read(s: DemoState, path: string, q: URLSearchParams, user: S['UserInfo']): unknown {
  const stamp = new Date().toISOString();
  const windowDays = path === '/work/throughput' ? Math.min(90, Math.max(1, Number(q.get('days')) || 14)) : 30;
  const from = q.get('from') ?? new Date(Date.now() - windowDays * 86400000).toISOString(), to = q.get('to') ?? stamp;
  if (path === '/auth/me') return user;
  if (path.startsWith('/auth/service-accounts')) {
    if (!user.roles.includes('Admin')) throw new DemoError(403, 'demo.forbidden');
    return path.endsWith('/keys') ? [] : [{ id: ruleId, name: 'warehouse-connector', displayName: 'Warehouse connector',
      roles: ['Operator'], isActive: true, createdAt: s.movements[0]!.occurredAt, activeKeyCount: 0 } satisfies S['ServiceAccountResponse']];
  }
  if (path === '/warehouses') return s.warehouses;
  if (path === '/zones') return s.zones;
  if (path === '/locations') return page(filtered(s.locations, q, { type: 'type', pickable: 'isPickable' }), q);
  if (path === '/items') return page(filtered(s.items, q, { abcClass: 'abcClass', temperature: 'temperatureClass' }), q);
  if (path.startsWith('/items/')) return found(s.items.find(i => i.sku === decodeURIComponent(path.slice(7))));
  if (path === '/inventory/balances') {
    const asOf = q.get('asOf');
    let balances = s.balances;
    let asOfSequence: number | null = null;
    if (asOf) {
      requireThat(Number.isFinite(Date.parse(asOf)));
      const replay = new Map<string, S['StockBalanceResponse']>();
      for (const balance of s.balances) replay.set(`${balance.sku}|${balance.locationCode}`, { ...balance, quantity: 0, reservedQuantity: 0, availableQuantity: 0 });
      const apply = (sku: string, code: string, amount: number) => {
        if (['SUPPLIER', 'CUSTOMER', 'ADJUSTMENT'].includes(code)) return;
        const key = `${sku}|${code}`;
        if (!replay.has(key)) {
          const item = found(s.items.find(i => i.sku === sku));
          replay.set(key, { sku, locationCode: code, description: item.description, baseUom: item.baseUom,
            lotNumber: null, expiryDate: null, sscc: null, status: 'Available', isAllocatable: true,
            quantity: 0, reservedQuantity: 0, availableQuantity: 0 });
        }
        const balance = replay.get(key)!; balance.quantity += amount; balance.availableQuantity = balance.quantity;
      };
      asOfSequence = 0;
      for (const m of s.movements.filter(m => Date.parse(m.occurredAt) <= Date.parse(asOf))) {
        apply(m.sku, m.fromLocationCode, -m.quantity); apply(m.sku, m.toLocationCode, m.quantity);
        asOfSequence = Math.max(asOfSequence, m.sequence);
      }
      balances = [...replay.values()];
    }
    return { ...page(filtered(balances.filter(b => q.get('includeEmpty') === 'true' || b.quantity > 0), q, {
      sku: 'sku', location: 'locationCode', locationCode: 'locationCode', status: 'status', lot: 'lotNumber', sscc: 'sscc',
    }), q), asOf: asOf ? new Date(asOf).toISOString() : null, asOfSequence } satisfies S['StockEnquiryResponse'];
  }
  if (path === '/inventory/movements') return page(filtered([...s.movements].reverse().filter(m =>
    (!q.get('from') || m.occurredAt >= from) && (!q.get('to') || m.occurredAt <= to)
    && (!q.get('location') || [m.fromLocationCode, m.toLocationCode].includes(q.get('location')!))), q, { sku: 'sku', type: 'type' }), q);
  if (path === '/inventory/accuracy') {
    const counts = s.counts.filter(c => c.at >= from && c.at <= to);
    const slice = (key: string, rows: typeof counts): S['AccuracySlice'] => ({ key, counted: rows.length,
      accurate: rows.filter(c => c.variance === 0).length, netVariance: rows.reduce((n, c) => n + c.variance, 0),
      absoluteVariance: rows.reduce((n, c) => n + Math.abs(c.variance), 0), accuracyRate: rows.length ? rows.filter(c => c.variance === 0).length / rows.length : null });
    return { from, to, overall: slice('all', counts), byZone: [slice('AMB', counts)],
      byClass: ['A', 'B', 'C'].map(key => slice(key, counts.filter(c => c.abcClass === key))) } satisfies S['CountAccuracyResponse'];
  }
  if (path === '/inventory/zone-density') {
    const locations = s.locations.filter(l => l.zoneCode === 'AMB');
    const occupied = new Set(s.balances.filter(b => b.quantity > 0 && locations.some(l => l.code === b.locationCode)).map(b => b.locationCode)).size;
    const zone: S['ZoneDensitySlice'] = { zoneId: s.zones[0]!.id, areaCode: 'STOR', zoneCode: 'AMB', locations: locations.length,
      occupied, occupancyRate: occupied / locations.length, knownCapacityLocations: locations.length,
      cubeUtilization: s.balances.reduce((n, b) => n + b.quantity * 0.002, 0) / (locations.length * 2) };
    return { observedAt: stamp, overall: zone, zones: [zone] } satisfies S['ZoneDensityResponse'];
  }
  if (path === '/inventory/reconciliation/history') return [{ id: ruleId, runAt: stamp, trigger: 'Manual',
    movementsReplayed: s.movements.length, holdingsChecked: s.balances.length, ledgerSum: 0,
    driftCount: 0, worstDrift: 0, elapsedMilliseconds: 8, isClean: reconciliation(s).isClean } satisfies S['ReconciliationRunResponse']];
  if (path === '/inventory/reservations/stranded') return [];
  if (path === '/work/tasks') return page(filtered(s.tasks.filter(t => q.get('mine') !== 'true' || t.assignedToUserName === user.userName), q,
    { kind: 'kind', status: 'status', assignedTo: 'assignedToUserName', sku: 'sku' }), q);
  if (path.startsWith('/work/tasks/')) {
    const task = found(s.tasks.find(t => t.id === path.split('/')[3]));
    return { task, decision: s.traces.find(t => t.id === task.decisionTraceId) ?? null } satisfies S['WorkTaskDetailResponse'];
  }
  if (path === '/work/tours') return page(s.tours, q);
  if (path.startsWith('/work/tours/')) return found(s.tours.find(t => t.id === path.split('/')[3]));
  if (path === '/work/statistics' || path === '/work/throughput') {
    const kinds: S['WorkTaskKind'][] = ['Putaway', 'Pick', 'CycleCount'];
    const tasks = s.tasks.filter(t => !t.completedAt || (t.completedAt >= from && t.completedAt <= to));
    const byKind = kinds.map(kind => ({ kind, completed: tasks.filter(t => t.kind === kind && t.status === 'Completed').length,
      overridden: 0, cancelled: 0, open: tasks.filter(t => t.kind === kind && t.status !== 'Completed').length, overrideRate: 0 }));
    if (path.endsWith('statistics')) return { from, to, byKind } satisfies S['WorkStatisticsResponse'];
    const days = Math.min(90, Math.max(1, Number(q.get('days')) || 14));
    return { from: new Date(Date.now() - days * 86400000).toISOString(), to, observedAt: stamp,
      completed: byKind.reduce((n, k) => n + k.completed, 0), ready: tasks.filter(t => t.status === 'Ready').length,
      assigned: tasks.filter(t => t.status === 'Assigned').length, byKind,
      days: Array.from({ length: days }, (_, i) => { const date = new Date(Date.now() - (days - i - 1) * 86400000).toISOString().slice(0, 10);
        return { date, completed: tasks.filter(t => t.completedAt?.startsWith(date)).length }; }),
    } satisfies S['WorkThroughputResponse'];
  }
  if (path === '/inbound/asns') return page(filtered(s.asns, q, { status: 'status' }), q);
  if (path.startsWith('/inbound/asns/')) return found(s.asns.find(a => a.reference === decodeURIComponent(path.split('/')[3]!)));
  if (path === '/outbound/orders') return page(filtered(s.orders, q, { status: 'status' }), q);
  if (path.startsWith('/outbound/orders/')) return found(s.orders.find(o => o.id === path.split('/')[3]));
  if (path === '/outbound/waves') return [{ id: ruleId, reference: 'WAVE-2026-001', status: 'Released', notes: null,
    createdAt: stamp, releasedAt: stamp, releasedByUserName: 'supervisor', orders: s.orders.slice(0, 3) } satisfies S['WaveResponse']];
  if (path === '/counting/plans') return page(filtered(s.plans, q, { status: 'status' }), q);
  if (path.startsWith('/counting/plans/')) return found(s.plans.find(p => p.id === path.split('/')[3]));
  if (path === '/counting/schedules') return [{ warehouseId: WAREHOUSE_ID, warehouseCode: 'WH1', enabled: false,
    intervalMinutes: 1440, maxTasks: 20, nextRunAt: null, lastRunAt: null, lastPlanId: s.plans.at(-1)?.id ?? null,
    isPending: false, lastErrorCode: null } satisfies S['CountScheduleResponse']];
  if (path === '/rules/sets') return {
    items: (['Putaway', 'Allocation', 'CycleCount'] as const).filter(kind => !q.get('kind') || q.get('kind') === kind).map((kind, i): S['RuleSetResponse'] => ({
      id: `${ruleId.slice(0, -1)}${i + 1}`, kind, name: `Standard ${kind}`, version: 1, status: 'Active', warehouseId: null,
      notes: null, createdAt: s.movements[0]!.occurredAt, activatedAt: stamp,
      rules: [{ code: 'putaway.travel_distance', role: 'Preference', order: 1, weight: 1, parameters: null, isEnabled: true }],
    })),
    vocabulary: (['Putaway', 'Allocation', 'CycleCount'] as const).map(kind => ({ kind, constraints: [], preferences: ['putaway.travel_distance'] })),
  } satisfies S['RuleSetsResponse'];
  if (path.startsWith('/rules/traces/')) return found(s.traces.find(t => t.id === path.split('/')[3]));
  if (path.startsWith('/replay/runs')) {
    const trace = s.traces[0]!;
    const result: S['ReplayRunResponse'] = { id: replayId, reference: 'RPL-2026-0001', warehouseId: WAREHOUSE_ID,
      kind: (q.get('kind') as S['DecisionKind']) ?? 'Putaway', candidateRuleSetId: ruleId, candidateRuleSetName: 'Standard putaway', candidateRuleSetVersion: 1,
      baselineRuleSetName: 'Standard putaway', baselineRuleSetVersion: 1, from, to,
      replayedCount: 1, exactCount: 1, approximateCount: 0, unreplayableCount: 0, changedCount: 0, reorderedCount: 0,
      gainedCount: 0, lostCount: 0, elapsedMilliseconds: 12, influence: [], requestedByUserName: 'supervisor', createdAt: stamp, notes: null,
      decisions: [{ decisionTraceId: trace.id, decidedAt: trace.decidedAt, subject: 'SKU-10001', fidelity: 'Exact', fidelityNote: null,
        outcome: 'Unchanged', recordedKey: trace.selectedKey, baselineKey: trace.selectedKey, candidateKey: trace.selectedKey,
        baselineScore: 0.9, candidateScore: 0.9, candidateRankUnderBaseline: 1, baselineRankUnderCandidate: 1,
        consideredCount: 3, baselineAdmissibleCount: 2, candidateAdmissibleCount: 2, baselineInfluence: [], candidateInfluence: [] }],
    };
    if (q.get('outcome') && q.get('outcome') !== 'Unchanged') result.decisions = [];
    if (path === '/replay/runs') return page([result], q);
    if (path.endsWith(replayId)) return result;
    throw new DemoError(404, 'demo.not_found');
  }
  if (path === '/replenishment/pick-faces') return page(s.balances.filter(b => b.quantity > 0).slice(0, 10).map((b): S['PickFacePolicyResponse'] => ({
    locationId: found(s.locations.find(l => l.code === b.locationCode)).id, locationCode: b.locationCode, warehouseCode: 'WH1',
    sku: b.sku, baseUom: 'EA', minimum: 12, maximum: 120, enabled: false, onHandQuantity: b.quantity,
    availableQuantity: b.availableQuantity ?? b.quantity, incomingQuantity: 0, lastCheckedAt: stamp,
    shortfallQuantity: 0, pendingPlanId: null, lastPlanId: null, lastDecisionTraceId: null, lastErrorCode: null,
  })), q);
  if (path === '/webhooks/subscriptions') return [{ id: ruleId, name: 'Inventory feed', url: 'https://example.com/inventory',
    verified: false, enabled: false, cursor: s.movements.at(-1)?.sequence ?? 0, pendingDeliveryId: null } satisfies S['WebhookResponse']];
  if (path === '/webhooks/triggers') return [{ id: ruleId, name: 'Inbound notification', enabled: false, pendingRequests: 0, lastFinishedAt: null } satisfies S['TriggerResponse']];
  if (/^\/webhooks\/subscriptions\/[^/]+\/deliveries$/.test(path)) return [];
  throw new DemoError(404, 'demo.not_found');
}
