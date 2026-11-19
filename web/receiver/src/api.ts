import { useCallback, useEffect, useState } from 'react';

const env = import.meta.env as Record<string, string | undefined>;

/** Which receiving system this window shows. The AppHost runs the same app twice, once per tenant. */
export const RECEIVER_API = (env.VITE_RECEIVER_API ?? 'http://localhost:5201').replace(/\/$/, '');
export const RECEIVER_NAME = env.VITE_RECEIVER_NAME ?? 'Receiving system';
export const PLATFORM_API = (env.VITE_PLATFORM_API ?? 'http://localhost:5100').replace(/\/$/, '');

export async function call<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${RECEIVER_API}${path}`, { ...init, headers: { 'Content-Type': 'application/json', ...(init?.headers ?? {}) } });
  const body = (await response.json().catch(() => null)) as T | { title?: string } | null;
  if (!response.ok) throw new Error((body as { title?: string } | null)?.title ?? `Request failed (${response.status})`);
  return body as T;
}

/** Polls an endpoint of the receiver; the screens are a live view, so they refresh by themselves. */
export function useLive<T>(path: string, everyMs = 2000): { data: T | undefined; error: string | null; reload: () => void } {
  const [data, setData] = useState<T>();
  const [error, setError] = useState<string | null>(null);
  const load = useCallback(() => {
    call<T>(path).then((d) => { setData(d); setError(null); }, (e: unknown) => setError(e instanceof Error ? e.message : 'The receiver could not be reached.'));
  }, [path]);
  useEffect(() => {
    load();
    const timer = setInterval(load, everyMs);
    return () => clearInterval(timer);
  }, [load, everyMs]);
  return { data, error, reload: load };
}

export interface Stats {
  name: string;
  counts: { received: number; processed: number; waiting: number; dead: number; duplicates: number; staleIgnored: number; rejected: number; chaosHits: number; invoices: number; files: number; filesVerified: number; kyc: number; recovered: number };
  chaos: { mode: string; delaySeconds: number };
  modes: string[];
}

export interface InboxRow {
  id: number; webhookId: string; type: string; aggregateId: string | null; sequence: number | null; status: string; outcome: string | null; attempts: number;
  duplicates: number; receivedAt: string; processedAt: string | null; test: boolean; apiVersion: string | null; traceParent: string | null; source: string;
}

export interface InboxDetail {
  webhookId: string; type: string; status: string; outcome: string | null; body: string; headers: Record<string, string> | null;
}

export interface InvoiceRow { invoiceId: string; number: string | null; customer: string | null; amount: number | null; currency: string | null; status: string; sequence: number; updatedAt: string }

export interface FileRow {
  id: number; webhookId: string; type: string; host: string; expectedSha256: string | null; actualSha256: string | null; expectedSize: number | null; actualSize: number | null;
  verdict: 'verified' | 'mismatch' | 'link_expired'; error: string | null; durationMs: number; fetchedAt: string;
}

export interface KycRow { verificationId: string; decision: string; riskScore: number; name: string | null; document: string | null; country: string | null; fetchedAt: string }

export interface SubscriptionRow { subscriptionId: string; plan: string | null; amount: number | null; currency: string | null; renewedAt: string }

export interface ReconcileResult { checked: number; missing: number; recovered: number; problem: string | null }
