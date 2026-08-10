import { createRootRoute, createRoute, createRouter } from '@tanstack/react-router';
import { ItemDetailPage } from './routes/ItemDetailPage.js';
import { ItemsPage } from './routes/ItemsPage.js';
import { LocationsPage } from './routes/LocationsPage.js';
import { RootLayout } from './routes/RootLayout.js';
import { ScanPage } from './routes/ScanPage.js';
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
