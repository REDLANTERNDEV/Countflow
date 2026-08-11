import { useTranslation } from '@countflow/i18n';
import { Button, Logo } from '@countflow/ui';
import { Link, Outlet } from '@tanstack/react-router';
import { useAuth } from '../auth.js';
import { LanguageSwitcher } from '../components/LanguageSwitcher.js';
import { ThemeSwitcher } from '../components/ThemeSwitcher.js';

export function RootLayout() {
  const { user, signOut } = useAuth();
  const { t } = useTranslation();

  const navLink =
    'rounded-[var(--radius-control)] px-[var(--space-3)] py-[var(--space-2)] ' +
    'text-[length:var(--text-sm)] text-[var(--color-text-secondary)] no-underline ' +
    'transition-colors duration-[var(--duration-fast)] ' +
    'hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text-primary)]';

  const navLinkActive =
    'bg-[var(--color-surface-active)] text-[var(--color-text-primary)] font-semibold';

  return (
    <div className="min-h-screen">
      {/* Translucent rather than opaque, so content visibly passes beneath —
          the chrome reads as a layer over the data, not a lid on it. */}
      <header
        className="sticky top-0 z-20 flex items-center gap-[var(--space-5)]
                   border-b border-[var(--color-border-subtle)]
                   bg-[color-mix(in_srgb,var(--color-surface-base)_82%,transparent)]
                   px-[var(--space-5)] py-[var(--space-3)] backdrop-blur-[12px]"
      >
        <div className="flex items-baseline gap-[var(--space-3)] whitespace-nowrap">
          <Logo size={20} />
          <span className="text-[length:var(--text-sm)] text-[var(--color-text-tertiary)]">
            {t('auth.subtitle')}
          </span>
        </div>

        <nav className="flex flex-1 gap-[var(--space-1)]">
          <Link to="/" activeOptions={{ exact: true }} className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.warehouses')}
          </Link>
          <Link to="/locations" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.locations')}
          </Link>
          <Link to="/items" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.items')}
          </Link>
          <Link to="/stock" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.stock')}
          </Link>
          <Link to="/ledger" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.ledger')}
          </Link>
          <Link to="/inbound" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.inbound')}
          </Link>
          <Link to="/work" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.work')}
          </Link>
          <Link to="/rules" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.rules')}
          </Link>
          <Link to="/scan" className={navLink} activeProps={{ className: navLinkActive }}>
            {t('nav.scanTest')}
          </Link>
        </nav>

        <div className="flex items-center gap-[var(--space-3)] whitespace-nowrap">
          <LanguageSwitcher />
          <ThemeSwitcher />
          <span className="text-[length:var(--text-xs)] text-[var(--color-text-tertiary)]">
            {user?.displayName}
          </span>
          <Button size="sm" onClick={() => void signOut()}>
            {t('common.signOut')}
          </Button>
        </div>
      </header>

      <main className="mx-auto max-w-[1700px] px-[var(--space-5)] py-[var(--space-5)]">
        <Outlet />
      </main>
    </div>
  );
}
