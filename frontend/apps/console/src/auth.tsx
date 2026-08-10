import { createContext, use, useCallback, useEffect, useMemo, useState, type ReactNode } from 'react';
import { api, tokenStore } from './api.js';

export interface CurrentUser {
  id: string;
  userName: string;
  displayName: string;
  operatorCode: string;
  roles: string[];
}

interface AuthContextValue {
  user: CurrentUser | null;
  initialising: boolean;
  signIn: (identifier: string, password: string) => Promise<string | null>;
  signOut: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null);
  const [initialising, setInitialising] = useState(true);

  // A stored session survives a reload, so restore it before rendering rather
  // than flashing the login screen at an already-authenticated user.
  useEffect(() => {
    let cancelled = false;

    async function restore() {
      if (!tokenStore.read()) {
        setInitialising(false);
        return;
      }

      const { data } = await api.GET('/api/auth/me');
      if (!cancelled) {
        setUser((data as CurrentUser | undefined) ?? null);
        setInitialising(false);
      }
    }

    void restore();
    return () => {
      cancelled = true;
    };
  }, []);

  /** Returns null on success, or the server's error *code* for the caller to translate. */
  const signIn = useCallback(async (identifier: string, password: string) => {
    const { data, error } = await api.POST('/api/auth/login', {
      body: { identifier, password },
    });

    if (error || !data) {
      // The problem document carries a stable `code` extension alongside its
      // English `detail`. We use the code so the wording can be Turkish.
      const problem = error as { code?: string } | undefined;
      return problem?.code ?? 'unknown';
    }

    tokenStore.write({
      accessToken: data.accessToken,
      accessTokenExpiresAt: data.accessTokenExpiresAt,
      refreshToken: data.refreshToken,
    });

    setUser(data.user as CurrentUser);
    return null;
  }, []);

  const signOut = useCallback(async () => {
    const session = tokenStore.read();
    if (session) {
      await api.POST('/api/auth/logout', { body: { refreshToken: session.refreshToken } });
    }

    tokenStore.clear();
    setUser(null);
  }, []);

  const value = useMemo(
    () => ({ user, initialising, signIn, signOut }),
    [user, initialising, signIn, signOut],
  );

  return <AuthContext value={value}>{children}</AuthContext>;
}

export function useAuth(): AuthContextValue {
  const context = use(AuthContext);
  if (!context) {
    throw new Error('useAuth must be used inside an AuthProvider.');
  }

  return context;
}
