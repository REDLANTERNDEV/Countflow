import { initI18n } from '@countflow/i18n';
import { ThemeProvider } from '@countflow/ui';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import './styles.css';
import { App } from './App.js';
import { AuthProvider } from './auth.js';

// The console uses the whole catalogue; the handheld narrows it.
initI18n();

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      // The API client already retries once after refreshing an expired token;
      // layering React Query retries on top would multiply that.
      retry: 1,
      staleTime: 30_000,
    },
  },
});

window.addEventListener('countflow:demo-change', () => void queryClient.invalidateQueries());

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root container is missing from index.html.');
}

createRoot(container).render(
  <StrictMode>
    <ThemeProvider>
      <QueryClientProvider client={queryClient}>
        <AuthProvider>
          <App />
        </AuthProvider>
      </QueryClientProvider>
    </ThemeProvider>
  </StrictMode>,
);
