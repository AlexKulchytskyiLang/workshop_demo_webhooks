import { userManager } from './oidc';
import { ApiError } from '../api/client';

/** How recent the sign-in must be for a dangerous action. The API enforces the same 5 minutes against the token's auth_time claim. */
export const STEP_UP_MAX_AGE_SECONDS = 300;

/**
 * Re-authenticates the person in a small pop-up window (Keycloak asks for the password again: prompt=login, max_age=0), then the fresh token
 * replaces the old one. The page the person is working on keeps its state, which a full-page redirect would lose.
 */
export async function signInAgain(): Promise<void> {
  await userManager.signinPopup({
    prompt: 'login',
    max_age: 0,
    popupWindowFeatures: { width: 520, height: 680, location: false, menubar: false, toolbar: false },
  });
}

/** Whether the current token already proves a sign-in within the allowed age (read from the token the API will also read). */
export async function isFresh(): Promise<boolean> {
  const user = await userManager.getUser();
  const authTime = Number((user?.profile as Record<string, unknown> | undefined)?.auth_time);
  return Number.isFinite(authTime) && Date.now() / 1000 - authTime <= STEP_UP_MAX_AGE_SECONDS - 15;
}

/**
 * Runs an action that needs a fresh sign-in. If the token is stale the person is asked to sign in again first; if the API still says
 * "step up required" (clock skew, a token from another tab) the sign-in is repeated once. Everything else is passed through untouched.
 */
export async function withStepUp<T>(action: () => Promise<T>): Promise<T> {
  if (!(await isFresh())) await signInAgain();
  try {
    return await action();
  } catch (error) {
    if (error instanceof ApiError && error.code === 'step_up_required') {
      await signInAgain();
      return action();
    }
    throw error;
  }
}
