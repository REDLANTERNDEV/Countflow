import { useTranslation } from '@countflow/i18n';
import { Button, Logo, LogoSymbol, Sheet, TooltipProvider } from '@countflow/ui';
import { Outlet, useRouterState } from '@tanstack/react-router';
import { Menu, PanelLeftClose, PanelLeftOpen } from 'lucide-react';
import { useEffect, useState } from 'react';
import { useAuth } from '../auth.js';
import { LanguageSwitcher } from '../components/LanguageSwitcher.js';
import { SideNav } from '../components/SideNav.js';
import { ThemeSwitcher } from '../components/ThemeSwitcher.js';
import { RAIL_BREAKPOINT, useSidebarCollapsed } from '../sidebar.js';

export function RootLayout() {
  const { user, signOut } = useAuth();
  const { t } = useTranslation();
  const [collapsed, toggleCollapsed] = useSidebarCollapsed();
  const [drawerOpen, setDrawerOpen] = useState(false);

  // A route change that did not come from tapping a link — the back button, or
  // a redirect — must still close the drawer, or it stays open over a page the
  // person is already looking at.
  const pathname = useRouterState({ select: (state) => state.location.pathname });
  useEffect(() => setDrawerOpen(false), [pathname]);

  // And a tablet rotated from portrait to landscape crosses the rail's
  // breakpoint with the drawer still open, which puts the same navigation on
  // screen twice.
  //
  // Not covered by the walkthrough this shipped with: the in-app browser's
  // viewport override changes the width without dispatching anything — no
  // `matchMedia` change, no `resize`, not even a `ResizeObserver` callback — so
  // there is no way to provoke it there. Verified by reading, and by the two
  // paths either side of it (a real rotation is what this is for).
  useEffect(() => {
    const rail = window.matchMedia(`(min-width: ${RAIL_BREAKPOINT})`);
    const close = () => rail.matches && setDrawerOpen(false);

    rail.addEventListener('change', close);
    return () => rail.removeEventListener('change', close);
  }, []);

  return (
    <TooltipProvider delayDuration={250}>
      <div className="min-h-screen lg:flex">
        {/* The rail. Sticky and full height so the navigation does not scroll
            away from a ledger that is two thousand rows long. */}
        <aside
          className={`sticky top-0 hidden h-screen shrink-0 flex-col
                      border-r border-[var(--color-border-subtle)]
                      bg-[var(--color-surface-sunken)]
                      transition-[width] duration-[var(--duration-standard)]
                      ease-[var(--ease-standard)] lg:flex
                      ${collapsed ? 'w-[60px]' : 'w-[220px]'}`}
        >
          <div
            className={`flex shrink-0 items-center gap-[var(--space-3)]
                        px-[var(--space-4)] py-[var(--space-4)]
                        ${collapsed ? 'justify-center px-0' : ''}`}
          >
            {collapsed ? <LogoSymbol size={20} /> : <Logo size={20} />}
          </div>

          <div className="min-h-0 flex-1 overflow-y-auto px-[var(--space-2)] pb-[var(--space-4)]">
            <SideNav collapsed={collapsed} touch={false} />
          </div>

          <div className="shrink-0 border-t border-[var(--color-border-subtle)] p-[var(--space-2)]">
            <button
              type="button"
              onClick={toggleCollapsed}
              aria-label={t(collapsed ? 'nav.expand' : 'nav.collapse')}
              aria-expanded={!collapsed}
              className={`flex w-full items-center gap-[var(--space-3)]
                          rounded-[var(--radius-control)] px-[var(--space-3)]
                          min-h-[var(--control-height)]
                          text-[length:var(--text-sm)] text-[var(--color-text-secondary)]
                          transition-colors duration-[var(--duration-fast)]
                          hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text-primary)]
                          ${collapsed ? 'justify-center px-0' : ''}`}
            >
              {collapsed ? (
                <PanelLeftOpen size={16} aria-hidden="true" />
              ) : (
                <PanelLeftClose size={16} aria-hidden="true" />
              )}
              {collapsed ? null : <span>{t('nav.collapse')}</span>}
            </button>
          </div>
        </aside>

        {/* min-w-0 is load-bearing: without it this flex child refuses to shrink
            below its content, and one wide table pushes the whole shell sideways
            instead of scrolling inside its own container. */}
        <div className="flex min-w-0 flex-1 flex-col">
          <header
            className="sticky top-0 z-20 flex items-center gap-[var(--space-4)]
                       border-b border-[var(--color-border-subtle)]
                       bg-[color-mix(in_srgb,var(--color-surface-base)_82%,transparent)]
                       px-[var(--space-4)] py-[var(--space-3)] backdrop-blur-[12px]"
          >
            {/* The drawer lives here rather than at the end of the layout so its
                trigger renders where the trigger belongs. The panel itself is
                portalled out regardless. */}
            <Sheet
              open={drawerOpen}
              onOpenChange={setDrawerOpen}
              title={t('nav.menu')}
              closeLabel={t('common.close')}
              trigger={
                <button
                  type="button"
                  aria-label={t('nav.menu')}
                  // 44px, and only ever rendered under the rail's breakpoint —
                  // this is the one control in the console that is always a thumb.
                  className="inline-flex h-[44px] w-[44px] shrink-0 items-center justify-center
                             rounded-[var(--radius-control)] text-[var(--color-text-secondary)]
                             transition-colors duration-[var(--duration-fast)]
                             hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text-primary)]
                             lg:hidden"
                >
                  <Menu size={20} aria-hidden="true" />
                </button>
              }
            >
              <SideNav collapsed={false} touch onNavigate={() => setDrawerOpen(false)} />
            </Sheet>

            {/* The symbol rather than the wordmark, and the rail carries neither
                once the rail exists. At 375px the full lockup costs about ninety
                points that the language and theme pickers need — it was pushing
                them to "Türk…" and "Sist…", which is a brand winning an argument
                against two controls somebody might actually want to use. */}
            <LogoSymbol size={22} className="shrink-0 lg:hidden" />

            <div className="ml-auto flex min-w-0 items-center gap-[var(--space-3)]">
              <LanguageSwitcher />
              <ThemeSwitcher />
              <span className="hidden text-[length:var(--text-xs)] text-[var(--color-text-tertiary)] sm:inline">
                {user?.displayName}
              </span>
              <Button size="sm" onClick={() => void signOut()}>
                {t('common.signOut')}
              </Button>
            </div>
          </header>

          <main className="mx-auto w-full max-w-[1700px] px-[var(--space-4)] py-[var(--space-5)] lg:px-[var(--space-5)]">
            <Outlet />
          </main>
        </div>

      </div>
    </TooltipProvider>
  );
}
