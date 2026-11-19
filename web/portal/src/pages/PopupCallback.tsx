import { useEffect } from 'react';
import { userManager } from '../auth/oidc';

/**
 * The pop-up window of a step-up sign-in lands here. It hands the result to the window that opened it and closes itself.
 * (main.tsx renders this page WITHOUT the app's own sign-in handling, so the two do not both try to consume the same code.)
 */
export function PopupCallback() {
  useEffect(() => {
    void userManager.signinPopupCallback().catch(() => window.close());
  }, []);
  return <p style={{ padding: 24 }} role="status">Finishing sign-in…</p>;
}
