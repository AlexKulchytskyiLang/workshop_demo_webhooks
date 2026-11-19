import type { ReactNode } from 'react';
import { ThemeToggle } from './ThemeToggle';

/** Centered card used by the screens that need no navigation yet: choose tenant, register, errors. */
export function AuthLayout({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <>
      <a className="skip-link" href="#main">Skip to content</a>
      <header className="auth-top">
        <div className="brand">
          <span className="logo" aria-hidden="true">↗</span>
          InvoiceHub <span className="muted">Webhooks</span>
        </div>
        <ThemeToggle />
      </header>
      <main id="main" className="auth-main">
        <section className="card auth-card">
          <div className="bd">
            <h1>{title}</h1>
            {subtitle && <p className="muted sub">{subtitle}</p>}
            {children}
          </div>
        </section>
      </main>
    </>
  );
}
