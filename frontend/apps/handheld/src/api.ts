import { createCountflowClient, createWebStorageTokenStore } from '@countflow/api-client';

// localStorage rather than sessionStorage: a handheld is enrolled to a shift,
// and a reload or an app restart must not throw the operator back to a login
// screen in the middle of a pick.
export const tokenStore = createWebStorageTokenStore(
  window.localStorage,
  'countflow.handheld.session',
);

export const api = createCountflowClient({
  baseUrl: '',
  tokenStore,
  onSessionExpired: () => {
    tokenStore.clear();
    window.location.reload();
  },
});
