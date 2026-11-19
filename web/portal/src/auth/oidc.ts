import { UserManager, WebStorageStateStore } from 'oidc-client-ts';
import { config } from '../config';

/**
 * One UserManager for the whole app: the React provider uses it for the sign-in flow and the API client reads the
 * access token from it. Authorization code + PKCE against the Keycloak realm; the session lives in sessionStorage
 * (cleared when the tab closes) and the access token is renewed with the refresh token before it expires.
 */
export const userManager = new UserManager({
  authority: config.authority,
  client_id: config.clientId,
  redirect_uri: `${window.location.origin}/auth/callback`,
  post_logout_redirect_uri: `${window.location.origin}/select-tenant`,
  popup_redirect_uri: `${window.location.origin}/auth/popup-callback`, // where the step-up sign-in window lands (see main.tsx)
  response_type: 'code',
  scope: 'openid profile email',
  automaticSilentRenew: true,
  userStore: new WebStorageStateStore({ store: window.sessionStorage }),
});
