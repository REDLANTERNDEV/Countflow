import {
  createContext,
  use,
  useCallback,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react';

/** Three states, not a boolean — "follow the system" is a real choice, not the absence of one. */
export type ThemePreference = 'system' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';

export const THEME_STORAGE_KEY = 'countflow.theme';

interface ThemeContextValue {
  /** What the user chose. */
  preference: ThemePreference;
  /** What is actually on screen right now. */
  resolved: ResolvedTheme;
  setPreference: (preference: ThemePreference) => void;
}

const ThemeContext = createContext<ThemeContextValue | null>(null);

function systemTheme(): ResolvedTheme {
  return window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}

function readPreference(): ThemePreference {
  const stored = window.localStorage.getItem(THEME_STORAGE_KEY);
  return stored === 'light' || stored === 'dark' || stored === 'system' ? stored : 'system';
}

export function ThemeProvider({ children }: { children: ReactNode }) {
  const [preference, setPreferenceState] = useState<ThemePreference>(readPreference);
  const [resolved, setResolved] = useState<ResolvedTheme>(() =>
    preference === 'system' ? systemTheme() : preference,
  );

  // Keep following the OS while the preference is "system", so a machine that
  // flips to dark at dusk takes the app with it without a reload.
  useEffect(() => {
    if (preference !== 'system') {
      setResolved(preference);
      return;
    }

    const query = window.matchMedia('(prefers-color-scheme: dark)');
    const sync = () => setResolved(query.matches ? 'dark' : 'light');

    sync();
    query.addEventListener('change', sync);
    return () => query.removeEventListener('change', sync);
  }, [preference]);

  useEffect(() => {
    document.documentElement.dataset['theme'] = resolved;
  }, [resolved]);

  const setPreference = useCallback((next: ThemePreference) => {
    // Persisted per device rather than per account: a supervisor's monitor and
    // a handheld under fluorescent light want opposite answers, and syncing the
    // preference to the user would fight whichever one is wrong.
    window.localStorage.setItem(THEME_STORAGE_KEY, next);
    setPreferenceState(next);
  }, []);

  const value = useMemo(
    () => ({ preference, resolved, setPreference }),
    [preference, resolved, setPreference],
  );

  return <ThemeContext value={value}>{children}</ThemeContext>;
}

export function useTheme(): ThemeContextValue {
  const context = use(ThemeContext);
  if (!context) {
    throw new Error('useTheme must be used inside a ThemeProvider.');
  }

  return context;
}

/**
 * Runs before first paint, from an inline script in index.html.
 *
 * Resolving the theme in React is one frame too late: the page paints with the
 * default first, so a dark-themed handheld flashes white at shift start. That
 * reads as unfinished software, and it is genuinely unpleasant in a dim aisle.
 *
 * Kept as a string so it can be inlined verbatim and stays in sync with the
 * storage key above.
 */
export const themeBootstrapScript = `
(function(){try{
var p=localStorage.getItem('${THEME_STORAGE_KEY}');
var d=window.matchMedia('(prefers-color-scheme: dark)').matches?'dark':'light';
document.documentElement.dataset.theme=(p==='light'||p==='dark')?p:d;
}catch(e){document.documentElement.dataset.theme='dark';}})();
`.trim();
