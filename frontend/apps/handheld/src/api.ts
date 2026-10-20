import { createCountflowClient, createWebStorageTokenStore } from '@countflow/api-client';

export const demo = import.meta.env.VITE_DEMO_MODE === 'true'
  ? await import('@countflow/demo').then(module => module.initializeDemo())
  : undefined;
export const storageKey = (key: string) => demo?.storageKey(key) ?? key;

// localStorage rather than sessionStorage: a handheld is enrolled to a shift,
// and a reload or an app restart must not throw the operator back to a login
// screen in the middle of a pick.
export const tokenStore = createWebStorageTokenStore(
  window.localStorage,
  storageKey('countflow.handheld.session'),
);

export const api = createCountflowClient({
  baseUrl: '',
  tokenStore,
  ...(demo ? { fetch: demo.fetch } : {}),
  onSessionExpired: () => {
    tokenStore.clear();
    window.location.reload();
  },
});
