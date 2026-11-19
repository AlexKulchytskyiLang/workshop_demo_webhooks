import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { AuthProvider } from 'react-oidc-context';
import { BrowserRouter } from 'react-router-dom';
import { App } from './App';
import { userManager } from './auth/oidc';
import { PopupCallback } from './pages/PopupCallback';
import { ToastProvider } from './ui/Feedback';
import './styles/prototype.css';
import './styles/app.css';

const queryClient = new QueryClient({
  defaultOptions: { queries: { refetchOnWindowFocus: false } },
});

const root = createRoot(document.getElementById('root')!);

if (window.location.pathname === '/auth/popup-callback') {
  // the step-up sign-in window: it only hands its result to the window that opened it (the app's own sign-in handling must NOT run here)
  root.render(<PopupCallback />);
} else {
  root.render(
    <StrictMode>
      <AuthProvider userManager={userManager} onSigninCallback={() => window.history.replaceState({}, document.title, '/')}>
        <QueryClientProvider client={queryClient}>
          <ToastProvider>
            <BrowserRouter>
              <App />
            </BrowserRouter>
          </ToastProvider>
        </QueryClientProvider>
      </AuthProvider>
    </StrictMode>,
  );
}
