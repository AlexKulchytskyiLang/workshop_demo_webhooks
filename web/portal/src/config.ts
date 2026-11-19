const env = import.meta.env as Record<string, string | undefined>;

/** Runtime settings. The AppHost injects the VITE_* values; the defaults match the fixed local ports. */
export const config = {
  apiUrl: (env.VITE_API_URL ?? 'http://localhost:5100').replace(/\/$/, ''),
  authority: env.VITE_OIDC_AUTHORITY ?? 'https://localhost:8085/realms/invoicehub',
  clientId: env.VITE_OIDC_CLIENT_ID ?? 'portal',
  /** The sign-in screen lists the demo accounts. Switch off for anything that is not a workshop. */
  showDemoHints: env.VITE_SHOW_DEMO_HINTS !== 'false',
};
