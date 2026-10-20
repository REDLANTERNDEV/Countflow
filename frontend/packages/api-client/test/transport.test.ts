import assert from 'node:assert/strict';
import { test } from 'node:test';
import { createCountflowClient } from '../src/client.js';
import { createMemoryTokenStore } from '../src/tokenStore.js';

test('custom transport handles refresh, typed requests, and 401 retries without using the network', async () => {
  const tokens = createMemoryTokenStore();
  tokens.write({ accessToken: 'old', accessTokenExpiresAt: new Date(0).toISOString(), refreshToken: 'refresh' });
  const calls: string[] = [];
  let reads = 0;
  const transport: typeof fetch = async (input, init) => {
    const request = new Request(input, init);
    calls.push(new URL(request.url).pathname);
    if (request.url.endsWith('/refresh')) return Response.json({
      accessToken: 'new', accessTokenExpiresAt: new Date(Date.now() + 3600000).toISOString(), refreshToken: 'refresh',
    });
    if (++reads === 1) return Response.json({}, { status: 401 });
    assert.equal(request.headers.get('Authorization'), 'Bearer new');
    return Response.json({ userName: 'operator' });
  };
  const api = createCountflowClient({ baseUrl: 'https://demo.invalid', tokenStore: tokens, fetch: transport });
  const result = await api.GET('/api/v1/auth/me');
  assert.equal(result.data?.userName, 'operator');
  assert.deepEqual(calls, ['/api/v1/auth/refresh', '/api/v1/auth/me', '/api/v1/auth/refresh', '/api/v1/auth/me']);
});
