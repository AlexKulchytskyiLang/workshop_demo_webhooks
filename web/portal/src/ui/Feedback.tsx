import { createContext, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from 'react';
import { ApiError } from '../api/client';

interface ToastApi {
  show: (message: string) => void;
}
const ToastContext = createContext<ToastApi>({ show: () => undefined });
export const useToast = () => useContext(ToastContext);

/** A polite live region: screen readers announce the message, sighted users see it for a few seconds. */
export function ToastProvider({ children }: { children: ReactNode }) {
  const [message, setMessage] = useState('');
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  const show = useCallback((text: string) => {
    setMessage(text);
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setMessage(''), 4000);
  }, []);
  const api = useMemo(() => ({ show }), [show]);

  return (
    <ToastContext.Provider value={api}>
      {children}
      <div className={`toast${message ? ' on' : ''}`} role="status" aria-live="polite" data-testid="toast">{message}</div>
    </ToastContext.Provider>
  );
}

export function Loading({ what }: { what: string }) {
  return <p className="help" role="status">Loading {what}…</p>;
}

/** An error in words, with a way forward. The API's own explanation is shown when there is one. */
export function ErrorBanner({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const message = error instanceof ApiError ? error.message : error instanceof Error ? error.message : 'Something went wrong.';
  return (
    <div className="banner red" role="alert">
      <div className="grow">{message}</div>
      {onRetry && <button type="button" className="btn sm" onClick={onRetry}>Try again</button>}
    </div>
  );
}

/** Field-level text for an ApiError: the API's own wording when it is a validation problem. */
export function errorText(error: unknown): string {
  return error instanceof ApiError ? error.message : error instanceof Error ? error.message : 'Something went wrong.';
}
