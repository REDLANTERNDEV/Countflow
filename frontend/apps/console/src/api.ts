import { createCountflowClient, createWebStorageTokenStore } from '@countflow/api-client';

export const tokenStore = createWebStorageTokenStore(
  window.sessionStorage,
  'countflow.console.session',
);

export const api = createCountflowClient({
  // Empty base URL keeps every call same-origin: the Vite proxy handles it in
  // development, Caddy in production. No environment-specific URL to get wrong.
  baseUrl: '',
  tokenStore,
  onSessionExpired: () => {
    tokenStore.clear();
    window.location.reload();
  },
});
