import { newTask, newTrace, uid, WAREHOUSE_ID, type DemoState, type S } from './data.js';

export class DemoError extends Error {
  readonly status: number;
  readonly code: string;
  constructor(status: number, code: string) { super(code); this.status = status; this.code = code; }
}
export function requireThat(condition: unknown, status = 400, code = 'demo.invalid_request'): asserts condition {
  if (!condition) throw new DemoError(status, code);
}
export const found = <T>(value: T | undefined): T => {
  requireThat(value !== undefined, 404, 'demo.not_found'); return value;
};
const quantity = (value: unknown) => {
  requireThat(typeof value === 'number' && Number.isFinite(value) && value > 0 && value <= 10000);
  return value;
};
const balanceAt = (s: DemoState, sku: string, code: string) => s.balances.find(b => b.sku === sku && b.locationCode === code);
const external = (code: string) => ['SUPPLIER', 'CUSTOMER', 'ADJUSTMENT'].includes(code);
const now = () => new Date().toISOString();

/** Only the supported demo transitions live here; this is not a second rules engine. */
export function move(s: DemoState, user: S['UserInfo'], sku: string, amount: number, from: string, to: string,
  type: S['MovementType'], reason: S['MovementReason'], task: S['WorkTaskResponse'] | null = null): S['PostedMovement'] {
  const item = found(s.items.find(i => i.sku === sku));
  requireThat(s.locations.some(l => l.code === from) && s.locations.some(l => l.code === to));
  const source = balanceAt(s, sku, from);
  requireThat(external(from) || (source && source.quantity >= amount), 409, 'inventory.insufficient_stock');
  if (source && !external(from)) {
    source.quantity -= amount;
    source.availableQuantity = source.quantity - (source.reservedQuantity ?? 0);
  }
  let target = balanceAt(s, sku, to);
  if (!external(to)) {
    if (!target) {
      target = { sku, description: item.description, baseUom: item.baseUom, lotNumber: null, expiryDate: null,
        locationCode: to, sscc: null, status: 'Available', quantity: 0, isAllocatable: true, reservedQuantity: 0, availableQuantity: 0 };
      s.balances.push(target);
    }
    target.quantity += amount;
    target.availableQuantity = target.quantity - (target.reservedQuantity ?? 0);
  }
  const movement: S['StockMovementResponse'] = {
    id: uid(), sequence: (s.movements.at(-1)?.sequence ?? 0) + 1, occurredAt: now(), sku, lotNumber: null,
    quantity: amount, fromLocationCode: from, toLocationCode: to, fromSscc: null, toSscc: null,
    fromStatus: 'Available', toStatus: 'Available', type, reason, userName: user.userName,
    reversesMovementId: null, isReversed: false, taskId: task?.id ?? null, decisionTraceId: task?.decisionTraceId ?? null, notes: null,
  };
  s.movements.push(movement);
  return { movementId: movement.id, sequence: movement.sequence, occurredAt: movement.occurredAt,
    quantity: amount, lotId: null, licensePlateId: null, fromQuantityAfter: source?.quantity ?? 0,
    toQuantityAfter: target?.quantity ?? 0, wasAlreadyPosted: false };
}

function claim(task: S['WorkTaskResponse'], user: S['UserInfo']) {
  requireThat(task.status === 'Ready' || (task.status === 'Assigned' && task.assignedToUserName === user.userName), 409, 'work.already_claimed');
  task.status = 'Assigned'; task.assignedToUserName = user.userName;
  return task;
}

