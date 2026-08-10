export interface StoredSession {
  accessToken: string;
  accessTokenExpiresAt: string;
  refreshToken: string;
}

/**
 * Where a client keeps its session. The console uses sessionStorage; the
 * handheld uses localStorage so a device survives a reload mid-shift without
 * the operator having to sign in again.
 */
export interface TokenStore {
  read(): StoredSession | null;
  write(session: StoredSession): void;
  clear(): void;
}

export function createWebStorageTokenStore(storage: Storage, key = 'countflow.session'): TokenStore {
  return {
    read() {
      const raw = storage.getItem(key);
      if (!raw) return null;

      try {
        return JSON.parse(raw) as StoredSession;
      } catch {
        // A corrupt entry should log the user out, not wedge the app on every load.
        storage.removeItem(key);
        return null;
      }
    },
    write(session) {
      storage.setItem(key, JSON.stringify(session));
    },
    clear() {
      storage.removeItem(key);
    },
  };
}

export function createMemoryTokenStore(): TokenStore {
  let session: StoredSession | null = null;

  return {
    read: () => session,
    write: (next) => {
      session = next;
    },
    clear: () => {
      session = null;
    },
  };
}
