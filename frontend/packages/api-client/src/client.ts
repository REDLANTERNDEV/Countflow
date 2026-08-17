import createClient, { type Middleware } from 'openapi-fetch';
import type { paths } from './schema.js';
import type { StoredSession, TokenStore } from './tokenStore.js';

export interface CountflowClientOptions {
  baseUrl: string;
  tokenStore: TokenStore;
  /** Called when the session is gone for good and the user must sign in again. */
  onSessionExpired?: () => void;
}

export type CountflowClient = ReturnType<typeof createClient<paths>>;

const AUTH_FREE_PATHS = ['/api/v1/auth/login', '/api/v1/auth/refresh', '/api/v1/auth/logout'];

/**
 * Typed API client.
 *
 * `paths` is generated from the API's own OpenAPI document, so this file cannot
 * compile against an endpoint the server does not actually expose — which is
 * what keeps the published spec honest.
 */
export function createCountflowClient(options: CountflowClientOptions) {
  const { baseUrl, tokenStore, onSessionExpired } = options;

  // Concurrent 401s must trigger exactly one refresh, not one per in-flight
  // request — otherwise the first rotation invalidates the rest and the reuse
  // detector revokes the whole session.
  let refreshInFlight: Promise<StoredSession | null> | null = null;

  async function refreshSession(): Promise<StoredSession | null> {
    const current = tokenStore.read();
    if (!current) return null;

    // Versioned, like every other path — and the one call in this package that
    // is not typed against the generated schema, which is exactly why it went
    // stale when Phase 7 moved the surface under `/api/v1`. It answered 404, so
    // every expiring token took `!response.ok` and signed the operator out
    // instead of being refreshed. On the handheld that means a reload after a
    // battery swap ends the shift's session with the operator's unsent work
    // still on the device.
    const response = await fetch(`${baseUrl}/api/v1/auth/refresh`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ refreshToken: current.refreshToken }),
    });

    if (!response.ok) {
      tokenStore.clear();
      onSessionExpired?.();
      return null;
    }

    const body = (await response.json()) as {
      accessToken: string;
      accessTokenExpiresAt: string;
      refreshToken: string;
    };

    const session: StoredSession = {
      accessToken: body.accessToken,
      accessTokenExpiresAt: body.accessTokenExpiresAt,
      refreshToken: body.refreshToken,
    };

    tokenStore.write(session);
    return session;
  }

  function refreshOnce(): Promise<StoredSession | null> {
    refreshInFlight ??= refreshSession().finally(() => {
      refreshInFlight = null;
    });

    return refreshInFlight;
  }

  const authMiddleware: Middleware = {
    async onRequest({ request, schemaPath }) {
      if (AUTH_FREE_PATHS.includes(schemaPath)) return undefined;

      let session = tokenStore.read();
      if (!session) return undefined;

      // Refresh proactively just before expiry. A handheld coming out of a dead
      // zone should not spend its first request discovering the token died.
      if (new Date(session.accessTokenExpiresAt).getTime() - Date.now() < 60_000) {
        session = await refreshOnce();
        if (!session) return undefined;
      }

      request.headers.set('Authorization', `Bearer ${session.accessToken}`);
      return request;
    },

    async onResponse({ request, response, schemaPath }) {
      if (response.status !== 401 || AUTH_FREE_PATHS.includes(schemaPath)) {
        return undefined;
      }

      const session = await refreshOnce();
      if (!session) return undefined;

      const retried = new Request(request, {
        headers: new Headers(request.headers),
      });
      retried.headers.set('Authorization', `Bearer ${session.accessToken}`);

      return fetch(retried);
    },
  };

  const client = createClient<paths>({ baseUrl });
  client.use(authMiddleware);

  return client;
}
