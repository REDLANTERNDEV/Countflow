import { uid, type DemoState, type S } from './data.js';
import { DemoError, found, perform, requireThat } from './actions.js';
import { read, reconciliation } from './reads.js';
import type { DemoStore } from './store.js';

function signIn(s: DemoState, identifier: string): S['AuthResponse'] {
  const name = identifier.toLowerCase();
  const aliases: Record<string, string> = { adm001: 'admin', sup001: 'supervisor', op001: 'operator' };
  const userName = aliases[name] ?? name;
  requireThat(['admin', 'supervisor', 'operator'].includes(userName), 401, 'auth.invalid_credentials');
  const user: S['UserInfo'] = { id: `${s.epoch.slice(0, -1)}${['admin', 'supervisor', 'operator'].indexOf(userName) + 1}`,
    userName, displayName: { admin: 'Demo Admin', supervisor: 'Demo Supervisor', operator: 'Demo Operator' }[userName]!,
    operatorCode: { admin: 'ADM001', supervisor: 'SUP001', operator: 'OP001' }[userName]!,
    roles: [{ admin: 'Admin', supervisor: 'Supervisor', operator: 'Operator' }[userName]!], isServiceAccount: false };
  const accessToken = `demo.${s.epoch}.${uid()}`, refreshToken = `demo.refresh.${uid()}`;
  s.sessions[accessToken] = { user, refreshToken };
  return { accessToken, refreshToken, accessTokenExpiresAt: new Date(Date.now() + 3600000).toISOString(), user };
}

function authenticate(s: DemoState, request: Request) {
  const token = request.headers.get('Authorization')?.replace(/^Bearer /, '') ?? '';
  const session = Object.hasOwn(s.sessions, token) ? s.sessions[token] : undefined;
  requireThat(session, 401, 'auth.invalid_credentials'); return session;
}

export function createDemoTransport(store: DemoStore): typeof fetch {
  return async (input, init) => {
    const request = new Request(typeof input === 'string' ? new URL(input, globalThis.location?.origin ?? 'https://demo.invalid') : input, init);
    if (request.signal.aborted) throw request.signal.reason;
    try {
      const url = new URL(request.url);
      requireThat(url.pathname.startsWith('/api/v1/'), 404, 'demo.not_found');
      const path = url.pathname.slice('/api/v1'.length);
      const text = request.body ? await request.text() : '';
      requireThat(text.length <= 32768, 413, 'demo.invalid_request');
      const body: Record<string, unknown> = text ? JSON.parse(text) : {};
      requireThat(body && typeof body === 'object' && !Array.isArray(body));
      const value = await store.transact(s => {
        if (path === '/auth/login' && request.method === 'POST') {
          requireThat(body.password === 'Countflow#2026', 401, 'auth.invalid_credentials');
          return signIn(s, String(body.identifier ?? ''));
        }
        if (path === '/auth/refresh' && request.method === 'POST') {
          const existing = Object.entries(s.sessions).find(([, session]) => session.refreshToken === body.refreshToken);
          requireThat(existing, 401, 'auth.invalid_credentials');
          const user = existing[1].user; delete s.sessions[existing[0]];
          return signIn(s, user.userName);
        }
        const session = authenticate(s, request);
        if (path === '/auth/logout' && request.method === 'POST') {
          for (const [token, value] of Object.entries(s.sessions)) if (value.refreshToken === body.refreshToken) delete s.sessions[token];
          return undefined;
        }
        if (request.method === 'GET') return read(s, path, url.searchParams, session.user);
        if (request.method !== 'POST') throw new DemoError(403, 'demo.unavailable');
        if (path === '/inventory/reconciliation') return reconciliation(s);
        if (path === '/scan/resolve') {
          const barcode = String(body.barcode ?? '').trim();
          const item = s.items.find(i => i.sku === barcode.toUpperCase() || i.packConfigurations.some(p => p.gtin === barcode || barcode === `(01)${p.gtin}`));
          const recognised = found(item);
          return { itemRecognised: true, item: { itemId: recognised.id, sku: recognised.sku, description: recognised.description,
            packagingLevel: 'Each', baseUnitQuantity: 1, isLotTracked: false, isExpiryTracked: false, isSerialTracked: false, baseUom: 'EA' },
            gtin: recognised.packConfigurations[0]?.gtin ?? null, sscc: null, ssccCheckDigitValid: false,
            lotNumber: null, expiryDate: null, productionDate: null, serialNumber: null, scannedQuantity: 1, baseUnitQuantity: 1, elements: [], warnings: [],
          } satisfies S['ScanResolutionResponse'];
        }
        const key = request.headers.get('Idempotency-Key');
        if (path === '/inbound/receipts' || path.endsWith('/complete') || /^\/outbound\/orders\/[^/]+\/(release|despatch)$/.test(path)) {
          requireThat(key && key.length <= 200, 400, 'demo.idempotency_required');
        }
        const cacheKey = key ? `${session.user.id}|${path}|${key}` : undefined;
        if (cacheKey && s.idempotency[cacheKey]) {
          requireThat(s.idempotency[cacheKey]!.body === text, 409, 'demo.idempotency_conflict');
          return s.idempotency[cacheKey]!.value;
        }
        const result = perform(s, path, body, session.user);
        if (cacheKey) s.idempotency[cacheKey] = { body: text, value: structuredClone(result) };
        return result;
      });
      // openapi-fetch and the existing handheld recognise an empty queue as 204.
      if (value === undefined || value === null) return new Response(null, { status: 204 });
      return Response.json(value);
    } catch (error) {
      const status = error instanceof DemoError ? error.status : error instanceof SyntaxError ? 400 : 503;
      const code = error instanceof DemoError ? error.code : status === 400 ? 'demo.invalid_request' : 'demo.storage_unavailable';
      return Response.json({ title: code, status, code }, { status, headers: { 'Content-Type': 'application/problem+json' } });
    }
  };
}
