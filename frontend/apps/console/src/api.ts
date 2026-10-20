import { createCountflowClient, createWebStorageTokenStore } from '@countflow/api-client';

export const demo = import.meta.env.VITE_DEMO_MODE === 'true'
  ? await import('@countflow/demo').then(module => module.initializeDemo())
  : undefined;
export const storageKey = (key: string) => demo?.storageKey(key) ?? key;

export const tokenStore = createWebStorageTokenStore(
  window.sessionStorage,
  storageKey('countflow.console.session'),
);

export const api = createCountflowClient({
  // Empty base URL keeps every call same-origin: the Vite proxy handles it in
  // development, Caddy in production. No environment-specific URL to get wrong.
  baseUrl: '',
  tokenStore,
  ...(demo ? { fetch: demo.fetch } : {}),
  onSessionExpired: () => {
    tokenStore.clear();
    window.location.reload();
  },
});
