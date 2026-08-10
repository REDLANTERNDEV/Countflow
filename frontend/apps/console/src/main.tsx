import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import '@countflow/ui/styles.css';
import './styles.css';
import { App } from './App.js';
import { AuthProvider } from './auth.js';

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

const container = document.getElementById('root');
if (!container) {
  throw new Error('Root container is missing from index.html.');
}

createRoot(container).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <AuthProvider>
        <App />
      </AuthProvider>
    </QueryClientProvider>
  </StrictMode>,
);
