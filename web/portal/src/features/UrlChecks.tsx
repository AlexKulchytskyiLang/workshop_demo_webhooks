import { useQuery } from '@tanstack/react-query';
import { useEffect } from 'react';
import { webhooksApi, type UrlCheck } from '../api/webhooks';
import { useDebounced } from '../lib/useDebounced';
import { Icon } from '../ui/Icon';

const ICON: Record<UrlCheck['status'], string> = { ok: 'check', error: 'x', warn: 'alert', todo: 'clock' };
const WORD: Record<UrlCheck['status'], string> = { ok: 'Passed', error: 'Problem', warn: 'Warning', todo: 'Still to do' };
const CLASS: Record<UrlCheck['status'], string> = { ok: 'ok', error: 'bad', warn: 'warn', todo: 'todo' };

/**
 * The live checklist under the URL field (UX-SPEC "URL validation"): every line is a real check the server ran, with its reason.
 * Reports whether the address is acceptable so the form can refuse to continue before anything is saved.
 */
export function UrlChecks({ url, endpointId, onResult }: { url: string; endpointId?: string; onResult?: (acceptable: boolean | null) => void }) {
  const trimmed = url.trim();
  const debounced = useDebounced(trimmed, 500);
  const query = useQuery({
    queryKey: ['check-url', debounced, endpointId ?? null],
    queryFn: ({ signal }) => webhooksApi.checkUrl(debounced, endpointId, signal),
    enabled: debounced !== '',
    retry: false,
    staleTime: 30_000,
  });

  const pending = trimmed !== debounced || (query.isFetching && !query.data);
  const acceptable = trimmed === '' || pending ? null : query.data?.ok ?? null;
  useEffect(() => onResult?.(acceptable), [acceptable, onResult]);

  if (trimmed === '') return <p className="help">We check the address as you type: HTTPS, where it points, redirects.</p>;
  return (
    <div aria-live="polite">
      {pending && <p className="help" role="status">Checking the address…</p>}
      {!pending && query.isError && <p className="error-text" role="alert">The address could not be checked right now. {query.error.message}</p>}
      {!pending && query.data && (
        <ul className="checks" data-testid="url-checks">
          {query.data.checks.map((c) => (
            <li key={c.id}>
              <span className={CLASS[c.status]}><Icon name={ICON[c.status]} /></span>
              <span><span className="sr-only">{WORD[c.status]}: </span>{c.text}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}
