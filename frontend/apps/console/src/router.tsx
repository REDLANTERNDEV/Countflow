import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router';
import { AccuracyPage } from './routes/AccuracyPage.js';
import { CountingPage } from './routes/CountingPage.js';
import { InboundPage } from './routes/InboundPage.js';
import { ItemDetailPage } from './routes/ItemDetailPage.js';
import { ItemsPage } from './routes/ItemsPage.js';
import { LedgerPage } from './routes/LedgerPage.js';
import { LocationsPage } from './routes/LocationsPage.js';
import { OutboundPage } from './routes/OutboundPage.js';
import { RootLayout } from './routes/RootLayout.js';
import { ReplayPage } from './routes/ReplayPage.js';
import { RulesPage } from './routes/RulesPage.js';
import { ScanPage } from './routes/ScanPage.js';
import { StockPage } from './routes/StockPage.js';
import { WorkPage } from './routes/WorkPage.js';
import { WarehousesPage } from './pages/WarehousesPage.js';

// Code-based routes rather than file-based: no codegen step to keep in sync,
// and at this size the whole route table fits on one screen.
const rootRoute = createRootRoute({ component: RootLayout });

const indexRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/',
  component: WarehousesPage,
});

const locationsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/locations',
  component: LocationsPage,
});

const itemsRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/items',
  component: ItemsPage,
});

const itemDetailRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/items/$sku',
  component: ItemDetailPage,
});

const stockRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/stock',
  component: StockPage,
});

const ledgerRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/ledger',
  component: LedgerPage,
});

const inboundRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/inbound',
  component: InboundPage,
});

const outboundRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/outbound',
  component: OutboundPage,
});

const countingRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/counting',
  component: CountingPage,
});

const accuracyRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/accuracy',
  component: AccuracyPage,
});

const workRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/work',
  component: WorkPage,
});

const rulesRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/rules',
  component: RulesPage,
});

const replayRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/replay',
  component: ReplayPage,
});

const scanRoute = createRoute({
  getParentRoute: () => rootRoute,
  path: '/scan',
  component: ScanPage,
});

const routeTree = rootRoute.addChildren([
  indexRoute,
  locationsRoute,
  itemsRoute,
  itemDetailRoute,
  stockRoute,
  ledgerRoute,
  inboundRoute,
  outboundRoute,
  countingRoute,
  accuracyRoute,
  workRoute,
  rulesRoute,
  replayRoute,
  scanRoute,
]);

// Type is inferred deliberately: the route tree's shape is what gives Link and
// useParams their compile-time checking, and annotating it as Router would
// erase exactly that.
export const router = createRouter({ routeTree });

declare module '@tanstack/react-router' {
  interface Register {
    router: typeof router;
  }
}
