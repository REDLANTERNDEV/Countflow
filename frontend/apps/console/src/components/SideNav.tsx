import { useTranslation } from '@countflow/i18n';
import { Tooltip, cn } from '@countflow/ui';
import { Link } from '@tanstack/react-router';
import { NAV_GROUPS, type NavItem } from './navigation.js';

interface NavLinkProps {
  item: NavItem;
  /** Icons only, with the label moved into a tooltip. Never true in the drawer. */
  collapsed: boolean;
  /** Taller rows, because the drawer only exists on a viewport that is touched. */
  touch: boolean;
  onNavigate?: () => void;
}

function NavLink({ item, collapsed, touch, onNavigate }: NavLinkProps) {
  const { t } = useTranslation();
  const label = t(`nav.${item.labelKey}`);
  const Icon = item.icon;

  const link = (
    <Link
      to={item.path}
      {...(item.exact ? { activeOptions: { exact: true } } : {})}
      onClick={onNavigate}
      // The name is on the element itself rather than left to the tooltip: a
      // tooltip is aria-describedby, so a collapsed rail relying on one would be
      // a column of unnamed links to a screen reader.
      aria-label={collapsed ? label : undefined}
      className={cn(
        'flex items-center gap-[var(--space-3)] no-underline',
        'rounded-[var(--radius-control)] px-[var(--space-3)]',
        'text-[length:var(--text-sm)] text-[var(--color-text-secondary)]',
        'transition-colors duration-[var(--duration-fast)]',
        'hover:bg-[var(--color-surface-hover)] hover:text-[var(--color-text-primary)]',
        touch ? 'min-h-[44px]' : 'min-h-[var(--control-height)]',
        collapsed && 'justify-center px-0',
      )}
      activeProps={{
        className: 'bg-[var(--color-surface-active)] text-[var(--color-text-primary)] font-semibold',
      }}
    >
      <Icon size={touch ? 18 : 16} aria-hidden="true" className="shrink-0" />
      {collapsed ? null : <span className="truncate">{label}</span>}
    </Link>
  );

  return collapsed ? <Tooltip label={label}>{link}</Tooltip> : link;
}

export interface SideNavProps {
  collapsed: boolean;
  touch: boolean;
  /** Closes the drawer after a jump; the rail has nothing to close. */
  onNavigate?: () => void;
}

/**
 * The navigation itself, rendered identically into the rail and the drawer.
 *
 * One component for both so they cannot drift into two different answers about
 * what the console contains — which is the failure the old header had in miniature
 * already, with `Tour` positioned by a `ml-auto` that only meant anything at one
 * width.
 */
export function SideNav({ collapsed, touch, onNavigate }: SideNavProps) {
  const { t } = useTranslation();

  return (
    <nav className="flex flex-col gap-[var(--space-4)]" aria-label={t('nav.primary')}>
      {NAV_GROUPS.map((group, index) => (
        <div key={group.labelKey ?? `group-${index}`} className="flex flex-col gap-[var(--space-1)]">
          {group.labelKey && !collapsed ? (
            <h2
              className="m-0 px-[var(--space-3)] pb-[var(--space-1)]
                         text-[length:var(--text-xs)] font-semibold uppercase
                         tracking-[var(--tracking-caption)] text-[var(--color-text-tertiary)]"
            >
              {t(`nav.groups.${group.labelKey}`)}
            </h2>
          ) : null}

          {/* Collapsed, the headings are gone and the groups would run together,
              so the gap has to do the work the words were doing. */}
          {collapsed && index > 0 ? (
            <div
              aria-hidden="true"
              className="mx-[var(--space-3)] mb-[var(--space-1)] border-t border-[var(--color-border-subtle)]"
            />
          ) : null}

          {group.items.map((item) => (
            <NavLink
              key={item.path}
              item={item}
              collapsed={collapsed}
              touch={touch}
              {...(onNavigate ? { onNavigate } : {})}
            />
          ))}
        </div>
      ))}
    </nav>
  );
}