export function perform(s: DemoState, path: string, body: Record<string, unknown>, user: S['UserInfo']): unknown {
  const supervisor = () => requireThat(user.roles.includes('Admin') || user.roles.includes('Supervisor'), 403, 'demo.forbidden');
  if (path === '/inbound/receipts') {
    const sku = String(body.sku ?? '').trim().toUpperCase();
    const item = found(s.items.find(i => i.sku === sku));
    const dock = found(s.locations.find(l => l.code === body.toLocationCode && l.type === 'ReceivingDock'));
    const amount = quantity(body.quantity);
    // Advanced traceability is deliberately not silently simulated.
    requireThat(!body.lotNumber && !body.expiryDate && !body.sscc, 403, 'demo.unavailable');
    const asn = body.asnReference ? found(s.asns.find(a => a.reference === body.asnReference)) : undefined;
    const line = asn ? found(asn.lines.find(l => l.sku === sku)) : undefined;
    if (line) {
      requireThat(asn?.status !== 'Closed' && asn?.status !== 'Cancelled' && amount <= line.outstandingQuantity, 409, 'demo.invalid_transition');
      line.receivedQuantity += amount; line.outstandingQuantity -= amount;
      asn!.status = asn!.lines.every(l => l.outstandingQuantity === 0) ? 'Complete' : 'Receiving';
    }
    const destination = found(s.locations.find(l => l.type === 'Bulk' && l.isActive));
    const task = newTask(item, 'Putaway', dock.code, destination.code, amount, asn?.reference ?? null);
    const trace = newTrace('Putaway', destination.code);
    task.decisionTraceId = trace.id;
    s.tasks.push(task); s.traces.push(trace);
    const receipt = move(s, user, sku, amount, 'SUPPLIER', dock.code, 'Receipt', 'PurchaseOrderReceipt', task);
    return { receipt, sku, description: item.description, toLocationCode: dock.code, lotNumber: null, sscc: null,
      workTaskId: task.id, suggestedLocationCode: destination.code, decisionTraceId: trace.id,
      consideredCount: trace.consideredCount, admissibleCount: trace.admissibleCount,
      putawayAdviceStale: false, putawayRevision: 0, completionPending: false,
      asnLine: line && asn ? { reference: asn.reference, lineNumber: line.lineNumber, expectedQuantity: line.expectedQuantity,
        receivedQuantity: line.receivedQuantity, outstandingQuantity: line.outstandingQuantity, asnStatus: asn.status } : null,
    } satisfies S['DockReceiptResponse'];
  }
  if (path === '/work/tasks/claim-next') {
    const task = s.tasks.find(t => t.status === 'Ready' && (!body.kind || t.kind === body.kind) && !t.pickTourId);
    if (!task) return null;
    claim(task, user);
    return { task, decision: s.traces.find(t => t.id === task.decisionTraceId) ?? null } satisfies S['WorkTaskDetailResponse'];
  }
  const taskMatch = path.match(/^\/work\/tasks\/([^/]+)\/(claim|complete)$/);
  if (taskMatch) {
    const task = found(s.tasks.find(t => t.id === taskMatch[1]));
    if (taskMatch[2] === 'claim') {
      claim(task, user);
      return { task, decision: s.traces.find(t => t.id === task.decisionTraceId) ?? null } satisfies S['WorkTaskDetailResponse'];
    }
    requireThat(task.status === 'Assigned' && task.assignedToUserName === user.userName, 409, 'work.not_assigned');
    requireThat(body.toLocationCode === task.suggestedLocationCode, 409, 'work.wrong_location');
    if (task.pickTourId) requireThat(body.pickTourId === task.pickTourId, 409, 'demo.invalid_transition');
    const movements: S['PostedMovement'][] = [];
    let count: S['CountOutcome'] | null = null;
    if (task.kind === 'CycleCount') {
      const counted = body.countedQuantity;
      requireThat(typeof counted === 'number' && Number.isFinite(counted) && counted >= 0 && counted <= 10000);
      const balance = found(balanceAt(s, task.sku, task.fromLocationCode));
      requireThat(counted >= (balance.reservedQuantity ?? 0), 409, 'inventory.insufficient_stock');
      const expected = balance.quantity, variance = counted - expected;
      if (variance) movements.push(move(s, user, task.sku, Math.abs(variance),
        variance > 0 ? 'ADJUSTMENT' : task.fromLocationCode, variance > 0 ? task.fromLocationCode : 'ADJUSTMENT',
        'CountAdjustment', 'CycleCountVariance', task));
      count = { countId: uid(), expectedQuantity: expected, countedQuantity: counted, variance,
        adjustment: movements[0] ?? null, wasAlreadyCounted: false };
      task.countedQuantity = counted;
      s.counts.push({ sku: task.sku, zone: 'AMB', abcClass: found(s.items.find(i => i.sku === task.sku)).abcClass, at: now(), variance });
      for (const plan of s.plans) {
        const line = plan.lines.find(l => l.workTaskId === task.id);
        if (!line) continue;
        Object.assign(line, { state: 'Completed', countedQuantity: counted, variance, countedAt: now(), countedByUserName: user.userName });
        plan.completedCount++; if (variance) plan.varianceCount++;
        if (plan.completedCount === plan.raisedCount) { plan.status = 'Completed'; plan.completedAt = now(); }
      }
    } else {
      requireThat(task.kind === 'Putaway' || task.kind === 'Pick', 403, 'demo.unavailable');
      if (task.kind === 'Pick') {
        const balance = found(balanceAt(s, task.sku, task.fromLocationCode));
        balance.reservedQuantity = Math.max(0, (balance.reservedQuantity ?? 0) - task.quantity);
      }
      movements.push(move(s, user, task.sku, task.quantity, task.fromLocationCode, String(body.toLocationCode),
        task.kind, task.kind === 'Pick' ? 'OrderPick' : 'PutawayComplete', task));
      if (task.kind === 'Pick') {
        const order = found(s.orders.find(o => o.reference === task.sourceReference));
        const line = found(order.lines.find(l => l.allocations.some(a => a.workTaskId === task.id)));
        line.pickedQuantity += task.quantity;
        const allocation = found(line.allocations.find(a => a.workTaskId === task.id));
        Object.assign(allocation, { taskState: 'Completed', pickedQuantity: task.quantity, pickedAtLocationCode: body.toLocationCode });
      }
    }
    Object.assign(task, { status: 'Completed', completedAt: now(), completedAtLocationCode: String(body.toLocationCode) });
    for (const tour of s.tours) {
      if (!tour.stops.some(t => t.id === task.id)) continue;
      tour.stops = tour.stops.map(t => t.id === task.id ? task : t);
      tour.remaining = tour.stops.filter(t => t.status !== 'Completed').length; tour.isComplete = tour.remaining === 0;
    }
    return { task, movements, count } satisfies S['CompleteWorkTaskResponse'];
  }
  const orderMatch = path.match(/^\/outbound\/orders\/([^/]+)\/(release|pack|despatch)$/);
  if (orderMatch) {
    supervisor();
    const order = found(s.orders.find(o => o.id === orderMatch[1]));
    if (orderMatch[2] === 'release') {
      requireThat(order.status === 'New', 409, 'demo.invalid_transition');
      for (const line of order.lines) {
        const source = found(s.balances.find(b => b.sku === line.sku && (b.availableQuantity ?? 0) >= line.orderedQuantity && s.locations.some(l => l.code === b.locationCode && l.isPickable)));
        const item = found(s.items.find(i => i.sku === line.sku));
        const task = newTask(item, 'Pick', source.locationCode, order.stagingLocationCode ?? 'PACK-01', line.orderedQuantity, order.reference);
        const trace = newTrace('Allocation', source.locationCode); task.decisionTraceId = trace.id;
        s.tasks.push(task); s.traces.push(trace);
        source.reservedQuantity = (source.reservedQuantity ?? 0) + task.quantity;
        source.availableQuantity = source.quantity - source.reservedQuantity;
        line.allocatedQuantity = task.quantity; line.outstandingQuantity = 0;
        line.allocations.push({ id: uid(), quantity: task.quantity, locationCode: source.locationCode, lotNumber: null,
          expiryDate: null, decisionTraceId: trace.id, workTaskId: task.id, taskState: 'Ready', pickedQuantity: 0, pickedAtLocationCode: null });
      }
      order.status = 'Released'; order.releasedAt = now();
      return { order, allocationsRaised: order.lines.length, tasksRaised: order.lines.length, shortfallQuantity: 0 } satisfies S['ReleaseOrderResponse'];
    }
    if (orderMatch[2] === 'pack') {
      requireThat(order.status === 'Released' && order.lines.every(l => l.pickedQuantity === l.orderedQuantity), 409, 'demo.invalid_transition');
      order.status = 'Packed'; order.packedAt = now(); return order;
    }
    requireThat(order.status === 'Packed', 409, 'demo.invalid_transition');
    const movements = order.lines.map(line => move(s, user, line.sku, line.pickedQuantity, order.stagingLocationCode ?? 'PACK-01', 'CUSTOMER', 'Despatch', 'CustomerDespatch'));
    order.status = 'Despatched'; order.despatchedAt = now();
    return { order, movements } satisfies S['DespatchOrderResponse'];
  }
  if (path === '/counting/plans') {
    supervisor(); requireThat(body.warehouseCode === 'WH1');
    const limit = body.maxTasks === undefined ? 20 : quantity(body.maxTasks);
    requireThat(Number.isInteger(limit) && limit <= 200);
    const candidates = s.balances.filter(b => b.quantity > 0 && s.locations.some(l => l.code === b.locationCode && l.isPickable)
      && !s.tasks.some(t => t.kind === 'CycleCount' && t.status !== 'Completed' && t.fromLocationCode === b.locationCode && t.sku === b.sku)).slice(0, limit);
    requireThat(candidates.length, 409, 'demo.no_candidates');
    const reference = `CNT-${String(s.plans.length + 1).padStart(4, '0')}`;
    const trace = newTrace('CycleCount', candidates[0]!.locationCode); s.traces.push(trace);
    const lines = candidates.map((balance, i): S['CountPlanLineResponse'] => {
      const task = newTask(found(s.items.find(item => item.sku === balance.sku)), 'CycleCount', balance.locationCode, balance.locationCode, balance.quantity, reference);
      task.decisionTraceId = trace.id; s.tasks.push(task);
      return { id: uid(), sku: balance.sku, lotNumber: null, locationCode: balance.locationCode, condition: 'Available',
        expectedQuantity: balance.quantity, rank: i + 1, score: 1 - i / 200, workTaskId: task.id, state: 'Ready',
        countedQuantity: null, variance: null, countedAt: null, countedByUserName: null };
    });
    const plan: S['CountPlanResponse'] = { id: uid(), reference, status: 'Open', warehouseCode: 'WH1',
      requestedTasks: limit, raisedCount: lines.length, completedCount: 0, varianceCount: 0, decisionTraceId: trace.id,
      createdAt: now(), createdByUserName: user.userName, completedAt: null, notes: null, lines };
    s.plans.push(plan); return plan;
  }
  if (path === '/work/tours/claim-next') {
    const tasks = s.tasks.filter(t => t.kind === 'Pick' && t.status === 'Ready' && !t.pickTourId).slice(0, Math.min(12, Number(body.maxStops) || 12));
    if (!tasks.length) return null;
    const tourId = uid();
    tasks.forEach((t, i) => { claim(t, user); t.pickTourId = tourId; t.tourSequence = i + 1; });
    const tour: S['PickTourResponse'] = { id: tourId, warehouseId: WAREHOUSE_ID, assignedToUserName: user.userName, claimedAt: now(),
      stopCount: tasks.length, remaining: tasks.length, isComplete: false, travelSteps: tasks.length * 10,
      baselineTravelSteps: tasks.length * 12, stepsSaved: tasks.length * 2, aisleChanges: 1,
      releasedAt: null, releasedStops: 0, segments: [{ zoneCode: 'AMB', requestedStrategy: 'Return', appliedStrategy: 'Return', fellBackBecause: null, stops: tasks.length }], stops: tasks };
    s.tours.push(tour); return tour;
  }
  const releaseTour = path.match(/^\/work\/tours\/([^/]+)\/release$/);
  if (releaseTour) {
    const tour = found(s.tours.find(t => t.id === releaseTour[1]));
    requireThat(tour.assignedToUserName === user.userName, 403, 'demo.forbidden');
    let released = 0;
    for (const task of s.tasks.filter(t => t.pickTourId === tour.id && t.status !== 'Completed')) {
      Object.assign(task, { status: 'Ready', assignedToUserName: null, pickTourId: null, tourSequence: null }); released++;
    }
    tour.releasedAt = now(); tour.releasedStops = released; tour.remaining = 0; tour.isComplete = true;
    return undefined;
  }
  throw new DemoError(403, 'demo.unavailable');
}
