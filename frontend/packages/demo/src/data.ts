import type { components } from '@countflow/api-client';

export type S = components['schemas'];
export const WAREHOUSE_ID = '00000000-0000-4000-8000-000000000001';
export const uid = () => crypto.randomUUID();
const id = (n: number) => `00000000-0000-4000-8000-${String(n).padStart(12, '0')}`;

export interface DemoState {
  version: 1;
  epoch: string;
  activityAt: number;
  sessions: Record<string, { user: S['UserInfo']; refreshToken: string }>;
  idempotency: Record<string, { body: string; value: unknown }>;
  warehouses: S['WarehouseResponse'][];
  zones: S['ZoneResponse'][];
  items: S['ItemResponse'][];
  locations: S['LocationResponse'][];
  balances: S['StockBalanceResponse'][];
  movements: S['StockMovementResponse'][];
  tasks: S['WorkTaskResponse'][];
  asns: S['AsnResponse'][];
  orders: S['SalesOrderResponse'][];
  plans: S['CountPlanResponse'][];
  tours: S['PickTourResponse'][];
  counts: { sku: string; zone: string; abcClass: string; at: string; variance: number }[];
  traces: S['DecisionTraceView'][];
}

export function newTask(item: S['ItemResponse'], kind: S['WorkTaskKind'], from: string, to: string, quantity: number, reference: string | null = null): S['WorkTaskResponse'] {
  return {
    id: uid(), kind, status: 'Ready', priority: 50, sku: item.sku, description: item.description,
    baseUom: item.baseUom, quantity, lotNumber: null, expiryDate: null, sscc: null,
    fromLocationCode: from, suggestedLocationCode: to, decisionTraceId: null,
    condition: 'Available', sourceReference: reference, createdAt: new Date().toISOString(),
    assignedToUserName: null, completedAt: null, completedAtLocationCode: null,
    wasOverridden: false, overrideReason: null, countedQuantity: null, pickTourId: null, tourSequence: null,
    pendingReceiptCount: 0, putawayRevision: 0, putawayAdviceStale: false, completionPending: false,
  };
}

export function newTrace(kind: S['DecisionKind'], selectedKey: string, now = new Date().toISOString()): S['DecisionTraceView'] {
  return {
    id: uid(), kind, ruleSetName: `Standard ${kind}`, ruleSetVersion: 1, decidedAt: now,
    consideredCount: 3, admissibleCount: 2, selectedId: uid(), selectedKey,
    candidates: [selectedKey, 'WH1-AMB-A02-01-01-A', 'WH1-AMB-A03-01-01-A'].map((key, i) => ({
      candidateId: uid(), candidateKey: key, rank: i + 1, isAdmissible: i < 2,
      score: i < 2 ? 0.9 - i * 0.3 : 0, excludedBy: i === 2 ? { code: 'putaway.not_active', args: {} } : null,
      scores: i < 2 ? [{ ruleCode: 'putaway.travel_distance', weight: 1, value: 0.9 - i * 0.3,
        contribution: 0.9 - i * 0.3, reason: { code: 'putaway.travel_distance', args: { distance: String(10 + i * 20) } } }] : [],
    })),
  };
}

