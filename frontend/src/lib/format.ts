import type { SourceSystem } from '../api/types';

export const ago = (t: number): string => {
  const s = Math.max(0, Math.round((Date.now() - t) / 1000));
  if (s < 60) return `${s}s ago`;
  if (s < 3600) return `${Math.round(s / 60)}m ago`;
  if (s < 86_400) return `${Math.round(s / 3600)}h ago`;
  return `${Math.round(s / 86_400)}d ago`;
};

/** Times and dates show in the viewer's own zone unless a page passes one: a firm's pages use
 *  the firm's zone, statements the billing calendar's (docs/api.md, timeZone fields). */
export const clock = (t: number, timeZone?: string): string => new Date(t).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit', timeZone });
export const when = (t: number, timeZone?: string): string => new Date(t).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short', timeZone });
export const leadNo = (eventId: string): string => `#${eventId.slice(-6).toUpperCase()}`;

export const pretty = (text: string): string => {
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
};

export const errorText = (e: unknown): string => (e instanceof Error ? e.message : String(e));

export const SOURCE_LABEL: Record<SourceSystem, string> = {
  legacy: 'Legacy',
  chatbot: 'Chatbot',
  airi: 'Answering AI',
  manual: 'Manual',
};

/** A length of time in plain words: "45 s", "20 min", "2 h 36 min", "3 d". */
export const duration = (ms: number): string => {
  const m = Math.round(ms / 60_000);
  if (ms < 1000) return 'under a second';
  if (ms < 60_000) return `${Math.round(ms / 1000)} s`;
  if (m < 60) return `${m} min`;
  if (m < 48 * 60) return m % 60 ? `${Math.floor(m / 60)} h ${m % 60} min` : `${m / 60} h`;
  return `${Math.round(m / 1440)} d`;
};

/** Dollars from cents: "$1,234.50". */
export const money = (cents: number): string => (cents / 100).toLocaleString('en-US', { style: 'currency', currency: 'USD' });

/** A calendar date: "Nov 15". */
export const day = (t: number, timeZone?: string): string => new Date(t).toLocaleDateString([], { month: 'short', day: 'numeric', timeZone });

export const plural = (n: number, one: string, many = `${one}s`): string => `${n} ${n === 1 ? one : many}`;

/** Per-viewer conveniences only (remembered firm, tab). Never state that must persist. */
export const store = {
  get<T>(key: string): T | null {
    try { return JSON.parse(localStorage.getItem(key) ?? 'null') as T | null; } catch { return null; }
  },
  set(key: string, v: unknown): void {
    try { localStorage.setItem(key, JSON.stringify(v)); } catch { /* private mode */ }
  },
};
