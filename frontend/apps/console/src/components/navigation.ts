import {
  Boxes,
  ClipboardCheck,
  Compass,
  History,
  ListChecks,
  MapPin,
  Package,
  Plug,
  ScanLine,
  ScrollText,
  Send,
  SlidersHorizontal,
  Target,
  Truck,
  Warehouse,
  type LucideIcon,
} from 'lucide-react';

export interface NavItem {
  /** Matches a route in `router.tsx`. */
  path: string;
  /** Key under `nav.` in the i18n catalogues. */
  labelKey: string;
  icon: LucideIcon;
  /** Only the index route should match on prefix alone. */
  exact?: boolean;
}

export interface NavGroup {
  /** Key under `nav.groups.` — omitted for the trailing set, which is deliberately unlabelled. */
  labelKey?: string;
  items: NavItem[];
}

/**
 * What the console is, in four groups.
 *
 * The bar this replaced held fifteen flat destinations, which is more than a
 * person reads — they scan for the word they already know and never discover the
 * rest. Grouping is the point of moving to a rail, more than the width is: four
 * headings of three or four are a glance, fifteen siblings are a lookup.
 *
 * The order inside a group is the order of the work, not the alphabet: goods
 * arrive, they are picked and shipped, they are counted. `Insight` is what you
 * open when a number looks wrong, and it ends with replay because that is the
 * last resort.
 *
 * **Adding a route means adding it here.** `SideNav` renders this and nothing
 * else, so a page reachable only by typing its URL is a page nobody will find —
 * there is a test for exactly that in `test/navigation.test.mjs`.
 */
export const NAV_GROUPS: NavGroup[] = [
  {
    labelKey: 'masterData',
    items: [
      { path: '/', labelKey: 'warehouses', icon: Warehouse, exact: true },
      { path: '/locations', labelKey: 'locations', icon: MapPin },
      { path: '/items', labelKey: 'items', icon: Package },
    ],
  },
  {
    labelKey: 'operations',
    items: [
      { path: '/inbound', labelKey: 'inbound', icon: Truck },
      { path: '/outbound', labelKey: 'outbound', icon: Send },
      { path: '/counting', labelKey: 'counting', icon: ClipboardCheck },
      { path: '/work', labelKey: 'work', icon: ListChecks },
    ],
  },
  {
    labelKey: 'insight',
    items: [
      { path: '/stock', labelKey: 'stock', icon: Boxes },
      { path: '/ledger', labelKey: 'ledger', icon: ScrollText },
      { path: '/accuracy', labelKey: 'accuracy', icon: Target },
      { path: '/replay', labelKey: 'replay', icon: History },
    ],
  },
  {
    labelKey: 'setup',
    items: [
      { path: '/rules', labelKey: 'rules', icon: SlidersHorizontal },
      { path: '/integrations', labelKey: 'integrations', icon: Plug },
    ],
  },
  {
    // Unlabelled and last, which keeps the decision the old bar made with
    // `ml-auto`: neither of these is part of anybody's day. A tour is for
    // somebody who has not used the product before, and putting it up with the
    // work gives every returning supervisor a signpost they do not need.
    items: [
      { path: '/scan', labelKey: 'scanTest', icon: ScanLine },
      { path: '/tour', labelKey: 'tour', icon: Compass },
    ],
  },
];

export const NAV_ITEMS: NavItem[] = NAV_GROUPS.flatMap((group) => group.items);