/** Synthetic fixtures only: never a snapshot of customer data or real credentials. */
export function createSeed(now = Date.now()): DemoState {
  const stamp = new Date(now).toISOString();
  const names = ['Olive oil 1 L', 'Roasted coffee 250 g', 'Pasta 500 g', 'Green tea 100 g', 'Ceramic mug', 'Cotton towel', 'Storage box', 'Notebook A5'];
  const items: S['ItemResponse'][] = Array.from({ length: 80 }, (_, i) => ({
    id: id(100 + i), sku: `SKU-${10001 + i}`, description: `${names[i % names.length]} · ${i + 1}`,
    baseUom: 'EA', abcClass: ['A', 'B', 'C'][i % 3]!, temperatureClass: 'Ambient',
    isLotTracked: false, isSerialTracked: false, isExpiryTracked: false, shelfLifeDays: null,
    isHazardous: false, isActive: true,
    packConfigurations: [{ level: 'Each', baseUnitQuantity: 1, gtin: `0869000${String(100000 + i).padStart(6, '0')}0`,
      lengthMm: 100, widthMm: 100, heightMm: 200, grossWeightKg: 1, volumeM3: 0.002,
      isReceivingUnit: true, isPickUnit: true }],
  }));
  const codes = ['SUPPLIER', 'CUSTOMER', 'ADJUSTMENT', 'DOCK-01', 'PACK-01',
    ...Array.from({ length: 120 }, (_, i) => `WH1-AMB-A${String(Math.floor(i / 10) + 1).padStart(2, '0')}-${String(i % 10 + 1).padStart(2, '0')}-01-A`)];
  const locations: S['LocationResponse'][] = codes.map((code, i) => ({
    id: id(1000 + i), code, type: ['Supplier', 'Customer', 'Adjustment', 'ReceivingDock', 'Packing'][i] ?? (i % 2 ? 'Bulk' : 'PickFace'),
    zoneCode: i >= 5 ? 'AMB' : null, areaCode: i >= 5 ? 'STOR' : 'DOCK',
    aisle: i >= 5 ? String(Math.floor((i - 5) / 10) + 1).padStart(2, '0') : null,
    bay: i >= 5 ? String((i - 5) % 10 + 1).padStart(2, '0') : null,
    level: i >= 5 ? '01' : null, position: i >= 5 ? 'A' : null, pickSequence: i,
    maxVolumeM3: i >= 5 ? 2 : null, maxWeightKg: i >= 5 ? 500 : null,
    isPickable: i >= 5, isReplenishable: i >= 5, isActive: true,
  }));
  const state: DemoState = {
    version: 1, epoch: uid(), activityAt: now, sessions: {}, idempotency: {}, items, locations,
    warehouses: [{ id: WAREHOUSE_ID, code: 'WH1', name: 'Countflow Distribution Centre', city: 'Istanbul', countryCode: 'TR', isActive: true }],
    zones: [{ id: id(2), code: 'AMB', name: 'Ambient storage', areaCode: 'STOR', temperatureClass: 'Ambient',
      pickSequence: 1, pickRouteStrategy: 'Return', hasRearCrossAisle: false, isActive: true, aisles: 12, bays: 10,
      routeOptions: [{ code: 'Return', isSupported: true, unsupportedBecause: null, requiresRearCrossAisle: false, hasRequiredGeometry: true }] }],
    balances: [], movements: [], tasks: [], asns: [], orders: [], plans: [], tours: [], counts: [], traces: [],
  };
  for (const [i, item] of items.entries()) {
    const quantity = 48 + i % 7 * 12;
    const locationCode = codes[5 + i]!;
    state.balances.push({ sku: item.sku, description: item.description, baseUom: item.baseUom,
      lotNumber: null, expiryDate: null, locationCode, sscc: null, status: 'Available', quantity,
      isAllocatable: true, reservedQuantity: 0, availableQuantity: quantity });
    state.movements.push({ id: id(2000 + i), sequence: state.movements.length + 1, occurredAt: new Date(now - (i % 14 + 1) * 86400000).toISOString(),
      sku: item.sku, lotNumber: null, quantity, fromLocationCode: 'SUPPLIER', toLocationCode: locationCode,
      fromSscc: null, toSscc: null, fromStatus: 'Available', toStatus: 'Available', type: 'Receipt', reason: 'PurchaseOrderReceipt',
      userName: 'operator', reversesMovementId: null, isReversed: false, taskId: null, decisionTraceId: null, notes: null });
    const receipt = state.movements.at(-1)!;
    receipt.toLocationCode = 'DOCK-01';
    const completed = newTask(item, 'Putaway', 'DOCK-01', locationCode, quantity);
    Object.assign(completed, { status: 'Completed', createdAt: receipt.occurredAt, completedAt: receipt.occurredAt,
      completedAtLocationCode: locationCode, assignedToUserName: 'operator' });
    state.tasks.push(completed);
    state.movements.push({ ...receipt, id: uid(), sequence: state.movements.length + 1,
      fromLocationCode: 'DOCK-01', toLocationCode: locationCode, type: 'Putaway', reason: 'PutawayComplete', taskId: completed.id });
    if (i < 8) {
      const task = newTask(item, 'CycleCount', locationCode, locationCode, quantity);
      const trace = newTrace('CycleCount', locationCode, stamp);
      task.decisionTraceId = trace.id;
      state.tasks.push(task); state.traces.push(trace);
    }
    if (i < 12) {
      state.asns.push({ id: id(3000 + i), reference: `ASN-2026-${String(i + 1).padStart(4, '0')}`, status: 'Expected',
        supplierName: ['Marmara Trading', 'Aegean Foods', 'Atlas Supplies'][i % 3]!, expectedAt: stamp,
        carrierReference: `TR-${1000 + i}`, notes: null, createdAt: stamp, closedAt: null,
        lines: [{ lineNumber: 1, sku: item.sku, description: item.description, baseUom: 'EA', expectedQuantity: 24,
          receivedQuantity: 0, outstandingQuantity: 24, expectedLotNumber: null, expectedExpiryDate: null }] });
      state.orders.push({ id: id(4000 + i), reference: `SO-2026-${String(i + 1).padStart(4, '0')}`, customerName: ['North Market', 'City Stores', 'Coast Retail'][i % 3]!,
        status: 'New', stagingLocationCode: 'PACK-01', shipBy: new Date(now + 86400000).toISOString().slice(0, 10),
        priority: 50, waveId: null, notes: null, createdAt: stamp, releasedAt: null, packedAt: null, despatchedAt: null,
        lines: [{ id: id(5000 + i), lineNumber: 1, sku: item.sku, description: item.description, baseUom: 'EA', orderedQuantity: 6,
          allocatedQuantity: 0, outstandingQuantity: 6, pickedQuantity: 0, returnedQuantity: 0, requiredLotNumber: null, allocations: [] }] });
    }
    state.counts.push({ sku: item.sku, zone: 'AMB', abcClass: item.abcClass,
      at: new Date(now - (i % 7 + 1) * 86400000).toISOString(), variance: 0 });
  }
  const countTasks = state.tasks.filter(t => t.kind === 'CycleCount');
  state.plans.push({ id: id(6000), reference: 'CNT-0001', status: 'Open', warehouseCode: 'WH1', requestedTasks: countTasks.length,
    raisedCount: countTasks.length, completedCount: 0, varianceCount: 0, decisionTraceId: countTasks[0]!.decisionTraceId!,
    createdAt: stamp, createdByUserName: 'supervisor', completedAt: null, notes: null,
    lines: countTasks.map((task, i) => ({ id: id(6100 + i), sku: task.sku, lotNumber: null,
      locationCode: task.fromLocationCode, condition: 'Available', expectedQuantity: task.quantity, rank: i + 1, score: 0.9,
      workTaskId: task.id, state: 'Ready', countedQuantity: null, variance: null, countedAt: null, countedByUserName: null })),
  });
  return state;
}
