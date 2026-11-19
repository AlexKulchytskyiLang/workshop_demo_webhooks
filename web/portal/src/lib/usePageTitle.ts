import { useEffect } from 'react';

/** Each screen names itself in the browser tab (and to screen readers when the page changes). */
export function usePageTitle(title: string) {
  useEffect(() => {
    document.title = `${title} · InvoiceHub Webhooks`;
  }, [title]);
}
