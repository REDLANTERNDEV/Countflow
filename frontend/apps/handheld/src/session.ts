export interface CurrentUser {
  id: string;
  displayName: string;
  operatorCode: string;
  roles: string[];
}

const KEY = 'countflow.handheld.operator';

/**
 * Who was signed in last time this device was awake.
 *
 * Kept beside the tokens because `GET /auth/me` is a network call, and a
 * handheld that reloads in a dead aisle — a crash, a service-worker update, a
 * battery swap — would otherwise land on a sign-in screen it cannot get past
 * until somebody walks it back into coverage. That is not a degraded experience:
 * the device is holding queued work that only this operator may send, and
 * throwing away the knowledge of who they are strands it.
 *
 * The refresh token is what actually authorises anything. This is a name, a
 * code and a role list for the header and for deciding whose queue is whose.
 */
export function rememberOperator(user: CurrentUser): void {
  window.localStorage.setItem(KEY, JSON.stringify(user));
}

export function recallOperator(): CurrentUser | null {
  const raw = window.localStorage.getItem(KEY);
  if (!raw) return null;

  try {
    return JSON.parse(raw) as CurrentUser;
  } catch {
    window.localStorage.removeItem(KEY);
    return null;
  }
}

export function forgetOperator(): void {
  window.localStorage.removeItem(KEY);
}
