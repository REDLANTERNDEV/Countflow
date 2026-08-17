import { useCallback, useState } from 'react';

export const SIDEBAR_STORAGE_KEY = 'countflow.sidebar.collapsed';

/**
 * The width at which the side rail replaces the drawer.
 *
 * The same number as `--breakpoint-lg`, which is what Tailwind builds the `lg:`
 * variant from — and a constant here rather than a `getComputedStyle` read of
 * that token, which was the first attempt and was wrong: under Vite the
 * stylesheet is injected by JavaScript, so a mount-time read returns `''` often
 * enough that the listener silently never gets registered. `test/navigation.test.mjs`
 * asserts the two agree, which is the check that read was standing in for.
 */
export const RAIL_BREAKPOINT = '64rem';

function read(): boolean {
  return window.localStorage.getItem(SIDEBAR_STORAGE_KEY) === 'true';
}

/**
 * Whether the side rail is showing icons only.
 *
 * Persisted per device for the reason the theme is: the same person runs this on
 * a 34" monitor where the labels cost nothing and on a laptop where a wide
 * ledger is worth more than knowing that the target icon is Accuracy.
 *
 * Expanded is the default — a collapsed rail is a preference somebody expresses,
 * never a state they arrive in and have to work out.
 */
export function useSidebarCollapsed(): [boolean, () => void] {
  const [collapsed, setCollapsed] = useState<boolean>(read);

  const toggle = useCallback(() => {
    setCollapsed((current) => {
      const next = !current;
      window.localStorage.setItem(SIDEBAR_STORAGE_KEY, String(next));
      return next;
    });
  }, []);

  return [collapsed, toggle];
}
