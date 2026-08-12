import { initI18n } from '@countflow/i18n';
import { ThemeProvider } from '@countflow/ui';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';
import { App } from './App.js';
import { registerServiceWorker } from './serviceWorker.js';

// Only the namespaces the handheld actually uses.
//
// This narrows what i18next holds and searches at runtime — it does *not* shrink
// the bundle: the catalogues are statically imported, so the console's strings
// are still in the build. Splitting them out would save roughly 1% of the
// bundle, which is not worth the indirection. The real cost of localisation
// here is i18next itself (~34 kB gzipped), not the strings.
initI18n(['common', 'auth', 'scan', 'work', 'queue', 'rules', 'inbound', 'errors']);

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root container is missing from index.html.');
}

createRoot(container).render(
  <StrictMode>
    <ThemeProvider>
      <App />
    </ThemeProvider>
  </StrictMode>,
);

// After the app is mounted, so a new build never takes the page over before the
// outbox exists to say whether the device is holding anything.
registerServiceWorker();
